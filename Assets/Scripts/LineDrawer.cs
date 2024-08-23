using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Schema;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

public class LineDrawer : MonoBehaviour
{
    public InputReader inputs;
    public GameObject Line;
    public Transform RightDot;
    public Transform LeftDot;
    public Transform Drawings;
    public Transform rh;
    public Transform lh;
    public Transform Middle;
    public NetworkLineDrawer netLineDraw;
    public Queue<GameObject> UnpairedLines = new Queue<GameObject>();
    public ulong currentLineNetId;
    public ulong myNetworkDrawingsObjectId;
    public GameObject myNetworkDrawings;
    public float minPointDistance = .1f;
    public int colorNumber = 4;
    public Transform myCam;
    public GameObject graph;
    public GameObject graphAxes;
    public GameObject sphericalAxes;
    public bool drawStraight = false;
    public GrabControl rightGrabber;
    public GrabControl leftGrabber;
    public string currentAction = "listening";
    public GameObject plane;

    // Bounds for the two-handed resize, in multiples of the drawing's authored size.
    public float MinDrawingScale = 0.05f;
    public float MaxDrawingScale = 5f;

    private PipeRenderer rightDrawLine;
    private PipeRenderer leftDrawLine;
    private List<Vector3> rightLinePoints;
    private List<Vector3> leftLinePoints;
    public float lineWidth = 0.02f;
    private float originalDistance;

    

    // Start is called before the first frame update
    void Start()
    {
        rightLinePoints = new List<Vector3>();
        leftLinePoints = new List<Vector3>();
    }

    // Update is called once per frame
    void Update()
    {
        if (currentAction == "listening")
        {
            if (inputs.RightMainTriggerDown)
            {
                currentAction = "drawingRH";
            }
            else if (inputs.LeftMainTriggerDown)
            {
                currentAction = "drawingLH";
            }
            else if (inputs.ButtonBDown)
            {
                currentAction = "deletingLines";
            }
            else if (inputs.leftJoystick[1] != 0)
            {
                currentAction = "changingLineWidth";
            }
            else if (inputs.RightGripDown && rightGrabber.lineHit)
            {
                currentAction = "grabbingRH";
            }
            else if (inputs.LeftGripDown && leftGrabber.lineHit)
            {
                currentAction = "grabbingLH";
            }
            else if ((inputs.LeftGripDown && inputs.RightGrip) || (inputs.RightGripDown && inputs.LeftGrip))
            {
                currentAction = "resizingDrawings";
            }
        }

        if (currentAction == "drawingRH")
        {
            if (inputs.RightMainTriggerDown)
            {
                rightLinePoints.Clear();
                rightLinePoints.Add(RightDot.position);
                makeNewLine(RightDot.position, true);
                rightDrawLine.autoCreateCollider = false;
                rightDrawLine.startWidth = lineWidth;
                rightDrawLine.positionCount = 1;
                rightDrawLine.SetPositions(rightLinePoints.ToArray());
            }
            else if (inputs.RightMainTrigger)
            {
                if (Vector3.Distance(rightLinePoints[rightLinePoints.Count - 1], RightDot.position) > minPointDistance)
                {
                    rightLinePoints.Add(RightDot.position);

                    if (drawStraight && rightLinePoints.Count > 2)
                    {
                        rightLinePoints.RemoveAt(rightLinePoints.Count - 2);
                    }

                    rightDrawLine.positionCount = rightLinePoints.Count;
                    rightDrawLine.SetPositions(rightLinePoints.ToArray());
                }
            }
            else if (inputs.RightMainTriggerUp)
            {
                rightDrawLine.SyncCollisionMesh();
                Drawings.GetChild(Drawings.childCount - 1).transform.position = new Vector3(0, 0, 0);
                Vector3[] positions = new Vector3[rightLinePoints.Count];
                rightDrawLine.GetPositions(ref positions);
                netLineDraw.CreateNetworkLine(myNetworkDrawingsObjectId, colorNumber, positions, rightDrawLine.startWidth);
                currentAction = "listening";
            }
        }

        else if (currentAction == "drawingLH")
        {
            if (inputs.LeftMainTriggerDown)
            {
                leftLinePoints.Clear();
                leftLinePoints.Add(LeftDot.position);
                makeNewLine(LeftDot.position, false);
                leftDrawLine.autoCreateCollider = false;
                leftDrawLine.startWidth = lineWidth;
                leftDrawLine.positionCount = 1;
                leftDrawLine.SetPositions(leftLinePoints.ToArray());
            }
            else if (inputs.LeftMainTrigger)
            {
                if (Vector3.Distance(leftLinePoints[leftLinePoints.Count - 1], LeftDot.position) > minPointDistance)
                {
                    leftLinePoints.Add(LeftDot.position);

                    if (drawStraight && leftLinePoints.Count > 2)
                    {
                        leftLinePoints.RemoveAt(leftLinePoints.Count - 2);
                    }

                    leftDrawLine.positionCount = leftLinePoints.Count;
                    leftDrawLine.SetPositions(leftLinePoints.ToArray());
                }
            }
            else if (inputs.LeftMainTriggerUp)
            {
                leftDrawLine.SyncCollisionMesh();
                Drawings.GetChild(Drawings.childCount - 1).transform.position = new Vector3(0, 0, 0);
                Vector3[] positions = new Vector3[leftLinePoints.Count];
                leftDrawLine.GetPositions(ref positions);
                netLineDraw.CreateNetworkLine(myNetworkDrawingsObjectId, colorNumber, positions, leftDrawLine.startWidth);
                currentAction = "listening";
            }
        }
        else if (currentAction == "deletingLines")
        {
            if (inputs.ButtonBDown && Drawings.childCount > 0)
            {
                GameObject lastLine = Drawings.GetChild(Drawings.childCount - 1).gameObject;
                netLineDraw.RemoveNetworkLine(lastLine.GetComponent<LineControl>().pairedNetLineId);
                Destroy(lastLine);
            }
            currentAction = "listening";
        }
        else if (currentAction == "changingLineWidth")
        {
            if (lineWidth < .2 && inputs.leftJoystick[1] > 0)
            {
                lineWidth += inputs.leftJoystick[1] * .003f;
            }
            else if (lineWidth > .005 && inputs.leftJoystick[1] < 0)
            {
                lineWidth += inputs.leftJoystick[1] * .003f;
            }
            else if (inputs.leftJoystick[1] == 0)
            {
                currentAction = "listening";
            }
        }
        else if (currentAction == "grabbingRH")
        {
            if (inputs.RightGripDown && rightGrabber.lineHit)
            {
                myNetworkDrawings.SetActive(false);
                foreach (GameObject line in rightGrabber.lines)
                {
                    line.transform.SetParent(rightGrabber.transform);
                }
            }

            if (inputs.RightGripUp && rightGrabber.lineHit)
            {
                ulong[] networkLineIds = new ulong[rightGrabber.lines.Count];
                Vector3[] positions = new Vector3[rightGrabber.lines.Count];
                Quaternion[] rotations = new Quaternion[rightGrabber.lines.Count];
                Vector3[] scales = new Vector3[rightGrabber.lines.Count];
                int i = 0;
                foreach (GameObject line in rightGrabber.lines)
                {
                    line.transform.SetParent(Drawings);
                    networkLineIds[i] = rightGrabber.lines[i].GetComponent<LineControl>().pairedNetLineId;
                    positions[i] = rightGrabber.lines[i].transform.position;
                    rotations[i] = rightGrabber.lines[i].transform.rotation;
                    scales[i] = rightGrabber.lines[i].transform.localScale;
                    i++;
                }
                netLineDraw.MoveNetworkLines(networkLineIds, positions, rotations, scales);
                currentAction = "listening";
            }
        }
        else if (currentAction == "grabbingLH")
        {
            if (inputs.LeftGripDown && leftGrabber.lineHit)
            {
                myNetworkDrawings.SetActive(false);
                foreach (GameObject line in leftGrabber.lines)
                {
                    line.transform.SetParent(leftGrabber.transform);
                }
            }
            if (inputs.LeftGripUp && leftGrabber.lineHit)
            {
                ulong[] networkLineIds = new ulong[leftGrabber.lines.Count];
                Vector3[] positions = new Vector3[leftGrabber.lines.Count];
                Quaternion[] rotations = new Quaternion[leftGrabber.lines.Count];
                Vector3[] scales = new Vector3[leftGrabber.lines.Count];
                int i = 0;
                foreach (GameObject line in leftGrabber.lines)
                {
                    line.transform.SetParent(Drawings);
                    networkLineIds[i] = leftGrabber.lines[i].GetComponent<LineControl>().pairedNetLineId;
                    positions[i] = leftGrabber.lines[i].transform.position;
                    rotations[i] = leftGrabber.lines[i].transform.rotation;
                    scales[i] = leftGrabber.lines[i].transform.localScale;
                    i++;
                }
                netLineDraw.MoveNetworkLines(networkLineIds, positions, rotations, scales);
                currentAction = "listening";
            }
        }
        else if (currentAction == "resizingDrawings")
        {
            //first frame both grips are held
            if ((inputs.LeftGripDown && inputs.RightGrip) || (inputs.LeftGrip && inputs.RightGripDown))
            {
                Drawings.SetParent(Middle);
                originalDistance = (rh.position - lh.position).magnitude;
                myNetworkDrawings.SetActive(false);
            }
            //While both grips are being held
            if (inputs.LeftGrip && inputs.RightGrip)
            {
                //scaling
                if (originalDistance > 0)
                {
                    float scaleChange = (((rh.position - lh.position).magnitude) / originalDistance);

                    // MR clamp: a real room has walls. Unbounded two-handed scaling either
                    // pushes drawings out through them or shrinks them to a speck, and there
                    // is no "reset scale" control to get back from either.
                    float current = Middle.localScale.x;
                    if (current > 0f)
                    {
                        float target = Mathf.Clamp(current * scaleChange, MinDrawingScale, MaxDrawingScale);
                        scaleChange = target / current;
                    }

                    Middle.localScale = Middle.localScale * scaleChange;
                    originalDistance = (rh.position - lh.position).magnitude;
                }
            }
            else if (inputs.LeftGripUp || inputs.RightGripUp)
            {
                Drawings.SetParent(null);
                ulong[] networkLineIds = new ulong[Drawings.childCount];
                Vector3[] positions = new Vector3[Drawings.childCount];
                Quaternion[] rotations = new Quaternion[Drawings.childCount];
                Vector3[] scales = new Vector3[Drawings.childCount];

                for (int i = 0; i < Drawings.childCount; i++)
                {
                    networkLineIds[i] = Drawings.GetChild(i).GetComponent<LineControl>().pairedNetLineId;
                    positions[i] = Drawings.GetChild(i).position;
                    rotations[i] = Drawings.GetChild(i).rotation;
                    scales[i] = Drawings.GetChild(i).localScale;
                }

                netLineDraw.MoveNetworkDrawingsObject(myNetworkDrawingsObjectId, Drawings.position, Drawings.rotation, Drawings.localScale);
                netLineDraw.MoveNetworkLines(networkLineIds, positions, rotations, scales);
                currentAction = "listening";
            }
        }
    }

    private void makeNewLine(Vector3 startPos, bool rightHand)
    {
        GameObject NewLine = Instantiate(Line, startPos - Drawings.position, Quaternion.identity);
        NewLine.GetComponent<LineControl>().ChangeMaterial(colorNumber);
        NewLine.transform.SetParent(Drawings);
        NewLine.transform.position = Vector3.zero;
        if (rightHand)
        {
            rightDrawLine = NewLine.GetComponent<PipeRenderer>();
        }
        else
        {
            leftDrawLine = NewLine.GetComponent<PipeRenderer>();
        }
        
        UnpairedLines.Enqueue(NewLine);
    }

    public void Setup(ulong newNetDrawControlId, GameObject newMyNetworkDrawings)
    {
        myNetworkDrawingsObjectId = newNetDrawControlId;
        myNetworkDrawings = newMyNetworkDrawings;
    }

    public void ClearLineLists()
    {
        UnpairedLines.Clear();
    }

    public void DrawFunction(string function, int xmin, int xmax, int ymin, int ymax,  float xIncrement, float yIncrement, float zIncrement, bool zScale, bool bigScale)
    {
        Vector3 center = myCam.position + myCam.forward.normalized * 1f + myCam.up.normalized * .5f;
        GameObject NewGraph = Instantiate(graph, Vector3.zero, Quaternion.identity);
        NewGraph.transform.SetParent(Drawings);
        try
        {
            if (zScale)
            {
                NewGraph.GetComponent<FunctionRenderer>().bigBox = bigScale;
                NewGraph.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, zScale, zIncrement);
            }
            else
            {
                NewGraph.GetComponent<FunctionRenderer>().bigBox = bigScale;
                NewGraph.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, zScale, xIncrement, yIncrement, zIncrement);
            }
        }
        catch (InvalidOperationException e)
        {
            Destroy(NewGraph);
            throw e;
        }
        UnpairedLines.Enqueue(NewGraph);
        NewGraph.transform.position += center;
        netLineDraw.CreateNetworkGraph(myNetworkDrawingsObjectId, function, center, xmin, xmax, ymin, ymax, NewGraph.GetComponent<FunctionRenderer>().zmin, NewGraph.GetComponent<FunctionRenderer>().zmax, xIncrement, yIncrement, zIncrement, zScale, bigScale);

        DrawLocalGraphAxes(xmin, xmax, ymin, ymax, NewGraph.GetComponent<FunctionRenderer>().zmin, NewGraph.GetComponent<FunctionRenderer>().zmax, zScale, zIncrement, xIncrement, yIncrement, NewGraph.transform, bigScale);
    }

    public void DrawLocalGraphAxes(float x1, float x2, float y1, float y2, float z1, float z2, bool scaleZ, float zIncrement, float xIncrement, float yIncrement, Transform parent, bool bigScale)
    {
        Vector3 center = myCam.position + myCam.forward.normalized * 1f + myCam.up.normalized * .5f;
        GameObject NewAxes = Instantiate(graphAxes, Vector3.zero, Quaternion.identity);
        NewAxes.transform.SetParent(parent);
        UnpairedLines.Enqueue(NewAxes);
        if (scaleZ)
        {
            if (bigScale)
            {
                NewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 3, 3, 1.25f);
            }
            else
            {
                NewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 1.2f, 1.2f, 1.2f);
            }
        }
        else
        {
            NewAxes.GetComponent<GraphAxisControl>().SetAxes(x1, x2, y1, y2, z1, z2, xIncrement, yIncrement, zIncrement);
        }
        
        NewAxes.transform.position += center;
    }

    public void DrawGraphAxes()
    {
        Vector3 center = myCam.position + myCam.forward.normalized * 1f + myCam.up.normalized * .5f;
        GameObject NewAxes = Instantiate(graphAxes, Vector3.zero, Quaternion.identity);
        NewAxes.transform.SetParent(Drawings);
        UnpairedLines.Enqueue(NewAxes);
        NewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(-5,5,-5,5,-5,5, 1.2f, 1.2f, 1.2f);
        NewAxes.transform.position += center;

        netLineDraw.CreateNetworkGraphAxes(myNetworkDrawingsObjectId, center, -5,5,-5,5,-5,5);
    }

    public void DrawSpherical()
    {
        Vector3 center = myCam.position + myCam.forward.normalized * 1f + myCam.up.normalized * .5f;
        GameObject NewSphericalAxes = Instantiate(sphericalAxes, Vector3.zero, Quaternion.identity);
        NewSphericalAxes.transform.SetParent(Drawings);
        UnpairedLines.Enqueue(NewSphericalAxes);
        NewSphericalAxes.GetComponent<SphericalAxisControl>().SetAxes();
        NewSphericalAxes.transform.position += center;

        netLineDraw.CreateNetworkSphericalAxes(myNetworkDrawingsObjectId, center);
    }

    public void DeleteAllLinesOwned()
    {
        netLineDraw.DeleteAllLines(myNetworkDrawingsObjectId);
        for (int i = 0; i < Drawings.childCount; i++)
        {
            Transform line = Drawings.GetChild(i).transform;
            Destroy(line.gameObject);
        }
        UnpairedLines.Clear();
    }

    public void MakePlane()
    {
        Vector3 center = myCam.position + myCam.forward.normalized * 1f + myCam.up.normalized * .5f;
        GameObject NewPlane = Instantiate(plane, Vector3.zero, Quaternion.identity);
        NewPlane.transform.SetParent(Drawings);
        UnpairedLines.Enqueue(NewPlane);
        NewPlane.transform.position += center;

        netLineDraw.MakeNetworkPlane(myNetworkDrawingsObjectId,center);
    }
}

