using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;
using Unity.Netcode;
using Unity.Services.Relay;
//using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Collections;
using System.Threading.Tasks;
using UnityEngine.UI;

public class GameController : MonoBehaviour
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
        // The keyboard IS the interaction here — there is no menu to gate the pointer behind — so
        // it has to be on from the first frame or no key can ever be pressed.
        //
        // OpeningScene DOES have a MenuControl: it comes in with PersistentRig. Both this and
        // MenuControl.ApplyPointerDefault write the pointer's active state on load, and Unity gives
        // no ordering guarantee between two Start() calls, so the two must AGREE rather than one
        // winning. MenuControl's lobby branch is what makes them agree; do not remove either half.
        if (pointer != null)
        {
            pointer.gameObject.SetActive(true);
        }
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
            if (pressedKey == null)
            {
                return;                       // trigger released without ever touching a key
            }

            pressedKey.MakeSmaller();

            if (pressedKey.keyName == "Enter" && joinedRelay)
            {
                if (joinCode == "" && nickName != "")
                {
                    HostNewRoom();
                }
                else if (nickName != "")
                {
                    TryToJoinRelayVivox();
                }
            }
        }
    }

    /// <summary>
    /// Host path. StartHost() has to complete before the scene can be loaded, because
    /// NetworkManager.SceneManager does not exist until the session is running.
    /// </summary>
    private async void HostNewRoom()
    {
        instructions.SetText("Creating room...");

        try
        {
            await relayVivoxStarter.StartRelayAndVivox(nickName);
        }
        catch (RelayServiceException)
        {
            instructions.SetText("Could not create a room. Press Enter to try again.");
            return;
        }

        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
        {
            instructions.SetText("Could not create a room. Press Enter to try again.");
            return;
        }

        // Server-driven. This is what puts the host — and every client that joins later,
        // via Netcode's synchronization — into StairsGame.
        GameSelector.LoadGameScene(GameRoutes.DefaultScene);
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

        // No LoadScene here, on purpose. Netcode synchronizes this client into whatever scene
        // the host already has open — which may be StairsGame or ChasmGame, depending on what
        // the room is playing right now. Loading a scene here would fight that.
        instructions.SetText("Joining room...");
        instructionsSubfield.gameObject.SetActive(false);
    }

}
