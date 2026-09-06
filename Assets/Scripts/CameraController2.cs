using Unity.Netcode;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

public class CameraController2 : MonoBehaviour
{
    public InputReader Inputs;
    public Transform LeftHand;
    public PlayerControls myPlayer;
    public Transform head;

    public float RotationAngle;
    public float RotationSpeed;
    public float MovingSpeed;

    [Tooltip("Reject an anchor that localizes further than this from this headset's own floor. " +
             "This is a TRACKING-failure bound, not a floor-calibration one: a headset that has " +
             "never had Space Setup run can be half a metre out, and taking the anchor's height is " +
             "exactly how that gets corrected. The startup race is caught separately, by asking " +
             "XROrigin whether it is in Floor mode yet.")]
    public float MaxAnchorHeightDisagreement = 0.6f;

    // Seconds between repeats of the anchor-height warning. At frame rate this is a log flood, and
    // the interesting events are "it started" and "it stopped", not the sixty in between.
    public float HeightWarningIntervalSeconds = 5f;
    private float nextHeightWarningAt;
    private float nextOriginModeWarningAt;

    // XROrigin is on this same GameObject (see CLAUDE.md, The persistent rig). Cached in Start()
    // because AlignRigToAnchor runs every frame while colocated.
    private XROrigin origin;

    // Static because everything that cares — PlayerControls, WorldGrab, the probe, the locomotion
    // gate — needs it without holding a reference to the rig, and the rig is destroyed and rebuilt
    // on every game switch (PlayerControls.cs:214-251).
    public static bool LocalIsAligned { get; private set; }
    public static event System.Action LocalAlignmentChanged;

    float yAngle;
    float movement;

    private bool leftJoystickReleased = true;

    // Where recentring brings the user back to. Starts as the rig's authored transform, so a
    // game scene is usable with no session running, and becomes this player's slot on the ring
    // around the board as soon as the server has assigned one.
    private Vector3 anchorPosition;
    private Quaternion anchorRotation;

    // -1 until this rig knows which slot on the ring belongs to the local player.
    private int ringSlot = -1;

    void Start()
    {
        anchorPosition = transform.position;
        anchorRotation = transform.rotation;

        origin = GetComponent<XROrigin>();
        if (origin == null)
        {
            // Not fatal — AlignRigToAnchor falls back to the height guard alone, which is what it
            // did before. Worth saying, because it means the startup race is unguarded.
            Debug.LogWarning("CameraController2: no XROrigin on " + name + ", so alignment cannot " +
                             "tell a Camera Offset that has not been zeroed yet from a real floor " +
                             "disagreement.");
        }

        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
        }

        // Movement runs along the left controller, so a rig with no LeftHand cannot move at all.
        // Resolve it from the rig rather than failing silently: this field used to be RightHand,
        // and renaming it dropped the value every scene had serialized against the old name.
        if (LeftHand == null)
        {
            LeftHand = HierarchyUtils.FindDescendant(transform, "Left Hand");
            if (LeftHand == null)
            {
                Debug.LogWarning("CameraController2: no \"Left Hand\" under " + name +
                                 "; joystick movement is off until one is assigned.");
            }
        }

        if (ringSlot >= 0)
        {
            // PlayerControls got here before Start did, so the two lines at the top of this
            // method have just overwritten the ring anchor with wherever the rig had been
            // moved to. Put it back.
            ApplyRingAnchor();
        }
        else
        {
            AdoptLocalPlayerSlot();
        }
    }

    void Update()
    {
        if (Inputs == null)
        {
            return;
        }

        // A colocated player moves by walking. Locomotion, recentring and the debug tilt all fight
        // the per-frame alignment (which wins — BoardAnchor runs at execution order 10 and this runs
        // at 0) so leaving them on would not break the frame, it would mean controls that silently
        // do nothing. Off is honest.
        if (LocalIsAligned)
        {
            return;
        }

        // Both grips are the world grab. The user is not trying to walk.
        if (WorldGrab.IsActive)
        {
            return;
        }

        //rotation
        if (Inputs.LeftControllerFound)
        {
            if ((Inputs.leftJoystick[0] > .75 || Inputs.leftJoystick[0] < -.75) && leftJoystickReleased)
            {
                leftJoystickReleased = false;
                this.transform.Rotate(0f, Inputs.leftJoystick[0] * RotationAngle, 0f, Space.World);
            }
            if (Inputs.leftJoystick[0] < .75 && Inputs.leftJoystick[0] > -.75)
            {
                leftJoystickReleased = true;
            }
        }
        else
        {
            yAngle = Inputs.leftJoystick[0];
            this.transform.Rotate(0f, yAngle * RotationSpeed, 0f, Space.World);
        }

        //translation
        if (LeftHand != null)
        {
            movement = Inputs.leftJoystick[1];
            this.transform.Translate(LeftHand.forward * MovingSpeed * movement * Time.deltaTime, Space.World);
        }

        //If the player presses left joystick, the room is re-placed around them.
        //This is the single most important control in an MR app.
        if (Inputs.LeftJoystickButtonDown)
        {
            Recenter();
        }

        //Need Keyboard method of tilting up and down for debugging purposes, M tilts down, N tilts up
        if (Input.GetKey(KeyCode.M))
        {
            this.transform.Rotate(RotationSpeed, 0f, 0f, Space.Self);
        }
        if (Input.GetKey(KeyCode.N))
        {
            this.transform.Rotate(-RotationSpeed, 0f, 0f, Space.Self);
        }
    }

    /// <summary>
    /// Called by PlayerControls once the local player has spawned, and again after every game
    /// switch. The hand comes from the player because that is the one place in the project that
    /// already re-resolves the current scene's hands.
    /// </summary>
    public void Setup(PlayerControls newPlayer, Transform leftHand)
    {
        myPlayer = newPlayer;

        if (leftHand != null)
        {
            LeftHand = leftHand;
        }
    }

    /// <summary>
    /// Stand this client's user on their slot on the ring around the board, facing the middle,
    /// and make that slot what the recentre button returns to. Called by PlayerControls once the
    /// server has assigned a slot, and again after every game switch.
    /// </summary>
    public void PlaceAtRingSlot(int slot)
    {
        // The lobby has an XRRig too, and its layout is the keyboard in front of the user.
        // Moving that rig onto the ring would drag somebody out of typing their username.
        // gameObject.scene.name is unusable here: the rig lives under PersistentRig
        // (DontDestroyOnLoad), so it always reports the "DontDestroyOnLoad" pseudo-scene.
        if (!GameRoutes.IsGameScene(SceneManager.GetActiveScene().name))
        {
            return;
        }

        // The slot is remembered either way, so losing the anchor re-places them correctly.
        // ApplyRingAnchor is what refuses to move a colocated player.
        ringSlot = slot;
        ApplyRingAnchor();
    }

    private void ApplyRingAnchor()
    {
        // A colocated player's position is a fact about the real room, not something to assign.
        // Moving them onto a slot is exactly the involuntary rig move PlayerRing.cs:56-61 warns
        // about, and it would put two people standing next to each other on opposite sides of the
        // board. The guard lives here rather than in PlaceAtRingSlot so that Start()'s catch-up call
        // after a game switch is covered by it too.
        if (LocalIsAligned)
        {
            return;
        }

        anchorPosition = PlayerRing.SlotPosition(ringSlot);
        anchorRotation = PlayerRing.SlotRotation(ringSlot);
        Recenter();
    }

    /// <summary>
    /// Ask the local player which slot it was given. PlayerControls pushes the slot in when it
    /// can, but on a game switch the rig and the player are brought up by two different systems
    /// in an order Netcode does not guarantee, and a rig that came up second would otherwise sit
    /// at the scene's authored transform for the rest of the game.
    /// </summary>
    private void AdoptLocalPlayerSlot()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return;
        }

        PlayerControls player = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        if (player == null || player.spawnSlot.Value < 0)
        {
            return;
        }

        myPlayer = player;
        PlaceAtRingSlot(player.spawnSlot.Value);
    }

    /// <summary>
    /// Move this rig so the shared anchor lands on the world origin, yaw-only. Called every frame by
    /// BoardAnchor while bound — not once — because the anchor's tracking-space pose is not a
    /// constant: the runtime recentres the tracking origin on its own, and anchors are re-localised
    /// as the room map improves. Each of those slides a one-shot alignment permanently out of the
    /// shared frame for one client.
    ///
    /// Idempotent by construction: the anchor is driven onto a fixed point, so a frame in which
    /// nothing moved writes back the pose that is already there. Writing W = XRRig . E, where E is
    /// everything between the rig and the tracking origin, the anchor's reported pose is
    /// A = XRRig . E . x; assigning XRRig' = D . XRRig with D the yaw-only inverse of A gives
    /// XRRig' . E . x = D . A = 0, whatever sits in E.
    /// </summary>
    public void AlignRigToAnchor(Transform anchor)
    {
        if (anchor == null)
        {
            return;
        }

        // XROrigin.MoveOffsetHeight only zeroes Camera Offset once the input subsystem reports
        // Floor mode; until then every pose in this method is off by the serialized CameraYOffset
        // (1.36144) and no alignment computed from it means anything. Ask the mode directly rather
        // than inferring it from a height — that inference is what forced
        // MaxAnchorHeightDisagreement down to a value too tight to absorb a real floor-calibration
        // difference, which is the thing this method is supposed to CORRECT rather than reject.
        //
        // Both are driven by the same event (XROrigin.OnInputSubsystemTrackingOriginUpdated calls
        // MoveOffsetHeight), so "the mode reads Floor" and "Camera Offset has been zeroed" are the
        // same fact, not two that could disagree.
        if (origin != null && origin.CurrentTrackingOriginMode != TrackingOriginModeFlags.Floor)
        {
            WarnOriginModeNotFloor();
            return;
        }

        // Yaw only. A full-rotation alignment would tip the board off the real floor, and pitch and
        // roll from an anchor are noise: the runtime gravity-aligns them and what is left is error.
        Vector3 flatForward = anchor.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 1e-6f)
        {
            // The anchor is pointing at the ceiling. Should be impossible — BoardAnchor creates it
            // with a yaw-only rotation — but LookRotation returns garbage rather than failing, and
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

        // Take the anchor's HEIGHT as well as its horizontal position. Each headset puts y = 0 on
        // its OWN estimate of the floor and those estimates routinely differ by a few centimetres.
        // The anchor is the only object in the system that knows the real answer. deltaRot is
        // yaw-only, so this is a pure vertical correction and cannot lean.
        //
        // The quantity below is invariant to where the rig is: anchor.position.y -
        // transform.position.y reduces to the anchor's height above the tracking origin, i.e. above
        // this headset's floor. That is why it is a meaningful guard and not a moving target.
        float anchorAboveMyFloor = anchor.position.y - transform.position.y;
        if (Mathf.Abs(anchorAboveMyFloor) > MaxAnchorHeightDisagreement)
        {
            // Reject the frame rather than half-applying the correction. Under a per-frame loop the
            // previous frame's pose is strictly better than any fallback: a board stale by one frame
            // is invisible, a board snapping to the floor and back is not.
            //
            // At 0.6 m this is a tracking-failure bound and nothing else. The startup race it used
            // to double as a guard for is caught above, by the tracking-origin-mode check — and
            // catching it here instead is what sized this constant at 0.25 m, tight enough to
            // reject the floor-calibration difference the height correction below exists to fix.
            WarnHeightDisagreement(anchorAboveMyFloor);
            return;
        }

        transform.SetPositionAndRotation(newPos, deltaRot * transform.rotation);
        SetAligned(true);
    }

    /// <summary>
    /// The mode check above returns every frame while it holds, and a silent permanent return is
    /// exactly the failure this whole change is about. Same throttle as the height warning: the
    /// interesting events are "it started" and "it stopped", not the sixty a second in between.
    /// One line at startup and never again is normal — the subsystem takes a moment to report.
    /// </summary>
    private void WarnOriginModeNotFloor()
    {
        if (Time.realtimeSinceStartup < nextOriginModeWarningAt)
        {
            return;
        }
        nextOriginModeWarningAt = Time.realtimeSinceStartup + HeightWarningIntervalSeconds;

        Debug.LogWarning("AlignRigToAnchor: XROrigin is in " + origin.CurrentTrackingOriginMode +
                         " mode, not Floor, so Camera Offset has not been zeroed and every pose " +
                         "here is off by CameraYOffset. Not aligning until it resolves.");
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

        // Nothing else tells a player which of the two states they are in, and "the players were
        // not aligned in the room" is indistinguishable from inside a headset from "nobody placed
        // an anchor". DebugLog mirrors every Application.logMessageReceived into the in-headset
        // box, so this lands where it can be read without adb.
        Debug.Log(value ? "Colocation: aligned to the room anchor."
                        : "Colocation: alignment LOST — this headset is now in its own room.");

        if (LocalAlignmentChanged != null)
        {
            LocalAlignmentChanged();
        }
    }

    /// <summary>
    /// Move the rig so that the user's CURRENT real-world standing position maps onto the
    /// anchor — their slot around the board, or where the rig was authored if there is no
    /// session — facing the way the anchor faces.
    /// Only this client's rig moves; the shared virtual world never does, so every networked
    /// value in the project stays in world space and needs no conversion.
    /// </summary>
    public void Recenter()
    {
        // Rotation must be set before the head offset is measured against it.
        transform.rotation = anchorRotation;

        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
        }

        if (head == null)
        {
            // No head to anchor against — fall back to absolute placement.
            transform.position = anchorPosition;
            return;
        }

        // Cancel out where the user's head currently is inside their real room, so the
        // anchor lands under their actual feet.
        Vector3 headLocal = transform.InverseTransformPoint(head.position);
        headLocal.y = 0f;
        transform.position = anchorPosition - transform.TransformVector(headLocal);
    }
}
