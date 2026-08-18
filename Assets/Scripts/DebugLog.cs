using System.Collections;
using UnityEngine;
using TMPro;

public class DebugLog : MonoBehaviour
{
    public TextMeshPro debugBox;
    public InputReader inputs;

    uint qsize = 10;  // number of messages to keep
    Queue myLogQueue = new Queue();
    RelayVivox relay;

    void Start()
    {
        GameObject inputReader = GameObject.Find("Input Reader");
        if (inputReader != null)
        {
            inputs = inputReader.GetComponent<InputReader>();
        }

        // "Network Manager" is carried over from the lobby by PersistentObject, so it does
        // not exist if you press Play directly in this scene. Resolve it once here rather
        // than on every GUI event, which is what the original did.
        GameObject networkManager = GameObject.Find("Network Manager");
        if (networkManager != null)
        {
            relay = networkManager.GetComponent<RelayVivox>();
        }

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
        if (debugBox == null)
        {
            return;
        }

        // The RIGHT joystick button, not the left. Locomotion moved to the left controller, and
        // pressing the left stick now recentres the rig — clearing the log off the same press
        // meant every recentre wiped the log you were reading to find out why you recentred.
        if (inputs != null && inputs.RightJoystickButtonDown)
        {
            myLogQueue.Clear();
        }

        string roomCode = relay != null ? relay.relayRoomCode : "no room";
        debugBox.SetText("\n" + "Right Joystick Button to clear, " + roomCode + "\n" + string.Join("\n", myLogQueue.ToArray()));
    }
}
