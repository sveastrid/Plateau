using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

public class DebugLog : MonoBehaviour
{
    public TextMeshPro debugBox;
    public InputReader inputs;

    uint qsize = 10;  // number of messages to keep
    Queue myLogQueue = new Queue();

    void Start()
    {
        inputs = GameObject.Find("Input Reader").GetComponent<InputReader>(); 
        Debug.Log("Started up logging.");
    }

    void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        myLogQueue.Enqueue("[" + type + "] : " + logString);
        if (type == LogType.Exception)
            myLogQueue.Enqueue(stackTrace);
        while (myLogQueue.Count > qsize)
            myLogQueue.Dequeue();
    }

    void OnGUI()
    {
        if (inputs.LeftJoystickButtonDown)
        {
            myLogQueue.Clear();
        }
        debugBox.SetText("\n" + "Left Joystick Button to clear, " + GameObject.Find("Network Manager").GetComponent<RelayVivox>().relayRoomCode + "\n" + string.Join("\n", myLogQueue.ToArray()));
    }
}
