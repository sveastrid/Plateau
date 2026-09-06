# BASH — `BashGame`

Menu key `BASH`. All of it lives in this folder. Rules: [`BASHRules.md`](../../../docs/BASHRules.md).

Read the root [`CLAUDE.md`](../../../CLAUDE.md) and [`../CLAUDE.md`](../CLAUDE.md) (shared systems)
for anything outside this folder.

## The BASH game

Ported from `D:\Unity_Stuff\BASH_U6`; the plan and its corrections are
[`BASHUpdate.md`](../../../docs/BASHUpdate.md). All of it lives in `Assets/Scripts/Bash/`, another `.asmdef`-less
subfolder that compiles into `Assembly-CSharp`.

Four players sit around a 3 m square of water. Each owns a **base** carrying four gamepieces —
boat (0), plane (1), sub (2), helicopter (3). Point at one of yours with the right trigger to select
it. What happens next depends on which piece it is, and **the four split into halves two different
ways** — `BashRoot.UsesArtillery` and `BashRoot.IsSurfaceCraft`, written out as named predicates one
line apart precisely because as bare integer comparisons they look like a mistake and invite being
"tidied" into agreement:

| | boat | plane | sub | helicopter |
| --- | --- | --- | --- | --- |
| `UsesArtillery` — lobs a shell before moving | ✔ | ✔ | | |
| `IsSurfaceCraft` — an island kills it | ✔ | | ✔ | |

- **sub, helicopter** — the piece spins on the spot at `SpinDegreesPerSecond` (90°/s, a full sweep
  every four seconds), carrying its coloured cannon dot round with it. The left trigger freezes the
  heading and fires. **Aiming is timing, not steering.** The line is lethal, and the piece teleports
  to the end of it.
- **boat, plane** — two trigger presses. First an **artillery arc**, aimed with the joystick, lobbed
  over the water and destroying everything within `blastRadius` of where it lands — anyone's pieces,
  **including the shooter's own**. Then the same spin-aimed line as above, which carries the piece
  but harms nobody.

The heading is derived from `NetworkManager.ServerTime` and a single `spinStartTime` write rather
than streamed per tick, so every headset puts the dot in the same place.

**Where a piece ends up is an RPC that names the piece — `NetworkBaseControl.MoveGamepiece` — not a
`NetworkVariable`.** It was an owner-written `activePos` whose `OnValueChanged` moved
`activeGamepiece`, and that cannot work: the frame that publishes the pose is the frame that
deselects, and Netcode does not deliver those two together. An RPC is queued the moment it is
called and a `NetworkVariable` delta only at the end of the tick, so `SetActiveGamepieceClientRpc(-1)`
reliably arrived **first** and every client but the shooter applied the new position with
`activeGamepiece` already `null`. The trail appeared and the piece stayed on its old square until its
owner selected it again — which republished a pose at a moment when something was selected to
receive it. Naming the piece in the message removes the dependency on selection order altogether.
`activeRot` stays a `NetworkVariable` because the spin is *derived* from it every frame on every
client, and it is only ever written while a piece is selected.

`ControlListener.Phase` (`Idle` → `Aiming` → `Lobbing` → `Spinning` → `Firing`) holds all of this.
**The phase lives on the controller, not on the piece**, which is what makes cancelling behave: a
boat that loses its lob to an opened menu returns to `Aiming` and may lob again, while one that
loses its *move* returns to `Spinning` — it has already spent its shell.

While a movement line is drawn it is a live collider, and what it touches decides what happens:

| Tag hit | Effect |
| --- | --- |
| `gamepiece` | destroyed — but **only if the line `killsPieces`**, i.e. the spin-aimed shot of a sub or helicopter. A boat's or plane's post-lob move is harmless |
| `obstacle` | walls and base pads: the shot ends there and **your** piece dies. Not gated — a harmless move still stops on a wall |
| `island` | `IsSurfaceCraft` (boat, sub) die; plane and helicopter fly over. Not gated either, or a plane's harmless move would be a way to park inside an island |

**`LineControls` owns those rules; it is no longer the only thing that evaluates them.** A trigger
fires on a physics step — 0.02 s, against a headset rendering at 72–90 and with
`m_AutoSyncTransforms` `0` — and late in a shot the tip covers about 0.2 board units per frame
against a wall 0.04 thick. So a shot could cross a wall and be released between two steps, and
nothing would ever look: `EndCannonLine` clears `checkForCollisions` on the way out. That is a
submarine driving into a wall and living. `ControlListener.GrowTip` therefore sweeps each segment
with a raycast **in the frame it is drawn** and asks `LineControls.Blocks` / `LineControls.Hit` — the
same two rules — so the trail also stops *at* the wall rather than one step past it. Both halves are
idempotent, which is what lets the sweep and the trigger report the same hit without special-casing
each other. `QueryTriggerInteraction.Collide` is mandatory in that sweep: every collider on this
board is a trigger, so the default finds nothing at all, silently.

The turn ends by deselecting the piece, so nothing is left spinning on the board after a player has
acted — but that deselection is the whole of the "turn". Still not turn-based and still no win
condition: any player may fire at any time. That is how BASH already was and the port did not change
it.

### The artillery arc

A fixed **`arcSegments` (24)** points, *not* one per frame like the movement line: `PipeRenderer`
rebuilds the entire mesh from the entire point list on every call, so a list growing by a point per
frame is quadratic work across a shot — and `SpawnNetworkCannonLineServerRpc` sends the array, so 25
points is 300 bytes, bounded for ever, whatever scale the board is at.

- `arcApexRatio` 0.25 is exactly a 45° launch; the launch angle is `atan(4 · apex / range)`.
- `arcMaxApex` 0.35 caps it in board-local units. Uncapped, a full-board lob peaks 0.75 m over the
  table, which in passthrough is at chest height and reads as a wall rather than an arc.
- `minArcRange` 0.02: below it the arc is a single point, and `PipeRenderer.GenerateCylinder`
  `FromToRotation`s the difference between the first two positions — a zero vector, giving a
  degenerate or NaN mesh with no exception. A mis-tapped trigger keeps the shell instead.
- `clampArcToBoard` is on. The hit rule ignores walls, which is the point of lobbing, but taken
  literally a held trigger throws the impact point off the board and into the room behind a player.
- `arcLifetime` 1.5 s, applied to both the local preview and the replicated object, so an arc is
  seen and then goes rather than accumulating.
- `Physics.SyncTransforms()` before the blast overlap, for the same reason `PointerBeam` does it:
  `RoomContent` is still smoothing `World Root` and `m_AutoSyncTransforms` is `0`.

### `Bash Root` is the content frame

**Every networked BASH value is in `World Root > Board > Bash Root`'s local space**, and every
conversion goes through the four helpers on `BashRoot` so there is one place to look when a shot
comes out of the wrong end of a cannon. This is the whole of the port's engineering: BASH was
written for a world that never moved, with its board at world `(0, 0.6, 2)` and plain world-space
`Vector3`s on the wire, and in this project `World Root` moves, turns and rescales continuously
under the two-grip world grab.

- Bases and trails are **spawned unparented, then `NetworkObject.TrySetParent(BashRoot.SpawnParent,
  worldPositionStays: false)`** on the server, so Netcode replicates the parenting and every
  client's objects inherit `World Root`'s pose and scale for free. The local transform is set
  before the reparent, which is what makes those exact numbers survive it.
- `NetworkBaseControl.activeRot` and the pose `MoveGamepiece` sends are board-local, converted back
  on the way out.
- `PipeRenderer` treats its point list as **mesh vertices in the pipe object's own local space**,
  which is why the line objects sit on `Bash Root` at an identity local transform and the points
  are fed in already converted.
- The cannon-speed constants are therefore **board-local units per second**, so a shot crosses the
  same fraction of the board however big the players have made it. That falls out; it is not
  extra work.

The acceptance test for all of it is §11.5 of `BASHUpdate.md`: grab the board with both grips,
move / turn / resize it, then fire. The trail must come out of the cannon and stay on the board.

`Board` is authored at local scale 1 — a true 3 m board, so players on the 2 m `PlayerRing` stand
half a metre clear of the walls. `Bash Root` is a **child** of `Board` rather than a sibling
(which is where `BASHUpdate.md` §7 put it), so that scale is the one knob that resizes water,
walls, islands, bases and trails together. Chasms' board is nearer 4 m across, and
`RoomAnchor.contentPos/Yaw/Scale` survives a `LoadSceneMode.Single` switch, so switching between
the two games leaves the table looking a size different until somebody re-grabs it. Changing that
is a one-number edit on `Board`.

### Seats

`PlayerControls.spawnSlot` — the only stable per-player index in the project, and already the
colour index for Chasms. `ControlListener` tolerates a null `netBaseControl` throughout, because a
player without a base spectates.

**`spawnSlot` is a place on `PlayerRing`'s twelve-slot ring, not a base index, and the two are not
the same number.** `PickFreeSlot` hands out the middle of the widest gap, so the first four players
get slots **0, 6, 3, 9** — treating that as a base index is what gave the second player into a
two-player game no base at all. `SpawnManager.BaseForRingSlot` is the map, and its four entries are
a measurement (they are the four slots a base actually stands at, per `SeatPosition`/`SeatYaw`), not
a convention.

`ServeSeats` is **two passes, and the order between them is the whole point**:

1. **The rule.** A player standing on one of the four ring slots a base stands at gets *that* base,
   whoever else is in the room and in whatever order Netcode enumerates clients.
2. **The fallback**, which only fires for a ring fragmented by mid-game departures — five players,
   the one at slot 0 leaves, and `PickFreeSlot` answers 11 for the next joiner rather than 0. Rather
   than leave base 0 empty while a player has none, hand out the lowest base nobody claimed in pass
   1. Running it *after* pass 1 is what stops a leftover player taking a base somebody else's ring
   slot entitles them to.

A player who got their base from pass 2 is standing somewhere other than behind it, and
`BaseIndexForRingSlot` answers `-1` for them, so their trails clamp to colour 0 — a cosmetic
mismatch confined to that same case. Fixing it properly means the server telling each client which
base it was handed, which is not worth a `NetworkVariable` until spectator seats are tested at all.

BASH's own `NetworkManager.ConnectedClients.Count` scheme is gone: it handed two players the same
base whenever somebody left and somebody else joined. `SpawnManager` **polls at 4 Hz** rather than
hooking `OnClientConnectedCallback`, for the identical reason `PlateauGame` does — `spawnSlot` is
assigned inside `PlayerControls.OnNetworkSpawn`, which can run later. A seat that already has a
base and a new occupant gets `ChangeOwnership`, so a reconnecting player is handed the base they
left rather than a second one; `NetworkBaseControl.OnGainedOwnership` re-wires it to their
`Controls`. The fallback deliberately keeps a client's existing base rather than re-deriving one
each tick — `ConnectedClients` enumerates in insertion order today but nothing promises it, and a
base changing hands every tick would re-run `OnGainedOwnership` four times a second.

### Interaction

`ControlListener` is `[DefaultExecutionOrder(25)]`, matching `PlateauSelection` — after
`RoomContent` (15) and `WorldGrab` (20) — and stands down on exactly the same conditions: menu
open, `WorldGrab.IsActive`, **or either grip merely held**. A shot in progress when any of those
goes true is dropped, not committed.

Piece selection goes through **`pointerControl`, not `PointerBeam`**: BASH's gamepiece colliders
are triggers, and `PointerBeam` raycasts with `QueryTriggerInteraction.Ignore`, so it would never
see them — and relaxing that would make the beam hit its own capsule and both grabber volumes.
The pointer stays live outside the menu because **`keepPointerAlwaysOn` is set on
`BashModule.asset`**, applied on `activeSceneChanged` so it holds from the first frame of the scene.
`ControlListener.Bind()` still calls `MenuControl.SetKeepPointerAlwaysOn(true)`; that is now a
redundant agreement with the module rather than the thing that switches it on.

**BASH's two menu keys are its own.** `Reset Game` and `Random Islands` are declared in
`BashModule.asset`'s `menuActions`, built into the room menu by `MenuControl` only while `BashGame`
is the loaded scene, and dispatched to `BashRoot.InvokeMenuAction`, which resolves `ControlListener`
and `IslandManager` in the active scene. They used to be two `case` blocks in `MenuControl` holding
direct type references to both of those classes, plus a row each in a `KeyScene` dictionary naming
BASH's scene, plus a key authored into `Menu1.prefab` **and** `Menu2.prefab`. Adding a third key is
now one row on the module and one `case` in `BashRoot`, both inside this folder.

### Known rough edges, inherited

- `SpawnNetworkCannonLineServerRpc` sends an **unbounded `Vector3[]` for the movement line**, which
  still grows a point per frame. A long shot is several hundred points, and a bigger board makes
  longer shots. (The artillery arc is *not* affected — it is a fixed 24 segments by construction.)
  If shots stop replicating, cap or simplify the polyline before sending.
- **Movement trails are never despawned except by `Reset Game`**, and each shot leaves both a local
  preview and a replicated `NetworkObject`. Over a long game that grows without bound. Arcs are the
  exception: both copies carry `arcLifetime`.
- Spectator seats are **untested** — the null-tolerance is written but nobody has had a fifth
  player in the room. The pass-2 colour mismatch above is untested for the same reason.
