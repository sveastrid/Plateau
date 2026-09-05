using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;
using Unity.Netcode;
using Unity.Services.Relay;
//using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Collections;
using System.Threading.Tasks;
using UnityEngine.UI;

public class GameController : MonoBehaviour
{
    //So you can get user inputs
    public InputReader inputs;
    public RelayVivox relayVivoxStarter;
    public GameObject inputField;
    public TextMeshPro instructions;
    public TextMeshPro instructionsSubfield;

    public Transform rh;
    public Transform lh;
    public pointerControl pointer;

    public static string joinCode="";
    public static string nickName = "";
    private bool joinedRelay = false;
    private keyInfo pressedKey;

    [Tooltip("How long to wait for the connection to come up before telling the player it failed. " +
             "UnityTransport is configured for 60 x 1000 ms connect attempts, so without this the " +
             "lobby sits on \"Joining room...\" for a full minute and then forever.")]
    public float JoinTimeoutSeconds = 15f;

    // Guards the async host/join path against being entered twice. Both entry points are
    // `async void`, so this has to be set before the first await and cleared on every failure path.
    private bool connectInFlight;

    private Coroutine joinWatchdog;

    // Start is called before the first frame update
    void Start()
    {
        // The keyboard IS the interaction here — there is no menu to gate the pointer behind — so
        // it has to be on from the first frame or no key can ever be pressed.
        //
        // OpeningScene DOES have a MenuControl: it comes in with PersistentRig. Both this and
        // MenuControl.ApplyPointerDefault write the pointer's active state on load, and Unity gives
        // no ordering guarantee between two Start() calls, so the two must AGREE rather than one
        // winning. MenuControl's lobby branch is what makes them agree; do not remove either half.
        if (pointer != null)
        {
            pointer.gameObject.SetActive(true);
        }

        // Start(), not OnEnable(): NetworkManager.Awake must have run for Singleton to exist, and
        // both live in OpeningScene with no ordering guarantee between them.
        //
        // Relay handing out an allocation only means the REST call worked. The DTLS handshake and
        // Netcode's own handshake happen afterwards and were previously unwatched, so any failure
        // after JoinAllocationAsync left the lobby on "Joining room..." with no error, no timeout
        // and no retry. NetworkProbe covers the same events for the whole life of the app; this
        // covers the one thing the probe cannot, which is putting the player back at the keyboard.
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

    // Update is called once per frame
    void Update()
    {
        //Get input from keyboard and update the joincode string
        if (inputs.RightMainTriggerDown)
        {
            if (pointer.currentKey != null)
            {
                pressedKey = pointer.currentKey;
            }

            //typing the relay room name
            if (!joinedRelay)
            {
                if (pointer.currentLetter == "Clear")
                {
                    joinCode = "";
                    inputField.GetComponent<TMP_InputField>().text = joinCode;
                }
                else if (pointer.currentLetter == "Enter")
                {
                    joinedRelay = true;
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                    instructions.SetText("Pick a Username:");
                    instructions.color = new Color(0,.98f,.6f,1);
                    instructions.transform.Translate(new Vector3(0, -.07f, 0));
                    inputField.GetComponent<TMP_InputField>().placeholder.gameObject.GetComponent<TextMeshProUGUI>().text = "Username...";
                    instructionsSubfield.gameObject.SetActive(false);
                    
                }
                else if (pointer.currentLetter == "Back")
                {
                    if (joinCode.Length > 0)
                    {
                        joinCode = joinCode.Remove(joinCode.Length - 1);
                        inputField.GetComponent<TMP_InputField>().text = joinCode;
                    }
                }
                else
                {
                    joinCode = joinCode + pointer.currentLetter;
                    inputField.GetComponent<TMP_InputField>().text = joinCode;
                }

                if (pointer.currentKey != null)
                {
                    pressedKey.MakeBigger();
                }
            }
            //typing in name to display and joining relay and vivox
            else
            {
                if (pointer.currentKey != null)
                {
                    pressedKey.MakeBigger();
                }


                if (pointer.currentLetter == "Clear")
                {
                    nickName = "";
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                }
                else if (pointer.currentLetter == "Back")
                {
                    if (nickName.Length > 0)
                    {
                        nickName = nickName.Remove(nickName.Length - 1);
                        inputField.GetComponent<TMP_InputField>().text = nickName;
                    }
                }
                else if (pointer.currentLetter == "Enter")
                {
                    
                }
                else if (pointer.currentLetter != "Enter")
                {
                    nickName = nickName + pointer.currentLetter;
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                }
            }
        }

        if (inputs.RightMainTriggerUp)
        {
            if (pressedKey == null)
            {
                return;                       // trigger released without ever touching a key
            }

            pressedKey.MakeSmaller();

            // Clear it here, the way MenuControl does (MenuControl.cs:174). Without this the join
            // path is re-entrant: TryToJoinRelayVivox deliberately does NOT LoadScene, so for the
            // seconds between StartClient() and Netcode synchronizing this client into the host's
            // scene the joiner is still in OpeningScene with a live GameController and pressedKey
            // still pointing at "Enter". One more press-and-release anywhere in empty space then
            // re-ran the whole join: a second JoinAllocationAsync whose fresh allocation
            // INVALIDATES the first, a second SetRelayServerData mid-connection, and a second
            // StartClient() on a running instance. That kills a join that was already succeeding,
            // and it is joiner-only — the host leaves this scene immediately.
            keyInfo acted = pressedKey;
            pressedKey = null;

            if (acted.keyName == "Enter" && joinedRelay)
            {
                if (joinCode == "" && nickName != "")
                {
                    HostNewRoom();
                }
                else if (nickName != "")
                {
                    TryToJoinRelayVivox();
                }
            }
        }
    }

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

        instructions.SetText("Creating room...");

        try
        {
            await relayVivoxStarter.StartRelayAndVivox(nickName);
        }
        catch (RelayServiceException)
        {
            connectInFlight = false;
            instructions.SetText("Could not create a room. Press Enter to try again.");
            return;
        }
        catch (ArgumentException e)
        {
            // "No endpoint for connection type ..." out of AllocationUtils, if the connection type
            // on RelayVivox names a protocol this Relay allocation does not offer. Not a
            // RelayServiceException, so without this it escapes the async void and the lobby hangs
            // with no message at all.
            connectInFlight = false;
            Debug.LogError("GameController: relay configuration rejected. " + e.Message);
            instructions.SetText("Could not create a room. Press Enter to try again.");
            return;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            connectInFlight = false;
            instructions.SetText("Could not create a room. Press Enter to try again.");
            return;
        }

        // Server-driven. This is what puts the host — and every client that joins later,
        // via Netcode's synchronization — into StairsGame.
        GameSelector.LoadGameScene(GameRoutes.DefaultScene);
    }

    private async void TryToJoinRelayVivox()
    {
        if (connectInFlight)
        {
            return;
        }
        connectInFlight = true;

        try
        {
            await relayVivoxStarter.JoinRelayAndVivox(nickName, joinCode);
        }
        catch (RelayServiceException)
        {
            Debug.Log("Caught exception");
            ShowJoinFailed("Wrong Room Code");
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

        // No LoadScene here, on purpose. Netcode synchronizes this client into whatever scene
        // the host already has open — which may be StairsGame or ChasmGame, depending on what
        // the room is playing right now. Loading a scene here would fight that.
        instructions.SetText("Joining room...");
        instructionsSubfield.gameObject.SetActive(false);

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
    /// the transport, or simply never answered. Netcode's own config-hash refusal disconnects with
    /// no reason string, so an empty DisconnectReason is itself informative.
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
    /// Back to the room-code keyboard, with a reason. Both the Relay failure and the Netcode
    /// failure land here so the two cannot disagree about what "back to the keyboard" means.
    ///
    /// Idempotent: the instructions transform is nudged by a RELATIVE 7 cm when the lobby moves to
    /// username entry, so undoing it twice would walk the text off. joinedRelay gates that.
    /// </summary>
    private void ShowJoinFailed(string why)
    {
        connectInFlight = false;

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

        instructions.SetText(why);
        instructionsSubfield.gameObject.SetActive(true);
        instructionsSubfield.SetText("Please enter a new code or press Enter to start a new room");
        instructions.color = new Color(0.22f, .94f, 1f, 1);

        if (joinedRelay)
        {
            instructions.transform.Translate(new Vector3(0, .07f, 0));
            joinedRelay = false;
        }

        inputField.GetComponent<TMP_InputField>().placeholder.gameObject.GetComponent<TextMeshProUGUI>().text = "Room Code...";
        joinCode = "";
        inputField.GetComponent<TMP_InputField>().text = joinCode;
    }

}
