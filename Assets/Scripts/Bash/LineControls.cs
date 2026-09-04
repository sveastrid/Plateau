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
            Transform parent = other.transform.parent;
            NetworkBaseControl victim = parent != null ? parent.GetComponent<NetworkBaseControl>() : null;
            if (victim != null)
            {
                victim.TurnOffGamepiece(other.transform.GetSiblingIndex());
            }
        }
        else if (other.gameObject.tag == "obstacle")
        {
            if (control != null)
            {
                control.EndCannonLine();
            }
            KillActivePiece();
        }
        else if (other.gameObject.tag == "island")
        {
            // Boats and subs are on the water; planes and helicopters are over it.
            int activeGamepieceNum = ActivePieceIndex();
            if (activeGamepieceNum == 0 || activeGamepieceNum == 2)
            {
                if (control != null)
                {
                    control.EndCannonLine();
                }
                KillActivePiece();
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

    void KillActivePiece()
    {
        int n = ActivePieceIndex();
        if (n < 0)
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
