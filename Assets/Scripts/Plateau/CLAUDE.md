# Plateau — `ChasmGame`

Menu key `Chasms`. All of it lives in this folder. Rules: [`plateauRules.md`](../../../docs/plateauRules.md).

Read the root [`CLAUDE.md`](../../../CLAUDE.md) and [`../CLAUDE.md`](../CLAUDE.md) (shared systems)
for anything outside this folder.

## The Plateau game

The first slice of `plateauRules.md`: starting forces, and moving pieces around the board. **Turns,
harvesting, buying and win conditions are not implemented** — any player may move their own pieces
at any time. The *reachability* half of every movement rule is enforced; the once-per-turn cap is
not.

There are **six `PieceKind`s** (`PlateauTypes.cs`): `Bridge` 0, `Troop` 1, `Parshendi` 2,
`Shardbearer` 3, `Gemheart` 4, `Chasmfiend` 5. The last two are **neutral** — owned by
`PlateauConst.NeutralSeat` rather than by a player, so they get no owner-colour disc
(`PlateauPalette.DiscFor` returns the authored alpha-0.035 white for any out-of-range seat) and no
movement rules. They are placed and removed by hand, on whichever plateau the player has selected,
which is what makes it possible to play the unimplemented parts of the rules by agreement around the
table.

All of it lives in `Assets/Scripts/Plateau/`. A subfolder with no `.asmdef` still compiles into
`Assembly-CSharp`, so this changes nothing structurally.

### A piece is a stack

One replicated `PieceStack` per **(plateau, owner, kind)** with a count — which is what
`plateauRules.md:20` describes and what the `Count` child on each piece prefab is for. Four bytes;
four players start the game at 16 stacks.

The GameObjects are **local visuals rebuilt from replicated state**. No `NetworkObject` per piece,
so `DefaultNetworkPrefabs.asset` is untouched and 48 stacks are not 48 spawn messages. Owner is
`PlayerControls.spawnSlot`, the only stable per-player index in the project; it is also the colour
index (`PlateauPalette`).

### Where the state lives

`PlateauGame` is a second `NetworkBehaviour` on **`Room Anchor.prefab`**, beside `RoomAnchor`. That
object is already spawned exactly once and already survives every `LoadSceneMode.Single` switch, and
`RoomAnchor`'s own comment already frames it as "the room-wide facts about a session". A second
network prefab would mean editing `DefaultNetworkPrefabs.asset`, and with `ForceSamePrefabs: 1` a
stale list is a hard connection failure with a generic error.

Everything on it is server-written. Its nine `ServerRpc`s are all `RequireOwnership = false`
(`RequestMove`, `RequestPlaceBridge`, `RequestMoveBridge`, `RequestAddPiece`, `RequestRemovePiece`,
`RequestAddNeutralPiece`, `RequestRemoveNeutralPiece`, `RequestAddScore`, `RequestSubtractScore`,
plus `RequestSpinChooser`), and every one of them validates `p.Receive.SenderClientId` against the
sender's seat, bounds-checks every index and clamps the count. The three movement ones additionally
**re-run the same `PlateauMoveRules` call the client used to draw the highlight**. The client's
highlight is a hint; the server's answer is the rule.

**Mutate the lists in place. Never `Clear()`-and-refill except on a deliberate reset** — a move is
two `NetworkList` deltas, about 16 bytes; a clear-and-refill resends the whole board every move.

**A `NetworkList`'s initial contents arrive in the spawn payload and raise no `OnListChanged`.**
`PlateauPieceView` therefore treats `OnListChanged` as a dirty flag and does a full reconcile,
which is also what makes a late joiner's board appear at all.

### Lifecycle

One server tick at 4 Hz does everything, so nothing depends on ordering:

- **Pressing `Chasms` always resets the board**, even when the room is already in Chasms. That is
  why the reset hangs off `GameSelector.RequestGameServerRpc` → `PlateauGame.HandleGameRequested`
  and not off a scene-load event: `LoadGameScene` deliberately no-ops for the scene you are already
  in, so a load hook would never fire for Chasms → Chasms. `HandleGameRequested` only clears; the
  board may not be loaded yet, so handing out armies is left to the tick.
- **Starting forces** (`plateauRules.md:31-36`): 6 troops, 2 parshendi, 1 shardbearer, 2 bridges, on
  the central plateau.
- **A late joiner gets their own army** and nothing else on the board moves. Implemented as "any
  seat with no pieces gets one", polled rather than hooked on `OnClientConnectedCallback`, because
  `spawnSlot` is assigned later, inside `PlayerControls.OnNetworkSpawn`.
- **A player who leaves keeps their pieces.** `PlayerRing.PickFreeSlot` reuses the slot, so a
  reconnect lands in the same seat and gets them back. The cost is that a different person taking a
  vacated seat inherits that army.

### The board graph — derived, not authored

Nothing in the scene carries adjacency: no ids, no neighbour lists, no ScriptableObject. The 81
`Bridge Spots` bars **are** the "faint line between two plateaus" of `plateauRules.md:11`, so
`PlateauBoard` derives the graph from their geometry at `Awake`. Derived rather than baked to an
asset because the board is still being hand-authored and a bake step would need re-running after
every edit.

- **Plateau index = sibling order under `Plateaus`.** Do not reorder those children.
- Each bar's endpoints are `cylinder.TransformPoint(0, ±1, 0)` — Unity's cylinder spans ±1 on local
  Y. Use the **`Cylinder` child's** transform, never the spot root's: the child is offset inside the
  prefab, so the root is not the bar's midpoint.
- Endpoints resolve to a plateau by an **elliptical, radius-normalised** score,
  `((qx−cx)/rx)² + ((qz−cz)/rz)²`. Raw distance mis-assigns short bars beside large plateaus, and
  footprints here vary fourfold in x and z *independently*.
- A pair may hold up to two bars as independent edges; a third would still collapse. Six bars
  around the central plateau are re-authored copies of the originals, and both of each pair are
  kept — each getting its own edge index and a runtime-added `PlateauEdgeTag` (the same
  added-at-bake-time contract as `PlateauTag`) — so two bridges can occupy that connection at once,
  one per bar. Every other pair has exactly one bar and so still yields exactly one edge.
- Everything is measured in **`World Root` local space**, which is what makes it invariant under the
  world grab — nothing is ever re-baked when the board moves or resizes.

**The server publishes the edge list; clients do not use their own.** The bake turns float geometry
into integer indices through an argmin, and an Editor x64 host breaking a near-tie differently from
a Quest ARM64 client would leave one of them highlighting destinations the server refuses forever,
with no error. 162 bytes buys that away. Geometry stays local, where sub-millimetre disagreement is
invisible; `graphHash` catches a mixed build.

**Select `World Root > Plateau Game` in the Scene view before changing anything here.** The gizmo
draws a green line for every derived connection and a red sphere on every bar it could not place,
and `Bake and Report` on the context menu logs the same thing. A mis-authored bar has to be visible,
not buried in a log.

### Movement — `PlateauMoveRules`

One place, used by the client to highlight and by the server to validate, so the two cannot disagree.
`adj(v)` is any faint line; `bridged_s` needs one of *that player's* bridges on it.

| Piece | Search |
| --- | --- |
| Troop | BFS over `(plateau, bridges spent ≤ 2)`, crossing only `bridged_s` |
| Parshendi | BFS over `(plateau, jump spent)`: unlimited `bridged_s`, plus one `adj` hop |
| Shardbearer | BFS over `(plateau, bridges ≤ 2, jump ≤ 1)`, both moves, any order |
| Bridge (from reserve) | exactly one end inside the closure of the **central plateau** over the player's own bridges, the other outside, and no bridge of anyone's already there — EXCEPT the free twin bar of a pair this player already bridged, both of whose ends now read as inside (`PlateauMoveRules.HasOwnBridgedTwin`) |
| Bridge (already laid) | the same, but the closure is seeded from **that bridge's own two ends**, so it can only be re-laid around the plateau system it is touching. Its own pair is excluded |

**At game start troops have zero legal destinations** — nobody has laid a bridge yet. That is the
rules working, not a bug, which is why a selected troop with nowhere to go turns its count **red**
instead of doing nothing. Parshendi and shardbearers can jump to the six plateaus around the centre.

Readings taken where the rules are ambiguous, all commented at their use site: bridges always mean
*your own*; shardbearer's "two bridges" is *up to* two; the jump may be taken at any point in the
move; the bridge network is seeded with the central plateau **for placement from reserve** (without
that seed no first bridge could ever be placed and the game deadlocks) and with the moving bridge's
**own two ends for a move** (a bridge is local to the plateau system it is touching — see
[`bridgeMovementUpdate.md`](../../../docs/bridgeMovementUpdate.md)); one bridge per gap regardless of owner.
`PlateauMoveRules.View.movingEdge` is the single switch between the two, and it does *not* lift the
bridge out of the graph — the board is read as it stands.

**A bridge is targeted by gap, unlike everything else.** Both bridge RPCs
(`RequestPlaceBridgeServerRpc`, `RequestMoveBridgeServerRpc`) carry an **edge index**, not a
destination plateau: with a bridge's local system possibly in two detached halves, one plateau can
be adjacent to it through two different free bars and a server-side re-derivation would sometimes
pick the bar the player did not click. `PlateauSelection` resolves the gap client-side — from the
bar that was hit, or via `TryResolveBridgeEdge` when the click landed on bare plateau — and the
server re-checks that exact gap with `PlateauMoveRules.IsLegalBridgeEdge`, the same predicate the
client highlighted from. While a bridge is selected every candidate bar is faintly tinted, and a hit
on the bar itself resolves to the plateau it names (`PlateauSelection.ResolveBridgeSpotPlateau`, via
each bar's `PlateauEdgeTag`) — the tint is a legitimate click target, not just a hint to aim past.
`RequestMoveServerRpc` now refuses `PieceKind.Bridge` outright.

### Interaction — `PlateauSelection`

Idle → point at one of **your own** pieces (others do not highlight) → trigger down and up on it →
selected. Then the count and the destination are live at the same time: **right joystick up/down**
changes the count (1 to that stack's own size), the legal plateaus glow, and a trigger on one sends
the move. `B` cancels; pressing the selected piece again lets it go; pressing a different own piece
switches to it.

- **Vertical, not horizontal.** `rightJoystick.x`'s Editor keyboard fallback is bound to `A`/`D`
  (`InputManager.asset`), and `A` is `BoardAnchor.RequestReAlign`. The vertical axis falls back to
  the arrow keys and `W`/`S`, which nothing else reads.
- The whole thing stands down when the menu is open, when `WorldGrab.IsActive`, **or when either
  grip is merely held** — the grab only goes active on both grips, so without that last check a
  player squeezing one grip in preparation still has a live selection beam.
- **No local prediction.** This project only predicts state a client holds an exclusive
  server-granted lock on (`RoomAnchor.worldHolder`); there is no such lock for a move.

### The spawn menu — `PlateauSpawnMenu`

Each player's personal piece menu, standing in for `plateauRules.md`'s "Buying Pieces" with a free,
uncapped `+`/`−` until buying is implemented. **Holding the LEFT grip by itself opens it**; letting
go, adding the right grip, or opening `Menu1` closes it again. It hangs off `SpawnMenu` under the
shared `Left Hand` and is tinted with the local player's own seat colour every frame it is open,
purely so a player can tell at a glance that the menu on their wrist is theirs — local, never
networked, like the passthrough toggle.

Keys are dispatched by `keyInfo.keyName`, the same contract as `MenuControl.HandleKey`, and an
unwired key is inert and logs. Three groups, and they do **not** all target the same plateau:

| Keys | Target | RPC |
| --- | --- | --- |
| `Add`/`Remove Troop`, `Parshendi`, `Shardbearer`, `Bridge` | the **central** plateau, sender's own seat | `RequestAddPieceServerRpc` / `RequestRemovePieceServerRpc` |
| `Add`/`Remove Gemheart`, `Chasmfiend` | whichever plateau the sender has **selected** (`PlateauSelection.TryGetSelectedPlateau`), neutral seat | `RequestAddNeutralPieceServerRpc` / `RequestRemoveNeutralPieceServerRpc` |
| `Add to Score`, `Subtract From Score` | the sender's own seat's `gemheartScores` entry | `RequestAddScoreServerRpc` / `RequestSubtractScoreServerRpc` |

**Unlike `Menu1` this never drops the board selection while it is opening or open** —
`PlateauSelection.Update()` special-cases `IsOpenOrOpening` for exactly this, because the `−` and
neutral keys act on whatever the player already has selected. That flag, not the execution order,
is what protects the selection: the 0.15 s open debounce leaves the menu reading "closed" for about
ten frames after the grip goes down.

`gemheartScores` is a `NetworkList<byte>` on `PlateauGame`, indexed by seat and grown lazily —
`plateauRules.md`'s "each player can see how many gemhearts they currently hold", adjusted by hand
because harvesting is not implemented. It renders on the `ScoreTag` label under every *other*
player's `TagsRoot`; see [Avatar replication](../CLAUDE.md#avatar-replication).

### The Plateau Chooser

`Plateau Chooser`, a scene-root object in `ChasmGame` beside `Plateau Controller` — **a novelty
prop, not one of the 41 plateaus `PlateauBoard` tracks.** Click it the same way a piece is selected
(`PlateauSelection`'s point-from-a-distance idiom, *not* `pointerControl`'s physical-touch one) and
it shuffles through the three per-plateau material tiers, slowing over about two seconds before
landing on one, weighted **15% / 35% / 50%** — the same `15/35/50.mat` materials 40 of the 41 real
plateaus carry as a per-instance override, and the same percentages `plateauRules.md` gives for "the
percent chance of that color being chosen". Independently, its Chasmfiend child — a serialized
reference (`chasmfiendObject`), not a `Find` by name, and inactive in the authored scene — has a
**30%** chance of waking up.

All of that is authoritative on `PlateauGame` (`chooserSpinEpoch`, `chooserResultEpoch`,
`chooserMaterialIndex`, `chooserChasmfiendActive`), like every other piece of board state;
`PlateauChooser` only asks for a spin and plays a local, latency-tolerant flourish while the real
answer is in flight. It is a plain `MonoBehaviour` with no `NetworkObject`, the same as
`PlateauSelection`, `PlateauPieceView` and `PlateauSpawnMenu` — all four talk to
`PlateauGame.Instance` rather than carrying wire state of their own.

**A material swap here is correct**, and it is the one place in Chasms where that is true: this is a
single unique object, not 41 plateaus carrying up to 36 owner-coloured pieces each, so `keyInfo`'s
instantiating `.material` idiom costs one instance rather than 41 leaks. See
[Highlighting](#highlighting--materialpropertyblock-not-keyinfos-material-swap) for the other case.

### The pointer

`pointerControl` is untouched — it is still a 2 m trigger capsule filtering on the tag `key`, which
is fine for a dozen menu keys and useless for 41 plateaus (one target, no distance sorting, and
trigger callbacks need a `Rigidbody` on the other collider, which `Key.prefab` has and plateaus do
not). Board targeting is a separate raycast in **`PointerBeam`**, on the same object:

- **`Physics.SyncTransforms()` first.** `m_AutoSyncTransforms` is `0` in this project and
  `RoomContent` moves `World Root` during `Update`, so without it the ray hits where the board was
  at the last `FixedUpdate`.
- Ray from the beam's near end along the Pointer's local +Y, then a 1 cm `SphereCast` as a fallback
  — at the smallest board scale a soldier is about a 1° target.
- `QueryTriggerInteraction.Ignore` drops the pointer's own capsule and the grabber volumes. Menu
  keys are **not** triggers, so the ray does hit them; that only shortens the beam.
- `Visible Pointer` and `Dot` are on layer 2, **Ignore Raycast** — they sit directly on the ray, and
  `Physics.DefaultRaycastLayers` already excludes that layer, so this needs no code and protects
  every other raycast too.
- `PointerBeam` writes the beam length **absolutely** every frame, which also masks
  `pointerControl.OnTriggerStay`'s compounding beam maths.
- The pointer is normally switched on only while the menu is open. `MenuControl.keepPointerAlwaysOn`
  leaves it on during play — `false` on the shared `Menu Manager` instance, opted into by
  `PlateauSpawnMenu.Bind()` via `MenuControl.SetKeepPointerAlwaysOn(true)` (ChasmGame-only). See
  [The persistent rig](../CLAUDE.md#the-persistent-rig).

### Highlighting — `MaterialPropertyBlock`, not `keyInfo`'s material swap

Four reasons, and the first one decides it: every piece needs its owner's colour anyway, and twelve
seats × three prefabs would be 36 hand-authored materials. `ClearWhite.mat` on the `Cube` disc is
already alpha-blended, so a colour written into a property block is all it takes. Beyond that,
`keyInfo` uses the *instantiating* `.material` accessor — harmless on three menu keys, 41 material
instances and 41 leaks on the plateaus — and 40 of the 41 plateaus carry a per-instance
`15/35/50.mat` override that a swap would destroy. A tint composes with what is there; a swap
replaces it. `PlateauTint` holds one reused block and clears with `SetPropertyBlock(null)` so an
untinted plateau rejoins the SRP batcher.

The count label is TMP `.color` — a vertex colour, no material instance, no batch break.

### Layout

Pieces hang under an **unscaled `World Root > Pieces`**, never under `Board` (2.5, 1, 2.5) or
`Plateaus` (2, 0.02, 2), both non-uniform. Slots are handed out by sorting a plateau's stacks by
`(seat, kind)` — integers over a byte-identical replicated set, so every headset lays them out the
same way — and placed on a 48-point phyllotaxis spiral fitted to `RadiusX`/`RadiusZ` **separately**,
because plateau footprints vary fourfold on each axis independently.

The `Count` labels are **billboarded**: up to twelve players stand in a ring, and the authored text
faces one direction, so half of them would read every number backwards. Their scale is compensated
against the content scale (clamped 1–3×) so shrinking the board does not shrink the numbers out of
legibility — local only, deliberately not networked, like the passthrough toggle.

Nothing geometric is hard-coded. `Plateau.prefab`'s collider and mesh are on a nested child that is
offset and rotated 180°, so **the `Plateau` root's position is not the plateau's centre** — every
centre, radius and surface height comes from `Renderer.localBounds`, and every lookup off a raycast
hit is `GetComponentInParent`.
