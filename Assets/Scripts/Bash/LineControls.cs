using UnityEngine;

/// <summary>
/// The trail's collision rules, on both CannonLine (the shooter's live local preview, the only
/// one with checkForCollisions on) and NetworkCannonLine (the replicated record of a finished
/// shot).
///
/// gamepiece -> that piece is destroyed; this is how you kill people.
/// obstacle   -> walls and base pads: the shot ends there and YOUR piece dies.
/// island     -> boats (0) and subs (2) die; planes and helicopters fly over.
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

        if (other.gameObject.tag == "gamepiece")
        {
            // Only this branch is gated. A harmless movement line still stops on a wall and still
            // drowns on an island — otherwise a boat drives through a wall and a plane's harmless
            // move becomes a way to park inside an island.
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
        else if (other.gameObject.tag == "obstacle")
        {
            // Read the index BEFORE EndCannonLine: it deselects the piece now, so by the time
            // KillPiece runs activeGamepiece is null and ActivePieceIndex() would answer -1 —
            // silently turning off the one rule that punishes a bad shot.
            int n = ActivePieceIndex();
            if (control != null)
            {
                control.EndCannonLine();
            }
            KillPiece(n);
        }
        else if (other.gameObject.tag == "island")
        {
            // Boats and subs are on the water; planes and helicopters are over it. Note this is a
            // different half of the four pieces from the one that lobs a shell — see
            // BashRoot.UsesArtillery.
            int n = ActivePieceIndex();
            if (BashRoot.IsSurfaceCraft(n))
            {
                if (control != null)
                {
                    control.EndCannonLine();
                }
                KillPiece(n);
            }
        }
    }

    int ActivePieceIndex()
    {
        if (myNetBaseControl == null || myNetBaseControl.activeGamepiece == null)
        {
            return -1;
        }
        return myNetBaseControl.activeGamepiece.transform.GetSiblingIndex();
    }

    /// <summary>Kill the piece this trail left from. Takes the index rather than reading it,
    /// because the caller has to capture it before EndCannonLine deselects.</summary>
    void KillPiece(int n)
    {
        if (n < 0 || myNetBaseControl == null)
        {
            return;                     // already dead, or a second hit in the same frame
        }

        myNetBaseControl.TurnOffGamepiece(n);
        myNetBaseControl.SetActiveGamepiece(-1);
    }

    public void ConnectLineToNetworkBase(NetworkBaseControl netBaseControl)
    {
        myNetBaseControl = netBaseControl;
    }
}
