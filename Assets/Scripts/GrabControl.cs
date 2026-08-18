using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks whatever grabbable objects a hand's grabber volume is currently overlapping.
/// Tag a game object "Grabbable" to make it pickable.
/// </summary>
public class GrabControl : MonoBehaviour
{
    public const string GrabbableTag = "Grabbable";

    public bool lineHit;
    public List<GameObject> lines = new List<GameObject>();

    public void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.tag == GrabbableTag)
        {
            lineHit = true;
            lines.Add(GrabbableRoot(other.transform).gameObject);
        }
    }

    public void OnTriggerExit(Collider other)
    {
        if (other.gameObject.tag == GrabbableTag)
        {
            if (lines.Count > 0)
            {
                lines.Remove(GrabbableRoot(other.transform).gameObject);
            }

            if (lines.Count == 0)
            {
                lineHit = false;
            }
        }
    }

    public void LineDestroyed(GameObject deletedLine)
    {
        if (lines.Contains(deletedLine))
        {
            lines.Remove(deletedLine);
        }

        if (lines.Count == 0)
        {
            lineHit = false;
        }
    }

    /// <summary>
    /// Walk up to the outermost object that is not already held by a grabber. The parent
    /// null check is load-bearing: the original loop only terminated because every grabbable
    /// object lived under a known container, and ran off the top of the hierarchy without it.
    /// </summary>
    private static Transform GrabbableRoot(Transform hit)
    {
        Transform current = hit;
        while (current.parent != null &&
               current.parent.name != "Right Grabber" &&
               current.parent.name != "Left Grabber")
        {
            current = current.parent;
        }
        return current;
    }
}
