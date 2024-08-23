using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LineControl : MonoBehaviour
{
    public ulong pairedNetLineId;
    public Material[] materials;
    public int materialNumber=4;
    public GrabControl leftGrabber;
    public GrabControl rightGrabber;

    private void Awake()
    {
        if (materials.Length>0)
        {
            this.GetComponent<Renderer>().material = materials[materialNumber];
        }
        leftGrabber = GameObject.Find("Left Grabber").GetComponent<GrabControl>();
        rightGrabber = GameObject.Find("Right Grabber").GetComponent<GrabControl>();
    }
    public void ChangeMaterial(int matNum)
    {
        materialNumber = matNum;
        this.GetComponent<Renderer>().material = materials[matNum];
    }

    private void OnDestroy()
    {
        if (leftGrabber.lines.Contains(this.gameObject))
        {
            leftGrabber.LineDestroyed(this.gameObject);
        }
        if(rightGrabber.lines.Contains(this.gameObject))
        {
            rightGrabber.LineDestroyed(this.gameObject);
        }
    }
}
