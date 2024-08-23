using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using Unity.Services.Relay;
//using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using Unity.Collections;
using System.Threading.Tasks;
using UnityEngine.UI;

public class GameController : NetworkBehaviour
{
    //So you can get user inputs
    public InputReader inputs;
    public RelayVivox relayVivoxStarter;
    public GameObject inputField;
    public TextMeshPro instructions;
    public TextMeshPro instructionsSubfield;

    public Transform rh;
    public Transform lh;
    public pointerControl pointer;

    public static string joinCode="";
    public static string nickName = "";
    private bool joinedRelay = false;
    private keyInfo pressedKey;

    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {
        //Get input from keyboard and update the joincode string
        if (inputs.RightMainTriggerDown)
        {
            if (pointer.currentKey != null)
            {
                pressedKey = pointer.currentKey;
            }

            //typing the relay room name
            if (!joinedRelay)
            {
                if (pointer.currentLetter == "Clear")
                {
                    joinCode = "";
                    inputField.GetComponent<TMP_InputField>().text = joinCode;
                }
                else if (pointer.currentLetter == "Enter")
                {
                    joinedRelay = true;
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                    instructions.SetText("Pick a Username:");
                    instructions.color = new Color(0,.98f,.6f,1);
                    instructions.transform.Translate(new Vector3(0, -.07f, 0));
                    inputField.GetComponent<TMP_InputField>().placeholder.gameObject.GetComponent<TextMeshProUGUI>().text = "Username...";
                    instructionsSubfield.gameObject.SetActive(false);
                    
                }
                else if (pointer.currentLetter == "Back")
                {
                    if (joinCode.Length > 0)
                    {
                        joinCode = joinCode.Remove(joinCode.Length - 1);
                        inputField.GetComponent<TMP_InputField>().text = joinCode;
                    }
                }
                else
                {
                    joinCode = joinCode + pointer.currentLetter;
                    inputField.GetComponent<TMP_InputField>().text = joinCode;
                }

                if (pointer.currentKey != null)
                {
                    pressedKey.MakeBigger();
                }
            }
            //typing in name to display and joining relay and vivox
            else
            {
                if (pointer.currentKey != null)
                {
                    pressedKey.MakeBigger();
                }


                if (pointer.currentLetter == "Clear")
                {
                    nickName = "";
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                }
                else if (pointer.currentLetter == "Back")
                {
                    if (nickName.Length > 0)
                    {
                        nickName = nickName.Remove(nickName.Length - 1);
                        inputField.GetComponent<TMP_InputField>().text = nickName;
                    }
                }
                else if (pointer.currentLetter == "Enter")
                {
                    
                }
                else if (pointer.currentLetter != "Enter")
                {
                    nickName = nickName + pointer.currentLetter;
                    inputField.GetComponent<TMP_InputField>().text = nickName;
                }
            }
        }

        if (inputs.RightMainTriggerUp)
        {
            if (pressedKey != null)
            {
                pressedKey.MakeSmaller();
            }
            if (pressedKey.keyName == "Enter" && joinedRelay)
            {
                if ((joinCode == "") && (nickName != ""))
                {
                    relayVivoxStarter.StartRelayAndVivox(nickName);
                    //stores the relayroomcode in the RelayVivox script
                    SceneManager.LoadScene("SecondScene");
                }
                else if (nickName != "")
                {
                    TryToJoinRelayVivox();
                }
            }

        }
    }

    private async void TryToJoinRelayVivox()
    {
        try
        {
            await relayVivoxStarter.JoinRelayAndVivox(nickName, joinCode);
        }
        catch (RelayServiceException)
        {
            Debug.Log("Caught exception");
            instructions.SetText("Wrong Room Code");
            instructionsSubfield.gameObject.SetActive(true);
            instructionsSubfield.SetText("Please enter a new code or press Enter to start a new room");
            instructions.color = new Color(0.22f, .94f, 1f, 1);
            instructions.transform.Translate(new Vector3(0, .07f, 0));
            inputField.GetComponent<TMP_InputField>().placeholder.gameObject.GetComponent<TextMeshProUGUI>().text = "Room Code...";
            joinedRelay = false;
            joinCode = "";
            inputField.GetComponent<TMP_InputField>().text = joinCode;
            return;
        }

        SceneManager.LoadScene("SecondScene");
    }
    
}
