using UnityEngine;

/// <summary>
/// Board targeting for the laser pointer: one raycast per frame, nearest hit, plus absolute
/// control of the visible beam.
///
/// Sits alongside pointerControl on the Pointer object and does not modify it, so the lobby
/// keyboard's code path is untouched. pointerControl stays what it is — a 2 m trigger capsule that
/// filters on the tag "key" — because that is fine for a dozen menu keys and useless for 41
/// plateaus: it tracks a single target with no distance sorting, and Unity trigger callbacks need
/// a Rigidbody on the other collider, which Key.prefab has and plateaus and pieces do not.
///
/// Order 24: after WorldGrab (20), so the hand pose and the board pose are both this frame's.
/// </summary>
[DefaultExecutionOrder(24)]
[RequireComponent(typeof(pointerControl))]
public class PointerBeam : MonoBehaviour
{
    [Tooltip("Metres. 10 covers the far side of the board at maximum content scale from a ring slot.")]
    public float MaxRange = 10f;

    [Tooltip("Fallback sweep radius, in metres. At the smallest board scale a soldier is about a " +
             "1 degree target; an exact ray first so deliberate aiming still wins, this as backup.")]
    public float SphereRadius = 0.01f;

    [Tooltip("How long the beam is drawn when it is pointing at nothing.")]
    public float DefaultLength = 2f;

    public bool HasHit { get; private set; }
    public RaycastHit Hit { get; private set; }
    public Vector3 Origin { get; private set; }
    public Vector3 Direction { get; private set; }

    pointerControl keys;
    Transform visibleBeam;
    Transform dot;

    void Awake()
    {
        keys = GetComponent<pointerControl>();

        // Pointer > Visible Pointer > Dot. Resolved positionally because that is how
        // pointerControl already addresses them and the prefab is not going to be re-skinned.
        if (transform.childCount > 0)
        {
            visibleBeam = transform.GetChild(0);
        }
        if (visibleBeam != null && visibleBeam.childCount > 0)
        {
            dot = visibleBeam.GetChild(0);
        }
    }

    void Update()
    {
        // Physics.autoSyncTransforms is 0 in this project, and RoomContent moves World Root during
        // Update (order 15). Without this the ray hits where the board was at the last FixedUpdate,
        // which reads as a highlight that lags the board and is very hard to diagnose.
        Physics.SyncTransforms();

        // The base of the beam, not the hand: the beam is drawn 10 cm below and offset from the
        // hand, and a ray that does not start where the beam starts puts the dot somewhere the
        // player is not pointing. The Pointer is rolled 90 degrees about X, so its local +Y is the
        // hand's forward and its local (0,-1,0) is the beam's near end.
        Origin = transform.TransformPoint(new Vector3(0f, -1f, 0f));
        Direction = transform.up;

        // QueryTriggerInteraction.Ignore drops this pointer's own trigger capsule and both hand
        // grabber volumes. m_QueriesHitTriggers is 1 in this project, so without it the ray would
        // hit its own collider immediately and never reach the board.
        //
        // Menu keys are NOT triggers — Key.prefab's collider is solid and it is the pointer's
        // capsule that is the trigger — so the ray does hit them. That is wanted: it shortens the
        // beam to the key. Nothing else follows from it, because a key carries neither a
        // PlateauPieceTag nor a PlateauTag, and PlateauSelection stands down while the menu is up.
        bool hit = Physics.Raycast(Origin, Direction, out RaycastHit info, MaxRange,
                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        if (!hit && SphereRadius > 0f)
        {
            hit = Physics.SphereCast(Origin, SphereRadius, Direction, out info, MaxRange,
                                     Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        HasHit = hit;
        Hit = info;

        DrawBeam();
    }

    void DrawBeam()
    {
        if (visibleBeam == null)
        {
            return;
        }

        float length = DefaultLength;

        if (keys != null && keys.currentKey != null)
        {
            Collider keyCollider = keys.currentKey.GetComponent<Collider>();
            if (keyCollider != null)
            {
                length = Mathf.Min(length, Vector3.Distance(Origin, keyCollider.ClosestPoint(Origin)));
            }
        }

        if (HasHit)
        {
            length = Mathf.Min(length, Hit.distance);
        }

        length = Mathf.Max(0.02f, length);

        // The cylinder spans -1..+1 on its own local Y and the beam's near end is at local y = -1,
        // so a beam running from there to `length` is centred at -1 + length/2 with half-length
        // length/2. Written absolutely every frame, which also overwrites pointerControl's
        // OnTriggerStay beam maths — that compounds its own scale and drifts while you hover.
        float half = length * 0.5f;
        visibleBeam.localScale = new Vector3(1f, half, 1f);
        visibleBeam.localPosition = new Vector3(0f, -1f + half, 0f);

        if (dot != null)
        {
            dot.gameObject.SetActive(HasHit || (keys != null && keys.currentKey != null));
        }
    }
}
