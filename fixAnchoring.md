# fixAnchoring.md — colocated alignment for the board game, and an avatar that is two cones and a name

`fix1.md` and `fix2.md` were written against **Math_Classroom_v8**, the project this one was forked
from. They describe two rounds of work on the same two objects — the hand cones and the nametag —
and they end with a colocation system that aligns every headset to one shared spatial anchor, every
frame. This document ports that work to **MRBoardGame2**, which has the same avatar and the same
bugs but **no anchoring code at all**, and adds the thing you asked for: the head and body never
render, so a remote player is two cones and a name.

**Audience:** the developer doing the work. Every claim below cites a real file, line, or serialized
value in this repo as of 2026-08-14, branch `mr-passthrough`, Unity 6000.5.4f1, URP 17.5.0, Meta XR
Core SDK 205.0.0, Oculus XR Plugin 4.5.4, Netcode for GameObjects 2.13.0, Vivox 16.10.0.

The SDK copy in `Library/PackageCache/com.meta.xr.sdk.core@4ea2676097b9` is the **same build** the
other two documents cite, and every line number they quote still lands: `OVRSpatialAnchor.cs:801-809`
is `UpdateTransform`, `:779-796` is `TryGetPose`, `OVRCommon.cs:119-134` is `ToWorldSpacePose`,
`OVRManager.cs:1933` is `AllowRecenter = true`, `OVRDisplay.cs:147-161` is the per-frame recenter
poll, and `AlignCameraToAnchor.cs:28` is `[DefaultExecutionOrder(10)]`. Read `fix1.md` §2 and §5 and
all of `fix2.md` first — this document does not repeat their derivations, it points at them.

---

## 0. What carried over, and what did not

| | Math_Classroom_v8 (fix1/fix2) | MRBoardGame2 (here) |
|---|---|---|
| `Player.prefab` | nametag at 1.43, cones at −1.22/−1.23, parent scale 0.2, cone scale 15 | **identical, to the value** (`Player.prefab:69`, `:648`, `:767`, `:29`, `:438`, `:620-633`) |
| Tracking origin | floor | **same** — `StairsGame.unity:960` `m_TrackingOriginMode: 2`, `OculusSettings.asset:28` `EnableTrackingOriginStageMode: 1` |
| Avatar sync | owner-written `NetworkVariable<Vector3>` pos/forward pairs | **same** — `PlayerControls.cs:27-32` |
| Anchoring | `ClassroomAnchor`, `AlignRigToAnchor`, `colocated`, `LocalIsAligned` | **none.** `OVRSpatialAnchor` appears nowhere in `Assets/` |
| Where a shared value lives | `seatControl`, a scene NetworkObject | **nothing.** `GlobalObjectIdHash` appears in no scene file — there are **zero** in-scene `NetworkObject`s |
| Standing positions | three arcs of seats, `RecenterToSeat` | `PlayerRing`, 12 slots on a 2 m circle, `CameraController2.PlaceAtRingSlot` |
| Root transform sync | stock `NetworkTransform`, server-authoritative | `ClientNetworkTransform` (`Player.prefab:338-367`), **owner**-authoritative — see §1.3 |
| Scene lifetime | one room scene for the session | **the room switches scenes mid-session** — `StairsGame` ↔ `ChasmGame` via `GameSelector.LoadGameScene` |
| The shared thing | a room with seats in it | **a board on a table** — `StairsGame.unity:228` puts `Board` at y 0.8 |
| Child lookup | `GetChild(0)`..`GetChild(4)`, hardcoded | by **name**, `PlayerControls.FindChild` (`:276-284`) |

The last four rows are why this is a port and not a copy-paste. Everything else transfers unchanged.

---

## 0.1 Summary

| # | Symptom | Cause | Certainty | Fix | Where |
|---|---|---|---|---|---|
| 1 | Nametag renders across the face | The tag is pinned **1.43 m above the avatar root** (`Player.prefab:69`) and the root is on the real floor (`PlayerControls.cs:261`). The floor does not predict the head. | **Certain.** Arithmetic, and it does not need two rooms or an anchor to reproduce — any two players in a session see it. | Drive the tag from the head pose, which is already networked. ~8 lines. | §1 |
| 2 | Head and body render on remote avatars | Nothing ever hides them. This project has no `ApplyAvatarVisibility` at all — only the owner's own children are disabled (`PlayerControls.cs:80-83`). | **Certain**, and it is the behaviour you asked to change. | One function, one switch, plus the prefab so there is no first-frame flash. | §2 |
| 3 | Remote cones sit ~18 cm out along the wrist | `+0.2f * forward` on the wire (`PlayerControls.cs:264-265`) very nearly cancels the −1.22/−1.23 in the prefab. What survives is a lever arm. | **Certain the lever arm exists**; whether anyone has noticed is unmeasured. | Broadcast the tracked pose; move the whole offset into the prefab. | §3 |
| 4 | Two headsets in one room do not share a frame | There is no colocation. Each client recentres onto its own ring slot, so two people at the same real table are metres apart virtually. | **Certain — the feature does not exist.** | Shared spatial anchor + **per-frame** alignment, built the way `fix2.md` §2 ends up rather than the way `fix1.md` starts. | §4–§8 |

**Order.** §1, §2 and §3 are independent of each other and of the anchoring work, need no anchors,
and can be verified with two headsets in two different rooms. Land them first and separately.
§4–§8 is the real project.

**Estimated effort.** §1 is 30 minutes. §2 is 20 minutes. §3 is an hour. §4–§8 is two to three days,
most of it two-headset testing. §9 (the probe) is an hour and should exist before §4 lands.

---

## 1. Defect 1 — the nametag sits across the face

This is `fix1.md` §2 verbatim, and the arithmetic is identical here because both numbers are
identical here. It is the cheapest certain win in the document.

### 1.1 Evidence

`Assets/Prefabs/Player.prefab:47-71` — `Username` is a `RectTransform` with

```yaml
m_Father: {fileID: 4966690844495405876}     # :65 — the Player root, a plain Transform
m_AnchoredPosition: {x: 0, y: 1.43}          # :69
```

A `RectTransform` whose parent is **not** a `RectTransform` treats its anchored position as a plain
local position, so the tag sits at local `(0, 1.43, 0)` — 1.43 m above the avatar root. Nothing ever
moves it: `PlayerControls.Update` writes only the tag's *rotation* (`:233-234`), never its position.

Now the root. `PlayerControls.cs:257-262`:

```csharp
Vector3 faceOffset = new Vector3(0, .36f, 0);
// Floor-level tracking origin: the rig's y IS the real floor, so project the head
// straight down onto it. ...
this.transform.position = new Vector3(myCam.position.x, rig.position.y, myCam.position.z);
facePos.Value = myCam.position - faceOffset;
```

The root is on the floor. The tag is 1.43 m above the floor. Nobody's eyes are at 1.43 m:

| Real eye height | Tag world y | Tag relative to eyes | What you see |
|---|---|---|---|
| 1.20 m (kneeling at a low table) | 1.43 | +0.23 m | floats above, looks fine |
| 1.45 m | 1.43 | −0.02 m | on the brow |
| 1.60 m | 1.43 | −0.17 m | **across the face** |
| 1.75 m | 1.43 | −0.32 m | at the collarbone |

Two standing adults both land in the "across the face" band, whatever headsets they are wearing.

The 1.43 is not arbitrary — it is a fossil. Before the MR conversion the tracking origin was
eye-level, so `myCam.position.y` read the serialized 1.36144 for everybody regardless of their real
height, the root landed near y = 0, and 1.43 put the tag 7 cm above the eyes. The origin moved to
the floor and the 1.36 assumption came out of the head but stayed in the prefab.

### 1.2 The fix

Anchor the tag to the head pose, which is already on the wire. In `PlayerControls`, next to the
other fields:

```csharp
// The mainFace mesh pivot sits this far below the eye point. Used by the owner to write
// facePos and by everyone else to recover the eye point from it, so it is one constant
// rather than the same magic number in two places.
private const float FaceBelowEyes = 0.36f;

// Nametag clearance above the eye point. Must clear the top of a REAL head seen through
// passthrough, not the top of the virtual one — the virtual head is never drawn (§2).
public float NameTagAboveEyes = 0.28f;
```

Then in the `!IsOwner` branch of `Update` (`:227-250`), before the rotation:

```csharp
// Position before rotation: the billboard is derived from where the tag IS, so computing
// it first also removes a one-frame lag that was there before.
usernameTransform.position = facePos.Value +
    new Vector3(0f, FaceBelowEyes + NameTagAboveEyes, 0f);

Vector3 lookDirection = myCam.position - usernameTransform.position;
usernameTransform.rotation = Quaternion.LookRotation(-lookDirection);
```

and in the owner branch use the constant at `:257` so the two can never drift apart:

```csharp
Vector3 faceOffset = new Vector3(0, FaceBelowEyes, 0);
```

Zero `m_AnchoredPosition.y` in `Player.prefab:69` afterwards. It is dead data the moment the code
writes `usernameTransform.position` every frame, and leaving 1.43 there is how the next person
spends an afternoon.

### 1.3 Why not just change the number in the prefab

Bumping 1.43 to ~1.95 looks right for one person of one height standing up, and is wrong for anyone
shorter, anyone kneeling to look under the board, and anyone leaning across the table. The height of
the tag is a property of the head, not of the room.

`fix1.md` §2.3 gives a second reason that **does not apply here**, and it is worth saying so
explicitly so nobody re-derives it: it argued that a client writing `this.transform.position` was
relying on server-authoritative `NetworkTransform` behaviour. In this project the Player root carries
`ClientNetworkTransform` (`Player.prefab:338-367`, script guid `439be0ec…`), whose
`OnIsServerAuthoritative()` returns `false` (`ClientNetworkTransform.cs:8-11`). The owner writing its
own root position at `PlayerControls.cs:261` is legitimate here. The other two reasons still stand:
the root's y stops being a useful predictor of anything the moment alignment lands (§5.4), and one
code path is better than two.

### 1.4 While you are in there

`Quaternion.LookRotation(-lookDirection)` at `:234` points the tag's **+Z away from the viewer**.
TextMeshPro generates its mesh readable from +Z, so this should read as mirrored text unless
something else compensates. It has evidently been fine in practice — but the tag is about to be
carrying the whole avatar (§2.4), so if anyone reports mirrored names, that minus sign is the
culprit and not the new position.

---

## 2. The avatar you asked for — head and body never render

This is `fix2.md` §6, adapted. The adaptation matters: **this project has no visibility logic at
all.** There is no `colocated` flag, no `ApplyAvatarVisibility`, and no code path that has ever
hidden a remote head. The only thing `PlayerControls` hides is your own avatar, from yourself
(`:80-83`).

### 2.1 What the five children actually are

`Player.prefab:238-243` lists them in order. Resolve them by **name**, not index —
`PlayerControls.FindChild` (`:276-284`) already does, and its comment says why.

| Object | What it is | Where it is defined |
|---|---|---|
| `tornado` | `Assets/tornado.fbx` — **the body** | nested `PrefabInstance &6540261695466956124`, `:518-611`; parent is the Player root (`:524`); renamed to `tornado` at `:596-600` |
| `Username` | `RectTransform` + `TextMeshPro` — the nametag | `:35-71` |
| `PlayerLeft` | scale 0.2, holds the left cone | `:3-34`; cone instance `:731-843`, local z **−1.23** at `:767`, scale 15 |
| `PlayerRight` | scale 0.2, holds the right cone | `:412-443`; cone instance `:612-724`, local z **−1.22** at `:648`, scale 15 |
| `mainFace` | plain GameObject holding `Assets/face.obj` — **the head** | `:380-395`, `m_IsActive: 1` at **line 395**; the `face` instance is `:444-517` |

Note the asymmetry, because it decides how you edit them. `mainFace` is a plain GameObject, so its
`m_IsActive` is an ordinary field you can flip in YAML at line 395. `tornado` is a **nested prefab
instance**, so unticking it records an `m_IsActive` modification inside the `PrefabInstance` block
starting at line 518. Do that one in the Editor rather than by hand.

### 2.2 The change

Keep a switch rather than deleting the concept, so turning heads back on later is one tick. In
`PlayerControls`, next to the other tunables:

```csharp
// Master switch for the remote avatar's head and body. Off: they never render, for anybody.
// A remote player is two cones and a name. In passthrough their real head and body are
// already there to look at, and a virtual copy of them is at best noise and at worst — before
// the alignment work in §4 lands — drawn somewhere they are not.
public bool ShowRemoteHeadAndBody = false;
```

Resolve the body alongside the other four in `OnNetworkSpawn` (`:45-51`):

```csharp
body = FindChild("tornado");
```

and add one function that is the only thing in the project allowed to decide:

```csharp
/// <summary>
/// Remote-only. One place decides whether the head and body render, so the prefab's
/// authored state and the runtime state cannot disagree. Called once at spawn; add
/// callers if ShowRemoteHeadAndBody ever becomes something other than a constant.
/// </summary>
private void ApplyAvatarVisibility()
{
    if (body != null)
    {
        body.gameObject.SetActive(ShowRemoteHeadAndBody);
    }
    if (face != null)
    {
        face.gameObject.SetActive(ShowRemoteHeadAndBody);
    }
}
```

Call it from the `else` side of the ownership test at `:71`:

```csharp
if (IsOwner)
{
    // You are inside your own avatar; do not render it for yourself.
    for (int i = 0; i < transform.childCount; i++)
    {
        transform.GetChild(i).gameObject.SetActive(false);
    }
}
else
{
    ApplyAvatarVisibility();
}
```

### 2.3 And in the prefab

`OnNetworkSpawn` is the earliest this code can run and it is not the first frame the object exists.
Set both inactive in `Player.prefab` too, so there is no flash:

- `mainFace` → `m_IsActive: 1` → `0` at **line 395**, or untick it in the Editor.
- `tornado` → untick in the Editor. It writes an `m_IsActive` modification into the `PrefabInstance`
  at line 518.

`PlayerControls.Update` goes on writing `face.position` and `face.rotation` into a deactivated object
every frame (`:245-249`). That is legal and free — `Transform.Find` returns inactive children and
assigning to their transform is fine — and it is what makes re-enabling a single `SetActive`.

**Do not delete the objects.** `fix2.md` §6.3 gives a reason that does not apply here (this project
resolves children by name, not index, so deleting one would not scramble the others). The reason
that *does* apply: `face` is dereferenced unguarded at `:245-249`, so a missing `mainFace` gives you
`FindChild`'s error at spawn (`:281`) followed by a `NullReferenceException` **every frame on every
remote avatar**. `body` is new and null-guarded above; `face` is not, and making it so is a bigger
change than leaving an inactive GameObject in place.

### 2.4 The nametag is now the entire avatar

With the head and body off, a remote player is two cones and a name — so §1 is no longer cosmetic.
`NameTagAboveEyes = 0.28f` was chosen to clear a **real** head seen through passthrough. That is the
case that matters here. Keep it.

Do not strip `facePos`/`faceRot` (`:31-32`, written at `:262-263`) just because the head is gone.
After §1 the nametag is driven from `facePos`, and after §9 the probe reads it. They are the only
thing that says where a remote player's head is.

---

## 3. Defect 2 — the hand cones hang off a lever arm

`fix1.md` §5.1, with this project's line numbers. Every value is the same.

| Step | Value | Source |
|---|---|---|
| Tracked pose | grip pose of `XRNode.Left/RightHand` | `StairsGame.unity:521-525` and `:919-923` — legacy `TrackedPoseDriver`, `m_Device: 1`, `m_PoseSource: 4`/`5` |
| Broadcast point | grip **+ 0.20 m along grip forward** | `PlayerControls.cs:264-265` |
| Cone parent scale | 0.2 | `Player.prefab:29`, `:438` |
| Cone offset | local z −1.23 (left) / −1.22 (right) → **−0.246 / −0.244 m** | `Player.prefab:767`, `:648` |
| Cone scale | 15, i.e. net 3 | `Player.prefab:739-752` (left), `:620-633` (right) |
| Mesh extent | `OculusHandPinchArrowBlended`, AABB centre z 0.0429 ± 0.0328 → **±0.098 m** at net scale | `Player.prefab:804-832` (left), `:690-713` (right) |

Net: the +0.20 in code and the −0.244 in the prefab nearly cancel, and what is left is a **~18 cm
lever arm along the grip's forward axis**. Rotate your wrist and the remote cone sweeps an arc; any
difference in where the runtime puts the grip pose is amplified along that arm. Left and right were
tuned separately and disagree by 1 cm of local units, which nobody has had a reason to notice.

### 3.1 The change

Broadcast the tracked pose and move the compensation into the prefab, where it is one number in the
Inspector instead of two hidden in two files. `PlayerControls.cs:264-265`:

```csharp
// Was localLeft.position + 0.2f * localLeft.forward. The visual offset now lives entirely in
// Player.prefab, so what goes on the wire is the pose the runtime actually reported and the
// remote cone's geometry is identical to the local controller model's.
lHPos.Value = localLeft.position;
rHPos.Value = localRight.position;
```

and set both cones' `m_LocalPosition.z` to **−0.22** (`Player.prefab:767` and `:648`).
`0.2 × −0.22 = −0.044 m`, which is what the two offsets net out to today, so the cones render exactly
where they render now — there is just one number left to calibrate.

Unlike the classroom project there is no second drawing point to reconcile: `Left Dot` / `Right Dot`
and `LineDrawer` are gone from this fork. The controller models `MainLeft` / `MainRight` are still in
both game scenes, though, so the local user sees a controller at the tracked pose while remote users
see a cone. Once §3 lands those two agree, which is what makes the tip-to-tip test in §10 meaningful.

`fix1.md` §5.3 (per-controller-generation grip calibration) and §5.5 (`Application.onBeforeRender`)
still apply and are still worth doing, but neither is worth starting before §4 exists and §9 has
produced numbers.

---

## 4. Colocation, from nothing — the design

Everything from here needs two headsets in one room. None of it can be verified in the Editor.

### 4.1 What has to be true

Two people at the same real table, wearing different headsets, must see the board in the same real
place and each other's cones on each other's real hands. That reduces to one requirement:

> Every headset's `XRRig` must be positioned so that a single agreed physical point in the room maps
> to the same Unity world coordinate on every headset.

A Meta **shared spatial anchor** is the agreed physical point. The rest is bookkeeping.

### 4.2 Move the rig, never the world

`CameraController2.Recenter` (`:212-234`) already establishes the convention: only this client's rig
moves, the shared virtual world never does. Keep it. Every networked value in this project —
`lHPos`, `facePos`, the avatar root, everything a game scene puts on the board — is a **world**
coordinate, and moving the world would mean rewriting all of them and re-deriving every collider.
`fix1.md` §8 and `fix2.md` §10 both say this; it is even more true here, where `Board` is a scene
object with a `BoxCollider` (`StairsGame.unity:141-161`).

### 4.3 Align every frame, not once

Do not build the one-shot version and then fix it. `fix2.md` §1 is the whole argument and it applies
unchanged; the short form is:

- An anchor's **world** pose is recomputed from the rig every frame — `OVRSpatialAnchor.UpdateTransform`
  (`:801-809`) → `TryGetPose` (`:779-796`) → `pose.ToWorldSpacePose(mainCamera)` (`OVRCommon.cs:119-134`).
  It is not an independent fact about the room.
- An anchor's **tracking-space** pose is not constant either. The runtime recentres the tracking
  origin on its own: `OVRManager.cs:2748-2750` applies a recenter whenever `AllowRecenter` is true,
  and `AllowRecenter` is explicitly serialized as **1** in all four scenes (`StairsGame.unity:1077`,
  `ChasmGame.unity:2725`, `GameScene.unity:964`, `OpeningScene.unity:3736`). Independently,
  `OVRDisplay.Update` (`:147-161`) polls `OVRPlugin.GetLocalTrackingSpaceRecenterCount()` **every
  frame on every Quest** and raises `RecenteredPose` for recenters the app never asked for. Nothing
  in this project subscribes to any of that.
- Anchors are also re-localised as the room map improves, and tracking loss/recovery can land the
  session on a different origin.

Each of those slides a one-shot alignment permanently out of the shared frame, for one client, while
everyone else is unaffected. That is the "sometimes exactly right, sometimes wrong" report `fix2.md`
opens with, and it is free to not have.

Meta's own sample does it per frame: `AlignCameraToAnchor.cs` in the SDK you already have, `Update()`
at `:33-36` and `[DefaultExecutionOrder(10)]` at `:28`. The execution order is load-bearing — it must
run *after* `OVRSpatialAnchor.Update()` (default order, `:699-705`) has refreshed the anchor's world
pose for this frame. **Adopt their cadence and their execution order; keep our maths** (§5), which
handles the `Camera Offset` in between and the near-vertical degenerate case that their
`eulerAngles.y` does not.

### 4.4 The board is on a table — this project's extra problem

The classroom anchored a room full of seats at floor level. This project anchors a **board**, and
`StairsGame.unity:228-229` puts `Board` at `(0, 0.8, 0)` scaled `(2, 0.02, 2)` — a 2 m square top,
80 cm up. `ChasmGame.unity:3345-3346` is `(−0.036, 0.81, −0.088)`, scale `(2.5, 1, 2.5)`.

So the anchor has to settle two things, not one: where the board is horizontally, and how high the
real table is. Put them in different places:

- **The anchor goes on the floor**, directly below the centre of the real table, with a yaw-only
  rotation. World y = 0 therefore stays the real floor on every headset, which keeps
  `PlayerRing.SlotPosition` (y = 0, `PlayerRing.cs:38`), `PlayerControls.cs:261` (avatar root at the
  user's feet) and the whole existing project correct.
- **The table height is a separate networked number**, measured at placement time, applied to the
  `Board` object's local y.

Placement flow, for the room owner (`PlayerControls.roomOwner`, `:33`, set from
`RelayVivox.roomOwner` at `:75`):

1. Stand at the table facing its centre.
2. Rest the right controller on the middle of the real table top.
3. Press the key (§7.2).

The code takes the controller's world position `P` and the rig's y, and derives both numbers:

```csharp
// The rig's y IS this headset's floor under floor tracking (StairsGame.unity:960), so the
// controller's height above it is the real table height, and dropping the controller point to
// that floor gives the anchor pose. Two numbers, one gesture, both measured rather than guessed.
float tableHeight  = rightHand.position.y - rig.position.y;
Vector3 anchorPos  = new Vector3(rightHand.position.x, rig.position.y, rightHand.position.z);
Quaternion anchorRot = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
```

`tableHeight` goes on the wire next to the anchor UUID; every client sets `Board`'s local y to it.
If you decide the tables are always the same and you would rather not touch the scenes, skip it —
the alignment in §5 and §6 does not depend on it. Say so in a commit message either way, because a
board floating 6 cm above a real table is the single most obvious defect in a colocated board game
and the next person will assume the alignment is broken.

### 4.5 Where the shared UUID lives

`ClassroomAnchor` published its group on `seatControl`, a `NetworkObject` sitting in the scene. That
option does not exist here: **`GlobalObjectIdHash` appears in no file under `Assets/Scenes`**, so
there are no in-scene `NetworkObject`s at all, and `updates1.md` §2.4 removed the last of them
deliberately. NGO also forbids putting a `NetworkObject` on the `NetworkManager`'s own GameObject, so
the persistent `Network Manager` object is out.

Three options, in order of preference:

1. **A spawned singleton.** New prefab `Assets/Prefabs/Room Anchor.prefab` — a `NetworkObject` plus a
   `RoomAnchor : NetworkBehaviour` — registered in `Assets/DefaultNetworkPrefabs.asset` (which today
   holds exactly one entry, `Player.prefab`, at `:17-22`) and spawned by the host when the session
   opens. **Recommended.** It survives the game switch for the same reason the players do — NGO
   migrates spawned `NetworkObject`s across a `LoadSceneMode.Single` load, which is exactly the
   mechanism `GameSelector.cs:9-12` documents — and it lives for the whole session rather than for
   the lifetime of one player object.
2. Hang the variables off the room owner's `PlayerControls`. No new prefab and no registration, but
   readers have to scan `NetworkManager.ConnectedClientsList` for `roomOwner.Value`, and the values
   die and reset if the host's player object ever respawns — which `NetworkReconnectHandler.cs:18-28`
   makes routine, since it calls `Shutdown()` then `StartClient()` in a loop.
3. An RPC with no backing variable. Do not: a client that joins late, or reconnects, never hears it.

`RoomAnchor` carries three server-written variables:

```csharp
// Server-written, everyone-readable. The group is what LoadUnboundSharedAnchorsAsync queries;
// the UUID is which anchor inside that group is the real one (§6.4); the height is §4.4.
public NetworkVariable<FixedString64Bytes> anchorGroup =
    new NetworkVariable<FixedString64Bytes>("", NetworkVariableReadPermission.Everyone,
                                                NetworkVariableWritePermission.Server);
public NetworkVariable<FixedString64Bytes> anchorUuid  = /* same */;
public NetworkVariable<float> tableHeight =
    new NetworkVariable<float>(0.8f, NetworkVariableReadPermission.Everyone,
                                     NetworkVariableWritePermission.Server);
```

`FixedString64Bytes` holds a 36-character GUID string with room to spare. Publish both in the same
server call, so no client can ever see a group without a UUID.

---

## 5. `CameraController2.AlignRigToAnchor`

This function does not exist in this project. Here it is, with the derivation, because you will need
to convince yourself of it before you trust a per-frame version of it.

### 5.1 The maths

Write the rig-to-world map as `W = XRRig ∘ E`, where `E` is everything between `XRRig` and the
tracking origin: `Camera Offset` (`StairsGame.unity:664-687`, authored at y 1.36144 and zeroed at
runtime by `XROrigin.MoveOffsetHeight` — `XROrigin.cs:229-232`, Floor mode → 0), plus the camera's
driven local pose, plus the OVRPlugin head pose the SDK divides back out. The anchor's tracking-space
position is `x`, so the pose the SDK reports is `A = XRRig ∘ E · x`.

Build `D`, the yaw-only inverse of `A`, and assign `XRRig' = D ∘ XRRig`. Then

```
new anchor world pose = XRRig' ∘ E · x = D ∘ XRRig ∘ E · x = D · A = 0
```

The anchor lands exactly on the world origin, in **all three axes**, regardless of where the rig was
and regardless of what sits in `E`. It is a fixed point, so running it again is a no-op — which is
the property §6 depends on.

### 5.2 The code

On `CameraController2`, next to the other tunables at `:11-13`:

```csharp
// A shared anchor placed on the floor under the table should localize within a few centimetres
// of this headset's own floor. Anything past this is a tracking failure, not a floor-calibration
// difference. Generous on purpose.
public float MaxAnchorHeightDisagreement = 0.25f;

// Seconds between repeats of the anchor-height warning. At frame rate this is a log flood, and
// the interesting events are "it started" and "it stopped", not the sixty in between.
public float HeightWarningIntervalSeconds = 5f;
private float nextHeightWarningAt;

// Static because everything that cares — PlayerControls, the probe, the locomotion gate — needs
// it without holding a reference to the rig, and the rig is destroyed and rebuilt on every game
// switch (PlayerControls.cs:171-177).
public static bool LocalIsAligned { get; private set; }
public static event System.Action LocalAlignmentChanged;
```

```csharp
/// <summary>
/// Move this rig so the shared anchor lands on the world origin, yaw-only. Called every frame
/// by BoardAnchor while bound — not once — because the anchor's tracking-space pose is not a
/// constant (§4.3). Idempotent by construction: the anchor is driven onto a fixed point, so a
/// frame in which nothing moved writes back the pose that is already there.
/// </summary>
public void AlignRigToAnchor(Transform anchor)
{
    if (anchor == null)
    {
        return;
    }

    // Yaw only. A full-rotation alignment would tip the board off the real floor, and pitch and
    // roll from an anchor are noise: the runtime gravity-aligns them and what is left is error.
    Vector3 flatForward = anchor.forward;
    flatForward.y = 0f;
    if (flatForward.sqrMagnitude < 1e-6f)
    {
        // The anchor is pointing at the ceiling. Should be impossible — BoardAnchor creates it
        // with a yaw-only rotation (§4.4) — but LookRotation returns garbage rather than failing,
        // and garbage here rotates the whole board game.
        flatForward = anchor.up;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 1e-6f)
        {
            return;
        }
    }

    Quaternion deltaRot = Quaternion.Inverse(
        Quaternion.LookRotation(flatForward.normalized, Vector3.up));
    Vector3 deltaPos = -(deltaRot * anchor.position);
    Vector3 newPos = deltaRot * transform.position + deltaPos;

    // Take the anchor's HEIGHT as well as its horizontal position. Each headset puts y = 0 on
    // its OWN estimate of the floor — the Quest 3 derives it from the depth sensor, the Quest 2
    // from wherever the user pointed during boundary setup — and those estimates routinely
    // differ by a few centimetres. The anchor is the only object in the system that knows the
    // real answer. deltaRot is yaw-only, so this is a pure vertical correction and cannot lean.
    //
    // The quantity below is invariant to where the rig is: anchor.position.y - transform.position.y
    // reduces to the anchor's height above the tracking origin, i.e. above this headset's floor.
    // That is why it is a meaningful guard and not a moving target.
    float anchorAboveMyFloor = anchor.position.y - transform.position.y;
    if (Mathf.Abs(anchorAboveMyFloor) > MaxAnchorHeightDisagreement)
    {
        // Reject the frame rather than half-applying the correction. Under a per-frame loop the
        // previous frame's pose is strictly better than any fallback, and a board that is stale
        // by one frame is invisible where a board snapping to the floor and back is not.
        //
        // NOTE this is the opposite of fix1.md §4.2, which forced newPos.y = 0 because it had
        // exactly one chance to produce a pose. See fix2.md §2.2.
        WarnHeightDisagreement(anchorAboveMyFloor);
        return;
    }

    transform.SetPositionAndRotation(newPos, deltaRot * transform.rotation);
    SetAligned(true);
}

private void WarnHeightDisagreement(float anchorAboveMyFloor)
{
    if (Time.realtimeSinceStartup < nextHeightWarningAt)
    {
        return;
    }
    nextHeightWarningAt = Time.realtimeSinceStartup + HeightWarningIntervalSeconds;

    Debug.LogWarning("AlignRigToAnchor: the board anchor localized " +
                     anchorAboveMyFloor.ToString("F2") + " m from this headset's floor, past " +
                     "MaxAnchorHeightDisagreement. Holding the previous alignment. Re-run Space " +
                     "Setup on this headset if it keeps happening.");
}

public static void SetAligned(bool value)
{
    if (LocalIsAligned == value)
    {
        return;                     // idempotent: this runs sixty times a second
    }
    LocalIsAligned = value;
    if (LocalAlignmentChanged != null)
    {
        LocalAlignmentChanged();
    }
}
```

### 5.3 The startup race the guard will catch

`XROrigin.MoveOffsetHeight` returns immediately outside play mode (`XROrigin.cs:226-227`) and only
zeroes `Camera Offset` once the input subsystem reports Floor mode. Any alignment attempted before
that lands is off by the serialized 1.36144 (`StairsGame.unity:679`), and the height guard will
reject those frames — correctly — until it resolves. If you see the warning exactly once at startup
and never again, that is what happened and nothing is wrong.

The probe (§9) makes this unmistakable: a standing adult reading `head.y ≈ 2.7` or `≈ 1.36` is in
this state, and every other number is meaningless until it clears.

---

## 6. `BoardAnchor.cs` — place, share, load, and hold

One new file, `Assets/Scripts/BoardAnchor.cs`. Sketched rather than written out in full, because the
async plumbing is ordinary and the decisions are what matter.

```csharp
// Must run AFTER OVRSpatialAnchor.Update() (default order, OVRSpatialAnchor.cs:699-705), which
// is what refreshes boundAnchor.transform's world pose for this frame from the runtime's
// tracking-space pose. Reading it at default order gets last frame's rig baked in. Same value
// and same reason as Meta's own AlignCameraToAnchor.cs:28.
[DefaultExecutionOrder(10)]
public class BoardAnchor : MonoBehaviour
```

### 6.1 Hold the alignment, every frame

```csharp
void Update()
{
    HoldAlignment();
    ReadPlacementInput();
}

/// <summary>
/// Re-derive the rig pose from the anchor. Every frame while bound, not once at adoption.
/// Cheap: the TryLocateSpace call is already being made by OVRSpatialAnchor.Update() whether we
/// read the result or not, so this costs one SetPositionAndRotation.
/// </summary>
private void HoldAlignment()
{
    if (boundAnchor == null)
    {
        return;
    }

    // Do not align to an anchor the runtime cannot currently locate. UpdateTransform only writes
    // the transform when it got a pose (OVRSpatialAnchor.cs:805), so an untracked anchor leaves a
    // stale one in place — and for a freshly created GameObject that stale pose is the world
    // origin with identity rotation, which AlignRigToAnchor would happily accept as "already
    // aligned". Holding the last good rig pose is right: the user has not moved, the tracker has
    // stopped reporting.
    if (!boundAnchor.IsTracked)
    {
        NoteUntracked();
        return;
    }

    ClearUntracked();

    CameraController2 rig = Rig;          // re-resolved after every scene load, see §8
    if (rig != null)
    {
        rig.AlignRigToAnchor(boundAnchor.transform);
    }
}
```

`NoteUntracked` / `ClearUntracked` are the hysteresis from `fix2.md` §2.1 — silent for a one-frame
dropout, and after `UntrackedWarningSeconds` (2 s) a message through `DebugLog`. Copy it as written.

**Never report alignment you did not verify** (`fix2.md` §3). `IsTracked`
(`OVRSpatialAnchor.cs:180`, set in `UpdateTransform` at `:804`) is the gate, and it must also guard
the moment of adoption: after `BindTo`, if the anchor is not yet tracked, log it and return —
`HoldAlignment` will pick it up on the frame it becomes tracked. That is only safe *because*
alignment is continuous, which is why §6 cannot be built as a one-shot and patched later.

### 6.2 Creating and sharing (room owner only)

```csharp
GameObject go = new GameObject("Board Anchor");
go.transform.SetPositionAndRotation(anchorPos, anchorRot);   // §4.4 — pose BEFORE the component
OVRSpatialAnchor anchor = go.AddComponent<OVRSpatialAnchor>();

if (!await anchor.WhenLocalizedAsync())  { /* report, bail */ }

Guid group = Guid.NewGuid();
var saved  = await anchor.SaveAnchorAsync();                          // :607
if (!saved.Success) { /* report, bail */ }

var shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, group);   // :446
if (!shared.Success) { /* report, bail */ }

roomAnchor.PublishServerRpc(group.ToString(), anchor.Uuid.ToString(), tableHeight);
```

Set the transform **before** adding the component, not after. `OVRSpatialAnchor.Start()`
(`:685-697`) calls `CreateSpatialAnchor()` (`:757-777`), which captures `GetTrackingSpacePose()` at
that moment; doing it in this order closes the window entirely rather than narrowing it, which is
what `fix2.md` §4.3 settles for.

Order matters: **create → localize → save → share**. The SDK is explicit that anchors "must exist, be
localized, and be saved prior to sharing" (`OVRSpatialAnchor.cs:419`). Skipping the save is the
most common way for `ShareAsync` to fail with something unhelpful.

### 6.3 Loading (everyone else)

```csharp
List<OVRSpatialAnchor.UnboundAnchor> unbound = new List<OVRSpatialAnchor.UnboundAnchor>();

// The three-argument overload (OVRSpatialAnchor.cs:1344-1347) filters by UUID inside the query.
// Ask for the one anchor the room owner published rather than loading the group and picking —
// a client on the wrong anchor looks like working software, and looking like working software
// is worse than failing, because a client that fails to align still gets a ring slot and
// behaves correctly as a remote player.
var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
    group, new[] { publishedUuid }, unbound);

if (!result.Success || unbound.Count == 0) { /* report, ShowStatus, return */ }
if (!await unbound[0].LocalizeAsync(LocalizeTimeoutSeconds)) { /* retry with backoff */ }

GameObject go = new GameObject("Board Anchor");
unbound[0].BindTo(go.AddComponent<OVRSpatialAnchor>());     // :1068
AdoptAnchor(go.GetComponent<OVRSpatialAnchor>(), group);
```

This is strictly better than `fix2.md` §4.2, which loads the whole group and then searches it — the
three-argument overload did not exist when that was written. It removes `unbound[0]` as a guess.

### 6.4 Latch requests, never drop them

`fix2.md` §4.1 is the bug to not write in the first place. Any request that arrives while a load is
in flight — the room owner re-placing the board, a reconnect re-delivering the group, a player
pressing the key — must be **latched into a pending field and retried**, never dropped on a `busy`
flag. Dropping it leaves that one client bound to the previous board's anchor with nothing to tell
it otherwise, permanently, and that is precisely "different headsets on different points in the
room". A load can be in flight for a minute with retries and an 8-second localize timeout, which is
a wide window to drop things in.

Guard the retry with `pending != current` so a repeated press during a successful load does not
restart it.

---

## 7. What alignment has to switch off

`CameraController2.Update` (`:65-114`) contains four things that move the rig. A colocated player
walks with their legs; every one of these is either meaningless or actively wrong for them.

| Lines | What | While aligned |
|---|---|---|
| `:73-89` | snap/smooth turn on the left joystick X | off |
| `:92-96` | translate along `LeftHand.forward` | off |
| `:100-103` | `Recenter()` on the left joystick button | off — it is the one control that would fight alignment on purpose |
| `:106-113` | M/N keyboard tilt | off — it is the only thing in the project that puts pitch into the rig, and §5.2's height guard assumes yaw-only |

```csharp
void Update()
{
    if (Inputs == null)
    {
        return;
    }

    // A colocated player moves by walking. Locomotion, recentring and the debug tilt all fight
    // the per-frame alignment (which wins, since BoardAnchor runs at execution order 10 and this
    // runs at 0) — so leaving them on would not break the frame, it would just mean controls that
    // silently do nothing. Off is honest.
    if (LocalIsAligned)
    {
        return;
    }
    ...
```

### 7.1 The ring slot

`PlayerRing` exists to spread players around a virtual board when they are in different rooms. A
colocated player is standing where they are standing; moving them to a slot is exactly the
"involuntary rig move" `PlayerRing.cs:56-61` warns about.

`CameraController2.PlaceAtRingSlot` (`:136-147`) already refuses in the lobby. Add the same shape of
guard for alignment:

```csharp
public void PlaceAtRingSlot(int slot)
{
    if (!GameRoutes.IsGameScene(gameObject.scene.name))
    {
        return;
    }

    // A colocated player's position is a fact about the real room, not something to assign.
    if (LocalIsAligned)
    {
        ringSlot = slot;      // remembered, so leaving the room re-places them correctly
        return;
    }

    ringSlot = slot;
    ApplyRingAnchor();
}
```

While you are there: have the server stop holding a ring slot for a colocated player, so a 12-slot
ring (`PlayerRing.cs:23`, matched to the 12-player Relay allocation) is not consumed by people who
are not standing on it. `PlayerControls.OccupiedSlots` (`:118-148`) already skips any player whose
slot is `< 0`, so releasing is a matter of setting `spawnSlot.Value = -1` on the server when
`colocated` goes true, and re-picking when it goes false. Minor, and safe to defer.

### 7.2 Placing the board

You need one control for "the board goes here". Two options:

- **A menu key.** `MenuControl.HandleKey` (`:78-106`) dispatches by `keyInfo.keyName`, and
  `Menu1.prefab` already has a `Row1` with `Stairs` (`:968-1080`) and `Chasms` (`:807-919`). Add a
  `Place Board` key and a case. This is the documented way to add a control to this project
  (`MenuControl.cs:8-11`), and it is the only way that survives someone re-skinning the menu.
- **A button.** `InputReader` exposes `ButtonA`/`ButtonB`/`ButtonY` and both grips with edge
  detection (`InputReader.cs:20-66`), and **none of them is read anywhere in `Assets/Scripts`** —
  only `ButtonX` (menu, `MenuControl.cs:41`), the left joystick button (recentre,
  `CameraController2.cs:100`) and the right joystick button (clear the log, `DebugLog.cs:63`) are
  taken. `ButtonA` is free.

Use the menu key for placing (it is rare, deliberate, and room-owner-only) and `ButtonA` for
re-align (it is a recovery action a user needs quickly, in the middle of a game, without a menu in
their face). Gate placement on `roomOwner` (`PlayerControls.cs:33`, `:331-334`) so two people cannot
put the board in two places.

---

## 8. Surviving a game switch

This is the piece with no equivalent in `fix1.md` or `fix2.md`, and the easiest to get wrong.

`GameSelector.LoadGameScene` (`:50-84`) does `LoadSceneMode.Single` through Netcode's scene manager.
`PlayerControls.BindToScene`'s comment (`:171-177`) spells out the consequence: **the rig, the
camera, the hands and the Menu Manager are all destroyed and rebuilt**, and anything holding a
reference to them holds a destroyed object.

Three requirements fall out:

1. **`BoardAnchor` must not live in a game scene.** Put it on the `Network Manager` object, which is
   `DontDestroyOnLoad` via `PersistentObject`, or create it at runtime and `DontDestroyOnLoad` it.
   The `Board Anchor` GameObject carrying the `OVRSpatialAnchor` must persist too — destroying it
   means re-loading and re-localising the anchor on every game switch, which is seconds of a board
   in the wrong place every time somebody changes game.
2. **`BoardAnchor` must re-resolve the rig.** Cache it, null-check it, and re-find it on
   `SceneManager.activeSceneChanged` — the same pattern `PlayerControls.BindToScene` (`:178-208`)
   and `CameraController2.AdoptLocalPlayerSlot` (`:162-178`) already use for the same reason. The
   `HoldAlignment` early-out on a null rig means a frame or two of no alignment during the load,
   which is invisible.
3. **The new rig starts at its authored transform** — `(0, 0, −2)` in `StairsGame.unity:971` — and
   `CameraController2.Start` (`:29-32`) captures that as its recentre anchor. Per-frame alignment
   fixes it on the first frame after the rig appears, which is the third argument for building §6
   as a loop rather than an event.

`LocalIsAligned` being `static` (§5.2) is deliberate for the same reason: it has to outlive the rig
that set it.

---

## 9. Instrument it before you change anything

You cannot tell §4–§8 working from lucky. `DebugLog` (`:44-70`) already prints to an in-headset box
on the `Debugger` object under `XRRig` (`StairsGame.unity:1546`, `:1564`, local `(0, 0, 1)`), cleared
with the **right** joystick button, so a probe needs no new UI and no `adb logcat`.

New file `Assets/Scripts/ColocationProbe.cs`, on `XRRig`. Take `fix1.md` §3.4 as the base and add
`fix2.md` §5's recenter counter, with this project's names:

```csharp
// Every system-initiated recenter of the tracking origin. OVRDisplay polls
// GetLocalTrackingSpaceRecenterCount() every frame on Quest and raises this
// (OVRDisplay.cs:147-161), so it catches the ones the app never asked for — exactly the ones
// that slide a one-shot alignment out of the shared frame.
void OnEnable()
{
    if (OVRManager.display != null) OVRManager.display.RecenteredPose += OnRecentered;
}
```

and, once a second:

```
probe rig.y=… head.y=… rH.y=… aligned=… | anchored=… tracked=… uuid=… recenters=…
      | <name> head.y=… rH.y=… slot=…
```

Read it like this:

| Reading | Meaning |
|---|---|
| `recenters` ticks and the cones go wrong at the same moment | §4.3 confirmed. Take this baseline **before** §6 lands. |
| `recenters` ticks and nothing moves | §6 is working. This is the acceptance test. |
| `tracked=False` for more than a moment | That client is not really in the room, or the room is badly mapped. |
| `uuid` differs between two headsets | They are on different anchors (§6.3, §6.4). |
| `head.y ≈ 1.36`, or `≈ 2.7`, on a standing adult | `Camera Offset` was never zeroed (§5.3). Every other number is meaningless until this clears. |
| Two headsets on the same table read different `rig.y` | The floor-calibration disagreement §5.2 exists to absorb. Measure it before and after. |

`OVRManager.display` is null in the Editor without a headset, so null-guard both the subscribe and
the unsubscribe. Delete the component when the numbers are known — the debug box holds ten lines
(`DebugLog.cs:10`) and this logs forever.

**Two things to rule out first, because they are free.** Both headsets are running the same build —
a stale APK on one makes every number here meaningless. And both actually got floor tracking: a
standing adult reading `head.y` of exactly 1.36 is in the fallback, not in the shared frame.

---

## 10. Project settings you have to change

Anchors are off in this project. All of these are required and none of them is code.

**`Assets/Oculus/OculusProjectConfig.asset`** — currently:

```yaml
anchorSupport: 0            # :19  -> 1  (AnchorSupport.Enabled)
sharedAnchorSupport: 0      # :20  -> 1  (FeatureSupport.Supported)
colocationSessionSupport: 0 # :26  -> leave 0, see below
```

`OVRManifestPreprocessor.cs` maps these to manifest permissions: `anchorSupport` enabled →
`com.oculus.permission.USE_ANCHOR_API` (`:759-771`), `sharedAnchorSupport` non-`None` →
`com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA` (`:773-783`). Leave `colocationSessionSupport`
off: it exists for `OVRColocationSession`'s BLE advertise/discover flow, and this project does not
need it — the group UUID rides on the Relay session that already exists (§4.5). Turn it on only if
you later want headsets to find each other without a room code.

**`Assets/Plugins/Android/AndroidManifest.xml`** — this project maintains the manifest by hand
(`MRTemplate.md` §1.5 point 3 warns that Meta's Manifest Tool regenerates it and drops the
passthrough tag). Add both permissions next to the existing ones at `:9-14`:

```xml
<!-- Meta spatial anchors. Without USE_ANCHOR_API every anchor call fails with
     Failure_SpacePermissionInsufficient; IMPORT_EXPORT_IOT_MAP_DATA is the one sharing needs. -->
<uses-permission android:name="com.oculus.permission.USE_ANCHOR_API" />
<uses-permission android:name="com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA" />
```

**`Assets/Editor/MRPassthroughSetup.cs`** — extend it to write the two `OVRProjectConfig` fields.
That file exists precisely because `OVRProjectConfig` is a ScriptableObject in the package folder
that cannot be edited as text (`:4-17`), and adding two more fields there is three lines beside the
`insightPassthroughSupport` block at `:50-54`. Bump `AppliedKey` (`:22`) to `.v2` so it re-runs on
machines that already have `.v1` in their `SessionState`.

**`Assets/DefaultNetworkPrefabs.asset`** — add `Room Anchor.prefab` next to the single existing
entry (`:17-22`, `Player.prefab`, guid `9243d030…`). A `NetworkObject` that is not in this list fails
to spawn with a message that does not say so.

**On every headset, once: OS Settings > Privacy and Safety > Device Permissions > Share Point Cloud
Data.** With it off, `ShareAsync` returns `FailureCloudStorageDisabled`
(`OVRAnchor.cs:460-471`); the OS may prompt for it once per app launch, and if the user declines,
sharing fails for that session with no other signal. This is the single most likely reason a
correctly written §6 does nothing on a new headset.

---

## 11. Test plan

Two headsets in one room, and they should be a **Quest 2 and a Quest 3** — a matched pair hides the
floor-calibration disagreement §5.2 exists to absorb, and you want to know it is handled.

**Baseline first**, with the §9 probe, before changing anything: both users standing still, both
reading `rig.y`, `head.y`, `uuid`, `recenters`, and where the other person's cones sit relative to
their real controllers. Without those numbers you cannot tell a fix from a coincidence.

1. **Nametag, standing.** (§1, no anchors needed.) Each tag floats clear above the other person's
   head with no overlap onto the face. Repeat at two noticeably different user heights — that is the
   case the old code could not represent.
2. **Nametag, kneeling.** Both users crouch to look under the board. Tags follow the heads down, not
   left floating at 1.43 m. This is the case a new prefab constant would break.
3. **Head and body.** (§2.) Never visible, on any client, owner or remote, aligned or not, including
   the first frame after a player joins. Tick `ShowRemoteHeadAndBody` on one client, confirm the
   body and head come back, untick it.
4. **Cones tip to tip.** (§3, §6.) Both users touch right controllers. Each sees the other's cone
   meeting their own from both viewpoints. Then each rotates their wrist through full range — the
   cone stays attached rather than swinging through an arc.
5. **Forced recenter.** (§6, the single test that proves the whole design.) User A long-presses the
   Meta button; `recenters` ticks on A's probe. B's view of A must not move. Without §6's per-frame
   loop it jumps and stays wrong.
6. **Headset off and on.** Same expectation. This is the one that happens by accident in a real game.
7. **The board is on the table.** (§4.4.) Both users see the virtual board coplanar with the real
   table top, from both sides, and a piece placed at the board's edge is at the table's edge for
   both. Force a recenter on one of them and check it is still true.
8. **Same anchor.** Both probes show the same `uuid`. Then the room owner re-places the board while
   the other client is mid-load (§6.4) — both must end up on the **new** uuid, not one on each.
9. **Untracked anchor.** Cover the cameras or step into an unmapped corridor. `tracked=False`, the
   board holds its last pose rather than snapping, the status appears after
   `UntrackedWarningSeconds`, and it re-acquires with no user action when you walk back.
10. **Game switch.** (§8.) Switch `Stairs` → `Chasms` and back with both users aligned. Alignment,
    cones, nametags and `uuid` all survive; nobody is teleported to a ring slot; the board is on the
    same real table in the new game.
11. **Reconnect.** Wifi off and on. `NetworkReconnectHandler` (`:9-28`) shuts down and restarts the
    client, so the player object respawns and `spawnSlot` is reassigned. Alignment must survive and
    `uuid` must be unchanged — this is where option 2 in §4.5 loses.
12. **A player in a different room.** They still get a ring slot, still have locomotion and the
    recentre button, see two cones and a name per person, and read `anchored=False`. Nothing about
    §4–§8 may break the non-colocated path — it is the only one the Editor can run.
13. **Editor still runs.** Everything anchor-related must no-op with no HMD: `BoardAnchor` gates on
    an availability check, `HoldAlignment` early-returns on a null `boundAnchor`, and the probe's
    `OVRManager.display` subscription is null-guarded.

---

## 12. Files touched

| File | Change | Section |
|---|---|---|
| `Assets/Scripts/PlayerControls.cs` | Nametag driven from `facePos`; `FaceBelowEyes` / `NameTagAboveEyes` | §1.2 |
| `Assets/Prefabs/Player.prefab` | `Username` `m_AnchoredPosition.y` 1.43 → 0 (`:69`) | §1.2 |
| `Assets/Scripts/PlayerControls.cs` | `ShowRemoteHeadAndBody`; `body` child; `ApplyAvatarVisibility` | §2.2 |
| `Assets/Prefabs/Player.prefab` | `mainFace` inactive (`:395`); `tornado` instance inactive (`:518`) | §2.3 |
| `Assets/Scripts/PlayerControls.cs` | Drop the 0.2 m lever arm (`:264-265`) | §3.1 |
| `Assets/Prefabs/Player.prefab` | Both cones `m_LocalPosition.z` −1.22/−1.23 → −0.22 (`:648`, `:767`) | §3.1 |
| `Assets/Scripts/CameraController2.cs` | `AlignRigToAnchor`, `LocalIsAligned`, height guard, rate-limited warning | §5.2 |
| `Assets/Scripts/CameraController2.cs` | Locomotion / recentre / tilt / ring slot gated on `LocalIsAligned` | §7 |
| `Assets/Scripts/BoardAnchor.cs` | **New.** Place, share, load, bind, hold every frame | §6, §8 |
| `Assets/Scripts/RoomAnchor.cs` | **New.** `anchorGroup` / `anchorUuid` / `tableHeight` | §4.5 |
| `Assets/Prefabs/Room Anchor.prefab` | **New.** `NetworkObject` + `RoomAnchor` | §4.5 |
| `Assets/DefaultNetworkPrefabs.asset` | Register `Room Anchor.prefab` | §10 |
| `Assets/Scripts/MenuControl.cs`, `Assets/Prefabs/Menu1.prefab` | `Place Board` key and case | §7.2 |
| `Assets/Scripts/ColocationProbe.cs` | **New**, temporary — delete when the numbers are known | §9 |
| `Assets/Oculus/OculusProjectConfig.asset` | `anchorSupport` `:19`, `sharedAnchorSupport` `:20` | §10 |
| `Assets/Plugins/Android/AndroidManifest.xml` | Two `uses-permission` entries | §10 |
| `Assets/Editor/MRPassthroughSetup.cs` | Write both config fields; bump `AppliedKey` | §10 |
| `Assets/Scenes/StairsGame.unity`, `ChasmGame.unity` | *Optional:* `Board` local y driven from `tableHeight` | §4.4 |

Land §1, §2 and §3 as three separate commits before starting §4 — they are certain, they are cheap,
and if a regression appears you want one suspect. §4–§8 is one feature and should land as one
reviewable change, with the probe (§9) already in the build.

---

## 13. Things not to do

- **Do not build the one-shot alignment first.** §4.3. The whole of `fix2.md` is the cost of doing
  that, paid once already on the project this one was forked from. Per-frame is cheaper than it
  sounds: `OVRSpatialAnchor.Update()` is already making the `TryLocateSpace` call whether you read
  the result or not.
- **Do not add a timer that re-aligns every N seconds.** It is the same one-shot with more chances
  to be wrong, and it makes the board lurch on a schedule.
- **Do not turn off `OVRManager.AllowRecenter` to stop the recenters.** It suppresses the *app*
  applying them; it does not stop the runtime moving the tracking origin (`OVRDisplay.cs:147-161`
  fires regardless), and it breaks the user's ability to recentre deliberately. §6 makes recenters
  harmless, which is strictly better than making them invisible.
- **Do not move the world instead of the rig**, and **do not take pitch or roll from the anchor.**
  §4.2 and §5.2.
- **Do not fall back to "whatever anchor came back" when the published UUID is missing.** §6.3. A
  client on the wrong anchor looks like working software; a client that failed to align gets a ring
  slot and behaves correctly as a remote player.
- **Do not delete `mainFace` or `tornado` from the prefab.** §2.3 — `face` is dereferenced unguarded
  every frame.
- **Do not "fix" the nametag by changing 1.43 to a bigger number.** §1.3.
- **Do not leave the ring placement running for an aligned player.** §7.1 — teleporting somebody
  mid-game because a third player joined is nauseating in passthrough, and `PlayerRing.cs:56-61`
  already says so about a milder case.
- **Do not let `BoardAnchor` live in a game scene.** §8. It will be destroyed on the next
  `Stairs` → `Chasms` press and the board will silently stop being anchored.
