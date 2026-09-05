# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## What this project is

**MRBoardGame2** — a colocated mixed-reality multiplayer board game for Meta Quest. Several
people stand around a real table in the same physical room, each wearing a headset, and see the
same virtual board sitting on it. The board is placed and resized by grabbing the air with both
hands. Passthrough is on, so players see each other's real faces and hands; the only virtual
parts of another player are two controller cones and a floating nametag.

The game being built is **Plateau** — plateaus, bridges, gemhearts, chasmfiends. The rules are in
[`plateauRules.md`](plateauRules.md). Under it sits the platform: colocation, networking, voice, the
shared content frame, the world grab, the player ring and the menu.

Of the rules, **starting forces and piece movement are implemented** — see
[The Plateau game](#the-plateau-game). **Turns, harvesting, buying and win conditions are not**: any
player may move their own pieces at any time. Gemhearts and chasmfiends exist as *pieces* — a
neutral `PieceKind` each, placed and removed by hand from the spawn menu — and each seat has a
gemheart score that is likewise adjusted by hand. They are props for playing the rest of the game
around, not the rules working.

## Toolchain and targets

| | |
| --- | --- |
| Unity | **6000.5.4f1** exactly (`ProjectSettings/ProjectVersion.txt`) |
| Render pipeline | URP 17.5.0, Linear color space |
| XR | Meta XR Core SDK **205.0.0** (scoped registry `npm.developer.oculus.com`) + Oculus XR Plugin 4.5.4, via Unity's `XROrigin` — **not** `OVRCameraRig` |
| Networking | Netcode for GameObjects 2.13.0 over Unity Relay (host/client, no dedicated server), tick rate 30. Transport protocol is `RelayVivox.connectionType`, default `dtls` — **it must match on host and joiner**; see [`quest_networking_plan.md`](quest_networking_plan.md) |
| Voice | Vivox 16.10.0, one group audio channel per room |
| Platform | Android / Quest, **ARM64 only**, IL2CPP, minSdk + targetSdk 34 |
| App id | `com.CoolDeal.MRBoardGame2` |
| Devices | Quest 2 / Pro / 3 / 3S (Quest 1 is explicitly dropped — no useful passthrough) |

Stereo rendering on Android is Multiview. Android ships quality level **1 = Balanced** →
`Assets/Settings/URP-Balanced.asset`; the Editor sits on level 2 (High Fidelity). HDR is off on
all three levels on purpose — see [Passthrough](#passthrough).

## Opening the project for the first time

Three things will bite, in this order.

**1. Meta XR Core SDK 205.0.0 does not compile on Unity 6000.5.** The project drops into Safe
Mode with `CS0619 ... instanceId is obsolete` from `SceneListenerNGO.cs`. This is an upstream bug,
not a bug here. Fix:

```powershell
powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
```

The patch lands in the gitignored `Library/PackageCache`, so **re-run it after any fresh clone,
cleared `Library/`, or package re-resolve.** It is idempotent. See
[`Tools/MetaSdkPatch/README.md`](Tools/MetaSdkPatch/README.md), including when the folder can be
deleted. **If Unity offers Safe Mode, take it** — "Ignore" opens with unloadable scripts, and
re-saving a scene in that state strips components off `XRRig`.

**2. Active Input Handling must stay "Input Manager (Old)".** The Meta SDK pulls in
`com.unity.xr.hands` → `com.unity.inputsystem`, and Unity flips `activeInputHandler` to `2`
("Both") when that appears. The Oculus XR Plugin then refuses to build for Android. This project
uses `UnityEngine.XR.InputDevices` for controllers and the legacy `Input` class for the keyboard
fallback — `InputReader.cs` alone has dozens of legacy `Input.*` calls, and the lobby's
`EventSystem` uses `StandaloneInputModule`. `activeInputHandler` is currently `0` and must stay
there.

> Unity prompts *"the native platform backends for the new input system are not enabled… enable
> the backends?"* on **every Editor launch**. Always answer **No**. There is no "don't ask again";
> the suppression flag is session-scoped by design.

**3. `MR Template > MR > Configure Meta Passthrough Project Config`** runs automatically on first
domain load (`Assets/Editor/MRPassthroughSetup.cs`, `[InitializeOnLoad]`) and is re-runnable from
that menu. It writes `OVRProjectConfig`, which is a ScriptableObject the SDK creates on demand
inside the package folder and therefore cannot be committed. Without it, Meta's *Android Manifest
Tool* regenerates `Assets/Plugins/Android/AndroidManifest.xml` and silently strips passthrough,
both anchor permissions, and boundary visibility. The hand-written manifest and this Editor script
have to agree; changing one means changing the other.

## Build and test

- **No CLI tooling, no CI, no package scripts.** All builds go through the Editor
  (File > Build Settings, Android).
- **C# can still be compile-checked headlessly**, without opening the Editor or taking the project
  lock. Take `<DefineConstants>` and the `<HintPath>` references from `Assembly-CSharp.csproj`
  (the Editor regenerates it; its `<Compile Include=>` list goes stale, so glob `Assets/Scripts`
  yourself), write a csc response file, and run Unity's **.NET** Roslyn —
  `Editor/Data/DotNetSdk/dotnet.exe Editor/Data/DotNetSdk/sdk/<ver>/Roslyn/bincore/csc.dll @rsp`.
  Not `MonoBleedingEdge`'s `csc.exe`, which fails to load `System.Text.Encoding.CodePages` and never
  reaches compilation. `Assets/Editor/*.cs` needs the same treatment against
  `Assembly-CSharp-Editor.csproj`, plus a reference to the runtime assembly you just built.
  **Always pass an explicit `-out:` outside the repo** — see the `BoardAnchor.dll` trap under
  [Dead or unwired code](#dead-or-unwired-code). Expect pre-existing `CS0618` warnings on
  `ServerRpcAttribute.RequireOwnership` and `FindFirstObjectByType` and ignore them. This checks C#
  only: it does not run Netcode's ILPP, so RPC/`NetworkVariable` codegen problems still need the
  Editor, and it cannot validate scene or prefab YAML wiring.
- `Library/ScriptAssemblies/Assembly-CSharp.dll` is only rebuilt when the Editor next opens, so a
  timestamp older than `Assets/Scripts/*.cs` means the current code has **never been compiled**.
  Check that before trusting that edits are sound.
- **No automated tests.** `com.unity.test-framework` is in the manifest, but there are no test
  assemblies, no `Tests/` folders, and **no `.asmdef` anywhere** — every runtime script compiles
  into the default `Assembly-CSharp`. Adding tests means creating assembly definitions first.
- **Build scene order** (`ProjectSettings/EditorBuildSettings.asset`) is
  `OpeningScene` (0) → `StairsGame` (1) → `ChasmGame` (2) → `GameScene` (3) → `BashGame` (4).
  Every scene a room can switch into must be in this list; `LoadScene` fails with
  `InvalidSceneName` otherwise, and that failure is only visible as a `Debug.LogError` from
  `GameSelector`.
- **Testing without a headset works.** `InputReader` falls back to the keyboard per-hand whenever
  that hand's XR controller is absent, so the whole app is playable in the Editor:

  | | Right hand (no right controller) | Left hand (no left controller) |
  | --- | --- | --- |
  | Trigger | `.` | `,` |
  | Grip | `P` | `Q` |
  | Face buttons | `A`, `B` | `X`, `Y` |
  | Joystick | arrow keys | `U`/`J` forward-back, `H`/`K` left-right |
  | Joystick click | `I` | `E` |

  `M` / `N` tilt the rig (`CameraController2`, debug only). The comment block at the top of
  `InputReader.cs` lists `O` and `W`; those are stale.
- `BoardAnchor` disables its own per-frame half when there is no Meta runtime
  (`BoardAnchor.cs:133-141`), so the Editor path is not an error state. Everything except the
  anchor itself — world grab, shared board placement, the ring, avatars — still runs.
- Anything multiplayer needs two running clients. Anything colocation needs two real headsets in
  one room.
- `.gitattributes` routes `.unity`/`.prefab`/`.asset` through Unity's smart merge
  (`merge=unityyamlmerge`). The driver itself is registered per machine in `.git/config`
  (`[merge "unityyamlmerge"]`, pointing at the local Editor install) — **not** committed, since the
  path is machine-specific. Re-register it after a fresh clone if merges on those files start
  failing outright instead of resolving.

## Repo state — read this before trusting `git`

**History before `dcad7e9` is a different application.** Everything up to and including that
commit is *Math Classroom*, a VR drawing-and-graphing app — its own `CLAUDE.md`, its line
drawing, function graphing and seating systems. This board game was built on top of it in the
working tree and landed in **one commit** on the `mr-passthrough` branch: 91 deletions, 78
additions, 25 renames, 34 modifications.

Practical consequences:

- `git blame` on nearly every file points at that single commit, not at incremental history.
  There is no per-feature history for colocation, the world grab, the player ring or the menu
  rework — the design docs below are the closest thing to a rationale trail.
- Git's rename detection paired files off by content similarity across the two applications, so
  the log shows nonsense like `Network Graph.prefab → Bridge Spots.prefab`. Those are unrelated
  files that happened to look alike; don't read meaning into them.
- `git show dcad7e9:<path>` is how to see the previous application's version of anything.

## Scenes

| Scene | Role |
| --- | --- |
| `OpeningScene` | Lobby. VR keyboard, room code + username entry, hosts or joins. Holds the **Network Manager** and the one **`PersistentRig`** instance — see [The persistent rig](#the-persistent-rig). |
| `StairsGame` | Default game. `World Root > Board > Cube` — a placeholder. No rig of its own. |
| `ChasmGame` | The Plateau board: 41 `Plateau` instances and 81 `Bridge Spots` instances under `World Root > Board`, plus the piece system — see [The Plateau game](#the-plateau-game). No rig of its own. |
| `BashGame` | BASH: a 3 m square of water inside four walls, four islands, and a base per player. See [The BASH game](#the-bash-game). No rig of its own. |
| `GameScene` | **Legacy.** In the build list, but absent from `GameRoutes`, so nothing can reach it. Predates the `RoomContent`/`WorldGrab` split. No rig of its own, same as every other game scene. |

A game scene's hierarchy, and the shape any new game should copy — deliberately thin, now that
the rig lives elsewhere:

```
World Root            [RoomContent]        <- everything the game owns hangs here
  Board
    ...
                                           <- ChasmGame additionally has, under World Root:
                                              Pieces        (empty, identity, SCALE 1)
                                              Plateau Game  [PlateauBoard, PlateauPieceView]
                                              and at the scene root:
                                              Plateau Controller [PlateauSelection, PlateauSpawnMenu]
                                              Plateau Chooser    [PlateauChooser]  <- novelty prop
```

`Input Reader`, `Menu Manager`, `XRRig` and `Directional Light` are **not** part of this — they
live once, in `OpeningScene`, under `PersistentRig`:

```
PersistentRig          [PersistentObject]   <- DontDestroyOnLoad; lives only in OpeningScene
  Directional Light
  XRRig                 [XROrigin, CameraController2, OVRManager,
                         OVRPassthroughLayer, PassthroughController,
                         WorldGrab, ColocationProbe(ENABLED — see below)]
    Camera Offset
      Main Camera
      Left Hand         [TrackedPoseDriver, LHController]
        SpawnMenu                              <- Chasms-only; see The persistent rig
      Right Hand        [TrackedPoseDriver, RHController]
        InfoBlock       [VisibleWhenLooking]   <- shows the room code when looked at
    Debugger            [DebugLog]
  Input Reader           [InputReader]
  Menu Manager           [MenuControl]
```

`XROrigin.m_TrackingOriginMode` is **2 (Floor)** on the shared rig. The rig's `y` is the real floor,
which the rest of the code assumes everywhere — `PlayerControls` projects the head straight down
onto it, `PlayerRing` puts every slot at `y = 0`, and `CameraController2` compares the anchor's
height against it.

## Session flow

1. `OpeningScene` loads. `RelayVivox.Start()` initializes Unity Services and signs in
   anonymously, setting `servicesReady`; a failure here (a headset that came up with no network) is
   caught and logged rather than vanishing into the `async void`. `MicPermissions` requests
   `RECORD_AUDIO`.
2. `GameController.Update()` reads whichever key the laser pointer is touching
   (`pointerControl.currentLetter`) and commits it on right-trigger down — first a **room code**,
   then a **username**.
3. On the final Enter:
   - **empty room code** → `RelayVivox.CreateRelay()` allocates a Relay slot for **12**, gets a
     join code, `StartHost()`. This client is the **room owner**. `GameController` then calls
     `GameSelector.LoadGameScene(GameRoutes.DefaultScene)` → `StairsGame`.
   - **non-empty code** → `RelayVivox.JoinRelay()` → `StartClient()`. **No `LoadScene` here on
     purpose** — Netcode synchronizes the joiner into whatever scene the room already has open. A
     bad code throws `RelayServiceException`, caught in `GameController.TryToJoinRelayVivox()`,
     which re-prompts through `ShowJoinFailed`. Both entry points take a `connectInFlight` latch,
     and `pressedKey` is cleared on every press — without that, a second trigger pull during the
     seconds before the joiner is pulled out of the lobby re-ran the whole join and the fresh
     allocation invalidated the one already connecting.
4. Vivox joins a group audio channel named after the room code.
5. `NetworkManager.OnServerStarted` fires `BoardAnchor.HandleServerStarted`, which instantiates
   `Room Anchor.prefab` and calls `Spawn(destroyWithScene: false)`.

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
config-hash refusal sends. See [`quest_networking_plan.md`](quest_networking_plan.md).

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
`BashGame`, i.e. every value in `GameRoutes.SceneByKey`), so the baked default only matters for the
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
- `MenuControl.keepPointerAlwaysOn` is `false` on the shared instance (`StairsGame`'s authored
  value — "the lobby and StairsGame keep the menu-only behaviour they were authored with").
  `PlateauSpawnMenu.Bind()` opts ChasmGame in through `MenuControl.SetKeepPointerAlwaysOn(true)`
  the moment it resolves `menu`, rather than changing the shared default and taking the other
  scenes down with it.

**The pointer's active state does not survive a scene switch on its own any more.**
`MenuControl.OpenMenu1`/`CloseMenu` toggle it, and `MenuControl.ApplyPointerDefault` resets it to
the incoming scene's default rather than trusting whatever the previous scene left behind. That
reset hangs off **`SceneManager.activeSceneChanged`, not `Start()`** — `Menu Manager` is part of
`PersistentRig` and therefore `DontDestroyOnLoad`, so its `Start()` runs exactly once for the life
of the app, in `OpeningScene`, and could never reset anything for a game scene.

Two consequences that have already caused bugs:

- **`OpeningScene` does have a `MenuControl`** — it arrives with `PersistentRig`. `GameController`
  is not the only thing writing the pointer's state there, so the two have to *agree* rather than
  one winning: `GameController.Start()` switches the pointer on for the keyboard, and
  `ApplyPointerDefault` returns `keepPointerAlwaysOn || !GameRoutes.IsGameScene(...)`, which is
  true in the lobby. Unity gives no ordering guarantee between two `Start()` calls, and when these
  two disagreed the pointer came up dead and no key on the lobby keyboard could be pressed.
- **Never assign `keepPointerAlwaysOn` directly; call `SetKeepPointerAlwaysOn`.** The
  `activeSceneChanged` reset has already run and switched the pointer off by the time any scene
  component's first `Update` opts in, so a bare field write leaves the pointer dead until the
  player opens and closes the menu. The setter applies the flag and re-evaluates in one call.
  `PlateauSelection` reads the flag and does not write it.

**`GameController` is the one place with direct, non-`Find` serialized references into the rig**
(`inputs`, `rh`, `lh`, `pointer`) rather than the `Find`-by-name convention everything else here
uses. That is a liability, not a model to copy: a rename anywhere in `PersistentRig` breaks
`GameController` at the Inspector level with no runtime fallback, unlike every `Bind()`-style
component under [Conventions that break silently](#conventions-that-break-silently).

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
> This is the same class of trap as [serialized-field renames](#conventions-that-break-silently).

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

Ten components carry `[DefaultExecutionOrder]` and the values are load-bearing. The whole chain
exists to put everything that *reads* a world pose after everything that *writes* one:

| Order | Component | Why |
| --- | --- | --- |
| **−10** | `PlateauBoard` | bakes the plateau table and the adjacency graph before anything reads them. Also idempotent via `EnsureBaked()`, because `PlateauGame`'s server tick can arrive in the same frame as the scene load |
| (default 0) | `CameraController2` | locomotion, recentre, and the per-frame alignment to the room anchor |
| **10** | `BoardAnchor` | must run **after** `OVRSpatialAnchor.Update()` refreshes the anchor's world pose; reading it earlier gets last frame's rig baked in |
| **15** | `RoomContent` | applies the shared board pose to `World Root` in this frame's aligned frame |
| **20** | `PlayerControls`, `WorldGrab` | sample head/hand world poses **after** the rig has moved; at order 0 every pose broadcast is a frame stale (~13 ms at 72 Hz) on top of network latency |
| **23** | `PlateauSpawnMenu` | so `IsOpenOrOpening` is up to date before `PlateauSelection` reads it. Ordering is no longer what protects the selection — `IsOpenOrOpening` is, because the 0.15 s open debounce leaves the menu "closed" for ~10 frames after the grip goes down |
| **24** | `PointerBeam` | one `Physics.SyncTransforms()` + one raycast, after the rig and the board have both settled |
| **25** | `PlateauSelection`, `PlateauChooser`, `ControlListener` | all three consume the hit `PointerBeam` produced this same frame. They are peers and none depends on the others — `PlateauChooser` and `PlateauSelection` are both ChasmGame, `ControlListener` is BASH |
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
[`bridgeMovementUpdate.md`](bridgeMovementUpdate.md)); one bridge per gap regardless of owner.
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
player's `TagsRoot`; see [Avatar replication](#avatar-replication).

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
  [The persistent rig](#the-persistent-rig).

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

## The BASH game

Ported from `D:\Unity_Stuff\BASH_U6`; the plan and its corrections are
[`BASHUpdate.md`](BASHUpdate.md). All of it lives in `Assets/Scripts/Bash/`, another `.asmdef`-less
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
- `NetworkBaseControl.activePos`/`activeRot` are board-local, converted back on the way out.
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
`ControlListener.Bind()` opts the scene into `MenuControl.SetKeepPointerAlwaysOn(true)`, the same
way `PlateauSpawnMenu.Bind()` does for Chasms.

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

- `ScoreTag` shows that seat's `PlateauGame.gemheartScores` entry, and only while a Plateau game is
  actually live. Like the nametag it is visible to everyone *except* its owner, because
  `OnNetworkSpawn` deactivates every child of your own avatar.

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
- `playerName` is a `FixedString32Bytes` (29 bytes of UTF-8) and is clamped before the ServerRpc —
  the lobby keyboard has no length limit, and an over-long name would throw *on the server* and
  take the name down for everybody.

## Menus, pointer, keys

`MenuControl` (on `Menu Manager`, one shared instance on `PersistentRig` — see
[The persistent rig](#the-persistent-rig)). **`X` opens and closes** the menu, which is
instantiated 1.3 m in front of the camera and 0.7 m to the left. The laser pointer plus the right
trigger picks a key: pressed on trigger **down**, acted on trigger **up**, so sliding off a key
cancels it.

Keys are dispatched by **`keyInfo.keyName` string**, never by child index. (The menu this replaced
resolved widgets with `GetChild(0).GetChild(9).GetChild(13)`, so re-skinning the prefab broke it
with no compile error.) `pointerControl` reports `keyName`, not the visible label.

`Menu1.prefab` holds seven keys — **`Stairs`**, **`Chasms`**, **`BASH`**, **`Place Anchor`**,
**`Reset Game`**, **`Random Islands`**, **`Voice Chat`**. `Menu2.prefab` holds the other six: the
same list without `Voice Chat`, which is host-only. `OpenMenu1` picks between the two prefabs.

This is **`Menu1`, the room menu — not `SpawnMenu`**, the per-player piece menu on the left wrist,
which is a separate prefab with its own key set and its own dispatcher
(`PlateauSpawnMenu.HandleKey`). See [The spawn menu](#the-spawn-menu--plateauspawnmenu).

**Not every key shows in every scene.** `MenuControl.KeyScene` names the keys that belong to one
game, and `ApplySceneKeyFilter` deactivates the rest on the menu instance as it is opened — which
is why a game's keys cost one row in that dictionary rather than a third menu prefab to keep in
step with the other two. Today only BASH's `Reset Game` and `Random Islands` are scoped; the five
original keys are meaningful everywhere. The `HandleKey` cases still log-and-no-op when their
target is missing, so the filter is a tidiness measure, not the guard.

One key is still one-sided: **`Passthrough`**. `HandleKey` handles it, but no such key exists in
either prefab (the Editor command that added it, `AddPassthroughMenuKey.cs`, was deleted) — the
handler is live and unreachable.

An unknown key is deliberately inert and logs.

`pointerControl` fires on trigger colliders tagged **`key`** and stretches the visible beam to the
hit — but in a game scene `PointerBeam` overwrites the beam length absolutely every frame, and
`MenuControl.keepPointerAlwaysOn` (opted into by `PlateauSpawnMenu.Bind()`, ChasmGame only) leaves
the pointer switched on outside the menu. See [The pointer](#the-pointer) and
[The persistent rig](#the-persistent-rig).

`GrabControl` (on `Left Grabber` / `Right Grabber`) tracks colliders tagged **`Grabbable`**;
nothing is tagged that today, so `WorldGrab.CanStart`'s "two grips with a piece in hand is a piece
grab, not a world grab" check is currently always false — it is there so it does not have to be
retrofitted the day the first piece becomes grabbable.

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
| Right trigger | select a menu / keyboard key; in Chasms, select a piece or a destination plateau or spin the chooser; in BASH, select one of your gamepieces |
| Left trigger | BASH only: fire — lob the arc, then commit the spin-aimed movement line |
| `X` | open / close the menu |
| `A` | re-align to the room anchor (`BoardAnchor.RequestReAlign`) |
| `B` | cancel the current piece selection (Chasms) |
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

## Adding a game

Four edits, by design:

1. A scene with `World Root` (+ `RoomContent`) and board geometry **at the origin**. Nothing else —
   `Input Reader`, `Menu Manager`, `XRRig` and `Directional Light` come from `PersistentRig` for
   free. See [The persistent rig](#the-persistent-rig).
2. A key in `Menu1.prefab` whose `keyInfo.keyName` matches exactly.
3. A `case` in `MenuControl.HandleKey` calling `RequestGame(keyName)`.
4. A row in `GameRoutes.SceneByKey`, and the scene in the build list.

## Conventions that break silently

**`GameObject.Find` by exact name**, resolved at runtime, in many scripts. Renaming any of these
compiles fine and fails at runtime:

`XRRig` · `Network Manager` · `Input Reader` · `Menu Manager` · `Left Hand` · `Right Hand` ·
`Left Grabber` · `Right Grabber`

`RHController`/`LHController` do `GameObject.Find("Input Reader").GetComponent<InputReader>()` with
no null check and will throw outright. `CameraController2` and `WorldGrab` search *descendants of
the rig* by name at any depth rather than by path, because `"Camera Offset/Left Hand"` is exactly
the kind of hardcoded path this project keeps getting bitten by.

**Since `PersistentRig`, a `Find`-by-name break is silent everywhere at once, not just in the scene
being edited** — there is one live instance of each of these names for the life of the app, not one
per scene. The other failure mode is two objects sharing a name simultaneously: if a scene keeps
its own local copy of something `PersistentRig` already provides, `Find` picks one
non-deterministically. That is exactly what happened when `ChasmGame`'s local rig briefly coexisted
with `PersistentRig` mid-migration — `PlateauSpawnMenu.Bind()`'s `Find("XRRig")` sometimes returned
the wrong one and the piece-buying menu could never find itself to turn off. No scene should ever
have its own copy of anything `PersistentRig` provides; if `Find("XRRig")` (or any name above) ever
matches more than one active object, that is the bug, not a `Find` implementation detail.

`PlateauBoard`/`PlateauPieceView`/`PlateauSelection` add to that list: `World Root` · `Plateaus` ·
`Bridges` · `Pieces` · `Pointer` · **`Central Plateau`** (matched by exact string; without it the
bridge rules have no seed and the board falls back to the largest plateau with an error).

**Prefab child names** are equally load-bearing: `TagsRoot` (and `Username`, `ScoreTag`, `Gemheart`
*inside* it), `PlayerLeft`, `PlayerRight`, `mainFace`, `tornado` on `Player.prefab`; `Count` and
`Cube` on `Soldier`, `Parshendi`, `Shardbearer` and `Bridge`; `Cylinder` on `Bridge Spots`. These
used to be `GetChild(1)..GetChild(4)`, so reordering the Hierarchy produced a scrambled avatar with
no error. Now a rename logs one.

Two of those are nested prefab instances whose names are **modification overrides** rather than
plain `m_Name` fields (`face` and `tornado`), so grepping `Player.prefab` for `m_Name:` will not
find them — look for `propertyPath: m_Name`.

**Sibling order under `Plateaus` is the plateau index.** Reordering those 41 children renumbers the
whole board, and the numbers are on the wire. Adding one at the end is safe.

**Tags**: `key` (pointer targets) and `Grabbable` (grabber volumes). `GrabControl` also walks
parents **by name** until it hits `Right Grabber`/`Left Grabber`. BASH adds five more —
`gamepiece`, `obstacle`, `island`, `base`, `line` — and every one of them is matched as a string
in `LineControls`, `ControlListener.ResetBoard`, `NetworkBaseControl.DeleteAllLinesServerRpc` and
`pointerControl`. A missing tag there fails silently: nothing collides, nothing resets.

**Serialized-field renames drop every scene's value silently.** `CameraController2.LeftHand` used
to be `RightHand`; `PersistentRig.prefab` (built from `StairsGame`'s old copy) still has that dead
data under the old key, orphaned, and reads null under the current name. This is why several
components re-resolve a null serialized reference in `Start()` — treat that as the pattern, not as
belt and braces.

**`DefaultNetworkPrefabs.asset` must hold only prefabs under `Assets/`.** `ForceSamePrefabs: 1`
folds every registered prefab's `GlobalObjectIdHash` into the config hash a joining client sends, so
any difference in the *set* is a hard refusal with no reason string. A prefab under a package's
`Editor/` folder exists in the Editor and not in a player build, which is why Editor↔device could
never connect until the eight Meta Building Blocks entries were pruned. `Assets/Editor/NetworkPrefabListGuard.cs`
turns Netcode's auto-generator off and **fails the build** if one gets back in. The same hash also
moves whenever `Player.prefab` is edited, so **flash both headsets from the same build, every time.**

**`NetworkVariable` write permission is the security boundary.** `spawnSlot`, `playerName`,
`roomOwner`, and everything on `RoomAnchor` are **server-written**; hand/head poses are
owner-written. Every `ServerRpc` that mutates shared state validates the sender
(`SetContentServerRpc` checks the lock, `RequestGameServerRpc` checks `GameRoutes`).

**Static state that outlives a scene, and a Play session with domain reload off**:
`CameraController2.LocalIsAligned`, `WorldGrab.IsActive` / `LocalHoldsWorld`,
`PassthroughController.passthroughOn`, `GameSelector.s_SwitchInProgress`,
`GameController.joinCode` / `nickName`. `BoardAnchor.Awake` resets the alignment flag explicitly
for this reason.

**`Instance` singletons** (`BoardAnchor`, `RoomAnchor`, `RoomContent`, `PlateauBoard`,
`PlateauGame`, `PlateauPieceView`) are set in `Awake`/`OnNetworkSpawn` and cleared in
`OnDestroy`/`OnNetworkDespawn` guarded by `if (Instance == this)`. Keep that guard — `RoomAnchor`
and `PlateauGame` despawn and respawn on a reconnect.

## Dead or unwired code

- `NetworkReconnectHandler.cs` is **attached to nothing**. `PersistentObject.cs` used to be in the
  same state but is not any more — it is on `PersistentRig`'s root now, doing exactly the
  `DontDestroyOnLoad` job its one line always promised. The Network Manager still survives scene
  loads because Netcode marks it `DontDestroyOnLoad` itself, independently of `PersistentObject`.
- `Assets/Scenes/GameScene.unity` — legacy, unreachable. Migrated to source its rig from
  `PersistentRig` like every other scene anyway, so it will not silently regress if it is ever
  wired back into `GameRoutes`.
- `MenuControl.worldRoot` is unassigned in every scene, so the board is not hidden behind an open
  menu. Pre-existing and not scene-specific — it was already unset before `PersistentRig` existed.
- Root clutter, not source: `BoardGames.apk`, `build/`,
  `MRBoardGame_BurstDebugInformation_DoNotShip/`, three `.sln` files (`MRBoardGame`,
  `MRBoardGame2`, `MRBoardGameTemplate`), `Assets/Scenes/SampleScene/` (stale baked lighting) —
  and **`BoardAnchor.dll`, a tracked binary at the repo root.** That last one is a trap for
  anything invoking `csc` from the project root: without an explicit `-out:`, Roslyn names the
  output after the first source file, so compiling a file list beginning with `BoardAnchor.cs`
  silently overwrites it and it turns up as a modified binary in `git status` with no obvious
  cause. Always pass `-out:` pointing somewhere outside the repo.

## Design docs

Long-form working documents. They are *plans with running commentary*, and parts
are marked applied, corrected, or out of scope — check the code before trusting a detail.

| File | Covers |
| --- | --- |
| [`plateauRules.md`](plateauRules.md) | The game's rules. Starting forces and movement are implemented; everything else is still the design target. It says 33 plateaus and the scene has 41 — the code counts children, so the doc is the stale one. |
| [`BASHUpdate.md`](BASHUpdate.md) | **Applied.** The plan for porting BASH (`D:\Unity_Stuff\BASH_U6`) in as a third game: the GUID collisions a bulk copy would cause, the world-space → `World Root` local conversion its networking needs, and the scene to build. Its §14 records where the port ended up different from the plan. |
| [`BASHRulesUpdate.md`](BASHRulesUpdate.md) | **Applied.** The rules rework that replaced BASH's joystick-steered shot with spin-aimed movement plus the boat/plane artillery arc — the two orthogonal halves of the four pieces, the `Phase` state machine, and the fixed-segment arc. |
| [`bridgeMovementUpdate.md`](bridgeMovementUpdate.md) | **Applied.** Re-laying a bridge that is already on the board: the local plateau system it may move around, why the search seed (not the graph) was the bug, and the switch of both bridge RPCs from a destination plateau to a gap index. Supersedes `bigFixes1.md` §4. |
| [`bigFixes1.md`](bigFixes1.md) | **§1 applied late** (see `quest_networking_plan.md`), §4 superseded by `bridgeMovementUpdate.md`. Its §1 is still the reference for *why* `ForceSamePrefabs` plus a package `Editor/` prefab makes Editor↔device connection structurally impossible. |
| [`bugFixes2.md`](bugFixes2.md) | **Applied.** The four defects from the first two-headset session (2026-09-05): the 0.25 m anchor-height guard rejecting a real floor-calibration difference, the 6.5 m `TagsRoot` label offsets, the spawn-menu open delay cancelling the board selection, and BASH serving only ring slots 0-3. Its §0 is the "flash both headsets from the same build" warning. |
| [`quest_networking_plan.md`](quest_networking_plan.md) | **Partly applied.** The Quest 3 → Quest 3S join failure: why the original "switch DTLS to UDP" diagnosis did not hold, the double-join defect in `GameController` that did, the connection instrumentation (`NetworkProbe`), and the prefab-list prune from `bigFixes1.md` §1. The cause is still unconfirmed — its last section is the measurement to take in two headsets. |
| [`anchoringUpdate.md`](anchoringUpdate.md) | One anchor per room, the `World Root` content frame, the two-grip world grab. |
| [`fixAnchoring.md`](fixAnchoring.md) | Colocated alignment, the nametag and hand-cone defects, the two-cones-and-a-name avatar. |
| [`updates1.md`](updates1.md) | Earlier pass — root causes and ordering. |
| `MRUpdate.md` — deleted on disk, read it with `git show dcad7e9:MRUpdate.md` | The passthrough conversion. §5 (transparency), §11 (repo landmines) and §14 (Editor-only steps) are still the reference for that work. |
