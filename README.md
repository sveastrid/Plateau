# MRBoardGame2 (Plateau)

**MRBoardGame2** is a colocated mixed-reality multiplayer board game developed for Meta Quest. 

In this experience, several players stand around a real-world table in the same physical room, each wearing a VR/MR headset, and see the same virtual board sitting on it. The board is placed and resized by grabbing the air with both hands. With passthrough enabled, players see each other's real faces and hands; the only virtual representation of another player is two controller cones and a floating nametag.

## 🎲 The Game: Plateau

The primary game being built on this platform is **Plateau** — a strategy game featuring plateaus, bridges, gemhearts, and chasmfiends. 

### Current Implementation Status
Underneath the game rules sits a robust MR platform handling:
- **Colocation & Networking**: Synchronizing the physical and virtual space across multiple headsets.
- **Shared Content Frame**: Ensuring all players see the board in the exact same physical location.
- **World Grab**: Intuitive gesture-based placement and resizing of the board.
- **Player Systems**: Voice, player ring, and interactive menus.

Of the game rules:
- ✅ **Starting forces and piece movement are implemented**: Any player may move their own pieces at any time.
- 🚧 **In Development**: Turns, harvesting, buying, gemhearts, chasmfiends, and win conditions.

## 🛠️ Technology Stack
* **Engine**: Unity 6
* **SDK**: Meta XR Core SDK (with specific patches for deprecated APIs)
* **Features Used**: Passthrough, Spatial Anchors, Shared Spatial Anchors (Colocation), Multiplayer Networking

## 📂 Documentation

Internal development logs, rulesets, and AI prompts are preserved in the [`docs/`](docs/) directory for reference:
* `docs/CLAUDE.md` - Original architecture and rule notes.
* `docs/plateauRules.md` - Detailed game rules for Plateau.
* `docs/anchoringUpdate.md` & `fixAnchoring.md` - Technical notes on Meta's Spatial Anchoring implementation.
