# Converting Math_Classroom_v2 to Mixed Reality (Passthrough)

**Goal:** replace the space skybox with the user's real room. Students and the teacher see the
3D drawings, function surfaces and each other's avatars floating in their own physical space
instead of in orbit around Jupiter.

**Audience:** the developer doing the work. Everything below references real files, real current
values, and the specific places in this codebase that will break.

**Status:** applied on branch `mr-passthrough` (commit "Convert background to mixed reality").
Decisions A/B/C all took the recommended option: Unity 6 (6000.5.4f1), Route A, both scenes MR.
Everything through §9 is in the repo. Outstanding: the three Editor-only steps in §14, and all
of §10 — **none of this has been run on a headset yet.**

---

## 0. The one thing to understand before starting

**Deleting the skybox does not give you passthrough. It gives you a black background.**

Passthrough on Quest is not a Unity render feature — it is a *compositor layer* owned by the
Meta runtime. Your app renders its content with a **transparent background**, and the runtime
composites the camera feed *underneath* it. So the work is really three things:

1. Get an SDK into the project that can talk to the passthrough compositor (the Unity Oculus XR
   Plugin currently in this project **cannot** — see §2).
2. Make the app render a genuinely transparent background — this is where most of the failures
   happen, and this project currently has three separate settings that silently destroy the
   alpha channel (§5).
3. Deal with the fact that the app's spatial design assumes an infinite empty void, and a real
   room is neither infinite nor empty (§6).

Step 3 is the one that takes real time. Steps 1 and 2 are a day; step 3 is a design decision.

---

## 1. Decisions to make before writing any code

These three choices determine everything downstream. Make them first.

### Decision A — Unity Editor version

The project is pinned to **Unity 2023.1.10f1** (`ProjectSettings/ProjectVersion.txt`). That is a
Tech Stream release, not LTS, and it is **outside Meta's supported Unity matrix** for current Meta
XR SDK versions (they support 2021.3 LTS, 2022.3 LTS, and Unity 6 LTS).

| Option | Cost | Recommendation |
| --- | --- | --- |
| Stay on 2023.1.10f1, use an era-matched Meta XR SDK (v57–v60, late 2023) | Low upfront, unsupported config, no Quest 3 feature parity, dead end | Only if this is a short-lived demo |
| Upgrade to **Unity 6 LTS (6000.0.x)** | Highest upfront (URP 17 migration, package churn) | **Recommended** — supported, current, and where all Meta docs point |
| Upgrade to 2022.3 LTS | Moves *backwards* in version number, still supported | Fallback if the Unity 6 upgrade fights you |

Whatever you choose, **do the Editor upgrade as its own commit, verify the app still builds and
runs in VR unchanged, and only then start the MR work.** Do not combine the two — if the build
breaks you will not know which change did it.

> Verify current Meta XR SDK version requirements against Meta's docs before committing —
> the support matrix moves and the numbers above will age.

### Decision B — passthrough SDK route

| | **Route A — Meta XR Core SDK** (recommended) | **Route B — OpenXR + AR Foundation** |
| --- | --- | --- |
| Packages | Add `com.meta.xr.sdk.core`; keep `com.unity.xr.oculus` loader | Add `com.unity.xr.openxr` + `com.unity.xr.meta-openxr`; upgrade AR Foundation 5.0.7 → 5.1.x; upgrade XR Management 4.0.1 → 4.4+; **swap the active loader** |
| Passthrough API | `OVRManager.isInsightPassthroughEnabled` + one `OVRPassthroughLayer` component | `ARSession` + `ARCameraManager` + `ARCameraBackground`, plus an `XROrigin` rig |
| Impact on existing rig | **None.** `XRRig` / `Camera Offset` / `Main Camera` stay as they are | Needs an `XROrigin`; this project's rig is hand-made and does not have one |
| Impact on input | **None.** `InputReader.cs` and the legacy `TrackedPoseDriver`s keep working | Both still work in principle, but see the `devices.Count == 1` landmine in §11 |
| Later features (room mesh, anchors, hand tracking) | Rich, first-party | Thinner |

**Take Route A.** This project has a hand-rolled XR rig (there is no XR Interaction Toolkit in
`Packages/manifest.json`), hand-rolled input (`InputReader.cs` reads `InputDevices` directly), and
hand poses driven by the legacy `UnityEngine.SpatialTracking.TrackedPoseDriver`. Route A leaves all
of that untouched. Route B asks you to re-platform the rig *and* debug passthrough at the same time.

The rest of this document assumes Route A.

> **Install Meta XR Core SDK via the Unity Package Manager (UPM), not the `.unitypackage`.**
> The `.unitypackage` installs into `Assets/Oculus/`, and this project already has a hand-trimmed
> `Assets/Oculus/` containing only the Quest 2 controller meshes. Importing over it will likely
> re-write those assets and change their GUIDs, which will break
> `Assets/Prefabs/OculusTouchForQuest2_Left.prefab` and `_Right.prefab` (they will lose their mesh
> references and render as empty objects in both scenes). UPM installs into `Packages/` and avoids
> the collision entirely.

### Decision C — does the lobby go MR too?

`OpeningScene` (the room-code / username keyboard) uses the same `Space.mat` skybox **and** is the
only scene containing the space props — `Assets/Prefabs/jupiter.fbx` and `saturn2.fbx` are
referenced by `OpeningScene` only, never by `SecondScene`.

- **Recommended:** convert both scenes to passthrough, delete the planets. A user who puts the
  headset on and is immediately in their own room understands the app is MR. Flipping from space
  to their living room at scene load is jarring.
- **Alternative:** keep the lobby in VR as a deliberate "waiting room", with passthrough starting
  at `SecondScene`. Defensible, and it keeps the planets. Costs you a hard visual transition.

---

## 2. Why the current setup cannot do passthrough

For context when you hit confusing docs:

- `Packages/manifest.json` has `com.unity.xr.oculus: 4.0.0`. Unity's Oculus XR Plugin exposes
  rendering and input subsystems only — **it has no passthrough API at any 4.x version**. There is
  no checkbox to find.
- `com.unity.xr.arfoundation: 5.0.7` is present but is *not* doing anything MR-related here; it
  came in as part of the XR tooling and is used by the XR Simulation loader in
  `Assets/XR/Loaders/SimulationLoader.asset`.
- `Assets/XR/Settings/OculusSettings.asset` targets Quest 1 and Quest 2 only
  (`TargetQuest: 1`, `TargetQuest2: 1`, `TargetQuestPro: 0`). There is no Quest 3 target field —
  it did not exist in plugin 4.0.0.

Quest 2 passthrough is **greyscale and low resolution**. Quest 3 and Quest Pro are colour. If the
classroom is going to be used with Quest 2 headsets, set expectations accordingly — a greyscale
room behind brightly coloured math is actually fine visually, but it is not what people picture.

---

## 3. Phase 0 — baseline

- [ ] Branch: `git checkout -b mr-passthrough`
- [ ] Confirm a clean VR build works **on device** before changing anything, so you have a known-good
      reference APK. Note the frame rate.
- [ ] Note that `git` is not on `PATH` in the default PowerShell session on this machine — invoke it
      by full path or use a shell where it resolves.

---

## 4. Phase 1 — packages and project settings

### 4.1 Packages

- [ ] Install **Meta XR Core SDK** via UPM (see the warning in §1 Decision B).
- [ ] Upgrade `com.unity.xr.oculus` from `4.0.0` to the latest 4.x compatible with your Editor
      version. Quest 3 targeting requires 4.1+.
- [ ] Run the Meta XR **Project Setup Tool** (`Edit > Project Settings > Meta XR`) and apply its
      recommended fixes — but **read each one before applying**. It will want to change settings
      listed in §5; let it, but know what it did.

### 4.2 `ProjectSettings/ProjectSettings.asset`

| Setting | Current value | Change to | Why |
| --- | --- | --- | --- |
| `AndroidTargetArchitectures` | `3` (ARMv7 + ARM64) | `2` (ARM64 only) | ARMv7 is dead weight, doubles build time, and is not accepted for current Quest submissions |
| `AndroidMinSdkVersion` | `23` | `29` or higher | Quest requires Android 10+; Meta XR SDK will not run below this |
| `AndroidTargetSdkVersion` | `0` (Automatic) | Pin explicitly (32+) | "Automatic" means "whatever SDK is installed on this machine", which is not reproducible |
| `mobileMTRendering: Android` | `0` (off) | `1` (on) | Multithreaded rendering is off. Passthrough costs GPU; you need the headroom |
| Graphics API (`m_APIs` for AndroidPlayer) | `15000000` = **Vulkan** | leave as-is | Already correct — Vulkan is the right choice for passthrough |
| Scripting backend | `1` = IL2CPP | leave as-is | Already correct, and required for ARM64 |
| Colour space | `1` = Linear | leave as-is | Already correct |

### 4.3 `Assets/XR/Settings/OculusSettings.asset`

| Setting | Current | Change to | Why |
| --- | --- | --- | --- |
| `TargetQuestPro` | `0` | `1` | Colour passthrough device |
| Quest 3 target | *(field does not exist in 4.0.0)* | enable after plugin upgrade | Primary colour passthrough device |
| `EnableTrackingOriginStageMode` | `0` (eye level) | `1` (stage / floor) | **Critical for MR** — see §6.1 |
| `m_StereoRenderingModeAndroid` | `2` (Multiview) | leave as-is | Already correct |

### 4.4 Android manifest

There is currently **no custom `AndroidManifest.xml` anywhere in `Assets/`** — Unity is generating
a default one. Passthrough needs manifest entries.

- [ ] Use the Meta XR SDK menu item (`Meta > Tools > Update AndroidManifest.xml`) to generate
      `Assets/Plugins/Android/AndroidManifest.xml`.
- [ ] Confirm it contains:
      ```xml
      <uses-feature android:name="com.oculus.feature.PASSTHROUGH" android:required="true" />
      ```
      Use `android:required="false"` instead if you want the app to still install and run in VR
      mode on headsets without passthrough.
- [ ] Confirm the existing internet/microphone permissions survived — this app needs both (Relay
      networking and Vivox voice). `MicPermissions.cs` requests the mic at runtime; if the manifest
      permission goes missing, voice chat dies silently.

---

## 5. Phase 2 — make the background actually transparent

**This section is where passthrough conversions fail.** Passthrough underlay compositing requires
the app to submit a frame whose background pixels have **alpha = 0**. This project has three
separate settings that each independently force alpha to 1. All three must change or you will get
a solid black background and no error message.

### 5.1 The camera — `Assets/Scenes/SecondScene.unity`, `Main Camera`

| Property | Current | Change to |
| --- | --- | --- |
| `m_ClearFlags` | `1` (Skybox) | `2` (Solid Color) |
| `m_BackGroundColor` | `{r: 0.259, g: 0.375, b: 0.557, a: 0}` | `{r: 0, g: 0, b: 0, a: 0}` |
| `m_RenderPostProcessing` | **`1` (ON)** | **`0` (OFF)** |

> `m_RenderPostProcessing: 1` is the single most likely cause of "I followed the tutorial and the
> background is still black". URP's post-processing stack writes opaque alpha into the final
> target. It must be off on the passthrough camera. If you later need post effects, you need a
> custom pass that preserves alpha — treat that as out of scope.

The background colour alpha is already `0` in both scenes, which is convenient but currently
meaningless because the clear flag is Skybox.

### 5.2 The URP asset — `Assets/Settings/URP-HighFidelity.asset`

> **Correction (verified while applying this).** `URP-HighFidelity.asset` is the asset the
> *Editor* uses — `m_CurrentQuality: 2` is the editor's current level. It is **not** what ships
> to Quest. `QualitySettings.asset` also has `m_PerPlatformDefaultQuality: Android: 1`, which
> selects the **Balanced** level → `URP-Balanced.asset` (GUID `e1260c1148f6143b28bae5ace5e9c5d1`).
> `m_SupportsHDR` was `1` there too, so the alpha fix had to land on Balanced. As applied,
> HDR is now off on **all three** quality levels, so passthrough works whichever one is active.

`ProjectSettings/QualitySettings.asset` has `m_CurrentQuality: 2` → the "High Fidelity" level →
`URP-HighFidelity.asset` (GUID `7b7fd9122c28c4d15b667c7040e3b3fd`), which is also the default in
`GraphicsSettings.asset`.

| Property | Current | Change to | Why |
| --- | --- | --- | --- |
| `m_SupportsHDR` | `1` | `0` | **Required.** HDR on mobile selects `R11G11B10_UFloat`, which has **no alpha channel**. Underlay compositing cannot work |
| `m_MSAA` | `4` | `2` | 4x MSAA plus passthrough is a lot of bandwidth on a tiler GPU |
| `m_ShadowDistance` | `150` | `15`–`20` | 150 m shadow distance in a room-scale MR app is pure waste |
| `m_MainLightShadowmapResolution` | `4096` | `1024`, or disable main light shadows | There is no floor or wall geometry in `SecondScene` for shadows to land on |
| `m_RenderScale` | `1` | leave as-is | Fine |

Also in `Assets/Settings/URP-HighFidelity-Renderer.asset`:

| Property | Current | Change to | Why |
| --- | --- | --- | --- |
| `m_DepthPrimingMode` | `1` (Auto) | `0` (Disabled) | Depth priming is a known perf regression on mobile tiler GPUs |

> **Sanity check:** the project ships three quality levels (Performant / Balanced / High Fidelity)
> and is shipping on the *heaviest* one. Consider switching the Android default to
> `URP-Performant.asset` and applying the alpha-related changes (`m_SupportsHDR: 0`) there instead.
> Whichever asset you ship, make sure the HDR change lands on **that** one — editing the wrong
> quality level's asset is an easy hour to lose.

### 5.3 Scene environment lighting — both scenes

In `SecondScene.unity` and `OpeningScene.unity`:

- [ ] `m_SkyboxMaterial` → `{fileID: 0}` (clear it). Currently both point at
      `Assets/Materials/Space.mat` (GUID `68fcb37f39e13a44d8ab3090b54247e5`).
- [ ] `m_DefaultReflectionMode`: currently `0` (Skybox). With no skybox this samples nothing —
      set it to Custom with no cubemap, or leave it and accept flat reflections on the
      URP/Lit materials.

**Good news:** `m_AmbientMode` is `3` (Flat colour), not Skybox, in both scenes. Ambient light
comes from `m_AmbientSkyColor` as a literal colour, **not** from the skybox. So removing the skybox
will *not* change how anything is lit. If the scene looks different after the change, the cause is
something else — do not go hunting through the lighting settings.

- [ ] Do **not** delete `Assets/Materials/Space.mat`. Keep it so passthrough can be toggled back
      off at runtime (§8) and so the change is trivially revertible.

---

## 6. Phase 3 — the spatial design problem

This is the real work. The app was designed for an infinite void. Here is what breaks in a real room.

### 6.1 Tracking origin: eye level → floor level

`OculusSettings.asset` currently has `EnableTrackingOriginStageMode: 0`, meaning the tracking
origin is **eye level** — the camera starts at y ≈ 0 wherever the user's head happens to be.

In VR this is invisible. In MR it is fatal: virtual content has no defined relationship to the real
floor, so a "table" could render at knee height or above the user's head depending on how tall they
are and whether they were sitting when the app launched.

- [ ] Set `EnableTrackingOriginStageMode: 1` (stage / floor-level origin).

**Two places in the code assume eye-level origin and must be updated:**

**(a) `Assets/Scripts/PlayerControls.cs:109-111`**

```csharp
Vector3 offset = new Vector3(0, 1.36f, 0);
...
this.transform.position = myCam.position - offset;
```

This hardcodes a 1.36 m eye height to push the avatar root down to "the floor". With floor-level
tracking the camera's `y` **is** the real eye height above the real floor, so subtracting a constant
1.36 puts the avatar root at roughly +0.3 instead of 0. Replace with an explicit floor projection:

```csharp
// Floor-level tracking origin: the rig's y IS the floor.
Vector3 rigFloorY = new Vector3(0f, rig.position.y, 0f);
this.transform.position = new Vector3(myCam.position.x, rigFloorY.y, myCam.position.z);
```

Leave `faceOffset` (`new Vector3(0, .36f, 0)`) alone — that is a head-model tuning offset, not a
tracking-origin assumption.

**(b) `Assets/Scripts/seatControl.cs:21-68`** — seat `y` values are currently interpreted as *eye*
heights. After the change they are *floor* positions. See §6.3.

### 6.2 Stop teleporting the rig every frame

`Assets/Scripts/CameraController2.cs:31-36`:

```csharp
if (theSeats.AssignedSeatsOn())
{
    Vector3 myPosition = myPlayer.GetMySeat();
    this.transform.position = myPosition;
    this.transform.rotation = Quaternion.LookRotation(-myPosition);
}
```

When the teacher turns on assigned seats, this hard-sets the rig transform **every frame**. In VR
that just pins you in place. In MR it fights the user's real body: they take a step, and the rig
snaps back to cancel it out. The result is nauseating and it is the fastest way to make a tester
take the headset off.

**Fix:** apply the seat *once*, as a recenter, and never again while the user walks.

```csharp
void Update()
{
    if (theSeats.AssignedSeatsOn())
    {
        // Seat assignment is a one-shot recenter in MR, not a per-frame lock.
        // Physical walking must remain the user's own.
        if (!recentered)
        {
            RecenterToSeat();
            recentered = true;
        }
    }
    else
    {
        recentered = false;
        // ... existing joystick locomotion, unchanged ...
    }
}

/// Move the rig so that the user's CURRENT real-world standing position maps onto
/// their assigned seat in the shared classroom, facing the classroom origin.
public void RecenterToSeat()
{
    Vector3 seat = myPlayer.GetMySeat();

    // Face the centre of the classroom.
    Vector3 lookDir = new Vector3(-seat.x, 0f, -seat.z);
    if (lookDir.sqrMagnitude < 0.0001f) lookDir = Vector3.forward;
    transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);

    // Cancel out where the user's head currently is inside their real room,
    // so the seat lands under their actual feet. Rotation must be set first.
    Vector3 headLocal = transform.InverseTransformPoint(head.position);
    headLocal.y = 0f;
    transform.position = new Vector3(seat.x, 0f, seat.z) - transform.TransformVector(headLocal);
}
```

Wire the existing `Inputs.RightJoystickButtonDown` handler (currently the "drop back to your seat"
button, `CameraController2.cs:65-70`) to call `RecenterToSeat()`. It becomes the MR "re-place the
classroom" button, which users will need — this is the single most important control in an MR app.

**Why this approach:** the shared virtual world stays exactly where it is, at the world origin.
Only each client's *rig* moves. That means every networked value in the project — avatar poses in
`PlayerControls`, drawing point arrays in `NetworkLineDrawer`, graph bounds — stays in world space
and **requires no changes at all**. The alternative (a per-user content anchor with world↔local
conversion on every networked position) is more flexible but forces you to convert coordinates in
`PlayerControls`, `NetworkLineDrawer`, and the late-join replay path. Do not do that unless you
need it.

### 6.3 Rework the seating geometry

`seatControl.makeSeats()` builds three rows of a semicircular arc at radius 2, stacked vertically
at `y = 0`, `y = +1` and `y = -1`.

In a real room:
- `y = -1` puts a student's avatar a metre **below the floor**.
- `y = +1` puts them at roughly 2.4 m — clipping or above most real ceilings.
- Radius 2 puts avatars 2 m away, which in a small room is inside the walls.

**Recommended change:** flatten to the floor and spread the rows outward as concentric arcs.

```csharp
private void makeSeats()
{
    seats = new List<Vector3>();
    // Concentric floor arcs instead of stacked vertical rows — MR has a real floor
    // and a real ceiling, so seats must all sit at y = 0.
    float[] radii = { 2.0f, 2.9f, 3.8f };

    foreach (float r in radii)
    {
        for (float theta = Mathf.PI / 2; theta < Mathf.PI; theta += Mathf.PI / 10)
        {
            if (theta > Mathf.PI / 2)
            {
                seats.Add(new Vector3(r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
                seats.Add(new Vector3(-r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
            }
            else
            {
                seats.Add(new Vector3(r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
            }
        }
    }
}
```

Avatars on the outer arcs will appear through the local user's real walls. That is normal and
accepted in remote MR — you are looking at a hologram of someone in a different building.

> **Do not reduce this to a single row.** One arc yields only ~9 seats, but Relay is allocated for
> 12 (`RelayVivox.CreateRelay()`). `PlayerControls.FindMySeatServerRpc()` indexes
> `theSeats.seats[clientIds.IndexOf(myId)]` with **no bounds check** — clients 10 through 12 would
> throw `ArgumentOutOfRangeException` on join. Keep at least 12 seats. (`IndexOf` also returns
> `-1` if the id is missing, which would throw the same way — worth adding a guard while you are
> in there, independent of MR.)

Also update the teacher's fixed seat in `PlayerControls.cs:132` — `new Vector3(0, 0, -1f)` is
already at `y = 0`, so it survives the change unchanged, but confirm it still reads sensibly as a
floor position.

### 6.4 Content scale and placement

The math content is sized for a void. Check on device:

- `GraphAxisControl.SetAxesAutoScale` fits graphs into a **1.2 unit** box, or **12 units** in "big"
  mode. 1.2 m is a good tabletop size for MR. **12 m is larger than most rooms** — in MR "big mode"
  will put the graph through the ceiling and outside the walls. Either cap big mode at ~3 m or
  disable it when passthrough is on.
- Two-handed resize (`LineDrawer.Update()`, `resizingDrawings` state) lets users scale the whole
  `Drawings` tree without limit. Consider clamping the maximum scale in MR.
- Menus are spawned 1.3 m in front of the camera (`MenuControl.OpenMenu1()`). That is fine in a
  room, but a user standing close to a real wall will get a menu inside it. Low priority.

---

## 7. Phase 4 — turn passthrough on

Add this to `Assets/Scripts/`. Attach to the `XRRig` GameObject in `SecondScene` (and
`OpeningScene` if you took the "lobby is MR too" option in Decision C).

```csharp
using UnityEngine;

/// Enables Meta Insight Passthrough and switches the camera to composite over it.
/// Attach to "XRRig". Keeps a reference to the old skybox so VR mode is still reachable.
public class PassthroughController : MonoBehaviour
{
    public OVRPassthroughLayer passthroughLayer;  // Underlay layer on the rig
    public Camera targetCamera;                   // "Main Camera"
    public Material vrSkybox;                     // Assets/Materials/Space.mat
    public bool startInPassthrough = true;

    static bool passthroughOn;

    void Start()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        SetPassthrough(startInPassthrough);
    }

    public void SetPassthrough(bool on)
    {
        passthroughOn = on;

        if (OVRManager.instance != null)
            OVRManager.instance.isInsightPassthroughEnabled = on;

        if (passthroughLayer != null)
            passthroughLayer.enabled = on;

        // Solid transparent black lets the compositor show the camera feed underneath.
        targetCamera.clearFlags = on ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
        targetCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        RenderSettings.skybox = on ? null : vrSkybox;
    }

    public void Toggle() => SetPassthrough(!passthroughOn);
    public static bool IsPassthroughOn() => passthroughOn;
}
```

Scene setup:

- [ ] Add an `OVRManager` to the scene (the Meta XR SDK's `OVRCameraRig` prefab carries one, but
      **do not replace this project's `XRRig` with `OVRCameraRig`** — too many scripts resolve
      `GameObject.Find("XRRig")`, see §11). Put `OVRManager` on a bare GameObject or on `XRRig`.
- [ ] On `OVRManager`, set **Passthrough Support** to `Supported` (or `Required` if the app is
      MR-only).
- [ ] Add an `OVRPassthroughLayer` component. Set **Placement** to **Underlay**. Overlay renders
      passthrough *on top of* your content, which is the opposite of what you want.
- [ ] Add `PassthroughController` to `XRRig`, wire `passthroughLayer`, `targetCamera`
      (= `Main Camera`), and `vrSkybox` (= `Assets/Materials/Space.mat`).

---

## 8. Phase 5 — in-app passthrough toggle (optional but recommended)

Teachers will want to switch to full VR for immersive moments and back to passthrough for
note-taking. The menu already has a working toggle-pair pattern to copy: the assigned-seats
`On`/`Off` keys.

In `Assets/Scripts/MenuControl.cs`, the handler chain starting at **line 509** is the model:

```csharp
else if (pressedKey.keyName == "On" && !seats.AssignedSeatsOn())
{
    seats.TurnOnAssignedSeats();
    OnKey.KeepOn();
    OffKey.TurnOff();
    CloseMenu();
}
```

Add a parallel branch:

```csharp
else if (pressedKey.keyName == "Passthrough")
{
    passthrough.Toggle();
    CloseMenu();
}
```

- [ ] Add a `Passthrough` key to `Assets/Prefabs/Menu1.prefab` (teacher) and/or `Menu2.prefab`
      (student).
- [ ] **Append the new key as the last child.** `MenuControl` resolves menu widgets by hardcoded
      child index — e.g. `currentMenu.transform.GetChild(0).GetChild(9).GetChild(13)`. Inserting a
      child anywhere but the end silently repoints every index after it, with no compile error and
      no runtime exception — just a menu that does the wrong thing.
- [ ] Decide whether passthrough is per-user or synced. **Per-user is correct** — it is a comfort
      setting, like brightness. Do not put it in a `NetworkVariable`.

---

## 9. Phase 6 — visual pass for MR

Content that reads well against a black starfield often disappears against a beige wall.

- [ ] **Line and surface materials.** Almost every material in `Assets/Materials/` uses URP/Lit
      (shader GUID `933532a4fcc9baf4fa0491de14d08ed7`) — `blue`, `red`, `green`, `yellow`,
      `orange`, `purple`, `white`, `Pipe`, `TubeMaterial`, `NetworkPipe`, `NetworkTubeMaterial`,
      `Spheres`. Lit materials are shaded by the scene's virtual `Directional Light`, which has no
      relationship to the real room's lighting, so drawings look subtly *wrong* — lit from a
      direction nothing else in view is lit from. Switching the drawing materials to **URP/Unlit
      with emissive colour** makes lines read as bright, self-luminous holograms and sidesteps the
      mismatch entirely. This is the single highest-impact visual change.

  > **Tried on device and reverted — do not re-apply.** Unlit was the wrong call for this app.
  > `PipeRenderer` tubes are 12-sided cylinders whose 3D form is legible *only* through specular
  > shading; unlit flattens every stroke to a coloured silhouette and the contours disappear.
  > That costs more than the lighting mismatch it fixes — this is a maths tool, and reading the
  > shape of a curve in space is the whole point. All twelve materials are back on URP/Lit at
  > their original `_Smoothness` (0.65 for the colour palette, 0.5 for the tube defaults).
  >
  > Note the palette is **eight** materials, not seven: `red, orange, yellow, green, blue,
  > purple, white, ClearWhite` (the array on `LineControl`/`NetLineControl`, blue at index 4 is
  > the default). The list in the paragraph above omits `ClearWhite`, so following it literally
  > converts seven of eight and leaves index 7 mismatched.
  >
  > Two side effects of the passthrough work that do slightly reduce sheen, if you ever want it
  > back: environment reflections used to sample `Space.mat` and now sample nothing (§5.3 clears
  > the skybox and this doc sets `m_DefaultReflectionMode` to Custom with no cubemap) — assign a
  > small cubemap there to restore them. And there is still only one `Directional Light`, so a
  > tube running parallel to it catches no highlight; a second fill light from another angle
  > would make contours read consistently in every orientation.
- [ ] **Contrast.** Test against a white wall and a dark wall. Consider a thin dark outline or a
      slight emissive boost on `PipeRenderer` tubes.
- [ ] **The `Directional Light`** in `SecondScene` — disable its shadows. There is no floor or wall
      geometry in the scene to receive them, so it is pure cost.
- [ ] **The planets.** `jupiter.fbx` and `saturn2.fbx` are in `OpeningScene` only. Delete them if
      the lobby goes MR, keep them if it stays VR.
- [ ] **`InfoBlock` / `RoomCodeText`** in `SecondScene` — check legibility against a real
      background. TMP text with no backing plate can be unreadable against a busy room.
- [ ] **Grab a floor reference.** Consider adding a faint grid or shadow disc at `y = 0` under the
      graph so it visually connects to the real floor rather than floating. Small touch, large
      perceived-quality difference.

---

## 10. Testing

**Passthrough does not render in the Editor Game view.** Plan for device testing.

- [ ] `InputReader` falls back to keyboard whenever no XR controller is detected, so all app *logic*
      is still testable in the Editor — you will just see a black background where passthrough
      would be. That is expected, not a bug.
- [ ] Meta XR Simulator can preview passthrough on desktop. Quest Link passthrough works in recent
      SDK versions but is inconsistent. **Treat on-device (`adb install`) as the source of truth.**
- [ ] Multiplayer still needs two running clients. One in-Editor + one on-device is the practical
      combination.

### Acceptance checklist

- [ ] Real room visible in `SecondScene`, no black background, no grey haze
- [ ] Drawings and graph surfaces render **on top of** passthrough, not behind it (if they are
      behind, the layer is set to Overlay instead of Underlay)
- [ ] Virtual content sits at a plausible height relative to the real floor (validates §6.1)
- [ ] Walking physically moves you through the virtual classroom and the rig does not fight you
      (validates §6.2)
- [ ] Teacher toggles assigned seats on → you recenter **once**, then can still walk freely
- [ ] Remote avatars appear at floor level, not sunk into the floor or floating (validates §6.1a)
- [ ] Two-handed resize still works and drawings do not mispair (see §11)
- [ ] Voice chat still works — confirm the mic permission survived the manifest regeneration
- [ ] Late joiner sees all existing drawings **and** graphs (exercises `LateSyncLinePointsServerRpc`)
- [ ] Frame rate: compare against the baseline APK from Phase 0. Passthrough is not free

---

## 11. Landmines specific to this repo

Things that will break silently. None of these produce a compile error.

1. **`GameObject.Find` by exact name.** These names are resolved at runtime across many scripts and
   renaming any of them breaks the app at runtime only: `XRRig`, `Network Manager`, `Input Reader`,
   `Left Hand`, `Right Hand`, `Left Grabber`, `Right Grabber`, `Seating Manager`, `Menu Manager`,
   `Scene Two Manager`, `Drawings`. **This is why you must not swap `XRRig` for Meta's
   `OVRCameraRig` prefab.** If you decide you need `OVRCameraRig`, rename it to `XRRig` and
   re-wire every inspector reference by hand.

2. **Hardcoded menu child indices.** `MenuControl` uses chains like
   `.GetChild(0).GetChild(9).GetChild(13)`. Adding a passthrough key anywhere but the end of the
   child list breaks the graph menu.

3. **`InputReader` requires exactly one matching device.** `InputReader.cs:137` and `:327` both do:
   ```csharp
   InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right, devices);
   if (devices.Count == 1)
   ```
   If anything else ever matches `Right` — hand tracking, a second interaction profile — `Count`
   becomes 2 and **all right-hand input silently stops**. Enabling hand tracking alongside
   controllers (a natural MR follow-up) will trigger this. Change to `devices.Count >= 1` and index
   `[0]`, or filter on `Controller | Right`. Worth fixing pre-emptively.

4. **Dual-copy drawing replication is order-dependent.** Every drawing exists twice — a local copy
   and a networked twin — and they are paired **positionally**, not by ID:
   `LineDrawer` enqueues into `Queue<GameObject> UnpairedLines` and `NetLineControl.Start()`
   dequeues on the owner. This depends on network spawn order matching local enqueue order. Do not
   add anything to the drawing path that could enqueue out of order. Mispairing is silent — moves
   and deletes just affect the wrong drawing.

5. **`MenuControl` keeps graph state in `static` fields** (function string, bounds, step sizes,
   scale flags). These persist across menu opens *and across scene reloads within a session*. If
   you add a static passthrough flag, it inherits the same behaviour — which is convenient here
   (the setting survives a scene reload) but be deliberate about it.

6. **Tags drive replication and grabbing**: `Drawings`, `Graph`, `Axes`, `Line`, `key`.
   `GrabControl` also walks parents *by name* until it hits `Drawings` / `Right Grabber` /
   `Left Grabber`.

7. **Build scene order must not change.** `ProjectSettings/EditorBuildSettings.asset` must keep
   `OpeningScene` at index 0, then `SecondScene`. `GameController` hard-codes
   `SceneManager.LoadScene("SecondScene")`.

8. **`AxisControl.cs` is dead code** — superseded by `GraphAxisControl.cs`, referenced by nothing.
   Deleted as part of this work.

   > **Correction (verified while applying this).** `CameraController.cs` is **not** dead code.
   > `OpeningScene`'s `XRRig` carries it (script GUID `b8da86dc87e79a8438af6715185c8e6b`);
   > only `SecondScene` uses `CameraController2`. Deleting it silently breaks lobby locomotion —
   > the reference is by GUID, so there is no compile error. It has been kept. Note this also
   > means the lobby rig does **not** get the §6.2 one-shot-recenter fix; that is fine, because
   > `CameraController` has no seating code to fight the user with.

---

## 12. Rollback

Every change in this document is reversible:

- Project settings and URP asset values are recorded above with their original values.
- `Assets/Materials/Space.mat` is retained, and `PassthroughController.SetPassthrough(false)`
  restores the skybox at runtime.
- The seating and recenter changes are the only ones that alter app behaviour in VR mode. If you
  need a single build that does both, gate them on `PassthroughController.IsPassthroughOn()`.

If the whole thing needs to be abandoned: `git checkout master`. Keep the Editor upgrade (Decision
A) on its own commit precisely so it can be kept or dropped independently of the MR work.

---

## 13. Suggested order of work

1. Decisions A, B, C (§1)
2. Editor upgrade, separate commit, verify VR build unchanged
3. Packages + project settings (§4)
4. Transparent background (§5) → **first milestone: you can see your room**
5. Tracking origin + stop the per-frame rig lock (§6.1, §6.2) → **second milestone: MR feels right**
6. Seating rework (§6.3)
7. Toggle (§8), visual pass (§9)
8. Device testing (§10)

Milestones 4 and 5 are the two points where you should stop and put the headset on a real user
before continuing. Everything after milestone 5 is polish; everything before it is plumbing.

---

## 14. What is left — steps that need the Unity Editor

Everything above that lives in a file has been applied. These three cannot be done by editing
files, because they depend on assets the Meta XR SDK creates on demand or on prefab
bookkeeping Unity has to do itself.

1. **Open the project once so UPM resolves `com.meta.xr.sdk.core`.**
   `Packages/manifest.json` now declares the Meta scoped registry
   (`https://npm.developer.oculus.com`, scope `com.meta`) and pins `205.0.0`, which requires
   Unity 6000.0.66f2+ and pulls in `com.unity.xr.hands` as a dependency. Until this resolves,
   `PassthroughController.cs` and `Assets/Editor/MRPassthroughSetup.cs` will not compile —
   they reference `OVRManager`, `OVRPassthroughLayer` and `OVRProjectConfig`.

   > **Unity 6000.5 needs a patch to the SDK itself.** Meta XR Core SDK 205.0.0 does not
   > compile on 6000.5: `SceneListenerNGO.cs` reads `ObjectChangeEventStream`'s `instanceId`,
   > which 6000.5 made an *error*-level obsolete, and the file is missing the
   > `UNITY_6000_5_OR_NEWER` branch that its own sibling `OVRSceneChangeListener.cs` already
   > has. The project drops into Safe Mode on open. Fix:
   >
   > ```powershell
   > powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
   > ```
   >
   > This has to be re-run after any fresh clone or package re-resolve, because the patch
   > lands in the gitignored `Library/PackageCache`. See `Tools/MetaSdkPatch/README.md`,
   > including when to delete it. **If Unity offers Safe Mode, take it** — "Ignore" opens the
   > project with unloadable scripts, and re-saving a scene in that state can strip the
   > components this conversion added to `XRRig`.

   > **Set Active Input Handling back to "Input Manager (Old)" after the package installs.**
   > The Meta SDK depends on `com.unity.xr.hands`, which depends on `com.unity.inputsystem`,
   > and Unity flips `activeInputHandler` from `0` to `2` ("Both") when that package appears.
   > The Oculus XR Plugin then refuses to build: *"Active Input Handling is set to Both, this
   > is unsupported on Android... Cancelling..."*. It must be **Input Manager (Old)**, not the
   > new Input System — `InputReader.cs` alone has 42 legacy `Input.*` calls and the lobby's
   > `EventSystem` uses `StandaloneInputModule`; the new backend makes all of them throw at
   > runtime. `Edit > Project Settings > Player > Other Settings > Active Input Handling`,
   > then restart the Editor. Leave `com.unity.inputsystem` installed — it is dormant with the
   > old backend, and nothing here uses XR Hands.
   >
   > Unity then prompts *"the native platform backends for the new input system are not
   > enabled... Do you want to enable the backends?"* on **every Editor launch**. Always answer
   > **No** — Yes puts `activeInputHandler` back and reintroduces the build failure. The warning
   > is about the Input System package, which this project does not use: controllers arrive via
   > `UnityEngine.XR.InputDevices` (an XR subsystem API, unaffected by this setting) and the
   > keyboard fallback via the legacy `Input` class. There is no "don't ask again" — the
   > suppression flag is session-scoped by design.

2. **`Math Classroom > MR > Configure Meta Passthrough Project Config`.**
   Runs automatically the first time the Editor loads, and is re-runnable from the menu. Writes
   the `OVRProjectConfig` asset: passthrough `Required`, contextual-passthrough splash, Quest
   2/Pro/3/3S targets. Skipping it means `Meta > Tools > Android Manifest Tool` would regenerate
   `Assets/Plugins/Android/AndroidManifest.xml` **without** the passthrough feature tag, silently
   undoing §4.4.

3. **`Math Classroom > MR > Add Passthrough Key To Menus`.**
   Clones an existing key in `Menu1.prefab` and `Menu2.prefab`, renames it `Passthrough`, and
   appends it as the **last** child so no hardcoded child index shifts (§11.2). The handler in
   `MenuControl` is already in place. **Placement is a guess** — the command puts the key one
   row below the lowest sibling and logs where it landed; check it in the Editor and drag it
   where it belongs. This is the one step whose result nobody has looked at.

Also commit the `.meta` files Unity generates on first open for `Assets/Editor/`,
`Assets/Plugins/Android/`, and `AndroidManifest.xml`.

### Known gaps, deliberately not changed

- **Joystick locomotion still flies.** `CameraController2` translates along `RightHand.forward`,
  which has a Y component, so the rig can leave `y = 0` and the virtual floor drifts away from
  the real one. `RecenterToSeat` (right joystick button) puts it back. Constraining locomotion to
  the horizontal plane would be a behaviour change beyond what §6 asks for — decide it on device.
- **Snap-turn rotates about the rig origin, not the head.** Standard MR practice is to rotate
  about the head. Same reasoning: out of scope here, best judged in the headset.
- **§9's floor reference** (a faint grid or shadow disc at `y = 0` under the graph) is not done.
- **The Oculus XR Plugin is deprecated.** Meta XR Core SDK v205 still ships full
  `USING_XR_SDK_OCULUS` support and `Unity.XR.Oculus` references, so Route A works today, but
  Meta now points new work at `com.unity.xr.openxr` + Unity OpenXR: Meta. Worth planning a
  migration; it is not urgent and should not be mixed into this change.
