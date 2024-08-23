using System.Collections;
using System.Collections.Generic;
using System.Xml.Serialization;
using TMPro;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UIElements;

public class GraphAxisControl : MonoBehaviour
{
    //when using methods from this class, x is left and right, z is forward and backward, and y is vertical
    public PipeRenderer xAxis;
    public PipeRenderer yAxis;
    public PipeRenderer zAxis;
    public TextMeshPro xmin;
    public TextMeshPro xmax;
    public TextMeshPro ymin;
    public TextMeshPro ymax;
    public TextMeshPro zmin;
    public TextMeshPro zmax;
    public Transform grid;
    public float axesXMin;
    public float axesXMax;
    public float axesYMin;
    public float axesYMax;
    public float axesZMin;
    public float axesZMax;
    public float zIncrement;
    public float xIncrement;
    public float yIncrement;

    //The stretch values are how far in the x and y directions we want the grid to go
    public void StretchGrid(float xStretchValue, float yStretchValue)
    {
        float xMaxValue = xStretchValue/2;
        float yMaxValue = yStretchValue/2;
        float gridXIncrement = xStretchValue/10;
        float gridYIncrement = yStretchValue/10;
        Vector3[] newPositions = new Vector3[2];

        for (int i = 0; i < 11; i++)
        {
            newPositions = new Vector3[] { new Vector3(-xMaxValue + i * gridXIncrement, 0, -yMaxValue), new Vector3(-xMaxValue + i * gridXIncrement, 0, yMaxValue) };
            grid.GetChild(i).GetComponent<PipeRenderer>().SetPositions(newPositions);
            newPositions = new Vector3[] { new Vector3(-xMaxValue, 0, -yMaxValue + i * gridYIncrement), new Vector3(xMaxValue, 0, -yMaxValue + i * gridYIncrement) };
            grid.GetChild(i+11).GetComponent<PipeRenderer>().SetPositions(newPositions);
        }
    }

    public void SetAxes(float myxmin, float myxmax, float myymin, float myymax, float myzmin, float myzmax, float newXIncrement, float newYIncrement, float newZIncrement)
    {
        axesXMin = myxmin;
        axesXMax = myxmax;
        axesYMin = myymin;
        axesYMax = myymax;
        axesZMin = myzmin;
        axesZMax = myzmax;
        zIncrement = newZIncrement;
        xIncrement = newXIncrement;
        yIncrement = newYIncrement;

        //note that mymax and min values are coordinates in the graph, not in worldspace or localspace
        float xycoord;
        float xzcoord;
        float yxcoord;
        float yzcoord;
        float zxcoord;
        float zycoord;
        Vector3 start;
        Vector3 end;
        Vector3 gridShift;
        float textOffset = .07f;

        if (myxmax < 0)
        {
            zxcoord = myxmax * xIncrement;
            yxcoord = myxmax * xIncrement;
        }
        else if (myxmin > 0)
        {
            zxcoord = myxmin * xIncrement;
            yxcoord = myxmin * xIncrement;
        }
        else
        {
            zxcoord = 0;
            yxcoord = 0;
        }

        if (myymax < 0)
        {
            xycoord = myymax * yIncrement;
            zycoord = myymax * yIncrement;
        }
        else if (myymin > 0)
        {
            xycoord = myymin * yIncrement;
            zycoord = myymin * yIncrement;
        }
        else
        {
            xycoord = 0;
            zycoord = 0;
        }

        if (myzmax < 0)
        {
            xzcoord = myzmax * zIncrement;
            yzcoord = myzmax * zIncrement;
            gridShift = new Vector3(0, -zIncrement*((myzmin + myzmax)/2 - myzmax), 0);
        }
        else if (myzmin > 0)
        {
            xzcoord = myzmin * zIncrement;
            yzcoord = myzmin * zIncrement;
            gridShift = new Vector3(0, -zIncrement * ((myzmin + myzmax) / 2 - myzmin), 0);
        }
        else
        {
            xzcoord = 0;
            yzcoord = 0;
            gridShift = new Vector3(0, -zIncrement * ((myzmin + myzmax) / 2), 0);
        }

        //set x axis (horizontal)
        start = new Vector3(myxmin * xIncrement, xzcoord, xycoord);
        end = new Vector3(myxmax * xIncrement, xzcoord, xycoord);
        xAxis.SetPositions(new Vector3[] { start, end });
        xmin.SetText("x=" + Mathf.Round(myxmin));
        xmax.SetText("x=" + Mathf.Round(myxmax));
        xmin.transform.localPosition = start + textOffset * (.5f * xmin.text.Length) * Vector3.left;
        xmax.transform.localPosition = end + textOffset * (.5f * xmax.text.Length) * Vector3.right;

        //set y axis (forward)
        start = new Vector3(yxcoord, yzcoord, myymin * yIncrement);
        end = new Vector3(yxcoord, yzcoord, myymax * yIncrement);
        yAxis.SetPositions(new Vector3[] { start, end });
        ymin.SetText("y=" + Mathf.Round(myymin));
        ymax.SetText("y=" + Mathf.Round(myymax));
        ymin.transform.localPosition = start + textOffset * Vector3.back;
        ymax.transform.localPosition = end + textOffset * Vector3.forward;

        //set z axis (up)
        start = new Vector3(zxcoord, myzmin * zIncrement, zycoord);
        end = new Vector3(zxcoord, myzmax * zIncrement, zycoord);
        zAxis.SetPositions(new Vector3[] { start, end });
        zmin.SetText("z=" + Mathf.Round(myzmin));
        zmax.SetText("z=" + Mathf.Round(myzmax));
        zmin.transform.localPosition = start + textOffset * Vector3.down;
        zmax.transform.localPosition = end + textOffset * Vector3.up;

        Vector3 shift = new Vector3(-xIncrement * (myxmax + myxmin) / 2, -zIncrement * (myzmax + myzmin) / 2, -yIncrement * (myymax + myymin) / 2);

        StretchGrid((myxmax - myxmin) * xIncrement, (myymax - myymin) * yIncrement);

        xAxis.transform.localPosition += shift;
        yAxis.transform.localPosition += shift;
        zAxis.transform.localPosition += shift;
        xmin.transform.localPosition += shift;
        xmax.transform.localPosition += shift;
        zmin.transform.localPosition += shift;
        zmax.transform.localPosition += shift;
        ymin.transform.localPosition += shift;
        ymax.transform.localPosition += shift;
        grid.transform.localPosition += gridShift;
        
    }

    public void SetAxesAutoScale(float myxmin, float myxmax, float myymin, float myymax, float myzmin, float myzmax, float boxXSize, float boxYSize, float boxZSize)
    {
        axesXMin = myxmin;
        axesXMax = myxmax;
        axesYMin = myymin;
        axesYMax = myymax;
        axesZMin = myzmin;
        axesZMax = myzmax;

        xIncrement = boxXSize/(myxmax - myxmin);
        yIncrement = boxYSize/(myymax - myymin);
        zIncrement = boxZSize / (myzmax - myzmin);

        //note that mymax and min values are coordinates in the graph, not in worldspace or localspace
        float xycoord;
        float xzcoord;
        float yxcoord;
        float yzcoord;
        float zxcoord;
        float zycoord;
        Vector3 start;
        Vector3 end;
        float textOffset = .07f;
        Vector3 gridShift;

        if (myxmax < 0)
        {
            zxcoord = myxmax * xIncrement;
            yxcoord = myxmax * xIncrement;
        }
        else if (myxmin > 0)
        {
            zxcoord = myxmin * xIncrement;
            yxcoord = myxmin * xIncrement;
        }
        else
        {
            zxcoord = 0;
            yxcoord = 0;
        }

        if (myymax < 0)
        {
            xycoord = myymax * yIncrement;
            zycoord = myymax * yIncrement;
        }
        else if (myymin > 0)
        {
            xycoord = myymin * yIncrement;
            zycoord = myymin * yIncrement;
        }
        else
        {
            xycoord = 0;
            zycoord = 0;
        }

        if (myzmax < 0)
        {
            xzcoord = myzmax * zIncrement;
            yzcoord = myzmax * zIncrement;
            gridShift = new Vector3(0, -zIncrement * ((myzmin + myzmax) / 2 - myzmax), 0);
        }
        else if (myzmin > 0)
        {
            xzcoord = myzmin * zIncrement;
            yzcoord = myzmin * zIncrement;
            gridShift = new Vector3(0, -zIncrement * ((myzmin + myzmax) / 2 - myzmin), 0);
        }
        else
        {
            xzcoord = 0;
            yzcoord = 0;
            gridShift = new Vector3(0, -zIncrement * ((myzmin + myzmax) / 2), 0);
        }

        //set x axis (horizontal)
        start = new Vector3(myxmin * xIncrement, xzcoord, xycoord);
        end = new Vector3(myxmax * xIncrement, xzcoord, xycoord);
        xAxis.SetPositions(new Vector3[] { start, end });
        xmin.SetText("x=" + Mathf.Round(myxmin));
        xmax.SetText("x=" + Mathf.Round(myxmax));
        xmin.transform.localPosition = start + textOffset * (.5f * xmin.text.Length) * Vector3.left;
        xmax.transform.localPosition = end + textOffset * (.5f * xmax.text.Length) * Vector3.right;

        //set y axis (forward)
        start = new Vector3(yxcoord, yzcoord, myymin * yIncrement);
        end = new Vector3(yxcoord, yzcoord, myymax * yIncrement);
        yAxis.SetPositions(new Vector3[] { start, end });
        ymin.SetText("y=" + Mathf.Round(myymin));
        ymax.SetText("y=" + Mathf.Round(myymax));
        ymin.transform.localPosition = start + textOffset * Vector3.back;
        ymax.transform.localPosition = end + textOffset * Vector3.forward;

        //set z axis (up)
        start = new Vector3(zxcoord, myzmin * zIncrement, zycoord);
        end = new Vector3(zxcoord, myzmax * zIncrement, zycoord);
        zAxis.SetPositions(new Vector3[] { start, end });
        zmin.SetText("z=" + Mathf.Round(myzmin));
        zmax.SetText("z=" + Mathf.Round(myzmax));
        zmin.transform.localPosition = start + textOffset * Vector3.down;
        zmax.transform.localPosition = end + textOffset * Vector3.up;

        Vector3 shift = new Vector3(-xIncrement * (myxmax + myxmin) / 2, -zIncrement * (myzmax + myzmin) / 2, -yIncrement * (myymax + myymin) / 2);

        StretchGrid(boxXSize, boxYSize);

        xAxis.transform.localPosition += shift;
        yAxis.transform.localPosition += shift;
        zAxis.transform.localPosition += shift;
        xmin.transform.localPosition += shift;
        xmax.transform.localPosition += shift;
        zmin.transform.localPosition += shift;
        zmax.transform.localPosition += shift;
        ymin.transform.localPosition += shift;
        ymax.transform.localPosition += shift;
        grid.transform.localPosition += gridShift;
    }
}
