using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The room-wide facts about a session: which shared spatial anchor everybody is aligned to, and
/// where the board has been put. One spawned singleton, not a scene object — GlobalObjectIdHash
/// appears in no file under Assets/Scenes, so there are no in-scene NetworkObjects to hang this on,
/// and Netcode forbids one on the NetworkManager's own GameObject.
///
/// Spawned by BoardAnchor on the server-started callback, with destroyWithScene false, so it
/// survives GameSelector's LoadSceneMode.Single game switch and outlives any one player object —
/// which matters, because NetworkReconnectHandler (:18-28) shuts the client down and restarts it
/// as routine behaviour and player objects respawn.
///
/// The anchor identity and the content pose live on the same object deliberately: they are both
/// facts about the room rather than about a player, and a late joiner then gets the anchor and the
/// board placement in the same replication pass.
/// </summary>
public class RoomAnchor : NetworkBehaviour
{
    public static RoomAnchor Instance { get; private set; }

    /// <summary>worldHolder when nobody is holding the world. Not 0 — that is the host's client id.</summary>
    public const ulong NoHolder = ulong.MaxValue;

    // Hard bounds on the board's size, enforced server-side as well as in the gesture, so a client
    // that has been modified cannot shrink the board to a speck for everybody.
    public const float MinScale = 0.15f;
    public const float MaxScale = 4f;

    // --- The common anchoring point. FixedString64Bytes holds a 36-character GUID string with
    // room to spare. Published in one server call so no client can ever see a group without a UUID.
    public NetworkVariable<FixedString64Bytes> anchorGroup = new NetworkVariable<FixedString64Bytes>(
        "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<FixedString64Bytes> anchorUuid = new NetworkVariable<FixedString64Bytes>(
        "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- The content frame: where World Root goes. Yaw only and uniform scale, deliberately — a
    // board that can be pitched off a real table or squashed on one axis is a board somebody will
    // pitch and squash.
    public NetworkVariable<Vector3> contentPos = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> contentYaw = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> contentScale = new NetworkVariable<float>(
        1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Who is currently allowed to write the three above. First to squeeze both grips wins until
    // they let go. Without this, two players gesturing at once each stream a pose derived from
    // their own hands, the server takes whichever arrived last, and the board oscillates between
    // two placements at the tick rate for as long as both hold on.
    public NetworkVariable<ulong> worldHolder = new NetworkVariable<ulong>(
        NoHolder, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- Voice chat, for the whole room. Off by default and host-controlled: the common case for
    // this game is everybody sitting at one real table, where Vivox costs money to deliver audio
    // people can already hear, and adds an echo of themselves on top. It lives here rather than on
    // the player because it is a fact about the room, and because a late joiner then picks up the
    // room's answer in the same replication pass as the anchor and the board placement.
    public NetworkVariable<bool> voiceEnabled = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- What kind of room this is. Server-written, alongside the anchor identity and the content
    // pose, because they are the same kind of fact — about the room, not about a player — and a
    // late joiner then gets all of them in one replication pass.
    //
    // A public room is listed in the directory and locked to one game: RequestGameServerRpc refuses
    // anything else, and the room menu shows only that game with a line saying why. A private room
    // may play anything in the room's combined library.
    public NetworkVariable<bool> isPublic = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<FixedString32Bytes> roomGameKey = new NetworkVariable<FixedString32Bytes>(
        "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private RelayVivox voice;

    /// <summary>The game a public room is locked to, or null when the room is private.</summary>
    public static string LockedGameKey
    {
        get
        {
            RoomAnchor room = Instance;
            if (room == null || !room.isPublic.Value)
            {
                return null;
            }

            string key = room.roomGameKey.Value.ToString();
            return string.IsNullOrEmpty(key) ? null : key;
        }
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
        {
            // RoomOptions carries the host's choice across the few frames between the Play panel
            // and OnServerStarted, where BoardAnchor spawns this. It is a static and therefore
            // outlives a scene, which is why BoardAnchor.Awake resets it.
            isPublic.Value = RoomOptions.IsPublic;
            roomGameKey.Value = RoomOptions.GameKey ?? "";

            // A holder who crashes, drops wifi, or is disconnected by NetworkReconnectHandler must
            // not lock the board for the rest of the session.
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
        }

        // Voice is driven from here for the host as well as for clients, so there is a single path
        // to reason about and the host cannot drift out of step with the room it is setting.
        voice = NetworkManager.Singleton != null
                    ? NetworkManager.Singleton.GetComponent<RelayVivox>()
                    : null;

        // Apply before subscribing, and apply unconditionally: Netcode does not raise
        // OnValueChanged for the value a client arrives already holding, so a late joiner entering
        // a room that has voice switched on would otherwise sit there silent.
        ApplyVoice(voiceEnabled.Value);
        voiceEnabled.OnValueChanged += HandleVoiceEnabledChanged;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        }
        voiceEnabled.OnValueChanged -= HandleVoiceEnabledChanged;
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void HandleClientDisconnect(ulong clientId)
    {
        if (worldHolder.Value == clientId)
        {
            worldHolder.Value = NoHolder;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ClaimWorldServerRpc(ServerRpcParams p = default)
    {
        if (worldHolder.Value != NoHolder)
        {
            return;                       // somebody else is holding it; the claim just fails
        }
        worldHolder.Value = p.Receive.SenderClientId;
    }

    [ServerRpc(RequireOwnership = false)]
    public void ReleaseWorldServerRpc(ServerRpcParams p = default)
    {
        if (worldHolder.Value == p.Receive.SenderClientId)
        {
            worldHolder.Value = NoHolder;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetContentServerRpc(Vector3 pos, float yaw, float scale, ServerRpcParams p = default)
    {
        if (worldHolder.Value != p.Receive.SenderClientId)
        {
            return;                       // not yours to move
        }
        contentPos.Value = pos;
        contentYaw.Value = yaw;
        contentScale.Value = Mathf.Clamp(scale, MinScale, MaxScale);
    }

    [ServerRpc(RequireOwnership = false)]
    public void PublishAnchorServerRpc(string group, string uuid)
    {
        anchorGroup.Value = group;
        anchorUuid.Value = uuid;
    }

    /// <summary>
    /// Switch voice chat on or off for the whole room. Only the host has a key for this — the
    /// client menu prefab does not carry one — but the check is here rather than only in the UI,
    /// so a modified client cannot start billing everybody's voice minutes.
    ///
    /// The host is the server in this project (RelayVivox.CreateRelay calls StartHost), so "the
    /// room owner" and "the sender is the server" are the same test.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void SetVoiceEnabledServerRpc(bool on, ServerRpcParams p = default)
    {
        if (p.Receive.SenderClientId != NetworkManager.ServerClientId)
        {
            return;                       // not the host; not theirs to decide
        }
        voiceEnabled.Value = on;
    }

    private void HandleVoiceEnabledChanged(bool previous, bool current)
    {
        ApplyVoice(current);
    }

    private void ApplyVoice(bool on)
    {
        // Nothing to do when voice has never been switched on, which is the whole point: a room
        // that leaves this alone never touches Vivox at all.
        if (voice == null)
        {
            if (on)
            {
                Debug.LogWarning("RoomAnchor: voice was switched on but there is no RelayVivox on " +
                                 "the NetworkManager object, so nobody will hear anything.");
            }
            return;
        }

        voice.ApplyVoiceState(on);
    }
}
