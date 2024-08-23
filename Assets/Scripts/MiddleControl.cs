using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MiddleControl : MonoBehaviour
{
    public Transform rh;
    public Transform lh;

    // Start is called before the first frame update
    void Start()
    {
        rh = GameObject.Find("Right Hand").transform;
        lh = GameObject.Find("Left Hand").transform;
    }

    // Update is called once per frame
    void Update()
    {
        this.transform.position = (rh.position + lh.position) / 2;
        if ((rh.position - lh.position).magnitude > 0)
        {
            this.transform.forward = rh.position - lh.position;
        }
    }
}
