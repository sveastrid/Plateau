using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hands out bases and spawns the networked trail objects. One per BASH scene, on the scene-root
/// object "Spawn Manager", which carries a NetworkObject of its own.
///
/// Seats come from PlayerControls.spawnSlot, the only stable per-player index in the project —
/// not from NetworkManager.ConnectedClients.Count, which is what BASH used and which is wrong in
/// a way that already bites it: if client 2 leaves and someone else joins, Count is 2 again and
/// two players are handed the same base. spawnSlot is assigned by PlayerRing.PickFreeSlot, which
/// reuses a vacated slot, so a reconnecting player lands back in the seat they left and gets
/// their own base back rather than a duplicate of somebody else's. It is also the colour index,
/// exactly as it is in Chasms.
///
/// The first four seats play; seats 4-11 get a ring slot and spectate. NetworkBaseControl is
/// never resolved for them, so ControlListener tolerates a null base throughout.
/// </summary>
public class SpawnManager : NetworkBehaviour
{
    public GameObject Base;
    public GameObject NetworkCannonLine;

    /// <summary>Seats 0-3 play. The rest watch.</summary>
    public const int PlayingSeats = 4;

    // Board-local. These are BASH's four hard-coded world positions with its old board origin
    // (0, 0.6, 2) subtracted — and they are exactly IslandManager.baseExclusionZones, which was
    // written in board-local space from the start. That agreement is the check that the rebasing
    // is right.
    static readonly Vector3[] SeatPosition =
    {
        new Vector3( 0f,   0f, -1.4f),
        new Vector3( 0f,   0f,  1.4f),
        new Vector3( 1.4f, 0f,  0f),
        new Vector3(-1.4f, 0f,  0f),
    };

    static readonly float[] SeatYaw = { 0f, 180f, -90f, 90f };

    // Server-side. Which NetworkObject is serving which seat, so a seat is never handed a second
    // base and a reconnecting player gets the one already standing there.
    readonly NetworkObject[] baseBySeat = new NetworkObject[PlayingSeats];

    // A tick, not a hook on OnClientConnectedCallback: spawnSlot is assigned inside
    // PlayerControls.OnNetworkSpawn, which can run after this component's OnNetworkSpawn. This is
    // the identical trap PlateauGame documents for handing a late joiner their army. 4 Hz is
    // plenty for something a human waits on.
    const float TickSeconds = 0.25f;
    float nextTick;

    void Start()
    {
        GameObject controls = GameObject.Find("Controls");
        ControlListener listener = controls != null ? controls.GetComponent<ControlListener>() : null;
        if (listener == null)
        {
            Debug.LogWarning("SpawnManager: no 'Controls' object with a ControlListener in this " +
                             "scene, so nothing can fire. The name is load-bearing.");
            return;
        }

        listener.ConnectSpawnManagerToControls(this);
    }

    void Update()
    {
        if (!IsServer || !IsSpawned)
        {
            return;
        }

        nextTick -= Time.deltaTime;
        if (nextTick > 0f)
        {
            return;
        }
        nextTick = TickSeconds;

        ServeSeats();
    }

    void ServeSeats()
    {
        NetworkObject frame = BashRoot.SpawnParent;
        NetworkManager nm = NetworkManager.Singleton;
        if (frame == null || nm == null)
        {
            return;                     // the scene is not up yet; try again next tick
        }

        foreach (var entry in nm.ConnectedClients)
        {
            NetworkObject playerObject = entry.Value.PlayerObject;
            if (playerObject == null)
            {
                continue;
            }

            PlayerControls player = playerObject.GetComponent<PlayerControls>();
            if (player == null)
            {
                continue;
            }

            int seat = player.spawnSlot.Value;
            if (seat < 0 || seat >= PlayingSeats)
            {
                continue;               // not seated yet, or a spectator
            }

            NetworkObject existing = baseBySeat[seat];
            if (existing != null && existing.IsSpawned)
            {
                // The seat was vacated and refilled. The base is still standing; hand it to
                // whoever holds the seat now, so their NetworkBaseControl.OnGainedOwnership
                // wires it to their own Controls.
                if (existing.OwnerClientId != entry.Key)
                {
                    existing.ChangeOwnership(entry.Key);
                }
                continue;
            }

            SpawnBase(seat, entry.Key, frame);
        }
    }

    void SpawnBase(int seat, ulong owner, NetworkObject frame)
    {
        // Spawn first, then parent: TrySetParent is Netcode's documented runtime parenting call
        // and replicates the new parent together with the local transform. The local transform is
        // set BEFORE the reparent, with worldPositionStays false, so those exact numbers survive
        // it. The prefab's own localScale (1.5, 0.75, 1.5) is deliberately left alone — the
        // gamepieces under it are authored at (1, 2, 1) so the product comes out uniform.
        GameObject newBase = Instantiate(Base);
        newBase.transform.localPosition = SeatPosition[seat];
        newBase.transform.localRotation = Quaternion.Euler(0f, SeatYaw[seat], 0f);

        newBase.GetComponent<NetworkBaseControl>().ChangeMaterial(seat);

        NetworkObject networkBase = newBase.GetComponent<NetworkObject>();
        // destroyWithScene: true. Netcode's default is false, which carries a spawned object
        // across a LoadSceneMode.Single switch — right for the players and Room Anchor, wrong
        // here: a base belongs to the BASH scene and must not follow the room into Chasms. Its
        // parent Bash Root is an in-scene object and goes with the scene regardless.
        networkBase.SpawnWithOwnership(owner, true);

        if (!networkBase.TrySetParent(frame, false))
        {
            // Without the frame the base is in raw world space, which goes wrong the moment
            // anybody grabs the board. Loud, because it is silent otherwise.
            Debug.LogError("SpawnManager: could not parent seat " + seat + "'s base to Bash Root.");
        }

        baseBySeat[seat] = networkBase;
        PlaceBaseClientRpc(networkBase.NetworkObjectId, seat);
    }

    /// <summary>
    /// Colour and place a base on every client. The placement is belt and braces — the spawn
    /// message already carries the transform — but it costs one small RPC per base for the life
    /// of a session and removes any doubt about how a spawn payload's transform is interpreted
    /// under a parent whose scale the players are free to change.
    /// </summary>
    [ClientRpc]
    private void PlaceBaseClientRpc(ulong netId, int seat)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var obj))
        {
            return;
        }

        obj.GetComponent<NetworkBaseControl>().ChangeMaterial(seat);

        if (seat >= 0 && seat < PlayingSeats)
        {
            obj.transform.localPosition = SeatPosition[seat];
            obj.transform.localRotation = Quaternion.Euler(0f, SeatYaw[seat], 0f);
        }
    }

    // ------------------------------------------------------------------ trails

    /// <summary>
    /// Publish a finished trail to the room. lifetime 0 means "for ever", which is what a movement
    /// line passes and what keeps its behaviour identical: it is the record of where a piece went.
    /// An artillery arc is not — it describes a shell that has already landed — so it passes a
    /// lifetime and is despawned rather than doubling the trails-grow-without-bound rough edge.
    /// </summary>
    public void SpawnNetworkCannonLine(Vector3[] linePoints, float lifetime = 0f)
    {
        SpawnNetworkCannonLineServerRpc(linePoints, LocalSeat(), lifetime);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SpawnNetworkCannonLineServerRpc(Vector3[] linePoints, int materialNumber, float lifetime)
    {
        NetworkObject frame = BashRoot.SpawnParent;
        if (frame == null)
        {
            return;
        }

        GameObject newCannonLine = Instantiate(NetworkCannonLine);
        newCannonLine.transform.localPosition = Vector3.zero;
        newCannonLine.transform.localRotation = Quaternion.identity;
        newCannonLine.transform.localScale = Vector3.one;

        newCannonLine.GetComponent<LineControls>().ChangeMaterial(materialNumber);

        NetworkObject netLine = newCannonLine.GetComponent<NetworkObject>();
        netLine.Spawn(true);            // destroyWithScene: see SpawnBase

        if (!netLine.TrySetParent(frame, false))
        {
            Debug.LogError("SpawnManager: could not parent a cannon line to Bash Root.");
        }

        // The points are already in Bash Root's local space, which is exactly the space
        // PipeRenderer builds its mesh in.
        newCannonLine.GetComponent<PipeRenderer>().SetPositions(linePoints);
        SyncLinePointsClientRpc(netLine.NetworkObjectId, linePoints, materialNumber);

        if (lifetime > 0f)
        {
            StartCoroutine(DespawnAfter(netLine, lifetime));
        }
    }

    /// <summary>
    /// Server-side timed despawn — the only network lifecycle in BASH that is not "Reset Game".
    /// Guarded on both counts because Reset Game (NetworkBaseControl.DeleteAllLinesServerRpc) can
    /// get to the same object first.
    /// </summary>
    IEnumerator DespawnAfter(NetworkObject netObj, float seconds)
    {
        yield return new WaitForSeconds(seconds);

        if (netObj != null && netObj.IsSpawned)
        {
            netObj.Despawn(true);
        }
    }

    [ClientRpc]
    private void SyncLinePointsClientRpc(ulong netId, Vector3[] linePoints, int materialNumber)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var obj))
        {
            return;
        }

        obj.transform.localPosition = Vector3.zero;
        obj.transform.localRotation = Quaternion.identity;
        obj.transform.localScale = Vector3.one;

        obj.GetComponent<LineControls>().ChangeMaterial(materialNumber);
        obj.GetComponent<PipeRenderer>().SetPositions(linePoints);
    }

    // ------------------------------------------------------------------ seats

    /// <summary>
    /// This client's seat, or -1 before the server has seated them. Same resolution
    /// MenuControl.ResolveMyPlayer and PlateauGame.LocalSeat use — asking Netcode directly rather
    /// than depending on rebind order, because BASH's own PlayerControls pushed itself into the
    /// SpawnManager and this project's does not.
    /// </summary>
    public static int LocalSeat()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return -1;
        }

        PlayerControls player = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        return player != null ? player.spawnSlot.Value : -1;
    }
}
