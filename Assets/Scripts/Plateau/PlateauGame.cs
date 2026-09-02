using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The room's Plateau board: who has what, where, and which gaps have been bridged.
///
/// Lives on Room Anchor.prefab, alongside RoomAnchor. That object is already spawned exactly once
/// (BoardAnchor.HandleServerStarted, with destroyWithScene false), already survives every
/// LoadSceneMode.Single game switch, and RoomAnchor's own comment already frames it as "the
/// room-wide facts about a session" — which a board in progress is. Adding a component to it costs
/// nothing; adding a second network prefab would mean editing DefaultNetworkPrefabs.asset, and with
/// NetworkConfig.ForceSamePrefabs on, a stale list is a hard connection failure with a generic
/// error message.
///
/// A piece is a STACK, not a unit — see PieceStack. The GameObjects are local visuals rebuilt from
/// these lists by PlateauPieceView; nothing here spawns a NetworkObject.
///
/// Write permission is the security boundary (CLAUDE.md). Every list and variable here is
/// server-written, every RPC is RequireOwnership=false and validates the sender, and every move is
/// re-checked server-side against the same PlateauMoveRules call the client used to highlight it.
/// </summary>
public class PlateauGame : NetworkBehaviour
{
    public static PlateauGame Instance { get; private set; }

    /// <summary>How often the server reconciles the board. Cheap; nothing here is per-frame work.</summary>
    const float ServerTickSeconds = 0.25f;

    public NetworkList<PieceStack> stacks;
    /// <summary>The adjacency graph, baked and published by the server. See PublishGraph.</summary>
    public NetworkList<BridgeEdge> edges;
    public NetworkList<PlacedBridge> placedBridges;
    /// <summary>
    /// Each seated player's held-gemheart count — plateauRules.md's "Each player can see how many
    /// gemhearts they currently hold", manually adjusted for now (harvesting isn't implemented; see
    /// CLAUDE.md). Indexed by seat, grown lazily as seats start using it.
    /// </summary>
    public NetworkList<byte> gemheartScores;

    public NetworkVariable<int> boardEpoch = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<byte> plateauCount = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<uint> graphHash = new NetworkVariable<uint>(
        0u, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    /// <summary>True once the room is in Chasms with a baked board and armies handed out.</summary>
    public NetworkVariable<bool> boardLive = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Raised whenever anything about the board changes. PlateauPieceView listens.</summary>
    public event System.Action BoardChanged;

    // --- local mirrors of the replicated graph, for the movement searches ---
    readonly List<BridgeEdge> edgeMirror = new List<BridgeEdge>();
    List<int>[] incidence = new List<int>[0];
    bool[] ownFlags = new bool[0];
    bool[] anyFlags = new bool[0];
    bool mirrorDirty = true;
    bool hashWarned;

    static readonly List<int> s_seats = new List<int>();
    readonly List<int> serverScratch = new List<int>();

    float nextTick;

    void Awake()
    {
        // NetworkList must exist before OnNetworkSpawn.
        stacks = new NetworkList<PieceStack>();
        edges = new NetworkList<BridgeEdge>();
        placedBridges = new NetworkList<PlacedBridge>();
        gemheartScores = new NetworkList<byte>();
    }

    public override void OnNetworkSpawn()
    {
        Instance = this;

        stacks.OnListChanged += HandleStacksChanged;
        placedBridges.OnListChanged += HandleBridgesChanged;
        edges.OnListChanged += HandleEdgesChanged;
        boardEpoch.OnValueChanged += HandleEpochChanged;

        if (IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += HandleLoadEventCompleted;
        }

        // A NetworkList's initial contents arrive in the spawn payload and do NOT raise
        // OnListChanged per element, so a late joiner would otherwise never build its visuals.
        mirrorDirty = true;
        BoardChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        stacks.OnListChanged -= HandleStacksChanged;
        placedBridges.OnListChanged -= HandleBridgesChanged;
        edges.OnListChanged -= HandleEdgesChanged;
        boardEpoch.OnValueChanged -= HandleEpochChanged;

        if (IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= HandleLoadEventCompleted;
        }

        // Guarded: RoomAnchor despawns and respawns on a reconnect, and so does this.
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void HandleStacksChanged(NetworkListEvent<PieceStack> e) => BoardChanged?.Invoke();
    void HandleBridgesChanged(NetworkListEvent<PlacedBridge> e) => BoardChanged?.Invoke();
    void HandleEpochChanged(int oldValue, int newValue) => BoardChanged?.Invoke();

    void HandleEdgesChanged(NetworkListEvent<BridgeEdge> e)
    {
        mirrorDirty = true;
        BoardChanged?.Invoke();
    }

    void HandleLoadEventCompleted(string sceneName, LoadSceneMode mode,
                                  List<ulong> completed, List<ulong> timedOut)
    {
        // Run the board tick next frame rather than up to a quarter second later.
        nextTick = 0f;
    }

    // ------------------------------------------------------------------ server

    /// <summary>
    /// Called on the server from GameSelector when any player picks a game.
    ///
    /// Picking Chasms always resets the board, which is why this hangs off the menu request rather
    /// than off a scene-load event: GameSelector.LoadGameScene deliberately no-ops when the room is
    /// already in that scene, so a load hook would never fire for Chasms -> Chasms.
    ///
    /// Only the clearing happens here. The board itself may not be loaded yet — the server is still
    /// in Stairs at this point when switching games — so handing out armies is left to the tick
    /// below, which runs as soon as PlateauBoard exists. Latch, do not depend on ordering.
    /// </summary>
    public void HandleGameRequested(string gameKey)
    {
        if (!IsServer || !IsSpawned || gameKey != GameRoutes.PlateauGameKey)
        {
            return;
        }

        stacks.Clear();
        placedBridges.Clear();
        boardLive.Value = false;
        boardEpoch.Value = boardEpoch.Value + 1;
        nextTick = 0f;

        Debug.Log("PlateauGame: board reset.");
    }

    void Update()
    {
        if (!IsSpawned || !IsServer || Time.unscaledTime < nextTick)
        {
            return;
        }
        nextTick = Time.unscaledTime + ServerTickSeconds;

        bool inChasms = SceneManager.GetActiveScene().name == GameRoutes.PlateauSceneName;
        PlateauBoard board = PlateauBoard.Instance;

        if (!inChasms || board == null)
        {
            if (boardLive.Value)
            {
                boardLive.Value = false;
            }
            return;
        }

        board.EnsureBaked();
        if (!board.IsBaked || board.CentralPlateau < 0)
        {
            return;
        }

        PublishGraph(board);

        // One path serves three cases: the first entry into Chasms, a reset (the lists were just
        // cleared, so every seat is missing an army), and a player who joined mid-game. Polling
        // rather than hooking OnClientConnectedCallback sidesteps the ordering puzzle that
        // spawnSlot is assigned later, inside PlayerControls.OnNetworkSpawn.
        GrantMissingArmies(board);

        if (!boardLive.Value)
        {
            boardLive.Value = true;
        }
    }

    void PublishGraph(PlateauBoard board)
    {
        if (graphHash.Value == board.GraphHash && edges.Count == board.Edges.Count)
        {
            return;
        }

        edges.Clear();
        for (int i = 0; i < board.Edges.Count; i++)
        {
            edges.Add(board.Edges[i]);
        }
        plateauCount.Value = (byte)Mathf.Min(255, board.PlateauCount);
        graphHash.Value = board.GraphHash;

        Debug.Log("PlateauGame: published " + edges.Count + " edges (hash " + graphHash.Value + ").");
    }

    void GrantMissingArmies(PlateauBoard board)
    {
        CollectSeats(s_seats);
        for (int i = 0; i < s_seats.Count; i++)
        {
            int seat = s_seats[i];
            if (SeatHasPieces(seat))
            {
                continue;
            }

            // plateauRules.md "Starting Forces", all on the central plateau.
            for (int kind = 0; kind < PlateauConst.KindCount; kind++)
            {
                int n = PlateauConst.StartingForces[kind];
                if (n > 0)
                {
                    AddPieces(board.CentralPlateau, seat, kind, n);
                }
            }
            Debug.Log("PlateauGame: starting forces for seat " + seat + ".");
        }
    }

    bool SeatHasPieces(int seat)
    {
        for (int i = 0; i < stacks.Count; i++)
        {
            if (stacks[i].seat == seat)
            {
                return true;
            }
        }
        for (int i = 0; i < placedBridges.Count; i++)
        {
            if (placedBridges[i].seat == seat)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The seats in the room. Read from the live players rather than tracked in a static, the same
    /// way PlayerControls.OccupiedSlots does — that method is private, so this is its three-line
    /// twin rather than a widening of PlayerControls' surface. Server only:
    /// ConnectedClientsList is not populated on a client.
    /// </summary>
    static void CollectSeats(List<int> into)
    {
        into.Clear();
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        foreach (NetworkClient client in nm.ConnectedClientsList)
        {
            if (client == null || client.PlayerObject == null)
            {
                continue;
            }
            PlayerControls pc = client.PlayerObject.GetComponent<PlayerControls>();
            if (pc == null)
            {
                continue;
            }
            int slot = pc.spawnSlot.Value;
            if (slot >= 0 && slot < PlateauConst.MaxSeats && !into.Contains(slot))
            {
                into.Add(slot);
            }
        }
    }

    static int SeatForClient(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
        {
            return -1;
        }
        if (client == null || client.PlayerObject == null)
        {
            return -1;
        }
        PlayerControls pc = client.PlayerObject.GetComponent<PlayerControls>();
        if (pc == null)
        {
            return -1;
        }
        int slot = pc.spawnSlot.Value;
        return slot >= 0 && slot < PlateauConst.MaxSeats ? slot : -1;
    }

    /// <summary>This client's seat, or -1 before the server has assigned one. Safe on a client.</summary>
    public static int LocalSeat()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return -1;
        }
        PlayerControls pc = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        if (pc == null)
        {
            return -1;
        }
        int slot = pc.spawnSlot.Value;
        return slot >= 0 && slot < PlateauConst.MaxSeats ? slot : -1;
    }

    // ------------------------------------------------------------------ mutation (server only)

    void AddPieces(int plateau, int seat, int kind, int n)
    {
        int idx = FindStack(plateau, seat, kind);
        if (idx >= 0)
        {
            PieceStack s = stacks[idx];
            s.count = (byte)Mathf.Min(255, s.count + n);
            stacks[idx] = s;                 // in place: a delta, not a whole-board resync
            return;
        }
        stacks.Add(new PieceStack(plateau, seat, kind, Mathf.Min(255, n)));
    }

    void RemovePieces(int index, int n)
    {
        PieceStack s = stacks[index];
        if (n >= s.count)
        {
            stacks.RemoveAt(index);          // a stack never sits at zero
            return;
        }
        s.count = (byte)(s.count - n);
        stacks[index] = s;
    }

    public int FindStack(int plateau, int seat, int kind)
    {
        for (int i = 0; i < stacks.Count; i++)
        {
            PieceStack s = stacks[i];
            if (s.plateau == plateau && s.seat == seat && s.kind == kind)
            {
                return i;
            }
        }
        return -1;
    }

    public int FindPlacedBridge(int edge)
    {
        for (int i = 0; i < placedBridges.Count; i++)
        {
            if (placedBridges[i].edge == edge)
            {
                return i;
            }
        }
        return -1;
    }

    // ------------------------------------------------------------------ RPCs

    /// <summary>
    /// Move <paramref name="count"/> pieces of one kind from one plateau to another. For a bridge
    /// still in reserve, <paramref name="to"/> is the new plateau it should reach and the gap is
    /// resolved from it; count is ignored (one bridge at a time).
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveServerRpc(byte from, byte kind, byte count, byte to, int epoch,
                                     ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || epoch != boardEpoch.Value)
        {
            return;                          // the board was reset between arming and sending
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        if (kind >= PlateauConst.KindCount || from >= board.PlateauCount ||
            to >= board.PlateauCount || from == to)
        {
            return;
        }

        int idx = FindStack(from, seat, kind);
        if (idx < 0)
        {
            return;                          // not the sender's to move
        }

        if (!TryBuildView(seat, -1, out PlateauMoveRules.View view))
        {
            return;
        }

        // The client's highlight is a hint. This is the rule.
        if (!PlateauMoveRules.IsLegal(view, (PieceKind)kind, from, to, serverScratch))
        {
            return;
        }

        if ((PieceKind)kind == PieceKind.Bridge)
        {
            if (!PlateauMoveRules.TryResolveBridgeEdge(view, to, out int edge))
            {
                return;
            }
            RemovePieces(idx, 1);
            placedBridges.Add(new PlacedBridge(edge, seat));
            return;
        }

        int n = Mathf.Clamp(count, 1, stacks[idx].count);
        RemovePieces(idx, n);
        AddPieces(to, seat, kind, n);
    }

    /// <summary>
    /// Pick up a bridge that has already been laid and put it somewhere else — plateauRules.md's
    /// "It must be repositioned to span from a plateau already connected by that player's bridges
    /// to a new plateau". Legality is computed with this bridge already lifted, so a bridge at the
    /// far end of a chain can be moved without its own presence propping up the component.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveBridgeServerRpc(byte fromEdge, byte to, int epoch,
                                           ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || epoch != boardEpoch.Value)
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked || to >= board.PlateauCount)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        int existing = FindPlacedBridge(fromEdge);
        if (existing < 0 || placedBridges[existing].seat != seat)
        {
            return;
        }

        if (!TryBuildView(seat, fromEdge, out PlateauMoveRules.View view))
        {
            return;
        }
        if (!PlateauMoveRules.TryResolveBridgeEdge(view, to, out int edge))
        {
            return;
        }

        placedBridges[existing] = new PlacedBridge(edge, seat);
    }

    /// <summary>
    /// The spawn menu's "+" key: one more piece of <paramref name="kind"/> on the central plateau,
    /// for the sender's own seat. Free and uncapped — plateauRules.md's gemheart cost ("Buying
    /// Pieces") is not implemented yet (see CLAUDE.md), so this is the sandbox stand-in for it.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestAddPieceServerRpc(byte kind, ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || kind >= PlateauConst.KindCount)
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked || board.CentralPlateau < 0)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        AddPieces(board.CentralPlateau, seat, kind, 1);
    }

    /// <summary>
    /// The spawn menu's "-" key: one fewer piece of <paramref name="kind"/> on
    /// <paramref name="plateau"/>, for the sender's own seat. The client only offers this for a
    /// stack it currently has selected, but FindStack re-scopes the request to the SENDER's own
    /// seat regardless of what the client claims, so it can never shrink another player's stack.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestRemovePieceServerRpc(byte plateau, byte kind, ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || kind >= PlateauConst.KindCount)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        int idx = FindStack(plateau, seat, kind);
        if (idx < 0)
        {
            return;
        }

        RemovePieces(idx, 1);
    }

    /// <summary>
    /// The spawn menu's Gemheart/Chasmfiend "+" keys: one more of <paramref name="kind"/> on
    /// whichever plateau the sender has selected client-side (PlateauSelection.TryGetSelectedPlateau),
    /// rather than always the central plateau — these two kinds aren't owned by a seat, so
    /// AddPieces is given PlateauConst.NeutralSeat instead of the sender's own seat.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestAddNeutralPieceServerRpc(byte plateau, byte kind, ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || !IsNeutralKind(kind))
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked || plateau >= board.PlateauCount)
        {
            return;
        }

        // Still must be a seated player to place one — not a free-for-all for anybody connected.
        if (SeatForClient(rpcParams.Receive.SenderClientId) < 0)
        {
            return;
        }

        AddPieces(plateau, PlateauConst.NeutralSeat, kind, 1);
    }

    /// <summary>The spawn menu's Gemheart/Chasmfiend "-" keys: the neutral twin of RequestRemovePieceServerRpc.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestRemoveNeutralPieceServerRpc(byte plateau, byte kind, ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || !IsNeutralKind(kind))
        {
            return;
        }

        if (SeatForClient(rpcParams.Receive.SenderClientId) < 0)
        {
            return;
        }

        int idx = FindStack(plateau, PlateauConst.NeutralSeat, kind);
        if (idx < 0)
        {
            return;
        }

        RemovePieces(idx, 1);
    }

    static bool IsNeutralKind(byte kind) =>
        kind == (byte)PieceKind.Gemheart || kind == (byte)PieceKind.Chasmfiend;

    /// <summary>The spawn menu's Score "+" key. Free and manual — see the class doc comment on gemheartScores.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestAddScoreServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        EnsureScoreCapacity(seat);
        gemheartScores[seat] = (byte)Mathf.Min(255, gemheartScores[seat] + 1);
    }

    /// <summary>The spawn menu's Score "-" key.</summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSubtractScoreServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        EnsureScoreCapacity(seat);
        gemheartScores[seat] = (byte)Mathf.Max(0, gemheartScores[seat] - 1);
    }

    void EnsureScoreCapacity(int seat)
    {
        while (gemheartScores.Count <= seat)
        {
            gemheartScores.Add(0);
        }
    }

    /// <summary>Safe on a client: 0 for a seat that has never scored yet.</summary>
    public int ScoreForSeat(int seat)
    {
        return (seat >= 0 && seat < gemheartScores.Count) ? gemheartScores[seat] : 0;
    }

    // ------------------------------------------------------------------ view for the rules

    /// <summary>
    /// Assemble the board slice one seat's movement search needs. <paramref name="excludeEdge"/>
    /// lifts one already-placed bridge out of the picture, for relocating it.
    ///
    /// Topology comes from the SERVER-PUBLISHED edge list, not from this client's own bake: the
    /// bake turns float geometry into integer indices through an argmin, and an Editor host and a
    /// Quest client breaking a near-tie differently would leave one of them highlighting
    /// destinations the server refuses forever, with no error anywhere. Geometry stays local,
    /// where a sub-millimetre disagreement is invisible.
    /// </summary>
    public bool TryBuildView(int seat, int excludeEdge, out PlateauMoveRules.View view)
    {
        view = default;

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked || board.CentralPlateau < 0)
        {
            return false;
        }

        SyncMirror(board);
        if (edgeMirror.Count == 0)
        {
            return false;
        }

        System.Array.Clear(ownFlags, 0, ownFlags.Length);
        System.Array.Clear(anyFlags, 0, anyFlags.Length);

        for (int i = 0; i < placedBridges.Count; i++)
        {
            PlacedBridge pb = placedBridges[i];
            if (pb.edge >= edgeMirror.Count || pb.edge == excludeEdge)
            {
                continue;
            }
            anyFlags[pb.edge] = true;
            if (pb.seat == seat)
            {
                ownFlags[pb.edge] = true;
            }
        }

        view = new PlateauMoveRules.View
        {
            edges = edgeMirror,
            incidence = incidence,
            ownBridge = ownFlags,
            anyBridge = anyFlags,
            plateauCount = board.PlateauCount,
            central = board.CentralPlateau,
        };
        return true;
    }

    void SyncMirror(PlateauBoard board)
    {
        if (!mirrorDirty && edgeMirror.Count == edges.Count && incidence.Length == board.PlateauCount)
        {
            return;
        }
        mirrorDirty = false;

        edgeMirror.Clear();
        for (int i = 0; i < edges.Count; i++)
        {
            edgeMirror.Add(edges[i]);
        }

        int n = board.PlateauCount;
        incidence = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            incidence[i] = new List<int>();
        }
        for (int e = 0; e < edgeMirror.Count; e++)
        {
            BridgeEdge edge = edgeMirror[e];
            if (edge.a < n)
            {
                incidence[edge.a].Add(e);
            }
            if (edge.b < n)
            {
                incidence[edge.b].Add(e);
            }
        }

        if (ownFlags.Length < edgeMirror.Count)
        {
            ownFlags = new bool[edgeMirror.Count];
            anyFlags = new bool[edgeMirror.Count];
        }

        if (!hashWarned && edges.Count > 0 && graphHash.Value != 0 && graphHash.Value != board.GraphHash)
        {
            hashWarned = true;
            Debug.LogError("PlateauGame: this client derived a different board graph from the " +
                           "server's (local " + board.GraphHash + " vs " + graphHash.Value +
                           "). The scene or the build differs. Highlights may not match what the " +
                           "server allows.");
        }
    }

    /// <summary>The bar a laid bridge should be drawn on, by edge index into the published list.</summary>
    public Transform SpotForEdge(int edge)
    {
        PlateauBoard board = PlateauBoard.Instance;
        return board != null ? board.SpotForEdge(edge) : null;
    }

    public bool TryGetEdgeEnds(int edge, out int a, out int b)
    {
        a = b = -1;
        if (edge < 0 || edge >= edges.Count)
        {
            return false;
        }
        BridgeEdge e = edges[edge];
        a = e.a;
        b = e.b;
        return true;
    }
}
