# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

**MRBoardGame2** — a colocated mixed-reality multiplayer board game for Meta Quest. Several people
stand around a real table in the same physical room, each wearing a headset, and see the same
virtual board sitting on it. The board is placed and resized by grabbing the air with both hands.
Passthrough is on, so players see each other's real faces and hands; the only virtual parts of
another player are two controller cones and a floating nametag.

The project hosts **three games** on one shared platform — colocation, networking, voice, the shared
content frame, the world grab, the player ring and the menu.

## Where to look

Each area below is a closed world. Read this file plus the one row you need — not the whole project.

| Working on | Read |
| --- | --- |
| Shared platform — rig, colocation, world grab, avatars, menus, input, passthrough | [`Assets/Scripts/CLAUDE.md`](Assets/Scripts/CLAUDE.md) + `Assets/Scripts/*.cs` |
| **Plateau** (menu key `Chasms`, scene `ChasmGame`) | [`Assets/Scripts/Plateau/CLAUDE.md`](Assets/Scripts/Plateau/CLAUDE.md) + `Assets/Scripts/Plateau/` |
| **BASH** (menu key `BASH`, scene `BashGame`) | [`Assets/Scripts/Bash/CLAUDE.md`](Assets/Scripts/Bash/CLAUDE.md) + `Assets/Scripts/Bash/` |
| **Stairs** (menu key `Stairs`, scene `StairsGame`) | Nothing — it is a placeholder `Cube` under `World Root > Board`, with no scripts |
| Lobby / join flow | `GameController.cs`, `RelayVivox.cs`, `OpeningScene.unity` |

Nothing in `Assets/Scripts/Plateau/` should be needed to work on BASH, or the reverse. If you find
yourself reading the other game, that is a coupling bug — say so rather than working around it.

**Do not search `Library/`, `Temp/`, `obj/`, `build/`, `.utmp/`, `.vs/`.** `Library/` alone is 25 GB
and holds 5,948 `Editor/*.cs` files that will drown any unscoped glob. Every `.cs` under `Assets/` is
first-party and there are only **52** of them, totalling ~0.5 MB.

## Toolchain and targets

| | |
| --- | --- |
| Unity | **6000.5.4f1** exactly (`ProjectSettings/ProjectVersion.txt`) |
| Render pipeline | URP 17.5.0, Linear color space |
| XR | Meta XR Core SDK **205.0.0** (scoped registry `npm.developer.oculus.com`) + Oculus XR Plugin 4.5.4, via Unity's `XROrigin` — **not** `OVRCameraRig` |
| Networking | Netcode for GameObjects 2.13.2 over Unity Relay (host/client, no dedicated server), tick rate 30. Transport protocol is `RelayVivox.connectionType`, default `dtls` — **it must match on host and joiner**; see [`docs/quest_networking_plan.md`](docs/quest_networking_plan.md) |
| Voice | Vivox 16.10.0, one group audio channel per room |
| Platform | Android / Quest, **ARM64 only**, IL2CPP, minSdk + targetSdk 34 |
| App id | `com.CoolDeal.MRBoardGame2` |
| Devices | Quest 2 / Pro / 3 / 3S (Quest 1 is explicitly dropped — no useful passthrough) |

Stereo rendering on Android is Multiview. Android ships quality level **1 = Balanced** →
`Assets/Settings/URP-Balanced.asset`; the Editor sits on level 2 (High Fidelity). HDR is off on all
three levels on purpose — see [Passthrough](Assets/Scripts/CLAUDE.md#passthrough).

## Opening the project for the first time

Three things will bite, in this order.

**1. Meta XR Core SDK 205.0.0 does not compile on Unity 6000.5.** The project drops into Safe Mode
with `CS0619 ... instanceId is obsolete` from `SceneListenerNGO.cs`. Upstream bug, not ours. Fix:

```powershell
powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
```

The patch lands in the gitignored `Library/PackageCache`, so **re-run it after any fresh clone,
cleared `Library/`, or package re-resolve.** It is idempotent. **If Unity offers Safe Mode, take
it** — "Ignore" opens with unloadable scripts, and re-saving a scene in that state strips components
off `XRRig`.

**2. Active Input Handling must stay "Input Manager (Old)"** (`activeInputHandler = 0`). The Meta SDK
pulls in `com.unity.xr.hands` → `com.unity.inputsystem`, and Unity flips the setting to `2` ("Both")
when that appears, after which the Oculus XR Plugin refuses to build for Android. This project uses
`UnityEngine.XR.InputDevices` for controllers and the legacy `Input` class for the keyboard fallback.

> Unity prompts *"the native platform backends for the new input system are not enabled… enable the
> backends?"* on **every Editor launch**. Always answer **No.** There is no "don't ask again".

**3. `MR Template > MR > Configure Meta Passthrough Project Config`** runs automatically on first
domain load (`Assets/Editor/MRPassthroughSetup.cs`) and is re-runnable from that menu. It writes
`OVRProjectConfig`, a ScriptableObject the SDK creates inside the package folder and which therefore
cannot be committed. Without it, Meta's *Android Manifest Tool* regenerates
`Assets/Plugins/Android/AndroidManifest.xml` and silently strips passthrough, both anchor
permissions, and boundary visibility. The hand-written manifest and that Editor script have to
agree; changing one means changing the other.

## Build and test

- **No CLI tooling, no CI, no tests, no package scripts.** All builds go through the Editor
  (File > Build Settings, Android). `com.unity.test-framework` is in the manifest but there are no
  test assemblies and no `Tests/` folders.
- **Four assemblies, and the boundary is the point.** `MRBoardGame.Shared` (`Assets/Scripts/`)
  references neither game; `MRBoardGame.Plateau` and `MRBoardGame.Bash` each reference Shared and
  **not each other**; `MRBoardGame.Editor` is Editor-only. `Assembly-CSharp` now holds no
  first-party code at all. A game reaching into another game, or shared code reaching into a game,
  is a compile error instead of something you discover in a headset — which is how BASH was caught
  calling `PlateauBoard.FindDescendant`. Editing one game recompiles only that assembly.
- **C# can still be compile-checked headlessly**, without opening the Editor or taking the project
  lock. Since the assembly split there is one `.csproj` per assembly — `MRBoardGame.Shared.csproj`,
  `MRBoardGame.Plateau.csproj`, `MRBoardGame.Bash.csproj`, `MRBoardGame.Editor.csproj`. Take
  `<DefineConstants>` and the `<HintPath>` references from the one you are checking (the Editor
  regenerates them; the `<Compile Include=>` list goes stale, so glob the folder yourself), write a
  csc response file, and run Unity's **.NET** Roslyn —
  `Editor/Data/DotNetSdk/dotnet.exe Editor/Data/DotNetSdk/sdk/<ver>/Roslyn/bincore/csc.dll @rsp`.
  Not `MonoBleedingEdge`'s `csc.exe`, which fails to load `System.Text.Encoding.CodePages` and never
  reaches compilation. A game assembly also needs a `-r:` on the Shared DLL you just built.
  **Always pass an explicit `-out:` pointing outside the repo** — see the `BoardAnchor.dll` trap
  under [Dead or unwired code](#dead-or-unwired-code). Expect pre-existing `CS0618` warnings on
  `ServerRpcAttribute.RequireOwnership` and `FindFirstObjectByType`; ignore them. This checks C#
  only: it does not run Netcode's ILPP, so RPC / `NetworkVariable` codegen problems still need the
  Editor, and it cannot validate scene or prefab YAML wiring.
- **With the Editor already open, just let it compile** — trigger *Assets > Refresh*, wait for the
  domain reload, and read the console. That is the only way to see an assembly-boundary error.
  Compiler messages arrive in the console typed as `Log`, not `Error`, so filter on the text
  `error CS` rather than on severity.
- `Library/ScriptAssemblies/MRBoardGame.*.dll` are only rebuilt when the Editor next opens, so a
  timestamp older than the matching `.cs` files means the current code has **never been compiled**.
  Check that before trusting that edits are sound.
- **Build scene order** (`ProjectSettings/EditorBuildSettings.asset`) is `OpeningScene` (0) →
  `StairsGame` (1) → `ChasmGame` (2) → `GameScene` (3) → `BashGame` (4). Every scene a room can
  switch into must be in this list; `LoadScene` fails with `InvalidSceneName` otherwise, and that
  failure is visible only as a `Debug.LogError` from `GameSelector`.
- **Testing without a headset works.** `InputReader` falls back to the keyboard per hand whenever
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
  (`BoardAnchor.cs:133-141`), so the Editor path is not an error state. Everything except the anchor
  itself — world grab, shared board placement, the ring, avatars — still runs.
- Anything multiplayer needs two running clients. Anything colocation needs **two real headsets in
  one room**, and both must be flashed from the **same build** — see `ForceSamePrefabs` below.
- `.gitattributes` routes `.unity`/`.prefab`/`.asset` through Unity's smart merge
  (`merge=unityyamlmerge`). The driver is registered per machine in `.git/config` and is **not**
  committed. Re-register it after a fresh clone if merges on those files start failing outright.

## Repo state — read this before trusting `git`

**History before `dcad7e9` is a different application.** Everything up to and including that commit
is *Math Classroom*, a VR drawing-and-graphing app. This board game was built on top of it in the
working tree and landed in **one commit** on the `mr-passthrough` branch.

- `git blame` on nearly every file points at that single commit, not at incremental history. The
  design docs below are the closest thing to a rationale trail.
- Git's rename detection paired files off by content similarity across the two applications, so the
  log shows nonsense like `Network Graph.prefab → Bridge Spots.prefab`. Don't read meaning into them.
- `git show dcad7e9:<path>` shows the previous application's version of anything.

## Scenes and session flow

| Scene | Role |
| --- | --- |
| `OpeningScene` | Lobby. VR keyboard, room code + username entry, hosts or joins. Holds the **Network Manager** and the one **`PersistentRig`** instance. |
| `StairsGame` | Default game. `World Root > Board > Cube` — a placeholder. |
| `ChasmGame` | Plateau: 41 `Plateau` and 81 `Bridge Spots` instances under `World Root > Board`. |
| `BashGame` | BASH: a 3 m square of water inside four walls, four islands, a base per player. |
| `GameScene` | **Legacy.** In the build list but absent from `GameRoutes`, so nothing can reach it. |

No game scene has its own rig. `Input Reader`, `Menu Manager`, `XRRig` and `Directional Light` live
once, in `OpeningScene`, under `PersistentRig` — see
[the shared systems doc](Assets/Scripts/CLAUDE.md#the-persistent-rig). A game scene's hierarchy is
just `World Root [RoomContent] > Board > …`, and that is the shape a new game should copy.

**Switching games** goes `MenuControl` → `GameSelector.RequestGame(key)` → `RequestGameServerRpc`,
where the server validates the key against `GameRoutes` (**never** hand a client string to
`LoadScene`) and calls `NetworkManager.SceneManager.LoadScene(name, LoadSceneMode.Single)`.
**Clients must never call `UnityEngine.SceneManagement.SceneManager.LoadScene` while a session is
running.** Full flow, including what survives the switch and what rebinds around it:
[Session flow](Assets/Scripts/CLAUDE.md#session-flow).

Ten components carry a load-bearing `[DefaultExecutionOrder]` (−10, 10, 15, 20, 23, 24, 25, 30). The
chain puts everything that *reads* a world pose after everything that *writes* one — the table is in
[the shared systems doc](Assets/Scripts/CLAUDE.md#execution-order-contract).

## Adding a game

Copy the shape and fill in one asset. **No shared source file is edited, and neither menu prefab.**

1. `Assets/Scripts/<Name>/` for its scripts, and a scene in `Assets/Scenes/`. The scene is
   deliberately thin, because the rig lives elsewhere:

   ```
   World Root            [RoomContent]     <- everything the game owns hangs here
     Board
       ...                                 <- board geometry, AT THE ORIGIN
   ```

   `Input Reader`, `Menu Manager`, `XRRig` and `Directional Light` must **not** be in it — they come
   from `PersistentRig`, and a second object with one of those names makes `Find` pick
   non-deterministically.
2. **An `.asmdef`** in the scripts folder, referencing `MRBoardGame.Shared` and **not** another
   game — copy `Assets/Scripts/Bash/MRBoardGame.Bash.asmdef`. This is what stops the new game
   destabilising the existing ones.
3. **A `GameModule` asset** — *Assets > Create > MR Board Game > Game Module*, saved beside the
   game (see `Assets/Games/Bash/BashModule.asset`). It carries the menu key, the scene name, the
   label, a rules `TextAsset`, any extra menu keys, and whether the laser pointer stays live outside
   the menu.
4. **A row in `Assets/Resources/GameCatalog.asset`**, and the scene in `EditorBuildSettings`.

That is the whole list. `MenuControl` builds the menu key from the catalog, `GameRoutes` reads the
catalog, and the rules panel reads the module — none of them learns the game's name.

Optionally, implement **`IGameSession`** on whatever already owns the game's state and register it
with `GameSessionRegistry` in `Awake`/`OnNetworkSpawn`. That is how a game gets a reset hook when it
is picked (`OnGameSelected`), behaviour behind its own menu keys (`InvokeMenuAction`), and a label
under every other player's nametag (`AvatarBadgeForSeat`). Doing nothing / returning null is the
normal answer — `BashRoot` implements one of the three, `PlateauGame` two, Stairs none.

Put the board **at the origin** and nothing else. If a board needs more room, change
`PlayerRing.Radius` rather than moving boards per scene.

**The two shared things that cannot be avoided**, both only if the game spawns `NetworkObject`s:
`Assets/DefaultNetworkPrefabs.asset` (`ForceSamePrefabs` folds the prefab *set* into the join
handshake, so it is global by construction) and the build scene list. Plateau sidesteps the first by
putting its state on the already-registered `Room Anchor.prefab` and rebuilding pieces as local
visuals; BASH does not, and registers `Base` and `NetworkCannonLine`.

## Conventions that break silently

**`GameObject.Find` by exact name**, resolved at runtime, in many scripts. Renaming any of these
compiles fine and fails at runtime:

`XRRig` · `Network Manager` · `Input Reader` · `Menu Manager` · `Left Hand` · `Right Hand` ·
`Left Grabber` · `Right Grabber` · `World Root` · `Controls`

`RHController`/`LHController` do `GameObject.Find("Input Reader").GetComponent<InputReader>()` with
no null check and throw outright. Since `PersistentRig`, a `Find`-by-name break is silent
**everywhere at once**, not just in the scene being edited — there is one live instance of each name
for the life of the app. The other failure mode is two objects sharing a name simultaneously: no
scene should ever have its own copy of anything `PersistentRig` provides.

Plateau adds `Plateaus` · `Bridges` · `Pieces` · `Pointer` · **`Central Plateau`** (exact string;
without it the bridge rules have no seed).

**Prefab child names** are equally load-bearing: `TagsRoot` (and `Username`, `ScoreTag`, `Gemheart`
*inside* it), `PlayerLeft`, `PlayerRight`, `mainFace`, `tornado` on `Player.prefab`; `Count` and
`Cube` on `Soldier`, `Parshendi`, `Shardbearer` and `Bridge`; `Cylinder` on `Bridge Spots`. Two of
those (`face`, `tornado`) are nested-prefab **modification overrides**, so grepping `Player.prefab`
for `m_Name:` will not find them — look for `propertyPath: m_Name`.

`Menu1.prefab` / `Menu2.prefab` add three: **`Row1`** (the container every key is parented to) and
the two inactive keys **`GameKeyTemplate`** and **`ActionKeyTemplate`**, which `MenuControl` clones
once per catalog entry. They exist so the generated keys inherit the authored collider, rigidbody,
materials and label scale rather than having them set from code — `Key.prefab` is the *lobby
keyboard's* key and its label sits at a different offset. Delete or rename one and the menu opens
empty; `MenuControl` logs an error rather than failing silently.

**Sibling order under `Plateaus` is the plateau index.** Reordering those 41 children renumbers the
whole board, and the numbers are on the wire. Adding one at the end is safe.

**Tags**: `key` (pointer targets) and `Grabbable` (grabber volumes); `GrabControl` also walks parents
**by name** until it hits `Right Grabber`/`Left Grabber`. BASH adds `gamepiece`, `obstacle`,
`island`, `base`, `line`, every one matched as a string. A missing tag fails silently.

**Serialized-field renames drop every scene's value silently**, and a serialized value **wins over
the field initializer** — raising a default in a `.cs` file is not enough on its own when
`PersistentRig.prefab` carries its own copy. This is why several components re-resolve a null
serialized reference in `Start()`; treat that as the pattern, not as belt and braces.

**`DefaultNetworkPrefabs.asset` must hold only prefabs under `Assets/`.** `ForceSamePrefabs: 1` folds
every registered prefab's `GlobalObjectIdHash` into the config hash a joining client sends, so any
difference in the *set* is a hard refusal with **no reason string** — it surfaces only as a join that
hangs on "Joining room…". `Assets/Editor/NetworkPrefabListGuard.cs` turns Netcode's auto-generator off
and **fails the build** if a foreign entry gets back in. The same hash moves whenever `Player.prefab`
is edited, so **flash both headsets from the same build, every time.**

**`NetworkVariable` write permission is the security boundary.** `spawnSlot`, `playerName`,
`roomOwner` and everything on `RoomAnchor` are **server-written**; hand and head poses are
owner-written. Every `ServerRpc` that mutates shared state validates the sender.

**Static state outlives a scene**, and a Play session with domain reload off:
`CameraController2.LocalIsAligned`, `WorldGrab.IsActive` / `LocalHoldsWorld`,
`PassthroughController.passthroughOn`, `GameSelector.s_SwitchInProgress`, `GameController.joinCode` /
`nickName`. `BoardAnchor.Awake` resets the alignment flag explicitly for this reason.

**`Instance` singletons** (`BoardAnchor`, `RoomAnchor`, `RoomContent`, `PlateauBoard`, `PlateauGame`,
`PlateauPieceView`, `BashRoot`) are set in `Awake`/`OnNetworkSpawn` and cleared in
`OnDestroy`/`OnNetworkDespawn` guarded by `if (Instance == this)`. Keep that guard — `RoomAnchor` and
`PlateauGame` despawn and respawn on a reconnect.

**There are still no namespaces.** Every class sits in the global namespace, so a name that collides
with something in a *referenced* assembly is a real hazard — `HierarchyUtils` is named that rather
than `Hierarchy` because Unity 6 added a `Unity.Hierarchy` namespace. The assembly boundaries below
mean two *games* can no longer collide with each other, which was the worse case.

## Dead or unwired code

- `NetworkReconnectHandler.cs` is **attached to nothing**.
- `Assets/Scenes/GameScene.unity` — legacy, unreachable, still at build index 3.
- `Assets/Scenes/SampleScene/` — lighting data for a scene that no longer exists.
- `MenuControl.worldRoot` is unassigned in every scene, so the board is not hidden behind an open
  menu. `MenuControl` also has a live `Passthrough` handler that nothing builds a key for — to make
  it reachable, generate one in `BuildKeys` beside `Place Anchor`.
- `PlayerControls` resolves the badge label's icon child as **`Gemheart`**, a name Plateau chose,
  even though what the label says is now the loaded game's business (`IGameSession.AvatarBadgeForSeat`).
  Cosmetic; renaming it means editing `Player.prefab`, which moves the join config hash.
- Root clutter, not source: `BoardGames.apk`, `build/`, `*_BurstDebugInformation_DoNotShip/`, three
  `.sln` files — and **`BoardAnchor.dll`, a tracked binary at the repo root.** That last one is a
  trap for anything invoking `csc` from the project root: without an explicit `-out:`, Roslyn names
  the output after the first source file, so compiling a list beginning with `BoardAnchor.cs`
  silently overwrites it and it turns up as a modified binary in `git status` with no obvious cause.

## Design docs

Long-form working documents in [`docs/`](docs/). They are *plans with running commentary*, and parts
are marked applied, corrected, or out of scope — **check the code before trusting a detail.**

| File | Covers |
| --- | --- |
| [`plateauRules.md`](docs/plateauRules.md) | Plateau's rules. Starting forces and movement are implemented; the rest is still the design target. It says 33 plateaus and the scene has 41 — the code counts children, so the doc is the stale one. |
| [`BASHRules.md`](docs/BASHRules.md) | BASH's rules. |
| [`stepsRules.md`](docs/stepsRules.md) | Stairs' rules. |
| [`BASHUpdate.md`](docs/BASHUpdate.md) | **Applied.** Porting BASH in as a third game: GUID collisions, the world-space → `World Root` local conversion, the scene to build. §14 records where the port differed from the plan. |
| [`BASHRulesUpdate.md`](docs/BASHRulesUpdate.md) | **Applied.** The rules rework that replaced BASH's joystick-steered shot with spin-aimed movement plus the boat/plane artillery arc. |
| [`bridgeMovementUpdate.md`](docs/bridgeMovementUpdate.md) | **Applied.** Re-laying a bridge already on the board. Supersedes `bigFixes1.md` §4. |
| [`bigFixes1.md`](docs/bigFixes1.md) | §1 applied late, §4 superseded. Its §1 is still the reference for *why* `ForceSamePrefabs` plus a package `Editor/` prefab makes Editor↔device connection structurally impossible. |
| [`bugFixes2.md`](docs/bugFixes2.md) | **Applied.** The four defects from the first two-headset session. Its §0 is the "flash both headsets from the same build" warning. |
| [`quest_networking_plan.md`](docs/quest_networking_plan.md) | **Partly applied.** The Quest 3 → Quest 3S join failure. The cause is still unconfirmed — its last section is the measurement to take. |
| [`anchoringUpdate.md`](docs/anchoringUpdate.md) | One anchor per room, the `World Root` content frame, the two-grip world grab. |
| [`fixAnchoring.md`](docs/fixAnchoring.md) | Colocated alignment, the nametag and hand-cone defects, the two-cones-and-a-name avatar. |
| [`updates1.md`](docs/updates1.md) | Earlier pass — root causes and ordering. |
| `MRUpdate.md` — deleted on disk, read with `git show dcad7e9:MRUpdate.md` | The passthrough conversion. §5, §11 and §14 are still the reference for that work. |
