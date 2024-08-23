using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;
using Unity.Netcode;

public class LHController : MonoBehaviour
{
    public InputReader input;
    public GameObject controller;
    public GameObject debugger;
    public bool rightHanded;

    // Start is called before the first frame update
    void Start()
    {
        rightHanded = true;
        debugger.SetActive(true);
        controller.SetActive(false);
        input = GameObject.Find("Input Reader").GetComponent<InputReader>();
    }

    // Update is called once per frame
    void Update()
    {
        if (input.LeftControllerFound)
        {
            debugger.SetActive(false);
            controller.SetActive(true);
        }
    }
}
