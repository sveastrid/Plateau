using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RightDotController : MonoBehaviour
{

    public GameObject gameManager;
    public Material[] materials;

    private PipeRenderer drawLine;
    private Vector3[] linePoints;


    // Start is called before the first frame update
    void Start()
    {
        linePoints = new Vector3[1];
        drawLine = GetComponent<PipeRenderer>();
        drawLine.startWidth = gameManager.GetComponent<LineDrawer>().lineWidth;
        linePoints[0] = Vector3.zero;
        drawLine.SetPositions(linePoints);
        ChangeColor(4);
    }

    // Update is called once per frame
    void Update()
    {
        drawLine.startWidth = gameManager.GetComponent<LineDrawer>().lineWidth;
        drawLine.SetPositions(linePoints);
    }

    public void ChangeColor(int matNum)
    {
        this.GetComponent<Renderer>().material = materials[matNum];
    }
}
