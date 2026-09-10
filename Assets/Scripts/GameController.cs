using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using Unity.Services.Relay;

/// <summary>
/// Opening and joining a room. Everything below <see cref="HostRoom"/> and <see cref="JoinRoom"/>
/// is unchanged from the version that drove the 40-key lobby keyboard — the connectInFlight latch,
/// the 15 s watchdog, the three failure callbacks converging on one ShowJoinFailed, the
/// Shutdown-before-retry. That is deliberate: the lobby rewrite replaced *what calls* these, not
/// what they do, and this is the flakiest path in the project.
///
/// What is gone is the keyboard state machine that used to sit in Update() reading
/// pointerControl.currentLetter one character at a time. <see cref="LobbyController"/> owns the UI
/// now and this owns the connection; the two meet at <see cref="Status"/> and the two public
/// methods.
///
/// ShowJoinFailed's relative 7 cm nudge of the instructions transform, and the joinedRelay
/// idempotence guard that existed only to stop the nudge being applied twice, went with the
/// keyboard. The message goes in the panel's status line instead.
/// </summary>
public class GameController : MonoBehaviour
{
    public RelayVivox relayVivoxStarter;

    public static string joinCode = "";
    public static string nickName = "";

    [Tooltip("How long to wait for the connection to come up before telling the player it failed. " +
             "UnityTransport is configured for 60 x 1000 ms connect attempts, so without this the " +
             "lobby sits on \"Joining room...\" for a full minute and then forever.")]
    public float JoinTimeoutSeconds = 15f;

    /// <summary>
    /// Whatever the player should be told right now, raised whenever it changes. LobbyController
    /// puts it in the Play panel's status line. Nothing here touches a TextMeshPro.
    /// </summary>
    public event Action<string> Status;

    /// <summary>
    /// Raised when a host or join attempt ends in failure, after <see cref="Status"/>. The lobby
    /// re-enables its rows off this rather than polling <see cref="Busy"/>.
    /// </summary>
    public event Action Failed;

    /// <summary>True while a host or join is in flight. The lobby greys its rows on it.</summary>
    public bool Busy => connectInFlight;

    // Guards the async host/join path against being entered twice. Both entry points are
    // `async void`, so this has to be set before the first await and cleared on every failure path.
    private bool connectInFlight;

    private Coroutine joinWatchdog;

    void Start()
    {
        // Start(), not OnEnable(): NetworkManager.Awake must have run for Singleton to exist, and
        // both live in OpeningScene with no ordering guarantee between them.
        //
        // Relay handing out an allocation only means the REST call worked. The DTLS handshake and
        // Netcode's own handshake happen afterwards and were previously unwatched, so any failure
        // after JoinAllocationAsync left the lobby on "Joining room..." with no error, no timeout
        // and no retry.
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
            NetworkManager.Singleton.OnTransportFailure += HandleTransportFailure;
        }
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
            NetworkManager.Singleton.OnTransportFailure -= HandleTransportFailure;
        }
    }

    // ------------------------------------------------------------------ what the lobby calls

    /// <summary>
    /// Open a room. <see cref="RoomOptions"/> has already been set by the Play panel and decides
    /// whether it is listed publicly and which game it is locked to.
    /// </summary>
    public void HostRoom(string playerName)
    {
        nickName = playerName;
        joinCode = "";
        HostNewRoom();
    }

    /// <summary>Join a room by its Relay code.</summary>
    public void JoinRoom(string playerName, string code)
    {
        nickName = playerName;
        joinCode = code ?? "";
        RoomOptions.SetPrivate();       // a joiner does not own the room's public/private choice
        TryToJoinRelayVivox();
    }

    // ------------------------------------------------------------------ the connection

    /// <summary>
    /// Host path. StartHost() has to complete before the scene can be loaded, because
    /// NetworkManager.SceneManager does not exist until the session is running.
    /// </summary>
    private async void HostNewRoom()
    {
        if (connectInFlight)
        {
            return;
        }
        connectInFlight = true;

        Say("Creating room...");

        try
        {
            await relayVivoxStarter.StartRelayAndVivox(nickName);
        }
        catch (RelayServiceException)
        {
            Fail("Could not create a room.");
            return;
        }
        catch (ArgumentException e)
        {
            // "No endpoint for connection type ..." out of AllocationUtils, if the connection type
            // on RelayVivox names a protocol this Relay allocation does not offer. Not a
            // RelayServiceException, so without this it escapes the async void and the lobby hangs
            // with no message at all.
            Debug.LogError("GameController: relay configuration rejected. " + e.Message);
            Fail("Could not create a room.");
            return;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            Fail("Could not create a room.");
            return;
        }

        // List it, if the host asked for a public room. After StartHost, not before: the join code
        // is what the listing carries, and a room nobody can enter is worse than one nobody can see.
        if (RoomOptions.IsPublic && RoomDirectory.Instance != null)
        {
            await RoomDirectory.Instance.PublishAsync(
                RoomOptions.RoomName, relayVivoxStarter.relayRoomCode, RoomOptions.GameKey,
                nickName, 12);
        }

        // Server-driven. This is what puts the host — and every client that joins later, via
        // Netcode's synchronization — into a game. A public room opens straight into the game it is
        // locked to; a private one opens into the catalog default.
        string scene = GameRoutes.DefaultScene;
        if (RoomOptions.IsPublic && GameRoutes.TryGetScene(RoomOptions.GameKey, out string locked))
        {
            scene = locked;
        }

        GameSelector.LoadGameScene(scene);
    }

    private async void TryToJoinRelayVivox()
    {
        if (connectInFlight)
        {
            return;
        }
        connectInFlight = true;

        Say("Joining room...");

        try
        {
            await relayVivoxStarter.JoinRelayAndVivox(nickName, joinCode);
        }
        catch (RelayServiceException)
        {
            ShowJoinFailed("Wrong room code");
            return;
        }
        catch (ArgumentException e)
        {
            // See HostNewRoom: AllocationUtils throws this, not a RelayServiceException, when the
            // allocation has no endpoint for the configured connection type.
            Debug.LogError("GameController: relay configuration rejected. " + e.Message);
            ShowJoinFailed("Could not join the room");
            return;
        }

        // No LoadScene here, on purpose. Netcode synchronizes this client into whatever scene the
        // host already has open — which may be any of the games, depending on what the room is
        // playing right now. Loading a scene here would fight that.

        // Relay accepted us; Netcode has not yet. Everything from here is asynchronous and, until
        // this watchdog, entirely unwatched.
        if (joinWatchdog != null)
        {
            StopCoroutine(joinWatchdog);
        }
        joinWatchdog = StartCoroutine(WatchJoin());
    }

    /// <summary>
    /// Fails the join out loud if the connection never comes up. A transport-level death may never
    /// raise OnClientDisconnectCallback at all, so the callbacks alone do not cover the whole
    /// failure space.
    ///
    /// This coroutine dies with the object, which is exactly right: being synchronized into the
    /// host's scene destroys OpeningScene and with it this GameController, and that only happens
    /// once the join has genuinely succeeded.
    /// </summary>
    private IEnumerator WatchJoin()
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, JoinTimeoutSeconds);

        while (Time.realtimeSinceStartup < deadline)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
            {
                joinWatchdog = null;
                yield break;
            }
            yield return null;
        }

        joinWatchdog = null;
        Debug.LogError("GameController: no connection " + JoinTimeoutSeconds +
                       "s after the relay join. Giving up.");
        ShowJoinFailed("Could not reach the room");
    }

    /// <summary>
    /// A join that Relay accepted but that never became a session — refused by the host, dropped by
    /// the transport, or simply never answered.
    ///
    /// Connection approval is what changed the interesting case here. Netcode's own config-hash
    /// refusal still disconnects with no reason string, so an empty DisconnectReason is still
    /// informative — but a build mismatch or a missing game is now caught one step earlier, by
    /// RoomApproval, and arrives here as a sentence the player can act on.
    /// </summary>
    private void HandleClientDisconnect(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsServer)
        {
            return;                           // the host watching somebody else leave
        }

        string why = string.IsNullOrEmpty(nm.DisconnectReason)
                         ? "The room refused the connection (different build?)"
                         : nm.DisconnectReason;
        ShowJoinFailed(why);
    }

    private void HandleTransportFailure()
    {
        ShowJoinFailed("Lost the connection to the room");
    }

    /// <summary>
    /// Back to the lobby, with a reason. Both the Relay failure and the Netcode failure land here so
    /// the two cannot disagree about what "back to the lobby" means.
    /// </summary>
    private void ShowJoinFailed(string why)
    {
        if (joinWatchdog != null)
        {
            StopCoroutine(joinWatchdog);
            joinWatchdog = null;
        }

        // Put the NetworkManager back where a retry can use it. StartClient() on an instance that
        // is already listening — which it still is while UnityTransport grinds through its 60
        // connect attempts — is refused, so without this the second attempt fails for a completely
        // different reason than the first. Re-entrant by way of the callbacks Shutdown raises, but
        // IsListening is false by the time they arrive, so the second pass does nothing.
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
        {
            nm.Shutdown();
        }

        joinCode = "";
        Fail(why);
    }

    private void Say(string message)
    {
        Status?.Invoke(message);
    }

    private void Fail(string why)
    {
        connectInFlight = false;
        Say(why);
        Failed?.Invoke();
    }
}
