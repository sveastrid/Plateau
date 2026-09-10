using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Netcode connection approval: the name and the library arrive with the connection, and a joiner
/// the room cannot accept is told why.
///
/// Lives on the Network Manager object, which Netcode marks DontDestroyOnLoad, so it is still
/// listening after a game switch — the same reason BoardAnchor and NetworkProbe live there.
///
/// **This is the one genuinely risky change in docs/LobbyUpdate.md.** ConnectionApproval is a
/// NetworkConfig field, and NetworkConfig is what the join handshake hashes alongside the
/// ForceSamePrefabs prefab set. An old build cannot join a new one, with the same reasonless
/// refusal documented in docs/bugFixes2.md §0. Reflash both headsets from the same build.
///
/// It is set from code, in <see cref="Prepare"/>, immediately before StartHost/StartClient rather
/// than authored in the scene: the host and the joiner then take the value from the same line, so
/// the two halves of the hash cannot come from an Inspector one of them forgot to save.
/// </summary>
public class RoomApproval : MonoBehaviour
{
    public static RoomApproval Instance { get; private set; }

    /// <summary>
    /// Server-side, what each connected client said about itself. PlayerControls and PlayerLibrary
    /// read their server-written NetworkVariables out of this rather than waiting for the owner to
    /// send an RPC after spawning — which is the asymmetry approval exists to remove.
    /// </summary>
    private readonly Dictionary<ulong, ConnectionPayload> accepted =
        new Dictionary<ulong, ConnectionPayload>();

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>
    /// Switch approval on and load this client's payload, immediately before StartHost or
    /// StartClient. Called by RelayVivox on both paths so they cannot disagree.
    /// </summary>
    public void Prepare(string playerName, bool asHost)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        ConnectionPayload payload = ConnectionPayload.ForLocalPlayer(playerName);

        nm.NetworkConfig.ConnectionApproval = true;
        nm.NetworkConfig.ConnectionData = payload.Encode();

        if (asHost)
        {
            accepted.Clear();
            nm.ConnectionApprovalCallback = Approve;

            // The host does go through the callback, but recording it here as well means the room
            // owner's own name and library are known even if that ever changes.
            accepted[NetworkManager.ServerClientId] = payload;

            nm.OnClientDisconnectCallback += Forget;
        }
    }

    private void Forget(ulong clientId)
    {
        accepted.Remove(clientId);
    }

    /// <summary>What a client claimed at approval time, or a blank payload.</summary>
    public bool TryGet(ulong clientId, out ConnectionPayload payload)
    {
        return accepted.TryGetValue(clientId, out payload);
    }

    /// <summary>
    /// Server-side. Runs before the client is synchronized into a scene, which is what makes a
    /// refusal cheap and a reason readable.
    ///
    /// The library check here is a UX and social rule, **not DRM**, and it is worth being honest
    /// about that in the place that implements it. There is no dedicated server: the "server" is
    /// another player's headset, and ownedMask is asserted by the client that sends it. A modified
    /// client can claim to own everything. The paid game's scene and assets ship inside the APK
    /// either way, because Meta add-ons gate entitlement and not delivery. Do not build a defence
    /// here that cannot work.
    /// </summary>
    private void Approve(NetworkManager.ConnectionApprovalRequest request,
                         NetworkManager.ConnectionApprovalResponse response)
    {
        response.CreatePlayerObject = true;
        response.Position = null;
        response.Rotation = null;

        if (!ConnectionPayload.TryDecode(request.Payload, out ConnectionPayload payload))
        {
            response.Approved = false;
            response.Reason = "The room could not read your join request (different version?)";
            response.Pending = false;
            return;
        }

        string ourBuild = Application.version ?? "";
        if (payload.build != ourBuild)
        {
            // The single most mystifying failure this project has: ForceSamePrefabs refuses a
            // mismatched build with no reason string at all, so the joiner sits on "Joining room..."
            // for ever. Catching it here, one step earlier, turns it into a sentence.
            response.Approved = false;
            response.Reason = "Different version — the room is on " +
                              (string.IsNullOrEmpty(ourBuild) ? "another build" : ourBuild);
            response.Pending = false;
            return;
        }

        if (RoomOptions.IsPublic && !string.IsNullOrEmpty(RoomOptions.GameKey))
        {
            GameCatalog catalog = GameCatalog.Instance;
            GameModule module = catalog != null ? catalog.ByKey(RoomOptions.GameKey) : null;

            if (module != null && !StoreService.MaskAllows(payload.ownedMask, module))
            {
                response.Approved = false;
                response.Reason = "You do not own " + module.DisplayName;
                response.Pending = false;
                return;
            }
        }

        accepted[request.ClientNetworkId] = payload;

        response.Approved = true;
        response.Pending = false;
    }
}
