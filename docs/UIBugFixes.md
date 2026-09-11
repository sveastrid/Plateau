# UIBugFixes.md — the panel toolkit is mirrored, clipped and mute

**Status: applied, §1–§9.** See [§10](#10--order-of-work) for what was verified and what still
needs a headset. The sections below are written as the plan they were, in the present tense of the
broken code; where the applied fix came out differently from the plan, the section says so inline.

Every measurement below was taken in the open Editor against the working tree at `8054e1a`, not
deduced from the YAML. Where a number is quoted it came out of a `Unity_RunCommand` dump; §11 has
the scripts, so any of it can be re-checked in one call.

Three defects account for essentially everything that is wrong with the UI, and they compound:

| | Defect | What the player sees |
| --- | --- | --- |
| §1 | Every world-space panel is rotated 180° too far | All text mirrored, every panel turned away from the player |
| §2 | Every list row's thumbnail, title and subtitle are positioned 580 units left of where they are drawn, and the viewport mask eats them | Rows are blank bars showing only their right-hand cell |
| §3 | The room menu's action list has 2 slots, 3–5 rows, and no scroll arrows | `Voice Chat`, `New Game`, `Reset Game`, `Random Islands` are unreachable |

§2 is why "some buttons have nothing" (rows whose right-hand cell is `""` render *completely*
blank) and why "some have the same words but don't tell what they do" — two Play-panel rows are
reduced to a bare `>`, and three more to `Host`, `Code` and `Change` with no titles.

Fix §1 and §2 first. They are eight numbers and four lines of code between them, and until they are
fixed nothing else about the UI can be judged.

---

## §1 — Every panel is mirrored and turned away

### What is wrong

Four places build a world-space Canvas and every one of them applies an extra 180° about Y:

| File | Line | Rotation as written |
| --- | --- | --- |
| `Assets/Scripts/Lobby/LobbyController.cs` | 215–216 | `LookRotation(forward, up) * Euler(0, 180f + yaw, 0)` |
| `Assets/Scripts/MenuControl.cs` | 277 | `myCam.rotation * Euler(0f, 180f, 0f)` |
| `Assets/Scripts/MenuControl.cs` | 460–461 | `LookRotation(flat, up) * Euler(0f, 180f - 40f, 0f)` |
| `Assets/Scripts/MenuControl.cs` | 552 | `myCam.rotation * Euler(0f, 180f, 0f)` (`moveMenu`) |

The comment at `LobbyController.cs:211-214` asserts the 180 is required because "a world-space
Canvas is drawn on its +Z face". **That is backwards, and this project already documents the correct
rule in two places and depends on it in three.**

`Assets/Scripts/Stairs/CLAUDE.md:334-338`:

> **TMP is read from its −Z side** — the reader looks *along* the text's own forward, which is why
> the standard billboard is `forward = camera.forward` and why `LookAt(camera)` famously mirrors
> text.

The three working billboards in the project all obey it — `PlayerControls.cs:406` (the nametags,
`LookRotation(-lookDirection)`, i.e. forward pointing *away* from the reader),
`PlateauPieceView.cs:441` (`LookRotation(away, up)`), `StairsView.cs:744`. The panels are the only
surfaces that do the opposite, and they are the only surfaces that come out mirrored.

Measured, with the player at the origin looking down +Z (`dot(panel.right, camera.right) > 0` means
the text runs left-to-right on screen; `dot(panel.forward, awayFromViewer) > 0` means the panel is
aimed at the player rather than away):

```
LobbyController.Place library (yaw 0), AS WRITTEN   RIGHT->LEFT (MIRRORED)  dot(+X)=-1.00 | aim=-0.96 TURNED AWAY
LobbyController.Place library, with the 180 removed LEFT->RIGHT (readable)  dot(+X)= 1.00 | aim= 0.96 square-on
LobbyController.Place play (yaw 35), AS WRITTEN     RIGHT->LEFT (MIRRORED)  dot(+X)=-0.82 | aim=-1.00 TURNED AWAY
LobbyController.Place play (yaw 35), 180 removed    LEFT->RIGHT (readable)  dot(+X)= 0.82 | aim= 1.00 square-on
MenuControl.OpenMenu1, AS WRITTEN                   RIGHT->LEFT (MIRRORED)  dot(+X)=-1.00 | aim=-0.88 TURNED AWAY
MenuControl.OpenMenu1, 180 removed                  LEFT->RIGHT (readable)  dot(+X)= 1.00 | aim= 0.88 square-on
MenuControl.BuildRulesPanel, AS WRITTEN             RIGHT->LEFT (MIRRORED)  dot(+X)=-0.77 | aim=-0.94 TURNED AWAY
MenuControl.BuildRulesPanel, 180 removed            LEFT->RIGHT (readable)  dot(+X)= 0.77 | aim= 0.94 square-on
PlayerControls nametag (known-good reference)       LEFT->RIGHT (readable)  dot(+X)= 1.00 | aim= 1.00 square-on
```

The `aim` column is the part that is easy to miss: the 180 does not only mirror the glyphs, it also
turns the panel's *good* side away, so the yaw that was supposed to angle a side panel in towards
the player angles it further out. Both halves are cured by the same deletion.

**The existing yaw magnitudes and signs are already correct.** `playYaw = +35°` on a panel 0.95 m to
the player's right is within 2° of pointing straight at them; `−40°` on the rules wing 1.3 m to the
left likewise. Delete only the `180f` term.

### The fix

`Assets/Scripts/Lobby/LobbyController.cs:215`

```csharp
        // A world-space Canvas is read from its -Z side: the reader looks ALONG the text's own
        // forward, which is why the standard billboard is forward = camera.forward and why
        // LookAt(camera) mirrors text. See Assets/Scripts/Stairs/CLAUDE.md, "TMP is read from its
        // -Z side", and PlayerControls.cs:406, which is the same rule on the nametags.
        //
        // The yaw turns a panel sitting to one side back in towards the player. Positive is to the
        // player's right, which is the side `right` is measured on, so the two signs agree.
        panel.transform.rotation = Quaternion.LookRotation(forward, Vector3.up) *
                                   Quaternion.Euler(0f, yaw, 0f);
```

`Assets/Scripts/MenuControl.cs:270-278` — and while you are here, stop inheriting the camera's
pitch. The menu is opened while the player is looking down at a table, so `myCam.rotation` tips the
whole panel; the rules wing beside it is placed off a flattened `flat` axis (line 453-455) and ends
up in a different plane. Level both:

```csharp
        Vector3 flat = myCam.forward;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, flat);

        currentMenu = Instantiate(roomMenu, myCam.position + menuDistance * flat, Quaternion.identity);

        // No 180: a Canvas is read from its -Z side, so the panel's forward points AWAY from the
        // reader, exactly as PlayerControls does for the nametags. Yaw-only, so a player looking
        // down at the board does not get a menu tipped to match - and so the rules wing below,
        // which is placed off the same flattened axis, stays coplanar with it.
        currentMenu.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        currentMenu.transform.position += -menuLeftOffset * rightAxis;
```

`Assets/Scripts/MenuControl.cs:460-461`

```csharp
        rulesCanvasGo.transform.rotation = Quaternion.LookRotation(flat, Vector3.up) *
                                           Quaternion.Euler(0f, -40f, 0f);
```

`Assets/Scripts/MenuControl.cs:547-554` (`moveMenu`) — same deletion. It is called by nothing today;
fix it anyway rather than leaving one copy of the wrong rule behind for somebody to copy.

### Check it

In the Editor, Play `OpeningScene` and read the two panel headers. "Library" and "Play" the right
way round, both squarely facing the camera, is the whole test. `M`/`N` tilt the rig
(`CameraController2`, debug only) — the panels must stay level and stay readable.

---

## §2 — Every row draws only its right-hand cell

### What is wrong

`Assets/Prefabs/Ui/Panel.prefab` — and therefore `RoomMenu.prefab`, which is a variant whose only
overrides are the root transform — lays out `RowTemplate`'s four children at **negative offsets from
the row's left edge**, as though they were anchored to the row's centre. They are not: all four are
anchored at `(0, 1)`, the row's top-*left*.

Measured after `ScrollList.EnsureSlots` has cloned and re-anchored a row (`ScrollList.cs:84-104`),
in viewport-local units, where the row and the viewport both span x 0..1160:

| Child | anchorMin/Max | anchoredPosition | sizeDelta | Renders at x | |
| --- | --- | --- | --- | --- | --- |
| `Thumb` | (0,1) | (−570, −5) | 86 × 86 | **−570 .. −484** | entirely outside — invisible |
| `Title` | (0,1) | (−470, −4) | 638 × 58 | **−470 .. 168** | left-aligned text sits in the clipped part |
| `Subtitle` | (0,1) | (−470, −57) | 638 × 36 | **−470 .. 168** | same |
| `State` | (0,1) | (139, −4) | 406 × 88 | 139 .. 545 | visible, but floating mid-left |

The viewport carries a `RectMask2D`, which clips pixels at x < 0. `Title` is left-aligned inside its
638-wide box starting at x = −470, so a title of up to ~470 units is **entirely inside the clipped
region**. At the authored 34 pt, "Place Anchor" is 201 units wide and "Browse Public Rooms" is 333 —
both invisible.

The offsets are not random. Add 580 (half the row width) to each and they become a sensible layout:
thumbnail at 10, title and subtitle at 110, state right-aligned ending at 1125. They were authored
against a centre anchor and the anchors say left.

### What this looks like in the headset

Everything on a panel that is *not* inside a `ScrollList` viewport renders correctly — `Header`,
`Status` and `Detail` are direct children of the Panel root and are laid out consistently. So:

**The room menu** (`MenuControl.BuildGameRows` / `BuildActionRows`) passes `state = ""` for every
game row and every action row. A private room's menu is therefore a panel headed **"Room"** followed
by **five completely blank bars**. The only row with anything visible is `Voice Chat` (state
`"ON"`/`"OFF"`), and it is off the bottom of the list — see §3.

**The lobby Play panel** is five rows reduced to their state cells:

```
Play
                              Host        <- "New Private Room"
                              Code        <- "Join Private Room"
                              >           <- "New Public Room"
                              >           <- "Browse Public Rooms"
                              Change      <- "You are: Player"
```

Two rows showing an identical bare `>`. That is the report, exactly.

**The lobby Library panel** is three rows showing `Free — Add` or `In Library` with no game names,
over a detail block that does render.

### The fix — Panel.prefab, `Main/RowTemplate` and `Actions/RowTemplate`

Do it in the prefab, not in code. Both row templates need the same eight fields. The row stretches
to its viewport's width (`anchorMin (0,1)`, `anchorMax (1,1)`, `sizeDelta.x 0`), so anchor the left
column to the left edge and the state cell to the **right** edge — then the layout survives being
re-used on a panel of another width, which `CodePad.prefab` already is (1000 wide).

| Child | anchorMin | anchorMax | pivot | anchoredPosition | sizeDelta | Result |
| --- | --- | --- | --- | --- | --- | --- |
| `Thumb` | (0, 1) | (0, 1) | (0, 1) | **(20, −5)** | 86 × 86 | 20 .. 106 |
| `Title` | (0, 1) | (0, 1) | (0, 1) | **(126, −6)** | **560 × 46** | 126 .. 686 |
| `Subtitle` | (0, 1) | (0, 1) | (0, 1) | **(126, −54)** | **560 × 34** | 126 .. 686 |
| `State` | **(1, 1)** | **(1, 1)** | **(1, 1)** | **(−20, −4)** | **380 × 88** | 760 .. 1140 |

Two things this also repairs, which the +580 shift alone would not:

- **Title and State overlapped.** At the authored widths they were 638-wide from 110 and 406-wide
  from 719 — a 29-unit collision that a long title would have run straight into. 686 / 760 leaves a
  74-unit gap.
- **Title and Subtitle overlapped vertically** by 5 units (−4..−62 against −57..−93). 46 + 34 in a
  96-tall row with an 8-unit bottom margin does not.

> **Quicker alternative, if you want it working before the design pass:** set `anchorMin.x` and
> `anchorMax.x` to `0.5` on all four children and change nothing else. That makes the authored
> numbers mean what they were written to mean. It leaves the Title/State overlap and it does not
> survive a width change, so treat it as a stopgap, not the fix.

### The fix — `PanelRow`, so a row with no subtitle is not top-heavy

`PanelRow.Bind` already hides the subtitle when there is none (`PanelRow.cs:108`), but leaves the
title in the upper band, so every room-menu row — none of which has a subtitle — sits high in a
96-unit bar with a blank strip under it. Centre it instead. Add to `PanelRow`:

```csharp
    [Header("Title placement")]
    [Tooltip("Title Y when a subtitle is showing, and when it is not. A two-line row puts the " +
             "title in the upper band; a one-line row centres it, or every room-menu row sits " +
             "high in its bar with a blank strip underneath.")]
    public float titleYWithSubtitle = -6f;
    public float titleYAlone = -25f;
```

and in `Bind`, after the subtitle block:

```csharp
        if (title != null)
        {
            RectTransform tr = title.rectTransform;
            bool twoLine = !string.IsNullOrEmpty(row.subtitle);
            tr.anchoredPosition = new Vector2(tr.anchoredPosition.x,
                                              twoLine ? titleYWithSubtitle : titleYAlone);
        }
```

### The fix — the empty thumbnail gutter

All three modules carry `thumbnail: {fileID: 0}` (`Assets/Games/*/`*`Module.asset`), so
`PanelRow.cs:120` disables the `Thumb` image and every row keeps a 106-unit indent with nothing in
it. Until there are real thumbnails (§8), close the gap:

```csharp
        // The thumbnail column collapses when there is nothing to put in it. Three modules ship
        // with no thumbnail today, and an indent with a hole in it reads as a broken row rather
        // than as a deliberate space.
        if (title != null && subtitle != null)
        {
            float x = row.thumbnail != null ? textXWithThumb : textXNoThumb;   // 126 / 20
            title.rectTransform.anchoredPosition =
                new Vector2(x, title.rectTransform.anchoredPosition.y);
            subtitle.rectTransform.anchoredPosition =
                new Vector2(x, subtitle.rectTransform.anchoredPosition.y);
        }
```

with `textXWithThumb = 126f` and `textXNoThumb = 20f` as serialized fields beside the two above.

### Check it

The measurement script in §11.2 prints each child's span in viewport-local coordinates and flags
anything outside `0 .. 1160`. After the change every row must read `OK`.

---

## §3 — The room menu's action list shows 2 rows out of up to 5, with no way to scroll

### What is wrong

`Panel.prefab > Actions` is a `ScrollList` with **`visibleRows = 2`** and
**`scrollUp`, `scrollDown` and `counter` all unassigned**. `ScrollList.Rebind` (`ScrollList.cs:243-261`)
therefore has nothing to switch on when the list overflows, and `Rebind`'s `needsArrows` branch is
dead for this list.

`MenuControl.BuildActionRows` (`MenuControl.cs:379-420`) feeds it:

| Scene | Role | Rows built | Visible |
| --- | --- | --- | --- |
| `ChasmGame` | host | Place Anchor, Passthrough, Voice Chat | 2 of 3 |
| `StairsGame` | host | Place Anchor, Passthrough, Voice Chat, New Game | 2 of 4 |
| `BashGame` | host | Place Anchor, Passthrough, Voice Chat, Reset Game, Random Islands | **2 of 5** |
| any | client | Place Anchor, Passthrough, + the game's actions | 2 of 2–4 |

So **`Voice Chat`, `New Game`, `Reset Game` and `Random Islands` cannot be pressed today.** The only
way to reach them is to know that pushing the right joystick down scrolls a list that gives no sign
it can scroll — and that same push simultaneously scrolls the game list above it *and* the rules
panel beside it, because all three read `rightJoystick.y` (`ScrollList.cs:199`,
`ScrollTextWithJoystick.cs:15`). The root `CLAUDE.md` records the two-way version of this as a known
rough edge; with the actions list it is three ways and it is no longer cosmetic.

### The fix

Two changes, and prefer both.

**1. Give the action list room.** The panel is 1200 × 900 and the region from y −630 (bottom of
`Main`) to −700 (top of `Actions`) is empty. Raise `Actions` and make it four rows:

- `Actions` RectTransform: `anchoredPosition (20, −510)`, `sizeDelta (1160, 384)`
- `Actions/Viewport`: `sizeDelta (1160, 384)`
- `ScrollList.visibleRows`: **2 → 4**
- `Main` shrinks to make room: `sizeDelta (1160, 336)`, `Main/Viewport` the same,
  `ScrollList.visibleRows` **5 → 3** (three games in the catalog; the arrows exist for when there
  are more), and move `Main/ScrollUp`, `Main/ScrollDown`, `Main/Counter` from y −488 to **y −344**.

  Four action rows covers every case in the table except BASH-as-host, which is the one that keeps
  the arrows honest.

**2. Wire the arrows.** `Actions/ScrollUp`, `Actions/ScrollDown` and `Actions/Counter` do not exist
in the prefab — duplicate the three under `Main` into `Actions`, set their `anchoredPosition.y` to
**−392** (just below the new 384-tall viewport), and assign them to the `Actions` `ScrollList`'s
`scrollUp` / `scrollDown` / `counter` slots. Their `keyInfo.keyName` must stay exactly `ScrollUp`
and `ScrollDown`: `Panel.Update` hands the key to each list's `HandleKey` before raising
`KeyPressed` (`Panel.cs:131-139`), which is what keeps an arrow press from reaching `MenuControl`.

**3. Stop the three-way joystick scroll.** Give `ScrollList` a modifier so the joystick only drives
the list under the beam:

```csharp
    [Tooltip("Only scroll on the joystick while the pointer is inside this list's viewport. " +
             "Two lists and a rules panel all read rightJoystick.y, so without this one push " +
             "moves all three at once.")]
    public bool joystickNeedsHover = true;
```

and in `ScrollList.Update`, before the direction test:

```csharp
        if (joystickNeedsHover && !PointerIsOverMe())
        {
            lastDirection = 0;
            return;
        }
```

where `PointerIsOverMe()` walks `Panel.pointer.currentKey.transform` up to see whether it is a
descendant of `viewport`. The arrows are then the affordance and the joystick is the shortcut, which
is the right way round.

---

## §4 — The words on the buttons

With §2 fixed the titles come back, and the remaining problem is that several of the right-hand
cells say nothing useful and two toggles do not show their state. Every string below is a literal in
`LobbyController.cs` or `MenuControl.cs`.

### Play panel — `LobbyController.DrawPlay`, lines 400-427

| Row | State cell now | Problem | Proposed |
| --- | --- | --- | --- |
| New Private Room | `"Host"` | restates the title | `"Start"` |
| Join Private Room | `"Code"` | a noun where an action belongs | `"Enter code"` |
| New Public Room | `">"` | **meaningless** | `"Choose game"` |
| Browse Public Rooms | `">"` | **meaningless, and identical to the row above** | `"Find a room"` |
| You are: *name* | `"Change"` | fine | keep |

`LobbyController.DrawBrowse`, lines 511-522:

| Row | Now | Proposed |
| --- | --- | --- |
| Back | `"<"` | `""` — the word "Back" is the affordance |
| Refresh, while a query is in flight | `"..."` | `"Refreshing…"` |
| the empty-list row | title `"No public rooms right now"` | add `subtitle = "Anyone can host one from the New Public Room row."` |

`DrawPickGame`, line 465: `Back` / `"<"` — same as above.

### Library panel — `LobbyController.RowFor`, lines 267-295, and `ShowDetail`, 297-320

| Where | Now | Problem | Proposed |
| --- | --- | --- | --- |
| detail, nothing selected | `"Point at a game to see what it is."` | **pointing does nothing — you must pull the trigger** | `"Pick a game to read its rules."` |
| state, unowned free game | `"Free — Add"` | "Add" is vague, and the em dash reads as a price separator | `"Add — Free"` |
| state, price not yet in | `"..."` | reads as broken | `"Price…"` |
| status during a purchase | `"..."` (line 339) | same | `"Working…"` |
| subtitle | `minPlayers + "-" + maxPlayers + " players"` | Stairs is `minPlayers 2, maxPlayers 2` → **"2-2 players"** | `min == max ? min + " players" : min + "–" + max + " players"` |

### Room menu — `MenuControl.BuildGameRows` / `BuildActionRows`, lines 325-420

| Row | Now | Problem | Proposed |
| --- | --- | --- | --- |
| a game the room can switch to | state `""` | nothing says pressing it moves the whole room | state `"Play"`; for the row where `selected` is already true, `"Playing"` |
| `Passthrough` | state `""` | **it is a toggle and shows no state**, unlike `Voice Chat` which shows `ON`/`OFF` | `PassthroughController.IsPassthroughOn() ? "ON" : "OFF"`, and set `selected` to match |
| `Place Anchor` | state `""` | silently does nothing for anyone but the room owner — `BoardAnchor.cs:366-370` logs `only the room owner places the anchor` and returns | subtitle `"Line every headset up to this room"`; when `!LocalPlayerIsRoomOwner()`, `state = "Host only"` and `pressable = false` |
| the panel header | `"Room"` (line 335) | the room's join code is reachable only by staring at the `InfoBlock` on your right hand (`VisibleWhenLooking.cs:43`) | put it on the status line |

`PassthroughController.IsPassthroughOn()` is already public and static
(`PassthroughController.cs:101`). The join code is on `RelayVivox.relayRoomCode`, reached the way
`VisibleWhenLooking.Start` reaches it:

```csharp
        // The room code belongs on the room menu. Today the only way to read it is to look at the
        // InfoBlock on your own right hand, which nothing tells you about.
        GameObject nm = GameObject.Find("Network Manager");
        RelayVivox relay = nm != null ? nm.GetComponent<RelayVivox>() : null;
        string code = relay != null ? relay.relayRoomCode : "";
```

and fold it into the two existing `SetStatus` calls at lines 354 and 371-373 rather than adding a
third writer to that line.

### The code pad's alphabet

`CodePad.alphabet` is `A-Z 0-9` (`CodePad.cs:39`). That is right for a six-character Relay join code
and wrong for the name pad opened at `LobbyController.cs:608`: a player cannot type a space or a
lower-case letter, so every name is a single shouted word. Either give `Open` an alphabet parameter,
or accept it and rename the row from `"You are: " + playerName` to something that does not imply
free text. Worth deciding; not worth blocking on.

---

## §5 — The code pad's `Cancel` key reads "Cance"

`CodePad.cellWidth = 150`, and `PadKeyTemplate` is 132 × 95 with a 44 pt label
(`CodePad.cs:35-36`, prefab). Measured preferred widths at that size against `LiberationSans SDF`:

```
'A'      29      'Back'    98      'Join'    81
'W'      43      'Clear'  105      'OK'      63
                 'Cancel' 137   <<< wider than the 132-unit key
```

The label has `enableWordWrapping = true` and `overflowMode = Truncate`, so "Cancel" breaks to two
44 pt lines (49 units each, 98 total) inside a 95-unit box and the second line is clipped. The key
renders **"Cance"**.

Three ways out; take the first:

1. **Shorten the word.** `CodePad.CancelKey` is a `const` used as the keyName *and* the label
   (`CodePad.cs:23`, `Clone` at 205-208). Changing it to `"Back"` collides with `BackKey`. Use
   `"Close"` (105 units, same as "Clear") — or keep `Cancel` and give the control row its own wider
   cell: the row has four keys in six columns, so there is room.
2. Set the label's `fontSize` to 36 for the control row only.
3. Turn on `enableAutoSizing` with `fontSizeMin = 28, fontSizeMax = 44` on `PadKeyTemplate/Label`.
   This fixes every future label too, at the cost of keys whose type size differs from each other.

Whichever you pick, set `overflowMode = Ellipsis` on `PadKeyTemplate/Label` so the next one that
overflows says so instead of quietly losing its last letter.

---

## §6 — The rules and detail text

### Emoji render as empty boxes

`TMP_Settings.defaultFontAsset` is `LiberationSans SDF`, Static, 250 glyphs, with one Dynamic
fallback (`LiberationSans SDF - Fallback`, source font `LiberationSans`). The dynamic fallback does
resolve the arrows `▲ ▼`, the em dash, and the box-drawing rules `───` used as separators — those
are all fine. Emoji are not in Liberation Sans at any size, and TMP substitutes **U+25A1 □**, logging
`The character with Unicode value \U0001F3AF was not found in the [LiberationSans SDF] font asset or
any potential fallbacks.`

| Asset | Unrenderable characters |
| --- | --- |
| `Assets/Resources/stepsRules.txt` | 7 — 🎯 🎲 🚶 ⚔ U+FE0F 🧱 🏆 |
| `Assets/Resources/BASHRules.txt` | 11 in 8 distinct — 🎯 🎮 ♟ U+FE0F ×4 🛥 ✈ ⛴ 🚁 |
| `Assets/Resources/plateauRules.txt` | none |

So Stairs' and BASH's rules show `□ Objective`, `□ Setup` and so on, in the in-room rules wing and in
the Library's detail block, which reads the same asset (`LobbyController.cs:316`).

**Fix:** strip the emoji from the two `.txt` files and use TMP rich text for the same emphasis — the
files already use `<size=150%><b>…</b></size>`, so `<size=120%><b>Objective</b></size>` costs
nothing and renders everywhere. Adding an emoji font asset to `TMP_Settings.fallbackFontAssets` is
the alternative and is not worth an extra atlas in the APK for seven glyphs.

### The Library's detail block truncates the rules

`Panel.prefab > Detail` is 1160 × 192 at 24 pt with `overflowMode = Truncate` — about six lines.
`ShowDetail` (`LobbyController.cs:310-319`) concatenates the blurb **and the entire rules asset**:
2,628 / 5,338 / 2,442 characters. The player gets the blurb plus two lines of rules, cut mid-word,
with no sign that there is more.

Pick one:

- **Blurb only in the lobby** — drop the `rulesText` concatenation. The rules have a home already,
  in the in-room panel. One line, and it is probably the right answer.
- Or give `Detail` the same treatment as the rules wing: a `RectMask2D` viewport, a
  `ContentSizeFitter`, and a `ScrollTextWithJoystick`. That is `MenuControl.BuildRulesPanel`'s
  85 lines of procedural construction, which would be better extracted onto the toolkit than copied.

### `Detail` and `Actions` occupy the same rectangle

Both are at `anchoredPosition (20, −700)`, `sizeDelta (1160, 192)`. Nothing collides today only
because the Library panel never fills its `Actions` list and the room menu never sets a `Detail` —
but the two are one `SetDetail` call away from drawing on top of each other. Moving `Actions` up
(§3) separates them; do it in the same edit.

### The rules wing steals the left joystick

`ScrollTextWithJoystick.cs:18-21` falls back to `leftJoystick.y` when the right stick is centred, and
the left stick is locomotion and snap-turn (`CameraController2`). For a player who is not colocated,
opening the room menu means walking forward also scrolls the rules. Delete the fallback — the right
stick is the documented control (`Assets/Scripts/CLAUDE.md`, Input).

---

## §7 — Hover and press are unreliable between adjacent rows

`pointerControl.OnTriggerExit` (`pointerControl.cs:69-80`) assigns `currentKey` from **the collider
that is leaving** and then nulls it, without checking whether that collider is the current key:

```csharp
        if (col.gameObject.tag == "key")
        {
            currentKey = col.gameObject.GetComponent<keyInfo>();   // <- the key being LEFT
            currentKey.ChangeToOffMaterial();
            currentLetter = "";
            currentKey = null;                                     // <- unconditional
```

List rows are adjacent — 96 units of pitch with a 96-unit collider — so sweeping the beam down a
list fires `OnTriggerEnter(B)` and `OnTriggerExit(A)` in the same physics step, in an order Unity
does not define. When `Exit(A)` lands second it wipes the `currentKey` that `Enter(B)` just set.
`OnTriggerStay` re-acquires on the next physics step (`pointerControl.cs:54-59`), so it heals — but
`Panel.Update` reads `pointer.currentKey` on trigger *release* and cancels the press when it is null
(`Panel.cs:126-129`). A release inside that window does nothing at all, and there is no feedback.

The same call also throws if anything tagged `key` has no `keyInfo` — `GetComponent` is
unguarded at lines 32, 56 and 73.

```csharp
    public void OnTriggerExit(Collider col)
    {
        if (col.gameObject.tag == "key")
        {
            keyInfo leaving = col.gameObject.GetComponent<keyInfo>();
            if (leaving != null)
            {
                leaving.ChangeToOffMaterial();
            }

            // Only forget the current key if it is the one being left. Rows in a ScrollList are
            // adjacent, so Enter(next) and Exit(previous) arrive in the same physics step in an
            // undefined order; clearing unconditionally drops the key the beam has already moved
            // onto, and a trigger release in that window is silently swallowed by Panel.Update.
            if (currentKey != leaving)
            {
                return;
            }

            currentLetter = "";
            currentKey = null;
            ...
```

Add the same null guard to `OnTriggerEnter` and `OnTriggerStay`.

**Related:** `keyInfo.MakeBigger` multiplies `localScale` by 1.2 (`keyInfo.cs:78-85`). On a 1160-unit
full-width row that is +116 units each side — clipped by the viewport mask — and +19 units downward,
over the next row. It reads as a glitch rather than as feedback. Give `keyInfo` a
`public bool growOnPress = true;`, guard both methods with it, and uncheck it on the two
`RowTemplate`s. Replace the feedback with a third colour: add `pressedColor` to `PanelRow` and have
`Panel.Update` apply it on trigger-down and restore `key.offColor` on release. The arrows and the
3D `SpawnMenu` keys keep the scale, where it works.

---

## §8 — The design pass: why it looks plain

Everything here is cosmetic and none of it is worth starting before §1–§3 are done, because the
panels cannot currently be judged.

The current palette is not the problem — it is coherent:

| Role | Colour | |
| --- | --- | --- |
| panel ground | `#0D0F14` @ 93% | |
| row, resting | `#21262F` @ 92% | |
| row, hovered | `#296B9E` | `keyInfo.onColor` |
| row, selected | `#2E805C` | `PanelRow.selectedColor` |
| title | `#F0F5FF` | |
| subtitle / detail | `#9EAEC2` | |
| status | `#59D9FF` | |
| disabled | `#8C94A1` | |

Six things make it read as a programmer's placeholder:

**1. Not one sprite in the project.** Every `Image` on all three prefabs has `sprite = NONE`, so
every surface is a hard-cornered rectangle. This is the single biggest visual win available. Either
assign Unity's built-in `UISprite` with `Image.type = Sliced` and tune `pixelsPerUnitMultiplier`
down (start at 0.2) until the corner radius is about 10 UI units, or — better, and worth the ten
minutes — author one 64 × 64 rounded-rect PNG with a 16 px radius, import it as
`Sprite (2D and UI)` with `border 16,16,16,16`, and use it for the panel ground, the rows and the
arrow keys. One asset, three prefabs, done.

**2. Rows are flush against each other.** `ScrollList.rowHeight = 96` and the row is 96 tall, so
there is no gutter at all and a list is one undifferentiated block. Leave `rowHeight` at 96 and set
`RowTemplate.sizeDelta.y` to **88** — the pitch stays, an 8-unit gap appears for free, and the row is
still one press target. **The `BoxCollider` must follow**: `size` 1160 × 96 × 10 → 1160 × 88 × 10,
`center` (0, −48, 0) → (0, −44, 0). It does not track the RectTransform.

**3. No hierarchy above the lists.** Two lists on the room menu with no captions and no separator.
Add an optional caption to the toolkit rather than to the prefab, so the lobby's single-list panels
stay clean:

```csharp
    [Tooltip("Optional heading above the list. Hidden when empty, so a panel that uses one list " +
             "does not carry a blank strip.")]
    public TMP_Text caption;

    public void SetCaption(string text)
    {
        if (caption == null) return;
        caption.SetText(text ?? "");
        caption.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }
```

`MenuControl` then sets `"Games"` and `"Room"`; `LobbyController` sets nothing. Add a 2-unit accent
rule under the panel header while you are in the prefab.

**4. No thumbnails.** All three modules have `thumbnail: {fileID: 0}`. Take a 256 × 256 shot of each
board in the Scene view, import as `Sprite (2D and UI)`, and drop it on the module. The slot,
the row layout and the `PanelRow.Bind` path already exist and are already correct
(`PanelRow.cs:117-121`) — this is authoring, not code.

**5. The type scale has six sizes and no rhythm** — 48 / 34 / 30 / 26 / 24 / 22. Tighten to four:
header 48, title 32, secondary 26 (status, state, detail), subtitle 24. And set `overflowMode` to
**`Ellipsis`** on `Title` and `State`, so a long room name says `Sam's very long roo…` rather than
being chopped. Check the 24 pt subtitle in a headset before committing to it — at 1.6 m it is the
smallest thing on the panel.

**6. The panel is a flat slab floating in passthrough.** Add a `UnityEngine.UI.Shadow` to the
`Background` image (one component, `effectDistance (4, -4)`, black at 50%) and a 4-unit accent-blue
rim. Keep the ground at alpha 0.93 — it must stay near-opaque or the passthrough feed shows through
the text. Do **not** reduce it below about 0.85.

---

## §9 — Smaller things, worth fixing while you are in there

- **`LobbyController.HandleLibraryKey` has no `busy` check** (`LobbyController.cs:322`), unlike
  `HandlePlayKey` (535-539). Library rows stay pressable during a join, and a purchase that lands
  mid-connect redraws a panel that is about to be destroyed.
- **`HandlePadSubmitted` dispatches on the submit *label***, `padPurpose == "Join"`
  (`LobbyController.cs:672`). Two pads with the same button word would collide. Use an enum.
- **A join code shorter than six characters is sent anyway** — only `value.Length == 0` is rejected
  (`LobbyController.cs:674`). A five-character code fails as `Wrong room code`, which is at least
  honest, but the pad knows the length and could say so before the round trip.
- **`RowTemplate`'s `BoxCollider` width 1160 is hardcoded** to `Panel.prefab`'s viewport width. A
  panel of any other width gets a press target that does not match the row. Worth a line in
  `ScrollList.EnsureSlots` setting `box.size.x` from `viewport.rect.width` after the re-anchor.
- **`Detail` is declared on `Panel` but `CodePad.prefab` leaves it null** — harmless (`Panel.cs:84`
  guards), noted so nobody "fixes" it by wiring one.
- **`MenuControl.worldRoot` is unassigned everywhere**, so the board is not hidden behind an open
  menu. Already recorded in the root `CLAUDE.md` under Dead or unwired code; if the menu is going to
  sit over the board it is worth deciding on rather than leaving.

---

## §10 — Order of work

1. **§1**, the 180s. Four lines. Everything else is unjudgeable until this is done.
2. **§2**, the row layout in `Panel.prefab`. Eight fields, and `RoomMenu.prefab` inherits them.
3. **§3**, the action list. Prefab geometry plus the three arrow objects.
4. Verify in the Editor with two clients: lobby → host → room menu → switch game → room menu again.
5. **§4**, the wording, and **§5**, `Cance`. Text only.
6. **§6**, the rules text and the detail block.
7. **§7**, the pointer exit race and the press feedback.
8. **§8**, the design pass, last — it is the only part that needs taste rather than measurement.

Steps 1–3 need no headset. Step 4 needs two Editor clients or one Editor plus one device; note that
`Assets/DefaultNetworkPrefabs.asset` is untouched by any of this, so **no reflash is required** for
a device that is already on this build — nothing here moves the join config hash.

### What was applied, and where it came out differently

**§1** — the `180f` term is gone from all four sites. Two of them also stopped inheriting the
camera's pitch: `OpenMenu1` and `moveMenu` now place off a flattened forward, so the menu and the
rules wing beside it stay coplanar when the player is looking down at the board.

**§2** — the geometry in the table above was superseded by folding §8's row gutter into the same
edit, so the numbers that shipped are for a **76-unit row in an 84-unit pitch**, not a 96-unit row:

| Child | anchorMin/Max | pivot | anchoredPosition | sizeDelta |
| --- | --- | --- | --- | --- |
| `Thumb` | (0, 1) | (0, 1) | (20, −8) | 60 × 60 |
| `Title` | (0, 1) | (0, 1) | (96, −4) | 570 × 40 |
| `Subtitle` | (0, 1) | (0, 1) | (96, −46) | 570 × 28 |
| `State` | (1, 1) | (1, 1) | (−20, −4) | 380 × 68 |

with `PanelRow.titleYWithSubtitle −4`, `titleYAlone −18`, `textXWithThumb 96`, `textXNoThumb 20`.

**§3** — the plan put the arrows *below* each list and could not fit four action rows on a
1200 × 900 panel. What shipped puts each list's caption, counter and arrows on **one band above the
list**, which freed the space: `Main` is 4 rows at `(20, −190)` 1160 × 336, `Actions` 3 rows at
`(20, −592)` 1160 × 252, and **both** lists have a caption, arrows and a counter. Three action rows
covers every case except BASH-as-host, which is the one that keeps the arrows honest.
`joystickNeedsHover` needed a pointer reference on `ScrollList`, pushed down by `Panel.Bind`
alongside `inputs`.

**§5** — took option 1. `CodePad.CancelKey` is `"Close"` (107 units at the new 42 pt label size,
against a 132-unit key), and the label overflows as `Ellipsis` so the next one to overrun says so.

**§6** — the emoji were the only unrenderable characters; 7 came out of `stepsRules.txt` and 11 out
of `BASHRules.txt`, replaced with the rich-text emphasis the files already use. `plateauRules.txt`
needed nothing. The detail block took the "blurb only" option. `ScrollTextWithJoystick` lost its
left-stick fallback.

**§8.4** — the thumbnails were rendered rather than hand-authored: a three-quarter view of each
scene's `World Root`, framed on its renderer bounds, 256 × 256, in `Assets/Textures/Ui/Thumb_*.png`.

**Added beyond the plan**, all of it small and all of it in the same pass: `Panel.SetDetail` warns
when the detail block and list 1 both have content, since they share a band;
`ScrollList.EnsureSlots` sizes each slot's `BoxCollider` from the viewport rather than trusting the
authored 1160; `keyInfo` gained `IsOn`/`RefreshTint()` so a pressed row restores the right colour
whether or not the beam is still on it; `pointerControl`'s three `GetComponent<keyInfo>()` calls are
null-guarded; the code pad's entry line sits on its own slab; and the five `"..."` status strings
were unified on `…`.

**Not applied:** `MenuControl.worldRoot` is still unassigned. §9 calls it a decision rather than a
fix and it is still one — the board is not hidden behind an open menu, deliberately, until somebody
says it should be.

### Verified

- Zero `error CS`, zero warnings; all five `MRBoardGame.*.dll` rebuilt after the edits.
- Row layout measured in an isolated preview scene, `Panel.prefab` **and** `RoomMenu.prefab`: every
  `Thumb`, `Title`, `Subtitle` and `State` inside the viewport on every slot, arrows and counter
  activating on overflow, counter reading `1-4 / 6` and `1-3 / 6`.
- Every string the UI writes, and all three rules assets, render with no missing glyphs.
- Orientation recomputed from the shipping expressions: all four surfaces readable and aimed at the
  player, matching the nametag reference exactly.
- `MR Template > MR > Check Game Catalog` clean; all three modules carry a thumbnail.
- `OpeningScene` untouched and not dirty — the thumbnail pass opened the three game scenes
  additively and closed them without saving.

### Still needs a headset

Legibility at 1.3–1.6 m; how the new pressed colour reads without the scale jump; whether
`joystickNeedsHover` feels right in the hand; and the room menu with two clients, where the
host-only `Voice Chat` and `Place Anchor` rows differ between them.

---

## §11 — How to re-measure any of this

All three run against the open Editor through `Unity_RunCommand`. Note the quirk from
`Assets/Scripts/CLAUDE.md`: the harness wraps the script in
`Unity.AI.Assistant.Agent.Dynamic.Extension.Editor`, where `Image` resolves to a namespace — write
`UnityEngine.UI.Image` in full or it will not compile.

**11.1 — Is a panel mirrored?** Place the player at the origin looking down +Z and test the two
dot products. `dot(rotation * Vector3.right, camera.right) > 0` means the text runs left-to-right;
`dot(rotation * Vector3.forward, -(toViewer)) > 0` means it is aimed at the player. Compare any
candidate against `Quaternion.LookRotation(panelPos - camPos)`, which is what the nametags use.

**11.2 — Is a row's content inside the viewport?** Instantiate `Panel.prefab`, reproduce
`ScrollList`'s re-anchor (`anchorMin (0,1)`, `anchorMax (1,1)`, `pivot (0.5,1)`,
`anchoredPosition (0, -i*rowHeight)`), then for each child call `GetWorldCorners` and
`viewport.InverseTransformPoint` the results. Anything with `x < viewport.rect.xMin` is clipped.

**11.3 — Will a glyph render?** `TMP_Settings.defaultFontAsset.HasCharacter(c, searchFallbacks: true,
tryAddCharacter: true)`. **All three arguments matter** — the one-argument overload searches neither
the fallback nor the dynamic atlas and reports `▲` as missing when it renders perfectly well. Code
points above `U+FFFF` cannot go through the `char` overload at all; Liberation Sans has no
astral-plane glyphs, so treat them as missing.

**11.4 — Will a label fit?** Build a throwaway `TextMeshProUGUI`, assign the font and size, set the
text, `ForceMeshUpdate()`, read `preferredWidth`. Compare against the label's `sizeDelta.x`. Do not
trust `textInfo.characterCount` on an object outside a Canvas — it stays 0.

---

## Where the docs need updating when this lands

Per the root `CLAUDE.md`, the doc changes in the same commit as the code.

- **`Assets/Scripts/CLAUDE.md`, "Menus, pointer, keys"** — the paragraph beginning *"A world-space
  Canvas draws on its `+Z` face, so a panel given the camera's own rotation shows the player its
  back"* is the wrong rule and is what the four call sites were written from. Replace it with the
  `-Z` statement already in `Assets/Scripts/Stairs/CLAUDE.md:334-338` and cross-link the two, so
  there is one rule in the project rather than two that contradict each other.
- Same file, **"Known rough edge"** on `rightJoystick.y` — becomes three readers, not two, until
  §3's `joystickNeedsHover` lands; then it goes away and the note should say so.
- Same file, the `ScrollList` bullet list — add that a list's `scrollUp`/`scrollDown`/`counter` are
  required whenever `visibleRows` can be smaller than the row count, and that `Actions` shipped
  without them.
- **Root `CLAUDE.md`, "Design docs"** — add the row for this file.

Then, as that file requires:

```powershell
powershell -File Tools/Check-DocLinks.ps1
```
