using UnityEngine;

/// <summary>
/// Applies the room's shared board placement to this scene's World Root.
///
/// World Root is the content frame: everything the game owns hangs under it, and nothing a player
/// owns does. That is what makes "move and resize everything in the room except the players" (see
/// WorldGrab) structural rather than a filter, and it is why the gesture scales this transform and
/// never the rig — scaling the rig would drag the user's tracked hands away from the real hands
/// they can see through passthrough.
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

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

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

        // Uniform, always. Non-uniform scale on a board game skews colliders, breaks normals, and
        // is not a thing anybody wants; making it unrepresentable is cheaper than policing it.
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
