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

    /// <summary>How long after RequestSpinChooserServerRpc the Plateau Chooser's spin resolves.</summary>
    const float ChooserSpinSeconds = 2f;

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

    /// <summary>Bumped when a Plateau Chooser spin is requested; clients treat any change as
    /// "(re)start your local shuffle flourish". See RequestSpinChooserServerRpc.</summary>
    public NetworkVariable<int> chooserSpinEpoch = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Bumped when a Plateau Chooser spin RESOLVES (about ChooserSpinSeconds after chooserSpinEpoch).
    /// A second epoch rather than driving PlateauChooser off chooserMaterialIndex/
    /// chooserChasmfiendActive's own OnValueChanged directly: NetworkVariable&lt;T&gt;.Value's setter
    /// only marks the variable dirty -- and so only ever sends it, and only ever fires
    /// OnValueChanged on a client -- when the new value differs from the old one. With only three
    /// materials and a bool, a spin landing on the same answer as the previous one is common enough
    /// that clients would sometimes never be told the spin resolved. A monotonically increasing
    /// counter can't collide with its own previous value, so this always fires -- the same reason
    /// boardEpoch, not the lists it accompanies, is what UpdateSelected keys its staleness check off of.
    /// </summary>
    public NetworkVariable<int> chooserResultEpoch = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>The Plateau Chooser's current material: 0=15.mat (15%), 1=35.mat (35%), 2=50.mat
    /// (50%). Default 2 matches what "Plateau Chooser" is authored with in ChasmGame.unity.</summary>
    public NetworkVariable<byte> chooserMaterialIndex = new NetworkVariable<byte>(
        2, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Whether the Plateau Chooser's Chasmfiend child is active. Default false matches its
    /// authored state (m_IsActive: 0) in ChasmGame.unity.</summary>
    public NetworkVariable<bool> chooserChasmfiendActive = new NetworkVariable<bool>(
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

    // Plateau Chooser: server-only spin-timer state, not replicated (chooserSpinEpoch/
    // chooserResultEpoch are what clients actually observe).
    bool chooserSpinPending;
    float chooserResolveAt = -1f;

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

        // A spin in flight when this despawns (RoomAnchor despawns and respawns on a reconnect --
        // see the Instance guard below) must not leave chooserSpinPending stuck true forever. A
        // freshly Instantiated PlateauGame already gets false/-1 from the field initializers above;
        // this line only matters if Netcode ever re-spawns THIS SAME instance instead of a new one,
        // and costs nothing either way.
        chooserSpinPending = false;
        chooserResolveAt = -1f;

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

        // Deliberately NOT touched: the Plateau Chooser is not one of the 41 tracked plateaus and
        // has no gameplay consequence today, so a "Chasms" reset leaves whatever it last landed on
        // in place instead of snapping back to its authored 50.mat / inactive-Chasmfiend defaults.
        // To reset it too, add: chooserMaterialIndex.Value = 2; chooserChasmfiendActive.Value =
        // false; -- but do NOT bump chooserSpinEpoch here, or every client would replay the shuffle
        // flourish on every scene load.

        Debug.Log("PlateauGame: board reset.");
    }

    void Update()
    {
        // Independent of the quarter-second tick below, and not gated on boardLive -- see
        // RequestSpinChooserServerRpc's doc comment for why a resolve landing near a board reset is
        // harmless. Checked every frame so the ~2 second delay lands on the frame it's actually due,
        // not up to ServerTickSeconds late.
        if (IsServer && IsSpawned && chooserResolveAt >= 0f && Time.unscaledTime >= chooserResolveAt)
        {
            ResolveChooserSpin();
        }

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
    /// Move <paramref name="count"/> pieces of one kind from one plateau to another. Bridges are NOT
    /// moved through here — they target a gap, not a plateau, and have their own two RPCs below.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveServerRpc(byte from, byte kind, byte count, byte to, int epoch,
                                     ServerRpcParams rpcParams = default)
    {
        if (kind == (byte)PieceKind.Bridge)
        {
            return;
        }

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

        int n = Mathf.Clamp(count, 1, stacks[idx].count);
        RemovePieces(idx, n);
        AddPieces(to, seat, kind, n);
    }

    /// <summary>
    /// Lay a bridge from reserve across <paramref name="toEdge"/>. The network is seeded from the
    /// central plateau — plateauRules.md's "a plateau already connected by that player's bridges",
    /// with the centre as the seed because that is where a first bridge must come from.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestPlaceBridgeServerRpc(byte fromPlateau, byte toEdge, int epoch,
                                            ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || epoch != boardEpoch.Value)
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked || fromPlateau >= board.PlateauCount)
        {
            return;
        }

        int seat = SeatForClient(rpcParams.Receive.SenderClientId);
        if (seat < 0)
        {
            return;
        }

        int idx = FindStack(fromPlateau, seat, (int)PieceKind.Bridge);
        if (idx < 0)
        {
            return;                          // no bridge of the sender's in reserve there
        }

        if (!TryBuildView(seat, -1, out PlateauMoveRules.View view) ||
            !PlateauMoveRules.IsLegalBridgeEdge(view, toEdge))
        {
            return;                          // the client's highlight is a hint; this is the rule
        }

        RemovePieces(idx, 1);
        placedBridges.Add(new PlacedBridge(toEdge, seat));
    }

    /// <summary>
    /// Pick up a bridge that has already been laid and put it in another gap. Legality is computed
    /// with the network seeded from the two plateaus this bridge currently spans, so it can only be
    /// re-laid around the plateau system it is actually touching. See bridgeMovementUpdate.md.
    ///
    /// toEdge == fromEdge needs no explicit guard: the bridge is still in anyBridge, so
    /// IsCandidateEdge refuses its own gap, and the SamePair check refuses its twin bar.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveBridgeServerRpc(byte fromEdge, byte toEdge, int epoch,
                                           ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || epoch != boardEpoch.Value)
        {
            return;
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

        int existing = FindPlacedBridge(fromEdge);
        if (existing < 0 || placedBridges[existing].seat != seat)
        {
            return;                          // not the sender's bridge to move
        }

        if (!TryBuildView(seat, fromEdge, out PlateauMoveRules.View view) ||
            !PlateauMoveRules.IsLegalBridgeEdge(view, toEdge))
        {
            return;
        }

        placedBridges[existing] = new PlacedBridge(toEdge, seat);
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

    /// <summary>
    /// The Plateau Chooser: a novelty prop at ChasmGame's scene root (see PlateauChooser.cs), not
    /// one of the 41 tracked plateaus -- it never touches stacks/edges/placedBridges. Seated-only
    /// anyway, purely to keep every ServerRpc in this file requiring the same thing; drop the
    /// SeatForClient check below if a spectator should be able to trigger it too.
    ///
    /// chooserSpinPending blocks a second click from restarting or stacking the delay -- the spin
    /// already in flight just keeps running. ResolveChooserSpin, not this method, picks the
    /// outcome, ChooserSpinSeconds later (see Update()).
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestSpinChooserServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!boardLive.Value || chooserSpinPending)
        {
            return;
        }

        if (SeatForClient(rpcParams.Receive.SenderClientId) < 0)
        {
            return;
        }

        chooserSpinPending = true;
        chooserResolveAt = Time.unscaledTime + ChooserSpinSeconds;
        chooserSpinEpoch.Value = chooserSpinEpoch.Value + 1;
    }

    /// <summary>
    /// Fires ChooserSpinSeconds after RequestSpinChooserServerRpc (see Update()). Picks the Plateau
    /// Chooser's final material -- 15.mat 15% of the time, 35.mat 35%, 50.mat 50%, the same tiers
    /// plateauRules.md ties to plateau size -- and, independently, whether its Chasmfiend child
    /// wakes up (30%).
    /// </summary>
    void ResolveChooserSpin()
    {
        chooserResolveAt = -1f;
        chooserSpinPending = false;

        chooserMaterialIndex.Value = RollChooserMaterialIndex();
        chooserChasmfiendActive.Value = UnityEngine.Random.value < 0.30f;
        chooserResultEpoch.Value = chooserResultEpoch.Value + 1;
    }

    /// <summary>0 (15%) / 1 (35%) / 2 (50%). The project's first use of UnityEngine.Random.</summary>
    static byte RollChooserMaterialIndex()
    {
        float r = UnityEngine.Random.value;
        if (r < 0.15f)
        {
            return 0;
        }
        return r < 0.50f ? (byte)1 : (byte)2;
    }

    // ------------------------------------------------------------------ view for the rules

    /// <summary>
    /// Assemble the board slice one seat's movement search needs. <paramref name="movingEdge"/> is
    /// the edge a bridge is being lifted FROM, or -1 for every other move.
    ///
    /// It does NOT remove that bridge from the graph — the board is read as it stands, so the gap
    /// the bridge is in stays flagged and is never offered back. All it does is move the seed of
    /// PlateauMoveRules.ConnectedComponent onto that bridge's own two ends. See
    /// bridgeMovementUpdate.md.
    ///
    /// Topology comes from the SERVER-PUBLISHED edge list, not from this client's own bake: the
    /// bake turns float geometry into integer indices through an argmin, and an Editor host and a
    /// Quest client breaking a near-tie differently would leave one of them highlighting
    /// destinations the server refuses forever, with no error anywhere. Geometry stays local,
    /// where a sub-millimetre disagreement is invisible.
    /// </summary>
    public bool TryBuildView(int seat, int movingEdge, out PlateauMoveRules.View view)
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
            if (pb.edge >= edgeMirror.Count)
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
            movingEdge = movingEdge,          // never omit: View is a struct and its default is 0
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
