# anchoringUpdate.md — one anchor for the room, and a board you move with both hands

`fixAnchoring.md` planned four pieces of work. Three of them landed: the nametag is driven from the
head pose, the head and body never render, and the hand cones no longer hang off a lever arm. The
fourth — colocation — was never started, and that is the whole of what "the anchoring isn't working"
means. This document specifies the remaining work and adds the two-handed world grab.

Two of `fixAnchoring.md`'s decisions are **reversed** here, both because the requirements changed
under them, and both are called out where they appear:

- §4.4's "anchor on the floor under the centre of the table, plus a networked `tableHeight`" — the
  two-handed grab makes the anchor's placement irrelevant and the table height a thing the user sets
  by hand. See §2.1.
- §3.1's cone offset of −0.22, chosen to reproduce the look the project already had — requirement 3
  asks for the cone to be *on* the controller instead. See §5.2.2.

**Audience:** the developer doing the work. Every claim cites a real file, line or serialized value
in this repo as of 2026-08-15, branch `mr-passthrough`, Unity 6000.5.4f1, URP 17.5.0, Meta XR Core
SDK 205.0.0, Oculus XR Plugin 4.5.4, Netcode for GameObjects 2.13.0, Vivox 16.10.0. Read
`fixAnchoring.md` §4–§8 first; this document replaces those sections rather than repeating them, and
says so where it diverges.

---

## 0. The four requirements, and where each one stands

| # | What you asked for | Status | Where |
|---|---|---|---|
| 1 | Every player's position derived from a **common anchoring point in the room** | **Not built.** `OVRSpatialAnchor` appears nowhere under `Assets/`; `anchorSupport` and `sharedAnchorSupport` are both `0` (`OculusProjectConfig.asset:19-20`). What exists today is the opposite of anchoring — see §1. | §2 |
| 2 | Head and body of players **inactive** | **Already true.** `ShowRemoteHeadAndBody = false` (`PlayerControls.cs:40`), `ApplyAvatarVisibility` (`:129-139`), `mainFace` `m_IsActive: 0` (`Player.prefab:398`), `tornado` overridden to `0` (`Player.prefab:606-607`). Nothing to build; §5.1 is how to prove it and what would silently undo it. | §5.1 |
| 3 | Cones **exactly where the controllers are** | **Partly.** The lever arm is gone and the prefab carries a single offset (`Player.prefab:656`, `:775`). Three things still put the cone somewhere else: the missing shared frame, a residual +8.5 cm, and a rotation channel that loses roll and goes ill-conditioned when you point at the board. | §5.2 |
| 4 | Username **above the head** | **Already true.** `usernameTransform.position = facePos + (0, FaceBelowEyes + NameTagAboveEyes, 0)` (`PlayerControls.cs:281-282`), prefab constant zeroed (`Player.prefab:69`). It renders in the wrong *room* today only because of requirement 1. | §5.3 |
| 5 | **Both grips** → move and resize everything in the room except the players | **Not built.** Nothing in `Assets/Scripts` reads either grip. The fork's ancestor had a version of this and its mistakes are worth not repeating. | §4 |

**The shape of the work.** Requirements 2 and 4 are done. Requirement 1 is one subsystem —
a shared spatial anchor plus per-frame rig alignment. Requirements 3 and 5 both fall out of it
almost for free once the frame is right, plus one number and one new component.

**Estimated effort.** §2 is two to three days, most of it two-headset testing. §3 is an afternoon.
§4 is a day. §5.2 is an hour once §2 works. §6 is half an hour and blocks everything.

---

## 1. Why the anchoring is not working

There is no anchoring. `PlayerRing` + `CameraController2.Recenter` is not anchoring, and it is worth
being precise about the difference, because the code reads like it might be.

**What the current system does.** The server hands each player a slot on a 12-point circle of radius
2 m around the world origin (`PlayerRing.cs:23`, `:29`, `PlayerControls.cs:80`).
`CameraController2.PlaceAtRingSlot` (`:136-147`) makes that slot this client's recentre anchor, and
`Recenter` (`:212-234`) moves the rig so the user's **current real standing position** lands on it.

That is exactly right for players in different rooms. It is exactly wrong for players at the same
table:

> Two people stand 1 m apart at one real table. The server gives A slot 0 and B slot 6 — opposite
> sides of the ring. `Recenter` puts each of their real bodies on their own slot. Their virtual
> positions are now **4 m apart** and their real positions are 1 m apart. Every cone, every nametag
> and every hand is off by the difference, and the difference is different for each pair of players.

Three more mechanisms make it worse, and all three are already in the build:

| Mechanism | Evidence | Effect |
|---|---|---|
| The runtime recentres the tracking origin on its own | `OVRManager.cs:2748` applies one whenever `AllowRecenter` is true, and it is serialized `1` in both game scenes (`StairsGame.unity:1077`, `ChasmGame.unity:5952`). Independently, `OVRDisplay.Update` polls `GetLocalTrackingSpaceRecenterCount()` **every frame on every Quest** (`OVRDisplay.cs:150-159`) and raises `RecenteredPose` for recenters the app never asked for. Nothing subscribes to either. | One client's whole world slides. Nobody else's does. |
| The recentre button | `CameraController2.cs:100-103` — a left joystick **click**, which is easy to hit by accident. | Deliberately moves one client's rig out of whatever frame it was in. |
| The rig is rebuilt on every game switch | `GameSelector.LoadGameScene` (`:50-84`) is `LoadSceneMode.Single`; `PlayerControls.BindToScene` (`:221-251`) documents that the rig, camera, hands and Menu Manager are all destroyed. The new rig starts at its authored transform — `(0, 0, −2)` in `StairsGame.unity:971`, `(0, 0, −10)` in `ChasmGame.unity:5837`. | Every `Stairs` ↔ `Chasms` press re-rolls the offset. |

So: no shared physical reference exists, and three separate things move the per-client one. That is
the report.

**What is *not* wrong.** World space is already a shared *virtual* frame — the board sits at the same
authored place on every client (`StairsGame.unity:219`, `ChasmGame.unity:7700`),
`ClientNetworkTransform` on the Player root is world-space
(`Player.prefab:368`, `InLocalSpace: 0`), and every networked value in `PlayerControls` is a world
coordinate. Nothing about the replication needs rewriting. The job is to make world space a shared
*physical* frame as well, which is one operation on one transform per client.

---

## 2. Requirement 1 — the common anchoring point

### 2.1 The design, in one page

Three frames, and keeping them separate is the whole design:

| Frame | What it is | What lives in it | Who moves it |
|---|---|---|---|
| **Tracking space** | one headset's own origin, wherever the runtime last put it | nothing of ours | the Meta runtime, unasked |
| **Room frame** = Unity **world space** | made identical on every headset by moving *this client's* `XRRig` every frame so the shared anchor lands on the world origin | every **player** value — `lHPos`, `rHPos`, `facePos`, the nametag, the avatar root — at 1:1 metric scale | `CameraController2.AlignRigToAnchor`, per frame, per client, locally |
| **Content frame** = `World Root` | a child of the room frame carrying a **networked** position, yaw and uniform scale | every **game** object — `Board` and everything under it | the two-grip gesture (§4), shared |

Requirement 5's "apart from other players" is then structural rather than a filter: players are
simply not under `World Root`. And requirement 3 survives resizing for the same reason — scaling the
*rig* would drag the user's tracked hands away from their real hands, which in passthrough is the
one thing you can actually see; scaling `World Root` leaves the hands untouched.

**One consequence worth stating up front, because it deletes work.** `fixAnchoring.md` §4.4 required
the anchor to be placed on the floor under the centre of the real table, and shipped a separate
networked `tableHeight` to lift the `Board` onto it. **Drop both.** With a shared two-handed grab, the
board is put on the table by the gesture, and that placement is already networked. The anchor no
longer has to be anywhere in particular — it only has to be *the same somewhere* for everybody. Its
yaw does not matter either, because the board's yaw is set by the gesture afterwards. So the
placement gesture becomes: *stand anywhere, press the key.*

### 2.2 Move the rig, never the world

`CameraController2.Recenter` (`:212-234`) already establishes the convention and `fixAnchoring.md`
§4.2 argues it. It is still right, and the content frame does not violate it: `World Root` is an
ordinary world-space object whose pose is replicated like any other networked value. Nobody rewrites
world coordinates into anchor-local ones anywhere.

### 2.3 Align every frame, not once

`fixAnchoring.md` §4.3 is unchanged and is the single most important decision in this document. The
short form: an anchor's world pose is recomputed from the rig every frame
(`OVRSpatialAnchor.UpdateTransform`, `:801-809` → `TryGetPose`, `:779-796` →
`pose.ToWorldSpacePose(Camera.main)`), its tracking-space pose is not constant either (the recenter
mechanisms in §1), and anchors are re-localised as the room map improves. Each of those slides a
one-shot alignment permanently out of the shared frame for one client.

Meta's own sample does it per frame: `AlignCameraToAnchor.cs`, `Update()` at `:33-36` with
`[DefaultExecutionOrder(10)]` at `:28`. **Adopt their cadence and their execution order; keep our
maths** — theirs takes yaw from `anchorTransform.eulerAngles.y` (`:60`), which is degenerate for a
near-vertical anchor, and round-trips the anchor through tracking space and back (`:51-66`), which
ours does not need to do.

### 2.4 `CameraController2.AlignRigToAnchor`

Unchanged from `fixAnchoring.md` §5.2, restated because it is load-bearing here.

Write the rig-to-world map as `W = XRRig ∘ E`, where `E` is everything between `XRRig` and the
tracking origin: `Camera Offset` (authored at y 1.36144, `StairsGame.unity:961`, zeroed at runtime by
`XROrigin.MoveOffsetHeight` — `XROrigin.cs:229-233`, Floor mode → 0), plus the camera's driven local
pose, plus the head pose the SDK divides back out. The anchor's tracking-space position is `x`, so
the pose the SDK reports is `A = XRRig ∘ E · x`. Build `D`, the yaw-only inverse of `A`, and assign
`XRRig' = D ∘ XRRig`:

```
new anchor world pose = XRRig' ∘ E · x = D ∘ XRRig ∘ E · x = D · A = 0
```

The anchor lands on the world origin in all three axes, whatever sits in `E`. It is a fixed point, so
running it again is a no-op — which is what makes a per-frame loop cheap and safe.

On `CameraController2`, next to the tunables at `:11-13`:

```csharp
// A shared anchor placed on the floor should localize within a few centimetres of this headset's
// own floor. Anything past this is a tracking failure, not a floor-calibration difference.
public float MaxAnchorHeightDisagreement = 0.25f;

// Seconds between repeats of the anchor-height warning. At frame rate this is a log flood, and the
// interesting events are "it started" and "it stopped", not the sixty in between.
public float HeightWarningIntervalSeconds = 5f;
private float nextHeightWarningAt;

// Static because everything that cares — PlayerControls, WorldGrab, the probe, the locomotion gate
// — needs it without holding a reference to the rig, and the rig is destroyed and rebuilt on every
// game switch (PlayerControls.cs:221-251).
public static bool LocalIsAligned { get; private set; }
public static event System.Action LocalAlignmentChanged;
```

```csharp
/// <summary>
/// Move this rig so the shared anchor lands on the world origin, yaw-only. Called every frame by
/// BoardAnchor while bound — not once — because the anchor's tracking-space pose is not a constant
/// (§2.3). Idempotent by construction: the anchor is driven onto a fixed point, so a frame in which
/// nothing moved writes back the pose that is already there.
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
        // The anchor is pointing at the ceiling. Should be impossible — BoardAnchor creates it with
        // a yaw-only rotation (§2.6) — but LookRotation returns garbage rather than failing, and
        // garbage here rotates the whole room.
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

    // Take the anchor's HEIGHT as well as its horizontal position. Each headset puts y = 0 on its
    // OWN estimate of the floor and those estimates routinely differ by a few centimetres. The
    // anchor is the only object in the system that knows the real answer. deltaRot is yaw-only, so
    // this is a pure vertical correction and cannot lean.
    //
    // The quantity below is invariant to where the rig is: anchor.position.y - transform.position.y
    // reduces to the anchor's height above the tracking origin, i.e. above this headset's floor.
    // That is why it is a meaningful guard and not a moving target.
    float anchorAboveMyFloor = anchor.position.y - transform.position.y;
    if (Mathf.Abs(anchorAboveMyFloor) > MaxAnchorHeightDisagreement)
    {
        // Reject the frame rather than half-applying the correction. Under a per-frame loop the
        // previous frame's pose is strictly better than any fallback: a board stale by one frame is
        // invisible, a board snapping to the floor and back is not.
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

    Debug.LogWarning("AlignRigToAnchor: the room anchor localized " +
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

Because the anchor is on the real floor and the rig origin is on the real floor
(`m_TrackingOriginMode: 2`, `StairsGame.unity:960`; `EnableTrackingOriginStageMode: 1`,
`OculusSettings.asset`), `newPos.y = rig.y − anchor.y ≈ 0`. World y = 0 stays the real floor, which
is what keeps `PlayerRing.SlotPosition` (y = 0, `PlayerRing.cs:38`) and `PlayerControls.cs:312` (the
avatar root at the user's feet) correct.

**The startup race the guard will catch.** `XROrigin.MoveOffsetHeight` returns immediately outside
play mode (`XROrigin.cs:226-227`) and only zeroes `Camera Offset` once the input subsystem reports
Floor mode. Any alignment attempted before that lands is off by the serialized 1.36144
(`StairsGame.unity:961`), and the guard will reject those frames — correctly — until it resolves.
One warning at startup and never again is that, and nothing is wrong.

### 2.5 `RoomAnchor.cs` — where the shared state lives

`fixAnchoring.md` §4.5 chose a spawned singleton and the reasoning still holds: **`GlobalObjectIdHash`
appears in no file under `Assets/Scenes`**, so there are no in-scene `NetworkObject`s to hang it on,
and NGO forbids one on the `NetworkManager`'s own GameObject.

New prefab `Assets/Prefabs/Room Anchor.prefab` — a `NetworkObject` plus `RoomAnchor : NetworkBehaviour`
— registered in `Assets/DefaultNetworkPrefabs.asset` (which today holds exactly one entry,
`Player.prefab`, `:16-22`) and spawned by the host when the session opens. It survives the game
switch for the same reason the players do, and it outlives any one player object — which matters,
because `NetworkReconnectHandler.cs:18-28` calls `Shutdown()` then `StartClient()` in a loop and
respawns player objects routinely.

It carries the anchor identity **and** the content pose. Both are room-wide facts about the session,
and putting them on one object means a late joiner gets the anchor and the board placement in the
same replication pass.

```csharp
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class RoomAnchor : NetworkBehaviour
{
    public static RoomAnchor Instance { get; private set; }

    /// <summary>worldHolder when nobody is holding the world. Not 0 — that is the host's client id.</summary>
    public const ulong NoHolder = ulong.MaxValue;

    // Hard bounds, enforced server-side as well as in the gesture, so a client that has been
    // modified cannot shrink the board to a speck for everybody.
    public const float MinScale = 0.15f;
    public const float MaxScale = 4f;

    // --- The common anchoring point (§2.6). FixedString64Bytes holds a 36-character GUID string
    // with room to spare. Publish both in the same server call so no client can ever see a group
    // without a UUID.
    public NetworkVariable<FixedString64Bytes> anchorGroup = new NetworkVariable<FixedString64Bytes>(
        "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<FixedString64Bytes> anchorUuid = new NetworkVariable<FixedString64Bytes>(
        "", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- The content frame (§3, §4). Yaw only and uniform scale, deliberately: a board that can be
    // pitched off a real table or squashed on one axis is a board somebody will pitch and squash.
    public NetworkVariable<Vector3> contentPos = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> contentYaw = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> contentScale = new NetworkVariable<float>(
        1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Who is currently allowed to write the three above. First to squeeze both grips wins until
    // they let go. Without this, two players gesturing at once fight at the tick rate and the board
    // vibrates between two poses.
    public NetworkVariable<ulong> worldHolder = new NetworkVariable<ulong>(
        NoHolder, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        Instance = this;

        if (IsServer)
        {
            // A holder who crashes, drops wifi, or is disconnected by NetworkReconnectHandler must
            // not lock the board for the rest of the session.
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        }
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void HandleClientDisconnect(ulong clientId)
    {
        if (worldHolder.Value == clientId)
        {
            worldHolder.Value = NoHolder;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ClaimWorldServerRpc(ServerRpcParams p = default)
    {
        if (worldHolder.Value != NoHolder)
        {
            return;                       // somebody else is already holding it; the claim just fails
        }
        worldHolder.Value = p.Receive.SenderClientId;
    }

    [ServerRpc(RequireOwnership = false)]
    public void ReleaseWorldServerRpc(ServerRpcParams p = default)
    {
        if (worldHolder.Value == p.Receive.SenderClientId)
        {
            worldHolder.Value = NoHolder;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetContentServerRpc(Vector3 pos, float yaw, float scale, ServerRpcParams p = default)
    {
        if (worldHolder.Value != p.Receive.SenderClientId)
        {
            return;                       // not yours to move
        }
        contentPos.Value = pos;
        contentYaw.Value = yaw;
        contentScale.Value = Mathf.Clamp(scale, MinScale, MaxScale);
    }

    [ServerRpc(RequireOwnership = false)]
    public void PublishAnchorServerRpc(string group, string uuid)
    {
        anchorGroup.Value = group;
        anchorUuid.Value = uuid;
    }
}
```

Spawn it once, from the server-started callback rather than from `GameController`, so the host path
and any future path both get it:

```csharp
// In BoardAnchor (§2.6), which already lives on the persistent Network Manager object.
void Awake()
{
    NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
}

void HandleServerStarted()
{
    if (RoomAnchor.Instance != null)
    {
        return;
    }
    GameObject go = Instantiate(roomAnchorPrefab);
    // destroyWithScene: false — this has to survive GameSelector's LoadSceneMode.Single switch,
    // which is the whole reason it is not a scene object.
    go.GetComponent<NetworkObject>().Spawn(false);
}
```

### 2.6 `BoardAnchor.cs` — place, share, load, and hold

One new file, `Assets/Scripts/BoardAnchor.cs`, on the `Network Manager` object (which is
`DontDestroyOnLoad` via `PersistentObject.cs:7-10`). Sketched rather than written out in full: the
async plumbing is ordinary and the decisions are what matter.

```csharp
// Must run AFTER OVRSpatialAnchor.Update() (default order, OVRSpatialAnchor.cs:699), which is what
// refreshes boundAnchor.transform's world pose for this frame from the runtime's tracking-space
// pose. Reading it at default order gets last frame's rig baked in. Same value and same reason as
// Meta's own AlignCameraToAnchor.cs:28.
[DefaultExecutionOrder(10)]
public class BoardAnchor : MonoBehaviour
```

**Hold the alignment, every frame.**

```csharp
void Update()
{
    HoldAlignment();
    ReadPlacementInput();
}

private void HoldAlignment()
{
    if (boundAnchor == null)
    {
        return;
    }

    // Do not align to an anchor the runtime cannot currently locate. UpdateTransform only writes
    // the transform when it got a pose (OVRSpatialAnchor.cs:805-808), so an untracked anchor leaves
    // a stale one in place — and for a freshly created GameObject that stale pose is the world
    // origin with identity rotation, which AlignRigToAnchor would happily accept as "already
    // aligned". Holding the last good rig pose is right: the user has not moved, the tracker has
    // stopped reporting.
    if (!boundAnchor.IsTracked)                    // OVRSpatialAnchor.cs:180, written at :804
    {
        NoteUntracked();
        return;
    }

    ClearUntracked();

    CameraController2 rig = Rig;                   // re-resolved after every scene load, see §2.8
    if (rig != null)
    {
        rig.AlignRigToAnchor(boundAnchor.transform);
    }
}
```

`NoteUntracked` / `ClearUntracked` are hysteresis — silent for a one-frame dropout, and after
`UntrackedWarningSeconds` (2 s) a message through `DebugLog`. **Never report alignment you did not
verify:** `IsTracked` gates it, and it must also guard the moment of adoption — after `BindTo`, if
the anchor is not yet tracked, log it and return; `HoldAlignment` picks it up on the frame it becomes
tracked. That is only safe *because* alignment is continuous.

**Creating and sharing (room owner only).** The anchor goes at the placer's feet, facing wherever
they face:

```csharp
// The anchor only has to be a stable point every headset agrees on. It does NOT have to be where
// the board is — the board is placed afterwards with both hands (§4), and that placement is shared,
// so neither the anchor's position nor its yaw needs to mean anything. Feet, not table: nothing to
// aim at and nothing to measure.
Vector3 anchorPos = new Vector3(head.position.x, rig.position.y, head.position.z);
Quaternion anchorRot = Quaternion.Euler(0f, head.eulerAngles.y, 0f);

GameObject go = new GameObject("Room Anchor Point");
go.transform.SetPositionAndRotation(anchorPos, anchorRot);   // pose BEFORE the component
DontDestroyOnLoad(go);                                       // §2.8
OVRSpatialAnchor anchor = go.AddComponent<OVRSpatialAnchor>();

if (!await anchor.WhenLocalizedAsync()) { /* report, bail */ }          // :206

Guid group = Guid.NewGuid();
var saved = await anchor.SaveAnchorAsync();                             // :607
if (!saved.Success) { /* report, bail */ }

var shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, group); // :446
if (!shared.Success) { /* report, bail */ }

RoomAnchor.Instance.PublishAnchorServerRpc(group.ToString(), anchor.Uuid.ToString());
AdoptAnchor(anchor);
```

Set the transform **before** adding the component. `OVRSpatialAnchor.Start()` calls
`CreateSpatialAnchor()`, which captures the tracking-space pose at that moment; this order closes
the window rather than narrowing it. Order matters throughout: **create → localize → save → share**.
The SDK is explicit that anchors must exist, be localized and be saved before sharing
(`OVRSpatialAnchor.cs:419`); skipping the save is the most common way for `ShareAsync` to fail with
something unhelpful.

**Loading (everyone else),** on `anchorUuid.OnValueChanged`:

```csharp
List<OVRSpatialAnchor.UnboundAnchor> unbound = new List<OVRSpatialAnchor.UnboundAnchor>();

// The three-argument overload (OVRSpatialAnchor.cs:1344-1347) filters by UUID inside the query. Ask
// for the one anchor the room owner published rather than loading the group and picking — a client
// on the wrong anchor looks like working software, and looking like working software is worse than
// failing, because a client that fails to align still gets a ring slot and behaves correctly.
var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
    group, new[] { publishedUuid }, unbound);

if (!result.Success || unbound.Count == 0) { /* report, ShowStatus, return */ }
if (!await unbound[0].LocalizeAsync(LocalizeTimeoutSeconds)) { /* retry with backoff */ }  // :1034

GameObject go = new GameObject("Room Anchor Point");
DontDestroyOnLoad(go);
unbound[0].BindTo(go.AddComponent<OVRSpatialAnchor>());                                     // :1068
AdoptAnchor(go.GetComponent<OVRSpatialAnchor>());
```

**Gate the whole component on the runtime being there.** With no headset attached, `OVRManager` never
comes up and every anchor call fails in a different way. Check once in `Start`
(`OVRManager.instance != null && OVRPlugin.initialized` — `OVRPlugin.cs:3637`) and disable the
component if not: the Editor
is the only place the non-colocated path can be exercised at all, and it has to keep working. Every
other guard below is a null check that also happens to cover it.

**Latch requests, never drop them.** Any request that arrives while a load is in flight — the owner
re-placing the anchor, a reconnect re-delivering the UUID, a user pressing the key — must be latched
into a pending field and retried, never dropped on a `busy` flag. A load can be in flight for a
minute with retries and an 8-second localize timeout, which is a wide window to drop things in, and
a dropped request leaves that one client bound to a stale anchor permanently with nothing to tell it
otherwise. Guard the retry with `pending != current` so a repeated press during a successful load
does not restart it.

### 2.7 What alignment has to switch off

`CameraController2.Update` (`:65-114`) contains four things that move the rig. A colocated player
walks with their legs; every one is meaningless or actively wrong for them.

| Lines | What | While aligned |
|---|---|---|
| `:73-89` | snap/smooth turn on the left joystick X | off |
| `:92-96` | translate along `LeftHand.forward` | off |
| `:100-103` | `Recenter()` on the left joystick button | off — it is the one control that would fight alignment on purpose |
| `:106-113` | M/N keyboard tilt | off — it is the only thing that puts pitch into the rig, and §2.4's height guard assumes yaw-only |

```csharp
void Update()
{
    if (Inputs == null)
    {
        return;
    }

    // A colocated player moves by walking. Locomotion, recentring and the debug tilt all fight the
    // per-frame alignment (which wins — BoardAnchor runs at execution order 10 and this runs at 0)
    // so leaving them on would not break the frame, it would mean controls that silently do
    // nothing. Off is honest.
    if (LocalIsAligned)
    {
        return;
    }

    // Both grips are the world grab (§4). The user is not trying to walk.
    if (WorldGrab.IsActive)
    {
        return;
    }
    ...
```

**The ring slot.** `PlayerRing` exists to spread players around a *virtual* board when they are in
different rooms. Moving a colocated player onto a slot is exactly the involuntary rig move
`PlayerRing.cs:56-61` warns about. `PlaceAtRingSlot` (`:136-147`) already refuses in the lobby; add
the same shape of guard:

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
        ringSlot = slot;      // remembered, so losing the anchor re-places them correctly
        return;
    }

    ringSlot = slot;
    ApplyRingAnchor();
}
```

Having the server release the slot of a colocated player (so the 12-slot ring is not consumed by
people who are not standing on it) is worth doing eventually and safe to defer:
`PlayerControls.OccupiedSlots` (`:161-191`) already skips any player whose slot is `< 0`.

**Placing the anchor.** One control, room-owner only. Add a `Place Anchor` key to `Menu1.prefab` and
a case to `MenuControl.HandleKey` (`:78-106`) — that is the documented way to add a control here
(`MenuControl.cs:8-11`), and the prefab already has `Stairs` (`:1080`) and `Chasms` (`:919`) keys to
copy. Gate it on `PlayerControls.roomOwner` (`:49`, `:387-390`) so two people cannot place two
anchors.

For **re-align** — a recovery action a user needs quickly, mid-game, without a menu in their face —
use `ButtonA`. Free: `ButtonA`, `ButtonB`, `ButtonY` and both grips are read nowhere in
`Assets/Scripts`; only `ButtonX` (menu, `MenuControl.cs:41`), the left joystick button (recentre,
`CameraController2.cs:100`) and the right joystick button (clear the log, `DebugLog.cs:63`) are taken.
§4 takes both grips; `ButtonA` is still free after that.

### 2.8 Surviving a game switch

`GameSelector.LoadGameScene` (`:50-84`) is `LoadSceneMode.Single`, and `PlayerControls.BindToScene`'s
comment (`:214-220`) spells out the consequence: the rig, the camera, the hands and the Menu Manager
are all destroyed and rebuilt.

1. **`BoardAnchor` must not live in a game scene.** Put it on the `Network Manager` object. The
   `Room Anchor Point` GameObject carrying the `OVRSpatialAnchor` must persist too — destroying it
   means re-loading and re-localising on every game switch, which is seconds of a wrong room every
   time somebody changes game.
2. **`BoardAnchor` must re-resolve the rig,** on `SceneManager.activeSceneChanged` — the same
   pattern `PlayerControls.BindToScene` (`:221-251`) and `CameraController2.AdoptLocalPlayerSlot`
   (`:162-178`) already use. The `HoldAlignment` early-out on a null rig means a frame or two of no
   alignment during the load, which is invisible.
3. **The new rig starts at its authored transform** and `CameraController2.Start` (`:29-32`) captures
   that as its recentre anchor. Per-frame alignment fixes it on the first frame after the rig
   appears — a third argument for building §2.6 as a loop rather than an event.
4. **`World Root` is per-scene**, so `RoomContent` (§3.3) must re-apply the networked pose on the
   first frame of the new scene. It does, because it reads `RoomAnchor.Instance` every frame and
   `RoomAnchor` survives.

`LocalIsAligned` being `static` is deliberate for the same reason: it has to outlive the rig that
set it.

---

## 3. The content frame — `World Root`

### 3.1 It already exists, and it is empty

Every game scene already has a root `World Root` at the origin with identity rotation and unit scale
and **no children** (`StairsGame.unity:2006`, `ChasmGame.unity:8829`, `GameScene.unity:1781`). It is
wired to `MenuControl.worldRoot` (`StairsGame.unity:2075`, `ChasmGame.unity:9328`), whose only job is
`SetActive(false)` while the menu is open (`MenuControl.cs:148-151`, `:172-175`) — which today hides
nothing, because nothing is under it.

So the object, the name and the concept are already there. Use it.

### 3.2 What goes under it, and what must not

| Scene | Move under `World Root` | Leave alone |
|---|---|---|
| `StairsGame` | `Board` (`:135`, with its `BoxCollider` at `:141-161`) and its `Cube` child | `XRRig`, `Menu Manager`, `Input Reader`, `Directional Light` |
| `ChasmGame` | `Board` (`:7694`) and everything under it — `Plateaus`, `Bridges`, and all the `Plateau (n)` / `Bridge Spots (n)` instances | same |
| `GameScene` | nothing — it has no board | — |

One drag per scene: both games already keep their content under a single `Board` root.

The **players are not under it** — they are spawned `NetworkObject`s that live at the scene root —
so requirement 5's "apart from other players" needs no code at all. The directional light stays out
because a directional light has no position. `Input Reader` and `Menu Manager` stay out because
scaling input and UI is never what anyone means.

`GameScene.unity` is in the build list (`EditorBuildSettings.asset`) but is **not** in `GameRoutes`
(`:16-20`), so it is unreachable at runtime. Do the work in `StairsGame` and `ChasmGame`; either
delete `GameScene` or keep it in step, but do not spend time on it.

### 3.3 `RoomContent.cs`

New file, on `World Root` in each game scene.

```csharp
using UnityEngine;

/// <summary>
/// Applies the room's shared board placement to this scene's World Root.
///
/// Order 15: after BoardAnchor (10) has aligned the rig this frame, and before WorldGrab (20),
/// which overwrites this on the one client currently holding the world.
/// </summary>
[DefaultExecutionOrder(15)]
public class RoomContent : MonoBehaviour
{
    public static RoomContent Instance { get; private set; }

    // Seconds to converge on a newly received pose. The gesture streams at WorldGrab.SendHz, well
    // under frame rate, so applying each arriving value raw makes the board step. This is not
    // network interpolation with a delay buffer — it is a first-order filter, and 80 ms is short
    // enough that the person doing the gesture and the person watching it stay in sync.
    public float SmoothTime = 0.08f;

    private bool snapped;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        RoomAnchor room = RoomAnchor.Instance;
        if (room == null)
        {
            return;                     // no session: the scene's authored placement stands
        }

        // The client holding the world already drove this transform at frame rate in WorldGrab.
        // Smoothing it toward a value it sent 50 ms ago would fight its own hands.
        if (WorldGrab.LocalHoldsWorld)
        {
            return;
        }

        Vector3 targetPos = room.contentPos.Value;
        Quaternion targetRot = Quaternion.Euler(0f, room.contentYaw.Value, 0f);
        float targetScale = room.contentScale.Value;

        // A scene that has just loaded, or a client that has just joined, must not glide in from
        // the authored transform — it must already be right on the first frame it is visible.
        float t = 1f;
        if (snapped && SmoothTime > 0f)
        {
            t = 1f - Mathf.Exp(-Time.deltaTime / SmoothTime);   // frame-rate independent
        }
        snapped = true;

        transform.SetPositionAndRotation(
            Vector3.Lerp(transform.position, targetPos, t),
            Quaternion.Slerp(transform.rotation, targetRot, t));
        transform.localScale = Vector3.one *
            Mathf.Lerp(transform.localScale.x, targetScale, t);
    }

    /// <summary>Called by WorldGrab on the holding client, at frame rate, with no smoothing.</summary>
    public void ApplyImmediate(Vector3 pos, float yaw, float scale)
    {
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        transform.localScale = Vector3.one * scale;
        snapped = true;
    }
}
```

`localScale` is set from `Vector3.one * scale`, never per-axis. Non-uniform scale on a board game
skews colliders, breaks normals, and is not a thing anybody wants; making it unrepresentable is
cheaper than policing it.

### 3.4 The menu must stop deactivating `World Root`

`MenuControl.OpenMenu1` calls `worldRoot.SetActive(false)` (`:148-151`). The field's own comment says
it is optional ("Leave empty if a game does not need it", `:19-21`), and it is doing nothing today.
Once the board is under `World Root`, opening the menu would make the entire board vanish — in a
colocated game, in front of everybody, including the people who did not open a menu.

**Clear `worldRoot` on the Menu Manager in `StairsGame` (`:2075`) and `ChasmGame` (`:9328`).** The
laser pointer only reports keys it is touching (`pointerControl`), so nothing behind the menu can be
interacted with through it anyway.

### 3.5 The ring radius should follow the board (optional)

Not required, and worth doing later. `PlayerRing.Radius` is 2 m (`PlayerRing.cs:29`), chosen against
a 1:1 board. Shrink the board to 0.3× and non-colocated players are standing 2 m from a 60 cm board.
The fix is to place ring slots in content space and flatten them back to the floor:

```csharp
// In CameraController2.ApplyRingAnchor. The height is dropped deliberately: the board may be 80 cm
// up on a table, and the player's feet are on the floor.
Transform content = RoomContent.Instance != null ? RoomContent.Instance.transform : null;
Vector3 slot = PlayerRing.SlotPosition(ringSlot);
anchorPosition = content != null ? content.TransformPoint(slot) : slot;
anchorPosition.y = 0f;
anchorRotation = (content != null ? Quaternion.Euler(0f, content.eulerAngles.y, 0f) : Quaternion.identity)
                 * PlayerRing.SlotRotation(ringSlot);
```

Do it after §4 is working and the scale range has settled in practice.

---

## 4. Requirement 5 — the two-grip world grab

### 4.1 What the fork's ancestor did, and what to keep from it

Math_Classroom_v8 had this gesture, in `LineDrawer.Update`'s `resizingDrawings` state. It is worth
reading before writing the new one (`git show HEAD:Assets/Scripts/LineDrawer.cs`) — one of its six
decisions carries over unchanged, one needs adjusting, and the four to drop are the four a fresh
implementation would most naturally arrive at anyway.

| It did | Keep? | Why |
|---|---|---|
| Entered on `(LeftGripDown && RightGrip) \|\| (RightGripDown && LeftGrip)` | **Change.** Test the two *levels*, not the edges — see §4.6. | The edge flags in `InputReader` are sticky after a one-frame press. |
| Only entered from the idle state, never while a hand was holding something | **Keep.** | `GrabControl` is dormant today — **nothing in the project is tagged `Grabbable`** — but the moment pieces become grabbable, two-handed grab and two-hand-holds-a-piece are the same input. |
| Reparented the content under a `Middle` object following the hand midpoint | **Drop.** | The reparent/unparent round trip rewrites every child's transform, accumulates float error, and forced a per-child readback afterwards to tell the network what moved. Compute the root pose directly instead — it is four lines of vector maths and touches one transform. |
| `originalDistance` re-baselined **every frame**, scale accumulated as a product of per-frame ratios | **Drop.** | Drift, and the clamp loses its meaning the moment it bites: once clamped, the baseline is gone and the board slides when it should be stationary. Capture the baseline once, at gesture start. |
| Took rotation from `Middle.forward = rh − lh`, i.e. full pitch and roll (`MiddleControl.cs`) | **Drop.** | A board on a real table. Yaw only. |
| Hid the networked twins for the duration and pushed one update on release | **Drop.** | Acceptable for drawings you own. Not acceptable for a shared board on a shared table, where everyone is watching the thing move. |

### 4.2 The gesture, defined

- **Enter:** both grip buttons held, neither grabber overlapping a grabbable, and no other player
  currently holding the world.
- **While held:** the board follows both hands — translate by the hand midpoint, turn by the yaw of
  the hand axis, scale by the ratio of the hand span to the span at the start. The point of the
  board that was under the midpoint when you squeezed stays under it. Yaw only, uniform scale only.
- **Exit:** either grip released. The final pose is committed and the lock is released.

### 4.3 The maths

Let `L`, `R` be the two controller world positions. At the moment the gesture starts, record

```
c₀ = (L + R) / 2                          hand midpoint
s₀ = |R − L|                              hand span
θ₀ = atan2((R−L).x, (R−L).z)              hand yaw, floor-projected
p₀, ψ₀, k₀                                the content root's position, yaw and scale
```

Each frame, with `c`, `s`, `θ` measured the same way and `Δθ` accumulated from per-frame
`Mathf.DeltaAngle` steps (not `θ − θ₀`, which wraps at ±180° and would spin the board a full turn
when the hands cross it):

```
k  = clamp(k₀ · s / s₀,  MinScale, MaxScale)
a  = k / k₀                                the ratio that ACTUALLY got applied
Q  = Quaternion.Euler(0, Δθ, 0)
p  = c + Q · (a · (p₀ − c₀))
ψ  = ψ₀ + Δθ
```

Using the **clamped** ratio `a` in the position line is the detail that matters: with the raw ratio,
hitting the scale limit makes the board slide out from under your hands while appearing to be
stationary in size.

`p = c + Q·(a·(p₀ − c₀))` is just "apply the similarity transform that maps `c₀ → c` with rotation
`Q` and scale `a` to the content root's origin". It has the property you want and the naive
`p = p₀ + (c − c₀)` does not: with the naive form, scaling about the midpoint drags the board
sideways whenever the board's origin is not exactly under your hands, which it never is.

### 4.4 `WorldGrab.cs`

New file, on `XRRig` in each game scene. Per-scene is correct — it holds no state that has to
outlive a game switch, because all the state that matters is on `RoomAnchor`.

```csharp
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Both grips: move, turn and resize everything in the room except the players.
///
/// Runs after BoardAnchor (order 10) and RoomContent (order 15) so the hand positions it reads are
/// in this frame's aligned room frame, not last frame's.
/// </summary>
[DefaultExecutionOrder(20)]
public class WorldGrab : MonoBehaviour
{
    public InputReader inputs;
    public Transform leftHand;
    public Transform rightHand;
    public GrabControl leftGrabber;
    public GrabControl rightGrabber;

    // Hands closer together than this at the start make the span ratio wildly sensitive — a 1 cm
    // wobble becomes a 20% scale change. Below it, move and turn still work and scale is held.
    public float MinSpan = 0.08f;

    // Server round trips per second while the gesture runs. The holder drives its own view at frame
    // rate regardless; this is only what everybody else sees. 20 with RoomContent's filter is
    // smooth; the NGO tick is 30 (OpeningScene.unity:523) so there is no point going higher.
    public float SendHz = 20f;

    /// <summary>True on any client currently running the gesture. Read by CameraController2.</summary>
    public static bool IsActive { get; private set; }

    /// <summary>True only on the client that owns the world right now. Read by RoomContent.</summary>
    public static bool LocalHoldsWorld { get; private set; }

    enum State { Idle, Claiming, Holding }
    State state = State.Idle;

    Vector3 startCenter, startPos;
    float startSpan, startScale, startYaw;
    float lastHandYaw, yawAccum;
    bool yawPrimed;                 // false until the hand axis has been usable at least once
    bool claimSent;                 // a Claim went out and has not been matched by a Release
    float nextSendAt;
    float nextClaimAt;

    // Re-ask this often while waiting for the lock. A claim that arrives while somebody else holds
    // the world is refused silently, and if they let go in the same breath there is nothing left to
    // observe — this client would wait in Claiming for as long as it kept squeezing. Claiming again
    // is free (the RPC is a no-op unless the lock is actually available) and closes that hole.
    const float ClaimRetrySeconds = 0.5f;

    void OnDisable()
    {
        // A game switch destroys this rig mid-gesture. Releasing here is what stops the lock from
        // leaking and freezing the board for the whole room.
        if (state != State.Idle)
        {
            Cancel();
        }
    }

    void Update()
    {
        RoomAnchor room = RoomAnchor.Instance;
        if (room == null || inputs == null || leftHand == null || rightHand == null)
        {
            return;
        }

        // Levels, not edges: InputReader's *Down flags stick after a one-frame press (§4.6).
        bool both = inputs.LeftGrip && inputs.RightGrip;

        switch (state)
        {
            case State.Idle:
                if (both && CanStart(room))
                {
                    Capture(room);
                    Claim(room);
                    state = State.Claiming;
                    IsActive = true;
                }
                break;

            case State.Claiming:
                if (!both)
                {
                    Cancel();
                    break;
                }
                if (room.worldHolder.Value == NetworkManager.Singleton.LocalClientId)
                {
                    // Re-capture. The claim cost a round trip and the hands moved during it; using
                    // the pre-claim baseline would make the board jump the moment the lock lands.
                    Capture(room);
                    LocalHoldsWorld = true;
                    state = State.Holding;
                }
                else if (Time.unscaledTime >= nextClaimAt)
                {
                    Claim(room);
                }
                break;

            case State.Holding:
                if (!both || room.worldHolder.Value != NetworkManager.Singleton.LocalClientId)
                {
                    Commit(room);
                    Cancel();
                    break;
                }
                Drive(room);
                break;
        }
    }

    void Claim(RoomAnchor room)
    {
        room.ClaimWorldServerRpc();
        claimSent = true;
        nextClaimAt = Time.unscaledTime + ClaimRetrySeconds;
    }

    bool CanStart(RoomAnchor room)
    {
        if (room.worldHolder.Value != RoomAnchor.NoHolder)
        {
            return false;
        }
        // Two grips with a piece in one of them is a two-handed piece grab, not a world grab.
        // Nothing is tagged Grabbable today, so both of these are false — this is here so it does
        // not have to be retrofitted the day the first piece becomes grabbable.
        if (leftGrabber != null && leftGrabber.lineHit) return false;
        if (rightGrabber != null && rightGrabber.lineHit) return false;
        return true;
    }

    void Capture(RoomAnchor room)
    {
        Vector3 l = leftHand.position;
        Vector3 r = rightHand.position;

        startCenter = (l + r) * 0.5f;
        startSpan = Vector3.Distance(l, r);
        yawAccum = 0f;

        // Squeezing with the controllers held together makes the hand axis vertical and the yaw
        // undefined. Leave it unprimed rather than seeding it with a made-up 0: the first frame the
        // axis IS usable would otherwise read as a large turn and snap the board.
        yawPrimed = TryHandYaw(l, r, out lastHandYaw);

        startPos = room.contentPos.Value;
        startYaw = room.contentYaw.Value;
        startScale = room.contentScale.Value;
    }

    void Drive(RoomAnchor room)
    {
        Vector3 l = leftHand.position;
        Vector3 r = rightHand.position;
        Vector3 center = (l + r) * 0.5f;
        float span = Vector3.Distance(l, r);

        // Accumulate the turn from per-frame deltas. atan2 wraps at +-180 degrees; a single
        // (theta - theta0) would spin the board a full turn when the hands cross the seam, and
        // would cap the gesture at half a revolution.
        //
        // A frame with no usable hand axis contributes nothing and does not move lastHandYaw, so
        // passing through a vertical hand pose is a pause in the turn rather than a spin.
        if (TryHandYaw(l, r, out float handYaw))
        {
            if (yawPrimed)
            {
                yawAccum += Mathf.DeltaAngle(lastHandYaw, handYaw);
            }
            lastHandYaw = handYaw;
            yawPrimed = true;
        }

        float scale = startScale;
        if (startSpan >= MinSpan)
        {
            scale = Mathf.Clamp(startScale * (span / startSpan),
                                RoomAnchor.MinScale, RoomAnchor.MaxScale);
        }

        float applied = scale / startScale;                  // the CLAMPED ratio, see §4.3
        Quaternion turn = Quaternion.Euler(0f, yawAccum, 0f);
        Vector3 pos = center + turn * (applied * (startPos - startCenter));
        float yaw = startYaw + yawAccum;

        // Locally at frame rate, so the hands never lag. Everybody else gets it at SendHz and
        // filters it (RoomContent).
        if (RoomContent.Instance != null)
        {
            RoomContent.Instance.ApplyImmediate(pos, yaw, scale);
        }

        if (Time.unscaledTime >= nextSendAt)
        {
            nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, SendHz);
            room.SetContentServerRpc(pos, yaw, scale);
        }
    }

    void Commit(RoomAnchor room)
    {
        // One unconditional final send. Without it the room keeps whatever the last rate-limited
        // sample happened to be, which can be up to 1/SendHz of hand movement away from where the
        // person let go. Called before Cancel(), and reliable-sequenced delivery means the server
        // applies this pose before it clears the lock that authorises it.
        if (RoomContent.Instance != null)
        {
            Transform t = RoomContent.Instance.transform;
            room.SetContentServerRpc(t.position, t.eulerAngles.y, t.localScale.x);
        }
    }

    void Cancel()
    {
        RoomAnchor room = RoomAnchor.Instance;

        // Release on claimSent, NOT on LocalHoldsWorld. Letting go before the claim's round trip
        // completes is easy to do and would otherwise leave the server granting a lock nobody is
        // using, for the rest of the session. RPCs from one sender are reliable-sequenced, so a
        // Release sent after a Claim is always processed after it, and ReleaseWorldServerRpc is a
        // no-op for anyone who is not the holder.
        //
        // The connection check is for OnDisable: this also runs on application quit and on a
        // NetworkReconnectHandler shutdown, where sending anything logs a warning at best.
        NetworkManager nm = NetworkManager.Singleton;
        if (room != null && claimSent && nm != null && nm.IsConnectedClient)
        {
            room.ReleaseWorldServerRpc();
        }

        claimSent = false;
        state = State.Idle;
        IsActive = false;
        LocalHoldsWorld = false;
    }

    /// <summary>
    /// Yaw of the hand axis, floor-projected. False when the two controllers are stacked vertically,
    /// where the axis has no yaw and atan2 would amplify tracking noise into a spin.
    /// </summary>
    static bool TryHandYaw(Vector3 l, Vector3 r, out float yaw)
    {
        Vector3 axis = r - l;
        axis.y = 0f;
        if (axis.sqrMagnitude < 1e-4f)      // hands within 1 cm horizontally
        {
            yaw = 0f;
            return false;
        }
        yaw = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;
        return true;
    }
}
```

`leftHand` / `rightHand` / `leftGrabber` / `rightGrabber` are assigned in the scene. Note that
`CameraController2` (`:41-50`) had to add a runtime fallback for exactly this kind of serialized
reference after a field rename dropped it silently — `StairsGame.unity:992` still carries the dead
key `RightHand: {fileID: 776116463}` from before that rename. If you would rather not repeat it, look
the four up by name in `Start` the way `CameraController2.FindDescendant` (`:185-203`) does.

### 4.5 Who owns the world while it is being moved

The lock in §2.5 is not optional politeness. Without it, two players squeezing at once each stream a
pose at `SendHz` derived from their own hands, the server takes whichever arrived last, and the board
oscillates between two placements at the tick rate for as long as both hold on. With it, the second
person's claim simply fails and their gesture never starts.

Four properties to preserve, and the fourth is the one that is easy to get wrong:

- **The lock is server-held state, not a local flag.** A client cannot grant itself the world.
- **It is released on disconnect** (`HandleClientDisconnect`), because `NetworkReconnectHandler`
  (`:18-28`) makes clients disappear mid-session as routine behaviour.
- **It is released in `OnDisable`**, because a game switch destroys the rig the gesture lives on.
- **It is released whenever a claim was *sent*, not only when one was granted.** Squeezing and
  letting go inside the claim's round trip is a normal thing for a person to do, and it is the one
  path where the lock can be granted to a client that has already stopped gesturing. Releasing on
  `claimSent` closes it; RPC ordering from a single sender does the rest.

If you later decide only the room owner should move the board, that is one line in `CanStart`:
`if (!myPlayer.GetIsRoomOwner()) return false;` (`PlayerControls.cs:387-390`). Leaving it open is the
better default for a board game — it is the same argument `MenuControl.RequestGame` (`:108-111`)
already makes about who may change the game.

### 4.6 The `InputReader` edge flags are sticky — use the levels

`InputReader`'s `*Down` flags are never cleared on release. Tracing `LeftGrip`
(`InputReader.cs:441-462`): on a press that lasts exactly one frame, `LeftGripDown` is set true on
frame N; on frame N+1 the code takes the `else if (LeftGripOn)` branch, which sets `LeftGripUp` and
clears `LeftGripOn` but **never touches `LeftGripDown`**; on frame N+2 the final `else` clears only
`LeftGripUp`. `LeftGripDown` stays true for the rest of the session.

Every button in the file has this shape (`:148-168`, `:172-192`, `:197-217`, `:222-242`, `:252-272`,
`:344-364`, `:368-388`, `:393-413`, `:418-438`, `:441-462`). It has not bitten anyone because a
one-frame press at 72–90 Hz is a ~12 ms button tap, and the two existing edge consumers
(`MenuControl.cs:41`, `DebugLog.cs:63`) are pressed deliberately.

`WorldGrab` reads levels, so it is immune. Fixing the flags themselves is a separate one-line-per-
button change — clear `XDown` in the `else if` branch as well — and is worth doing while nothing
depends on the current behaviour.

**Keyboard testing.** `Q` and `P` are left and right grip (`InputReader.cs:287-289`, `:483-485`), so
the state machine, the claim/release round trip and the lock are all reachable in the Editor with two
clients and no headset. The gesture *maths* is not: with no HMD the `Left Hand` / `Right Hand`
transforms never move, so the span and the midpoint are constant and every frame computes the
identity. Drag those two transforms in the Hierarchy during play mode to drive it, or test it on a
headset. Neither path reproduces the anchoring.

---

## 5. The avatar

### 5.1 Head and body — already inactive

This is done. The evidence, so you can confirm rather than re-do it:

| Where | What |
|---|---|
| `PlayerControls.cs:40` | `public bool ShowRemoteHeadAndBody = false;` |
| `PlayerControls.cs:129-139` | `ApplyAvatarVisibility()` — the one place allowed to decide |
| `PlayerControls.cs:104` | called on the `!IsOwner` side of the ownership test |
| `PlayerControls.cs:97-100` | the owner disables **all** of its own children, so you never see your own avatar |
| `Player.prefab:398` | `mainFace` `m_IsActive: 0` — no first-frame flash |
| `Player.prefab:606-607` | `tornado`'s `PrefabInstance` overrides `m_IsActive` to `0` |
| `Player.prefab:291` | `ShowRemoteHeadAndBody: 0` serialized on the prefab |

Two things would silently undo it. `ShowRemoteHeadAndBody` is a public serialized field, so ticking
it in the Inspector on a scene instance brings the head and body back for that build. And
`ApplyAvatarVisibility` is called exactly once, at spawn — if ownership ever became transferable, a
newly-remote avatar would keep whatever state it had. Neither is a bug today; both are worth knowing.

**Do not delete `mainFace` or `tornado`.** `face` is dereferenced unguarded every frame at
`PlayerControls.cs:296-300`; `Transform.Find` returns inactive children, so an inactive `mainFace` is
free where a missing one is a `NullReferenceException` per frame per remote avatar. Keeping them also
makes turning heads back on a single `SetActive`.

### 5.2 The cones — three separate problems

The lever arm is gone (`PlayerControls.cs:315-323`, `Player.prefab:656` and `:775` both `−0.22`).
Three things are left, in descending order of how far they move the cone.

#### 5.2.1 The frame — metres

Without §2 this dominates everything else by two orders of magnitude. Nothing in this section is
measurable until §2 is working. Do §2 first.

#### 5.2.2 The offset — 8.5 cm, and here is the arithmetic

`lHPos`/`rHPos` carry the tracked pose (`PlayerControls.cs:320-321`), which is the **grip** pose:
`Left Hand` and `Right Hand` carry legacy `TrackedPoseDriver`s with `m_Device: 1`
(GenericXRController) and `m_PoseSource: 4` / `5` (LeftPose / RightPose) — `StairsGame.unity:521-525`
and `:919-923`. The local controller models `MainLeft` / `MainRight` sit at that pose with **zero
offset** (both at local `(0,0,0)`, identity), so the controller model *is* the ground truth for
"where the controller is".

The cone is not there. Four values, all serialized:

| Step | Value | Source |
|---|---|---|
| `PlayerLeft` / `PlayerRight` scale | 0.2 | `Player.prefab:29`, `:441` |
| cone local z | −0.22 | `Player.prefab:656`, `:775` |
| cone local scale | 15 → net 3 | `Player.prefab:631`, `:750` |
| mesh AABB centre z / extent z | 0.04286451 / 0.03283713 | `Player.prefab:705-706`, `:720-721` |

```
cone origin      = 0.2 × (−0.22)                          = −0.0440 m
mesh centre      = −0.0440 + 3 × 0.04286451               = +0.0846 m
half length      = 3 × 0.03283713                         =  0.0985 m
cone occupies z ∈ [−0.0139, +0.1831] m along grip-forward
```

So the cone's tail is 1.4 cm behind the controller and its 20 cm body extends forward from there. It
reads as an arrow *coming out of* the hand rather than an object *at* the hand — which is what
"exactly where the controllers are" is asking to change.

**To centre the cone on the tracked pose**, put the mesh centre at z = 0 in `PlayerLeft` space —
`z + 15 × 0.04286451 = 0`:

```
z = −15 × 0.04286451 = −0.6430
```

Set `m_LocalPosition.z` to **−0.643** on both cones (`Player.prefab:656`, `:775`). The 20 cm cone
then spans ±9.9 cm around the tracked pose instead of hanging off the front of it, so it overlaps a
real hand seen through passthrough rather than pointing away from one.

If ±9.9 cm reads as too much cone behind the wrist, shorten it by lowering the cone's **local scale**
`s` from 15 — but `z` and `s` are not independent, so recompute both together:

```
z = −s × 0.04286451                    centres the mesh on the controller
half length = 0.2 × s × 0.03283713     what you get for that s
```

e.g. `s = 8` → `z = −0.343`, a 5.3 cm half-length. Changing `s` alone and leaving `z` at −0.643 puts
the cone back off-centre, which is the thing this whole section is about.

Keeping **−0.22** is the other defensible answer: it puts the tail at the hand and reads as a
pointer. Pick one deliberately and write down which; both numbers are now derived rather than tuned,
which is the point of having moved the offset into the prefab in the first place. Verify with the
tip-to-tip test (§8 test 3) — two people touching controllers is the only measurement that settles
it.

#### 5.2.3 The rotation channel loses roll, and degrades pointing down

`lHRot`/`rHRot` are `NetworkVariable<Vector3>` carrying `localLeft.forward`
(`PlayerControls.cs:322-323`), rebuilt remotely with `Quaternion.LookRotation(lHRot.Value)`
(`:289`, `:294`). A forward vector is three numbers and a rotation is four:

- **Roll is discarded.** `LookRotation(f)` picks the roll that keeps `Vector3.up` up. Spin your wrist
  about the controller's forward axis and the remote cone does not spin. Invisible on a
  rotationally-symmetric cone; wrong the moment anybody puts a controller model or an asymmetric tool
  on the end of it.
- **It is ill-conditioned when `f` is near vertical,** because the roll is derived from
  `cross(up, f)`, which vanishes there. Pointing the controller straight down at the board is not an
  edge case in a board game — it is the default posture — and near that pose a millimetre of tracking
  noise swings the derived roll through a wide arc.

Send the rotation instead of a direction. NGO 2.13 serializes `Quaternion` natively and it is 16
bytes against 12:

```csharp
public NetworkVariable<Quaternion> lHRot = new NetworkVariable<Quaternion>(
    Quaternion.identity, NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);
```

```csharp
// owner
lHRot.Value = localLeft.rotation;
rHRot.Value = localRight.rotation;

// remote — no reconstruction, no degenerate case, no lost roll
playerLeft.rotation = lHRot.Value;
playerRight.rotation = rHRot.Value;
```

The same applies to `faceRot` (`:314`, `:299`). Drop the `!= Vector3.zero` guards with the
`Vector3`s — they were guarding against `LookRotation`'s zero-vector case, which stops existing.

#### 5.2.4 30 Hz — interpolate on receive

`NetworkConfig.TickRate` is **30** (`OpeningScene.unity:523`) and the headset renders at 72–90 Hz, so
remote hand values arrive on roughly every third frame. `NetworkVariable` does no interpolation — the
`Interpolate: 1` on the Player root (`Player.prefab:369`) applies only to `ClientNetworkTransform`,
and the hands do not go through it. The result is cones that step.

Filter on the receiving side, in the `!IsOwner` branch of `PlayerControls.Update`:

```csharp
// Convergence time for remote hands and head. Long enough to hide the 33 ms gap between ticks,
// short enough not to add lag of its own on top of what the network already costs.
public float RemoteSmoothTime = 0.06f;
```

```csharp
// NetworkVariable delivers at the tick rate (30 Hz) and we render at 72-90. Assigning the raw value
// makes remote cones step visibly. This removes the stepping; it does not remove the latency, which
// is a different problem — see below.
float t = 1f - Mathf.Exp(-Time.deltaTime / RemoteSmoothTime);
playerLeft.position = Vector3.Lerp(playerLeft.position, lHPos.Value, t);
playerLeft.rotation = Quaternion.Slerp(playerLeft.rotation, lHRot.Value, t);
```

Apply the same to `playerRight`. The head needs one extra step: the nametag is computed from
`facePos.Value` directly (`:281-282`), not from `face.position`, so smoothing `face` alone would
leave the tag stepping while the (invisible) head glided. Smooth the *value* once into a field and
drive both from it:

```csharp
smoothFacePos = Vector3.Lerp(smoothFacePos, facePos.Value, t);
usernameTransform.position = smoothFacePos + new Vector3(0f, FaceBelowEyes + NameTagAboveEyes, 0f);
face.position = smoothFacePos;
```

Note what this does **not** fix: at 30 Hz plus Relay round trip, a hand moving 1 m/s is roughly 5–8 cm
behind where it really is. That is larger than the anchor localisation error will be, and it is the
floor on how well "exactly where the controllers are" can hold for a *moving* hand. Two options, in
order: land the filter first (free, removes the visible artefact), then raise `TickRate` to 60
(`OpeningScene.unity:523`) and measure. The per-player payload is under 100 bytes a tick, so 12
players at 60 Hz is still small over Relay — but measure rather than assume.

While you are in `PlayerControls`: `usernameTransform` and `playerLeft`/`playerRight`/`face` are
dereferenced unguarded at `:281-300`, so a renamed child in `Player.prefab` turns
`FindChild`'s one-time error at `:337` into an exception every frame on every remote avatar. One
null check at the top of the branch.

### 5.3 The nametag — already above the head

Done, at `PlayerControls.cs:281-285`: position from `facePos` plus `FaceBelowEyes + NameTagAboveEyes`
(`:30`, `:34`), computed before the billboard rotation so there is no one-frame lag, with the prefab's
old 1.43 m constant zeroed (`Player.prefab:69`).

Two things to keep in mind rather than change:

- The tag is on the Player prefab, **not** under `World Root`, so it does not scale when the board
  does. That is correct — a name is UI, and shrinking the board to 0.3× should not shrink everyone's
  names to unreadable. It falls out of §3.2 for free; just do not "tidy" the players under
  `World Root` later.
- `Quaternion.LookRotation(-lookDirection)` at `:285` points the tag's +Z **away** from the viewer.
  TextMeshPro generates its mesh readable from +Z, so on the face of it this should read mirrored,
  and it evidently does not. With the head and body gone the tag is most of the avatar, so if anyone
  reports mirrored names, that minus sign is the culprit and not the new position.

### 5.4 Execution order

| Order | Component | Why |
|---|---|---|
| 0 | `OVRSpatialAnchor.Update` (`:699`) | refreshes the anchor's world pose for this frame |
| 10 | `BoardAnchor` | aligns the rig — must be after the anchor pose is fresh. Meta's own value (`AlignCameraToAnchor.cs:28`) |
| 15 | `RoomContent` | applies the shared board pose in a settled room frame |
| 20 | `WorldGrab`, `PlayerControls` | read hand and head world poses **after** the rig has moved this frame |

Adding `[DefaultExecutionOrder(20)]` to `PlayerControls` is a one-line change with a real effect: at
the default order it samples `myCam.position` and the hand transforms *before* `BoardAnchor` has
aligned the rig, so every pose it broadcasts is one frame stale in the shared frame — ~13 ms at 72 Hz,
on top of the network latency in §5.2.4.

The remaining refinement, if you want it after the above: sample the hands in
`Application.onBeforeRender` rather than `Update`. The `TrackedPoseDriver`s run
`m_UpdateType: 0` = UpdateAndBeforeRender (`StairsGame.unity:525`, `:923`), so the local user already
sees their own controller at a later-latched pose than the one being broadcast. It is a fraction of a
frame. Do it last or not at all.

---

## 6. Project settings you have to change

Anchors are off in this project. All of these are required, and none of them is code.

**`Assets/Oculus/OculusProjectConfig.asset`** — currently:

```yaml
anchorSupport: 0            # :19  -> 1  (OVRProjectConfig.AnchorSupport.Enabled)
sharedAnchorSupport: 0      # :20  -> 1  (OVRProjectConfig.FeatureSupport.Supported)
colocationSessionSupport: 0 # :26  -> leave 0, see below
```

`OVRManifestPreprocessor` maps these to permissions: `anchorSupport` enabled →
`com.oculus.permission.USE_ANCHOR_API` (`:759-771`), `sharedAnchorSupport` non-`None` →
`com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA` (`:773-783`). Leave `colocationSessionSupport`
off: it exists for `OVRColocationSession`'s BLE advertise/discover flow
(`OVRColocationSession.cs:178`, `:259`), and this project does not need it — the group UUID rides on
the Relay session that already exists. Turn it on only if you later want headsets to find each other
without a room code.

**`Assets/Plugins/Android/AndroidManifest.xml`** — maintained by hand here (Meta's Manifest Tool
regenerates it and drops the passthrough tag; `MRPassthroughSetup.cs:12-15` says so). Add both next
to the existing permissions at `:9-14`:

```xml
<!-- Meta spatial anchors. Without USE_ANCHOR_API every anchor call fails with
     Failure_SpacePermissionInsufficient; IMPORT_EXPORT_IOT_MAP_DATA is the one sharing needs. -->
<uses-permission android:name="com.oculus.permission.USE_ANCHOR_API" />
<uses-permission android:name="com.oculus.permission.IMPORT_EXPORT_IOT_MAP_DATA" />
```

**`Assets/Editor/MRPassthroughSetup.cs`** — extend it to write both `OVRProjectConfig` fields. That
file exists precisely because `OVRProjectConfig` is a ScriptableObject in the package folder that
cannot be edited as text (`:4-17`), and this is two more `if` blocks in the same shape as the
`insightPassthroughSupport` one at `:50-54`:

```csharp
if (config.anchorSupport != OVRProjectConfig.AnchorSupport.Enabled)
{
    config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
    changed = true;
}
if (config.sharedAnchorSupport != OVRProjectConfig.FeatureSupport.Supported)
{
    config.sharedAnchorSupport = OVRProjectConfig.FeatureSupport.Supported;
    changed = true;
}
```

Bump `AppliedKey` (`:22`) to `.v2` so it re-runs on machines that already have `.v1` in their
`SessionState`.

**`Assets/DefaultNetworkPrefabs.asset`** — add `Room Anchor.prefab` next to the single existing entry
(`:16-22`, `Player.prefab`, guid `9243d030…`). A `NetworkObject` that is not in this list fails to
spawn with a message that does not say so.

**On every headset, once: OS Settings > Privacy and Safety > Device Permissions > Share Point Cloud
Data.** With it off, `ShareAsync` returns `FailureCloudStorageDisabled`
(`OVRAnchor.cs:469-471`); the OS may prompt once per app launch, and if the user declines, sharing
fails for that session with no other signal. This is the single most likely reason a correctly
written §2.6 does nothing on a new headset.

---

## 7. Instrument it before you change anything

You cannot tell §2 working from lucky. `DebugLog` (`:44-70`) already prints to an in-headset box on
the `Debugger` object under `XRRig` (inactive today — `StairsGame.unity`, `Debugger` at
`m_IsActive: 0`; tick it on), cleared with the **right** joystick button, so a probe needs no new UI
and no `adb logcat`.

New file `Assets/Scripts/ColocationProbe.cs`, on `XRRig`, temporary. `fixAnchoring.md` §9 has the
shape; add the two new fields:

```
probe rig.y=… head.y=… aligned=… | anchored=… tracked=… uuid=… recenters=…
      | scale=… holder=… | <name> head.y=… rH.y=…
```

The recenter counter is the important one, and it needs `OVRManager.display.RecenteredPose`
(`OVRDisplay.cs:167`, raised from the per-frame poll at `:150-159`) — null-guard both the subscribe
and the unsubscribe, because `OVRManager.display` is null in the Editor with no headset.

| Reading | Meaning |
|---|---|
| `recenters` ticks and the cones go wrong at the same moment | §1 confirmed. Take this baseline **before** §2 lands. |
| `recenters` ticks and nothing moves | §2 is working. This is the acceptance test. |
| `tracked=False` for more than a moment | That client is not really in the room, or the room is badly mapped. |
| `uuid` differs between two headsets | They are on different anchors. |
| `head.y ≈ 1.36`, or `≈ 2.7`, on a standing adult | `Camera Offset` was never zeroed (§2.4). Every other number is meaningless until this clears. |
| Two headsets on one table read different `rig.y` | The floor-calibration disagreement `MaxAnchorHeightDisagreement` exists to absorb. Measure it before and after. |
| `scale` differs between two headsets while nobody is gesturing | `RoomContent` is not reading `RoomAnchor`, or the lock leaked. |

**Two things to rule out first, because they are free.** Both headsets running the same build — a
stale APK on one makes every number meaningless. And both actually got floor tracking.

---

## 8. Test plan

Two headsets in one room, and they should be a **Quest 2 and a Quest 3** — a matched pair hides the
floor-calibration disagreement §2.4 exists to absorb, and you want to know it is handled. Take the §7
baseline before changing anything.

1. **Anchor placed and shared.** Owner presses `Place Anchor`. Both probes show the same non-empty
   `uuid` and `anchored=True`, `tracked=True`, within a few seconds.
2. **Standing in the same place.** Both users stand shoulder to shoulder at the table. Each sees the
   other's nametag over the other's **real** head and their cones on the other's **real** hands.
   This is the whole of requirement 1 and it either works or it does not.
3. **Cones tip to tip.** (§5.2.) Both touch right controllers. Each sees the other's cone meeting
   their own, from both viewpoints. Then each rotates their wrist through full range — the cone stays
   attached rather than sweeping an arc — and each points a controller **straight down at the board**
   and holds it there. Before §5.2.3, watch the remote cone's roll wander; after, it should not.
4. **Nametags at two heights.** Repeat test 2 with users of noticeably different height, and again
   with both crouched to look under the board. Tags follow the heads; no overlap onto a face.
5. **Head and body.** (§5.1.) Never visible, on any client, owner or remote, aligned or not,
   including the first frame after a player joins. Tick `ShowRemoteHeadAndBody` on one client,
   confirm they come back, untick it.
6. **Forced recenter.** (The single test that proves the design.) User A long-presses the Meta
   button; `recenters` ticks on A's probe. B's view of A must not move, and A's view of the board
   must not move. Without the per-frame loop it jumps and stays wrong.
7. **Headset off and on.** Same expectation. This is the one that happens by accident in a real game.
8. **Untracked anchor.** Cover the cameras or step into an unmapped corridor. `tracked=False`, the
   room holds its last pose rather than snapping, the status appears after `UntrackedWarningSeconds`,
   and it re-acquires with no user action when you walk back.
9. **World grab — move.** (§4.) A squeezes both grips and walks the board onto the real table. B sees
   it move smoothly, in real time, and ends up agreeing where it is. Both then see the board on the
   real table from their own side.
10. **World grab — scale.** A pulls hands apart and together. Uniform, no skew, clamps at both ends
    without the board sliding when the clamp bites. The **players do not scale** — check that B's
    cones are still on B's real hands at 0.3× and at 3×.
11. **World grab — turn.** A rotates the hand axis through more than a full revolution in one
    gesture. The board follows continuously; no 360° snap at the ±180° seam. Then stack the hands
    vertically mid-gesture: the turn pauses, it does not spin.
12. **Two grabbers.** A and B both squeeze both grips at the same moment. Exactly one of them moves
    the board; the other's gesture does nothing at all. Then A holds and B squeezes — B is refused.
13. **Grabber releases on drop.** A holds the world and force-quits or drops wifi. The lock clears
    and B can immediately grab. Then A holds the world and B presses `Stairs` → the scene switches,
    A's gesture ends cleanly, and the lock is not stuck.
14. **Late joiner.** A third player joins after the board has been placed and resized. They see the
    board where it is, at the size it is, on the first frame — not at 1× at the origin, then jumping.
15. **Game switch.** (§2.8.) `Stairs` → `Chasms` and back with both users aligned. Alignment, cones,
    nametags and `uuid` all survive; nobody is teleported to a ring slot; the board placement and
    scale carry over; the room is on the same real table in the new game.
16. **Menu over the board.** (§3.4.) Open the menu with the board placed. The board must still be
    there.
17. **Reconnect.** Wifi off and on. `NetworkReconnectHandler` (`:9-28`) shuts down and restarts the
    client, so the player object respawns and `spawnSlot` is reassigned. Alignment must survive and
    `uuid` must be unchanged.
18. **A player in a different room.** They still get a ring slot, still have locomotion and the
    recentre button, still see two cones and a name per person, read `anchored=False` — and can
    still use the world grab, because it is content-frame and not anchor-dependent.
19. **Editor still runs.** Everything anchor-related must no-op with no HMD: `BoardAnchor` gates on
    an availability check, `HoldAlignment` early-returns on a null `boundAnchor`, and the probe's
    `OVRManager.display` subscription is null-guarded. Two Editor clients also exercise the world
    grab's state machine and its lock on `Q` + `P` — but **not** the gesture itself, because with no
    HMD the hand transforms never move, so span and midpoint are constant. Move the hands by hand in
    the Hierarchy while play mode is running if you want to see the maths run.

---

## 9. Files touched

| File | Change | Section |
|---|---|---|
| `Assets/Scripts/CameraController2.cs` | `AlignRigToAnchor`, `LocalIsAligned`, height guard, rate-limited warning | §2.4 |
| `Assets/Scripts/CameraController2.cs` | Locomotion / recentre / tilt / ring slot gated on `LocalIsAligned` and `WorldGrab.IsActive` | §2.7 |
| `Assets/Scripts/RoomAnchor.cs` | **New.** Anchor identity + content pose + the world-grab lock | §2.5 |
| `Assets/Prefabs/Room Anchor.prefab` | **New.** `NetworkObject` + `RoomAnchor` | §2.5 |
| `Assets/DefaultNetworkPrefabs.asset` | Register `Room Anchor.prefab` (`:16-22`) | §6 |
| `Assets/Scripts/BoardAnchor.cs` | **New.** Spawn `RoomAnchor`; place, share, load, bind, hold every frame | §2.6, §2.8 |
| `Assets/Scenes/OpeningScene.unity` | `BoardAnchor` on the `Network Manager` object; `roomAnchorPrefab` assigned | §2.5 |
| `Assets/Scripts/MenuControl.cs`, `Assets/Prefabs/Menu1.prefab` | `Place Anchor` key and case, gated on `roomOwner` | §2.7 |
| `Assets/Scripts/RoomContent.cs` | **New.** Applies the shared board pose to `World Root` | §3.3 |
| `Assets/Scenes/StairsGame.unity` | `Board` (`:135`) under `World Root` (`:2006`); `RoomContent` on `World Root`; `WorldGrab` on `XRRig`; `Menu Manager.worldRoot` cleared (`:2075`) | §3.2, §3.4, §4.4 |
| `Assets/Scenes/ChasmGame.unity` | Same: `Board` (`:7694`), `World Root` (`:8829`), `worldRoot` cleared (`:9328`) | §3.2, §3.4, §4.4 |
| `Assets/Scripts/WorldGrab.cs` | **New.** The two-grip gesture | §4.4 |
| `Assets/Prefabs/Player.prefab` | Both cones `m_LocalPosition.z` −0.22 → **−0.643** (`:656`, `:775`) | §5.2.2 |
| `Assets/Scripts/PlayerControls.cs` | `lHRot`/`rHRot`/`faceRot` → `NetworkVariable<Quaternion>`; drop the `LookRotation` reconstruction | §5.2.3 |
| `Assets/Scripts/PlayerControls.cs` | Receive-side smoothing on hands and face; null guards at `:281-300`; `[DefaultExecutionOrder(20)]` | §5.2.4, §5.4 |
| `Assets/Scripts/InputReader.cs` | *Optional:* clear the `*Down` flags on release | §4.6 |
| `Assets/Scenes/OpeningScene.unity` | *Optional:* `TickRate` 30 → 60 (`:523`), after measuring | §5.2.4 |
| `Assets/Oculus/OculusProjectConfig.asset` | `anchorSupport` (`:19`), `sharedAnchorSupport` (`:20`) | §6 |
| `Assets/Plugins/Android/AndroidManifest.xml` | Two `uses-permission` entries | §6 |
| `Assets/Editor/MRPassthroughSetup.cs` | Write both config fields; bump `AppliedKey` (`:22`) | §6 |
| `Assets/Scripts/ColocationProbe.cs` | **New**, temporary — delete when the numbers are known | §7 |

**Landing order.** §6 first — nothing works without the permissions and it is half an hour. Then §7,
so you have a baseline. Then §2 as one reviewable change, because per-frame alignment is one idea and
half of it is worse than none. Then §3 and §4 together — the content root and the gesture are the same
feature. §5.2.2, §5.2.3 and §5.2.4 are three small independent commits and want to be separate, so a
regression has one suspect.

---

## 10. Things not to do

- **Do not build a one-shot alignment and fix it later.** §2.3. That path was already walked once on
  the project this one was forked from, and `fixAnchoring.md` exists because of it. Per-frame is
  cheaper than it sounds: `OVRSpatialAnchor.Update()` is already making the `TryLocateSpace` call
  whether you read the result or not.
- **Do not add a timer that re-aligns every N seconds.** It is the same one-shot with more chances to
  be wrong, and it makes the board lurch on a schedule.
- **Do not turn off `OVRManager.AllowRecenter` to stop the recenters.** It suppresses the *app*
  applying them; it does not stop the runtime moving the tracking origin (`OVRDisplay.cs:150-159`
  fires regardless), and it breaks the user's ability to recentre deliberately.
- **Do not scale the rig to resize the world.** It is the obvious implementation and it is wrong
  here: scaling the `XRRig` scales the tracked controller poses, so the virtual hands walk away from
  the real hands you can see through passthrough — and it breaks requirement 3 on the exact gesture
  meant to satisfy requirement 5.
- **Do not put the players under `World Root`.** It is what makes "apart from other players" free.
  Nametags would scale, cones would scale, and the ring would scale with them.
- **Do not reparent the content to move it.** §4.1. The ancestor did, and paid for it with a
  per-child transform readback and accumulated float error.
- **Do not take pitch or roll from the anchor, or from the hand axis.** §2.4 and §4.3. Both would tip
  a board that is sitting on a real table.
- **Do not re-baseline the gesture every frame.** §4.3. Capture once, at the start, and use the
  clamped ratio for position.
- **Do not let two clients write the content pose.** §4.5. It is not a race that resolves; it is a
  board that vibrates for as long as both people hold on.
- **Do not fall back to "whatever anchor came back" when the published UUID is missing.** A client on
  the wrong anchor looks like working software, and looking like working software is worse than
  failing — a client that fails to align still gets a ring slot and behaves correctly as a remote
  player.
- **Do not let `BoardAnchor` or the `Room Anchor Point` GameObject live in a game scene.** §2.8. They
  will be destroyed on the next `Stairs` → `Chasms` press and the room will silently stop being
  anchored.
- **Do not delete `mainFace` or `tornado` from the prefab.** §5.1 — `face` is dereferenced unguarded
  every frame.
- **Do not measure any of §5.2 before §2 works.** The frame error is metres and everything else is
  centimetres.
