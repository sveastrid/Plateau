# BASHRulesUpdate.md — the spinning aim and the artillery arc

> **Status: applied in code, not yet verified on a headset.** Every change in §§2–9 is written and
> the project compiles clean. None of §10 has been run — the ordering test (step 3), the wall
> regression (step 5) and the world-grab acceptance test (step 8) are all still outstanding, and
> `blastRadius`, `arcLifetime` and `arcMaxApex` are untuned guesses until somebody judges them by
> eye. §9's `CLAUDE.md` row is deliberately still undone for the same reason.

BASH as ported ([`BASHUpdate.md`](BASHUpdate.md)) gives all four gamepieces the same verb: aim with
the right joystick, hold the left trigger, a tube of geometry grows out of the cannon and steers,
release and the piece teleports to the end of it. What it hits on the way kills.

This document splits that verb in two.

- **Helicopter and sub** keep the one-shot line, but you no longer aim it. The piece spins on the
  spot, carrying its cannon dot around with it, and the trigger press is what freezes the heading
  and launches. Aiming becomes timing.
- **Boat and plane** get a two-part turn: first an **artillery arc** lobbed over the water, aimed
  with the joystick as today, which destroys whatever it lands on; then a **movement line** aimed
  the same spinning way as the helicopter's, which carries the piece but harms nothing.

Both end by **deselecting the piece**, so nothing is left spinning on the board after a player has
acted.

Read [`CLAUDE.md`](../CLAUDE.md)'s *The BASH game* first, and §5 of [`BASHUpdate.md`](BASHUpdate.md) —
everything below is measured in `Bash Root`'s local space for the reason stated there, and getting
that wrong is the failure this whole port was built to avoid.

---

## 0. The short answers

**What is "the dot"?** Each gamepiece's fourth child, named `Cannon`: a `PipeRenderer` with
`positions [(0,0,0), (0,0,0.01)]`, `startWidth 0.0075` and `autoCreateCollider 0`, sitting at local
`(-0.005, 0, 0.04)` — a 1 cm nub just in front of the hull, coloured with the owner's material by
`NetworkBaseControl.ChangeMaterial`. It has no transform of its own that moves; it swings because
**the whole piece rotates**, driven by `NetworkBaseControl.activeRot`. That is why "the dot rotates
around the piece" and "the piece spins" are the same change.

**Decisions taken** (asked and answered before this document was written):

| | Decision |
| --- | --- |
| Turns | **Deselect only.** BASH stays free-for-all: any player may act at any time. "The other player's turn" is the piece going idle, not an enforced order. No new turn state, no active-seat `NetworkVariable`. |
| Arc hit rule | **Impact point, anyone.** At release, every gamepiece within a blast radius of where the arc lands dies — yours, a teammate's, an enemy's. The arc passes harmlessly over walls and islands; a miss does nothing. |
| Steering | **Both still steer.** The joystick keeps bending the movement line while it grows (unchanged) *and* keeps turning the arc while its range grows. The spin sets the **initial** direction only. |
| What spins | **The whole piece.** Reuses `activeRot` exactly as it works today: one heading, one code path, one thing on the wire. |

**Everything else about BASH is unchanged**, and that is worth stating because it constrains the
work: the three collision rules, the teleport-to-the-end-of-the-line movement, seats from
`PlayerControls.spawnSlot`, `Reset Game`, `Random Islands`, the board frame and every conversion
through `BashRoot`.

---

## 1. What changes, per piece

| | Boat (0) | Plane (1) | Sub (2) | Helicopter (3) |
| --- | --- | --- | --- | --- |
| **Today** | aim with joystick → hold trigger → lethal line → teleport | same | same | same |
| **After** | joystick-aimed **arc**, then spin-aimed **harmless** line → teleport → deselect | same as boat | spin-aimed **lethal** line → teleport → deselect | same as sub |

Two pieces get one phase, two get two. The trigger sequence for a boat or plane is therefore
**press–release, press–release**: the first pair lobs the shell, the second pair moves the piece.

---

## 2. The two-piece groupings are not the same two pieces

`LineControls.cs:67-79` already splits the four pieces in half:

```csharp
// Boats and subs are on the water; planes and helicopters are over it.
if (activeGamepieceNum == 0 || activeGamepieceNum == 2)
```

That is **boat + sub**. The split this document adds is **boat + plane** — the two that lob rather
than shoot straight. Same four indices, two different pairings, both written as bare integer
comparisons, three lines apart in the same file if you are not careful.

**Give both a name and never inline the test again:**

```csharp
// BashRoot.cs, beside the other shared BASH facts.
public const int Boat = 0, Plane = 1, Sub = 2, Helicopter = 3;

/// <summary>Boat and plane lob a shell before they move. Sub and helicopter shoot as they move.</summary>
public static bool UsesArtillery(int kind) => kind == Boat || kind == Plane;

/// <summary>Boat and sub are on the water, so an island kills them. Plane and helicopter are over it.</summary>
public static bool IsSurfaceCraft(int kind) => kind == Boat || kind == Sub;
```

Then rewrite `LineControls`'s island branch as `if (BashRoot.IsSurfaceCraft(ActivePieceIndex()))`.
It is the same behaviour; the point is that the next person to read the file cannot mistake one
pairing for the other, and cannot "tidy" them into agreement.

---

## 3. The state machine

`ControlListener.Update()` today is a flat chain of `if`s over the trigger edges
(`ControlListener.cs:50-104`). With two phases for two of the pieces that stops being readable, and
worse, stops being *checkable* — several of the new rules are about which phase the joystick belongs
to. Make the state explicit.

```csharp
enum Phase
{
    Idle,       // nothing selected
    Aiming,     // arc piece selected: the joystick aims, waiting for trigger down
    Lobbing,    // the arc is growing while the trigger is held
    Spinning,   // the heading is auto-rotating, waiting for trigger down
    Firing      // the movement line is growing while the trigger is held
}

Phase phase = Phase.Idle;
```

Transitions, complete:

| From | On | To | Does |
| --- | --- | --- | --- |
| `Idle` | right trigger down on one of your pieces | `Aiming` or `Spinning` | `ChangeGamePiece`, then `UsesArtillery(kind) ? Aiming : Spinning` |
| `Aiming` | `rightJoystick.x` | `Aiming` | today's `activeRot` rotation, unchanged |
| `Aiming` | left trigger **down** | `Lobbing` | `BeginArc` |
| `Lobbing` | left trigger **held** | `Lobbing` | `ExtendArc` — range grows, joystick turns the heading |
| `Lobbing` | left trigger **up** | `Spinning`, or `Idle` if the shooter killed itself | `ResolveArc` |
| `Spinning` | *(joystick is ignored — the spin **is** the aim)* | | |
| `Spinning` | left trigger **down** | `Firing` | `FreezeSpin`, then `BeginShot` |
| `Firing` | left trigger **held** | `Firing` | `ExtendShot`, unchanged |
| `Firing` | left trigger **up**, or `LineControls` ending the shot | `Idle` | `EndCannonLine`, then **deselect** |
| *any* | `!Playable()`, or the piece dies under you | back to the **start of the current phase** | `CancelShot` |

A piece that is re-selected mid-sequence (pressing a different own piece) starts over at its own
first phase. A boat that cancelled its lob returns to `Aiming` and may lob again; a boat that
cancelled its *move* returns to `Spinning` — it has already spent its shell and does not get another.
Storing the phase on the controller, not on the piece, is what makes that fall out: `CancelShot`
clears the geometry and leaves `phase` alone.

**Entering `Spinning` is the only new thing `ChangeGamePiece` has to trigger.** It already returns
early for a null, inactive or foreign piece (`NetworkBaseControl.cs:315-330`), so
`ControlListener` cannot read "the selection succeeded" from the call. Have `ChangeGamePiece` return
`bool`, and set the phase from that.

---

## 4. The spinning aim

### Why it is not a per-frame `activeRot` write

The naive version — rotate `activeRot.Value` a little every frame while `Spinning` — pushes a
`NetworkVariable` delta every network tick (30 Hz) for as long as any piece anywhere is selected,
for something that is pure anticipation. Instead, **publish the parameters once and let every client
derive the angle from the server clock.** The spin costs one write per selection, and every headset
draws the dot in the same place because they are all evaluating the same formula against the same
`NetworkManager.ServerTime`.

On `NetworkBaseControl`, beside `activePos`/`activeRot` (`NetworkBaseControl.cs:23-24`), and
owner-written for the same reason they are:

```csharp
/// <summary>Server time the spin began. The heading is derived from it, so the spin costs one
/// write per selection rather than one per network tick.</summary>
public NetworkVariable<double> spinStartTime =
    new NetworkVariable<double>(0d, NetworkVariableReadPermission.Everyone,
                                    NetworkVariableWritePermission.Owner);

public NetworkVariable<bool> spinning =
    new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone,
                                     NetworkVariableWritePermission.Owner);

/// <summary>A full sweep every four seconds. Shared, not per-base: every client derives the
/// heading from it, so a value that differed between headsets would put the dot in a
/// different place on each.</summary>
public const float SpinDegreesPerSecond = 90f;
```

`activeRot` keeps its meaning: the **committed** board-local heading. While `spinning` is true the
displayed heading is `activeRot` rotated by the elapsed angle; when the trigger freezes it, the
derived heading is written *into* `activeRot` and `spinning` goes false, so everybody snaps to
exactly the heading the shooter saw.

```csharp
void Update()
{
    if (!spinning.Value || activeGamepiece == null) { return; }

    NetworkManager nm = NetworkManager.Singleton;
    if (nm == null) { return; }

    float angle = (float)(nm.ServerTime.Time - spinStartTime.Value) * SpinDegreesPerSecond;
    Look(activeGamepiece.transform, BashRoot.RotateInXZ(activeRot.Value, angle));
}

public void StartSpin()
{
    if (!IsOwner) { return; }
    spinStartTime.Value = NetworkManager.Singleton.ServerTime.Time;
    spinning.Value      = true;
}

/// <summary>Commit the spun heading. Runs before the shot samples the muzzle, because
/// activeRot's OnValueChanged fires synchronously on the writer and is what turns the piece.</summary>
public void FreezeSpin()
{
    if (!IsOwner || !spinning.Value) { return; }
    float angle = (float)(NetworkManager.Singleton.ServerTime.Time - spinStartTime.Value)
                  * SpinDegreesPerSecond;
    activeRot.Value = BashRoot.RotateInXZ(activeRot.Value, angle);
    spinning.Value  = false;
}
```

`NetworkBaseControl` has no `Update()` today; this adds one. It is guarded by `spinning.Value` on
its first line, so a base with nothing selected costs a bool read per frame.

### Three details that will bite

- **`RotateVectorInXZ` is currently private on `ControlListener` (`ControlListener.cs:329-339`).**
  Two files need it now. Move it to `BashRoot` as `public static Vector3 RotateInXZ(...)` — that
  file is already the one place BASH's frame maths lives — and delete the copy. Two copies of a
  rotation helper that must agree exactly is how the dot ends up somewhere different on each
  headset.
- **`FreezeSpin()` must run before the muzzle is sampled.** `BeginShot` reads
  `activeGamepiece.transform.GetChild(3).position` (`ControlListener.cs:128`). Netcode raises
  `OnValueChanged` synchronously on the writer, so writing `activeRot` in `FreezeSpin` turns the
  piece in the same statement, and the muzzle read that follows is already correct. Reverse the two
  and every shot leaves from one frame's worth of rotation behind where the player aimed.
- **`BeginShot` should take its direction from `activeRot.Value`, not from
  `ToLocalDirection(transform.forward)`.** Both are correct after the freeze, but the round trip
  through the transform re-normalises through the content scale for no reason, and the whole point
  of the freeze is that there is one authoritative heading.

### The cheap fallback

If the server-time derivation turns out to be more than it is worth, the local-only version is
five lines: the owner rotates `activeGamepiece.transform` directly each frame and publishes nothing
until the freeze. The cost is that remote players see a stationary piece that snaps and fires —
they lose the tension of watching somebody's aim come round, which is most of the reason for the
mechanic. Take the fallback only if the replicated version misbehaves.

---

## 5. The parabola

### Growth: share the integrator, do not copy it

"The same acceleration as the lines" is satisfied literally, by making the arc's range use the
identical accumulator the straight line's tip uses (`ControlListener.cs:135-143`):

```csharp
arcRange += currentCannonSpeed * Time.deltaTime;
currentCannonSpeed += cannonAcceleration;
```

One field, `currentCannonSpeed`, reset to `initialCannonSpeed` at the start of either kind of shot.
Two separate copies of the same two lines would be free to drift apart the first time anybody tunes
one of them.

> **Inherited quirk, worth knowing before you tune anything.** `cannonAcceleration` is added **per
> frame**, not per second, so growth is frame-rate dependent: in one second a shot covers about
> **4.05** board-local units at 72 fps and **4.95** at 90 — 22 % further on a faster headset, on a
> 3 m board. This is BASH's behaviour today and this document does not change it. If you do fix it
> (`+= cannonAcceleration * Time.deltaTime`), fix it in the one shared line so both shot types move
> together, and expect to raise `cannonAcceleration` from `0.1` to about `7` to keep the same feel.
>
> The practical consequence for the arc: a shot crosses the whole board in well under a second, so
> the range clamp below is not an edge case — it is reached on any deliberate shot.

### Shape

Sampled fresh every frame from three numbers — origin, heading, range — rather than accumulated
point by point:

```csharp
public float arcApexRatio = 0.25f;  // apex = ratio x range. 0.25 is exactly a 45 deg launch:
                                    // the launch angle of this curve is atan(4 * apex / range).
public float arcMaxApex   = 0.35f;  // board-local ceiling. Without it a full-board lob peaks
                                    // 0.75 m over the table, which in passthrough is at chest
                                    // height and reads as a wall, not an arc.
public int   arcSegments  = 24;
public float minArcRange  = 0.02f;

Vector3[] SampleArc()
{
    float apex = Mathf.Min(arcApexRatio * arcRange, arcMaxApex);
    Vector3[] pts = new Vector3[arcSegments + 1];
    for (int i = 0; i <= arcSegments; i++)
    {
        float t = (float)i / arcSegments;
        pts[i] = arcOrigin
               + arcHeading * (arcRange * t)
               + Vector3.up * (4f * apex * t * (1f - t));   // 4t(1-t) peaks at 1 when t = 0.5
    }
    return pts;
}
```

`Vector3.up` is board-local +Y here, which is what makes the arc sit above the water however the
board has been turned. The water surface is board-local `y = 0` (`Water` is at `y = -0.01` with a
`0.02` scale) and the muzzle is about `y = 0.008`, so the arc leaves and lands at piece height,
which is exactly the height the impact test wants.

**A fixed segment count, not one point per frame,** and it is worth being explicit about why,
because it makes the arc strictly better behaved than the line it sits beside:

- `PipeRenderer.SetPositions` rebuilds the **entire** mesh from the **entire** point list on every
  call. A list that grows by one point per frame is quadratic work over a shot; 25 points rebuilt
  each frame is constant. `CLAUDE.md` already flags long shots as a rough edge — do not add a
  second one.
- `SpawnNetworkCannonLineServerRpc` sends an unbounded `Vector3[]` — the other flagged rough edge.
  25 points is 300 bytes, bounded for ever, whatever the board is scaled to.

**Guard the degenerate first frame.** `PipeRenderer.GenerateCylinder` takes
`Vector3.Cross(Vector3.up, positions[1] - positions[0])` and `Quaternion.FromToRotation` on the same
difference (`PipeRenderer.cs:94-97`). At `arcRange == 0` every sample is the same point, the cross
product is zero and the mesh comes out degenerate or NaN with no exception. Do not call
`SetPositions` until `arcRange > minArcRange`.

### Keep the shell on the table

The chosen hit rule ignores walls, which is the point of lobbing — but taken literally it also lets
a held trigger throw the impact point off the board and into the room behind a player. Clamp the
range so the landing point stays inside the wall rectangle (board-local `|x|, |z| <= 1.5`):

```csharp
/// <summary>How far the arc may reach before its landing point would leave the walls. A 2D slab
/// test against the board rectangle; the shell stops at the far wall rather than sailing over it
/// into somebody's living room.</summary>
static float RangeToBoardEdge(Vector3 origin, Vector3 heading, float half = 1.5f)
```

Clamp `arcRange` to it each frame. Holding the trigger past that point simply does nothing more,
which is legible: the arc stops growing at the far wall. Expose it as `clampArcToBoard` so it can be
switched off for testing.

### The preview object

Reuse `CannonLine.prefab` — no new prefab, and therefore nothing to add to
`DefaultNetworkPrefabs.asset`. That matters: `NetworkConfig.ForceSamePrefabs` is `1`, so touching
that list is a hard connection failure with a generic error for any headset still on an older build.

Set two flags on the instance at `BeginArc` and the same object serves as an arc:

```csharp
line.GetComponent<PipeRenderer>().autoCreateCollider = false;   // visual only
line.GetComponent<LineControls>().checkForCollisions = false;   // the hit test is explicit
```

`autoCreateCollider = false` is not just tidiness — the straight line assigns a **non-convex**
`MeshCollider.sharedMesh` every frame while it grows, and the arc has no reason to pay for that
since nothing collides with it.

**Do not call `TurnOffMyColliders()` in `BeginArc`.** `BeginShot` calls it so a line cannot hit the
pieces it left from (`ControlListener.cs:111`); the arc has no live collider to protect against, and
friendly fire is on, so the impact overlap in §6 has to be able to *see* the shooter's own base.
Disabling them here would silently make your own base the one thing a shell cannot hit.

---

## 6. Resolving the impact

On trigger up, in this order:

```csharp
void ResolveArc()
{
    Vector3[] pts = SampleArc();
    Vector3 impactLocal = pts[pts.Length - 1];

    if (netSpawnManager != null)
    {
        netSpawnManager.SpawnNetworkCannonLine(pts, arcLifetime);   // see below
    }

    // World Root is still being smoothed toward its target pose by RoomContent, and
    // m_AutoSyncTransforms is 0 in this project, so without this the overlap reads collider
    // poses from the last FixedUpdate. Same reason PointerBeam does it.
    Physics.SyncTransforms();

    Vector3 impactWorld = BashRoot.ToWorldPoint(impactLocal);

    // blastRadius is board-local like everything else here, so it has to be taken through the
    // content scale by hand - OverlapSphere takes a world radius. World Root's scale is
    // uniform (RoomContent.cs:75), so lossyScale.x is the whole story.
    float radius = blastRadius * BashRoot.Frame.lossyScale.x;

    // Gamepiece colliders are TRIGGERS. QueryTriggerInteraction.Collide is mandatory; the
    // default would find nothing at all, silently, and the arc would never kill anybody.
    Collider[] hits = Physics.OverlapSphere(impactWorld, radius,
                                            Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Collide);

    for (int i = 0; i < hits.Length; i++)
    {
        if (!hits[i].CompareTag("gamepiece")) { continue; }

        // Same walk LineControls does: a gamepiece is a direct child of its base.
        Transform parent = hits[i].transform.parent;
        NetworkBaseControl victim = parent != null
            ? parent.GetComponent<NetworkBaseControl>() : null;
        if (victim != null)
        {
            victim.TurnOffGamepiece(hits[i].transform.GetSiblingIndex());
        }
    }
}
```

`blastRadius` starts at `0.08` board-local — roughly the width of a gamepiece — and wants judging by
eye once it is on a headset. It is the single number that decides whether the arc feels like
artillery or like a dart.

**No owner filter.** Friendly fire is on by decision, so a shell that lands on your own base kills
what is there, including the piece that fired it. That is a real own goal rather than a free
no-op, and `minArcRange` already stops a mis-tapped trigger from being one. If it plays badly, the
one-line reversal is to skip colliders whose `NetworkBaseControl == netBaseControl`.

**If the shooter killed itself, there is no movement phase.** After the loop:

```csharp
if (netBaseControl.activeGamepiece == null ||
    !netBaseControl.activeGamepiece.activeInHierarchy)
{
    Deselect();
    phase = Phase.Idle;
    return;
}
phase = Phase.Spinning;
netBaseControl.StartSpin();
```

`TurnOffGamepiece` does `SetActive(false)` rather than destroying, so the reference survives and
`activeInHierarchy` is the test — the same trap `ChangeGamePiece` already documents
(`NetworkBaseControl.cs:317-321`).

### The arc's replicated record

Unlike a movement line, an arc does not describe where a piece went, so it should not stay on the
board — and `CLAUDE.md` already flags trails growing without bound as a known rough edge. Give the
arc a lifetime instead of doubling that growth:

- `SpawnManager.SpawnNetworkCannonLine(Vector3[] points, float lifetime = 0f)`, forwarded to the
  ServerRpc. `0` means "for ever", which is what the existing straight-line call passes and what
  keeps its behaviour identical.
- On the server, after `netLine.Spawn(true)` and the `TrySetParent`, start a coroutine that waits
  `lifetime` seconds and despawns — guarded on `netObj != null && netObj.IsSpawned`, because
  `Reset Game` (`NetworkBaseControl.DeleteAllLinesServerRpc`) can get there first.
- `arcLifetime` on `ControlListener`, default `1.5f`. Long enough that everybody round the table
  sees where the shell came from and where it landed, short enough that a long game does not fill
  with arcs.

The despawn path is new; nothing in BASH despawns a trail except `Reset Game`. It is about ten
lines and it is the only piece of network lifecycle this document adds.

---

## 7. The movement line, and making it harmless

The boat's and plane's second phase is `BeginShot`/`ExtendShot`/`EndCannonLine` unchanged, with one
difference: it must not kill other people's pieces.

That is one flag on `LineControls`, not a new component:

```csharp
/// <summary>False for the movement half of a boat's or plane's turn: it still stops on a wall
/// and still drowns a surface craft on an island, it just does not take anybody else with it.</summary>
public bool killsPieces = true;
```

and in `OnTriggerEnter` (`LineControls.cs:50-58`):

```csharp
if (other.gameObject.tag == "gamepiece")
{
    if (!killsPieces) { return; }
    ...
}
```

**Only the `gamepiece` branch is gated.** The `obstacle` and `island` rules stay live for a movement
line — otherwise a boat drives through a wall and a plane's harmless move becomes a way to park
inside an island. "They just don't destroy other pieces if they hit them when moving" is exactly
that one branch and no more.

Set it at `BeginShot` from the phase: `killsPieces = !BashRoot.UsesArtillery(kind)`. The sub and
helicopter, which have no arc, keep `true` and are unchanged in every respect.

---

## 8. Deselecting, and the ordering bug it introduces

Deselection is not just `SetActiveGamepiece(-1)` — that call does not touch the selection ring
(`NetworkBaseControl.cs:224-241`; `ChangeGamePiece` turns the ring off separately). One helper, on
`NetworkBaseControl`:

```csharp
public void Deselect()
{
    if (activeGamepiece != null)
    {
        TurnOffSelectionRing(activeGamepiece.transform.GetSiblingIndex());
    }
    if (IsOwner) { spinning.Value = false; }
    SetActiveGamepiece(-1);
}
```

Call it at the end of `EndCannonLine`, **after** the pose is published. `activePos.Value = ...`
raises `OnValueChanged` synchronously on the writer and that callback is what teleports the piece
(`NetworkBaseControl.cs:33-45`); deselecting first would null `activeGamepiece` and the piece would
stay where it was.

### `LineControls` must capture the piece index before it ends the shot

This is the one place where adding auto-deselect breaks something that works today, and it breaks
it silently.

`LineControls.OnTriggerEnter` currently calls `control.EndCannonLine()` and *then* `KillActivePiece()`
(`LineControls.cs:59-79`), and `KillActivePiece` reads the index off
`myNetBaseControl.activeGamepiece` (`LineControls.cs:82-101`). Once `EndCannonLine` deselects,
`activeGamepiece` is null by the time `KillActivePiece` runs, `ActivePieceIndex()` returns `-1`, and
**hitting a wall stops killing you** — with no error, in the one rule that punishes bad shots.

Capture first:

```csharp
else if (other.gameObject.tag == "obstacle")
{
    // Read the index BEFORE EndCannonLine: it deselects the piece now.
    int n = ActivePieceIndex();
    if (control != null) { control.EndCannonLine(); }
    KillPiece(n);
}
```

`KillPiece(int n)` replaces `KillActivePiece()`, keeps the `n < 0` guard (a second `OnTriggerEnter`
in the same frame, in the corner where two walls meet), and does the same two calls. The island
branch takes the same treatment.

### What else deselect has to leave clean

- `CancelShot` does **not** deselect — the menu opening mid-shot should not cost you your piece. It
  clears the geometry, re-enables the colliders and leaves `phase` alone. Add the arc preview to
  what it destroys.
- The hover ring on `GetChild(1)` is switched off by `activePos.OnValueChanged`, which now fires on
  every single shot rather than occasionally. That is the right direction, but `pointerControl` gets
  no `OnTriggerExit` while a collider is disabled and `EndCannonLine` re-enables them
  (`TurnOnMyColliders`) a statement later — watch for a hover ring left on after a teleport, and if
  it appears, clear `pointer.currentGamepiece` in `Deselect` rather than chasing it in
  `pointerControl`.

---

## 9. File by file

| File | Change |
| --- | --- |
| `Assets/Scripts/Bash/BashRoot.cs` | `RotateInXZ` moved in from `ControlListener`; the four piece-kind constants; `UsesArtillery` / `IsSurfaceCraft`. |
| `Assets/Scripts/Bash/ControlListener.cs` | The bulk. `Phase` enum and the §3 transitions; `BeginArc` / `ExtendArc` / `SampleArc` / `RangeToBoardEdge` / `ResolveArc`; `Deselect` at the end of `EndCannonLine`; the joystick block at `ControlListener.cs:66-72` becomes phase-dependent; `CancelShot` learns about the arc; `RotateVectorInXZ` deleted. New tunables: `arcApexRatio`, `arcMaxApex`, `arcSegments`, `minArcRange`, `blastRadius`, `arcLifetime`, `clampArcToBoard`. |
| `Assets/Scripts/Bash/NetworkBaseControl.cs` | `spinStartTime`, `spinning`, `SpinDegreesPerSecond`; `Update()`; `StartSpin` / `FreezeSpin` / `Deselect`; `ChangeGamePiece` returns `bool`. |
| `Assets/Scripts/Bash/LineControls.cs` | `killsPieces` flag gating only the `gamepiece` branch; `KillActivePiece` → `KillPiece(int)`; both call sites capture the index first; island test via `BashRoot.IsSurfaceCraft`. |
| `Assets/Scripts/Bash/SpawnManager.cs` | `SpawnNetworkCannonLine` takes a `lifetime`; a server-side timed despawn. |
| `Assets/Scripts/Bash/PipeRenderer.cs` | **No change.** |
| `Assets/Scripts/Bash/IslandManager.cs` | **No change.** |
| `Assets/Scripts/pointerControl.cs` | **No change** unless the stale hover ring in §8 turns up. |
| `docs/CLAUDE.md` | *The BASH game* → *Interaction* needs rewriting once this is applied, and a row in the design-docs table. Leave both until it is actually working. |

**No Editor, prefab or scene work at all**, and that is deliberate rather than lucky: reusing
`CannonLine.prefab` for the arc keeps `DefaultNetworkPrefabs.asset` untouched, and with
`ForceSamePrefabs: 1` an untouched list is the difference between "old headsets see a stale game"
and "old headsets cannot connect". Nothing here needs a new tag, a new menu key, a
`GlobalObjectIdHash`, or a rebuild of anybody else's APK to test against.

---

## 10. Verification

In order, because each failure masks the next. Steps 1–7 are one Editor client; 8–9 need two.

1. **Compiles clean**, no Safe Mode. (`CS0619 … instanceId is obsolete` is the unrelated Meta SDK
   bug — run `powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1`.)
2. **Select a helicopter.** It starts turning on the spot at 90°/s, the coloured dot sweeping round
   it. The joystick does nothing.
3. **Left trigger down.** The spin stops dead and the line leaves the dot **in the direction the dot
   was pointing at that instant** — not one frame behind it, and not from the pre-spin heading.
   This is the §4 ordering test.
4. **Hold, steer, release.** The line still bends with the joystick and the piece still teleports to
   its end. Then the piece is **deselected**: no selection ring, nothing spinning.
5. **Helicopter into a wall.** Your piece still dies. This is the §8 regression test and it is the
   one that will be silently wrong.
6. **Select a boat.** It does *not* spin; the joystick aims it as before. Hold the trigger: an arc
   rises over the water, grows, and turns with the joystick. It stops growing at the far wall
   rather than leaving the board.
7. **Release over another piece** → that piece dies, the arc lingers about a second and vanishes,
   and the boat then starts spinning for its move. Release over open water → nothing dies, same
   otherwise. Drive the resulting movement line **through** an enemy piece: it must survive. Drive
   it into a wall: the boat must still die. Drive it onto an island: the boat drowns, and a plane
   in the same place does not.
8. **World grab, then all of the above again.** Move, turn and resize the board with both grips,
   then fire. The arc must leave the cannon, keep its shape, and land where the blast actually
   goes off — a shell that kills at 1× and misses at 3× means `blastRadius` was not taken through
   `lossyScale`. This is §11.5 of `BASHUpdate.md` extended to the new geometry and it is the
   acceptance test for the whole document.
9. **Two clients.** The spin is visible on the other headset, at the same angle, and the dot is in
   the same place when it freezes. A kill by arc shows on both. `Reset Game` still clears
   everything, including an arc mid-flight.
10. **Two headsets, one room.** `Place Anchor`, then `ColocationProbe`: recenters ticks and nothing
    moves. Unchanged by this work, but it is what the shared frame is for.

---

## 11. Risks and things deliberately not done

- **The spin's replication depends on `ServerTime` agreeing.** If two headsets disagree about
  `NetworkManager.ServerTime.Time` by more than a few tens of milliseconds, their dots are in
  visibly different places — at 90°/s, 50 ms is 4.5°. The freeze snaps everybody to the same
  `activeRot`, so it is cosmetic and self-correcting, but if it looks bad the fallback in §4 is the
  answer, not a re-derivation.
- **Frame-rate-dependent acceleration is left in place** (§5). It is BASH's behaviour and fixing it
  changes the feel of every shot, which is a tuning session, not a code change. Flagged so it is a
  decision rather than a surprise.
- **Nothing is turn-based.** Deselection is the whole of the "turn" in this document. If enforced
  turn order is wanted later it is a server-owned active-seat `NetworkVariable` on
  `Room Anchor.prefab` beside the others, plus a guard in `Playable()` — a self-contained addition
  that none of this work forecloses.
- **A piece can still be selected while somebody else's shot is in the air.** Free-for-all, by
  decision.
- **Spectator seats (4–11) stay untested**, as they are today. Every new path here goes through
  `netBaseControl`, which is null for them, so keep the existing null tolerance rather than assuming
  a base.
- **The arc has no impact effect** — no splash, no flash, nothing but the piece disappearing.
  `CannonHit.mat` exists in `Assets/Materials/Bash/` and is unused; that is the obvious hook if the
  hit needs to read more clearly, and it is out of scope here.
