using UnityEngine;

/// <summary>
/// The trail's collision rules, on both CannonLine (the shooter's live local preview, the only
/// one with checkForCollisions on) and NetworkCannonLine (the replicated record of a finished
/// shot).
///
/// gamepiece -> that piece is destroyed; this is how you kill people.
/// obstacle   -> walls and base pads: the shot ends there and YOUR piece dies.
/// island     -> boats (0) and subs (2) die; planes and helicopters fly over.
///
/// The rules are split into two halves that ControlListener can also call, because the trail's own
/// MeshCollider is not the only thing that reports a hit any more: a trigger is evaluated on a
/// physics step and a shot can cross a wall and end between two of them, so ControlListener sweeps
/// each segment as it draws it. See ControlListener.GrowTip. Both halves are safe to run twice for
/// the same collider, which is what lets the two paths overlap without special-casing each other.
/// </summary>
public class LineControls : MonoBehaviour
{
    public Material[] lineMaterials = new Material[5];
    public NetworkBaseControl myNetBaseControl;
    public bool checkForCollisions = false;
    public ControlListener control;

    /// <summary>
    /// False for the movement half of a boat's or plane's turn: it still stops on a wall and still
    /// drowns a surface craft on an island, it just does not take anybody else with it. Those two
    /// pieces have already had their shot — the artillery arc. Sub and helicopter keep it true and
    /// are unchanged in every respect.
    /// </summary>
    public bool killsPieces = true;

    private void Start()
    {
        GameObject go = GameObject.Find("Controls");
        control = go != null ? go.GetComponent<ControlListener>() : null;

        if (control == null)
        {
            // Only the live preview needs this, and only to stop itself when it hits something.
            // A replicated trail is inert, so warn rather than throw.
            Debug.LogWarning("LineControls: no 'Controls' object with a ControlListener in this " +
                             "scene. A trail that hits a wall will not stop itself.");
        }
    }

    public void ChangeMaterial(int n)
    {
        if (lineMaterials == null || lineMaterials.Length == 0)
        {
            return;
        }

        // A seat index arrives here, and a spectator's is -1.
        n = Mathf.Clamp(n, 0, lineMaterials.Length - 1);
        this.GetComponent<Renderer>().material = lineMaterials[n];
    }

    public void OnTriggerEnter(Collider other)
    {
        if (!checkForCollisions)
        { return; }

        Hit(other);

        if (Blocks(other))
        {
            // ControlListener does the ending AND the kill, because it is the only thing that
            // knows which piece is moving before it deselects, and the only thing that can stop
            // the trail growing one more step past whatever just stopped it.
            if (control != null)
            {
                control.StopShotHere();
            }
        }
    }

    /// <summary>
    /// Kill whatever this trail just ran through, if it is a piece and this trail kills pieces.
    /// Idempotent — a piece already switched off is simply switched off again.
    /// </summary>
    public void Hit(Collider other)
    {
        if (!other.CompareTag("gamepiece"))
        {
            return;
        }

        // Only this rule is gated. A harmless movement line still stops on a wall and still drowns
        // on an island — otherwise a boat drives through a wall and a plane's harmless move becomes
        // a way to park inside an island.
        if (!killsPieces)
        {
            return;
        }

        Transform parent = other.transform.parent;
        NetworkBaseControl victim = parent != null ? parent.GetComponent<NetworkBaseControl>() : null;
        if (victim != null)
        {
            victim.TurnOffGamepiece(other.transform.GetSiblingIndex());
        }
    }

    /// <summary>
    /// Does this stop the trail — and so kill the piece that fired it? Walls and base pads always;
    /// an island only for the surface craft that drown on it; a gamepiece never, the trail carries
    /// on through. Pure, deliberately: ControlListener asks it while deciding where a segment ends,
    /// before anything has happened.
    /// </summary>
    public bool Blocks(Collider other)
    {
        if (other.CompareTag("obstacle"))
        {
            return true;
        }

        if (other.CompareTag("island"))
        {
            // Boats and subs are on the water; planes and helicopters are over it. Note this is a
            // different half of the four pieces from the one that lobs a shell — see
            // BashRoot.UsesArtillery.
            return BashRoot.IsSurfaceCraft(ActivePieceIndex());
        }

        return false;
    }

    int ActivePieceIndex()
    {
        if (myNetBaseControl == null || myNetBaseControl.activeGamepiece == null)
        {
            return -1;
        }
        return myNetBaseControl.activeGamepiece.transform.GetSiblingIndex();
    }

    public void ConnectLineToNetworkBase(NetworkBaseControl netBaseControl)
    {
        myNetBaseControl = netBaseControl;
    }
}
