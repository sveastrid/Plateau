using System;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class FunctionRenderer : MonoBehaviour
{
    //when using function from this class, z is the vertical, x is left and right, and y is forward and backward
    public string function;

    //the mins and maxs are all points on the function, not in world or local space
    public int xmin;
    public int xmax;
    public int ymin;
    public int ymax;
    public float zmin;
    public float zmax;
    public float zIncrement = .12f;
    public float xStep = .12f;
    public float yStep = .12f;
    public bool scaleZ = true;
    public bool bigBox = false;

    private Mesh mesh;
    private float zCutoff = 10000;


    void Awake()
    {
        mesh = new Mesh();
        GetComponent<MeshFilter>().mesh = mesh;
        GetComponent<MeshRenderer>().material = new Material(GetComponent<MeshRenderer>().material);
    }

    public void SetFunction(string newFunction, int newxmin, int newxmax, int newymin, int newymax, bool newScaleZ, float newZIncrement)
    {
        function = newFunction;
        xmin = newxmin;
        xmax = newxmax;
        ymin = newymin;
        ymax = newymax;
        scaleZ = newScaleZ;
        zIncrement = newZIncrement;
        GenerateGraph();
    }

    public void SetFunction(string newFunction, int newxmin, int newxmax, int newymin, int newymax, bool newScaleZ,  float newXStep, float newYStep, float newZIncrement)
    {
        function = newFunction;
        xmin = newxmin;
        xmax = newxmax;
        ymin = newymin;
        ymax = newymax;
        scaleZ = newScaleZ;
        zIncrement = newZIncrement;
        xStep = newXStep;
        yStep = newYStep;
        GenerateGraph();
    }

    void GenerateGraph()
    {
        if (function == null)
        {
            throw new InvalidOperationException("No function to graph");
        }
        if (xmax <= xmin)
        {
            throw new InvalidOperationException("Invalid x bounds");
        }
        if (ymax <= ymin)
        {
            throw new InvalidOperationException("Invalid y bounds");
        }
        int numPoints = 100;
        float xIncrement = (float)(xmax - xmin) / numPoints;
        float yIncrement = (float)(ymax - ymin) / numPoints;
        int vertexCount = (numPoints + 1) * (numPoints + 1);
        int triangleCount = numPoints * numPoints * 2;
        Vector3[] vertices = new Vector3[vertexCount];
        int[] triangles = new int[triangleCount * 3];
        int currentIndex = 0;

        float xi = xmin;
        float yi = ymin;
        float zi = 0;
        zmin = FunctionEvaluator.EvaluateFunction(function, xi, yi);
        zmax = FunctionEvaluator.EvaluateFunction(function, xi, yi);
        if (zmin < -zCutoff)
        {
            zmin = -zCutoff;
            zmax = -zCutoff;
        }
        else if (zmin > zCutoff)
        {
            zmin = zCutoff;
            zmax = zCutoff;
        }

        for (int i = 0; i < numPoints+1; i++)
        {
            for (int j = 0; j < numPoints+1; j++)
            {
                xi = xmin + i * xIncrement;
                yi = ymin + j*yIncrement;
                try
                {
                    zi = FunctionEvaluator.EvaluateFunction(function, xi, yi);
                    if (float.IsNaN(zi) || Mathf.Abs(zi) > zCutoff)
                    {
                        Debug.Log("Not a number error");
                        zi = 0;
                    }
                    else if (zi > zmax)
                    {
                        zmax = zi;
                    }
                    else if (zi < zmin)
                    {
                        zmin = zi;
                    }

                    vertices[currentIndex++] = new Vector3(xi, zi, yi);
                }
                catch (InvalidOperationException e)
                {
                    throw e;
                }
            }
        }

        //adjust all of the coordinates based on how they are being scaled or not being scaled
        for (int i = 0; i< vertexCount; i++)
        {
            if (zmax-zmin ==0)
            {
                zmin = zmin - 1;
                zmax = zmax + 1;
            }
            if (scaleZ)
            {
                if (bigBox)
                {
                    // MR cap: big mode used to span 12 m, which puts the surface through the
                    // ceiling and out past the walls of a real classroom. 3 m still reads as
                    // "big" next to the 1.2 m default but stays inside a room.
                    // The matching axes box is in LineDrawer/NetworkLineDrawer — all four
                    // SetAxesAutoScale big-mode call sites must stay in step with these numbers,
                    // or a client's local graph and its networked twin render at different sizes.
                    vertices[i].y = 1.5f * (vertices[i].y - zmin) / (zmax - zmin) - .75f;
                    GetComponent<MeshRenderer>().material.SetFloat("_TopHeight", .75f);
                    GetComponent<MeshRenderer>().material.SetFloat("_BottomHeight", -.75f);
                    vertices[i].x = 3f * (vertices[i].x - xmin) / (xmax - xmin) - 1.5f;
                    vertices[i].z = 3f * (vertices[i].z - ymin) / (ymax - ymin) - 1.5f;
                }
                else
                {
                    vertices[i].y = 1.2f * (vertices[i].y - zmin) / (zmax - zmin) - .6f;
                    GetComponent<MeshRenderer>().material.SetFloat("_TopHeight", .6f);
                    GetComponent<MeshRenderer>().material.SetFloat("_BottomHeight", -.6f);
                    vertices[i].x = 1.2f * (vertices[i].x - xmin) / (xmax - xmin) - .6f;
                    vertices[i].z = 1.2f * (vertices[i].z - ymin) / (ymax - ymin) - .6f;
                }
                
            }
            else
            {
                vertices[i].y = (vertices[i].y)*zIncrement;
                if (Mathf.Abs(vertices[i].y)>1000)
                {
                    throw new InvalidOperationException("Your z scale is too large");
                }
                GetComponent<MeshRenderer>().material.SetFloat("_TopHeight", zmax*zIncrement);
                GetComponent<MeshRenderer>().material.SetFloat("_BottomHeight", zmin*zIncrement);
                vertices[i].x = (vertices[i].x) * xStep;
                vertices[i].z = (vertices[i].z) * yStep;
            }
        }

        if (!scaleZ)
        {
            Vector3 shift = new Vector3(-xStep * (xmin + xmax) / 2, -zIncrement * (zmax + zmin) / 2, -yStep * (ymax + ymin) / 2);
            this.transform.localPosition += shift;
        }


        currentIndex = 0;
        int rowCount = numPoints + 1;
        int colCount = numPoints + 1;
        int startingVertex = 0;

        for (int i = 0; i < colCount - 1; i++)
        {
            for (int j = 0; j < rowCount - 1; j++)
            {
                triangles[currentIndex++] = (startingVertex + j); //bottom left
                triangles[currentIndex++] = startingVertex + j + rowCount; //top left
                triangles[currentIndex++] = (j + 1) + startingVertex; //bottom right

                triangles[currentIndex++] = (startingVertex + j) + rowCount; //top left
                triangles[currentIndex++] = (j + 1) + startingVertex + rowCount; //top right
                triangles[currentIndex++] = (j + 1) + startingVertex; //bottom right
            }
            startingVertex += rowCount;
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
    }
}