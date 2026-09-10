# Shared systems — `Assets/Scripts/`

The platform every game sits on: the persistent rig, colocation, the content frame and world
grab, the player ring, avatar replication, menus, passthrough, input and in-headset debugging.

Read the root [`CLAUDE.md`](../../CLAUDE.md) first.

This folder is the **`MRBoardGame.Shared`** assembly. It references no game, and the compiler
enforces that: `Assets/Scripts/Plateau/`, `Assets/Scripts/Bash/` and `Assets/Scripts/Stairs/` carve
themselves out into `MRBoardGame.Plateau`, `MRBoardGame.Bash` and `MRBoardGame.Stairs`, all three
referencing Shared and none referencing each other. Anything here that wants to know something about
a game must go through [`IGameSession`](#games-as-data--assetsscriptsgames). See
[Adding a game](../../CLAUDE.md#adding-a-game).

`HierarchyUtils.FindDescendant` is the shared name-based hierarchy search. It replaced four
identical private copies (`CameraController2`, `WorldGrab`, `PlateauBoard`, `MenuControl`), one of
which BASH was borrowing across the game boundary. It is `HierarchyUtils` and not `Hierarchy`
because Unity 6 added a `Unity.Hierarchy` namespace for the global name to collide with.

## Games as data — `Assets/Scripts/Games/`

Four small types are what let the rest of this folder stop naming games.

| Type | What it is |
| --- | --- |
| `GameModule` | A `ScriptableObject` per game: menu key, scene name, label, rules `TextAsset`, extra menu keys, whether the pointer stays live — **and the store half**: `productId`, `libraryBit`, `displayName`, `blurb`, `thumbnail`, `isPaid`, `metaSku`, `minPlayers`/`maxPlayers`. One asset per game, kept beside the game (`Assets/Games/Bash/BashModule.asset`). Both identities live here so the saved-library key and the on-the-wire bit are authored in one place. |
| `GameCatalog` | The list, at `Assets/Resources/GameCatalog.asset`. Loaded by name because `GameRoutes` is static and `MenuControl` lives on `PersistentRig` — neither has an Inspector slot a scene could fill. `ActiveModule` resolves the loaded scene to its module. |
| `IGameSession` | The only thing shared code is allowed to know about a game: `OnGameSelected`, `InvokeMenuAction`, `AvatarBadgeForSeat`. Doing nothing / returning null is the normal answer. |
| `GameSessionRegistry` | Where games put themselves. Games register in `Awake`/`OnNetworkSpawn` and unregister on the way out. |

**The registry has two lookups and conflating them is a bug.** `ForKey` is for "the room is
switching to this game" — `GameSelector` calls it *before* `LoadScene`, so it cannot be keyed off
the active scene; `PlateauGame` is reachable there only because it rides on the persistent
`Room Anchor` and is registered the whole time. `Active` is for "the game on screen now" — menu
actions and avatar badges — and resolves through the catalog, because in `BashGame` both `BashRoot`
and the still-registered `PlateauGame` are present and "last one to register" would be a coin toss.

`GameRoutes` survives as a thin façade over the catalog (`DefaultScene`, `IsGameKey`, `IsGameScene`,
`TryGetScene`) so its dozen call sites did not have to change. It used to be a `Dictionary` literal
with a `const` pair per game.

This replaced four places where infrastructure named a game: `GameSelector` calling
`PlateauGame.Instance.HandleGameRequested`, `PlayerControls` reading Plateau's gemheart score onto
the shared avatar, and two `MenuControl` cases holding direct type references to `ControlListener`
and `IslandManager`.

## Session flow

1. `OpeningScene` loads. `RelayVivox.Start()` initializes Unity Services and signs in
   anonymously, setting `servicesReady`; a failure here (a headset that came up with no network) is
   caught and logged rather than vanishing into the `async void`. `MicPermissions` requests
   `RECORD_AUDIO`.
2. `LobbyController` (on the `Lobby` root) places two `Panel`s off the camera — **Library** dead
   ahead, **Play** angled to its right — and a `CodePad` over them, and awaits
   `StoreService.InitializeAsync()`. **There is no keyboard**: the 40-key `Keyboard` root, the
   `Text Input` canvas and the character-at-a-time state machine that used to live in
   `GameController.Update()` are all gone. See [Menus, pointer, keys](#menus-pointer-keys).
3. The player presses a Play row. `LobbyController` sets [`RoomOptions`](#rooms-libraries-and-the-store)
   and calls one of two methods on `GameController`, which is now purely the connection controller:
   - **`HostRoom`** → `RelayVivox.CreateRelay()` allocates a Relay slot for **12**, gets a join
     code, `StartHost()`. This client is the **room owner**. A **public** room is then listed with
     `RoomDirectory.PublishAsync` and opens straight into the game it is locked to; a private one
     opens into `GameRoutes.DefaultScene` → `StairsGame`.
   - **`JoinRoom(code)`** → `RelayVivox.JoinRelay()` → `StartClient()`. **No `LoadScene` here on
     purpose** — Netcode synchronizes the joiner into whatever scene the room already has open. A
     bad code throws `RelayServiceException`, caught in `GameController.TryToJoinRelayVivox()`,
     which reports through `ShowJoinFailed`.

   Both entry points still take the `connectInFlight` latch. The other half of that guard — clearing
   `pressedKey` on every press, so a second trigger pull during the seconds before the joiner leaves
   the lobby could not re-run the whole join and invalidate the allocation already connecting — is
   now `Panel`'s, and applies to every panel in the project rather than to one hand-written loop.
4. **Connection approval** decides before the client is synchronized into a scene. `RelayVivox`
   calls `RoomApproval.Prepare` on the last line before `StartHost`/`StartClient`, which switches
   `NetworkConfig.ConnectionApproval` on and loads `ConnectionData` with a `ConnectionPayload` —
   `(playerName, ownedMask, Application.version)`. The server refuses a build mismatch or, in a
   public room, a joiner who does not own the game, with a `Reason` the joiner can read.
5. Vivox joins a group audio channel named after the room code.
6. `NetworkManager.OnServerStarted` fires `BoardAnchor.HandleServerStarted`, which instantiates
   `Room Anchor.prefab` and calls `Spawn(destroyWithScene: false)`. `RoomAnchor.OnNetworkSpawn`
   copies `RoomOptions` onto its two server-written room-kind variables.

**`ConnectionApproval` is a `NetworkConfig` field, and `NetworkConfig` is what the join handshake
hashes** alongside the `ForceSamePrefabs` prefab set. An old build cannot join a new one, with the
same reasonless refusal `bugFixes2.md` §0 documents. It is set from **code**, in one method both
paths call, rather than authored in the scene, so the host and the joiner take the value from the
same line — but that does not help across builds. Reflash both headsets.

The **Network Manager** GameObject carries `NetworkManager`, `UnityTransport`, `RelayVivox`,
`BoardAnchor` and `NetworkProbe`. Netcode marks it `DontDestroyOnLoad`, which is why `BoardAnchor`
and `NetworkProbe` live there: both must outlive the scene switches below.

**A join that fails after Relay accepted it is now visible.** `JoinAllocationAsync` succeeding only
means the REST call worked — the transport handshake and Netcode's own handshake happen afterwards,
and nothing used to watch them, so any failure past that point left the lobby on "Joining room…"
for ever. `GameController` now subscribes `OnClientDisconnectCallback` and `OnTransportFailure`, and
runs a `JoinTimeoutSeconds` (15 s) watchdog; all three land in one `ShowJoinFailed`, which returns
the player to the room-code keyboard and calls `NetworkManager.Shutdown()` so the retry is not
refused by an instance still grinding through `m_MaxConnectAttempts` (60 × 1000 ms). An empty
`DisconnectReason` is reported as a probable build mismatch, because that is exactly what Netcode's
config-hash refusal sends. See [`quest_networking_plan.md`](../../docs/quest_networking_plan.md).

### Switching games

`MenuControl` → `GameSelector.RequestGame(key)` → `RequestGameServerRpc` → the server validates
the key against `GameRoutes` (**never** hand a client string to `LoadScene`) → 
`NetworkManager.SceneManager.LoadScene(name, LoadSceneMode.Single)`. `EnableSceneManagement` is
on, so every client follows and spawned `NetworkObject`s are carried across. **Clients must never
call `UnityEngine.SceneManagement.SceneManager.LoadScene` while a session is running.**

`LoadSceneMode.Single` destroys everything in the outgoing scene that is not itself
`DontDestroyOnLoad` — which, now that `PersistentRig` exists, no longer includes the rig, the
camera, the hands, the Input Reader or the Menu Manager. See
[The persistent rig](#the-persistent-rig). Components still rebind around a switch, and every new
component that caches a scene object should follow the same pattern, even though the object being
re-found is now usually the very same instance as before:

- `PlayerControls.BindToScene()` on `SceneManager.activeSceneChanged` — the `Player` prefab is not
  part of `PersistentRig` (see Surviving objects, below), so this still has real work to do.
- `BoardAnchor.HandleActiveSceneChanged()` clears its cached rig and Input Reader references and
  re-`Find`s them next frame. Now a harmless no-op — same objects, never destroyed — kept so a
  future scene that does not source its rig from `PersistentRig` still degrades safely.
- `CameraController2.AdoptLocalPlayerSlot()` in `Start()`, for the case where the rig comes up
  after the player object.

Surviving objects: the Network Manager (and `BoardAnchor` with it), the `Player` prefabs, the
`Room Anchor` object, the `GameObject` holding the bound `OVRSpatialAnchor` (`DontDestroyOnLoad`,
so it is not re-downloaded and re-localized on every switch) — and now also **`PersistentRig`**:
the rig, camera, hands, Input Reader and Menu Manager, one instance for the life of the app.

## The persistent rig

`Assets/Prefabs/PersistentRig.prefab` — `Directional Light`, `XRRig` (with `Camera Offset`, the
hands, `SpawnMenu`), `Input Reader` and `Menu Manager` all under one root carrying
`PersistentObject`, which self-`DontDestroyOnLoad`s in `Awake`. One instance lives in
`OpeningScene`; no other scene has its own copy of any of it. Before this existed, each game scene
baked its own full copy and `LoadSceneMode.Single` destroyed and rebuilt all of them on every
switch — see [Switching games](#switching-games) for what still runs on that switch and why.

**Resting position.** The prefab's own root transform is `(0, 0, -10)`, matching what
`OpeningScene`'s and `GameScene`'s original local rigs were both hand-placed at (facing the
keyboard / the menu, respectively). `CameraController2.PlaceAtRingSlot` repositions the rig at
runtime for actual game scenes (`GameRoutes.IsGameScene` — `StairsGame`, `ChasmGame` and
`BashGame`, i.e. every `sceneName` in `GameCatalog`), so the baked default only matters for the
two scenes that never call it: `OpeningScene` and the legacy `GameScene`. Do not "fix" this position
to somewhere sensible for a game scene — that breaks the lobby instead.

**`gameObject.scene` is the fixed pseudo-scene `"DontDestroyOnLoad"` on every persisted object,
forever.** Anything that used to read `gameObject.scene.name` to tell a game scene from the lobby
(only `CameraController2.PlaceAtRingSlot` did) has to read `SceneManager.GetActiveScene().name`
instead.

**ChasmGame-specific exceptions**, because the shared prefab's authored defaults came from
`StairsGame`:

- `SpawnMenu` (the left-hand piece-buying menu) lives under the shared `Left Hand` even though only
  `PlateauSpawnMenu` (ChasmGame-only) ever opens it — it has to live somewhere every scene shares,
  and nothing in another scene references it, so its presence there is harmless.
- `MenuControl.keepPointerAlwaysOn` is no longer an authored default at all. `AdoptSceneDefaults`
  overwrites it from the incoming scene's `GameModule.keepPointerAlwaysOn` on every scene change,
  so it is right from the **first frame** of the scene and correct in *both* directions. It used to
  be a sticky field that only ever got set true, by `PlateauSpawnMenu.Bind()` on some later frame —
  so the pointer was dead for the opening frames of ChasmGame, and once ChasmGame had switched it on
  it stayed on in Stairs too. `PlateauSpawnMenu` and `ControlListener` still call
  `SetKeepPointerAlwaysOn(true)`; both are now redundant agreements with their own module.

**The pointer's active state does not survive a scene switch on its own any more.**
`MenuControl.OpenMenu1`/`CloseMenu` toggle it, and `MenuControl.ApplyPointerDefault` resets it to
the incoming scene's default rather than trusting whatever the previous scene left behind. That
reset hangs off **`SceneManager.activeSceneChanged`, not `Start()`** — `Menu Manager` is part of
`PersistentRig` and therefore `DontDestroyOnLoad`, so its `Start()` runs exactly once for the life
of the app, in `OpeningScene`, and could never reset anything for a game scene.

Two consequences that have already caused bugs:

- **`OpeningScene` does have a `MenuControl`** — it arrives with `PersistentRig`. `LobbyController`
  is not the only thing writing the pointer's state there, so the two have to *agree* rather than
  one winning: `LobbyController.Start()` switches the pointer on for its panels, and
  `ApplyPointerDefault` returns `keepPointerAlwaysOn || !GameRoutes.IsGameScene(...)`, which is
  true in the lobby. Unity gives no ordering guarantee between two `Start()` calls, and when these
  two disagreed the pointer came up dead and nothing in the lobby could be pressed. (`GameController`
  held this obligation while the lobby was a keyboard; it no longer touches the pointer at all.)
- **Prefer setting it on the game's `GameModule`.** If a component really must override it mid-scene,
  call `SetKeepPointerAlwaysOn` and never assign the field: `AdoptSceneDefaults` has already run and
  switched the pointer off by the time any scene component's first `Update` opts in, so a bare field
  write leaves the pointer dead until the player opens and closes the menu. The setter applies the
  flag and re-evaluates in one call. `PlateauSelection` reads the flag and does not write it.

`GameController` used to be the one place with direct, non-`Find` serialized references into the rig
(`inputs`, `rh`, `lh`, `pointer`). Those four are **gone**: it no longer reads input at all, and
`LobbyController` resolves the Input Reader by name and the pointer through
`Menu Manager`'s `MenuControl.pointer`, which is the `Bind()`-style convention everything else here
uses. A rename anywhere in `PersistentRig` now fails the same way everywhere — see
[Conventions that break silently](../../CLAUDE.md#conventions-that-break-silently) — instead of
failing at the Inspector level in one component with no runtime fallback.

## Colocation — one anchor, every headset in the same real room

This is the core of the project and the part most likely to be broken by an innocent change.

**The idea.** The room owner presses *Place Anchor*. `BoardAnchor` creates an `OVRSpatialAnchor`
at their feet, localizes it, saves it, shares it into a fresh group GUID, and publishes
`(group, uuid)` onto `RoomAnchor`'s `NetworkVariable`s. Every other client polls those, downloads
that exact UUID, localizes it, and binds. From then on each client moves **its own rig** every
frame so the anchor lands on the world origin. World space is now the same physical frame on every
headset, so every networked value in the project stays in plain world coordinates and needs no
conversion.

The anchor does **not** have to be where the board is. The board is placed separately with the
world grab, and that placement is networked. Feet, not table — nothing to aim at, nothing to
measure.

**Alignment is continuous, not one-shot** (`CameraController2.AlignRigToAnchor`). The runtime
recenters the tracking origin on its own and re-localizes anchors as the room map improves; each
of those slides a one-shot alignment permanently out of the shared frame. The transform is
idempotent by construction, so a frame in which nothing moved writes back the pose already there.
It is **yaw-only** — pitch and roll from an anchor are noise, and applying them would tip the board
off the real floor — but it *does* take the anchor's height, because each headset puts `y = 0` on
its own floor estimate and those differ. A height disagreement past
`MaxAnchorHeightDisagreement` **rejects the whole frame** rather than half-applying it.

**That guard is `0.6 m`, and it is a tracking-failure bound — not a floor-calibration one.** It was
`0.25 m`, which rejected the very case the height correction exists to fix: a headset that has never
had Space Setup run is guessing the floor from whatever surface it last saw and can be half a metre
out, and because the check re-runs and re-fails every frame such a headset **never aligned at all,
not once**. The startup race the tight bound was really guarding against is now caught separately
and directly, by asking `XROrigin.CurrentTrackingOriginMode` whether it has reached `Floor` yet —
until it has, `Camera Offset` is not zeroed and every pose in `AlignRigToAnchor` is off by the
serialized `CameraYOffset`. Both are driven by the same event, so "the mode reads Floor" and
"`Camera Offset` has been zeroed" are one fact rather than two that could disagree. The rejection
warning is rate-limited to one every `HeightWarningIntervalSeconds` (5) — at frame rate it was a log
flood, and the interesting events are "it started" and "it stopped".

> The serialized value wins over the initializer, so raising the default in `CameraController2.cs`
> is **not enough on its own** — `PersistentRig.prefab` carries its own copy and that is what runs.
> This is the same class of trap as [serialized-field renames](../../CLAUDE.md#conventions-that-break-silently).

**What alignment switches off.** Once `CameraController2.LocalIsAligned` is true, joystick
locomotion, snap-turn, recentring and the debug tilt all early-out (`CameraController2.cs:91-94`),
and `ApplyRingAnchor` refuses to move the rig. A colocated player's position is a fact about the
real room, not something to assign; involuntary rig moves in passthrough are nauseating. The flag
is `static` because everything that cares — `PlayerControls`, `WorldGrab`, the probe — needs it
without holding a reference to the rig at all; it predates `PersistentRig` and the reasoning still
holds even though the rig itself no longer gets destroyed on a game switch.

**Failure is survivable and honest.** A client that never binds still plays; it is simply a player
in a different room. It keeps locomotion and the recentre button, and it still gets a ring slot.
`A` (`BoardAnchor.RequestReAlign`) re-downloads and re-localizes; it releases the current binding
first, because the SDK filters already-bound anchors out of query results and the retry would
otherwise report a download failure that never happened.

**Load requests are latched, never dropped** (`RequestLoad` / `PumpLoadsAsync`). A load can be in
flight for a minute with retries and an 8-second localize timeout; a dropped request would leave
one client bound to a stale anchor permanently with nothing to tell it otherwise.

**A freshly published anchor is ignored coming back.** After publishing, `PollRoomAnchor` refuses
to follow `RoomAnchor` until the server echoes the client's own UUID (15 s timeout). Until then
what is on `RoomAnchor` is still the *previous* anchor, and following it would tear down the one
just placed.

### Execution-order contract

Fourteen components carry `[DefaultExecutionOrder]` and the values are load-bearing. The whole chain
exists to put everything that *reads* a world pose after everything that *writes* one:

| Order | Component | Why |
| --- | --- | --- |
| **−10** | `PlateauBoard`, `StairsBoard` | bake their board tables before anything reads them. Both idempotent via `EnsureBaked()`, because a game's server tick can arrive in the same frame as the scene load |
| (default 0) | `CameraController2` | locomotion, recentre, and the per-frame alignment to the room anchor |
| **10** | `BoardAnchor` | must run **after** `OVRSpatialAnchor.Update()` refreshes the anchor's world pose; reading it earlier gets last frame's rig baked in |
| **15** | `RoomContent` | applies the shared board pose to `World Root` in this frame's aligned frame |
| **20** | `PlayerControls`, `WorldGrab` | sample head/hand world poses **after** the rig has moved; at order 0 every pose broadcast is a frame stale (~13 ms at 72 Hz) on top of network latency |
| **20** | `StairsView` | reconciles Stairs' pieces after the frame has been placed and before `StairsSelection` lights them. (Chasms' equivalent is at 30 instead; both work, because the billboard that wanted the late slot is in `LateUpdate` either way) |
| **23** | `PlateauSpawnMenu` | so `IsOpenOrOpening` is up to date before `PlateauSelection` reads it. Ordering is no longer what protects the selection — `IsOpenOrOpening` is, because the 0.15 s open debounce leaves the menu "closed" for ~10 frames after the grip goes down |
| **24** | `PointerBeam` | one `Physics.SyncTransforms()` + one raycast, after the rig and the board have both settled |
| **25** | `PlateauSelection`, `PlateauChooser`, `ControlListener`, `StairsSelection` | all four consume the hit `PointerBeam` produced this same frame. They are peers and none depends on the others — `PlateauChooser` and `PlateauSelection` are ChasmGame, `ControlListener` is BASH, `StairsSelection` is StairsGame, and no two of them are ever in the same scene |
| **30** | `PlateauPieceView` | reconciles pieces; its `LateUpdate` billboard needs the final `World Root` pose |

## The content frame and the two-grip world grab

`World Root` is the content frame. **Everything the game owns hangs under it and nothing a player
owns does** — which is what makes "move and resize everything except the players" structural
rather than a filter. `RoomContent` (on `World Root`) applies `RoomAnchor.contentPos/Yaw/Scale`
each frame with an 80 ms first-order filter, and snaps rather than glides on the first frame so a
joiner or a freshly loaded scene is already correct.

The gesture (`WorldGrab`, on `XRRig`) is: **both grips** → move, turn and resize the board.

- Yaw is **accumulated from per-frame deltas**, not from a single `theta - theta0`. `atan2` wraps
  at ±180°, which would spin the board a full turn at the seam and cap the gesture at half a
  revolution. Frames where the hands are stacked vertically contribute nothing (the axis has no
  yaw there) — a pause in the turn rather than a spin.
- Scale is the **clamped** span ratio. Using the raw ratio makes the board slide out from under
  your hands once it hits a limit while appearing stationary in size. Bounds are
  `RoomAnchor.MinScale`/`MaxScale` (0.15 – 4), enforced server-side too.
- The pose applied is a proper similarity transform mapping `startCenter → center`. Naive
  `startPos + (center - startCenter)` drags the board sideways whenever its origin is not exactly
  under your hands, which it never is.
- Scaling `World Root` and never the rig is deliberate: scaling the rig drags the user's tracked
  hands away from the real hands they can see through passthrough.

**The lock.** `RoomAnchor.worldHolder` is a server-written client id, `NoHolder = ulong.MaxValue`
(not `0` — that is the host). First to squeeze both grips wins until they let go. Without it, two
players gesturing at once each stream a pose from their own hands and the board oscillates at the
tick rate. Details that matter:

- The holder drives `RoomContent.ApplyImmediate` at frame rate locally and sends at `SendHz` (20);
  everybody else smooths what arrives.
- On release, `Commit()` sends one unconditional final pose before the lock is dropped — RPCs from
  one sender are reliable-sequenced, so the server applies it before clearing the lock.
- `Cancel()` releases on `claimSent`, **not** on `LocalHoldsWorld`, so letting go mid-round-trip
  cannot leave the server granting a lock nobody is using.
- `OnDisable` cancels. This existed because a game switch used to destroy the rig mid-gesture;
  since `PersistentRig` the rig is never destroyed or disabled by a switch, so **this path no
  longer fires on a game switch** — only on the component's own actual disable/destroy (e.g. Play
  mode stopping). A lock held into a `LoadSceneMode.Single` switch is not released by this any
  more. Not yet hit in practice — the server-side release below is the remaining safety net — but
  worth an explicit release on scene switch if it turns out to matter.
- The server clears the lock on `OnClientDisconnectCallback`.

## Where players stand — `PlayerRing`

A static ring of **12** slots (matching the Relay allocation of 12) at radius 2 m, centred on the
world origin, all at `y = 0`. Slot 0 is on the −Z side.

The server assigns a slot once in `PlayerControls.OnNetworkSpawn` via
`PlayerRing.PickFreeSlot(OccupiedSlots())` — the middle of the widest empty stretch, so one player
is at 0, the second opposite, the third and fourth on the quarters. **It never re-spaces players
who are already there**; teleporting somebody because a third player joined is exactly the
involuntary rig move that makes people sick. Occupancy is read from live players rather than a
static, so a slot cannot leak on an ungraceful disconnect.

Nothing about the ring is networked and it never moves the shared world — a slot is only ever the
anchor for *this client's* rig (`CameraController2.PlaceAtRingSlot`), and it is skipped entirely
in the lobby (`GameRoutes.IsGameScene`) and for colocated players.

**Adding a game should mean putting its board at the origin and nothing else.** If a board needs
more room, change `PlayerRing.Radius` rather than moving boards per scene.

## Avatar replication

`Player.prefab` (the `NetworkManager`'s player prefab, auto-spawned per client) carries
`PlayerControls`, `GameSelector` and `ClientNetworkTransform` (a `NetworkTransform` with
`OnIsServerAuthoritative() => false`). Children, resolved **by name** in `OnNetworkSpawn`:
`PlayerLeft`, `PlayerRight`, `mainFace`, `tornado`, and **`TagsRoot`** — which holds the three
labels, found beneath it rather than on the root: `Username`, `ScoreTag`, `Gemheart`.

**The labels hang off `TagsRoot` and are laid out in the prefab**, not offset by constants in code.
`Update()` puts `TagsRoot` itself above the head (`FaceBelowEyes + NameTagAboveEyes`, 0.36 + 0.28)
and billboards it; everything below it keeps its authored local offset, so the spacing is visible in
the Scene view instead of being a number in a script that nothing reads. `ScoreTagBelowName` used to
exist for that and is gone. There is a fallback to finding `Username`/`ScoreTag` on the root when
`TagsRoot` is missing, so an older prefab degrades rather than throwing.

> The reparent that created `TagsRoot` is worth knowing about: done in the Editor it kept Unity's
> world-position-preserving offsets, and every label ended up **6.5 m** from its root along the view
> axis — present, correct, and nowhere near the player. See `bugFixes2.md` §2.

- `ScoreTag` shows whatever the loaded game returns from
  `IGameSession.AvatarBadgeForSeat(spawnSlot)`, and **null hides it** — which is the normal case,
  since only two games have anything to say: Chasms that seat's `gemheartScores` entry, Stairs the
  captured-tile count for whichever of its two seats holds that ring slot (null for a spectator).
  `PlayerControls` does not know what the number means; it used to read
  `PlateauGame.Instance.ScoreForSeat` directly, which put one game's score on the shared avatar.
  It is read **every frame for every remote avatar**: `StairsGame` answers from a cached
  `string[0..40]`, `PlateauGame` still does a fresh `int.ToString()` per player per frame. Copy the
  former. Like the nametag it is visible to everyone
  *except* its owner, because `OnNetworkSpawn` deactivates every child of your own avatar.
  The icon child beside it is still named `Gemheart`, which is Plateau's word for a generic slot.

- **A remote player is two cones and a name.** `ShowRemoteHeadAndBody` is `false`: in passthrough
  their real head and body are already there, and a virtual copy is at best noise and at worst
  drawn where they are not. The head is `SetActive(false)`, not deleted, because `Update()`
  dereferences it unguarded every frame.
- Your own avatar's children are all disabled locally — you are inside it.
- Hands go on the wire as **position + `Quaternion`**, not position + forward. Rebuilding a
  rotation with `LookRotation` discards roll and is ill-conditioned near vertical — and pointing a
  controller straight down at a board is the default posture here, not an edge case.
- The nametag is derived from the **head pose**, not from a fixed height above the avatar root.
  The old fixed 1.43 m rendered across the face of anyone 1.45 m at eye height or taller.
  `FaceBelowEyes` (0.36) is the mesh-pivot-to-eye offset and is used in both directions, so it
  exists once rather than as the same magic number in two places. `NameTagAboveEyes` (0.28) has to
  clear the top of a **real** head seen through passthrough, not the virtual one — which is never
  drawn.
- Remote poses are smoothed with a frame-rate-independent exponential lerp (`RemoteSmoothTime`
  0.06 s). `NetworkVariable` delivers at the 30 Hz tick and the headset renders at 72–90, so raw
  assignment makes the cones step. A player who has just spawned **snaps** rather than gliding in
  from the origin. The face *value* is smoothed once and drives both the head and the tag, so the
  two cannot diverge.
- `playerName` is a `FixedString32Bytes` (29 bytes of UTF-8) and is clamped — an over-long name
  throws *on the server* and takes the name down for everybody. **The clamp matters more now.** The
  name arrives in the connection approval payload, before this object exists, so the server sets it
  in `OnNetworkSpawn` from `RoomApproval`'s record and clamps there; the owner's `SetPlayerNameServerRpc`
  is kept as the fallback for a session that never went through the lobby. And the source is no
  longer a keyboard with a fixed key count — it is `IEntitlementService.DisplayName`, which on a
  store build is the player's Meta display name and is not length-limited by anything.

## Menus, pointer, keys

Three surfaces — the lobby's two canvases, the code pad, and the in-room menu — are one toolkit
(`Assets/Scripts/Ui/`) drawn on world-space Canvases. **Canvas for pixels, colliders for presses.**

There is deliberately **no `EventSystem`-driven uGUI input module**, and adding one is the wrong
move. `OpeningScene` still carries an `EventSystem` root; it drives nothing. A ray-driven module is
~250 lines of well-known-but-fiddly code that this project has no other use for, and everything
pressable here already has a working path: `PointerBeam` (order 24, after its own
`Physics.SyncTransforms()`) publishes the ray, and `pointerControl`'s trigger capsule reports
`currentKey` for any collider tagged **`key`**. So uGUI draws the row — `Image`,
`TextMeshProUGUI`, `RectMask2D` — and the row additionally carries a `BoxCollider`, a kinematic
`Rigidbody`, the tag `key` and a `keyInfo`. If a later feature genuinely needs `Button`,
`InputField` focus or `Dropdown`, that is the moment to write a module, not before.

### The toolkit — `Assets/Scripts/Ui/`

| Type | What it is |
| --- | --- |
| `Panel` | A world-space Canvas with a header, a status line, an optional detail block and any number of `ScrollList`s. Owns the **one** press model: pressed on trigger **down**, acted on trigger **up**, so sliding off a key cancels it. |
| `PanelRow` | One pressable row: thumbnail, title, subtitle, right-hand state cell. `Bind(RowData)` rebinds it. |
| `RowData` | What a row says, plus the **stable id** it acts on. |
| `ScrollList` | N fixed slots plus a scroll offset. |
| `CodePad` | A 6 × 6 grid of `A-Z 0-9` plus `Back`, `Clear`, `Cancel` and a submit key, generated from an inactive template. |

**`1 UI unit = 1 mm`, `localScale 0.001`, everywhere.** A 1.2 m × 0.8 m panel is
`sizeDelta (1200, 800)` and a 96-unit row is 9.6 cm tall — legible at 1.6 m. The project used to
have two conventions in play at once (`Text Input` at 0.01, the rules panel at 0.002), which is
exactly how panels end up subtly different sizes. `BuildRulesPanel` was moved onto this one.

**A world-space Canvas draws on its `+Z` face**, so a panel given the camera's own rotation shows
the player its back. Everything that places one applies a further `180°` about Y, plus a yaw that
turns a panel sitting to one side back in towards the player. Two consequences that have already
cost a sign error each: `MenuControl` takes its left offset off the **camera's** `right`, not the
menu's (after the flip those point opposite ways), and the rules wing is placed in **world** space
and then parented with `worldPositionStays`, rather than in menu-local coordinates that now run
backwards on two axes and are in millimetres.

**`ScrollList` recycles, and that is a correctness property, not an optimisation.** A `RectMask2D`
clips *pixels*, not colliders, so the naive "tall content, slide the content" list leaves rows you
cannot see sitting where the beam can still press them. Rows that never leave the viewport have
nothing off-screen to press. Two rules follow:

- **The list refuses to scroll while the right trigger is held.** Without this a press begun on row
  3 and released after a scroll acts on whatever row 3 now shows — the key object is the same, its
  `keyName` is not. One `if` removes the whole class of bug.
- **A row's `keyName` is its stable id** (`GameModule.gameKey`, a lobby id, an action key), never a
  slot index.
- Rows are positioned by `anchoredPosition` and **must not** sit under a `LayoutGroup`:
  `keyInfo.MakeBigger` multiplies `localScale` on press and a layout group would fight it.

`keyInfo` is renderer-agnostic. It swaps a `Material` on a `MeshRenderer` (the 3D keys — the
`SpawnMenu`, `Key.prefab`) *and* tints a `Graphic` (`targetGraphic`, `onColor`/`offColor`), both
through one `Apply(bool)`, so "on" cannot come to mean different things in the two. `keyLabel` is
`TMP_Text`, which both `TextMeshPro` and `TextMeshProUGUI` satisfy. A key with neither renderer is
legal and still presses.

### The room menu — `RoomMenu.prefab`

`MenuControl` (on `Menu Manager`, one shared instance on `PersistentRig` — see
[The persistent rig](#the-persistent-rig)). **`X` opens and closes** the menu, which is instantiated
1.3 m in front of the camera and 0.7 m to the left. **`X` does nothing in the lobby** — gated on
`GameRoutes.IsGameScene` — where it used to resolve no local player, warn, and would now open a
list filtered to nothing.

`Assets/Prefabs/Ui/RoomMenu.prefab` is a **prefab variant of `Panel.prefab`**, so the room menu can
be re-skinned without touching the lobby's canvases and a fix to the shared row layout still reaches
both. **`Menu1.prefab` and `Menu2.prefab` are gone from the wiring.** The split existed only to hide
`Voice Chat` from non-hosts, which with generated rows is one `if`; `OpenMenu1`'s prefab-picking
branch and its "Menu2 is not assigned" fallback went with it. So did `GameKeyTemplate`,
`ActionKeyTemplate`, `Row1` and the `ComfortableColumnKeys = 4` ceiling — the old fixed column ran
the action row into the authored `Voice Chat` key at `z −0.631` past about four games.

Keys are dispatched by **`keyInfo.keyName` string**, never by child index. (The menu this replaced
resolved widgets with `GetChild(0).GetChild(9).GetChild(13)`, so re-skinning the prefab broke it
with no compile error.) `pointerControl` reports `keyName`, not the visible label.

Rows are built on open, into the panel's **two** lists:

| List | Rows | Source |
| --- | --- | --- |
| `Main` (scrolls) | one per game the **room** may play | `RoomLibrary.Playable()` — the catalog filtered by the union of every player's library. In a **public** room, reduced to the one locked game with a line saying why |
| `Actions` | `Place Anchor`, `Passthrough`, then `Voice Chat: ON/OFF` for the host only, then the loaded game's own keys | `GameModule.menuActions` of the **active scene's** module |

**`Passthrough` is now reachable.** `HandleKey` had handled it since it was written and nothing ever
built a key for it — a live handler with no way to press it.

`HandleKey` knows exactly three keys — `Passthrough`, `Voice Chat`, `Place Anchor`. Anything else is
either a game key (`GameRoutes.IsGameKey` → `GameSelector.RequestGame`) or the loaded game's own
action, handed to `GameSessionRegistry.Active.InvokeMenuAction`. **Nothing in `MenuControl` names a
game, and adding one must not change that.** An unknown key is deliberately inert and logs.

**The game-row filter is cosmetic.** `GameSelector.RequestGameServerRpc` runs the same two checks
server-side — the room's public lock and `RoomLibrary.Union()` — and that is the enforcement, on the
next line after the `GameRoutes` check that exists for the identical reason.

**Known rough edge:** the rules wing and the game list both read `rightJoystick.y`
(`ScrollTextWithJoystick` and `ScrollList`), so with more than five games in the catalog a joystick
push scrolls both at once. Neither is destructive; fix it by giving `ScrollList` a modifier or by
moving the rules panel onto a `ScrollList` of its own.

This is **the room menu — not `SpawnMenu`**, the per-player piece menu on the left wrist, which is a
separate prefab with its own key set and its own dispatcher (`PlateauSpawnMenu.HandleKey`). See
[The spawn menu](Plateau/CLAUDE.md#the-spawn-menu--plateauspawnmenu).

`pointerControl` fires on trigger colliders tagged **`key`** and stretches the visible beam to the
hit — but in a game scene `PointerBeam` overwrites the beam length absolutely every frame, and
`MenuControl.keepPointerAlwaysOn` (taken from the loaded game's `GameModule`; true for all three
games) leaves the pointer switched on outside the menu. See [The pointer](Plateau/CLAUDE.md#the-pointer) and
[The persistent rig](#the-persistent-rig).

`GrabControl` (on `Left Grabber` / `Right Grabber`) tracks colliders tagged **`Grabbable`**;
nothing is tagged that today, so `WorldGrab.CanStart`'s "two grips with a piece in hand is a piece
grab, not a world grab" check is currently always false — it is there so it does not have to be
retrofitted the day the first piece becomes grabbable.

## Rooms, libraries and the store

Who may play what, and how a public room is found. The store itself is one level down, in
[`Store/CLAUDE.md`](Store/CLAUDE.md); this is the part that touches the room.

| Type | Where | What it is |
| --- | --- | --- |
| `RoomOptions` | `Lobby/`, static | The host's public/private choice, carried the few frames from the Play panel to `RoomAnchor.OnNetworkSpawn`. **Reset in `BoardAnchor.Awake`**, beside `CameraController2.SetAligned(false)` and for the same reason. |
| `ConnectionPayload` | `Lobby/` | `(playerName, ownedMask, build)`, hand-encoded with a version byte, in `NetworkConfig.ConnectionData`. |
| `RoomApproval` | on **Network Manager** | Switches approval on; the server's `ConnectionApprovalCallback`; remembers what each client claimed. |
| `RoomDirectory` | on **Network Manager** | Unity Lobby as a noticeboard over the unchanged Relay flow. |
| `PlayerLibrary` | on `Player.prefab` | `NetworkVariable<ulong> ownedMask`, **server-written**, set at spawn from the approval record. |
| `RoomLibrary` | static | `Union()` — the OR of every spawned `PlayerLibrary`. `Playable()` — the catalog filtered by it. |

**`RoomApproval` and `RoomDirectory` are on the Network Manager**, not the rig or the lobby, for the
reason `BoardAnchor` and `NetworkProbe` are: Netcode marks that object `DontDestroyOnLoad`, and both
have to keep working after the host has left the lobby for a game — the directory's ~15 s heartbeat
above all.

**The room library is the UNION, not the intersection.** Somebody who owns a game can show it to the
table. Requiring everybody to own it makes buying a game pointless until your whole group has, which
is the opposite of what a store wants. It is computed **on demand**, when the menu opens and when a
switch is validated; both are rare, and a cache would need invalidating on spawn, despawn and change.

**The library gate is a UX and social rule, not DRM, and the code says so out loud.** There is no
dedicated server here — the "server" is another player's headset, and `ownedMask` is asserted by the
client that sends it, so a modified client can claim to own everything. The paid game's scene and
assets ship inside the APK either way, because Meta add-ons gate entitlement and not delivery.
Decide that is acceptable and do not build a defence that cannot work.

**Two server-written `NetworkVariable`s make a room public**, on `RoomAnchor` alongside the anchor
identity and the content pose, because they are the same kind of fact and a late joiner then gets
all of them in one replication pass:

- `isPublic` — listed in the directory.
- `roomGameKey` — the one game it plays. `RequestGameServerRpc` refuses anything else, the room menu
  shows only that game, and `RoomApproval` refuses a joiner whose mask lacks that bit with
  `Reason = "You do not own <label>"`.

`RoomAnchor.LockedGameKey` is the static that reads both and answers null for a private room.

### The directory

Unity Lobby is **a directory over the existing Relay flow and nothing more**: the host publishes its
Relay join code into a lobby, and a browser reads the code back out and goes down
`RelayVivox.JoinRelay(code)` completely unchanged. The alternative — the Sessions API — replaces
`RelayVivox` wholesale, including the `connectInFlight` latch, the 15 s watchdog, the `dtls` match
and the Shutdown-before-retry fix, and re-opens
[`quest_networking_plan.md`](../../docs/quest_networking_plan.md) in new code.

Four things decide whether it works in practice, and all four are the service's rules rather than
C#:

1. **Heartbeat or the room vanishes** — a lobby is deleted after roughly 30 s without a ping, so
   `RoomDirectory` pings every ~15 s for as long as the room is open. That timeout is a *feature*: a
   host that crashes cannot leave a ghost room in the browser for ever, which is the failure
   everyone hits when they clean up only on a graceful exit.
2. **Rate limits are per-lobby and tight** (roughly: query 1/s, update 5 per 5 s, create 2 per 6 s).
   Refresh is throttled and disabled while a query is in flight; the player count is written by the
   **host only** and coalesced to at most one update every ~6 s. Having every joiner also join the
   Lobby so `AvailableSlots` maintained itself would double the heartbeat and leave failure surface
   for one number.
3. **The build is published, and rooms this build cannot join are greyed.** This is the highest-value
   line in the feature: `ForceSamePrefabs` refuses a mismatched build with **no reason string**, so
   the joiner just sees "Joining room…" for ever. `Different version` in a greyed row turns this
   project's single most mystifying failure into three readable words. `RoomApproval` catches the
   same case one step later, for a code typed by hand.
4. **Degrade, do not block.** If Lobby is unreachable the two public rows go inert with a reason and
   private rooms keep working. Same principle as the anchoring code.

**Prerequisite, once: Lobby must be enabled for this project in the Unity Cloud dashboard**, as Relay
and Vivox already are. Same project id, same anonymous sign-in `RelayVivox.Start()` already performs.
No package change was needed — `com.unity.services.multiplayer` already folds Lobby, Relay and
Matchmaker into the one assembly `MRBoardGame.Shared.asmdef` references.

## Passthrough

`PassthroughController` on `XRRig`. Passthrough on Quest is a **compositor layer owned by the Meta
runtime**, not a render feature: the app must submit a frame whose background pixels have
**alpha = 0**, and the runtime composites the camera feed underneath. Deleting the skybox alone
gives you a black background with no error message. Four things had to be true and all four are
currently applied — **do not regress any of them**:

1. `Main Camera`: `m_ClearFlags: 2` (Solid Color), background `(0,0,0,0)`,
   **`m_RenderPostProcessing: 0`**. URP's post stack writes opaque alpha into the final target and
   is the single most common cause of "I followed the tutorial and it is still black".
2. `m_SupportsHDR: 0` on **all three** URP quality assets. HDR on mobile selects
   `R11G11B10_UFloat`, which has **no alpha channel**.
3. `OVRPassthroughLayer.overlayType = Underlay` (Overlay draws the feed on top of everything).
4. `m_SkyboxMaterial: {fileID: 0}` in every scene. `m_AmbientMode` is `3` (flat colour) in all of
   them, so removing the skybox does **not** change the lighting — if something looks different
   after a change here, the cause is elsewhere.

`Assets/Materials/Space.mat` is kept, not deleted, so VR mode is still reachable
(`vrSkybox` on the controller). The on/off state is `static`, so a choice made in the lobby
survives the load into the game. It is deliberately **not** a `NetworkVariable` — passthrough is a
per-user comfort setting like brightness, and one player switching to VR must not drag the room
with them.

The controller also owns **Guardian suppression**, because Meta requires the two to move together:
in full-VR mode the player cannot see the real room, so the boundary is the only thing keeping
them off the furniture and it must come back. Suppression needs all three of
`OVRManager.shouldBoundaryVisibilityBeSuppressed`, `boundaryVisibilitySupport` in `OVRProjectConfig`
(written by `MRPassthroughSetup`), and `com.oculus.permission.BOUNDARY_VISIBILITY` in the manifest.
Missing any one and the runtime refuses silently.

`EnsureOvrComponents()` will add a missing `OVRManager` or `OVRPassthroughLayer` at runtime, but
wiring them in the scene is preferred — Meta's Project Setup Tool only validates components it can
find in the scene.

## Input

`InputReader` (on `Input Reader`, one shared instance on `PersistentRig`) polls
`UnityEngine.XR.InputDevices` every frame
and republishes everything as plain public fields: level (`ButtonA`), edge-down (`ButtonADown`),
edge-up (`ButtonAUp`), and analogue values. Two things to know:

- Device queries filter on `Controller | Left`/`Right`. Asking for bare `Right` also matches a
  tracked right *hand*, so enabling hand tracking alongside controllers used to push the match
  count to 2 and silently kill all right-hand input.
- Each hand independently falls back to the keyboard when its controller is absent.
- `*Down` flags are one-shot edges. Held gestures must read levels — `WorldGrab` reads
  `LeftGrip && RightGrip`, not the `Down` flags.

Control map as it stands:

| Input | Effect |
| --- | --- |
| Right trigger | select a menu / keyboard key; in Chasms, select a piece or a destination plateau or spin the chooser; in BASH, select one of your gamepieces; in Stairs, **held** it drags a pawn or a step onto the board — or lifts a tile already laid, to re-lay it — and **tapped** it selects your pawn, takes a move, or presses End Turn |
| Left trigger | BASH only: fire — lob the arc, then commit the spin-aimed movement line |
| `X` | open / close the room menu — **in a game scene only**; it does nothing in the lobby |
| `A` | re-align to the room anchor (`BoardAnchor.RequestReAlign`) |
| `B` | cancel the current piece selection (Chasms), or the current selection or drag (Stairs) |
| **Left grip alone** | Chasms: open the personal spawn menu (`PlateauSpawnMenu`), held — releasing it, adding the right grip, or opening `Menu1` closes it |
| Both grips | world grab — move, turn, resize the board |
| Left joystick | move and snap-turn — **only when not colocated and not world-grabbing** |
| Left joystick click | recentre the rig on the ring slot |
| Right joystick | Chasms: up/down sets how many pieces to move. BASH: aims the artillery arc |
| Right joystick click | clear the in-headset debug log |
| `M` / `N` | tilt the rig (Editor debugging) |

`Y` is read nowhere. The **left grip on its own is now meaningful** (the spawn menu), so it is no
longer true that only the pair matters — which is exactly why `PlateauSelection` and
`ControlListener` both stand down when *either* grip is merely held, rather than waiting for
`WorldGrab.IsActive`. Note `rightJoystick.x` is **not** read in Chasms and should stay that way:
its Editor keyboard fallback is bound to `A`/`D` (`InputManager.asset`) and `A` is re-align, so a
horizontal nudge in the Editor would also re-align the rig.

## Debugging in the headset

`DebugLog` (on `Debugger`, a child of `XRRig`) mirrors `Application.logMessageReceived` into a
10-line TextMeshPro box and appends the Relay room code. `Debug.Log` from anywhere lands there —
no adb, no extra UI. Right joystick click clears it. (It used to be the *left* click, which meant
every recentre wiped the log you were reading to find out why you recentred.)

`ColocationProbe` (on `XRRig`) prints one line a second. **It is currently `m_Enabled: 1` on
`PersistentRig.prefab`, i.e. running in every build** — its own class comment says to delete the
component once the numbers are known, and that has not happened. Practical consequence: at 1 Hz it
refills the ten-line box every ten seconds, so **turn it off before debugging anything else**,
`NetworkProbe`'s connection events included. It prints:
rig/head height, `aligned`, `anchored`, `tracked`, the short UUID, the count of system-initiated
recenters, the content scale, the lock holder, and every remote player's head/hand height. Its
class comment is a read-it-like-this guide; the short version:

- recenters ticks **and the cones move** → no shared frame
- recenters ticks **and nothing moves** → alignment works; this is the acceptance test
- differing `uuid` on two headsets → they are on different anchors
- `head.y ≈ 1.36` or `≈ 2.7` on a standing adult → `Camera Offset` was never zeroed; nothing else
  means anything until this clears

Take a baseline before changing anything in this area.

`NetworkProbe` (on **`Network Manager`**, not the rig — Netcode marks that object
`DontDestroyOnLoad`, so the probe is still listening after a game switch, which is exactly when a
late disconnect shows up) logs every connection event: `connected as client`, `peer JOINED` /
`peer LEFT`, `connection LOST` with `NetworkManager.DisconnectReason`, and `transport FAILURE`.
Before it existed, `StartHost`/`StartClient`'s return values were discarded and nothing subscribed
to any connection callback, so a client whose connection died *after* Netcode had synchronized it
into the host's scene was indistinguishable from a client that was connected and alone.

- **A `connection LOST` with an empty reason is Netcode's config-hash refusal**
  (`ForceSamePrefabs`), which sends no reason string. Compare `prefabs=` on the two devices.
- `LogHeartbeat` (off by default) adds one state line per second — `connected`, `clients`, `scene`,
  `prefabs`, `player`. It is off because the debug box holds ten lines and a 1 Hz heartbeat scrolls
  the events worth reading out of it.

`VisibleWhenLooking` on `InfoBlock` shows the room code when the player looks at it.
