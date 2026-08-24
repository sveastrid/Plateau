# Plateau — Colocated Mixed-Reality Board Game

**Plateau** is a colocated mixed-reality multiplayer strategy board game built for **Meta Quest** headsets. Players stand around a real-world table in the same room, each wearing a headset with passthrough enabled, and see a shared virtual board sitting on the table between them. Real faces, real hands — only the board, pieces, and a floating nametag are virtual.

> **Company:** Cool Deal · **Platform:** Meta Quest 2 / Pro / 3 / 3S · **Engine:** Unity 6 (`6000.5.4f1`)

---

## The Game

Plateau is a strategy game of **plateaus, bridges, and gemhearts** for 2–12 players.

The board consists of **40 irregular plateaus** connected by bridge slots ("faint lines"), plus one large **Central Plateau** where all players begin. Players compete to harvest **gemhearts** — rare resources scattered across the board — while navigating the terrain and fending off roaming **chasmfiends**.

### Pieces

Each player starts with:

| Piece | Count | Movement |
|---|---|---|
| **Bridge** | 2 | Reposition one per turn to connect an already-reached plateau to a new one |
| **Troop** | 6 | Cross up to 2 of your own bridges per turn |
| **Parshendi** | 2 | Cross unlimited own bridges + 1 jump (no bridge needed) per turn |
| **Shardbearer** | 1 | Cross up to 2 own bridges + 1 jump per turn |

### Turn Order

1. **Harvest** — Automatically claim gemhearts where you have majority troops (≥ 3 or with a Shardbearer; Shardbearer required if a chasmfiend is present).
2. **Buy** — Spend gemhearts to recruit reinforcements at the Central Plateau.
3. **Move** — Move each piece once.

### Winning

Agree on the target before play. Recommended: **players × 4** gemhearts to win, out of **players × 8** total. First to the target wins immediately; if all gemhearts have been placed, one final round determines the winner by count.

> Full rules: [`docs/plateauRules.md`](docs/plateauRules.md)

---

## Technology Stack

| Layer | Technology |
|---|---|
| Engine | Unity 6 (`6000.5.4f1`), URP, IL2CPP, ARM64 |
| XR Runtime | Meta XR Core SDK `205.0.0`, Oculus XR Plugin `4.5.4`, AR Foundation `6.5.0` |
| Networking | Unity Netcode for GameObjects `2.13.0` over Unity Relay (host/client, DTLS) |
| Voice Chat | Unity Vivox `16.10.0` (spatial voice) |
| Passthrough | Meta Insight Passthrough (required), Guardian suppression |
| Colocation | Meta Shared Spatial Anchors — one player creates an anchor, others align to it |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                  OpeningScene (Lobby)                │
│  3D Keyboard → Room Code / Username → Relay + Vivox │
└──────────────────────┬──────────────────────────────┘
                       │ Host loads scene
          ┌────────────┴────────────┐
          ▼                         ▼
   StairsGame (demo)         ChasmGame (Plateau)
                              41 plateaus · 81 bridge slots
```

### Key Systems

- **Colocation** (`BoardAnchor` → `RoomAnchor` → `RoomContent`): The host creates an `OVRSpatialAnchor` and shares its UUID. Every client shifts its XR rig so the anchor lands at the world origin — all headsets share the exact same physical reference frame.

- **World Grab** (`WorldGrab`): Two-grip bimanual gesture to translate, yaw-rotate, and uniformly scale the board in real time. Acquires an exclusive network lock so only one player repositions at a time.

- **Board Graph** (`PlateauBoard`): At runtime, automatically parses the 41 plateau GameObjects and 81 bridge-spot bars into an adjacency graph — no manual wiring needed.

- **Game State** (`PlateauGame`): Server-authoritative `NetworkList<PieceStack>`, `NetworkList<PlacedBridge>`, and `NetworkList<BridgeEdge>`. Every move is validated server-side via the same BFS rules used for client-side highlighting.

- **Movement Rules** (`PlateauMoveRules`): Shared BFS graph traversal implementing per-piece movement budgets (bridge crossings, jumps). Runs symmetrically on client (highlights) and server (validation).

- **Selection UX** (`PlateauSelection` → `PointerBeam`): Laser pointer to select a piece stack, joystick up/down to choose count, highlighted legal destinations, trigger to commit.

- **Piece Visuals** (`PlateauPieceView`): Local-only — spawns and smooths visual prefabs from the replicated network lists. No piece is a `NetworkObject`; they're lightweight stack-based views.

---

## Project Structure

```
Assets/
├── Editor/
│   └── MRPassthroughSetup.cs       # Auto-configures OVR project settings on domain reload
├── Materials/                       # Plateau tiers, board, keys, pointer, skybox
├── Prefabs/
│   ├── Plateau, Bridge, Bridge Spots, Soldier, Parshendi, Shardbearer
│   ├── Chasmfiend, Gemheart
│   ├── Player, Room Anchor, Pointer, Grabber
│   ├── Keyboard, Key, Text Input, Menu1
│   └── Objects/                     # Raw .obj meshes for game pieces
├── Scenes/
│   ├── OpeningScene.unity           # Lobby — room code + username entry
│   ├── StairsGame.unity             # Simple demo board
│   └── ChasmGame.unity              # Main Plateau game (41 plateaus)
├── Scripts/
│   ├── BoardAnchor.cs               # Spatial anchor colocation
│   ├── RoomAnchor.cs                # Networked room singleton
│   ├── RoomContent.cs               # World Root transform sync
│   ├── WorldGrab.cs                 # Bimanual board placement
│   ├── PlayerControls.cs            # Networked player avatar
│   ├── CameraController2.cs        # XR rig + desktop fallback
│   ├── PassthroughController.cs     # Passthrough + Guardian
│   ├── RelayVivox.cs                # Relay + Vivox setup
│   ├── GameController.cs            # Lobby keyboard + join logic
│   ├── InputReader.cs               # XR / desktop input bridge
│   ├── MenuControl.cs               # In-VR floating menu
│   ├── GameRoutes.cs / GameSelector.cs  # Scene routing
│   └── Plateau/
│       ├── PlateauTypes.cs          # PieceKind, PieceStack, BridgeEdge, PlacedBridge
│       ├── PlateauBoard.cs          # Runtime adjacency graph baking
│       ├── PlateauGame.cs           # Server-authoritative game state
│       ├── PlateauMoveRules.cs      # BFS movement validation
│       ├── PlateauSelection.cs      # Point-and-click piece interaction
│       ├── PlateauPieceView.cs      # Local visual piece spawning
│       ├── PlateauPieceTag.cs       # Per-piece-visual metadata
│       ├── PlateauTint.cs           # MaterialPropertyBlock coloring
│       ├── PlateauPalette.cs        # Per-seat color generation
│       ├── PlateauTag.cs            # Plateau index tag for raycasting
│       └── PointerBeam.cs           # Laser pointer raycast
docs/
├── plateauRules.md                  # Full game rules
├── CLAUDE.md                        # Architecture & rule notes
├── anchoringUpdate.md               # Spatial anchor implementation notes
├── fixAnchoring.md                  # Anchor troubleshooting
└── updates1.md                      # Development log
Tools/
└── MetaSdkPatch/                    # Patches Meta SDK 205.0.0 for Unity 6000.5
```

---

## Getting Started

### Prerequisites

- **Unity 6** (`6000.5.x`) with Android Build Support (IL2CPP, ARM64)
- **Meta Quest** headset (Quest 2 or newer)
- A [Meta developer account](https://developer.oculus.com/) with spatial anchor permissions
- A [Unity Gaming Services](https://dashboard.unity3d.com/) project for Relay & Vivox

### Setup

1. **Clone the repo** and open in Unity 6.

2. **Apply the Meta SDK patch** (required on first open and after any package re-resolve):
   ```powershell
   powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
   ```
   This fixes an upstream `CS0619` error in Meta XR Core SDK 205.0.0 on Unity 6000.5+. See [`Tools/MetaSdkPatch/README.md`](Tools/MetaSdkPatch/README.md) for details.

3. **Configure Unity Gaming Services**: Link the project to your UGS project ID in *Edit → Project Settings → Services* for Relay and Vivox.

4. **Build & Deploy**:
   - *File → Build Settings → Android → Build* to produce an APK.
   - Install on Quest via `adb install BoardGames.apk`.

### Playing

1. All players put on their headsets in the **same physical room**.
2. One player creates a room (entering a room code); others join with the same code.
3. The host's headset establishes a shared spatial anchor — other headsets align to it automatically.
4. **Grab the air with both grips** to place, rotate, and scale the board on your table.
5. Select a game from the wrist menu (choose **Chasms** for Plateau).
6. Point at your pieces, use the joystick to pick a count, and trigger to move to highlighted destinations.

---

## Implementation Status

### ✅ Implemented

- Colocated spatial anchor alignment across multiple headsets
- Bimanual world grab (translate, yaw-rotate, scale)
- Runtime adjacency graph baking from scene geometry
- Starting army distribution
- BFS legal movement engine for all four piece types
- Server-authoritative piece movement with client-side prediction/highlighting
- Laser pointer selection with joystick count adjustment
- Voice chat (Vivox) and relay networking
- Passthrough toggle and Guardian suppression
- Desktop keyboard/mouse fallback for editor testing

### 🚧 In Development

- Turn order state machine (active player enforcement, turn timer)
- Gemheart placement (random distribution or catapult launcher)
- Automatic harvest phase resolution
- Piece purchasing & spawning flow
- Chasmfiend spawning, wipe, and flee mechanics
- Win condition checks and score tracking

---

## Documentation

| Document | Description |
|---|---|
| [`docs/plateauRules.md`](docs/plateauRules.md) | Complete game rules |
| [`docs/CLAUDE.md`](docs/CLAUDE.md) | Architecture notes and design decisions |
| [`docs/anchoringUpdate.md`](docs/anchoringUpdate.md) | Meta Spatial Anchor implementation deep-dive |
| [`docs/fixAnchoring.md`](docs/fixAnchoring.md) | Anchor troubleshooting notes |
| [`docs/updates1.md`](docs/updates1.md) | Development log |
| [`Tools/MetaSdkPatch/README.md`](Tools/MetaSdkPatch/README.md) | Meta SDK patch explanation |

---

## License

Private project — not currently licensed for redistribution.
