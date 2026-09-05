using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Instrumentation for the connection, in the same spirit as ColocationProbe and read the same way:
/// through Debug.Log, which DebugLog mirrors into the in-headset box on the Debugger object. No new
/// UI, no adb logcat.
///
/// This exists because nothing in the project could previously tell a live session from a dead one.
/// StartHost/StartClient's return values are discarded (RelayVivox.cs), and the only
/// OnClientDisconnectCallback anywhere is RoomAnchor's, server-side, clearing the world-grab lock.
/// A client whose connection died *after* Netcode had already synchronized it into the host's scene
/// therefore looked exactly like a client that was connected and alone: no other player, no scene
/// changes following the host, and its own GameSelector.RequestGame silently early-outing on
/// !IsOwner because its player object never finished spawning. That is the shape of the Quest 3 ->
/// Quest 3S failure, and it is invisible without this.
///
/// Lives on Network Manager, not on the rig: Netcode marks that object DontDestroyOnLoad, so the
/// probe outlives every LoadSceneMode.Single switch and is still listening when a LATE drop happens.
/// GameController cannot do this job — it dies with OpeningScene the moment the joiner is
/// synchronized into the game scene, which is right about when the interesting failure starts.
///
/// Read it like this:
///
///   "connection LOST" with a reason   -> the room refused or dropped us, and the reason names why.
///   "connection LOST", reason empty   -> Netcode's config-hash refusal (NetworkConfig.ForceSamePrefabs
///     is 1 and it disconnects with no reason string). Compare prefabs= on the two devices: if the
///     numbers differ the builds differ. See docs/bigFixes1.md §1.
///   "transport FAILURE"               -> the failure is underneath Netcode, in UTP/Relay/DTLS. This
///     is the only reading that would justify the udp switch in docs/quest_networking_plan.md.
///   "peer JOINED" then "peer LEFT"    -> the other headset reached us and then died. Whose fault
///     that is depends on which device logs the transport failure.
///   connected=True clients=1 forever, and scene changes DO follow -> we are genuinely in the room
///     and alone-looking for some other reason. Not a network fault: go to ColocationProbe.
///
/// The periodic line is OFF by default on purpose. The debug box holds ten lines, and a 1 Hz
/// heartbeat scrolls the events worth reading straight out of it. Switch LogHeartbeat on when a
/// number is wanted, not to find out whether something broke — the events already say that.
/// </summary>
public class NetworkProbe : MonoBehaviour
{
    [Tooltip("Print one state line per interval. Off by default: the in-headset box holds ten " +
             "lines and a heartbeat scrolls the connection events out of it.")]
    public bool LogHeartbeat = false;

    public float IntervalSeconds = 1f;

    private float nextPrintAt;

    void Start()
    {
        // Start(), not OnEnable(): NetworkManager.Awake has to have run for Singleton to exist.
        // This component sits on the same GameObject, and Unity gives no ordering guarantee
        // between two components' OnEnable on a scene load.
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("NetworkProbe: no NetworkManager.Singleton. Nothing will be reported.");
            return;
        }

        nm.OnConnectionEvent += HandleConnectionEvent;
        nm.OnTransportFailure += HandleTransportFailure;
        nm.OnClientStopped += HandleClientStopped;
        nm.OnServerStopped += HandleServerStopped;
    }

    void OnDestroy()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        nm.OnConnectionEvent -= HandleConnectionEvent;
        nm.OnTransportFailure -= HandleTransportFailure;
        nm.OnClientStopped -= HandleClientStopped;
        nm.OnServerStopped -= HandleServerStopped;
    }

    // ------------------------------------------------------------------ events

    /// <summary>
    /// One hook for all four cases. ClientConnected/ClientDisconnected are about us;
    /// PeerConnected/PeerDisconnected are about the other headset, which is the half that answers
    /// "why can I not see the other player".
    /// </summary>
    private void HandleConnectionEvent(NetworkManager nm, ConnectionEventData data)
    {
        switch (data.EventType)
        {
            case ConnectionEvent.ClientConnected:
                Debug.Log("NET: connected as client " + data.ClientId + ". " + State());
                break;

            case ConnectionEvent.ClientDisconnected:
                // LogError, not Log: this is the line the whole probe exists to surface, and an
                // error is worth losing a heartbeat line over.
                Debug.LogError("NET: connection LOST. " + Reason() + " " + State());
                break;

            case ConnectionEvent.PeerConnected:
                Debug.Log("NET: peer JOINED as " + data.ClientId + ". " + State());
                break;

            case ConnectionEvent.PeerDisconnected:
                Debug.LogError("NET: peer LEFT (" + data.ClientId + "). " + State());
                break;
        }
    }

    private void HandleTransportFailure()
    {
        // Netcode never saw a refusal; UTP itself gave up. Relay allocation gone, DTLS handshake
        // dead, or the network vanished under us.
        Debug.LogError("NET: transport FAILURE — the failure is under Netcode (UTP/Relay). " + State());
    }

    private void HandleClientStopped(bool wasHost)
    {
        Debug.Log("NET: client stopped (wasHost=" + wasHost + "). " + Reason());
    }

    private void HandleServerStopped(bool wasHost)
    {
        Debug.Log("NET: server stopped (wasHost=" + wasHost + ").");
    }

    // ------------------------------------------------------------------ heartbeat

    void Update()
    {
        if (!LogHeartbeat || Time.realtimeSinceStartup < nextPrintAt)
        {
            return;
        }
        nextPrintAt = Time.realtimeSinceStartup + Mathf.Max(0.25f, IntervalSeconds);

        Debug.Log("NET " + State());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Everything worth knowing in one line. prefabs= is the integer docs/bigFixes1.md §1 reduces
    /// the whole ForceSamePrefabs config-hash problem to: it MUST be equal on both devices, and the
    /// Editor and a player build cannot agree while the list holds package Editor/ prefabs.
    /// </summary>
    private static string State()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return "(no NetworkManager)";
        }

        StringBuilder line = new StringBuilder(160);
        line.Append("host=").Append(nm.IsHost);
        line.Append(" server=").Append(nm.IsServer);
        line.Append(" connected=").Append(nm.IsConnectedClient);
        line.Append(" id=").Append(nm.LocalClientId);

        // ConnectedClients is server-only; ConnectedClientsIds is populated on a client too.
        line.Append(" clients=").Append(nm.IsListening ? nm.ConnectedClientsIds.Count : 0);

        line.Append(" scene=").Append(SceneManager.GetActiveScene().name);

        NetworkConfig config = nm.NetworkConfig;
        if (config != null && config.Prefabs != null && config.Prefabs.NetworkPrefabOverrideLinks != null)
        {
            line.Append(" prefabs=").Append(config.Prefabs.NetworkPrefabOverrideLinks.Count);
        }

        line.Append(" player=").Append(nm.LocalClient != null && nm.LocalClient.PlayerObject != null);

        return line.ToString();
    }

    private static string Reason()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || string.IsNullOrEmpty(nm.DisconnectReason))
        {
            // Netcode's own config-hash refusal calls DisconnectClient with no reason string
            // (ConnectionRequestMessage.Deserialize), so "empty" is itself a diagnosis.
            return "reason=<none: usually a NetworkConfig/prefab-list mismatch between builds>";
        }
        return "reason=\"" + nm.DisconnectReason + "\"";
    }
}
