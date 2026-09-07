using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Every replicated fact about a game of Stairs, and the only thing allowed to change one.
///
/// Sits on the in-scene object `World Root > Stairs Root`, which also carries the NetworkObject and
/// StairsBoard. In-scene rather than on Room Anchor.prefab (where Chasms put its state): Stairs owns
/// nothing that has to outlive its own scene, and putting a third game's state on the persistent
/// anchor would make every game pay for it. The consequence is that **switching away to another game
/// and back starts a new game** — the object is destroyed with the scene. That is the intended
/// behaviour and the reason "New Game" is a separate key rather than something re-pressing `Stairs`
/// does: a player who steps out of the room and comes back should not wipe the board.
///
/// There is no NetworkObject per piece. Towers, pawns, supply stacks and captured piles are local
/// visuals StairsView rebuilds from the state here — the same choice Chasms made and for the same
/// reason: adding prefabs to DefaultNetworkPrefabs.asset changes the prefab *set*, and with
/// ForceSamePrefabs a difference in that set is a join that hangs on "Joining room..." with no
/// reason string. 160 steps would also be 160 spawn messages.
///
/// Everything here is server-written. Both ServerRpcs that place a piece re-run the same
/// StairsMoveRules call the client used to draw its highlight: the client's highlight is a hint,
/// this is the rule.
/// </summary>
[DisallowMultipleComponent]
public class StairsGame : NetworkBehaviour, IGameSession
{
    public static StairsGame Instance { get; private set; }

    /// <summary>How often the server looks for a player to seat. 4 Hz, matching SpawnManager and
    /// PlateauGame, and polled rather than hooked on OnClientConnectedCallback for the same reason
    /// they are: spawnSlot is assigned inside PlayerControls.OnNetworkSpawn, which can run later.</summary>
    const float TickSeconds = 0.25f;

    /// <summary>What is stacked on each of the 64 cells, indexed the way StairsBoard indexes them.</summary>
    public NetworkList<StairsTower> towers;

    /// <summary>The two playing seats. Indexed by seat, NOT by ring slot — see StairsSeat.</summary>
    public NetworkList<StairsSeat> seats;

    /// <summary>The winning seat once the game is over. StairsConst.NoSeat means "no winner yet"
    /// before then and **a draw** after it — <see cref="IsGameOver"/> is what disambiguates, since
    /// there is no game-wide phase left to ask. See bugFixesStairsGame.md §1.</summary>
    public NetworkVariable<int> winner = new NetworkVariable<int>(
        StairsConst.NoSeat, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Anything above changed. StairsView reconciles the board on it and StairsSelection recomputes
    /// its highlights; both treat it as a dirty flag rather than a diff, because a NetworkList's
    /// initial contents arrive in the spawn payload and raise no OnListChanged at all — which is
    /// also the only reason a late joiner's board appears.
    /// </summary>
    public event Action BoardChanged;

    // Server-side scratch, reused. One allocation for the life of the session rather than a pair of
    // 64-entry arrays per validated RPC.
    StairsMoveRules.View serverView;

    float tickTimer;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        Instance = this;

        // Before OnNetworkSpawn, and before anything can write them.
        towers = new NetworkList<StairsTower>();
        seats = new NetworkList<StairsSeat>();

        // In Awake rather than OnNetworkSpawn, like BashRoot: the menu action has to resolve even in
        // the frames between the scene loading and Netcode spawning this object.
        GameSessionRegistry.Register(this);
    }

    /// <summary>
    /// Override, and call base. NetworkBehaviour declares OnDestroy virtual and does its own
    /// teardown in it; hiding it with a plain `void OnDestroy()` compiles with a warning and then
    /// leaks the behaviour's registration with its NetworkObject.
    /// </summary>
    public override void OnDestroy()
    {
        GameSessionRegistry.Unregister(this);

        // Guarded like every other Instance singleton here: a scene switch can construct the next
        // one before destroying this one.
        if (Instance == this)
        {
            Instance = null;
        }

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            ResetGame();
        }

        towers.OnListChanged += HandleTowersChanged;
        seats.OnListChanged += HandleSeatsChanged;
        winner.OnValueChanged += HandleIntChanged;

        // The values a client arrives already holding raise no change event, so say so by hand.
        RaiseBoardChanged();
    }

    public override void OnNetworkDespawn()
    {
        towers.OnListChanged -= HandleTowersChanged;
        seats.OnListChanged -= HandleSeatsChanged;
        winner.OnValueChanged -= HandleIntChanged;
    }

    void HandleTowersChanged(NetworkListEvent<StairsTower> e) => RaiseBoardChanged();
    void HandleSeatsChanged(NetworkListEvent<StairsSeat> e) => RaiseBoardChanged();
    void HandleIntChanged(int previous, int current) => RaiseBoardChanged();

    void RaiseBoardChanged()
    {
        BoardChanged?.Invoke();
    }

    void Update()
    {
        if (!IsServer || !IsSpawned)
        {
            return;
        }

        tickTimer -= Time.deltaTime;
        if (tickTimer > 0f)
        {
            return;
        }
        tickTimer = TickSeconds;

        ServeSeats();
    }

    // ------------------------------------------------------------------ seating

    /// <summary>
    /// Hand the two seats to the first two players who turn up, and never take one back.
    ///
    /// A seat is keyed to a ring slot rather than to a client id, because PlayerRing.PickFreeSlot
    /// gives a reconnecting player the slot they vacated — so a player who drops out and comes back
    /// walks into their own half-finished game. The cost is the deliberate one: while a seat's
    /// player is away, a third person in the room spectates rather than taking it.
    /// </summary>
    void ServeSeats()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || seats.Count < StairsConst.Seats)
        {
            return;
        }

        foreach (var pair in nm.ConnectedClients)
        {
            NetworkClient client = pair.Value;
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
            if (slot < 0 || SeatForRingSlot(slot) >= 0)
            {
                continue;                       // not placed on the ring yet, or already seated
            }

            int seat = FreeSeatFor(slot);
            if (seat < 0)
            {
                continue;                       // both seats are spoken for: this player spectates
            }

            StairsSeat state = seats[seat];
            state.ringSlot = slot;
            seats[seat] = state;
        }
    }

    /// <summary>
    /// Which side of the board a ring slot is standing on. Seat 0's console is on the board's -Z
    /// edge and seat 1's on +Z, so a player is seated to match wherever possible — otherwise their
    /// supply and End Turn key would be across the table from them.
    ///
    /// PlayerRing hands the first two players slots 0 and 6, which are exactly -Z and +Z, so in the
    /// ordinary case this is already right; it earns its keep for a ring fragmented by mid-game
    /// departures. Slots 3 and 9 sit exactly on the axis and fall through to the lowest free seat.
    /// </summary>
    static int PreferredSeat(int ringSlot)
    {
        float z = PlayerRing.SlotPosition(ringSlot).z;
        return z > 0.001f ? 1 : 0;
    }

    int FreeSeatFor(int ringSlot)
    {
        int preferred = PreferredSeat(ringSlot);
        if (!seats[preferred].IsOccupied)
        {
            return preferred;
        }

        int other = StairsConst.Opponent(preferred);
        return seats[other].IsOccupied ? StairsConst.NoSeat : other;
    }

    // ------------------------------------------------------------------ reading the state

    /// <summary>This seat's own turn state. There is no game-wide phase any more — see
    /// bugFixesStairsGame.md §1. Safe before the first sync: an unsynced seat reads Setup.</summary>
    public StairsPhase PhaseOf(int seat) => SeatState(seat).Phase;

    /// <summary>Tiles this seat still owes in its own Build phase.</summary>
    public int StepsToPlaceOf(int seat) => SeatState(seat).stepsToPlace;

    /// <summary>This seat's momentum for its current turn, for StairsMoveRules.</summary>
    public int MoveDirectionOf(int seat) => SeatState(seat).moveDirection;

    /// <summary>Somebody won or the supply ran out. EndGame writes GameOver into both seats, so this
    /// is one fact rather than two that can disagree.</summary>
    public bool IsGameOver => SeatState(0).Phase == StairsPhase.GameOver;

    /// <summary>Safe before the first sync, when the lists are still empty.</summary>
    public StairsSeat SeatState(int seat)
    {
        return StairsConst.IsSeat(seat) && seats.Count > seat ? seats[seat] : StairsSeat.Fresh();
    }

    public StairsTower TowerAt(int cell)
    {
        return StairsConst.IsCell(cell) && towers.Count == StairsConst.CellCount
            ? towers[cell]
            : StairsTower.Empty;
    }

    public int SeatForRingSlot(int ringSlot)
    {
        if (ringSlot < 0)
        {
            return StairsConst.NoSeat;
        }
        for (int s = 0; s < StairsConst.Seats && s < seats.Count; s++)
        {
            if (seats[s].ringSlot == ringSlot)
            {
                return s;
            }
        }
        return StairsConst.NoSeat;
    }

    /// <summary>This client's seat, or StairsConst.NoSeat when it is spectating. Safe on a client.</summary>
    public static int LocalSeat()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (Instance == null || nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return StairsConst.NoSeat;
        }

        PlayerControls pc = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        return pc != null ? Instance.SeatForRingSlot(pc.spawnSlot.Value) : StairsConst.NoSeat;
    }

    /// <summary>
    /// A flat snapshot of the board for StairsMoveRules. The caller owns the arrays and they are
    /// reused, so this allocates nothing after the first call.
    /// </summary>
    public void FillView(ref StairsMoveRules.View view)
    {
        view.EnsureCapacity();

        bool ready = towers.Count == StairsConst.CellCount;
        for (int i = 0; i < StairsConst.CellCount; i++)
        {
            StairsTower t = ready ? towers[i] : StairsTower.Empty;
            view.height[i] = t.height;
            view.owner[i] = t.Owner;
        }

        for (int s = 0; s < StairsConst.Seats; s++)
        {
            view.pawnCell[s] = SeatState(s).pawnCell;
        }
    }

    // Client-side scratch for the one question StairsSelection has to ask outside its per-frame
    // refresh. Server validation uses serverView and never this.
    StairsMoveRules.View askView;

    /// <summary>Whether this seat may pick the top tile off this cell right now. Same rule the server
    /// re-runs in RequestMoveStepServerRpc.</summary>
    public bool CanLiftStep(int seat, int cell)
    {
        if (IsGameOver)
        {
            return false;
        }
        FillView(ref askView);
        return StairsMoveRules.IsLegalStepLift(askView, seat, cell);
    }

    /// <summary>
    /// stepsRules.md has Player 1 place first and Player 2 second. That order is no longer enforced at
    /// all: either player may put their pawn down whenever they like, and doing so starts that player's
    /// own first Move without waiting for the other pawn. bugFixesStairsGame.md §1.
    /// </summary>
    public bool CanPlacePawn(int seat)
    {
        if (!StairsConst.IsSeat(seat))
        {
            return false;
        }

        StairsSeat state = SeatState(seat);
        return state.IsOccupied && !state.HasPawnOnBoard && state.Phase == StairsPhase.Setup;
    }

    // ------------------------------------------------------------------ the requests

    [ServerRpc(RequireOwnership = false)]
    public void RequestPlacePawnServerRpc(int cell, ServerRpcParams p = default)
    {
        int seat = SeatForSender(p);
        if (!StairsConst.IsSeat(seat) || !CanPlacePawn(seat))
        {
            return;
        }

        FillView(ref serverView);
        if (!StairsMoveRules.IsLegalPawnPlacement(serverView, cell))
        {
            return;
        }

        StairsSeat state = seats[seat];
        state.pawnCell = cell;
        seats[seat] = state;

        // This seat starts its own first Move at once. The other seat is not touched and may still be
        // in Setup, mid-Move or mid-Build; the two never wait on each other.
        BeginTurn(seat);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveServerRpc(int cell, ServerRpcParams p = default)
    {
        int seat = SeatForSender(p);
        if (!StairsConst.IsSeat(seat))
        {
            return;
        }

        StairsSeat state = seats[seat];
        if (state.Phase != StairsPhase.Move)
        {
            return;
        }

        FillView(ref serverView);
        if (!StairsMoveRules.IsLegalMove(serverView, seat, cell, state.moveDirection, out bool capture))
        {
            return;
        }

        int from = state.pawnCell;
        int direction = StairsMoveRules.DirectionOf(serverView, from, cell);

        state.pawnCell = cell;
        state.stepsMoved = (byte)Mathf.Min(byte.MaxValue, state.stepsMoved + 1);

        if (capture)
        {
            int taken = towers[cell].height;
            SetTower(cell, StairsConst.NoSeat, 0);
            state.captured = (byte)Mathf.Min(byte.MaxValue, state.captured + taken);
            seats[seat] = state;

            // "Your turn ends immediately after completing a capture", and a capturing turn does not
            // build. Under §1 that means this player's OWN next Move, not the opponent's.
            if (!CheckForWinner())
            {
                BeginTurn(seat);
            }
            return;
        }

        if (state.moveDirection == StairsMoveRules.DirectionFree)
        {
            state.moveDirection = (sbyte)direction;
        }

        seats[seat] = state;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestEndMoveServerRpc(ServerRpcParams p = default)
    {
        int seat = SeatForSender(p);
        if (!StairsConst.IsSeat(seat) || PhaseOf(seat) != StairsPhase.Move)
        {
            return;
        }

        BeginBuild(seat);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestPlaceStepServerRpc(int cell, ServerRpcParams p = default)
    {
        int seat = SeatForSender(p);
        if (!StairsConst.IsSeat(seat))
        {
            return;
        }

        StairsSeat state = seats[seat];
        if (state.Phase != StairsPhase.Build || state.stepsToPlace == 0)
        {
            return;
        }

        FillView(ref serverView);
        if (!StairsMoveRules.IsLegalBuild(serverView, seat, cell))
        {
            return;
        }

        SetTower(cell, seat, towers[cell].height + 1);

        state.supply = (byte)Mathf.Max(0, state.supply - 1);
        state.stepsToPlace = (byte)(state.stepsToPlace - 1);
        seats[seat] = state;

        if (state.supply == 0)
        {
            EndOnSupplyExhausted();
            return;
        }

        if (state.stepsToPlace == 0)
        {
            BeginTurn(seat);
            return;
        }

        // Still owed tiles, but possibly nowhere left to put them.
        FillView(ref serverView);
        if (!StairsMoveRules.AnyLegalBuild(serverView, seat))
        {
            BeginTurn(seat);
        }
    }

    /// <summary>
    /// Move a tile this player has already placed. Deliberately not tied to a phase: a misplacement is
    /// worth fixing whenever it is noticed, and it costs nothing — no supply is spent or returned and
    /// no tile is owed, because the tile was already on the board. bugFixesStairsGame.md §3.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestMoveStepServerRpc(int fromCell, int toCell, ServerRpcParams p = default)
    {
        int seat = SeatForSender(p);
        if (!StairsConst.IsSeat(seat) || IsGameOver || fromCell == toCell)
        {
            return;
        }

        FillView(ref serverView);
        if (!StairsMoveRules.IsLegalStepLift(serverView, seat, fromCell) ||
            !StairsMoveRules.IsLegalBuild(serverView, seat, toCell))
        {
            return;
        }

        SetTower(fromCell, seat, towers[fromCell].height - 1);
        SetTower(toCell, seat, towers[toCell].height + 1);
    }

    /// <summary>
    /// Start over. Reachable from the room menu as "New Game" — deliberately a separate key rather
    /// than something re-pressing `Stairs` does, so a player rejoining a game in progress cannot
    /// wipe it. Any player may press it, the same as BASH's Reset Game.
    /// </summary>
    [ServerRpc(RequireOwnership = false)]
    public void RequestResetServerRpc(ServerRpcParams p = default)
    {
        ResetGame();
    }

    // ------------------------------------------------------------------ server-side turn machinery

    void SetTower(int cell, int owner, int height)
    {
        // The one place a tower is written, which is what makes "a tower has exactly one owner"
        // hold: a capture clears it outright and a build can only ever add to your own.
        towers[cell] = new StairsTower(owner, height);
    }

    /// <summary>Start this seat's own next turn. Nothing about the other seat changes.</summary>
    void BeginTurn(int seat)
    {
        StairsSeat state = seats[seat];
        state.phase = (byte)StairsPhase.Move;
        state.moveDirection = (sbyte)StairsMoveRules.DirectionFree;
        state.stepsMoved = 0;
        state.stepsToPlace = 0;
        seats[seat] = state;
    }

    void BeginBuild(int seat)
    {
        StairsSeat state = seats[seat];

        if (state.supply == 0)
        {
            EndOnSupplyExhausted();
            return;
        }

        FillView(ref serverView);
        if (!StairsMoveRules.AnyLegalBuild(serverView, seat))
        {
            BeginTurn(seat);
            return;
        }

        // "The number of tiles you place is equal to the number of spaces you moved", and the Minimum
        // Rule: at least one even after a turn that moved nowhere.
        int owed = Mathf.Max(1, state.stepsMoved);
        state.stepsToPlace = (byte)Mathf.Min(owed, state.supply);
        state.phase = (byte)StairsPhase.Build;
        seats[seat] = state;
    }

    /// <summary>
    /// Stop the game and record who won. GameOver goes into BOTH seats rather than into a flag beside
    /// them: a seat's phase is the only turn state there is now, and two things that have to agree are
    /// two things that can disagree.
    /// </summary>
    void EndGame(int winnerSeat)
    {
        winner.Value = winnerSeat;

        for (int s = 0; s < StairsConst.Seats; s++)
        {
            StairsSeat state = seats[s];
            state.phase = (byte)StairsPhase.GameOver;
            seats[s] = state;
        }
    }

    bool CheckForWinner()
    {
        for (int s = 0; s < StairsConst.Seats; s++)
        {
            if (SeatState(s).captured >= StairsConst.CapturesToWin)
            {
                EndGame(s);
                return true;
            }
        }
        return false;
    }

    void EndOnSupplyExhausted()
    {
        int a = SeatState(0).captured;
        int b = SeatState(1).captured;
        EndGame(a > b ? 0 : b > a ? 1 : StairsConst.NoSeat);
    }

    void ResetGame()
    {
        // A reset is a new game, not a new room: whoever is sitting down stays sitting down.
        int[] keep = new int[StairsConst.Seats];
        for (int s = 0; s < StairsConst.Seats; s++)
        {
            keep[s] = seats.Count > s ? seats[s].ringSlot : StairsConst.NoSeat;
        }

        // Clear-and-refill is the one thing CLAUDE.md says not to do to these lists, and a
        // deliberate reset is the exception it names — every other write below is a single element.
        towers.Clear();
        for (int i = 0; i < StairsConst.CellCount; i++)
        {
            towers.Add(StairsTower.Empty);
        }

        seats.Clear();
        for (int s = 0; s < StairsConst.Seats; s++)
        {
            StairsSeat fresh = StairsSeat.Fresh();
            fresh.ringSlot = keep[s];
            seats.Add(fresh);
        }

        winner.Value = StairsConst.NoSeat;
    }

    /// <summary>
    /// The sender's seat, or NoSeat for a spectator. Every ServerRpc above starts here rather than
    /// trusting a seat index in the message: a client that has been modified must not be able to
    /// move the other player's pawn.
    /// </summary>
    int SeatForSender(ServerRpcParams p)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(p.Receive.SenderClientId, out NetworkClient client))
        {
            return StairsConst.NoSeat;
        }

        if (client == null || client.PlayerObject == null)
        {
            return StairsConst.NoSeat;
        }

        PlayerControls pc = client.PlayerObject.GetComponent<PlayerControls>();
        return pc != null ? SeatForRingSlot(pc.spawnSlot.Value) : StairsConst.NoSeat;
    }

    // ------------------------------------------------------------------ IGameSession

    public string GameKey => StairsConst.GameKey;

    /// <summary>
    /// Nothing. Picking `Stairs` from the menu does not reset the board — that is what the New Game
    /// key is for, and it is deliberately separate so a player rejoining a game in progress does not
    /// wipe it. Chasms does the opposite because its state rides the persistent Room Anchor and has
    /// no other moment to be cleared at.
    /// </summary>
    public void OnGameSelected()
    {
    }

    public void InvokeMenuAction(string keyName)
    {
        if (keyName == StairsConst.NewGameKey)
        {
            RequestResetServerRpc();
            return;
        }

        Debug.Log("StairsGame: menu action '" + keyName + "' is declared on StairsModule.asset but " +
                  "not handled here.");
    }

    // PlayerControls reads this every frame for every remote avatar, so the answer is a cached
    // string rather than a fresh int.ToString() per player per frame.
    static readonly string[] s_badges = BuildBadges();

    static string[] BuildBadges()
    {
        string[] badges = new string[StairsConst.StepsPerPlayer + 1];
        for (int i = 0; i < badges.Length; i++)
        {
            badges[i] = i.ToString();
        }
        return badges;
    }

    /// <summary>
    /// The captured count under a seated player's nametag, or null for a spectator — and null hides
    /// the label, which is the normal case for everybody who is not playing.
    ///
    /// The argument is a PlayerControls.spawnSlot (a place on the ring), not a Stairs seat.
    /// </summary>
    public string AvatarBadgeForSeat(int seat)
    {
        int mine = SeatForRingSlot(seat);
        if (!StairsConst.IsSeat(mine))
        {
            return null;
        }

        int captured = SeatState(mine).captured;
        return captured < s_badges.Length ? s_badges[captured] : captured.ToString();
    }
}
