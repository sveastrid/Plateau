# GEMINI.md

Guidance for Gemini when working in this repository.

## What this project is

**MRBoardGame2** (often referred to as **Plateau**) is a colocated mixed-reality multiplayer board game for Meta Quest headsets. Several players stand around a real physical table in the same room, each wearing a headset with passthrough enabled, and interact with the same shared virtual board sitting on the table.

The game is a strategy game called **Plateau** involving plateaus, bridges, gemhearts, and chasmfiends. 

### Current Status
**Implemented:** Starting forces distribution, BFS legal movement engine for all four piece types, colocation, world grab, server-authoritative piece movement with client prediction/highlighting, voice chat (Vivox), and passthrough/guardian suppression.
**In Development:** Turns (currently any player can move at any time), harvesting, buying, gemhearts, chasmfiends, and win conditions.

---

## Toolchain and Targets

| Component | Technology |
| --- | --- |
| **Engine** | Unity 6000.5.4f1 (`ProjectSettings/ProjectVersion.txt`) |
| **Render Pipeline**| URP 17.5.0, Linear color space (HDR off for passthrough) |
| **XR Runtime** | Meta XR Core SDK 205.0.0 + Oculus XR Plugin 4.5.4 via Unity's `XROrigin` |
| **Networking** | Unity Netcode for GameObjects 2.13.0 over Unity Relay (host/client, tick rate 30) |
| **Voice Chat** | Unity Vivox 16.10.0 (spatial voice) |
| **Platform** | Android / Quest (ARM64 only, IL2CPP, minSdk/targetSdk 34) |
| **Devices** | Meta Quest 2 / Pro / 3 / 3S |

---

## Getting Started & Quirks

1. **Meta SDK Patch:** Meta XR Core SDK 205.0.0 has a known upstream bug with Unity 6000.5 (`CS0619 ... instanceId is obsolete`). If you see this, apply the patch:
   ```powershell
   powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
   ```
2. **Input System:** Active Input Handling must stay **"Input Manager (Old)"** (`activeInputHandler = 0`). Do not upgrade or switch to the new Input System, as it breaks the legacy keyboard fallback (`InputReader.cs`) used for Editor testing.
3. **Passthrough Settings:** Passthrough is heavily dependent on the camera's `m_ClearFlags` being `Solid Color` with `(0,0,0,0)`, post-processing disabled, HDR disabled, and `OVRPassthroughLayer.overlayType` set to `Underlay`. Do not modify these.

---

## Architecture Overview & Key Scripts

### Colocation & The Content Frame
- **`BoardAnchor.cs` & `RoomAnchor.cs` (Colocation):** The host drops an `OVRSpatialAnchor` at their feet, localizes it, and shares its UUID via `RoomAnchor`. Every other client polls this, downloads the anchor, and shifts its **own rig** (`CameraController2.AlignRigToAnchor`) so the anchor lands at the world origin. This is a continuous yaw-and-height alignment applied each frame.
- **`RoomContent.cs` (The World Root):** Everything the game owns hangs under `World Root`. Nothing the player owns does. This structural split allows resizing/moving the entire game via an 80ms first-order filter without moving the players.
- **`WorldGrab.cs` (Bimanual Placement):** A two-grip gesture to translate, yaw-rotate (accumulated from per-frame deltas), and uniformly scale the board. Squeezing both grips acquires an exclusive server lock (`RoomAnchor.worldHolder`) so only one player controls it at a time.
- **`PlayerRing.cs`:** A static ring of 12 slots around the world origin where remote avatars are placed. Slots are assigned on spawn; players are never dynamically re-spaced.
- **`PlayerControls.cs` (Avatar Replication):** Manages the networked avatar. Hands transmit position and rotation (quaternion, not forward vectors). Remote players are rendered simply as two controller cones and a nametag floating above their actual passthrough bodies, utilizing a 0.06s exponential lerp for smoothness.

### The Plateau Game Systems

The Plateau game logic resides in `Assets/Scripts/Plateau/` and revolves around server-authoritative state with local visual views.

- **`PlateauGame.cs` (Game State):**
  - A `NetworkBehaviour` attached to the `Room Anchor.prefab`.
  - Holds server-authoritative state via `NetworkList<PieceStack>`, `NetworkList<PlacedBridge>`, and `NetworkList<BridgeEdge>`.
  - Mutates lists in place (`.Add()`, `.RemoveAt()`) to save bandwidth. Do **not** call `.Clear()` and refill unless performing a deliberate board reset.
  - Receives move requests via RPCs, validates them symmetrically against `PlateauMoveRules`, and applies the state.
  - Uses a single server tick (4 Hz) for lifecycle events (resetting boards, distributing forces to late joiners).

- **`PlateauBoard.cs` (Graph Generation):**
  - Bakes the board's adjacency graph at `Awake` (Execution Order -10).
  - The graph is not authored manually; it's derived dynamically from the 41 `Plateau` and 81 `Bridge Spots` GameObjects.
  - Sibling order under the `Plateaus` GameObject dictates the plateau index. **Do not reorder them.**
  - Measures in `World Root` local space, meaning the graph is invariant when players move or resize the board.
  - The server publishes the baked edge list to clients to ensure perfect synchronization of geometry tie-breakers.

- **`PlateauMoveRules.cs` (Movement Validation):**
  - A shared BFS graph traversal used by the client (highlighting legal moves) and server (enforcing rules).
  - Handles distinct piece movement budgets (Troops cross up to 2 bridges; Parshendi cross unlimited + 1 jump; Shardbearer crosses up to 2 bridges + 1 jump).
  - Bridges are validated to connect exactly one gap away from the player's connected network (anchored at the central plateau).

- **`PlateauPieceView.cs` (Visual Representation):**
  - Spawns and manages local visual prefabs based on the replicated `NetworkList` state.
  - Pieces are *not* `NetworkObject`s. They are lightweight views reconstructed locally to avoid NetworkObject spawning overhead.
  - Reconciles pieces on `OnListChanged` dirty flags.
  - Manages piece layouts on plateaus using a phyllotaxis spiral fit to each plateau's independent X/Z radii, ensuring identical layouts across headsets.

- **`PlateauSelection.cs` & `PointerBeam.cs` (Interaction UX):**
  - **`PointerBeam.cs`**: Performs a raycast to target the board. Forces `Physics.SyncTransforms()` first to ensure it hits the correct coordinates while `World Root` is moving.
  - **`PlateauSelection.cs`**: Handles point-and-click selection logic. Point at your own piece, trigger to select. Use the right joystick (up/down) to adjust the piece count. Trigger on a highlighted plateau to commit.
  - Temporarily disables if the menu is open, the world grab is active, or if either grip is held.

- **`PlateauTint.cs` (Highlighting):**
  - Colors plateaus for legal move hints using a `MaterialPropertyBlock` rather than material swaps. This preserves the SRP batcher and prevents 41 distinct material instance memory leaks.

---

## Critical Code Conventions

When writing or modifying code in this project, adhere to these strict rules:

1. **GameObject Names are Load-Bearing:**
   Many scripts rely on exact `GameObject.Find` lookups (e.g., `XRRig`, `Input Reader`, `World Root`, `Pieces`, `Central Plateau`). Do not rename objects in the hierarchy unless you update all corresponding script references. Sibling order under `Plateaus` determines the plateau index.
2. **NetworkVariable Boundaries:**
   Write permissions enforce security. `spawnSlot`, `playerName`, `roomOwner`, and `RoomAnchor` state are server-written. Head/hand poses are owner-written. Always validate RPCs on the server.
3. **Execution Order Matters:**
   Several components have a load-bearing `[DefaultExecutionOrder]`. For example, `PlateauBoard` (-10) must bake the graph before anything reads it. `RoomContent` (15) must apply the shared board pose before pointers raycast against it.
4. **Editor Fallbacks:**
   `InputReader` seamlessly falls back to keyboard/mouse when controllers are absent. Ensure new input features maintain this pattern for rapid Editor testing without a headset.
5. **No Assumed Scene State:**
   Scenes are loaded via `LoadSceneMode.Single`. Objects survive either via `DontDestroyOnLoad` (like the Network Manager) or are re-bound when the scene changes (e.g., `PlayerControls.BindToScene()`).

## Debugging

- **In-Headset Log:** A debug console hangs off the `XRRig` (`Debugger`). `Debug.Log` output lands there. Click the right joystick to clear it.
- **ColocationProbe:** Disabled by default in game scenes. Enable it on the `XRRig` to debug alignment issues (it logs height, UUIDs, recenter ticks, etc.).
