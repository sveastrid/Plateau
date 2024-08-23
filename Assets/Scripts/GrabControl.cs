using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GrabControl : MonoBehaviour
{
    public bool lineHit;
    public List<GameObject> lines = new List<GameObject>();

    public void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.tag == "Line")
        {
            Transform currentObject = other.transform;
            while ((currentObject.parent.name != "Drawings") && (currentObject.parent.name != "Right Grabber") && (currentObject.parent.name != "Left Grabber"))
            {
                currentObject = currentObject.parent;
            }
            lineHit = true;
            lines.Add(currentObject.gameObject);
        }
    }

    public void OnTriggerExit(Collider other)
    {
        
        if (other.gameObject.tag == "Line")
        {
            Transform currentObject = other.transform;
            while ((currentObject.parent.name != "Drawings") && (currentObject.parent.name != "Right Grabber") && (currentObject.parent.name != "Left Grabber"))
            {
                currentObject = currentObject.parent;
            }
            if (lines.Count > 0)
            {
                lines.Remove(currentObject.gameObject);
            }
            
            if (lines.Count == 0)
            {
                lineHit=false;
            }
        }
    }

    public void LineDestroyed(GameObject deletedLine)
    {
        if (lines.Contains(deletedLine))
        {
            lines.Remove(deletedLine);
        }
        
        if(lines.Count == 0)
        {
            lineHit=false;
        }
    }
}
