using TMPro;
using UnityEngine;

public class VisibleWhenLooking : MonoBehaviour
{
    public float maxDistance = 10f; // The maximum distance to check for the object
    private bool objectOff;
    private RelayVivox relay;

    void Start()
    {
        // "Network Manager" is carried over from the lobby by PersistentObject, so it does
        // not exist if you press Play directly in this scene. Resolve it once here rather
        // than on every frame, which is what the original did.
        GameObject networkManager = GameObject.Find("Network Manager");
        if (networkManager != null)
        {
            relay = networkManager.GetComponent<RelayVivox>();
        }
    }

    void Update()
    {
        CheckIfLookingAtObject();
    }

    void CheckIfLookingAtObject()
    {
        if (Camera.main == null)
        {
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2, Screen.height / 2, 0));
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, maxDistance))
        {
            if (hit.transform == this.transform)
            {
                //set the text to the relay room code
                string roomCode = relay != null ? relay.relayRoomCode : "no room";
                this.transform.GetChild(0).GetComponent<TextMeshPro>().SetText("Room Code: \r\n" + roomCode);
                for (int i = 0; i<this.transform.childCount; i++)
                {
                    this.transform.GetChild(i).gameObject.SetActive(true);
                }
                objectOff = false;
            }
            else if (!objectOff)
            {
                for (int i = 0; i < this.transform.childCount; i++)
                {
                    this.transform.GetChild(i).gameObject.SetActive(false);
                }
                objectOff = true;
            }
        }
        else if (!objectOff)
        {
            for (int i = 0; i < this.transform.childCount; i++)
            {
                this.transform.GetChild(i).gameObject.SetActive(false);
            }
            objectOff = true;
        }
    }
}
