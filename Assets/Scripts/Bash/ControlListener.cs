using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BASH's input loop and firing state machine. One per BASH scene, on the scene-root object
/// "Controls" — logic, no geometry, so it deliberately does not hang under World Root.
///
/// Point at one of your own pieces with the right trigger to select it. What happens then depends
/// on the piece, and the two halves of the four are not the same two halves LineControls uses for
/// its island rule — see BashRoot.UsesArtillery:
///
///   sub, helicopter  the piece spins on the spot at SpinDegreesPerSecond, carrying its coloured
///                    cannon dot round with it, and the left trigger freezes the heading and
///                    fires. Aiming is timing, not steering. The line is lethal, and the piece
///                    teleports to the end of it.
///   boat, plane      two trigger presses. First an artillery arc, aimed with the joystick as
///                    before, lobbed over the water and destroying everything within blastRadius
///                    of where it lands — anyone's pieces, including the shooter's. Then the same
///                    spin-aimed line as above, which carries the piece but harms nobody.
///
/// Either way the turn ends by deselecting the piece, so nothing is left spinning on the board
/// after a player has acted. BASH is still free-for-all: that deselection is the whole of the
/// "turn", and there is no enforced order.
///
/// Everything here works in BashRoot's local space. See BashRoot for why.
///
/// Order 25, matching PlateauSelection: after RoomContent (15) and WorldGrab (20), so the board
/// pose this reads is this frame's rather than last frame's.
/// </summary>
[DefaultExecutionOrder(25)]
public class ControlListener : MonoBehaviour
{
    /// <summary>
    /// Where the local player is in their turn. This used to be a flat chain of ifs over the
    /// trigger edges, which stopped being readable once two of the four pieces took two presses —
    /// and, worse, stopped being checkable, because several of the rules are about which phase
    /// the joystick belongs to.
    ///
    /// The phase lives on the controller, not on the piece, and that is what makes cancelling
    /// behave: a boat that lost its lob to an opened menu returns to Aiming and may lob again, and
    /// a boat that lost its *move* returns to Spinning — it has already spent its shell.
    /// </summary>
    public enum Phase
    {
        Idle,       // nothing selected
        Aiming,     // arc piece selected: the joystick aims, waiting for trigger down
        Lobbing,    // the arc is growing while the trigger is held
        Spinning,   // the heading is auto-rotating, waiting for trigger down
        Firing      // the movement line is growing while the trigger is held
    }

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

    // ---- the artillery arc (boat and plane only) ----------------------------------------------

    // Apex as a fraction of range. 0.25 is exactly a 45 degree launch: the launch angle of this
    // curve is atan(4 * apex / range).
    public float arcApexRatio = 0.25f;

    // Board-local ceiling on the apex. Without it a full-board lob peaks 0.75 m over the table,
    // which in passthrough is at chest height and reads as a wall rather than an arc.
    public float arcMaxApex = 0.35f;

    // A FIXED count, not one point per frame like the straight line. PipeRenderer.SetPositions
    // rebuilds the entire mesh from the entire point list every call, so a list that grows by one
    // point per frame is quadratic work over a shot — and SpawnNetworkCannonLineServerRpc sends
    // the array, so 25 points is 300 bytes, bounded for ever, whatever the board is scaled to.
    public int arcSegments = 24;

    // Below this the arc is a single point: PipeRenderer.GenerateCylinder crosses and
    // FromToRotations the difference between the first two positions, which at zero length is a
    // zero vector, and the mesh comes out degenerate or NaN with no exception.
    public float minArcRange = 0.02f;

    // Board-local, roughly the width of a gamepiece. The single number that decides whether the
    // arc feels like artillery or like a dart; wants judging by eye on a headset.
    public float blastRadius = 0.08f;

    // Long enough that everybody round the table sees where the shell came from and where it
    // landed, short enough that a long game does not fill with arcs.
    public float arcLifetime = 1.5f;

    // The hit rule ignores walls, which is the point of lobbing — but taken literally a held
    // trigger would throw the impact point off the board and into the room behind a player.
    public bool clampArcToBoard = true;

    private PipeRenderer currentCannonLine;
    private List<Vector3> currentLinePoints = new List<Vector3>();
    private Vector3 currentDirection;
    private LineControls newCannonLineControl;
    private float currentCannonSpeed;

    private Phase phase = Phase.Idle;

    private LineControls arcLineControl;
    private PipeRenderer arcPipe;
    private Vector3 arcOrigin;
    private Vector3 arcHeading;
    private float arcRange;

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
        if (Inputs.RightMainTriggerDown && netBaseControl != null &&
            pointer != null && pointer.currentGamepiece != null &&
            netBaseControl.ChangeGamePiece(pointer.currentGamepiece))
        {
            // Selecting a piece mid-sequence starts that piece's turn over at its own first
            // phase, so whatever was half-drawn goes with it. Idle first, so CancelShot does not
            // read the outgoing phase and restart a spin we are about to replace.
            phase = Phase.Idle;
            CancelShot();

            if (BashRoot.UsesArtillery(ActivePieceIndex()))
            {
                phase = Phase.Aiming;   // the joystick aims the shell
            }
            else
            {
                EnterSpinning();        // sub and helicopter: the spin is the aim
            }
        }

        if (netBaseControl == null || netBaseControl.activeGamepiece == null)
        {
            phase = Phase.Idle;
            CancelShot();
            return;
        }

        // A piece killed under the player — by somebody else's line, or by a shell, their own
        // included — is SetActive(false) rather than destroyed, so the reference survives and
        // activeInHierarchy is the test. Same trap ChangeGamePiece documents.
        if (!netBaseControl.activeGamepiece.activeInHierarchy)
        {
            phase = Phase.Idle;
            CancelShot();
            netBaseControl.Deselect();
            return;
        }

        // Idle means nothing is selected, and every path that deselects sets it. A selection can
        // still arrive without a trigger press — SyncOverNetworkClientRpc hands a joiner whatever
        // the server had selected for their base — so adopt it rather than leaving a piece wearing
        // a selection ring that no trigger can do anything with.
        if (phase == Phase.Idle)
        {
            if (BashRoot.UsesArtillery(ActivePieceIndex()))
            {
                phase = Phase.Aiming;
            }
            else
            {
                EnterSpinning();
            }
        }

        Transform frame = BashRoot.Frame;
        if (frame == null)
        {
            return;                     // no Bash Root in this scene: nothing to measure against
        }

        switch (phase)
        {
            case Phase.Aiming:
                if (Inputs.rightJoystick.x != 0f)
                {
                    // Aim the parked piece, and with it the shell it is about to lob.
                    netBaseControl.activeRot.Value =
                        BashRoot.RotateInXZ(netBaseControl.activeRot.Value, -Inputs.rightJoystick.x);
                }
                if (Inputs.LeftMainTriggerDown)
                {
                    BeginArc(frame);
                }
                break;

            case Phase.Lobbing:
                if (Inputs.LeftMainTrigger)
                {
                    ExtendArc();
                }
                else if (Inputs.LeftMainTriggerUp)
                {
                    ResolveArc();
                }
                break;

            case Phase.Spinning:
                // The joystick is deliberately ignored: the spin IS the aim, and the trigger
                // press is what freezes it. The spin itself is drawn by NetworkBaseControl.Update
                // on every client, derived from the server clock.
                if (Inputs.LeftMainTriggerDown)
                {
                    // Order matters and is the whole point of the freeze. Writing activeRot
                    // raises OnValueChanged synchronously on the writer and that callback is what
                    // turns the piece, so the muzzle BeginShot reads a statement later is already
                    // at the frozen heading. Reverse the two and every shot leaves from one
                    // frame's worth of rotation behind where the player aimed.
                    netBaseControl.FreezeSpin();
                    BeginShot(frame);
                    phase = Phase.Firing;
                }
                break;

            case Phase.Firing:
                if (Inputs.LeftMainTrigger && currentLinePoints.Count > 0)
                {
                    ExtendShot();
                }
                else if (Inputs.LeftMainTriggerUp && currentLinePoints.Count > 0)
                {
                    EndCannonLine();
                }
                break;
        }
    }

    /// <summary>The selected piece's kind, or -1 with nothing selected. BashRoot.UsesArtillery and
    /// IsSurfaceCraft both answer false for -1, so this is safe to hand either.</summary>
    int ActivePieceIndex()
    {
        if (netBaseControl == null || netBaseControl.activeGamepiece == null)
        {
            return -1;
        }
        return netBaseControl.activeGamepiece.transform.GetSiblingIndex();
    }

    void EnterSpinning()
    {
        phase = Phase.Spinning;
        if (netBaseControl != null)
        {
            netBaseControl.StartSpin();
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

        // A boat or plane has already had its shot — the arc. Its move carries it but harms
        // nobody. The wall and island rules stay live either way; only the gamepiece branch of
        // LineControls.OnTriggerEnter is gated.
        newCannonLineControl.killsPieces = !BashRoot.UsesArtillery(ActivePieceIndex());

        currentCannonLine = line.GetComponent<PipeRenderer>();

        Transform muzzle = netBaseControl.activeGamepiece.transform.GetChild(3);
        currentLinePoints.Add(frame.InverseTransformPoint(muzzle.position));
        currentCannonLine.SetPositions(currentLinePoints.ToArray());

        // From activeRot rather than a round trip through the transform: FreezeSpin has just
        // written the one authoritative heading, and ToLocalDirection(transform.forward) would
        // re-normalise it through the content scale for no reason.
        currentDirection = netBaseControl.activeRot.Value;
        if (currentDirection.sqrMagnitude <= 0f)
        {
            currentDirection = BashRoot.ToLocalDirection(netBaseControl.activeGamepiece.transform.forward);
        }
    }

    void ExtendShot()
    {
        currentDirection = BashRoot.RotateInXZ(currentDirection,
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

        currentDirection = BashRoot.RotateInXZ(currentDirection,
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

            // The turn is over. AFTER the pose is published: activePos.Value raises
            // OnValueChanged synchronously on the writer and that callback is what teleports the
            // piece, so deselecting first would null activeGamepiece and leave it where it was.
            netBaseControl.Deselect();
        }

        phase = Phase.Idle;

        currentLinePoints.Clear();
        if (newCannonLineControl != null)
        {
            newCannonLineControl.checkForCollisions = false;
            // Released rather than destroyed. CancelShot destroys whatever it still holds, and it
            // now runs on the very next frame of every shot because the deselect above leaves no
            // active piece — which would take the local preview down before the replicated trail
            // has finished its round trip.
            newCannonLineControl = null;
        }
        currentCannonLine = null;
        currentCannonSpeed = initialCannonSpeed;
    }

    /// <summary>
    /// Drop a shot or an arc in progress without publishing it — the menu opened, a world grab
    /// started, or the piece being fired was destroyed under us. Leaves the piece where it was,
    /// and deliberately does NOT deselect: the menu opening mid-shot should not cost you a piece.
    /// </summary>
    void CancelShot()
    {
        // Back to the start of the phase that was interrupted. A boat that lost its lob may lob
        // again; a boat that lost its move has already spent its shell and only gets to move.
        if (phase == Phase.Lobbing)
        {
            phase = Phase.Aiming;
        }
        else if (phase == Phase.Firing)
        {
            EnterSpinning();
        }

        if (currentLinePoints.Count == 0 && newCannonLineControl == null && arcPipe == null)
        {
            return;
        }

        DestroyArc(0f);

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

    // ------------------------------------------------------------------ the artillery arc

    /// <summary>
    /// Start lobbing. Reuses CannonLine.prefab — no new prefab, and therefore nothing to add to
    /// DefaultNetworkPrefabs.asset, which matters: NetworkConfig.ForceSamePrefabs is 1, so
    /// touching that list is a hard connection failure with a generic error for any headset still
    /// on an older build.
    /// </summary>
    void BeginArc(Transform frame)
    {
        currentCannonSpeed = initialCannonSpeed;
        arcRange = 0f;

        GameObject line = Instantiate(cannonLine, frame);
        line.transform.localPosition = Vector3.zero;
        line.transform.localRotation = Quaternion.identity;
        line.transform.localScale = Vector3.one;

        arcLineControl = line.GetComponent<LineControls>();
        arcLineControl.ChangeMaterial(SpawnManager.LocalSeat());
        arcLineControl.ConnectLineToNetworkBase(netBaseControl);
        arcLineControl.checkForCollisions = false;   // the hit test is explicit, in ResolveArc

        arcPipe = line.GetComponent<PipeRenderer>();
        // Visual only, and not just tidiness: the straight line assigns a non-convex
        // MeshCollider.sharedMesh every frame while it grows, and nothing collides with an arc.
        arcPipe.autoCreateCollider = false;

        // Deliberately NOT TurnOffMyColliders(). BeginShot calls it so a line cannot hit the
        // pieces it left from, but the arc has no live collider to protect against — and friendly
        // fire is on, so the impact overlap in ResolveArc has to be able to see the shooter's own
        // base. Disabling them here would silently make your own base the one thing a shell
        // cannot hit.

        Transform muzzle = netBaseControl.activeGamepiece.transform.GetChild(3);
        arcOrigin = frame.InverseTransformPoint(muzzle.position);

        // Flattened: the parabola supplies the whole of the vertical, and RangeToBoardEdge is a
        // 2D slab test that wants a horizontal heading to be exact.
        arcHeading = netBaseControl.activeRot.Value;
        arcHeading.y = 0f;
        arcHeading = arcHeading.sqrMagnitude > 0f ? arcHeading.normalized : Vector3.forward;

        phase = Phase.Lobbing;
    }

    void ExtendArc()
    {
        // Both still steer: the joystick keeps turning the arc while its range grows, exactly as
        // it bends a movement line. The spin sets a movement line's initial direction only.
        arcHeading = BashRoot.RotateInXZ(arcHeading,
            -cannonTiltSpeed * Inputs.rightJoystick.x * Time.deltaTime);

        // The same integrator the straight line's tip uses, sharing the one currentCannonSpeed
        // field rather than a second copy of these two lines that would be free to drift apart
        // the first time anybody tunes one of them. Note cannonAcceleration is added per FRAME,
        // not per second — that is BASH's inherited behaviour, so growth is frame-rate dependent
        // and a shot covers about 22% further at 90 fps than at 72. Fix it in this one shared
        // place if it is ever fixed.
        arcRange += currentCannonSpeed * Time.deltaTime;
        currentCannonSpeed += cannonAcceleration;

        if (clampArcToBoard)
        {
            arcRange = Mathf.Min(arcRange, RangeToBoardEdge(arcOrigin, arcHeading));
        }

        if (arcRange <= minArcRange || arcPipe == null)
        {
            return;                     // see minArcRange: SetPositions would build a NaN mesh
        }

        arcPipe.SetPositions(SampleArc());
    }

    /// <summary>
    /// Sampled fresh from three numbers — origin, heading, range — rather than accumulated point
    /// by point, so the point count is fixed. Vector3.up is board-local +Y here, which is what
    /// makes the arc sit above the water however the board has been turned: the water surface is
    /// board-local y = 0 and the muzzle is about y = 0.008, so the shell leaves and lands at piece
    /// height, which is exactly the height the impact test wants.
    /// </summary>
    Vector3[] SampleArc()
    {
        int segments = Mathf.Max(1, arcSegments);
        float apex = Mathf.Min(arcApexRatio * arcRange, arcMaxApex);

        Vector3[] pts = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            pts[i] = arcOrigin
                   + arcHeading * (arcRange * t)
                   + Vector3.up * (4f * apex * t * (1f - t));   // 4t(1-t) peaks at 1 when t = 0.5
        }
        return pts;
    }

    /// <summary>
    /// How far the arc may reach before its landing point would leave the walls. A 2D slab test
    /// against the board rectangle; the shell stops at the far wall rather than sailing over it
    /// into somebody's living room. Holding the trigger past that simply does nothing more, which
    /// is legible: the arc stops growing at the far wall.
    /// </summary>
    static float RangeToBoardEdge(Vector3 origin, Vector3 heading, float half = 1.5f)
    {
        float limit = Mathf.Min(AxisLimit(origin.x, heading.x, half),
                                AxisLimit(origin.z, heading.z, half));

        // Both axes parallel to their walls means a degenerate heading, not a shot that can leave
        // the board. Do not clamp it to zero.
        return float.IsPositiveInfinity(limit) ? 2f * half : Mathf.Max(0f, limit);
    }

    static float AxisLimit(float origin, float direction, float half)
    {
        if (Mathf.Abs(direction) < 1e-6f)
        {
            return float.PositiveInfinity;      // parallel to this pair of walls
        }

        float t = ((direction > 0f ? half : -half) - origin) / direction;
        return t > 0f ? t : 0f;
    }

    /// <summary>
    /// Trigger up: publish the arc, then kill everything at the impact point. The hit rule is
    /// impact point, anyone — the shell passes harmlessly over walls and islands, and friendly
    /// fire is on, so a shell landing on your own base kills what is there, the piece that fired
    /// it included. That is a real own goal rather than a free no-op. The one-line reversal if it
    /// plays badly is to skip colliders whose NetworkBaseControl == netBaseControl.
    /// </summary>
    void ResolveArc()
    {
        // A mis-tapped trigger. Nothing was ever drawn, so there is nothing to resolve, and the
        // player keeps their shell.
        if (arcRange <= minArcRange)
        {
            DestroyArc(0f);
            phase = Phase.Aiming;
            return;
        }

        Vector3[] pts = SampleArc();
        Vector3 impactLocal = pts[pts.Length - 1];

        if (netSpawnManager != null)
        {
            netSpawnManager.SpawnNetworkCannonLine(pts, arcLifetime);
        }

        // The local preview outlives the round trip to the replicated one and then goes with it,
        // so there is neither a gap nor an arc left standing.
        DestroyArc(arcLifetime);

        // World Root is still being smoothed toward its target pose by RoomContent, and
        // m_AutoSyncTransforms is 0 in this project, so without this the overlap reads collider
        // poses from the last FixedUpdate. Same reason PointerBeam does it.
        Physics.SyncTransforms();

        Vector3 impactWorld = BashRoot.ToWorldPoint(impactLocal);

        // blastRadius is board-local like everything else here, so it has to be taken through the
        // content scale by hand — OverlapSphere takes a world radius. World Root's scale is
        // uniform (RoomContent.cs:75), so lossyScale.x is the whole story.
        Transform frame = BashRoot.Frame;
        float radius = blastRadius * (frame != null ? frame.lossyScale.x : 1f);

        // Gamepiece colliders are TRIGGERS. QueryTriggerInteraction.Collide is mandatory; the
        // default would find nothing at all, silently, and the arc would never kill anybody.
        Collider[] hits = Physics.OverlapSphere(impactWorld, radius,
                                                Physics.DefaultRaycastLayers,
                                                QueryTriggerInteraction.Collide);

        for (int i = 0; i < hits.Length; i++)
        {
            if (!hits[i].CompareTag("gamepiece"))
            {
                continue;
            }

            // The same walk LineControls does: a gamepiece is a direct child of its base.
            Transform parent = hits[i].transform.parent;
            NetworkBaseControl victim = parent != null
                ? parent.GetComponent<NetworkBaseControl>() : null;
            if (victim != null)
            {
                victim.TurnOffGamepiece(hits[i].transform.GetSiblingIndex());
            }
        }

        // If the shooter killed itself there is no movement phase. TurnOffGamepiece does
        // SetActive(false) rather than destroying, so the reference survives and activeInHierarchy
        // is the test.
        if (netBaseControl == null || netBaseControl.activeGamepiece == null ||
            !netBaseControl.activeGamepiece.activeInHierarchy)
        {
            if (netBaseControl != null)
            {
                netBaseControl.Deselect();
            }
            phase = Phase.Idle;
            return;
        }

        EnterSpinning();
    }

    /// <summary>Let go of the arc preview, immediately or after a delay. Idempotent.</summary>
    void DestroyArc(float delay)
    {
        if (arcLineControl != null)
        {
            if (delay > 0f)
            {
                Destroy(arcLineControl.gameObject, delay);
            }
            else
            {
                Destroy(arcLineControl.gameObject);
            }
        }

        arcLineControl = null;
        arcPipe = null;
        arcRange = 0f;
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
}
