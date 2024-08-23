using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

public class VisibleWhenLooking : MonoBehaviour
{
    public float maxDistance = 10f; // The maximum distance to check for the object
    private bool objectOff;

    void Start()
    {
        
    }

    void Update()
    {
        CheckIfLookingAtObject();
    }

    void CheckIfLookingAtObject()
    {
        Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2, Screen.height / 2, 0));
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, maxDistance))
        {
            if (hit.transform == this.transform)
            {
                //set the text to the relay room code
                this.transform.GetChild(0).GetComponent<TextMeshPro>().SetText("Room Code: \r\n" + GameObject.Find("Network Manager").GetComponent<RelayVivox>().relayRoomCode);
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
