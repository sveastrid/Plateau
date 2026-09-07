# Stairs — `StairsGame`

Menu key `Stairs`. All of it lives in this folder. Rules: [`stepsRules.md`](../../../docs/stepsRules.md).

Read the root [`CLAUDE.md`](../../../CLAUDE.md) and [`../CLAUDE.md`](../CLAUDE.md) (shared systems)
for anything outside this folder.

## The Stairs game

**Two players, on an 8×8 board; everybody else in the room watches.** Each has a pawn and a supply of
40 tiles. You walk your pawn one level up or down at a time, then build with as many tiles as you
moved; drop onto the opponent's tiles from above and you take the lot. Twelve captured tiles wins.

All of it compiles into **`MRBoardGame.Stairs`**, which references `MRBoardGame.Shared` and nothing
else. Chasms and BASH are invisible from here and this is invisible from them — enforced by the
compiler, not by convention.

`stepsRules.md` is implemented, including both win conditions — **except its turn order, which was
deliberately removed**: the two seats run independently and neither ever waits on the other. See
[The turn](#the-turn) and [`bugFixesStairsGame.md`](../../../docs/bugFixesStairsGame.md) §1. What is
*not* here: an undo, a move history, a clock, or any way to agree a draw.

### Where the state lives

`StairsGame` is a `NetworkBehaviour` on the in-scene object **`World Root > Stairs Root`**, which
also carries the `NetworkObject`, `StairsBoard` and `StairsView`. `StairsSelection` is the exception:
it sits alone on **`Stairs Controller`, a scene root** beside `World Root`, and needs nothing from the
content frame — it reaches the other three through their `Instance` singletons and the rig by name,
and every position it writes is local to a parent already inside the frame. That is BASH's shape
(`Bash Root`), not Chasms' — Chasms puts `PlateauGame` on the persistent `Room Anchor.prefab`
because its board has to survive being switched away from. Stairs owns nothing that needs to outlive
its own scene, and a third game's state on the shared anchor makes every game pay for it.

**The consequence is that switching to another game and back starts a new game.** That is intended,
and it is why `OnGameSelected` does nothing and there is a separate **`New Game`** key
(`StairsModule.asset`'s one `menuActions` row → `StairsGame.InvokeMenuAction`): pressing `Stairs`
while already in Stairs must not wipe a board somebody is still playing on.

There is **no `NetworkObject` per piece**. Towers, pawns, supply stacks and captured piles are local
visuals `StairsView` rebuilds from the replicated state — the same choice Chasms made and for the
same reason: `DefaultNetworkPrefabs.asset` is global by construction under `ForceSamePrefabs`, and a
difference in that *set* is a join that hangs on "Joining room…" with no reason string. 160 steps
would also be 160 spawn messages. **Adding Stairs left that asset at four entries; keep it there.**

Two `NetworkList`s and one `NetworkVariable`, all server-written:

| | |
| --- | --- |
| `towers` | `NetworkList<StairsTower>`, 64 entries, indexed the way `StairsBoard` indexes cells |
| `seats` | `NetworkList<StairsSeat>`, 2 entries — ring slot, pawn cell, supply, captured, **and that seat's whole turn**: `phase`, `moveDirection`, `stepsMoved`, `stepsToPlace` |
| `winner` | the winning seat, or `NoSeat` for "not over yet" *and* for a draw |

**The turn state is on the seat, not on the game**, and that is the whole of "either player may act at
any time". There is no `currentSeat` and no game-wide `phase` to fall out of step with the seats;
`EndGame` writes `GameOver` into **both** seats, which is why `IsGameOver` can read seat 0 alone.
Five `NetworkVariable`s were deleted to get here — `StairsGame.unity` still holds their serialized
values until the scene is next saved. Read the turn through `PhaseOf(seat)`, `StepsToPlaceOf(seat)`
and `MoveDirectionOf(seat)`; the old global `CurrentPhase` and `IsSeatToAct` are gone on purpose,
because every one of `IsSeatToAct`'s call sites meant something slightly different.

Every `ServerRpc` is `RequireOwnership = false` and starts by resolving the **sender's** seat from
`ServerRpcParams` — never from a seat index in the message — and then **re-runs the same
`StairsMoveRules` call the client used to draw its highlight**. The client's highlight is a hint; the
server's answer is the rule. There is no local prediction: this project only predicts state a client
holds an exclusive server-granted lock on (`RoomAnchor.worldHolder`), and there is no such lock for a
move.

**A `NetworkList`'s initial contents arrive in the spawn payload and raise no `OnListChanged`.**
`StairsGame` therefore raises `BoardChanged` by hand at the end of `OnNetworkSpawn`, and both
`StairsView` and `StairsSelection` treat it as a dirty flag rather than a diff — which is also what
makes a late joiner's board appear at all.

### A tower has exactly one owner

`StairsTower` is `{ height, owner }` and `owner` only means anything when `height > 0`. That a tower
is single-owner is a *rule*, not a coincidence: you may build on empty squares or on your own tiles
and never on the opponent's, and a capture takes the whole stack off at once. Every write goes
through `StairsGame.SetTower`, so there is one place for that invariant to hold.

`StairsSeat.ringSlot` is a `PlayerControls.spawnSlot` — a place on `PlayerRing`, the only stable
per-player index in the project — and **not** the seat number. The two are different values and
conflating them is the bug that gave BASH's second player no base.

### Seats, and why they are never given back

`ServeSeats` polls at **4 Hz** rather than hooking `OnClientConnectedCallback`, for the same reason
`SpawnManager` and `PlateauGame` do: `spawnSlot` is assigned inside `PlayerControls.OnNetworkSpawn`,
which can run later than the connect callback.

A seat is keyed to a ring slot, and **once taken it is never released** — not on disconnect, not on
a `New Game`. `PlayerRing.PickFreeSlot` hands a reconnecting player the slot they vacated, so a
player who drops out walks back into their own half-finished game. The deliberate cost is that a
third person in the room spectates even while a seat's player is away. Since the turn state moved
onto the seat the *other* player is no longer blocked by this — they keep moving and building — but
the absent seat's board is frozen where they left it and their console still says what they were
mid-way through.

`PreferredSeat` maps a ring slot to the board edge it is standing at — seat 0's console is on the
board's **−Z** edge and seat 1's on **+Z**. `PickFreeSlot` gives the first two players slots 0 and 6,
which are exactly −Z and +Z, so in the ordinary case this is already right; it earns its keep for a
ring fragmented by mid-game departures. Slots 3 and 9 sit on the axis and fall through to the lowest
free seat.

### The turn

Each seat walks this cycle **at its own pace**, and the two never interact. There is no such thing as
"whose turn it is".

| That seat's phase | What ends it |
| --- | --- |
| `Setup` | that player drops their pawn → straight into their own `Move` |
| `Move` | the **End Turn** key → their `Build`, or a capture → their own next `Move` |
| `Build` | the last owed tile placed, or nowhere legal left to build → their own next `Move` |
| `GameOver` | `New Game`. Written into **both** seats at once |

Deliberate departures from `stepsRules.md`, decided in `bugFixesStairsGame.md` §1 and §9 — these are
choices, not readings, and the rules doc carries a note saying so:

- **There is no turn order.** Either player may move, end their move, build or place their pawn at
  any moment, including while the other is mid-turn. A quick player can take three turns while the
  other thinks.
- **A capture no longer hands the board over.** It ends the capturing player's turn, which now means
  their *own* next `Move` starts at once, with fresh momentum and no build.
- **Setup has no order** and does not have to be finished by both players before either can play.
  Seat 0 can be four turns in before seat 1 puts a pawn down; on a bare board the opening is pure
  building anyway.
- **A solo player can play the whole game.** Before this, one player alone placed their pawn and the
  board went permanently inert — nothing left `Setup` until *both* pawns were down.

Everything else is enforced exactly as before, per seat, by the same `StairsMoveRules` calls. The two
new races are both safe by construction: the server re-runs the rule for every request and the client
predicts nothing, so the loser of a same-tick build simply keeps their tile and still owes it.

Readings taken where `stepsRules.md` is ambiguous:

- **A capture is exactly one level down.** The rules say "from an adjacent space that is at least one
  level higher", which under an exact-one-level movement rule can only ever be exactly one higher, so
  the two collapse into the same test.
- **A capture takes the whole tower** and the pawn lands on the bare cell it just cleared. The turn
  ends immediately with no build, because the build clause is "if you do not capture".
- **Tiles owed = spaces moved, at least one** — the Minimum Rule — and clamped to what is left in the
  supply. On a bare board nothing is one level away from anything, so the first several turns are
  pure building; that is the rules working, not a bug.
- **No height cap.** Nothing in the rules gives one, a tower can only be as tall as the 40 tiles its
  owner started with, and a very tall tower is self-defeating: it can only be climbed a level at a
  time.
- Two escape hatches so a turn can always finish: `BeginBuild` passes the turn when there is nowhere
  legal left to build, and `RequestPlaceStepServerRpc` re-checks after every tile. Both are
  effectively unreachable on a 64-cell board — which is exactly how a room ends up waiting for ever
  on a placement that can never be made.

`winner` is `StairsConst.NoSeat` both before the game ends *and* for a draw. **`IsGameOver` is what
disambiguates**: `NoSeat` with both seats in `GameOver` is the supply-exhaustion tie.

## The board grid — derived, not authored

Nothing in `StairsGame.unity` carries a cell index. `StairsBoard` reads
`World Root > Board > Cells`, sorts the rows by z and each row's cells by x, and hands out cell 0 at
the corner nearest seat 0 through cell 63 at the far corner.

**Sibling order is not grid order.** The eight cells in every row are named
`Cube, Cube (4), Cube (1), Cube (5), Cube (2), Cube (6), Cube (3), Cube (7)` and sit in the Hierarchy
in that order — the columns interleave. Reading `GetChild(i)` as column `i` gives a shuffled board
that still looks plausible in play, so this sorts by position and never by index. Chasms does the
opposite and takes sibling order as the plateau index; that is a different board, authored
differently, and the two must not be assumed to match.

- Everything is measured in **`Stairs Root`'s local space**, which is what makes it invariant under
  the world grab — nothing is ever re-baked when the board moves, turns or resizes.
- A cell's surface comes from the renderer's **`localBounds`** pushed through the cell's own
  transform, never from `Renderer.bounds`: that is a world-space AABB and it grows the moment
  somebody yaws the board.
- Step thickness is **measured from `Step1.prefab`** (`localScale.y` × the mesh height, 0.03), not
  written down. The prefab is the thing somebody will edit.
- Bare squares are targeted by **where the ray landed, not by what it hit**: `ResolveHit` looks for a
  `StairsPieceTag` on the hit collider's parents and, finding none, maps the hit *point* onto the
  lattice. The 64 cells do each carry a `BoxCollider` — they are untagged scenery and are never
  resolved off the hit itself — so what **`Board`'s collider** actually catches is the 1 cm gaps
  between them and anything overhanging the edge. `Bake` logs an error if it goes missing; the
  wording there overstates the damage, since a ray landing squarely on a bare cell still resolves.
- Rounding to the nearest lattice point rather than testing each cell's footprint snaps the 1 cm gaps
  between cells to the nearer of the two, so a drag never dies in the cracks.

**Select `World Root > Stairs Root` in the Scene view before changing the board.** The gizmo draws a
line from cell 0 to cell 63 in index order; it should snake row by row from the seat 0 corner. A
yellow sphere marks each console.

## The consoles

One per seat, derived from the grid rather than authored, at `ConsolePitches` (1.7) grid pitches
beyond the outermost row — about 1.30 m from the board centre at scale 1, clear of the wall at 1.05
and 0.7 m in front of a player standing on the 2 m `PlayerRing`. A console's **+Z faces the board**,
so its +X is that player's right, and the layout constants at the top of `StairsView` are the tuning
knobs for the whole thing:

```
   supply 4x10        pawn   captured        End Turn
  [##][##][##][##]     (o)      [#]          [======]
  -0.84 .. -0.12      +0.12    +0.40           +0.80
```

Which edge is a **board-local** fact keyed off the seat number, not off where the player is standing:
it has to be the same on every headset, and the console turns with the board under the world grab
because it is part of the board.

- The supply is **4 stacks of 10** — tall enough to point at, short enough not to hide the board, and
  it empties one stack at a time from the last.
- The captured pile is drawn in the **opponent's** colour, because half the point of a pile in front
  of you is that everyone can see whose tiles they were. Only the first `CapturedVisualCap` (20) are
  drawn; the count in the status line is the truth.
- The **End Turn** key exists only while it can be pressed — `SetActive(false)` otherwise — so it is
  never a target that does nothing, and it appears the moment a player's move begins.
- The status line is two rows of plain **ASCII**. These labels are code-built TextMeshPro on TMP's
  default LiberationSans SDF atlas, which is generated over ASCII only; a typographic dash renders as
  a hollow box rather than failing.

## Interaction — `StairsSelection`

Two idioms, and which applies is decided by the **piece's role**, not by the button, so they can never
be ambiguous:

| | Where | How |
| --- | --- | --- |
| **Drag** | your pawn in `Setup`, a supply step in `Build`, and **a tile you have already laid, at any time** | hover (it lights up) → hold the right trigger → a ghost follows the pointer, snapping to the middle of whichever cell the beam is on → release over a legal cell |
| **Click** | your pawn once it is on the board, and the End Turn key | trigger **down and up on the same thing**, so sliding off cancels — the same contract every key in the project has |

**Re-laying a placed tile** (`IsLegalStepLift` + `RequestMoveStepServerRpc`) is the one affordance
that is not in `stepsRules.md` — it exists to fix a misplacement, and it is deliberately narrow: your
own tiles only, the **top** of the stack only, never one with a pawn standing on it, and it costs
nothing (no supply spent or returned, no tile owed, no phase gate). Where it may land is plain
`IsLegalBuild`, so a re-laid tile can never end up on the opponent's tower and "a tower has exactly
one owner" still holds. `CanDrag` also refuses while a pawn is selected, so the two idioms cannot
fight over the same trigger press.

> The rule lives in `StairsMoveRules` and not in `StairsGame` for the reason that file's header
> gives: one implementation, so the client's highlight and the server's answer cannot disagree.
> `StairsGame.CanLiftStep` is the client's cheap "may I?", and it fills its **own** scratch `askView`
> rather than touching `serverView`.

Clicking your own pawn selects it and lights its legal moves; clicking one of those takes it, and the
pawn **stays selected**, because a turn is usually several steps and the momentum rule makes the next
legal set different. `B` cancels. Pressing the pawn again lets it go.

- The legal set is **recomputed every frame** rather than cached against a dirty flag. It is one pass
  over 64 cells and at most eight rule checks, it only runs while something is selected or in hand,
  and it removes the whole class of bug where the lit squares are one server message out of date.
- Order **25**, matching `PlateauSelection` and `ControlListener`: it consumes the hit `PointerBeam`
  (24) produced this frame, after `StairsView` (20) reconciled the objects it lights.
- It stands down on the same three conditions those two do — menu open, `WorldGrab.IsActive`, **or
  either grip merely held**. The grab only goes active on *both* grips, so without that last check a
  player squeezing one grip in preparation still has a live selection beam.
- The server can end a player's turn out from under them (a capture does exactly that), so
  `StillValid` drops a selection or a drag that is no longer allowed before it can be acted on.
- The **drag ghost is cloned from the live piece**, not from its prefab, so it is exactly what the
  player grabbed — seat colour included. Its colliders come off *and* every one of its objects goes
  on layer **Ignore Raycast**, because the ghost hangs on the end of the very ray that decides where
  it goes: left solid it would sit in front of the board and freeze the drag the instant it started.
  (`Destroy` is deferred to the end of the frame, so the layer is what actually protects the ray.)
- Targeting goes through **`PointerBeam`, not `pointerControl`** — `pointerControl` is a 2 m trigger
  capsule tracking a single target with no distance sorting, which is fine for a dozen menu keys and
  useless for 64 cells and up to 80 steps. The pointer stays live outside the menu because
  **`keepPointerAlwaysOn` is set on `StairsModule.asset`**, applied on `activeSceneChanged` so it
  holds from the first frame of the scene.

## Highlighting

`StairsTint`, a `MaterialPropertyBlock` per object — **not** `keyInfo`'s material swap, which uses
the *instantiating* `Renderer.material` accessor: harmless on three menu keys, 64 material instances
and 64 leaks on a board of cells. A tint also composes with what is there, so a highlighted step
still shows whose tiles they are.

It is a deliberate **second copy** of Chasms' `PlateauTint` rather than a shared component:
`MRBoardGame.Stairs` cannot see `MRBoardGame.Plateau` — that boundary is the point — and lifting the
component into Shared would mean editing Chasms. It is the leaner half: Stairs needs no per-renderer
base-colour override, because `Step1`/`Step2` and `Player1`/`Player2` already carry the two seats'
colours as authored materials, and a pawn gets its seat's material by pointing `sharedMaterial` at
the other asset.

What lights up: legal destinations green, a **capture red** (worth telling apart from an ordinary
step down), the selected pawn yellow, and whatever the pointer is resting on white — applied last, so
pointing at one of several lit squares says which one the trigger would take. `HighlightTarget` lights
the **top step of a tower** rather than the cell under it, which would be invisible.

## The height labels

The number on top of a tower is not in `stepsRules.md` — a stack of identical tiles is unreadable at
a glance across a real table.

**The number is the tile's own authored `Height` child**, switched on and set once when the tile is
built, to that tile's **1-based level in its tower** — so the top of a stack always reads as the
stack's height and every tile below it is buried inside the one above. A separate `Labels` root with
one clone per cell used to do this; it is gone, along with `labelTemplate`, `labelColors` and
`UpdateTowerLabel`.

Set-once is enough, and this is the part worth knowing: a tower's steps are only ever appended to or
removed from the **end**, and an owner change clears the whole list and rebuilds it, so a tile's
index never changes underneath it. Re-laying a tile destroys the source tower's top step and builds a
new one on the destination; a capture empties a tower outright. No path leaves a stale number.

> The old reason for cloning was that Unity **shears** a rotated child of a non-uniformly scaled
> parent. It does not apply here: `Step1`/`Step2` are `(0.2, 0.03, 0.2)`, X and Z are *equal*, and the
> label lies in the XZ plane — so any yaw about Y maps its two in-plane axes onto two equally scaled
> ones. **The moment those two scales differ, the shear is back** and the label has to stop turning or
> move off the tile again. (The End Turn key's label is still a *sibling* of its slab for exactly this
> reason: the slab is `(0.34, 0.02, 0.16)`.)

`StairsView.LateUpdate` keeps only the **top** tile's label per cell (`towerTopLabel`, refreshed at
the end of `ReconcileTower`) and yaws it so the **local** player reads it the right way up: two
players stand on opposite sides of a real table, so a fixed orientation is upside down for one of
them. Local and deliberately not networked, like the passthrough toggle and Chasms' billboarded
counts.

**TMP is read from its −Z side** — the reader looks *along* the text's own forward, which is why the
standard billboard is `forward = camera.forward` and why `LookAt(camera)` famously mirrors text. A
label lying flat therefore wants its forward pointing at the **floor**. That is `Euler(90, 0, 0)`
(both step prefabs, and the End Turn key), and `LookRotation(-up, away)` in billboard form. `-90` and
`LookRotation(up, away)` are the mirrored versions of the same thing. See
[`bugFixesStairsGame.md`](../../../docs/bugFixesStairsGame.md) §2.

## Load-bearing names

Resolved at runtime by exact string. Renaming any of these compiles fine and fails in the headset —
see [Conventions that break silently](../../../CLAUDE.md#conventions-that-break-silently) for the
project-wide list.

`Stairs Root` · `Cells` · `Board` · `Height` (the TMP child on both step prefabs)

`StairsView` builds and then re-finds nothing by name, so the objects it creates — `Pieces`,
`Console 0`/`1`, `Supply`, `Captured`, `Home`, `Status`, `End Turn`, `Slab`, `Label` — are for
reading the Hierarchy, not contracts.

## Known rough edges

- **A game in progress is lost on a scene switch**, because the state is in-scene. Leaving Stairs and
  coming back deals a new board. Moving `StairsGame` onto `Room Anchor.prefab` the way `PlateauGame`
  rides it would fix that, at the cost of every other game carrying it.
- **Spectators are untested.** The null-tolerance is written — `LocalSeat()` answers `NoSeat` and
  `StairsSelection` stands down entirely — but nobody has had a third player in the room.
- **A seat is never released**, so a room whose two players both leave cannot be played by anybody
  else without a `New Game`, and even that keeps the seating. Deliberate; see Seats above.
- There is no way to concede. Nobody waits on anybody any more, so a game no longer *stalls* when a
  player leaves — but neither win condition can be reached against an empty seat either, so it just
  never ends.
- `CapturedVisualCap` means a capture of a tower taller than 20 shows fewer tiles than the count says.
- **Tower numbers render mirrored.** `StairsView.LateUpdate` still billboards with
  `LookRotation(up, away)` where the rule above says `-up`. The prefabs and the End Turn key were
  fixed; this one line was not. One-line fix, `StairsView.cs:711`.
- **A tile being re-laid is drawn twice** — once in hand as the ghost, once still sitting on its
  tower, because the local hide (`StairsView.SetLiftedCell`, `bugFixesStairsGame.md` §3.3) was never
  built. Harmless but confusing, and it also means the ghost sits one level too high over its own
  source square.
- **The tiles a player owes are not lifted out of their supply** — `bugFixesStairsGame.md` §6 is
  designed but unbuilt. The count is in the status line and nowhere else.
