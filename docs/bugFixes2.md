# bugFixes2 — four bugs from the first two-headset session

Working document for the four defects reported after playing on a Quest 3 and a Quest 3S on
2026-09-05, in the style of the rest of `docs/`: the plan with the evidence attached, so a claim
can be checked rather than taken on trust.

> **Status: §1, §2, §3 and §4 are APPLIED.** §0 is not — `DefaultNetworkPrefabs.asset` still holds
> twelve entries and the auto-generator is still on, so the Editor still cannot host for a device.
> Three things came out differently from the plan below, and each is noted inline where it belongs:
>
> - **§1** — the answer to question 1 was *both* players pressed `Place Anchor`, and nothing visibly
>   happened. Only the room owner's press does anything (`BoardAnchor.RequestPlaceAnchor` →
>   `LocalPlayerIsRoomOwner`, `:360-364`), so the guest's press only logged
>   `"only the room owner places the anchor"` — there was never a second anchor. That leaves §1b
>   unsettled rather than confirmed, so it was applied defensively along with all three visibility
>   items.
> - **§1** — raising the default on `MaxAnchorHeightDisagreement` was **not enough on its own**.
>   It is a public serialized field and `PersistentRig.prefab:2378` still carried `0.25`, which wins
>   over any initializer. The prefab value was raised to `0.6` too.
> - **§4** — option 2 was chosen, and `ServeSeats` became two passes rather than one plus a tail:
>   the ring-slot rule has to claim every base it is entitled to *before* the fallback hands any
>   out, or a leftover player takes a base by winning the enumeration order.
>
> Original document follows unchanged.

**Nothing here has been applied.** Everything marked *Verified* was read out of the working tree at
`dfdaea5` plus the uncommitted changes to `Assets/Prefabs/Player.prefab` and
`Assets/Scripts/PlayerControls.cs`. Anything marked *Hypothesis* needs one measurement in a headset
before it is worth writing code for.

| # | Symptom | Root cause | Confidence |
| --- | --- | --- | --- |
| [1](#1--the-players-were-not-aligned-in-the-room) | Players not aligned in the real room | Most likely no shared anchor at all, or the 0.25 m height guard rejecting a floor-calibration difference. The alignment *maths* is correct | **Hypothesis** — two questions settle it |
| [2](#2--no-username-scoretag-or-gemheart-labels-over-anybodys-head) | No nametag / scoretag / gemheart labels | The uncommitted `TagsRoot` reparent kept Unity's world-position-preserving offsets. Every label is **6.524 m** off its root along the view axis | **Verified** |
| [3](#3--opening-the-spawn-menu-drops-the-plateau-selection) | Plateau deselects when the spawn menu opens | `dfdaea5` added a 0.15 s open delay to `PlateauSpawnMenu`. `PlateauSelection` cancels during that window because the menu is not open *yet* | **Verified**, a regression in the last commit |
| [4](#4--only-one-set-of-bases-in-bash) | Only one player got a BASH base | `PlayerRing.PickFreeSlot` hands the second player slot **6**, and `SpawnManager` only serves slots 0-3 | **Verified** |

Bugs 2, 3 and 4 are all cheap and all independent. Bug 1 is the only one that needs a measurement
before code.

---

## 0 — One thing to know before touching any of it

`Assets/Prefabs/Player.prefab`'s `GlobalObjectIdHash` changed in the uncommitted edit — **393850970
→ 3192981804** (`Player.prefab:449`). `NetworkManager.NetworkConfig.ForceSamePrefabs` is `1`, which
folds every registered prefab hash into the config hash a joining client sends, so **a headset still
on an APK built before 09:17 today cannot join one built after it.** Flash both headsets from the
same build, every time, for as long as `Player.prefab` is being edited.

Separately, and still outstanding: `bigFixes1.md` §1 was never applied. `Assets/DefaultNetworkPrefabs.asset`
still holds **twelve** entries, eight of them Meta SDK prefabs from `Library/PackageCache`, four of
those under an `Editor/` folder — and `ProjectSettings/NetcodeForGameObjects.asset` still does not
exist, so the auto-generator is still on. Consequence, unchanged: **the Editor can never host for a
device, or join one.** That did not block this session because both clients were headsets, but it
will block every attempt to debug any of the four bugs below from the Editor with a headset
attached. It is worth the ten minutes.

---

## 1 — The players were not aligned in the room

### What is definitely correct

I read the whole alignment path against Meta's SDK, because you asked. It is right:

- **The transform is exact.** `CameraController2.AlignRigToAnchor` (`CameraController2.cs:263-317`)
  builds `deltaRot` as the yaw-only inverse of the anchor's rotation and `deltaPos` as
  `-(deltaRot * anchor.position)`, so the map applied to the rig is `p ↦ deltaRot * (p - anchor.position)`.
  That sends the anchor to the world origin exactly — **including its height**, which the translation
  cancels in full. The class comment's algebra checks out.
- **The height *is* taken, despite being applied through a horizontal-looking formula.** This is worth
  saying plainly because it is easy to misread the code as yaw-and-xz only. It is not.
- **The execution order is right.** `OVRSpatialAnchor` has no `[DefaultExecutionOrder]`, so it runs
  at 0, and its `Update()` calls `UpdateTransform()` (`OVRSpatialAnchor.cs:801-809`). `BoardAnchor`
  is at order 10 and therefore reads this frame's anchor pose, not last frame's.
- **The premise behind the height guard holds.** `OVRSpatialAnchor.TryGetPose`
  (`OVRSpatialAnchor.cs:779-796`) converts the tracking-space pose with `pose.ToWorldSpacePose(Camera.main)`,
  which composes the camera's world pose with the inverse of the head's tracking pose — i.e. exactly
  the tracking-origin→world transform, which for this rig is `XRRig.localToWorld ∘ CameraOffset.local`.
  With `Camera Offset` at `y = 0` (Floor mode), `anchor.position.y - XRRig.position.y` really does
  reduce to the anchor's height above *this headset's* floor. The comment at `CameraController2.cs:296-299`
  is accurate.
- **The permissions and project config are complete.** `USE_ANCHOR_API` and `IMPORT_EXPORT_IOT_MAP_DATA`
  are both in `Assets/Plugins/Android/AndroidManifest.xml:18-19`, and `MRPassthroughSetup.cs:68-80`
  writes `anchorSupport = Enabled` and `sharedAnchorSupport = Supported` into `OVRProjectConfig`.

One cosmetic wart, deliberately *not* a bug: the anchor's yaw comes from `head.eulerAngles.y`
(`BoardAnchor.cs:403`), and Euler decomposition is unstable near vertical pitch — which is a real
posture for somebody placing an anchor at their own feet. It does not matter, because every headset
reads the *same* anchor: a strange yaw only rotates the shared world relative to the room, and the
board is hand-placed afterwards anyway. Leave it.

### The two things that can actually produce what you saw

#### 1a. No anchor was ever placed — *check this first*

Colocation is not automatic. The room owner has to open the menu and press **`Place Anchor`**
(`MenuControl.cs:245-251` → `BoardAnchor.RequestPlaceAnchor`). Until somebody does,
`RoomAnchor.anchorUuid` is empty, `PollRoomAnchor` returns immediately (`BoardAnchor.cs:510-513`),
`boundAnchor` stays null, `HoldAlignment` returns on its first line, and `LocalIsAligned` stays
false on **both** headsets.

What that looks like from inside is precisely "the players were not aligned": each headset falls
back to `CameraController2.PlaceAtRingSlot`, which stands player one at ring slot 0 — `(0, 0, -2)` —
and player two at slot 6 — `(0, 0, +2)` — *each measured from that headset's own tracking origin*.
Two people standing side by side in one real room see each other's avatars on opposite sides of a
board that is itself in two different real places.

If `Place Anchor` was never pressed, **there is no bug here and nothing below needs doing.**

#### 1b. `MaxAnchorHeightDisagreement` turns a floor-calibration difference into total failure

This is the code-level defect, and it is exactly the failure mode your "I didn't set up the space"
instinct points at.

`CameraController2.cs:300-313`:

```csharp
float anchorAboveMyFloor = anchor.position.y - transform.position.y;
if (Mathf.Abs(anchorAboveMyFloor) > MaxAnchorHeightDisagreement)   // 0.25 m
{
    WarnHeightDisagreement(anchorAboveMyFloor);
    return;                       // <- returns BEFORE SetAligned(true)
}
```

The `return` is above `SetAligned(true)` at `:316`. So a headset whose floor estimate is more than
25 cm from the anchor's **never aligns at all** — not once, not ever, because the check is re-run and
re-fails every frame. It silently keeps locomotion, keeps its ring slot, and plays as a player in a
different room.

The problem is that the guard rejects the very case the correction exists for. Its own comment
(`:292-295`) says: *"each headset puts y = 0 on its OWN estimate of the floor and those estimates
routinely differ by a few centimetres. The anchor is the only object in the system that knows the
real answer."* A headset that has never had Space Setup run does not differ by a few centimetres —
it can be half a metre out, because it is guessing the floor from whatever surface it last saw. At
0.30 m the guard fires and you get *no* alignment, when applying the correction would have produced
a *correct* one.

The guard is doing two jobs at once, and that is why it is mis-sized:

| Job | Magnitude | Right test |
| --- | --- | --- |
| Catch the `XROrigin` startup race — `Camera Offset` still at the serialized `1.36144` until the input subsystem reports Floor mode | **~1.36 m**, a known constant | ask `XROrigin` what mode it is in |
| Catch a genuine tracking failure | arbitrary garbage | a height sanity bound |
| *(absorbed by mistake)* a real floor-calibration difference | 0 – ~0.5 m | this is what the correction is **for** |

Split them. Two changes in `CameraController2`:

```csharp
// Cached in Start(); XROrigin is on this same GameObject (see CLAUDE.md, The persistent rig).
// Needs: using Unity.XR.CoreUtils;  and  using UnityEngine.XR;  (TrackingOriginModeFlags)
XROrigin origin;
```

```csharp
// In AlignRigToAnchor, BEFORE the height guard.
//
// XROrigin.MoveOffsetHeight only zeroes Camera Offset once the input subsystem reports Floor
// mode; until then every pose in this method is off by the serialized CameraYOffset (1.36144)
// and no alignment computed from it means anything. Ask the mode directly rather than inferring
// it from a height — that inference is what forced MaxAnchorHeightDisagreement down to a value
// too tight to absorb a real floor-calibration difference, which is the thing this method is
// supposed to CORRECT rather than reject.
if (origin != null && origin.CurrentTrackingOriginMode != TrackingOriginModeFlags.Floor)
{
    return;
}
```

```csharp
[Tooltip("Reject an anchor that localizes further than this from this headset's own floor. " +
         "This is a TRACKING-failure bound, not a floor-calibration one: a headset that has " +
         "never had Space Setup run can be half a metre out, and taking the anchor's height is " +
         "exactly how that gets corrected. The startup race is caught separately, by asking " +
         "XROrigin whether it is in Floor mode yet.")]
public float MaxAnchorHeightDisagreement = 0.6f;
```

0.6 m is a judgement, not a measurement: comfortably above any plausible floor-estimate error,
comfortably below the 1.36 m startup offset — so even if the mode check above were somehow bypassed,
the old protection still holds.

### Make the answer visible, whichever it is

The whole of §1 is guesswork today because nothing tells a player which state they are in. Three
small things, in increasing order of cost:

1. **Say it out loud.** `CameraController2.SetAligned` (`:333-344`) already detects the edge and
   already has the idempotency guard, so this is two lines inside the `if`:

   ```csharp
   Debug.Log(value ? "Colocation: aligned to the room anchor."
                   : "Colocation: alignment LOST — this headset is now in its own room.");
   ```

   `DebugLog` mirrors every `Application.logMessageReceived` into the in-headset box
   (`DebugLog.cs:44-51`), warnings included, so this shows up where you can read it.

2. **Turn the probe on** for the next session. `ColocationProbe` is on `XRRig` with `m_Enabled: 0`
   (`PersistentRig.prefab:2578`). It prints `aligned`, `anchored`, `tracked` and the short UUID once
   a second, and CLAUDE.md's *Debugging in the headset* section already explains how to read it.
   Differing `uuid` on two headsets means they are on different anchors; the same uuid with
   `aligned=False` on one is §1b.

3. The existing `WarnHeightDisagreement` (`:319-331`) already logs the exact number every 5 s. If
   §1b is what happened, that warning was in the box the whole time and reads
   `"the room anchor localized -0.42 m from this headset's floor"`.

### Before the next session, regardless

Run **Space Setup** on both headsets, and check **Settings ▸ Privacy and Safety ▸ Device Permissions
▸ Share Point Cloud Data** is on for both — `BoardAnchor.cs:437-441` already names that setting in
its error message, which is the one to look for if `Place Anchor` fails outright.

---

## 2 — No username, scoretag or gemheart labels over anybody's head

**Verified, and the cause is in the working tree, not in a stale build.** `BoardGames.apk` was built
at 09:19:30 today; `Player.prefab` was last saved at 09:17:12 and `PlayerControls.cs` at 09:16:43.
The build you played *does* contain these changes.

### What the reparent actually did

The three labels were moved under a new `TagsRoot` child, and Unity did what it always does on a
reparent: **preserved their world positions by writing compensating offsets into both sides.**

| Object | Where | Local offset from `TagsRoot` |
| --- | --- | --- |
| `TagsRoot` | `Player.prefab:650-667` | itself at `(0.41846, 0, 6.524)` under the Player root |
| `Username` | `:53-71` | `(-0.41846, 0, **-6.524**)` |
| `ScoreTag` | `:686-704` | `(-0.41846, +0.287, **-6.524**)` |
| `Gemheart` | `:234-252` | `(-9.71146, -1.79, **-6.524**)` |

(The x and y come from `m_AnchoredPosition`, not `m_LocalPosition` — these are `TextMeshPro`
components, which require a `RectTransform`, and a RectTransform under a plain-`Transform` parent
resolves its anchors against a zero-sized parent rect, so anchoredPosition *is* the local x/y.
Unity's own serialization proves the point: on the reparent it left `m_LocalPosition.x/y` at 0 and
put the compensation in `m_AnchoredPosition`.)

Those `±6.524` cancel while `TagsRoot` sits still. They stop cancelling the moment anything moves it
— and `PlayerControls.Update` now moves *and rotates* it every frame (`PlayerControls.cs:385-390`):

```csharp
tagsRoot.position = smoothFacePos + new Vector3(0f, FaceBelowEyes + NameTagAboveEyes, 0f);
Vector3 lookDirection = myCam.position - tagsRoot.position;
tagsRoot.rotation = Quaternion.LookRotation(-lookDirection);
```

`tagsRoot.forward` now points **away** from the viewer, so the children's local `z = -6.524` puts
every label **6.524 m from the player's head, straight towards the viewer's face** — and straight
past it. Two people standing 2–3 m apart around a table put the labels three to four metres *behind
the viewer's own head*. `Gemheart` is additionally 9.7 m to one side and 1.79 m down.

Nothing is being hidden and nothing failed to resolve. The labels are rendering — in the room behind
you.

### The fix: zero the offsets, in the Inspector

`TagsRoot` exists so the three labels can be positioned and billboarded as one group. That only
works if its children sit at the offsets you *want*, not at the leftovers of a drag-and-drop. Open
`Player.prefab` and set, on each object's Transform / Rect Transform (with the parent being a plain
Transform the Inspector shows plain **Pos X / Pos Y / Pos Z** fields, which is what these are):

| Object | Pos X | Pos Y | Pos Z | Also |
| --- | --- | --- | --- | --- |
| `TagsRoot` | 0 | 0 | 0 | rotation identity, scale 1 |
| `Username` | 0 | 0 | 0 | — |
| `ScoreTag` | 0 | **0.287** | 0 | keeps the number above the name, as authored |
| `Gemheart` | 0 | **-0.28** | 0 | see below |

Do it in the Inspector rather than by hand in the YAML: RectTransform recomputes `m_LocalPosition`
from `m_AnchoredPosition` on load, so a hand edit to the wrong one of the pair is silently ignored.

**`Gemheart` needs two more edits**, because it is the old unused `Text (TMP)` object renamed and it
carries that object's layout junk:

- `m_SizeDelta` is `20 × 5` (`:251`) against `6 × 0.4` on the other two.
- `m_margin` is `{x: 18.613, y: 0, z: 0, w: 4.736}` (`:388`) — an 18.6-unit **left** margin on a
  20-wide box, so its centred text renders about 9 units right of its own rect origin. The old
  `anchoredPosition.x` of `-9.293` was hand-dragged to cancel exactly that. It is two numbers
  fighting each other and neither is meant.

  Set **Width 6, Height 0.4** and **Margins all 0** (Extra Settings ▸ Margins on the TMP inspector).
  Then Pos X 0 above actually centres it.

### While the prefab is open

- **`ScoreTagBelowName` is now dead.** `PlayerControls.cs:49` declares it, `Player.prefab:494` sets
  it to `-0.28`, and nothing reads it any more — the old `UpdateScoreTag` used it to position the
  label and the rewrite (`:195-214`) does not. Delete the field. The layout now lives in the prefab,
  which is better: it is visible in the Scene view instead of being a number in a script. (Keep
  `NameTagAboveEyes` — `:387` still uses it.)
- **The `TagsRoot`-less fallback branches are worth keeping**, both in `OnNetworkSpawn` (`:100-104`)
  and in `Update` (`:391-396`). They cost nothing and they are what would have kept the nametag
  working through this. But note the `OnNetworkSpawn` fallback never resolves `gemheartLabelTransform`,
  which is correct — there is no such child in the old shape — and `UpdateScoreTag` already
  null-guards it.

### Verification

Two clients, in **ChasmGame** (`ScoreTag` and `Gemheart` are deliberately hidden anywhere else —
`UpdateScoreTag`'s `isChasmGame` test at `:197`). Each player should see the other's name, with the
number above it and the caption below, sitting above their real head and turning to face them as
either walks. You should see nothing above your own head; that is `OnNetworkSpawn:143-146`
deactivating every child of your own avatar, and it is correct.

---

## 3 — Opening the spawn menu drops the plateau selection

**Verified. It is a regression introduced in `dfdaea5`**, the most recent commit — `git log -S leftGripTimer`
names it, and it is the same commit that added the rotating movement and parabolic shots.

### The exact sequence

`PlateauSpawnMenu` is `[DefaultExecutionOrder(23)]` and `PlateauSelection` is `(25)`, and the class
comment on the former (`PlateauSpawnMenu.cs:24-26`) states why:

> *"Order 23: must execute before PlateauSelection (25) so that the menu opens before PlateauSelection
> checks IsOpen, preventing the selection from being cleared when the left grip is pressed."*

That was true. `dfdaea5` then added a 0.15 s debounce (`PlateauSpawnMenu.cs:88-99`):

```csharp
if (isLeftGripHeld) { leftGripTimer += Time.unscaledDeltaTime; } else { leftGripTimer = 0f; }
bool wantOpen = leftGripTimer > 0.15f;
```

so the menu no longer opens on the frame the grip goes down. Meanwhile `PlateauSelection.Playable`
stands down the moment either grip is held (`PlateauSelection.cs:224-227`), and the exemption that
protects the selection is keyed on the menu being **already open** (`:137-148`):

```csharp
if (!Bind() || !Playable(game))
{
    if (spawnMenu == null || !spawnMenu.IsOpen)
    {
        Cancel();            // <- fires here, in the 0.15 s window
    }
    return;
}
```

| Frame | `PlateauSpawnMenu` (23) | `PlateauSelection` (25) |
| --- | --- | --- |
| grip down | `leftGripTimer = 0.011`, `wantOpen` **false**, menu stays closed | `Playable` false (grip held), `IsOpen` **false** → **`Cancel()`** |
| +0.15 s | menu opens | selection was destroyed nine frames ago |

`Cancel()` (`:574-588`) clears `selected`, and `ClearHighlights` → `ClearPlateauSelection` clears
`selectedPlateau` too — so both the piece selection and the plateau selection go. That is why
`Add Gemheart` / `Add Chasmfiend` do nothing: `TryGetSelectedPlateau` (`:767-771`) requires
`state == State.PlateauSelected`, and by the time the key is pressable the state is `Idle`.

The debounce itself is worth keeping — it is there so the menu does not flash during the two-grip
world grab, which is a real problem.

### The fix: exempt the *gesture*, not just the open menu

`PlateauSelection` needs to know that a spawn-menu open is **in progress**, not merely finished.
Add one property to `PlateauSpawnMenu` beside `IsOpen` (`:47`):

```csharp
/// <summary>True while the menu is up OR while the left-grip-only gesture that opens it is
/// being timed out. PlateauSelection stands down for a held grip but must not CANCEL for one
/// that is opening this menu — the menu's "-" and Gemheart keys act on exactly the selection
/// it would be throwing away. IsOpen alone is not enough: the 0.15 s debounce means the menu is
/// still closed for about ten frames after the grip goes down, and the cancel lands in there.
/// The timer is only ever non-zero for a LEFT grip held BY ITSELF with Menu1 shut, so this
/// cannot swallow the cancel that a world grab or Menu1 is supposed to cause.</summary>
public bool IsOpenOrOpening => IsOpen || leftGripTimer > 0f;
```

and read it in `PlateauSelection.Update` (`:143`):

```csharp
if (spawnMenu == null || !spawnMenu.IsOpenOrOpening)
{
    Cancel();
}
```

That is the whole change. The `leftGripTimer > 0f` term is safe precisely because `isLeftGripHeld`
(`:86`) already requires `LeftGrip && !RightGrip && !menu.IsOpen` — engage the right grip and the
timer resets to 0 on the same frame, so a world grab still cancels the selection, and opening Menu1
still cancels it.

Also fix the now-false claim in the class comment at `PlateauSpawnMenu.cs:24-26`: the ordering no
longer prevents the cancel on its own, `IsOpenOrOpening` does. Order 23 still matters — the property
must be updated before `PlateauSelection` reads it — so say *that* instead.

### One small thing noticed in the same code

`PlateauSpawnMenu.Update` returns early when `Bind()` fails (`:67-82`) **without** resetting
`leftGripTimer`. Hold the left grip across a game switch and the timer survives at whatever it
reached, so the menu pops open on the first frame after the rebind rather than 0.15 s later. Add
`leftGripTimer = 0f;` to that branch. Cosmetic, one line, do it while you are in there.

### Verification

In ChasmGame: select a plateau (trigger on bare board — it pulses), then hold the left grip. The
plateau must keep pulsing while the menu comes up, and `Add Gemheart` must place one on it. Repeat
with a troop selected and `Remove Troop`. Then check the negatives still work: with something
selected, squeeze **both** grips — the selection must drop; and press `X` for Menu1 — it must drop.

---

## 4 — Only one set of bases in BASH

**Verified, and it is arithmetic rather than anything subtle.**

### Why the second player got nothing

`SpawnManager.ServeSeats` reads `PlayerControls.spawnSlot` and skips anything outside 0-3
(`SpawnManager.cs:107-111`):

```csharp
int seat = player.spawnSlot.Value;
if (seat < 0 || seat >= PlayingSeats)   // PlayingSeats = 4
{
    continue;                           // not seated yet, or a spectator
}
```

But `spawnSlot` is a **ring slot out of twelve**, and `PlayerRing.PickFreeSlot` (`PlayerRing.cs:63-94`)
deliberately hands out *the middle of the widest empty stretch* so that players end up spread evenly
around the board. Walk it through:

| Joiner | Occupied before | `PickFreeSlot` returns | `< 4`? |
| --- | --- | --- | --- |
| 1st | `{}` | **0** | yes — gets a base |
| 2nd | `{0}` | **6** | **no — gets nothing** |
| 3rd | `{0,6}` | **3** | yes — gets a base |
| 4th | `{0,3,6}` | **9** | **no — gets nothing** |

So with two players you get one base, and with four players you get two. Exactly what you saw.
`SpawnManager`'s doc comment ("The first four seats play; seats 4-11 get a ring slot and spectate")
assumed the slots fill 0, 1, 2, 3 in join order. They never have. The same wrong assumption is
written down in `PlateauPalette.cs:20-21` — *"Seats 0-3 — the order PlayerRing.PickFreeSlot hands
them out"* — which is also wrong, and is corroborating evidence rather than a second bug.

### The fix, and a nice surprise inside it

Do **not** change `PlayerRing`. Spreading players around the board is the point, and re-spacing them
is the involuntary rig move `PlayerRing.cs:56-61` refuses to make.

Instead, map ring slot → base index. And the mapping is not arbitrary — the four base positions
already line up exactly with four ring slots:

| Base | `SeatPosition` (`SpawnManager.cs:32-38`) | `SeatYaw` | Ring slot | `SlotPosition` (`PlayerRing.cs:35-39`) | `SlotRotation` yaw |
| --- | --- | --- | --- | --- | --- |
| 0 | `(0, 0, -1.4)` | 0° | **0** | `(0, 0, -2)` | 0° |
| 1 | `(0, 0, 1.4)` | 180° | **6** | `(0, 0, 2)` | 180° |
| 2 | `(1.4, 0, 0)` | -90° | **3** | `(2, 0, 0)` | -90° |
| 3 | `(-1.4, 0, 0)` | 90° | **9** | `(-2, 0, 0)` | 90° |

Position *and* facing agree on all four. So the mapping is not a workaround — it says **your base is
the one you are physically standing behind**, which for a colocated game around a real table is the
only answer that is not confusing. And because `PickFreeSlot` hands out 0, 6, 3, 9 in that order,
the first four joiners still get bases 0, 1, 2, 3 in join order, and the fifth still spectates.

In `SpawnManager`, beside `SeatPosition` and `SeatYaw` so the numbers that must agree sit together:

```csharp
/// <summary>
/// Ring slot -> base index, -1 for a spectator. NOT the identity: spawnSlot is a place on
/// PlayerRing's twelve-slot ring, and PickFreeSlot deliberately hands out the middle of the
/// widest gap — 0, 6, 3, 9 for the first four players — so treating it as a base index gave the
/// second player no base at all.
///
/// The four entries are not a convention, they are a measurement: SeatPosition/SeatYaw above and
/// PlayerRing.SlotPosition/SlotRotation agree on position AND facing for exactly these four
/// slots. A player's base is therefore the one they are standing behind, which is the only
/// arrangement that reads correctly around a real table. Re-derive this table if either set of
/// numbers ever moves.
/// </summary>
static readonly int[] BaseForRingSlot = { 0, -1, -1, 2, -1, -1, 1, -1, -1, 3, -1, -1 };

public static int BaseIndexForRingSlot(int slot) =>
    slot >= 0 && slot < BaseForRingSlot.Length ? BaseForRingSlot[slot] : -1;
```

Then in `ServeSeats` (`:107-111`):

```csharp
int seat = BaseIndexForRingSlot(player.spawnSlot.Value);
if (seat < 0)
{
    continue;               // not seated yet, or a spectator
}
```

Everything downstream already works on a 0-3 index: `baseBySeat` (`:44`), `SpawnBase`, and
`PlaceBaseClientRpc`'s bounds check (`:177`) all become correct rather than merely lucky.

**And change `LocalSeat()` with it** (`SpawnManager.cs:271-281`) — it returns the raw ring slot
today, and it is used only for colour:

```csharp
// Was: return player != null ? player.spawnSlot.Value : -1;
return player != null ? BaseIndexForRingSlot(player.spawnSlot.Value) : -1;
```

Rename it `LocalBaseIndex()` while you are there; the two call sites are `ControlListener.cs:290`
and `:440`. This fixes a second, quieter symptom: both `NetworkBaseControl.ChangeMaterial`
(`:310-325`) and `LineControls.ChangeMaterial` (`:41-51`) clamp their argument into `0..3`, so ring
slots 6 and 9 both clamped to **3** and two players' bases and trails came out the same colour.

`SpawnManager.SpawnBase` also passes the raw slot to `ChangeMaterial(seat)` (`:141`) and
`PlaceBaseClientRpc` (`:158`); after the change above `seat` is already the base index in both, so
nothing else moves.

### The one case this does not cover

Tying playable seats to four specific ring slots means a ring fragmented by mid-game departures can
leave a base unmanned. Concretely: five players, then the one at slot 0 leaves; `PickFreeSlot` with
`{1,3,6,9}` occupied returns **11**, not 0, so base 0 stands empty while a player has no base.

Two ways to go:

1. **Accept it.** It needs five players and a departure, and CLAUDE.md already records spectator
   seats as untested. Simplest, and it never puts anybody behind somebody else's base.
2. **Add a fallback**, ~8 lines in `ServeSeats`: after the table lookup misses, if any base index is
   unclaimed, hand out the lowest one. Nobody is ever left without a base while one is free; the
   cost is that such a player is standing somewhere other than behind their base. I would build
   this, but it is your call — see the questions below.

### Verification

Two headsets into BASH: two bases, in two different colours, one in front of each player, each
selectable only by the player standing at it. Then reset and try four if you can get four. Fire from
both — the trails should be the same colours as the bases.

---

## 5 — Suggested order of work

1. **§3**, the spawn-menu regression. One property and one call site, no rebuild risk, and it
   restores Gemheart/Chasmfiend placement, which is the feature `1e7d1b9` shipped and `dfdaea5`
   broke.
2. **§4**, the BASH seats. Self-contained, and it makes two-player BASH playable at all.
3. **§2**, the labels. Prefab-only apart from deleting one dead field.
4. **§0**, prune `DefaultNetworkPrefabs.asset` and turn off the auto-generator (`bigFixes1.md` §1
   steps 1-2). Do it before §1, because §1 is much easier to debug with an Editor host attached to a
   headset.
5. **§1**, colocation — but answer the two questions first. It may be nothing.

§2, §3 and §4 can all go into one build.

---

## 6 — Questions

1. **§1** — did the room owner press **`Place Anchor`** in the menu? If not, that is the whole of
   bug 1 and nothing in §1b needs doing.
2. **§1** — if they did: what was in the in-headset debug box? Specifically, did either headset show
   `"AlignRigToAnchor: the room anchor localized N m from this headset's floor"`? That line, and the
   value of `N`, settles §1b outright — and `N` also says how far apart the two floor estimates
   actually were, which is what `MaxAnchorHeightDisagreement` should be sized against rather than my
   guessed 0.6.
3. **§4** — option 1 (accept an unmanned base after a fragmented ring) or option 2 (fall back to the
   lowest unclaimed base)? I would build option 2, but option 1 is honest and needs five players
   before it matters.
4. **§2** — is `Gemheart` a static caption (its text is `"Gemhearts"`, fontSize 2, white) sitting
   under the `ScoreTag` number (text `"0"`, fontSize 3, pink)? That is what I have assumed for the
   layout above. If it is meant to display a *second* number — gemhearts on the board as distinct
   from gemhearts held — nothing currently computes one, and `PlateauGame` has only the single
   `gemheartScores` list that `ScoreTag` already shows.
