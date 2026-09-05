using System.Collections;
using System.Collections.Generic;
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
/// spawnSlot is a place on PlayerRing's TWELVE-slot ring, not a base index, and the two are not
/// the same number: PickFreeSlot hands out the middle of the widest gap, so the first four players
/// get slots 0, 6, 3, 9. Treating that as a base index is what gave the second player into a
/// two-player game no base at all. BaseForRingSlot below is the map, and the four playing slots are
/// the four a base actually stands at. Everything past those spectates: NetworkBaseControl is never
/// resolved for them, so ControlListener tolerates a null base throughout.
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

    /// <summary>
    /// Ring slot -> base index, -1 for a slot with no base behind it. NOT the identity:
    /// PlayerControls.spawnSlot is a place on PlayerRing's twelve-slot ring, and PickFreeSlot
    /// deliberately hands out the middle of the widest gap -- 0, 6, 3, 9 for the first four
    /// players -- so treating it as a base index gave the second player no base at all.
    ///
    /// The four entries are not a convention, they are a measurement: SeatPosition/SeatYaw above
    /// and PlayerRing.SlotPosition/SlotRotation agree on position AND facing for exactly these four
    /// slots. A player's base is therefore the one they are standing behind, which is the only
    /// arrangement that reads correctly around a real table. Re-derive this table if either set of
    /// numbers ever moves.
    ///
    /// Because PickFreeSlot hands out 0, 6, 3, 9 in that order, the first four joiners still get
    /// bases 0, 1, 2, 3 in join order and the fifth still spectates.
    /// </summary>
    static readonly int[] BaseForRingSlot = { 0, -1, -1, 2, -1, -1, 1, -1, -1, 3, -1, -1 };

    public static int BaseIndexForRingSlot(int slot) =>
        slot >= 0 && slot < BaseForRingSlot.Length ? BaseForRingSlot[slot] : -1;

    // Server-side. Which NetworkObject is serving which seat, so a seat is never handed a second
    // base and a reconnecting player gets the one already standing there.
    readonly NetworkObject[] baseBySeat = new NetworkObject[PlayingSeats];

    // Server-side scratch for ServeSeats, reused across ticks so a 4 Hz poll allocates nothing.
    readonly bool[] claimedThisTick = new bool[PlayingSeats];
    readonly List<ulong> unplacedClients = new List<ulong>();

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

    /// <summary>
    /// Two passes, and the order between them is the whole point of splitting them.
    ///
    /// Pass 1 is the rule: a player standing on one of the four ring slots that a base stands at
    /// gets THAT base, whoever else is in the room and in whatever order Netcode happens to
    /// enumerate the clients.
    ///
    /// Pass 2 is the fallback, and it only ever fires for a ring fragmented by mid-game departures
    /// -- five players, then the one at slot 0 leaves, and PickFreeSlot answers 11 for the next
    /// joiner rather than 0. Rather than leave base 0 standing empty while a player has none, hand
    /// out the lowest base nobody claimed in pass 1. The cost is that such a player is standing
    /// somewhere other than behind their own base, which is strictly better than not playing. Doing
    /// it after pass 1 is what stops a leftover player taking a base its own ring slot entitles
    /// somebody else to.
    /// </summary>
    void ServeSeats()
    {
        NetworkObject frame = BashRoot.SpawnParent;
        NetworkManager nm = NetworkManager.Singleton;
        if (frame == null || nm == null)
        {
            return;                     // the scene is not up yet; try again next tick
        }

        for (int i = 0; i < PlayingSeats; i++)
        {
            claimedThisTick[i] = false;
        }
        unplacedClients.Clear();

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

            int slot = player.spawnSlot.Value;
            if (slot < 0)
            {
                continue;               // not seated on the ring yet; try again next tick
            }

            int seat = BaseIndexForRingSlot(slot);
            if (seat < 0)
            {
                unplacedClients.Add(entry.Key);
                continue;
            }

            claimedThisTick[seat] = true;
            Serve(seat, entry.Key, frame);
        }

        foreach (ulong client in unplacedClients)
        {
            // Keep the base this client was already handed rather than re-deriving one from
            // scratch. ConnectedClients enumerates in insertion order today, but nothing promises
            // it, and a fallback base changing hands between two players every tick would re-run
            // OnGainedOwnership on both of them four times a second.
            int seat = UnclaimedSeatOwnedBy(client);
            if (seat < 0)
            {
                seat = FirstUnclaimedSeat();
            }
            if (seat < 0)
            {
                continue;               // all four bases are spoken for: this player spectates
            }

            claimedThisTick[seat] = true;
            Serve(seat, client, frame);
        }
    }

    /// <summary>A base this client already owns that pass 1 did not claim for somebody else.</summary>
    int UnclaimedSeatOwnedBy(ulong client)
    {
        for (int i = 0; i < PlayingSeats; i++)
        {
            if (claimedThisTick[i])
            {
                continue;
            }

            NetworkObject standing = baseBySeat[i];
            if (standing != null && standing.IsSpawned && standing.OwnerClientId == client)
            {
                return i;
            }
        }
        return -1;
    }

    int FirstUnclaimedSeat()
    {
        for (int i = 0; i < PlayingSeats; i++)
        {
            if (!claimedThisTick[i])
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Give this client the base for that index, spawning it if it is not up yet.</summary>
    void Serve(int seat, ulong owner, NetworkObject frame)
    {
        NetworkObject existing = baseBySeat[seat];
        if (existing != null && existing.IsSpawned)
        {
            // The seat was vacated and refilled. The base is still standing; hand it to
            // whoever holds the seat now, so their NetworkBaseControl.OnGainedOwnership
            // wires it to their own Controls.
            if (existing.OwnerClientId != owner)
            {
                existing.ChangeOwnership(owner);
            }
            return;
        }

        SpawnBase(seat, owner, frame);
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
        SpawnNetworkCannonLineServerRpc(linePoints, LocalBaseIndex(), lifetime);
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
    /// This client's BASE index (0-3), or -1 before the server has seated them and for a
    /// spectator. Same resolution MenuControl.ResolveMyPlayer and PlateauGame.LocalSeat use —
    /// asking Netcode directly rather than depending on rebind order, because BASH's own
    /// PlayerControls pushed itself into the SpawnManager and this project's does not.
    ///
    /// It is used for colour, and returning the raw ring slot is why two players' trails used to
    /// come out the same: LineControls.ChangeMaterial clamps into 0..3, so slots 6 and 9 both
    /// clamped to 3.
    ///
    /// Derived from the ring slot, i.e. from pass 1 of ServeSeats. A player who got a base from
    /// pass 2's unclaimed-base fallback answers -1 here and their trails clamp to colour 0 — a
    /// cosmetic mismatch confined to the same five-players-and-a-departure case the fallback
    /// exists for. Fixing it properly means the server telling each client which base it was
    /// actually handed, which is not worth a NetworkVariable until spectator seats are tested at
    /// all (see CLAUDE.md, Known rough edges).
    /// </summary>
    public static int LocalBaseIndex()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return -1;
        }

        PlayerControls player = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        return player != null ? BaseIndexForRingSlot(player.spawnSlot.Value) : -1;
    }
}
