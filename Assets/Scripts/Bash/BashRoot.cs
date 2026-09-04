using Unity.Netcode;
using UnityEngine;

/// <summary>
/// BASH's content frame. Sits on the scene object "World Root > Board > Bash Root", which also
/// carries a NetworkObject: every BASH object spawned at runtime is parented to it, so Netcode
/// replicates the parenting and every client's bases and trails inherit World Root's pose and
/// scale for free.
///
/// It exists because BASH was written for a world that never moved. Its board sat at world
/// (0, 0.6, 2) and every networked value in it — NetworkBaseControl.activePos/activeRot, the
/// point list a shot is made of — was a plain world-space Vector3. In this project World Root
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
}
