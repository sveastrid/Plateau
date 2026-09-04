using System.IO;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider), typeof(Rigidbody))]
[ExecuteInEditMode]

public class PipeRenderer : MonoBehaviour
{
    public int numSides = 12;
    public Vector3[] positions;
    public int positionCount;
    public float startWidth = 1;
    public bool autoCreateCollider = true;

    private Mesh mesh;
    private Rigidbody rb;
    private MeshCollider meshCollider;

    void Awake()
    {
        mesh = new Mesh();
        GetComponent<MeshFilter>().mesh = mesh;
        rb = GetComponent<Rigidbody>();
        meshCollider = GetComponent<MeshCollider>();

        // Automatically configure Rigidbody
        rb.isKinematic = true;
        rb.useGravity = false;

        // Automatically configure MeshCollider
        meshCollider.convex = false;

        if (positions != null)
        {
            positionCount = positions.Length;
        }
        GenerateCylinder();
    }

    [ContextMenu("Regenerate Pipe")]
    public void RegeneratePipe()
    {
        if (positions != null && positions.Length > 0)
        {
            positionCount = positions.Length;
            GenerateCylinder();
        }
    }

    public void SetPositions(Vector3[] newPositions)
    {
        positions = newPositions;
        positionCount = newPositions.Length;
        GenerateCylinder();
    }

    public void ChangeWidth(float width)
    {
        startWidth = width;
        GenerateCylinder();
    }

    public void GetPositions(ref Vector3[] currentPositions)
    {
        currentPositions = new Vector3[(int)positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            currentPositions[i] = positions[i];
        }
    }

    void GenerateCylinder()
    {

        if (positionCount < 1)
        {
            return;
        }

        if (positionCount == 1)
        {
            positionCount = 2;
            positions = new Vector3[] { positions[0], positions[0] - .1f * startWidth * Vector3.forward };
        }

        int vertexCount = numSides * positionCount + 2 * (numSides + 1);
        int triangleCount = numSides * 6 * (positionCount - 1) + 6 * (numSides + 2 * numSides);
        Vector3[] vertices = new Vector3[vertexCount];
        int[] triangles = new int[triangleCount];
        int currentIndex = 0;

        //start cap vertices
        vertices[currentIndex++] = positions[0] - .85f * startWidth * (positions[1] - positions[0]).normalized;
        Vector3 axis = Vector3.Cross(Vector3.up, positions[1] - positions[0]);
        float angle = Vector3.Angle(Vector3.up, positions[1] - positions[0]);
        Vector3[] nextPoints = GetCircleVertices(startWidth * .85f, positions[0] - .5f * startWidth * (positions[1] - positions[0]).normalized, axis, angle);
        for (int i = currentIndex; i < currentIndex + numSides; i++)
        {
            vertices[i] = nextPoints[i - currentIndex];
        }
        currentIndex += numSides;



        //bottom vertices
        axis = Vector3.Cross(Vector3.up, positions[1] - positions[0]);
        angle = Vector3.Angle(Vector3.up, positions[1] - positions[0]);
        nextPoints = GetCircleVertices(startWidth, positions[0], axis, angle);
        for (int i = currentIndex; i < currentIndex + numSides; i++)
        {
            vertices[i] = nextPoints[i - currentIndex];
        }
        currentIndex += numSides;

        Quaternion nextRotation;
        if (positionCount > 2)
        {
            //middle vertices
            nextRotation = Quaternion.FromToRotation(positions[1] - positions[0], positions[2] - positions[0]);
            for (int i = 0; i < numSides; i++)
            {
                nextPoints[i] = nextRotation * (nextPoints[i] - positions[0]) + positions[1];
                vertices[i + currentIndex] = nextPoints[i];
            }
            currentIndex += numSides;
            for (int i = 2; i < positionCount - 1; i++)
            {
                nextRotation = Quaternion.FromToRotation(positions[i] - positions[i - 2], positions[i + 1] - positions[i - 1]);
                for (int j = 0; j < numSides; j++)
                {
                    nextPoints[j] = nextRotation * (nextPoints[j] - positions[i - 1]) + positions[i];
                    vertices[j + currentIndex] = nextPoints[j];
                }
                currentIndex += numSides;
            }
        }

        //top vertices
        if (positionCount > 2)
        {
            nextRotation = Quaternion.FromToRotation(positions[positionCount - 1] - positions[positionCount - 3], positions[positionCount - 1] - positions[positionCount - 2]);
        }
        else
        {
            nextRotation = Quaternion.identity;
        }

        for (int j = 0; j < numSides; j++)
        {
            nextPoints[j] = nextRotation * (nextPoints[j] - positions[positionCount - 2]) + positions[positionCount - 1];
            vertices[j + currentIndex] = nextPoints[j];
        }
        currentIndex += numSides;

        //end cap vertices
        nextRotation = Quaternion.FromToRotation(positions[positionCount - 1] - positions[positionCount - 2], Vector3.up);
        Quaternion reverseRotation = Quaternion.FromToRotation(Vector3.up, positions[positionCount - 1] - positions[positionCount - 2]);
        for (int j = 0; j < numSides; j++)
        {
            vertices[j + currentIndex] = reverseRotation * (.85f * (nextRotation * (nextPoints[j] - positions[positionCount - 1]))) + positions[positionCount - 1] + .5f * startWidth * (positions[positionCount - 1] - positions[positionCount - 2]).normalized;
        }
        currentIndex += numSides;
        vertices[currentIndex] = positions[positionCount - 1] + .85f * startWidth * (positions[positionCount - 1] - positions[positionCount - 2]).normalized;


        //triangles
        //start cap
        currentIndex = 0;

        for (int i = 0; i < numSides; i++)
        {
            triangles[currentIndex++] = 0;
            triangles[currentIndex++] = i + 1;
            triangles[currentIndex++] = 1 + (i + 1) % numSides;
        }


        //all the middle caps
        int startingVertex = 1;
        for (int j = 0; j < positionCount + 1; j++)
        {
            for (int i = 0; i < numSides; i++)
            {
                triangles[currentIndex++] = i + startingVertex;  //bottom left
                triangles[currentIndex++] = i + startingVertex + numSides;   //top left
                triangles[currentIndex++] = (i + 1) % numSides + startingVertex;   //bottom right
                triangles[currentIndex++] = i + startingVertex + numSides;  //top left
                triangles[currentIndex++] = (i + 1) % numSides + (startingVertex + numSides); //top right
                triangles[currentIndex++] = (i + 1) % numSides + startingVertex;  //bottom right
            }
            startingVertex += numSides;
        }

        //end cap
        for (int i = 0; i < numSides; i++)
        {
            triangles[currentIndex++] = vertexCount - 1;
            triangles[currentIndex++] = startingVertex + (i + 1) % numSides;
            triangles[currentIndex++] = startingVertex + i;
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        if (autoCreateCollider)
        {
            SyncCollisionMesh();
        }
    }

    public void SyncCollisionMesh()
    {
        meshCollider.sharedMesh = mesh;
    }

    private Vector3[] GetCircleVertices(float radius, Vector3 center, Vector3 rotationAxis, float rotationAngle)
    {
        Vector3[] points = new Vector3[numSides];
        float angleStep = 2 * Mathf.PI / numSides;
        for (int i = 0; i < numSides; i++)
        {
            Vector3 newPoint = new Vector3(radius * Mathf.Cos(i * angleStep), 0, radius * Mathf.Sin(i * angleStep));
            points[i] = center + Quaternion.AngleAxis(rotationAngle, rotationAxis) * newPoint;
        }
        return points;
    }

    void OnDrawGizmos()
    {
        if (meshCollider != null && meshCollider.sharedMesh != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireMesh(meshCollider.sharedMesh, transform.position, transform.rotation, transform.localScale);
        }
    }
}