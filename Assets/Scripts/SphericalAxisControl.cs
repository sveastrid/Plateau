using System.Collections;
using System.Collections.Generic;
using System.Xml.Serialization;
using TMPro;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UIElements;

public class SphericalAxisControl : MonoBehaviour
{
    //when using methods from this class, x is left and right, z is forward and backward, and y is vertical
    public PipeRenderer xAxis;
    public PipeRenderer zAxis;
    public PipeRenderer yAxis;
    public TextMeshPro xmax;
    public TextMeshPro zmax;

    public void SetAxes()
    {
        float textOffset = .07f;
        //set x axis (horizontal)
        Vector3 start = new Vector3(-1.2f, 0, 0);
        Vector3 end = new Vector3(1.2f, 0, 0);
        xAxis.SetPositions(new Vector3[] { start, end });
        xmax.SetText("theta=0");
        xmax.transform.localPosition = end + textOffset * (.5f * xmax.text.Length) * Vector3.right;

        //set y axis (forward)
        start = new Vector3(0,0,-1.2f);
        end = new Vector3(0,0,1.2f);
        yAxis.SetPositions(new Vector3[] { start, end });

        //set z axis (up)
        start = new Vector3(0,0,0);
        end = new Vector3(0, 1.2f, 0);
        zAxis.SetPositions(new Vector3[] { start, end });
        zmax.SetText("phi = 0");
        zmax.transform.localPosition = end + textOffset * Vector3.up;

    }
}
