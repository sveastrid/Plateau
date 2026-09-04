using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BASH's input loop and firing state machine. One per BASH scene, on the scene-root object
/// "Controls" — logic, no geometry, so it deliberately does not hang under World Root.
///
/// Point at one of your own pieces with the right trigger to select it, then hold the left
/// trigger to fire: a tube of geometry (PipeRenderer) grows out of the piece's cannon at an
/// accelerating speed, steered by the right joystick. Release and the piece teleports to the end
/// of the trail. What the trail touches on the way decides what dies — see LineControls.
///
/// Everything here works in BashRoot's local space. See BashRoot for why.
///
/// Order 25, matching PlateauSelection: after RoomContent (15) and WorldGrab (20), so the board
/// pose this reads is this frame's rather than last frame's.
/// </summary>
[DefaultExecutionOrder(25)]
public class ControlListener : MonoBehaviour
{
    // Resolved by Bind() at runtime, not in the Inspector: Input Reader, Menu Manager and the
    // Pointer all live on PersistentRig in OpeningScene and are DontDestroyOnLoad, so no scene
    // in this project can hold Inspector references to them. See PlateauSpawnMenu.Bind().
    public InputReader Inputs;
    public pointerControl pointer;
    public MenuControl menu;

    public NetworkBaseControl netBaseControl;
    public SpawnManager netSpawnManager;
    public GameObject cannonLine;

    // Board-local units per second, and per second per second. Because the trail is built in
    // BashRoot's local space these are invariant under the world grab: a shot crosses the same
    // fraction of the board whatever size the players have made it.
    public float initialCannonSpeed = 0.5f;
    public float cannonAcceleration = 0.1f;
    public float cannonTiltSpeed = 500f;

    // Wired to World Root > Board > Islands in the scene. MenuControl reaches the same object
    // through FindFirstObjectByType when the "Random Islands" key is pressed; this reference is
    // the direct path for anything already holding the ControlListener.
    public IslandManager islandManager;

    private PipeRenderer currentCannonLine;
    private List<Vector3> currentLinePoints = new List<Vector3>();
    private Vector3 currentDirection;
    private LineControls newCannonLineControl;
    private float currentCannonSpeed;

    void Update()
    {
        if (!Bind())
        {
            return;
        }

        if (!Playable())
        {
            CancelShot();
            return;
        }

        // A spectator (seat 4-11) never gets a base, so netBaseControl stays null for the whole
        // scene. Every path below tolerates that rather than checking once at the top, because
        // a base can also arrive mid-scene when a seat frees up.
        if (netBaseControl != null && netBaseControl.activeGamepiece != null &&
            Inputs.rightJoystick.x != 0f)
        {
            // Aim the parked piece. Overwritten by the shot's own direction on release.
            netBaseControl.activeRot.Value =
                RotateVectorInXZ(netBaseControl.activeRot.Value, -Inputs.rightJoystick.x);
        }

        if (Inputs.RightMainTriggerDown && netBaseControl != null &&
            pointer != null && pointer.currentGamepiece != null)
        {
            netBaseControl.ChangeGamePiece(pointer.currentGamepiece);
        }

        if (netBaseControl == null || netBaseControl.activeGamepiece == null)
        {
            CancelShot();
            return;
        }

        Transform frame = BashRoot.Frame;
        if (frame == null)
        {
            return;                     // no Bash Root in this scene: nothing to measure against
        }

        if (Inputs.LeftMainTriggerDown)
        {
            BeginShot(frame);
        }
        else if (Inputs.LeftMainTrigger && currentLinePoints.Count > 0)
        {
            ExtendShot();
        }
        else if (Inputs.LeftMainTriggerUp && currentLinePoints.Count > 0)
        {
            EndCannonLine();
        }
    }

    // ------------------------------------------------------------------ firing

    void BeginShot(Transform frame)
    {
        currentCannonSpeed = initialCannonSpeed;
        netBaseControl.TurnOffMyColliders();

        // A child of Bash Root with an identity local transform, because PipeRenderer treats the
        // points it is given as mesh vertices in the pipe object's own local space. In BASH that
        // worked because the line was instantiated at the world origin and the world never moved.
        GameObject line = Instantiate(cannonLine, frame);
        line.transform.localPosition = Vector3.zero;
        line.transform.localRotation = Quaternion.identity;
        line.transform.localScale = Vector3.one;

        newCannonLineControl = line.GetComponent<LineControls>();
        newCannonLineControl.ChangeMaterial(SpawnManager.LocalSeat());
        newCannonLineControl.ConnectLineToNetworkBase(netBaseControl);
        newCannonLineControl.checkForCollisions = true;

        currentCannonLine = line.GetComponent<PipeRenderer>();

        Transform muzzle = netBaseControl.activeGamepiece.transform.GetChild(3);
        currentLinePoints.Add(frame.InverseTransformPoint(muzzle.position));
        currentCannonLine.SetPositions(currentLinePoints.ToArray());

        currentDirection = BashRoot.ToLocalDirection(netBaseControl.activeGamepiece.transform.forward);
    }

    void ExtendShot()
    {
        currentDirection = RotateVectorInXZ(currentDirection,
            -cannonTiltSpeed * Inputs.rightJoystick.x * Time.deltaTime);
        currentLinePoints.Add(currentLinePoints[currentLinePoints.Count - 1] +
                              currentCannonSpeed * currentDirection * Time.deltaTime);
        currentCannonLine.SetPositions(currentLinePoints.ToArray());
        currentCannonSpeed += cannonAcceleration;
    }

    /// <summary>
    /// Commit the shot: publish it to the room and teleport the piece to the end of the trail.
    /// Also called by LineControls the moment the trail touches something that stops it.
    /// </summary>
    public void EndCannonLine()
    {
        // LineControls can reach here twice in one frame — a trail entering the corner where two
        // walls meet raises two OnTriggerEnter calls. Without this the second one indexes an
        // empty list.
        if (currentLinePoints.Count == 0 || currentCannonLine == null)
        {
            return;
        }

        currentDirection = RotateVectorInXZ(currentDirection,
            cannonTiltSpeed * Inputs.rightJoystick.x * Time.deltaTime);
        currentLinePoints.Add(currentLinePoints[currentLinePoints.Count - 1] +
                              currentCannonSpeed * currentDirection * Time.deltaTime);
        currentCannonLine.SetPositions(currentLinePoints.ToArray());

        if (netSpawnManager != null)
        {
            netSpawnManager.SpawnNetworkCannonLine(currentLinePoints.ToArray());
        }

        if (netBaseControl != null)
        {
            // Board-local, like the points themselves. NetworkBaseControl converts back on the
            // way out.
            netBaseControl.activePos.Value = currentLinePoints[currentLinePoints.Count - 1];
            netBaseControl.activeRot.Value = currentDirection;
            netBaseControl.TurnOnMyColliders();
        }

        currentLinePoints.Clear();
        if (newCannonLineControl != null)
        {
            newCannonLineControl.checkForCollisions = false;
        }
        currentCannonLine = null;
        currentCannonSpeed = initialCannonSpeed;
    }

    /// <summary>
    /// Drop a shot in progress without publishing it — the menu opened, a world grab started, or
    /// the piece being fired was destroyed under us. Leaves the piece where it was.
    /// </summary>
    void CancelShot()
    {
        if (currentLinePoints.Count == 0 && newCannonLineControl == null)
        {
            return;
        }

        currentLinePoints.Clear();
        if (newCannonLineControl != null)
        {
            newCannonLineControl.checkForCollisions = false;
            Destroy(newCannonLineControl.gameObject);
            newCannonLineControl = null;
        }
        currentCannonLine = null;
        currentCannonSpeed = initialCannonSpeed;

        if (netBaseControl != null)
        {
            netBaseControl.TurnOnMyColliders();
        }
    }

    // ------------------------------------------------------------------ menu entry points

    /// <summary>
    /// Restore every base and delete every trail. Public because the "Reset Game" key on the
    /// shared menu calls it — BASH's own BASHMenu and the state machine that drove it are gone.
    /// </summary>
    public void ResetBoard()
    {
        GameObject[] bases = GameObject.FindGameObjectsWithTag("base");
        for (int i = 0; i < bases.Length; i++)
        {
            NetworkBaseControl b = bases[i].GetComponent<NetworkBaseControl>();
            if (b != null)
            {
                b.ResetBases();
            }
        }
    }

    /// <summary>Re-scatter the islands. The "Random Islands" key on the shared menu.</summary>
    public void RandomizeIslands()
    {
        if (islandManager == null)
        {
            islandManager = FindFirstObjectByType<IslandManager>();
        }

        if (islandManager == null)
        {
            Debug.LogWarning("ControlListener: no IslandManager in this scene, so there are no " +
                             "islands to randomize.");
            return;
        }

        islandManager.RandomizeIslands();
    }

    // ------------------------------------------------------------------ wiring

    public void ConnectBaseToControls(NetworkBaseControl newNetBaseControl)
    {
        netBaseControl = newNetBaseControl;
    }

    public void ConnectSpawnManagerToControls(SpawnManager newSpawnManager)
    {
        netSpawnManager = newSpawnManager;
    }

    /// <summary>
    /// Everything that must be true before the triggers mean "play BASH". Copied from
    /// PlateauSelection.Playable, including the last check.
    /// </summary>
    bool Playable()
    {
        // The menu owns the right trigger while it is up, and it is instantiated between the
        // player and the board, so its keys are what the pointer is on anyway.
        if (menu != null && menu.IsOpen)
        {
            return false;
        }

        if (WorldGrab.IsActive)
        {
            return false;
        }

        // Levels, not the gesture's own flag. WorldGrab only goes active on BOTH grips, so
        // without this a player squeezing one grip in preparation still has a live firing beam.
        if (Inputs.LeftGrip || Inputs.RightGrip)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Re-resolve everything the rig owns. Same contract as PlateauSelection.Bind(): these
    /// objects live on PersistentRig and are found by name, never held as scene references.
    /// </summary>
    bool Bind()
    {
        if (Inputs == null)
        {
            GameObject go = GameObject.Find("Input Reader");
            Inputs = go != null ? go.GetComponent<InputReader>() : null;
        }

        if (menu == null)
        {
            GameObject go = GameObject.Find("Menu Manager");
            menu = go != null ? go.GetComponent<MenuControl>() : null;
        }

        if (pointer == null)
        {
            GameObject rig = GameObject.Find("XRRig");
            Transform t = rig != null ? PlateauBoard.FindDescendant(rig.transform, "Pointer") : null;
            pointer = t != null ? t.GetComponent<pointerControl>() : null;
        }

        // The pointer is the only way to pick a gamepiece, so BASH needs it on outside the menu
        // too. Through the setter, never by assigning the field — MenuControl's activeSceneChanged
        // reset has already run and switched it off by now. Same opt-in PlateauSpawnMenu makes
        // for ChasmGame.
        if (menu != null && !menu.keepPointerAlwaysOn)
        {
            menu.SetKeepPointerAlwaysOn(true);
        }

        return Inputs != null;
    }

    private Vector3 RotateVectorInXZ(Vector3 vector, float angleInDegrees)
    {
        float angleInRadians = Mathf.Deg2Rad * angleInDegrees;
        float cos = Mathf.Cos(angleInRadians);
        float sin = Mathf.Sin(angleInRadians);

        float newX = vector.x * cos - vector.z * sin;
        float newZ = vector.x * sin + vector.z * cos;

        return new Vector3(newX, vector.y, newZ);   // preserve the y value
    }
}
