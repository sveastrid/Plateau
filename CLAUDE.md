# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Multiplayer VR math classroom for Meta Quest. Students and a teacher share a room where they draw 3D shapes in the air with controllers and plot 3D function surfaces, with live voice chat.

- **Unity 2023.1.10f1** (exact version — `ProjectSettings/ProjectVersion.txt`), URP
- **Target**: Android / Quest, ARMv7 + ARM64, minSdk 23 (`AndroidTargetArchitectures: 3`)
- **Networking**: Netcode for GameObjects 1.5.2 over Unity Relay (host/client, DTLS — no dedicated server) + Vivox 16.3.0 for voice

## Build and test

There is no CLI tooling, package manifest script, or CI in this repo — all builds go through the Unity Editor (File > Build Settings, Android platform).

- **No automated tests exist.** `com.unity.test-framework` is in `Packages/manifest.json`, but there are no test assemblies, no `Tests/` folders, and no `.asmdef` files anywhere in `Assets/`. All runtime code compiles into the default `Assembly-CSharp`. Adding a test suite means creating the assembly definitions first.
- **Build scenes** (`ProjectSettings/EditorBuildSettings.asset`) must stay in this order: `Assets/Scenes/OpeningScene.unity` (index 0, the lobby) then `Assets/Scenes/SecondScene.unity`. `GameController` hard-codes `SceneManager.LoadScene("SecondScene")`.
- **Testing without a headset**: `InputReader` falls back to keyboard input whenever no XR controller is detected, so the whole app is playable in the Editor. The mapping is documented at the top of `Assets/Scripts/InputReader.cs` — trigger is `.` (right) / `,` (left), grips are `P` / `Q`, face buttons are `A`/`B`/`X`/`Y`, joysticks are arrow keys (right) and `UHJK` (left), and `M`/`N` tilt the camera. Multiplayer paths still need two running clients.
- `git` is not on `PATH` in the default PowerShell session on this machine; invoke it by full path or use a shell where it resolves.

## Architecture

### Session flow

`OpeningScene` is a lobby with a VR keyboard. `GameController.Update()` reads which key the pointer is touching and commits it on trigger press: first a room code, then a username. On the final Enter:

- **empty room code** → `RelayVivox.CreateRelay()` allocates a Relay slot for 12, gets a join code, `StartHost()`. This client becomes the **room owner** (teacher).
- **non-empty code** → `RelayVivox.JoinRelay()` → `StartClient()`. A bad code throws `RelayServiceException`, caught in `GameController.TryToJoinRelayVivox()` to re-prompt.

Vivox then joins a group audio channel named after the room code. The NetworkManager/RelayVivox object survives the scene load via `PersistentObject` (`DontDestroyOnLoad`). Everything else happens in `SecondScene`.

Room-owner status gates most UI: `MenuControl` opens `Menu1` for the owner and `Menu2` for students, and only the owner can assign seats or delete everyone's drawings.

### Dual-copy drawing replication

This is the least obvious part of the codebase and the easiest to break. Every drawing exists **twice**: a local copy the author draws against with zero latency, and a networked twin that everyone else sees.

1. On join, `NetworkLineDrawer.OnNetworkSpawn()` calls `SpawnDrawingsObjectServerRpc` — the server spawns a per-client "Network Drawings" container owned by that client. All of that client's networked drawings are parented to it.
2. Finishing a stroke calls `CreateNetworkLine` → ServerRpc → server instantiates the prefab, `SpawnWithOwnership(sender)`, parents it, then ClientRpcs the point array and material index out.
3. **Local↔network pairing** is positional, not by ID: `LineDrawer` enqueues each new local line into `Queue<GameObject> UnpairedLines`, and the networked twin's `NetLineControl.Start()` dequeues on the owner and stamps `pairedNetLineId`. **This depends on network spawn order matching local enqueue order.** Every later move/delete resolves through `pairedNetLineId`, so anything that enqueues out of order silently mispairs drawings.
4. **Late joiners** are caught up by `LateSyncLinePointsServerRpc`, which walks every `"Drawings"`-tagged object and replays it to that one client via a targeted `ClientRpcParams`. Graphs replay as *function string + bounds*, not vertices — cheaper, and it re-derives the mesh client-side.

### Line geometry

`PipeRenderer` replaces Unity's `LineRenderer` with a procedural tube mesh — a 12-sided cylinder swept along the point list with rounded caps, rotated segment-to-segment via `Quaternion.FromToRotation`. This exists so lines carry real `MeshCollider`s and can be physically grabbed. Colliders are only synced on demand via `SyncCollisionMesh()` (drawing sets `autoCreateCollider = false` and syncs once on release) — regenerating them every frame is a performance cliff.

`LineDrawer.Update()` is a state machine on the `currentAction` string: `listening`, `drawingRH`/`drawingLH`, `deletingLines`, `changingLineWidth`, `grabbingRH`/`grabbingLH`, `resizingDrawings`. `MenuControl.Update()` early-returns unless `currentAction == "listening"`, so menus and drawing are mutually exclusive. Two-handed resize reparents the whole `Drawings` tree under `Middle` (the midpoint of both hands, maintained by `MiddleControl`) and scales by the ratio of hand distance.

### Graphing

- `FunctionEvaluator` — hand-written recursive expression parser, no library. Handles `+ - * / ^`, nested parens, `sin`/`cos`/`tan`, `p` for pi, `e`, and the variables `x` and `y`. `EvaluateInOrder` applies precedence by repeatedly collapsing the highest-priority operator out of parallel number/operator lists. Its `InvalidOperationException` messages are user-facing — they surface directly into the VR menu's error text, so keep them plain-language.
- `FunctionRenderer` — samples a 101x101 grid and builds the `z = f(x,y)` surface mesh. **Axis convention flips here**: this class treats z as vertical, but Unity's y is up, so vertices are written `new Vector3(xi, zi, yi)`. It also feeds `_TopHeight`/`_BottomHeight` to the material for height-gradient shading. NaN and values past a ±10000 cutoff clamp to 0.
- `GraphAxisControl` — axes as `PipeRenderer`s plus an 11x11 grid and TMP labels, handling the case where the origin falls outside the plotted range (axes snap to the nearest edge). `SetAxesAutoScale` fits into a fixed box (1.2 units, or 12 in "big" mode); `SetAxes` uses literal step sizes.

### Seating

`seatControl.makeSeats()` generates three rows of a semicircular arc at radius 2. The room owner always gets `(0, 0, -1)`; everyone else is assigned by their index in `NetworkManager.ConnectedClients` (`PlayerControls.FindMySeatServerRpc`). Note the arc yields ~30 seats but Relay is allocated for 12. When the teacher toggles assigned seats on, `CameraController2.Update()` locks each player to their seat and free movement is disabled.

`PlayerControls` syncs head and both hands as owner-writable `NetworkVariable<Vector3>` position/forward pairs; the display name is server-writable and set through a ServerRpc. Your own avatar's children are disabled locally so you don't see your own floating head.

## Conventions that break silently

- **`GameObject.Find` by exact name**, resolved at runtime across many scripts. Renaming any of these in `SecondScene` compiles fine and fails at runtime: `XRRig`, `Network Manager`, `Input Reader`, `Left Hand`, `Right Hand`, `Left Grabber`, `Right Grabber`, `Seating Manager`, `Menu Manager`, `Scene Two Manager`, `Drawings`.
- **Tags** drive replication and grabbing: `Drawings`, `Graph`, `Axes`, `Line`, `key`. `GrabControl` also walks parents by *name* until it hits `Drawings`/`Right Grabber`/`Left Grabber`.
- **Menu wiring uses hardcoded child indices** — e.g. `currentMenu.transform.GetChild(0).GetChild(9).GetChild(13)` in `MenuControl`. Reordering children in the menu prefabs breaks the menu with no compile error.
- **`MenuControl` keeps graph state in `static` fields** (function string, bounds, step sizes, scale flags) so it persists across menu opens — and across scene reloads within a session.
- Material/color selection is an index into `Material[]` arrays on `LineControl`/`NetLineControl`/dot controllers; the default is `4` in several places.

## Repo layout note

Nine `.apk` files, eight `*_BurstDebugInformation_DoNotShip/` folders, `Math_Classroom_Web_Build/`, and `v2_windows_build/` sit at the repo root. These are stale build artifacts, not source — `.gitignore` covers `/Build/` and `/Builds/` but not these names. All real code is the 33 files in `Assets/Scripts/`. `Assets/TutorialInfo/` is leftover Unity URP template sample content.

`CameraController.cs` is the superseded pre-seating version of `CameraController2.cs`; only the latter is wired up. `AxisControl.cs` (LineRenderer-based) is likewise superseded by `GraphAxisControl.cs` (PipeRenderer-based).
