using Unity.Netcode;
using UnityEngine;

/// <summary>
/// BASH's content frame. Sits on the scene object "World Root > Board > Bash Root", which also
/// carries a NetworkObject: every BASH object spawned at runtime is parented to it, so Netcode
/// replicates the parenting and every client's bases and trails inherit World Root's pose and
/// scale for free.
///
/// It exists because BASH was written for a world that never moved. Its board sat at world
/// (0, 0.6, 2) and every networked value in it — NetworkBaseControl.activeRot and the poses it
/// sends, the point list a shot is made of — was a plain world-space Vector3. In this project World Root
/// moves, rotates and rescales continuously under the two-grip world grab, so a world-space value
/// for anything parented under it goes stale the instant somebody grabs the board. This is the
/// same problem PlateauBoard already solved, and the answer is the same one CLAUDE.md states:
/// everything is measured in the content frame's local space, which is what makes it invariant.
///
/// Every conversion in BASH goes through the four helpers below, so there is one place to look
/// when a shot comes out of the wrong end of the cannon. Note the direction helpers normalise:
/// InverseTransformDirection divides by the content scale, and the cannon speeds are board-local
/// units, which is what makes a shot cross the same fraction of the board however big the players
/// have made it.
/// </summary>
public class BashRoot : MonoBehaviour
{
    public static BashRoot Instance { get; private set; }

    /// <summary>The frame itself, or null when no BASH scene is loaded.</summary>
    public static Transform Frame => Instance != null ? Instance.transform : null;

    /// <summary>
    /// The frame's own NetworkObject — the spawn parent SpawnManager hands to TrySetParent. Null
    /// until Netcode has spawned this in-scene object, which is a normal state for the first few
    /// frames after a scene load, so callers poll rather than assume.
    /// </summary>
    public static NetworkObject SpawnParent
    {
        get
        {
            if (Instance == null || Instance.netObject == null || !Instance.netObject.IsSpawned)
            {
                return null;
            }
            return Instance.netObject;
        }
    }

    private NetworkObject netObject;

    void Awake()
    {
        Instance = this;
        netObject = GetComponent<NetworkObject>();

        if (netObject == null)
        {
            Debug.LogError("BashRoot: no NetworkObject on '" + name + "'. Nothing can be spawned " +
                           "into the board frame without one, so bases and trails will not " +
                           "replicate.");
        }
    }

    void OnDestroy()
    {
        // Guarded, like every other Instance singleton here: a scene switch can construct the
        // next one before destroying this one.
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public static Vector3 ToLocalPoint(Vector3 world)
    {
        Transform f = Frame;
        return f != null ? f.InverseTransformPoint(world) : world;
    }

    public static Vector3 ToWorldPoint(Vector3 local)
    {
        Transform f = Frame;
        return f != null ? f.TransformPoint(local) : local;
    }

    public static Vector3 ToLocalDirection(Vector3 world)
    {
        Transform f = Frame;
        Vector3 v = f != null ? f.InverseTransformDirection(world) : world;
        return v.sqrMagnitude > 0f ? v.normalized : v;
    }

    public static Vector3 ToWorldDirection(Vector3 local)
    {
        Transform f = Frame;
        Vector3 v = f != null ? f.TransformDirection(local) : local;
        return v.sqrMagnitude > 0f ? v.normalized : v;
    }

    // ------------------------------------------------------------------ the four gamepieces

    // A piece's kind is its sibling index under its base, and that integer is already on the wire
    // in every selection RPC. These are names for it, not a new concept.
    public const int Boat = 0, Plane = 1, Sub = 2, Helicopter = 3;

    /// <summary>
    /// Boat and plane lob a shell before they move. Sub and helicopter shoot as they move.
    ///
    /// This is NOT the same pairing as IsSurfaceCraft below, and the two are one line apart on
    /// purpose: same four indices, two different halves, and written as bare integer comparisons
    /// they are indistinguishable at a glance and invite being "tidied" into agreement.
    /// </summary>
    public static bool UsesArtillery(int kind)
    {
        return kind == Boat || kind == Plane;
    }

    /// <summary>Boat and sub are on the water, so an island kills them. Plane and helicopter are
    /// over it. See UsesArtillery for why this is spelled out rather than inlined.</summary>
    public static bool IsSurfaceCraft(int kind)
    {
        return kind == Boat || kind == Sub;
    }

    /// <summary>
    /// Rotate a board-local direction about board-local +Y, preserving its y component.
    ///
    /// This lives here rather than on ControlListener because two files now need it — the shot's
    /// steering and NetworkBaseControl's spinning aim — and every client has to derive the same
    /// heading from the same numbers. Two copies of a rotation helper that must agree exactly is
    /// how the cannon dot ends up somewhere different on each headset.
    /// </summary>
    public static Vector3 RotateInXZ(Vector3 vector, float angleInDegrees)
    {
        float angleInRadians = Mathf.Deg2Rad * angleInDegrees;
        float cos = Mathf.Cos(angleInRadians);
        float sin = Mathf.Sin(angleInRadians);

        float newX = vector.x * cos - vector.z * sin;
        float newZ = vector.x * sin + vector.z * cos;

        return new Vector3(newX, vector.y, newZ);   // preserve the y value
    }
}
