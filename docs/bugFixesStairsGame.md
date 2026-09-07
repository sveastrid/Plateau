# bugFixesStairsGame — six changes to the Stairs game

Working document for the six items reported after the first sessions on `StairsGame`, in the style
of the rest of `docs/`: the plan with the evidence attached, so a claim can be checked rather than
taken on trust. Line references are against the working tree at `b0e31dd` plus the one uncommitted
change to `Assets/Scripts/Stairs/StairsView.cs`.

> **Status: MOSTLY APPLIED**, as of the commit that carries this file. Read this block before
> trusting any "Verified" in the table — three things below did not land, and one of them is still a
> visible defect.
>
> | § | State |
> | --- | --- |
> | 1 | **Applied in full.** The turn state is on `StairsSeat`; the five `NetworkVariable`s and `IsSeatToAct` are gone. |
> | 2 | **Applied to the End Turn key and both step prefabs. NOT applied to the tower billboard** — `StairsView.cs:711` still reads `LookRotation(up, away.normalized)` where §2.3 and §5.4 both say `-up`, so **tower height numbers currently render mirrored.** One line. |
> | 3 | **Applied**, except §3.3: `SetLiftedCell`/`liftedCell`/`ApplyLifted` were never written, `HighlightTarget` does not skip a hidden tile, and `MoveGhost` has no source-square `y` offset. A tile being re-laid is therefore drawn both in hand and on its tower. `BeginDrag` also sets `dragFromCell` for *every* role rather than only `TowerStep`, which is harmless — nothing reads it for the other two. |
> | 4 | **Closed without code.** Cause B (the game never left `Setup` with one seat occupied) is removed by §1; the rule questioned in §4.5 stands, per §9. The §4.4 diagnostic was not left in the tree. |
> | 5 | **Applied in full.** The `Labels` root, `towerLabels`, `labelTemplate`, `labelColors`, `labelScale` and `UpdateTowerLabel` are gone; `towerTopLabel` replaces them. |
> | 6 | **NOT APPLIED.** No `SupplyStepPosition`, no `OwedLiftInSteps`; `ReconcileSupply` still uses the inline arithmetic and nothing lifts the tiles a player owes. |
>
> Two other departures from the plan, both deliberate-looking and both harmless: `StairsSelection.Update`
> calls `ClearHighlights()` before `Bind()` and then calls `Bind()` a second time, and `CanDrag` now
> refuses outright while a pawn is selected so the click and drag idioms cannot fight over one press.

| § | Asked for | What actually has to change | Confidence |
| --- | --- | --- | --- |
| [1](#1--either-player-may-act-at-any-time) | Don't wait for the other player's turn | The four turn variables move off `StairsGame` and **onto the seat**. Each seat runs its own `Setup → Move → Build → Move` cycle | **Verified** — the gate is `IsSeatToAct`, `StairsGame.cs:323-340` |
| [2](#2--the-end-turn-label-is-mirrored) | End Turn text rotated +90 not −90 | One number, `StairsView.cs:289`. The same sign is wrong in two other places | **Verified** |
| [3](#3--re-laying-a-tile-that-is-already-on-the-board) | Move a placed step at any time | A new rule in `StairsMoveRules`, one new `ServerRpc`, and a third `dragRole` in `StairsSelection` | **Verified** — nothing today can drag a `TowerStep` (`StairsSelection.cs:284-295`) |
| [4](#4--the-pawn-would-not-move) | Pawn wouldn't move onto an adjacent step | Five candidate causes. Two are bugs that §1 removes, three are the rules working as written | **Hypothesis** — §4.4 identifies which in one line of log |
| [5](#5--the-number-on-a-tile-comes-from-the-tile) | Use the step prefab's own text | Delete the cloned-label machinery; activate the authored `Height` child and set its number. **Both prefabs also need §2's rotation fix** | **Verified** |
| [6](#6--lift-the-tiles-a-player-still-owes) | Lift/highlight the tiles owed | One position pass at the end of `ReconcileSupply` | **Verified** |

§2, §5 and §6 are cheap and independent. §1 is the big one and §3 and §4 both lean on it, so the
order that costs least is **§2 → §5 → §6 → §1 → §3**, with §4 measured *before* §1 and re-checked
after. See [§7](#7--order-of-work-and-how-to-verify-each-one).

---

## 0 — Things to know before touching any of it

**None of this adds a `NetworkObject`.** Stairs draws everything as local visuals reconciled from
`StairsGame`'s lists, so `Assets/DefaultNetworkPrefabs.asset` stays at **four** entries and the join
handshake's config hash does not move. Keep it that way — see
[the root CLAUDE.md on `ForceSamePrefabs`](../CLAUDE.md#conventions-that-break-silently).

**The wire format does change,** twice: §1 adds four fields to `StairsSeat` and deletes five
`NetworkVariable`s from `StairsGame`, and §3 adds a `ServerRpc`. `NetworkVariable` ordering inside a
behaviour is positional and RPC ids are hashed from the method signature, so **both headsets must be
flashed from the same build** for as long as this work is in progress. That is already the standing
rule; it is now load-bearing again.

**No new load-bearing names.** The only name resolved by string here is `Height`, the TMP child on
`Step1.prefab`/`Step2.prefab`, and it already exists (`StairsConst.StepLabelName`, `StairsTypes.cs:66`).

**Where the pieces actually live**, because two of them are not where `Assets/Scripts/Stairs/CLAUDE.md`
implies:

| Object | Scene path | Carries |
| --- | --- | --- |
| `Stairs Root` | `World Root > Stairs Root` (`StairsGame.unity:3717`) | `NetworkObject`, `StairsGame`, `StairsBoard`, `StairsView` |
| `Stairs Controller` | **a scene root**, not under `World Root` (`StairsGame.unity:2428-2440`) | `StairsSelection` only |

**The 64 cells do have their own `BoxCollider`s** (e.g. `Cube (5)`, `StairsGame.unity:3886`). The
`Board` slab is still needed — a ray that lands in a 1 cm gap between cells hits the slab — but the
claim in `Assets/Scripts/Stairs/CLAUDE.md` that the cells carry "no collider of their own" is stale.
Worth fixing while you are in there.

**Compile check without opening the Editor:** the recipe is in the root
[`CLAUDE.md`](../CLAUDE.md#build-and-test) — one `.csproj` per assembly, Unity's **.NET** Roslyn, and
always an explicit `-out:` pointing at the scratchpad. `MRBoardGame.Stairs` also needs `-r:` on the
`MRBoardGame.Shared` DLL. It will not catch Netcode's ILPP problems, so anything touching an RPC or a
`NetworkVariable` still has to be seen by the Editor once.

**These are dead today and stay dead** — do not spend time on them: `StairsGame.IsPlaying`
(`:253`), `StairsView.PawnMaterial`, `PawnPrefab`, `PawnObject`, `SupplyTop`, `StairsBoard.CellTransform`,
`StairsBoard.Pitch`. §3 is the first caller `StairsView.StepThickness` has ever had.

---

## 1 — Either player may act at any time

> *"I don't want to have to wait on player 1 to end their turn before player 2 gets to go. If a
> player wants to go out of turn they should be able to."*

### 1.1 What stops it today

One turn, one phase, one seat, all of it global on `StairsGame`:

```csharp
// StairsGame.cs:42-60
public NetworkVariable<int> phase          = ... (int)StairsPhase.Setup ...
public NetworkVariable<int> currentSeat    = ... 0 ...
public NetworkVariable<int> moveDirection  = ... StairsMoveRules.DirectionFree ...
public NetworkVariable<int> stepsMoved     = ... 0 ...
public NetworkVariable<int> stepsToPlace   = ... 0 ...
```

and one gate every client check and every `ServerRpc` runs through:

```csharp
// StairsGame.cs:323-340
public bool IsSeatToAct(int seat)
{
    ...
    case StairsPhase.Move:
    case StairsPhase.Build:
        return currentSeat.Value == seat;      // <- this is the whole of "wait your turn"
}
```

There is a second, worse consequence of the same shape, and it is almost certainly what
[§4](#4--the-pawn-would-not-move) ran into: **with only one seat occupied the game never leaves
`Setup`.** `RequestPlacePawnServerRpc` only calls `BeginTurn(0)` when *both* seats are occupied and
*both* pawns are down (`StairsGame.cs:391-395`). One player alone places their pawn, `CanPlacePawn`
then answers false because the pawn is on the board, `IsSeatToAct` answers false, and from that
moment the pawn cannot be selected, the End Turn key is never shown and no supply tile can be
dragged. The board is live and nothing on it responds.

### 1.2 The shape of the fix

**The turn state stops being a property of the game and becomes a property of the seat.** After
this there is no such thing as "whose turn it is": each seat walks its own
`Setup → Move → Build → Move → …` cycle at its own pace and neither ever waits on the other.

Everything else — the movement rules, the momentum rule, tiles-owed-equals-spaces-moved, captures,
both win conditions — is untouched and still enforced per seat by the same `StairsMoveRules` calls.

`StairsPhase.GameOver` is written into **both** seats when the game ends, so there is no separate
"the game is over" flag that could fall out of step with the seats. That is why no replacement for
the global `phase` variable appears below.

### 1.3 `StairsTypes.cs` — the struct grows four fields

```csharp
public struct StairsSeat : INetworkSerializable, IEquatable<StairsSeat>
{
    public int ringSlot;
    public int pawnCell;
    public byte supply;
    public byte captured;

    /// <summary>This seat's own turn, as a StairsPhase. Each seat runs its own Setup -> Move ->
    /// Build cycle and neither player ever waits on the other; see bugFixesStairsGame.md §1.
    /// GameOver is written into BOTH seats at once, so it doubles as the game's end state.</summary>
    public byte phase;

    /// <summary>stepsRules.md "Momentum Rule", per seat. DirectionFree until this seat takes the
    /// first step of its own turn, then locked to whichever way that step went.</summary>
    public sbyte moveDirection;

    /// <summary>Spaces this seat has moved in its current turn. Becomes the tiles it owes.</summary>
    public byte stepsMoved;

    /// <summary>Tiles this seat still owes in its own Build. Reaching 0 starts its next Move.</summary>
    public byte stepsToPlace;

    public StairsPhase Phase => (StairsPhase)phase;

    public static StairsSeat Fresh()
    {
        StairsSeat s = new StairsSeat();
        s.ringSlot = StairsConst.NoSeat;
        s.pawnCell = StairsConst.NoCell;
        s.supply = StairsConst.StepsPerPlayer;
        s.captured = 0;
        s.phase = (byte)StairsPhase.Setup;
        s.moveDirection = (sbyte)StairsMoveRules.DirectionFree;
        s.stepsMoved = 0;
        s.stepsToPlace = 0;
        return s;
    }

    public bool IsOccupied => ringSlot >= 0;
    public bool HasPawnOnBoard => pawnCell >= 0;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ringSlot);
        serializer.SerializeValue(ref pawnCell);
        serializer.SerializeValue(ref supply);
        serializer.SerializeValue(ref captured);
        serializer.SerializeValue(ref phase);
        serializer.SerializeValue(ref moveDirection);
        serializer.SerializeValue(ref stepsMoved);
        serializer.SerializeValue(ref stepsToPlace);
    }

    public bool Equals(StairsSeat other) =>
        ringSlot == other.ringSlot && pawnCell == other.pawnCell &&
        supply == other.supply && captured == other.captured &&
        phase == other.phase && moveDirection == other.moveDirection &&
        stepsMoved == other.stepsMoved && stepsToPlace == other.stepsToPlace;

    public override bool Equals(object obj) => obj is StairsSeat s && Equals(s);

    public override int GetHashCode() =>
        (ringSlot << 20) ^ (pawnCell << 10) ^ (supply << 5) ^ captured ^
        (phase << 26) ^ (moveDirection << 24) ^ (stepsMoved << 16) ^ (stepsToPlace << 13);
}
```

Three things to be careful about:

- **`Equals` must list the new fields.** `NetworkList<T>` is declared
  `where T : unmanaged, IEquatable<T>` and the struct's equality is its contract; leaving the new
  fields out of it is the kind of thing that works until the day something calls `IndexOf`.
- **`sbyte` serializes fine.** `BufferSerializer.SerializeValue<T>` takes any `unmanaged` primitive
  that is `IComparable`/`IConvertible`/`IEquatable<T>`, which `sbyte` is. `sbyte → int` is an
  implicit widening, so `StairsMoveRules.DirectionFree/Up/Down` (all `int`) compare and pass through
  unchanged; only the assignment back needs a `(sbyte)` cast.
- **The struct stays `unmanaged`**, which is what `NetworkList` requires. Four integer fields keep
  it so.

Also update the doc comment on `StairsPhase.Build` (`StairsTypes.cs:14`), which currently points at
`StairsGame.stepsToPlace` — that field is about to stop existing.

### 1.4 `StairsGame.cs` — every edit, in file order

**Delete** the five `NetworkVariable`s at `:42-60` (`phase`, `currentSeat`, `moveDirection`,
`stepsMoved`, `stepsToPlace`). **Keep** `winner` and both `NetworkList`s.

**`OnNetworkSpawn` / `OnNetworkDespawn` (`:122-128`, `:139-143`)** — drop the five
`OnValueChanged` subscriptions for the deleted variables. `winner.OnValueChanged += HandleIntChanged`
stays and is now `HandleIntChanged`'s only caller.

**Replace `CurrentPhase` and `IsPlaying` (`:252-253`)** with per-seat readers:

```csharp
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
```

**Delete `IsSeatToAct` (`:323-340`) outright.** Do not keep the name with new semantics — every one
of its six call sites means something slightly different and each is spelled out below. The compiler
will find them all.

**`CanPlacePawn` (`:347-367`)** loses the seat-0-first clause, which is the Setup half of "going out
of turn":

```csharp
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
```

**`RequestPlacePawnServerRpc` (`:371-396`)** — the both-pawns-down gate at `:391-395` goes, and the
placing seat starts moving immediately:

```csharp
    StairsSeat state = seats[seat];
    state.pawnCell = cell;
    seats[seat] = state;

    // This seat starts its own first Move at once. The other seat is not touched and may still be
    // in Setup, mid-Move or mid-Build; the two never wait on each other.
    BeginTurn(seat);
}
```

**`RequestMoveServerRpc` (`:398-446`)** — read the turn off the seat, write it back to the seat, and
a capture starts *your own* next turn rather than handing the board over:

```csharp
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
```

Note the `seats[seat] = state` on the capture path happens **before** `CheckForWinner`, which reads
`SeatState(seat).captured` — the order matters and it is the order the current code uses.

**`RequestEndMoveServerRpc` (`:448-458`)**:

```csharp
    int seat = SeatForSender(p);
    if (!StairsConst.IsSeat(seat) || PhaseOf(seat) != StairsPhase.Move)
    {
        return;
    }

    BeginBuild(seat);
```

**`RequestPlaceStepServerRpc` (`:460-503`)**:

```csharp
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
```

**`BeginTurn` / `BeginBuild` (`:525-556`)** — both now write one seat and nothing else:

```csharp
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
```

**`CheckForWinner` / `EndOnSupplyExhausted` (`:558-583`)** route through one new method:

```csharp
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
```

**`ResetGame` (`:585-616`)** — drop the four deleted `.Value` writes at `:610-614`, keep
`winner.Value = StairsConst.NoSeat`. `StairsSeat.Fresh()` now carries the per-seat turn reset, so
the rest of the method is unchanged.

### 1.5 `StairsSelection.cs`

Four sites, all mechanical once `IsSeatToAct` is gone:

```csharp
// :147-164  StillValid
case Mode.PawnSelected:
    return game.PhaseOf(seat) == StairsPhase.Move && game.SeatState(seat).HasPawnOnBoard;

case Mode.Dragging:
    return dragRole == StairsPieceRole.Pawn
        ? game.CanPlacePawn(seat)
        : game.PhaseOf(seat) == StairsPhase.Build && game.StepsToPlaceOf(seat) > 0;
        // §3 replaces this line again, with a switch over all three drag roles.

// :256  Click
if (game.PhaseOf(seat) != StairsPhase.Move)
{
    return;
}

// :290  CanDrag, SupplyStep
return game.PhaseOf(seat) == StairsPhase.Build && game.StepsToPlaceOf(seat) > 0;

// :414  RefreshLegal
int direction = game.MoveDirectionOf(seat);

// :476  HoverTint
if (hit.role == StairsPieceRole.Pawn && hit.seat == seat && game.PhaseOf(seat) == StairsPhase.Move)
```

### 1.6 `StairsView.cs` — `ReconcileConsole` (`:570-639`)

The "waiting for Green" and "waiting for a second player" branches both go: nobody waits for
anybody now, and a player whose opponent has not arrived can still play — their console says what
*they* can do, and the empty seat's own console already says `seat open`.

```csharp
void ReconcileConsole(int seat)
{
    TMP_Text status = statusLabels[seat];
    if (status == null)
    {
        return;
    }

    StairsSeat state = game.SeatState(seat);
    StairsPhase phase = state.Phase;

    text.Length = 0;
    text.Append(StairsConst.SeatName(seat));

    if (!state.IsOccupied)
    {
        text.Append("\nseat open");
        status.SetText(text);
        SetEndTurnVisible(seat, false);
        return;
    }

    // Plain ASCII throughout — these are code-built TextMeshPro on TMP's default LiberationSans SDF
    // atlas, which is generated over ASCII only.
    text.Append(" - captured ").Append(state.captured).Append('/').Append(StairsConst.CapturesToWin);
    text.Append(" - supply ").Append(state.supply);
    text.Append('\n');

    switch (phase)
    {
        case StairsPhase.GameOver:
            int won = game.winner.Value;
            text.Append(won == StairsConst.NoSeat
                ? "game over - a draw"
                : StairsConst.SeatName(won) + " wins");
            break;
        case StairsPhase.Setup:
            text.Append("drag your pawn onto an empty space");
            break;
        case StairsPhase.Move:
            text.Append("move, then End Turn");
            break;
        case StairsPhase.Build:
            text.Append("place ").Append(state.stepsToPlace).Append(" step");
            if (state.stepsToPlace != 1)
            {
                text.Append('s');
            }
            break;
    }

    status.SetText(text);

    // The key exists only while it can be pressed. Under §1 that is "this seat is in its own Move",
    // with no reference to anybody else.
    SetEndTurnVisible(seat, phase == StairsPhase.Move);
}
```

### 1.7 What this changes about the rules, and what it does not

Still enforced, per seat, by the same code as before: the eight-way adjacency, the exactly-one-level
elevation limit, the momentum rule, no moving onto the opponent's pawn or tiles, capture-takes-the-
whole-tower, tiles-owed-equals-spaces-moved with the Minimum Rule, "may not build under the
opponent's pawn", both win conditions.

Genuinely different, and worth writing into `stepsRules.md` rather than leaving as folklore:

- **There is no turn order.** Either player may move, end their move, build or place their pawn at
  any moment, including while the other player is mid-turn.
- **A capture no longer hands the board over.** It ends the capturing player's turn — meaning their
  own next `Move` begins immediately, with fresh momentum and no build.
- **Setup no longer has an order**, and does not have to be finished by both players before either
  can play. Seat 0 can be four turns into the game before seat 1 puts a pawn down. On a bare board
  the first few turns are pure building anyway, so this is less dramatic than it sounds.
- **A solo player can play the whole game.** Today that wedges after the first pawn placement.

Two races become possible and both are already handled, because the server re-runs the rule for
every request and the client predicts nothing:

- Both players build on the same empty cell in the same tick. The first request wins; the second
  finds a cell the opponent now owns, `IsLegalBuild` answers false, and the request is dropped —
  that player keeps the tile and still owes it. Their ghost simply does not land.
- One player captures the tower the other is standing next to. Both clients re-derive their legal
  sets from scratch every frame (`RefreshLegal`, `:389-424`), and `StillValid` drops a selection or
  a drag the server would no longer accept.

### 1.8 Two side effects to expect, not to fix

- `PreferredSeat` and the whole seating path are untouched; seats are still keyed to ring slots and
  still never released.
- The status line no longer tells a player that they are waiting. If two people want to *play*
  alternately they now have to take turns by agreement. That is the point of the change.

---

## 2 — The End Turn label is mirrored

> *"The End Turn text needs to be rotated 90 around x instead of −90 (the text is currently
> backwards)."*

### 2.1 The rule, once, so the other two cases are obvious

TextMeshPro lays its glyphs on the object's local **XY plane facing +Z**, and you read it from the
**−Z side**: the reader looks *along* the text's own forward. That is why the standard TMP billboard
is `transform.forward = camera.forward` and why `transform.LookAt(camera)` famously produces
mirrored text.

So for a label lying flat on a table and read from above:

| | maps forward to | maps text-up to | result read from above |
| --- | --- | --- | --- |
| `Quaternion.Euler(90, 0, 0)` | **down** | +Z (away from a reader at −Z) | correct |
| `Quaternion.Euler(-90, 0, 0)` | up, at the ceiling | −Z (at the reader) | upside down **and** mirrored |

The comment at `StairsView.cs:287-288` has this backwards — "+90 points a transform's forward at the
floor, which would put the text face-down under the key" is true about the geometry and wrong about
which way a reader is looking. Fix the comment as well as the number, or the next person will change
it back.

### 2.2 The change

`Assets/Scripts/Stairs/StairsView.cs:286-289`, in `BuildEndTurnKey`:

```csharp
        rt.localPosition = new Vector3(0f, EndTurnHeight + LabelLift, 0f);
        // Lying on the slab, reading face-up. +90 about X, not -90: TMP faces +Z and is read from
        // the -Z side, so a flat label wants its forward pointing at the FLOOR and its up pointing
        // away from the reader. -90 puts the face at the ceiling and reads mirrored.
        rt.localRotation = Quaternion.Euler(90f, 0f, 0f);
```

### 2.3 The same sign is wrong in two other places

- **`BuildStatusLabel` (`:244`) — already fixed in the working tree** and the fix is right. The
  console's +Z faces the board, the player stands at the console's −Z, so `Euler(0, 0, 0)` points the
  text's forward at the board and away from the reader. The old `Euler(0, 180, 0)` faced it at the
  player and read mirrored. Only the comment above it (`:243`) is now wrong; delete or reword it.
- **The `Height` child on `Step1.prefab` and `Step2.prefab`** is at `Euler(-90, 0, 0)` in both
  (`Step1.prefab:141`, `Step2.prefab:28`). Nobody has noticed because the code switches that child
  off on every instantiated step — which [§5](#5--the-number-on-a-tile-comes-from-the-tile) is about
  to stop doing. Fix both prefabs there.
- The tower-number billboard at `StairsView.cs:772` has the same error in `LookRotation` form —
  `LookRotation(up, away)` puts the text's forward at the ceiling. §5 deletes or replaces that line;
  if you keep a billboard it is `LookRotation(-up, away)`.

---

## 3 — Re-laying a tile that is already on the board

> *"Once a step is placed, players should be able to move them at any time (this is just to make it
> easier to fix a mistake in placement)."*

Nothing today can drag a placed tile: `CanDrag` (`StairsSelection.cs:284-295`) answers true only for
`Pawn` and `SupplyStep` and falls through to `default: return false` for `TowerStep`.

### 3.1 The rule, in `StairsMoveRules`

It goes here and not in `StairsGame`, for the reason the file's own header gives: one implementation,
so the client's highlight and the server's answer cannot disagree. Add beside `IsLegalBuild`:

```csharp
/// <summary>
/// Whether <paramref name="seat"/> may lift the top tile off <paramref name="cell"/> and re-lay it
/// somewhere else.
///
/// Not in stepsRules.md — this is the "fix a misplacement" affordance from bugFixesStairsGame.md §3,
/// and it is deliberately narrow: your own tiles only, the top of the stack only, and never a stack
/// with a pawn standing on it, which would move the floor out from under somebody. Where the tile
/// may then land is plain IsLegalBuild, so a re-laid tile can never end up on the opponent's tower
/// and "a tower has exactly one owner" still holds.
/// </summary>
public static bool IsLegalStepLift(View v, int seat, int cell)
{
    if (!v.IsReady || !StairsConst.IsSeat(seat) || !StairsConst.IsCell(cell))
    {
        return false;
    }

    if (v.height[cell] == 0 || v.owner[cell] != seat)
    {
        return false;
    }

    return !v.HasPawnOn(cell);
}
```

### 3.2 `StairsGame` — the request, and one client-side reader

A cheap "may I?" for the client, using its own scratch view so it never touches `serverView`:

```csharp
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
```

```csharp
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
```

`SetTower` zeroes the owner when the height reaches 0 (`StairsTower`'s constructor,
`StairsTypes.cs:112-116`), so emptying a cell this way leaves it genuinely bare. Both writes are
single `NetworkList` element writes and both raise `OnListChanged`, which `StairsView` and
`StairsSelection` treat as one dirty flag — two writes, one rebuild.

`fromCell == toCell` is rejected rather than treated as an error: [§3.4](#34-stairsselection--the-third-drag-role)
deliberately lets the ghost rest back on its own square so a drag can be abandoned by putting the
tile back.

### 3.3 `StairsView` — take the tile off the board while it is in hand

Without this the player sees the tile they are dragging *and* the tile still sitting on the tower.

```csharp
/// <summary>
/// The cell whose top tile is currently in this player's hand, or NoCell. Local and per-client: as
/// far as the server is concerned the tile is still on the board, and the drag may be abandoned.
/// </summary>
int liftedCell = StairsConst.NoCell;

public void SetLiftedCell(int cell)
{
    if (liftedCell == cell)
    {
        return;
    }

    int previous = liftedCell;
    liftedCell = cell;
    ApplyLifted(previous);
    ApplyLifted(cell);
}

void ApplyLifted(int cell)
{
    if (!StairsConst.IsCell(cell) || towerSteps[cell] == null || towerSteps[cell].Count == 0)
    {
        return;
    }

    GameObject top = towerSteps[cell][towerSteps[cell].Count - 1];
    if (top != null)
    {
        top.SetActive(cell != liftedCell);
    }
}
```

Call `ApplyLifted(cell)` as the last line of `ReconcileTower` (`:370-403`), so a rebuild that lands
mid-drag re-hides the right tile instead of putting the old one back.

`HighlightTarget` (`:804-825`) then has to skip a hidden tile, or the source cell goes dark while it
is being dragged:

```csharp
public StairsTint HighlightTarget(int cell)
{
    List<GameObject> steps = StairsConst.IsCell(cell) ? towerSteps[cell] : null;
    if (steps != null)
    {
        // Top down, skipping the tile currently in somebody's hand: highlighting a hidden object
        // shows nothing, and highlighting the cell under a tower is invisible.
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            GameObject step = steps[i];
            if (step == null || !step.activeSelf)
            {
                continue;
            }
            StairsTint tint = step.GetComponent<StairsTint>();
            if (tint != null)
            {
                return tint;
            }
        }
    }

    return board != null ? board.CellTint(cell) : null;
}
```

### 3.4 `StairsSelection` — the third drag role

A field beside `dragRole`:

```csharp
/// <summary>Where a dragged TowerStep came from, or NoCell for anything else.</summary>
int dragFromCell = StairsConst.NoCell;
```

```csharp
// CanDrag (:277-296) gains a case
case StairsPieceRole.TowerStep:
    // Re-laying a tile you have already placed, at any time. bugFixesStairsGame.md §3.
    return game.CanLiftStep(seat, piece.cell);
```

```csharp
// StillValid (:155-159), now a switch rather than a ternary
case Mode.Dragging:
    switch (dragRole)
    {
        case StairsPieceRole.Pawn:
            return game.CanPlacePawn(seat);
        case StairsPieceRole.SupplyStep:
            return game.PhaseOf(seat) == StairsPhase.Build && game.StepsToPlaceOf(seat) > 0;
        case StairsPieceRole.TowerStep:
            return game.CanLiftStep(seat, dragFromCell);
        default:
            return false;
    }
```

```csharp
// BeginDrag (:300-315)
void BeginDrag(StairsView pieces, StairsPieceTag piece)
{
    // The ghost is cloned from the LIVE piece, so it has to be cloned before the piece is hidden:
    // Instantiate copies activeSelf, and a ghost cloned from a deactivated tile is invisible.
    ghost = pieces.CreateGhost(piece.gameObject);
    if (ghost == null)
    {
        return;
    }

    StairsTint tint = StairsTint.Attach(ghost);
    tint.SetHighlight(GhostColor, GhostStrength);

    dragRole = piece.role;
    dragFromCell = piece.role == StairsPieceRole.TowerStep ? piece.cell : StairsConst.NoCell;
    mode = Mode.Dragging;

    // Now take it off the board, so the player has one tile in hand rather than two on screen.
    pieces.SetLiftedCell(dragFromCell);
}
```

```csharp
// EndDrag (:356-364)
void EndDrag()
{
    if (ghost != null)
    {
        Destroy(ghost);
        ghost = null;
    }

    // Cancel() reaches here on a scene switch, when the view may already be gone.
    if (StairsView.Instance != null)
    {
        StairsView.Instance.SetLiftedCell(StairsConst.NoCell);
    }

    dragFromCell = StairsConst.NoCell;
    mode = Mode.Idle;
}
```

```csharp
// CommitDrag (:339-354)
if (legalCells.Contains(cell))
{
    switch (dragRole)
    {
        case StairsPieceRole.Pawn:
            game.RequestPlacePawnServerRpc(cell);
            break;
        case StairsPieceRole.SupplyStep:
            game.RequestPlaceStepServerRpc(cell);
            break;
        case StairsPieceRole.TowerStep:
            game.RequestMoveStepServerRpc(dragFromCell, cell);
            break;
    }
}
```

```csharp
// MoveGhost (:317-337), inside the legal branch
Vector3 point = dragRole == StairsPieceRole.Pawn
    ? pieces.PawnPoint(cell)
    : pieces.PlacementPoint(cell);

// The lifted tile is still in the tower as far as the state is concerned, so its own square reads
// one level too tall while it is in hand.
if (dragRole == StairsPieceRole.TowerStep && cell == dragFromCell)
{
    point.y -= pieces.StepThickness;
}

ghost.transform.localPosition = point;
```

`RefreshLegal` (`:389-424`) needs no change: its `else` branch already calls
`StairsMoveRules.CollectBuilds` for every non-pawn drag, and a `TowerStep`'s own square is in that
list by construction (it is your own tower), which is exactly what lets a drag be abandoned by
putting the tile back where it came from.

### 3.5 What this deliberately does not allow

The scope below is the settled one (2026-09-07, [§9](#9--decisions-taken)). Each line is a rule
somebody will want to relax later; each is also load-bearing.

- Somebody else's tiles. `IsLegalStepLift` requires `owner == seat`.
- A tile from under any pawn, yours or theirs.
- A tile from the middle of a stack — top only.
- Landing on the opponent's tower, or under the opponent's pawn: that is `IsLegalBuild`, unchanged.

---

## 4 — The pawn would not move

> *"I couldn't move the gamepiece after initially placing it on the board, even though I had a step
> next to me so I should have been able to move up to that step."*

Five things produce exactly that symptom. Two are bugs and §1 removes both; three are the rules
working as written, and one of those is a decision for you rather than a fix.

| | Cause | Where | Verdict |
| --- | --- | --- | --- |
| A | It was the other seat's turn. Nothing responds and nothing says why | `StairsGame.cs:336` | **Bug in spirit** — §1 removes it |
| B | Only one seat was occupied, so the game never left `Setup` after the pawn went down | `StairsGame.cs:391-395` | **Bug** — §1 removes it |
| C | The adjacent tile is the **opponent's**, and you may only climb your own | `StairsMoveRules.cs:133-143` | Rules as written, and **kept** — [§4.5](#45-the-rule-that-was-questioned-and-kept) |
| D | The height difference is not exactly ±1 | `StairsMoveRules.cs:114-118` | Rules as written |
| E | Momentum: you had already stepped the other way this turn | `StairsMoveRules.cs:120-124` | Rules as written |

A sixth, which is not about the rules at all: `StairsSelection` stands down completely while the
menu is open, while `WorldGrab.IsActive`, **or while either grip is merely held**
(`Playable()`, `:516-529`). A resting finger on a grip button makes the whole board inert with no
feedback. If it turns out to be that, the fix is not here — it is a deliberate shared convention
(`PlateauSelection` and `ControlListener` do the same) and changing it belongs in a separate change.

### 4.1 B is certain if you were alone in the room

Worth stating plainly because it is invisible from inside the headset. `RequestPlacePawnServerRpc`
only starts the game when both seats are occupied *and* both pawns are down:

```csharp
// StairsGame.cs:391-395
if (SeatState(0).HasPawnOnBoard && SeatState(1).HasPawnOnBoard &&
    SeatState(0).IsOccupied && SeatState(1).IsOccupied)
{
    BeginTurn(0);
}
```

With one player: the pawn goes down, `phase` stays `Setup`, `CanPlacePawn` now answers false
(the pawn is on the board), `IsSeatToAct` therefore answers false, and every path out is shut —
`Click` returns early because the phase is not `Move` (`StairsSelection.cs:256`), the End Turn key
is never shown (`StairsView.cs:638`), and no supply tile can be dragged because that needs `Build`.
The console reads `waiting for a second player` and it never stops.

The only thing that does not fit the report is the step you say was next to you: in this state
nobody can build, so a second player must have been present. Which is why the measurement below is
worth the two minutes.

### 4.2 If two players were present, C is the most likely

`stepsRules.md:14` — *"You cannot move onto a space occupied by the opponent's pawn **or the
opponent's tiles** (unless you are making a capture)"* — and a capture is by definition downward:

```csharp
// StairsMoveRules.cs:133-143
if (target > 0 && v.owner[to] == opponent)
{
    if (step != DirectionDown)
    {
        return false;        // climbing onto their tiles is forbidden; dropping onto them is Action 1
    }
    capture = true;
}
```

On a bare board the first tile placed by either player is the *opponent's* tile from the other
player's point of view, so the very first "there is a step next to me" is normally one you may not
climb. It looks exactly like a bug and it is the rule.

### 4.3 D is the other easy one to hit

You must be **exactly** one level below the tile to climb it. A pawn standing on its own tile
(level 1) next to another 1-high tower has a difference of 0 and no legal move there; a pawn on bare
board next to a 2-high tower likewise.

### 4.4 The measurement — one line, before you write any of §1

Paste at the top of `StairsSelection.Click` (`:243`) and delete it once you know. `DebugLog` mirrors
`Debug.Log` into the in-headset box, so this is readable without adb — but that box holds ten lines,
so **turn `ColocationProbe` off first** or its 1 Hz output will scroll this away.

```csharp
    // TEMPORARY — bugFixesStairsGame.md §4. Delete once the cause is known.
    if (hit != null && hit.role == StairsPieceRole.Pawn && hit.seat == seat)
    {
        game.FillView(ref view);
        int here = view.pawnCell[seat];
        string neighbours = "";
        int[] around = new int[8];
        int count = StairsConst.IsCell(here) ? StairsBoard.Neighbours(here, around) : 0;
        for (int i = 0; i < count; i++)
        {
            neighbours += around[i] + ":h" + view.height[around[i]] + "o" + view.owner[around[i]] + " ";
        }
        Debug.Log("Stairs seat " + seat + " phase " + game.CurrentPhase +
                  " turn " + game.currentSeat.Value + " dir " + game.moveDirection.Value +
                  " cell " + here + " level " + (StairsConst.IsCell(here) ? view.height[here] : -1) +
                  " | " + neighbours);
    }
```

After §1 the same line reads `" phase " + game.PhaseOf(seat) + " dir " + game.MoveDirectionOf(seat)`
and drops `turn` entirely.

How to read the one line it prints:

| What you see | Cause | What to do |
| --- | --- | --- |
| `turn` is not your seat | A | §1 |
| `phase Setup` with `cell` ≥ 0 | B | §1 |
| a neighbour `o<the other seat>` at `h` = your level + 1 | C | §4.5 — your call |
| no neighbour at `h` = your level ± 1 | D | nothing; the rules |
| `dir` is not 0 and every candidate is the other way | E | nothing; the rules |

### 4.5 The rule that was questioned, and kept

If it is **C**, there is nothing to fix. `stepsRules.md`'s rule that you may only ever climb your own
tiles **stays as written** (decided 2026-09-07, [§9](#9--decisions-taken)): the opponent's towers are
walls you can only ever drop onto, and that is the whole shape of a capture.

The alternative is recorded only so it is not rediscovered and re-argued. It would be this, and
nothing else:

```csharp
// StairsMoveRules.cs:136-142 — NOT taken. Allowing an upward step onto the opponent's tiles.
if (target > 0 && v.owner[to] == opponent && step == DirectionDown)
{
    capture = true;
}
```

That makes any tile climbable and leaves capture as "you came down onto them" — a materially
different game, in which the opponent's towers become staircases rather than walls.

So if the measurement says C, the honest answer is that the board was behaving correctly and the
surprise is a *legibility* problem, not a rules one: nothing on screen says why a square is not lit.
The cheapest thing that would have prevented the confusion is already in the code — the legal
squares light green — so if C is what happened, consider whether the tile that could not be climbed
should be lit in a third colour ("theirs, and you may only drop onto it") rather than not lit at all.
That is a new item, not part of this document.

---

## 5 — The number on a tile comes from the tile

> *"The game is currently making some different labels to show the number of tiles on a cell, but I
> just want the text on the step prefab to be active (it is currently inactive). Any time a tile
> gets moved it should see if there are tiles under it and update the text accordingly."*

### 5.1 What is there now, and why

`StairsView` never uses the authored label. It clones the `Height` child out of `Step1.prefab` into
a separate `Stairs Root > Labels` root, one clone per cell, and switches the authored copy off on
every instantiated step (`BuildStep`, `:664-668`). The reason recorded at `:405-415` is that a
rotated child of a non-uniformly scaled parent gets sheared by Unity, and the label yaws to face
each viewer.

**That reason does not hold for these prefabs.** `Step1`/`Step2` are scaled `(0.2, 0.03, 0.2)`: X
and Z are *equal*, and the label lies in the XZ plane with its normal along Y. Any yaw about Y maps
the text's two in-plane axes onto X and Z, both scaled 0.2 — a uniform scale, so no shear. The only
odd axis, 0.03, is the direction the text has no thickness in. A flat number on one of these tiles
can be a child and can still turn to face the reader.

(It *would* shear the moment the step prefab's X and Z scales differ. If that ever happens, the
label has to stop turning or go back to a separate root. Worth a line in
`Assets/Scripts/Stairs/CLAUDE.md`.)

### 5.2 The prefab edit — both prefabs, and it is §2's bug again

Both `Assets/Prefabs/Stairs/Step1.prefab` and `Step2.prefab` hold a `Height` child that is a
`TextMeshPro` with `m_text: 1`, `fontSize 8`, `anchoredPosition (0, 0.6)`, `localScale (1,1,1)`,
`m_IsActive: 1` — and `m_LocalRotation: {x: -0.7071068, y: 0, z: 0, w: 0.7071068}`, which is
`Euler(-90, 0, 0)` (`Step1.prefab:141`, `Step2.prefab:28`). Note `m_LocalEulerAnglesHint` already
says `{x: 90}` in both — the hint and the quaternion disagree, which is how this got past review.

**In the Editor** (preferred — it keeps the hint and the quaternion consistent): open each prefab,
select `Height`, set Rotation X to **90**, save.

Hand-editing the YAML is equivalent if you prefer:

```yaml
  m_LocalRotation: {x: 0.7071068, y: 0, z: 0, w: 0.7071068}
  ...
  m_LocalEulerAnglesHint: {x: 90, y: 0, z: 0}
```

Nothing else about the child needs to change. `anchoredPosition (0, 0.6)` is a RectTransform
position in the parent's space and is unaffected by the parent's own rotation: 0.6 × the step's
0.03 Y scale = **18 mm**, i.e. 3 mm clear of a 30 mm-thick tile's top face. That is the same offset
the cloned label gets from `LabelLift` today, and the authored scale of 1 under the step's 0.2
renders at exactly the size the clone's `labelScale = 0.2` produces now — **the number does not
change size or position, only which object draws it.**

### 5.3 The code

**Delete** from `StairsView`:

- the `labels` root and its creation (`:76`, `:187`)
- `towerLabels` (`:87`), `labelTemplate` (`:94`), `labelColors` (`:95`), `labelScale` (`:98`)
- `UpdateTowerLabel` in full (`:405-447`) and its call at `:402`
- the `labelTemplate` / `labelColors` half of `MeasureFromPrefabs` (`:155-174`), keeping a warning
  if the `Height` child is missing, because a missing child is now a missing number rather than a
  missing feature

**`BuildStep` (`:651-673`)** takes the number and decides the label with it:

```csharp
/// <param name="number">This tile's own level in its tower, 1-based. 0 for a tile on a console,
/// which is not in a tower and shows nothing.</param>
GameObject BuildStep(int owner, Transform parent, StairsPieceRole role, int seat, int cell, int number)
{
    GameObject prefab = StepPrefab(owner);
    if (prefab == null || parent == null)
    {
        return null;
    }

    GameObject step = Instantiate(prefab, parent, false);
    step.transform.localRotation = Quaternion.identity;

    // The prefab's own Height child IS the number now — every tile says how many tiles are under
    // it, itself included, so the top of a tower always reads as the tower's height. The ones
    // beneath it are buried inside the tile above and are never seen.
    Transform authored = HierarchyUtils.FindDescendant(step.transform, StairsConst.StepLabelName);
    if (authored != null)
    {
        TMP_Text label = number > 0 ? authored.GetComponent<TMP_Text>() : null;
        if (label != null)
        {
            label.SetText("{0}", number);
        }
        authored.gameObject.SetActive(label != null);
    }

    StairsPieceTag.Attach(step, role, seat, cell);
    StairsTint.Attach(step);
    return step;
}
```

Call sites:

```csharp
// ReconcileTower (:393)
GameObject step = BuildStep(tower.Owner, pieces, StairsPieceRole.TowerStep, tower.Owner, cell,
                            steps.Count + 1);

// ReconcileSupply (:521) and ReconcileCaptured (:558) — no number
..., StairsConst.NoCell, 0);
```

**Why "set it once, when the tile is built" is enough**, and answers *"any time a tile gets moved it
should see if there are tiles under it"*: a tower's steps are only ever appended to or removed from
the **end** (`ReconcileTower`, `:383-400`), and an owner change clears the whole list and rebuilds it
(`:377-381`). A tile's index therefore never changes underneath it. Moving a tile (§3) destroys the
top step of the source tower and builds a new one at the top of the destination, each with its own
correct number. A capture empties a tower outright. There is no path that leaves a stale number.

`StairsTint` is unaffected: `Capture()` skips TMP renderers explicitly (`StairsTint.cs:67-70`), so a
highlighted tile does not tint its own number, and it walks inactive children so the order of
`Attach` and `SetActive` does not matter.

`CreateGhost` (`:883-887`) already switches the authored label off on the clone. Keep that — a tile
in hand has no level yet.

### 5.4 The facing decision

With the label back on the tile, it inherits the tile's orientation: steps are built with
`localRotation = Quaternion.identity`, so every number's top points at the board's +Z. **Seat 0 (the
−Z edge) reads them the right way up and seat 1 reads them upside down.**

**Decided: keep the per-viewer turn** (2026-09-07, [§9](#9--decisions-taken)), which is what the
existing `LateUpdate` is for. Cache the top tile's label per cell and point the same loop at it:

```csharp
// beside towerSteps
readonly TMP_Text[] towerTopLabel = new TMP_Text[StairsConst.CellCount];
```

Set it at the end of `ReconcileTower`:

```csharp
    // Only the top tile's number is ever visible, so only that one is worth turning each frame.
    towerTopLabel[cell] = null;
    if (steps.Count > 0)
    {
        Transform authored = HierarchyUtils.FindDescendant(steps[steps.Count - 1].transform,
                                                           StairsConst.StepLabelName);
        towerTopLabel[cell] = authored != null ? authored.GetComponent<TMP_Text>() : null;
    }
```

and in `LateUpdate` (`:737-774`), replacing `towerLabels` with `towerTopLabel` and fixing the sign:

```csharp
            // Forward at the FLOOR and text-up away from the reader — TMP is read from its -Z side,
            // so LookRotation(up, ...) is what draws a mirrored number. See §2.
            label.transform.rotation = Quaternion.LookRotation(-up, away.normalized);
```

The alternative — delete `LateUpdate` and let the number sit at its authored angle — is one player
reading every tower upside down across a real table. It is fine for a single-headset test and not
for a game.

---

## 6 — Lift the tiles a player still owes

> *"When the player presses end turn, the appropriate number of tiles from the player's stack should
> lift slightly or highlight so that it is clear how many that player gets to place."*

A lift rather than a tint, for one concrete reason: `StairsSelection` owns the tint system and
clears every highlight it applied at the top of its own `Update` (`ClearHighlights`, `:496-506`) at
order 25, after `StairsView` has run at order 20. A tint applied during `Reconcile` would be wiped
the first frame the pointer hovered a supply tile and never come back. A position is not contested
by anything.

### 6.1 `StairsView`

```csharp
/// <summary>How far the tiles a player still owes float clear of their supply stack, in step
/// thicknesses. One tile is a visible gap without reading as a second stack.</summary>
const float OwedLiftInSteps = 1f;

/// <summary>Supply tiles per stack. 4 x 10 at StepsPerPlayer 40.</summary>
static readonly int SupplyPerStack =
    Mathf.Max(1, Mathf.CeilToInt(StairsConst.StepsPerPlayer / (float)StairsConst.SupplyStacks));

/// <summary>Console-local position of the nth tile in a player's supply, counting from 0 at the
/// bottom of the first stack. Owed tiles float clear of the rest — bugFixesStairsGame.md §6.</summary>
Vector3 SupplyStepPosition(int index, bool owed)
{
    int stack = Mathf.Min(index / SupplyPerStack, StairsConst.SupplyStacks - 1);
    int level = index - stack * SupplyPerStack;

    return new Vector3(
        SupplyFirstX + stack * SupplyStackPitch,
        (level + 0.5f) * stepThickness + (owed ? OwedLiftInSteps * stepThickness : 0f),
        0f);
}
```

`ReconcileSupply` (`:506-539`) then builds with `SupplyStepPosition(steps.Count, false)` in place of
the inline arithmetic at `:517-536`, and ends with one pass that is the whole feature:

```csharp
    // The tiles this player owes, floating clear of the stack: the count is readable across the
    // table without reading the status line, and it shrinks as they are placed.
    int owed = game.SeatState(seat).Phase == StairsPhase.Build ? game.StepsToPlaceOf(seat) : 0;
    for (int i = 0; i < steps.Count; i++)
    {
        steps[i].transform.localPosition = SupplyStepPosition(i, i >= steps.Count - owed);
    }
```

Before §1 the same line is
`int owed = game.CurrentPhase == StairsPhase.Build && game.currentSeat.Value == seat ? game.stepsToPlace.Value : 0;`
— so §6 can be done first and adjusted, or done after §1 and written once.

### 6.2 Why it updates itself

`stepsToPlace` lives on the seat after §1, so every change to it is a `seats[seat] = state` write →
`OnListChanged` → `BoardChanged` → `dirty` → `Reconcile` on the next frame, on every client. Pressing
End Turn lifts the tiles; each placement drops one back into line because `steps.Count` and `owed`
both fall by one; reaching zero leaves the stack flat.

The lift is drawn in the **owner's own supply**, on both headsets — the opponent can see how many
tiles you owe, which is public information in a game played across a real table.

### 6.3 If you want the highlight as well

Both, not either, is fine — but the tint has to be applied in `LateUpdate`, after `StairsSelection`
has run and cleared its own highlights for the frame:

```csharp
// in LateUpdate, after the label pass
for (int seat = 0; seat < StairsConst.Seats; seat++)
{
    int owed = game != null && game.SeatState(seat).Phase == StairsPhase.Build
        ? game.StepsToPlaceOf(seat) : 0;
    List<GameObject> steps = supplySteps[seat];
    for (int i = steps.Count - owed; i < steps.Count; i++)
    {
        StairsTint tint = i >= 0 && steps[i] != null ? steps[i].GetComponent<StairsTint>() : null;
        if (tint != null)
        {
            tint.SetHighlight(OwedColor, OwedStrength);
        }
    }
}
```

Note this leaves the last-lit tiles tinted for one frame after the count drops, because nothing
clears them — `StairsSelection.ClearHighlights` only clears what *it* lit. If you take this option,
clear the whole supply first each frame rather than tracking which ones changed.

---

## 7 — Order of work, and how to verify each one

Cheapest order, with the reason:

1. **§4.4's diagnostic line** — before anything else changes, so the measurement describes the game
   you actually played.
2. **§2** — one number, no dependencies.
3. **§5** — the prefab edit shares §2's rotation fix, so do them together and save both prefabs once.
4. **§6** — self-contained; written against the pre-§1 names costs one line to revisit.
5. **§1** — everything else is stable underneath it.
6. **§3** — reads `PhaseOf`/`IsGameOver` from §1 and `HighlightTarget` from §5's neighbourhood.
7. **Re-run §4's diagnostic.** If it was A or B, it is gone. If it was C, decide.

Testing without a headset works for all of it except the parts that need two clients. The keyboard
fallbacks that matter here (from the root [`CLAUDE.md`](../CLAUDE.md#build-and-test)): right trigger
is **`.`**, `B` cancels a selection or a drag, `X` opens the menu, and the right grip is **`P`** —
which is worth knowing because holding it makes the whole board inert (§4's sixth cause).

| § | What to check | How |
| --- | --- | --- |
| 1 | A single player can place a pawn, move, End Turn, build and start again | Editor alone, host, place the pawn, press End Turn — the console must go to `place 1 step`, not `waiting for a second player` |
| 1 | Both seats live at once | Two clients: put seat 1 into Build while seat 0 is mid-Move and confirm neither console changes when the other acts |
| 1 | A capture still ends the capturing turn | Build a tower, drop onto it, confirm the capturing seat's console returns to `move, then End Turn` with no build owed |
| 2 | Stand at a console and read the key | The words `End Turn` read left-to-right, top away from you |
| 3 | Drag a placed tile to another square | Tower shrinks by one, destination grows by one, supply and owed count unchanged on both consoles |
| 3 | The three refusals | A tile under a pawn, the opponent's tile, and a tile that is not on top all fail to start a drag at all (no ghost appears) |
| 4 | Re-run the diagnostic | One line, read against the table in §4.4 |
| 5 | Build a 3-tower | It reads `3`; take one off with §3 and it reads `2` immediately |
| 5 | Walk round the table | The number stays upright from wherever you stand (if you kept the billboard) |
| 6 | Press End Turn after moving 2 spaces | Exactly two tiles float clear of the supply; placing one leaves one floating; placing the last leaves the stack flat |

**One Editor pass is mandatory** even if everything compiles headlessly: §1 changes a
`NetworkVariable` set and §3 adds a `ServerRpc`, and neither Netcode's ILPP nor the scene's
serialized copy of `StairsGame` is checked by `csc`. Open `StairsGame.unity` afterwards and confirm
the `Stairs Root > StairsGame` Inspector no longer lists the five deleted variables —
`StairsGame.unity:3806-3830` still holds their serialized values and Unity drops them on the next
save of the scene.

---

## 8 — Docs to update in the same commit

The repo's own rule, and the reason `docs/CLAUDE.md` rotted: the doc changes with the code.

- **[`Assets/Scripts/Stairs/CLAUDE.md`](../Assets/Scripts/Stairs/CLAUDE.md)** — the state table
  (`towers`/`seats`/`phase`/`currentSeat`/…) is wrong after §1 and so is the whole **The turn**
  section. The "readings taken where `stepsRules.md` is ambiguous" list needs the three new
  deviations from [§1.7](#17-what-this-changes-about-the-rules-and-what-it-does-not). **The height
  labels** section describes machinery §5 deletes. Also: the cells *do* have colliders (§0), and
  `Stairs Controller` is where `StairsSelection` lives.
- **[the root `CLAUDE.md`](../CLAUDE.md)** — the `stepsRules.md` row says "**Implemented in full**".
  After §1 that is no longer true; it becomes "implemented, with the turn order deliberately
  removed — see `bugFixesStairsGame.md` §1".
- **[`docs/stepsRules.md`](stepsRules.md)** — the Setup section's numbered order and the implied
  alternation are both gone. Either note it there or accept that the rules doc now describes the
  board game and the code describes this one; say which, in one line, rather than leaving it open.
- **This file** — mark the sections applied as they land, the way `bugFixes2.md` and
  `BASHUpdate.md` do, with anything that came out differently noted inline.

`powershell -File Tools/Check-DocLinks.ps1` after editing any of them.

---

## 9 — Decisions taken

Four things above could reasonably have gone either way. All four were settled on **2026-09-07**,
before any code was written, and all four took the option this document recommends — so nothing
below needs revisiting to follow the plan. They are written down because each has a plausible
alternative that somebody will propose again.

| | Question | Decided | The alternative, and why it was not taken |
| --- | --- | --- | --- |
| §1 | How far does "out of turn" go? | **Fully independent seats.** Each seat runs its own `Setup → Move → Build` cycle; a quick player can take three turns while the other thinks | Keeping the alternation and letting a player *take* the turn out of order. It forfeits whatever the other player had in flight (owed tiles, momentum), needs more code, and has more edge cases |
| §3 | Scope for re-laying a placed tile | **Your own tile, top of the stack, never one with a pawn on it, at any time** | Lifting from under your own pawn (drops you a level as a side effect of a correction), or anything at all including the opponent's tiles (breaks one-owner-per-tower and lets either player dismantle the other's board) |
| §4 | May a player climb the opponent's tiles? | **No — `stepsRules.md` stands.** Their towers are walls you can only drop onto | Allowing it is one line and makes every tower a staircase. See [§4.5](#45-the-rule-that-was-questioned-and-kept) |
| §5 | Should the tower number face each viewer? | **Yes, keep the per-viewer turn.** It is shear-free on these prefabs | A fixed orientation is simpler and leaves the player on the +Z edge reading every tower upside down |

Nothing else in this document is waiting on an answer. The one thing still *unmeasured* is which of
§4's five causes actually produced the pawn that would not move — [§4.4](#44-the-measurement--one-line-before-you-write-any-of-1)
is the line that settles it, and it is the first thing to do.
