# GEMINI.md

**The guidance for this repository lives in [`CLAUDE.md`](CLAUDE.md).** Read that first — it is the
single source of truth for toolchain, build steps, architecture and the conventions that break
silently. This file used to duplicate it and drifted; it is now a pointer so that cannot happen again.

Per-area detail is in nested files, and you should read only the one you need:

| Working on | Read |
| --- | --- |
| Shared platform — rig, colocation, world grab, avatars, menus, input, passthrough | [`Assets/Scripts/CLAUDE.md`](Assets/Scripts/CLAUDE.md) |
| **Plateau** (menu key `Chasms`, scene `ChasmGame`) | [`Assets/Scripts/Plateau/CLAUDE.md`](Assets/Scripts/Plateau/CLAUDE.md) |
| **BASH** (menu key `BASH`, scene `BashGame`) | [`Assets/Scripts/Bash/CLAUDE.md`](Assets/Scripts/Bash/CLAUDE.md) |
| **Stairs** (menu key `Stairs`, scene `StairsGame`) | [`Assets/Scripts/Stairs/CLAUDE.md`](Assets/Scripts/Stairs/CLAUDE.md) |

Do not search `Library/`, `Temp/`, `obj/`, `build/`, `.utmp/` or `.vs/`. `Library/` alone is 25 GB
and holds 5,948 `Editor/*.cs` files from packages. Every `.cs` under `Assets/` is first-party, and
there are only 64 of them.
