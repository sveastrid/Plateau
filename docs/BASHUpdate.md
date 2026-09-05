# BASHUpdate.md — bringing BASH into MRBoardGame2 as a third game

> **Status: applied.** Everything below §1–§10 has been done except where §14 says otherwise —
> read [§14](#14-where-the-port-came-out-different) before trusting a detail in the body, because
> four things in the plan turned out to be wrong about the source scene or about Netcode. The
> verification list in §11 has **not** been run: none of it can be, without a headset and a second
> client. See §11 for what to do first.

A plan, in the shape of the other docs in this folder: what BASH is, what it assumes, where those
assumptions collide with this project, and the ordered work to resolve them. Read
[`CLAUDE.md`](../CLAUDE.md) first — especially [Adding a game], [The persistent rig] and
[The content frame and the two-grip world grab]. This document is the BASH-specific delta on top of
those four steps, and it is longer than four steps because BASH was written against the
architecture this project had **before** `PersistentRig`, `World Root` and colocation existed.

---

## 0. The short answers

**Do you need to copy or export anything by hand?** **No.** The BASH project is on this machine at
`D:\Unity_Stuff\BASH_U6` and is readable directly. There *is* a file-copy step (§4), but it is a
precise allow-list that can be scripted — see §3 for why a bulk copy would quietly corrupt this
project.

**Is the Unity Editor reachable?** Yes. The Unity MCP bridge is live and attached to
`C:/Users/Sam/Documents/Unity/MRBoardGame2` on Editor `6000.5.4f1`. That means the scene, the
components, the tags and the reference wiring can all be done through the bridge rather than by
hand. §10 splits the work into *bridge-doable* and *genuinely yours*.

**Source project, confirmed.** `D:\Unity_Stuff\BASH_U6` — Unity `6000.0.31f1`, Meta XR SDK
`78.0.0`, Netcode for GameObjects `2.13.0`, URP `17.5.0`. Gameplay lives entirely in
`Assets/Scenes/Second Scene.unity`. There are two other BASH folders on `D:` (`BASH`,
`BASH_bad_one`); both are from 2024 and neither is this one. **If `BASH_U6` is not the version you
mean, stop and say so — most of the specifics below come from reading it.**

**Decisions taken** (asked and answered before this document was written):

| | Decision |
| --- | --- |
| Integration depth | **Full citizen.** The board hangs under `World Root`, so the two-grip world grab and colocation work exactly as they do in Chasms. This is the one real engineering task — see §5. |
| Menu | **Fold into the shared menu.** `BASHMenu.prefab` and `ControlListener`'s menu state machine are deleted; `Reset Game` and `Random Islands` become keys on `Menu1`/`Menu2` dispatched by `MenuControl.HandleKey`. |
| Players | **First four play, the rest spectate.** Base assignment moves from `NetworkManager.ConnectedClients.Count` to `PlayerControls.spawnSlot`, which also fixes an existing reconnect bug. |
| Random Islands | **Finish it.** The C# is already written and correct; only three Editor steps were never done. |

---

## 1. What BASH is

Four players sit around a 3 m square board. Each owns a **base** carrying four gamepieces — boat,
plane, sub, helicopter — in the player's colour. You point at one of your pieces with the right
trigger to select it, then **hold the left trigger** to fire: a tube of geometry (`PipeRenderer`)
grows out of the piece's cannon at an accelerating speed, steered left/right by the right joystick.
Release, and your piece teleports to the end of the trail.

The trail is a live collider while it is being drawn. What it touches decides what happens:

| Tag hit | Effect |
| --- | --- |
| `gamepiece` | that piece is destroyed (`TurnOffGamepiece`) — this is how you kill people |
| `obstacle` | walls and base pads: your own shot ends there and **your** piece dies |
| `island` | boats (piece 0) and subs (piece 2) die; planes and helicopters fly over |

Four islands sit on the board as obstacles. `Reset Game` restores every base and deletes every
trail. `Random Islands` re-scatters the islands.

### The code

`D:\Unity_Stuff\BASH_U6\Assets\Scripts\`, seven files that matter:

| File | Role |
| --- | --- |
| `ControlListener.cs` | The whole input loop and the firing state machine. Scene object `Controls`. |
| `NetworkBaseControl.cs` | One per base. `NetworkVariable<Vector3> activePos/activeRot` (owner-written) plus a fleet of ServerRpc/ClientRpc pairs for piece visibility, selection rings and reset. |
| `SpawnManager.cs` | Spawns a `Base` per connected client and a `NetworkCannonLine` per shot. |
| `PipeRenderer.cs` | Procedural tube mesh + `MeshCollider` from a point list. |
| `LineControls.cs` | The trail's `OnTriggerEnter` — the three rules in the table above. |
| `IslandManager.cs` | Randomised island layout with late-join sync. Written, never wired up. |
| `seatControl.cs` | The old seating ring. **Not ported** — `PlayerRing` replaces it. |

Everything else in that folder (`InputReader`, `keyInfo`, `pointerControl`, `PlayerControls`,
`CameraController2`, `GameController`, `RelayVivox`, `DebugLog`, `GrabControl`, `LHController`,
`RHController`, `MicPermissions`, `VisibleWhenLooking`) is the **shared template heritage** — this
project has its own, further-developed copies. Do not bring any of them across.

---

## 2. The one genuinely lucky fact

Both projects descend from `Multiplayer_Template_U6`, and **every shared script has an identical
GUID in both projects**:

```
CameraController2  e4ecd317378d0764cb6e8580592567bf   InputReader     69986d10d78827e40bab68b8507bb69f
DebugLog           8498537441a0c4f43afce156a627ff99   keyInfo         b217a1a81c2228343a6e6a694c0acec9
GameController     8d53792d7e5a1f945be4c55acd5eec95   PlayerControls  4fc0fbf8090e9954699a65e46a71559d
GrabControl        98d5a4176a7ae9b4086bd10e5dffa571   pointerControl  0bc03be8f6775c949acc95f2376350f3
LHController       3d208b94a6a84ff41924c6d69122fd1a   RelayVivox      f97c0ce1518575f47ae8959f4dc4e228
MicPermissions     cde0e03084479184889f42b91aa07a4d   RHController    5add22760f684254496ab0115e3058af
                                                      VisibleWhenLooking afe710f13ef6d3d4aab5d2c9ba9fa1d6
```

So a BASH prefab copied into this project resolves `keyInfo`, `InputReader` and friends to *this*
project's versions with no rewiring at all. The six BASH-only scripts keep their own GUIDs when
copied with their `.meta` files, so `Base.prefab` finds `NetworkBaseControl` too.

Two more freebies:

- **This project's `InputReader` is a strict superset of BASH's.** The diff is entirely bug fixes
  (device filtering on `Controller | Left/Right`, and clearing the `*Down` edges so a one-frame
  press cannot stick). Every field `ControlListener` reads — `ButtonXDown`, `leftJoystick`,
  `rightJoystick`, `LeftJoystickButton`, `LeftMainTrigger`/`Down`/`Up`,
  `RightMainTriggerDown`/`Up` — exists here already. **No change needed.**
- **The `BASH` menu key already exists** in `Menu1.prefab` (line 472) and `Menu2.prefab`
  (line 1539), tagged `key`, with `keyInfo.keyName: BASH`. It is currently inert and falls through
  to `MenuControl.HandleKey`'s `default` case, which logs
  *"'BASH' pressed — no game is wired to that key yet."* — exactly as designed.

  > `CLAUDE.md` says `Menu1.prefab` holds three keys. That is stale: it holds five
  > (`Voice Chat`, `BASH`, `Place Anchor`, `Chasms`, `Stairs`), and `Menu2` holds four. Worth
  > correcting when `CLAUDE.md` is next touched.

---

## 3. Why a bulk copy would break this project

The same shared heritage that makes §2 work also means **thirty assets carry the same GUID in both
projects with different content**. Copying `Assets/Prefabs/` or `Assets/Materials/` wholesale would
overwrite this project's versions, and every scene reference would silently retarget.

The ones that would actually hurt:

| GUID | In BASH | In MRBoardGame2 | If overwritten |
| --- | --- | --- | --- |
| `9243d030…` | `Player.prefab` | `Player.prefab` | Catastrophic. This project's `Player.prefab` carries `PlayerControls`, `GameSelector` and `ClientNetworkTransform` with the `Username`/`PlayerLeft`/`PlayerRight`/`mainFace`/`tornado` children. BASH's is the old template version. The `NetworkManager`'s player prefab, avatars, nametags and game switching all go at once. |
| `39fb5288…` | `Pointer.prefab` | `Pointer.prefab` | Loses `PointerBeam`, the `Ignore Raycast` layer on `Visible Pointer`/`Dot`, and the Chasms board targeting with it. |
| `d123f9ca…` | `Grabber.prefab` | `Grabber.prefab` | Breaks `GrabControl` / `WorldGrab.CanStart`. |
| `99c9720a…` | `Opening Scene.unity` | `OpeningScene.unity` | Replaces the lobby — and the only `PersistentRig` and `Network Manager` instance in the project. |
| `68fcb37f…` | `Space2.mat` | `Space.mat` | Different filename, same GUID. Breaks `PassthroughController.vrSkybox`. |
| `1e2691bf…`, `bce145d4…` | `OculusTouchForQuest2_*` | same | Controller models regress to SDK 78 versions. |
| `1c7ecdf1…`, `b66423ed…`, `48c7f88a…`, `c549bd03…`, `3b4ad889…`, `945792df…`, `294ef473…` | `Stars.png`, `InfoBlockMat 1.mat`, `Pointer.mat`, `OffKey.mat`, `OnKey.mat`, `ClearWhite.mat`, `tornado.fbx` | same | Cosmetic regressions, plus `ClearWhite.mat` is load-bearing for `PlateauTint` (see `CLAUDE.md`, *Highlighting*). |

**Rule: copy only the explicit allow-list in §4. Never `robocopy` a folder.**

---

## 4. Step 1 — bring the assets across

This is the transitive dependency closure of `Base.prefab`, `Island.prefab`, `CannonLine.prefab`
and `NetworkCannonLine.prefab`, computed by walking every `guid:` reference and dropping anything
this project already has. **Copy each file together with its `.meta`** — the `.meta` is what
preserves the GUID, and without it Unity mints a new one and every reference inside the prefabs
breaks.

**Scripts** → `Assets/Scripts/Bash/` (a subfolder with no `.asmdef` still compiles into
`Assembly-CSharp`, exactly like `Assets/Scripts/Plateau/`):

```
ControlListener.cs        IslandManager.cs        LineControls.cs
NetworkBaseControl.cs     PipeRenderer.cs         SpawnManager.cs
```

Not copied, deliberately: `seatControl.cs`, `CameraController.cs`, and every file in §2's table.

**Prefabs** → `Assets/Prefabs/Bash/`:

```
Base.prefab   Island.prefab   CannonLine.prefab   NetworkCannonLine.prefab
```

**Models** → `Assets/Prefabs/Bash/` (the `.obj` files and their `.mtl` siblings; the `.mtl` has no
`.meta` of its own but Unity's OBJ importer wants it beside the `.obj`):

```
ship.obj.obj + .mtl     plane.obj.obj + .mtl     sub.obj.obj + .mtl
heli2.obj.obj + .mtl    island2.obj.obj + .mtl   ring.obj    obj.mtl
```

**Materials and textures** → `Assets/Materials/Bash/`:

```
CannonHit.mat  HoverRing.mat  SelectedRing.mat  Walls.mat  Water.mat
island.mat     Rocks.mat      Player1.mat  Player2.mat  Player3.mat  Player4.mat
CartoonWater.shadergraph   DepthFade.shadersubgraph   Movement.shadersubgraph
white_noise.jpeg   grass_height.jpg   grass_normal.jpg   grass_normal2.jpg
```

`island.mat` and `Rocks.mat` are not referenced by `Island.prefab` directly — they are **material
remaps inside `island2.obj.obj.meta`**, which is why they must travel with it.

Not copied: `Board.prefab` (stale — it has `Bases P1..P4` baked in from an older static version;
the live board is the *scene* object, rebuilt in §7), `BASHMenu.prefab` (folded into the shared
menu), `Game Manager.prefab`, `Seating Manager.prefab`, `XRRig.prefab`, `XRRig2.prefab`,
`Keyboard New.prefab`, `Text Input New.prefab`, `MenuBackground.mat`, `New Material.mat`,
`Space2.mat`, `cartoon_space.jpg`, `space.jpg`, `jupiter.fbx`, `saturn2.fbx`.

After copying, refresh the AssetDatabase (the bridge can do this) and confirm zero import errors
before touching anything else.

---

## 5. Step 2 — the coordinate problem

**This is the one part that is real engineering rather than wiring, and it is the reason "full
citizen" costs more than "minimal port".**

BASH was written for a world that never moved. Its board sat at world `(0, 0.6, 2)` on a
**Device**-origin rig with `m_CameraYOffset: 1.36144`, and every networked value in it is a plain
world-space `Vector3`:

- `NetworkBaseControl.activePos` is written as a world position and applied as
  `activeGamepiece.transform.position = newVal`.
- `SyncOverNetworkServerRpc` sends `tempGamePiece.position` and `.forward`, both world.
- `ControlListener` feeds `netBaseControl.activeGamepiece.transform.GetChild(3).position` — a world
  point — straight into `PipeRenderer.SetPositions`, which treats its input as **mesh vertices in
  the pipe object's local space**. That only works because the `CannonLine` object is instantiated
  at the origin with identity rotation and scale.

In this project `World Root` moves, rotates and rescales continuously under the two-grip world grab
(`RoomContent`, order 15; `WorldGrab`, order 20; scale clamped 0.15–4 by `RoomAnchor`). World-space
values for anything parented under it go stale the instant somebody grabs the board. This is the
same problem `PlateauBoard` already solved, and the solution is the same one `CLAUDE.md` states:

> Everything is measured in **`World Root` local space**, which is what makes it invariant under the
> world grab — nothing is ever re-baked when the board moves or resizes.

### The shape of the fix

Add a scene-placed `NetworkObject` under `World Root` — call it **`Bash Root`** — and parent every
runtime-spawned BASH object to it via `NetworkObject.TrySetParent(bashRoot, worldPositionStays:
false)` on the server. Netcode replicates the parenting, so every client's bases and trails inherit
`World Root`'s pose and scale for free. Then, mechanically:

| Currently | Becomes |
| --- | --- |
| `activeGamepiece.transform.position = newVal` | `.localPosition = newVal` |
| `activeGamepiece.transform.rotation = Quaternion.LookRotation(newVal)` | `.localRotation = Quaternion.LookRotation(newVal)` |
| `tempGamePiece.position` / `.forward` in `SyncOverNetworkServerRpc` | `.localPosition` / `parent.InverseTransformDirection(.forward)` |
| `…GetChild(3).position` fed to `PipeRenderer` | `bashRoot.InverseTransformPoint(…GetChild(3).position)` |
| `Instantiate(cannonLine)` at the origin | instantiate as a child of `bashRoot`, identity local transform |

The cannon-speed constants (`initialCannonSpeed 0.5`, `cannonAcceleration 0.1`) then become
board-local units, so a shot crosses the same fraction of the board no matter how big the players
have made it. That is the behaviour you want, and it falls out for free.

### The rebasing arithmetic

Subtract the old board origin `(0, 0.6, 2)`. `SpawnManager`'s four hard-coded base positions become:

| Seat | BASH world | Board-local |
| --- | --- | --- |
| 0 | `( 0,   .6,  .6)`, rot `0°`    | `( 0, 0, -1.4)` |
| 1 | `( 0,   .6, 3.4)`, rot `180°`  | `( 0, 0,  1.4)` |
| 2 | `( 1.4, .6, 2  )`, rot `-90°`  | `( 1.4, 0, 0)` |
| 3 | `(-1.4, .6, 2  )`, rot `90°`   | `(-1.4, 0, 0)` |

**These are exactly `IslandManager.baseExclusionZones` already** — `(0,-1.4)`, `(0,1.4)`, `(1.4,0)`,
`(-1.4,0)`. `IslandManager` was written in board-local space from the start and needs **no change at
all**. That is the confirmation that the rebasing above is right.

### Board size and the player ring

The board is 3 m across (`Water` = a unit cube at scale `(3, 0.02, 3)`; four walls at `±1.52`).
`PlayerRing.Radius` is 2 m, so at `contentScale = 1` players stand half a metre outside the board
edge, facing in. **The ring needs no change.**

One nuisance to know about: `RoomAnchor.contentPos/Yaw/Scale` lives on `Room Anchor.prefab`, which
survives every `LoadSceneMode.Single` switch. So BASH inherits whatever placement and scale the room
last set in Chasms, and vice versa. If the two boards have very different footprints at
`contentScale = 1`, switching games rescales the table under the players. Author `Board`'s **local**
scale in the BASH scene so its footprint matches `ChasmGame`'s at `contentScale = 1`, and the
problem disappears without any new networked state.

---

## 6. Step 3 — the script changes

### `Assets/Scripts/pointerControl.cs` — additive, ~12 lines

This project's copy lost BASH's gamepiece branch. Restore it verbatim:

```csharp
public GameObject currentGamepiece;
// in OnTriggerEnter, beside the existing "key" branch:
else if (col.gameObject.tag == "gamepiece")
{
    col.transform.GetChild(1).gameObject.SetActive(true);   // hover ring
    currentGamepiece = col.gameObject;
}
// and the mirror in OnTriggerExit
```

This is the right call rather than routing selection through `PointerBeam`: BASH's gamepiece
colliders are **triggers** (`isTrigger: 1` on all four capsules) and `PointerBeam` raycasts with
`QueryTriggerInteraction.Ignore`, so it would never see them — and relaxing that would make the
beam hit its own capsule and both grabber volumes. The gamepieces already carry the `Rigidbody`
that Unity trigger callbacks need.

Known limitation, inherited: `pointerControl` tracks **one** target with no distance sorting. With
four well-separated pieces per base that is fine; at the smallest board scale (0.15×) the four
pieces sit within a couple of centimetres and picking will get fiddly. If it does, the fix is a
dedicated raycast with a gamepiece layer mask alongside `PointerBeam`, not a change here.

### `Assets/Scripts/Bash/ControlListener.cs` — the big one

Delete:

- **The whole `Action == "menu"` branch** and the `BASHMenu` field. The menu is `MenuControl`'s now.
- **The locomotion block** — `MainRig.RotateCam(...)`, `MainRig.MoveCam(...)`, `MainRig.MoveToSeat()`.
  This project's `CameraController2` owns locomotion, snap-turn and recentre itself, and **early-outs
  on all three when `LocalIsAligned`** (`CameraController2.cs:91-94`) because a colocated player's
  position is a fact about the real room. BASH's version would move the rig anyway and break
  colocation. These three methods do not exist on this project's `CameraController2`, so leaving the
  block in is also a compile error.

Change:

- **Serialized rig references → `Find`-by-name in a `Bind()`.** `Inputs`, `MainRig`, `RightHand`,
  `LeftHand` and `pointer` currently point at scene objects. Those objects now live on
  `PersistentRig` in `OpeningScene` and are `DontDestroyOnLoad`, so a scene in this project
  **cannot** hold Inspector references to them. Use the project convention:
  `GameObject.Find("Input Reader").GetComponent<InputReader>()`, `Find("XRRig")`, `Find("Pointer")`.
  See `PlateauSpawnMenu.Bind()` for the pattern, and `CLAUDE.md`'s
  *Conventions that break silently* for the names that are load-bearing.
- **Stand down when something else owns the trigger.** Copy `PlateauSelection`'s guard exactly:
  bail out when `MenuControl.IsOpen`, when `WorldGrab.IsActive`, **or when either grip is merely
  held** — the grab only goes active on both grips, so without that last check a player squeezing
  one grip in preparation still has a live firing beam.
- **`[DefaultExecutionOrder(25)]`**, matching `PlateauSelection`: after `RoomContent` (15) and
  `WorldGrab` (20), so the board pose it reads is this frame's.
- Coordinate conversion per §5.
- Add `public void ResetBoard()` and route `Random Islands` through a public method, so
  `MenuControl` can call both (they are currently private / inline in the deleted menu branch).

### `Assets/Scripts/Bash/SpawnManager.cs`

- **Delete `public seatControl theSeats;`** — `seatControl.cs` is not being copied, so this is a
  compile error otherwise. The field is never read anyway.
- **Replace the base-assignment logic.** `SpawnBaseServerRpc` currently switches on
  `NetworkManager.Singleton.ConnectedClients.Count`, which is wrong in a way that already bites
  BASH: if client 2 leaves and someone else joins, `Count` is 2 again and two players are handed the
  same base. Use `PlayerControls.spawnSlot` instead — `PlayerRing.PickFreeSlot` already assigns it,
  already reuses a vacated slot on reconnect, and is described in `CLAUDE.md` as *"the only stable
  per-player index in the project"*. It is also already the colour index for Chasms.
  - `spawnSlot` 0–3 → a base at the matching board-local position from §5, coloured `Player{n+1}.mat`.
  - `spawnSlot` 4–11 → no base. They get a ring slot and can watch. `NetworkBaseControl` is never
    resolved for them, so `ControlListener` must tolerate a null `netBaseControl` throughout
    (today it dereferences it unguarded).
- **Poll; do not hook.** `spawnSlot` is assigned inside `PlayerControls.OnNetworkSpawn`, which can
  run *after* `SpawnManager.OnNetworkSpawn`. This is the identical trap `PlateauGame` documents —
  *"a late joiner gets their own army … polled rather than hooked on `OnClientConnectedCallback`,
  because `spawnSlot` is assigned later"*. Copy that: a server-side tick (4 Hz is plenty) that gives
  a base to any seat 0–3 that has not got one.
- **Parent spawned objects to `Bash Root`** per §5.

### `Assets/Scripts/Bash/NetworkBaseControl.cs`

- `.position`/`.rotation` → `.localPosition`/`.localRotation` throughout (§5).
- `SyncOverNetworkServerRpc`'s world-space `position`/`forward` → local.
- `controls = GameObject.Find("Controls")` in `Start()` stays — `Controls` is a scene object in the
  new scene. Add the null guard the project convention expects; today it will throw outright if the
  object is renamed.
- `activePos`/`activeRot` write permission is `Owner`, which is correct and matches this project's
  rule that hand/head-like per-player values are owner-written. Leave it.

### `Assets/Scripts/Bash/LineControls.cs`, `PipeRenderer.cs`

`PipeRenderer` needs no change — it already works in the object's local space; §5 just makes sure
the points arriving are in that space. `LineControls` needs its `GameObject.Find("Controls")` null
guard and nothing else.

### `Assets/Scripts/Bash/IslandManager.cs`

**No change.** Already board-local, already correct, already has late-join sync.

### `Assets/Scripts/MenuControl.cs`

Add three cases to `HandleKey`:

```csharp
case "BASH":
    RequestGame(keyName);       // alongside the existing "Stairs" / "Chasms" cases
    CloseMenu();
    break;

case "Reset Game":
case "Random Islands":
    // Inert outside the BASH scene, and says so — same contract as the default case.
    break;
```

`Reset Game` and `Random Islands` should resolve their target in the active scene
(`FindFirstObjectByType<ControlListener>()` / `<IslandManager>()`) and log-and-no-op when there
isn't one, mirroring how `Place Anchor` handles a missing `BoardAnchor`.

**Optional but recommended:** those two keys will be present on `Menu1`/`Menu2` in the lobby, Stairs
and Chasms too, where they do nothing. `CLAUDE.md` already flags the mirror-image of this as a wart
(*"the handler is live and unreachable"* for `Passthrough`). A ~10-line filter in `OpenMenu1` that
deactivates keys not applicable to `SceneManager.GetActiveScene().name` removes the dead keys
without touching the prefabs per scene.

### `Assets/Scripts/GameRoutes.cs`

```csharp
public const string BashGameKey   = "BASH";
public const string BashSceneName = "BashGame";
// …
{ BashGameKey, BashSceneName },
```

`IsGameScene` is derived from the same dictionary, so `CameraController2.PlaceAtRingSlot` starts
applying ring slots in BASH automatically. Nothing else to do for that.

---

## 7. Step 4 — build `Assets/Scenes/BashGame.unity`

Do **not** copy `Second Scene.unity`. It carries its own `XRRig2`, `Camera Offset`, `Main Camera`,
hands, grabbers, `Pointer`, `Debugger`, `InfoBlock`, `Input Reader` and `Directional Light` — every
one of which `PersistentRig` now provides exactly once for the life of the app. Two live objects
sharing those names is precisely the failure `CLAUDE.md` documents:

> That is exactly what happened when `ChasmGame`'s local rig briefly coexisted with `PersistentRig`
> — `Find("XRRig")` sometimes returned the wrong one … **No scene should ever have its own copy of
> anything `PersistentRig` provides.**

Build a new scene with this hierarchy instead. Values are taken from the live BASH scene, rebased
per §5:

```
World Root                    [RoomContent]                 identity
  Board                                                     identity (was (0, 0.6, 2))
    Water                     Cube, BoxCollider, Water.mat   pos (0, -0.01, 0)  scale (3, 0.02, 3)
    Edges
      Wall                    Cube, tag "obstacle", Walls.mat  pos ( 1.52, 0, 0)  scale (0.04, 0.04, 3.08)
      Wall (1)                  "                              pos (-1.52, 0, 0)  "
      Wall (2)                  "                              pos (0, 0,  1.52)  rot y 90°
      Wall (3)                  "                              pos (0, 0, -1.52)  rot y 90°
    Islands                   [NetworkObject, IslandManager]  identity
      Island, Island (1..3)   Island.prefab, tag "island"     authored layout
  Bash Root                   [NetworkObject]                 identity — spawn parent, §5
Controls                      [ControlListener]               scene root, NOT under World Root
Spawn Manager                 [NetworkObject, SpawnManager]   scene root
```

`Water` and the walls are built-in Unity primitives (`Cube`, mesh `10202`), so nothing needs
importing for them beyond the two materials.

`Controls` and `Spawn Manager` stay outside `World Root` on purpose: they are logic, they own no
geometry, and `World Root` is defined as *"everything the game owns hangs under it and nothing a
player owns does"*. Putting a `NetworkObject` under a transform that rescales continuously buys
nothing and risks confusion.

**Four scene settings that are not optional:**

1. **`m_SkyboxMaterial: {fileID: 0}`.** A newly created scene gets Unity's default skybox, which
   writes opaque alpha and turns passthrough into a black room with no error message. Every other
   scene in this project has it cleared.
2. **`m_AmbientMode: 3`** (flat colour), matching every other scene — so removing the skybox does
   not change the lighting.
3. **No `Directional Light`.** `PersistentRig` has one.
4. **Do not port BASH's `Global Volume`.** `Main Camera` has `m_RenderPostProcessing: 0` for
   passthrough, so a volume would be inert at best.

**Scene-placed `NetworkObject`s need a `GlobalObjectIdHash`, which only the Editor assigns.** Add
those components through the Editor (or the bridge) and save the scene — never by editing YAML. This
applies to `Islands`, `Bash Root` and `Spawn Manager`.

---

## 8. Step 5 — the four remaining wiring edits

**`Assets/DefaultNetworkPrefabs.asset`** — add `Base.prefab` (`df7f4d0b65a99cb499ac837ab48a020f`)
and `NetworkCannonLine.prefab` (`1ccd535724552304c94b833ddaa7f4af`). `NetworkConfig.ForceSamePrefabs`
is `1`, so a client whose list differs gets a **hard connection failure with a generic error**. Every
headset needs the same build after this change. `Island.prefab` and `CannonLine.prefab` are *not*
network prefabs — islands are scene objects and the local preview line is never spawned.

**`ProjectSettings/TagManager.asset`** — add `gamepiece`, `obstacle`, `island`, `base`, `line`. The
project currently has only `key` and `Grabbable`; `LineControls`, `ControlListener.ResetBoard` and
`NetworkBaseControl.DeleteAllLinesServerRpc` all match on these strings and fail silently without
them. (BASH's `DebugText` tag is not needed.)

**`Menu1.prefab` / `Menu2.prefab`** — the `BASH` key already exists. Add `Reset Game` and
`Random Islands` keys: duplicate an existing key, set `keyInfo.keyName` exactly (capital R, capital
I — `ControlListener` matched on that string and `MenuControl` will now), and lay them out so they
do not overlap.

**`ProjectSettings/EditorBuildSettings.asset`** — add `Assets/Scenes/BashGame.unity` at index 4.
Without it `LoadScene` fails with `InvalidSceneName`, and that failure is visible only as a
`Debug.LogError` from `GameSelector`.

---

## 9. Step 6 — finish Random Islands

The three Editor steps from `D:\Unity_Stuff\BASH_U6\INTEGRATION_RandomizeIslands.md` were never
done — `Islands` in the BASH scene has exactly one component (its `Transform`), and
`BASHMenu.prefab` still holds only `Reset Game`. Carried into the new scene they are:

1. `NetworkObject` + `IslandManager` on `World Root > Board > Islands` (§7).
2. A `Random Islands` key on `Menu1`/`Menu2` (§8) — replacing the original plan's `BASHMenu` button.
3. Drag `Islands` onto `ControlListener.islandManager` in the new scene.

Defaults (`boardMin/Max` ±1.3, `minSpacing` 0.15, `baseExclusionRadius` 0.5,
`islandColliderRadius` 0.28) are all correct for the rebased board — `0.28` matches
`Island.prefab`'s `CapsuleCollider` radius exactly, and the exclusion zones match §5's base
positions.

---

## 10. Who does what

**The Unity MCP bridge is live**, so most of what would normally be hand-work is scriptable:

| Bridge / tooling can do | You have to do |
| --- | --- |
| Copy the §4 allow-list and refresh the AssetDatabase | Confirm `BASH_U6` is the right source version |
| Add the five tags | Judge the island layout and menu-key placement by eye |
| Create `BashGame.unity`, build the §7 hierarchy, add components (`GlobalObjectIdHash` is assigned by the Editor when the component is added through it) | The two-headset colocation test — nothing else can |
| Every script edit in §6 | Decide whether the board's authored scale matches Chasms' (§5) once you can see both |
| `DefaultNetworkPrefabs.asset`, `EditorBuildSettings.asset`, `GameRoutes.cs` | Build the APK — no CLI tooling or CI exists in this project |
| Duplicate and rename the two new menu keys in `Menu1`/`Menu2` | |

Before anything: this working tree already has thirteen modified files and five untracked paths
(including `PersistentRig.prefab`). **Commit or stash first**, and do the work on a branch —
`.unity` and `.prefab` merges here go through `unityyamlmerge`, whose driver is registered
per-machine in `.git/config` and is not committed.

---

## 11. Verification

In order, because each step's failure mode masks the next:

1. **Compiles clean**, no Safe Mode. (If Unity drops into Safe Mode with `CS0619 … instanceId is
   obsolete`, that is the unrelated Meta SDK bug — run
   `powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1`.)
2. **Editor, single client.** Open the room, press `X`, press `BASH`. The scene loads, the board is
   at the origin, one base appears in front of you at seat 0.
3. **Point and fire.** Right trigger selects a piece (hover ring on), left trigger draws a trail
   that steers with the right joystick, release teleports the piece to the end.
4. **The three collision rules** — trail into a wall kills your own piece; boat into an island dies;
   plane over the same island survives.
5. **World grab.** Both grips, move / turn / resize the board. Fire again. **The trail must come out
   of the cannon and stay on the board.** If it comes out at the wrong place or the wrong size, the
   §5 conversion is incomplete — that is the acceptance test for this whole document.
6. **`Reset Game`** from the shared menu restores all bases and deletes every trail.
7. **`Random Islands`** — 20+ presses, islands always inside the walls, never overlapping, never on
   a base.
8. **Two clients.** Bases in different colours at different seats; both see the same island layout;
   a kill on one shows on the other. Then have the second client leave and rejoin — **they must get
   the same base back**, not a duplicate of somebody else's.
9. **Switch away and back.** `Chasms` → `BASH` → `Stairs` → `BASH`. Nothing leaks, the rig is never
   rebuilt, `Find("XRRig")` still matches exactly one object.
10. **Two headsets, one room.** `Place Anchor`, then check `ColocationProbe`: recenters ticks and
    nothing moves. Take a baseline before changing anything in this area.

---

## 12. Known risks and things deliberately deferred

- **`SpawnNetworkCannonLineServerRpc(Vector3[] linePoints, …)` sends an unbounded array.** A long
  shot at 72 fps with acceleration can be several hundred points; that is a fragmented Netcode
  message at best and over the payload limit at worst. Not a new bug — it is in BASH today — but a
  bigger board makes longer shots. If shots start failing to replicate, cap the point count or
  simplify the polyline before sending.
- **Cannon lines are never despawned except by `Reset Game`.** Every shot leaves a permanent
  `NetworkObject`. Over a long game this grows without bound. Consider a per-player cap or an age
  limit.
- **Physics vs. a moving `World Root`.** `m_AutoSyncTransforms` is `0` in this project, so trigger
  colliders under `World Root` are a physics step behind while the board is being smoothed by
  `RoomContent`. Firing is disabled during a world grab, so this should not bite — but if selection
  or collisions feel a frame late, `Physics.SyncTransforms()` after `SetPositions` is the fix, the
  same way `PointerBeam` handles it.
- **Spectators (seats 4–11) are untested territory.** `ControlListener` dereferences
  `netBaseControl` unguarded in several places today; every one of those paths needs a null check
  before a fifth player can safely join.
- **BASH is not turn-based** and has no win condition — any player may fire at any time. That is how
  BASH already is, and this document does not change it.
- **Meta SDK version gap.** BASH is authored against SDK `78.0.0`, this project against Core
  `205.0.0`. It does not matter for the assets in §4 — none of them reference an OVR component. It
  would matter enormously for `XRRig`/`XRRig2`, which is one more reason they are not being copied.

---

## 13. Summary of every file touched

| File | Change |
| --- | --- |
| `Assets/Scripts/Bash/*.cs` | **New** — six files copied from BASH, then edited per §6 |
| `Assets/Prefabs/Bash/*` | **New** — four prefabs + six models |
| `Assets/Materials/Bash/*` | **New** — eleven materials, three shader graphs, four textures |
| `Assets/Scenes/BashGame.unity` | **New** — §7 |
| `Assets/Scripts/pointerControl.cs` | `currentGamepiece` restored |
| `Assets/Scripts/MenuControl.cs` | three cases in `HandleKey` (+ optional per-scene key filter) |
| `Assets/Scripts/GameRoutes.cs` | `BashGameKey` / `BashSceneName` + one dictionary row |
| `Assets/Prefabs/Menu1.prefab`, `Menu2.prefab` | two new keys each |
| `Assets/DefaultNetworkPrefabs.asset` | `Base`, `NetworkCannonLine` |
| `ProjectSettings/TagManager.asset` | five tags |
| `ProjectSettings/EditorBuildSettings.asset` | `BashGame` at index 4 |
| `docs/CLAUDE.md` | new Scenes row, new design-docs row, and the stale `Menu1.prefab` key list (§2) |

Nothing in `Assets/Scripts/Plateau/`, `PersistentRig.prefab`, `Player.prefab`, `Room Anchor.prefab`,
`OpeningScene`, `StairsGame` or `ChasmGame` is touched.

---

## 14. Where the port came out different

Written after the fact. The plan above is left as it was; this is the delta.

### Corrections to the plan

**§0 — `BASH_U6` is on Unity `6000.5.4f1` now, not `6000.0.31f1`.** It was upgraded at some point
between this document being written and the port being done. Confirmed as the intended source
before starting. Nothing else about it had moved: the board is still at `(0, 0.6, 2)`, `Islands`
still has four children and one component, and `ControlListener.islandManager` is still unwired —
so §9's three unfinished Editor steps were all still outstanding.

**§7's wall table was wrong.** The walls are not four identical cubes with two of them rotated
90°. They are two shapes, unrotated:

| | Position | Scale |
| --- | --- | --- |
| `Wall` | `(0, 0, 1.52)` | `(3.08, 0.04, 0.04)` |
| `Wall (1)` | `(0, 0, -1.52)` | `(3.08, 0.04, 0.04)` |
| `Wall (2)` | `(1.52, 0, 0)` | `(0.04, 0.04, 3.08)` |
| `Wall (3)` | `(-1.52, 0, 0)` | `(0.04, 0.04, 3.08)` |

They are also **triggers with a kinematic `Rigidbody`**, which §7 does not mention and which is
load-bearing: `LineControls` is an `OnTriggerEnter`, and the trail's own `MeshCollider` is
non-convex and therefore cannot be the trigger half of the pair.

**§8's `DefaultNetworkPrefabs.asset` step did itself.** Netcode 2.x's prefab post-processor adds
any imported prefab carrying a `NetworkObject` to the default list, so `Base` and
`NetworkCannonLine` were already there after the §4 copy and the refresh. Worth knowing, because
it also means the list changes on any prefab import — and with `ForceSamePrefabs: 1` that is a
hard connection failure for a headset on an older build.

**§4's list has one file nothing references.** `grass_normal2.jpg` is not used by any material,
shader graph or prefab in BASH. Copied anyway, for exactness against the plan; safe to delete.

**`Base.prefab`'s root is not identity.** Its local scale is `(1.5, 0.75, 1.5)` and the gamepieces
under it are authored at `(1, 2, 1)`, so the product is uniform. `SpawnManager` leaves the
prefab's scale alone and sets only position and rotation — resetting it to one would squash every
piece.

### Deliberate departures

**`Bash Root` is a child of `Board`, not a sibling** (§5, §7). §7 hangs it beside `Board` under
`World Root`, but §5 also says to author `Board`'s local scale to match Chasms' footprint — and
those two contradict: a scaled `Board` beside an unscaled `Bash Root` would leave the water and
walls one size and the bases and trails another. Under `Board`, one local scale governs the whole
game. `Board` is currently authored at scale 1, i.e. BASH's true 3 m board (see below).

**A seventh script, `BashRoot.cs`** (§13 says six). The conversion in §5 appears in five methods
across three files; putting `ToLocalPoint`/`ToWorldPoint`/`ToLocalDirection`/`ToWorldDirection`
and the `Instance` singleton in one 70-line file is what keeps the §11.5 acceptance test
debuggable. It also holds `SpawnParent`, the `NetworkObject` handed to `TrySetParent`.

**Spawn first, then `TrySetParent`.** §5 says to parent spawned objects via `TrySetParent`, and
that is what happens — but the objects are instantiated **unparented**, given their board-local
transform, spawned, and only then reparented with `worldPositionStays: false`. Parenting before
the spawn also works in Netcode 2.x, but only the post-spawn call is the documented path, and it
carries the local transform in its own `ParentSyncMessage`. A `PlaceBaseClientRpc` re-asserts the
local transform on clients afterwards, which is belt and braces and costs one small RPC per base
per session.

**`ControlListener.Action` is gone entirely**, not just its `"menu"` branch. With the menu branch
deleted the state machine had one state, so the guard is `netBaseControl != null` plus the
`PlateauSelection`-style `Playable()` check. `NetworkBaseControl` no longer sets
`controls.Action = "playing bash"`; it calls `ConnectBaseToControls` and nothing else.

**Board scale left at 1** (§5's "board size and the player ring"). Chasms' board measures roughly
4 m across at `contentScale = 1`, so players on the 2 m `PlayerRing` already stand at its edge;
sizing BASH's 3 m board to match would put its walls under their hands. The 3 m board keeps the
half-metre margin BASH was designed for, at the cost of the table changing apparent size on a
Chasms ↔ BASH switch until somebody re-grabs it. One number on `Board` if that turns out to be
the wrong trade.

**The §6 "optional but recommended" per-scene key filter was taken.** `MenuControl.KeyScene` plus
`ApplySceneKeyFilter`, applied to the menu instance as it opens.

### Small fixes made in passing

- **`EndCannonLine` could throw.** A trail entering the corner where two walls meet raises two
  `OnTriggerEnter` calls in one frame, and the second indexed an empty point list. Guarded.
- **A dead piece could be re-selected.** `pointerControl` gets no `OnTriggerExit` when its target
  is switched off under it, so `currentGamepiece` went stale; `ChangeGamePiece` now ignores a
  piece that is not `activeInHierarchy`.
- **`LineControls` dereferenced `myNetBaseControl.activeGamepiece` unguarded** on an island or
  obstacle hit. Both paths go through `ActivePieceIndex()` now.
- **Bases and trails are spawned `destroyWithScene: true`.** Netcode's default is `false`, which
  is what carries the players and `Room Anchor` across a `LoadSceneMode.Single` switch — and
  would have carried BASH's bases into Chasms with them. Not a bug in BASH, which had one scene;
  a new one the moment it became a third game. This is §11.9's "nothing leaks".
- Unused `using NUnit.Framework;` and `using Unity.VisualScripting;` removed from the copied files.

### Still outstanding

- **Nothing in §11 has been run.** The port compiles clean with no warnings and the scene loads in
  Play mode with no exceptions, and that is the whole of what has been verified. Steps 2-9 need a
  running room; step 10 needs two headsets.
- **Menu key placement is a first pass.** `Reset Game` and `Random Islands` sit side by side at
  `z = -0.431` on `Row1`, narrower than the other keys (`x` scale `0.55`, font 7) so both fit on
  one row — two more full-width rows would have pushed `Menu1`'s `Voice Chat` key off the bottom
  of the panel. Judge it by eye and move them.
- **The island layout is BASH's, transplanted.** Same four positions, scales and yaws. Judge by
  eye; `Random Islands` overwrites it anyway.
- **`keepPointerAlwaysOn` is never switched back off** when leaving BASH for Stairs or the lobby.
  That is the pre-existing behaviour Chasms already has, not something this port introduced, but
  BASH is now a second way to reach it.
- **`rightJoystick.x` steers the cannon**, and `CLAUDE.md` says that axis should stay unused
  because its Editor keyboard fallback is bound to `A`/`D` and `A` is `BoardAnchor.RequestReAlign`.
  On a headset there is no conflict. In the Editor, steer with the **left/right arrow keys**,
  which drive the same axis and collide with nothing.
