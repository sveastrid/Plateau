using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuControl : MonoBehaviour
{
    public InputReader inputs;
    public GameObject Menu1;
    public GameObject Menu2;
    public GameObject ConfirmDeleteMenu;
    public Transform myCam;
    public GameObject pointer;
    public GameObject LeftDot;
    public GameObject RightDot;
    public GameObject SceneManager;
    public Transform RightHand;
    public seatControl seats;
    public RelayVivox relayVivoxInfo;
    public PlayerControls myPlayer;
    public NetworkLineDrawer netDrawLine;
    public LineDrawer drawLine;
    public GameObject GraphMenu;
    // Passthrough is a per-user comfort setting, like brightness — deliberately NOT a
    // NetworkVariable. One student switching to full VR must not drag the class with them.
    public PassthroughController passthrough;

    private pointerControl currentPointer;
    private bool graphOpen = false;
    private bool deleteMenuOpen = false;
    private GameObject currentMenu;
    private keyInfo OnKey;
    private keyInfo OffKey;
    private keyInfo StraightOnKey;
    private keyInfo StraightOffKey;
    private keyInfo FunctionKey;
    private keyInfo XMinKey;
    private keyInfo XMaxKey;
    private keyInfo YMinKey;
    private keyInfo YMaxKey;
    private keyInfo ZScaleKey;
    private keyInfo ZStepKey;
    private keyInfo XStepKey;
    private keyInfo YStepKey;
    private keyInfo BigScaleKey;
    private keyInfo SmallScaleKey;
    private static int colorIndex = 4;
    private static string functionString = "";
    private TextMeshPro functionText;
    private TextMeshPro errorText;
    private TextMeshPro xMinText;
    private TextMeshPro xMaxText;
    private TextMeshPro yMinText;
    private TextMeshPro yMaxText;
    private TextMeshPro zStepText;
    private TextMeshPro xStepText;
    private TextMeshPro yStepText;
    private string tempString = "";
    private bool changingFunction = true;
    private TextMeshPro currentTextBox;
    private static bool autoScaleZ = true;
    private keyInfo pressedKey;
    private static int xMin = -5;
    private static int xMax = 5;
    private static int yMin = -5;
    private static int yMax = 5;
    private static float zStepNum = 1;
    private static float xStepNum = 1;
    private static float yStepNum = 1;
    private static bool bigScale = false;
    private GameObject incrementKeys;
    private GameObject scaleSizeKeys;

    private void Start()
    {
        relayVivoxInfo = GameObject.Find("Network Manager").GetComponent<RelayVivox>();
        
    }

    // Update is called once per frame
    void Update()
    {
        if (drawLine.currentAction != "listening")
        {
            return;
        }

        if (inputs.ButtonYDown && currentMenu == null)
        {
            OpenDeleteMenu();
            deleteMenuOpen = true;
        }
        else if (inputs.ButtonYDown && deleteMenuOpen)
        {
            CloseMenu();
            deleteMenuOpen = false;
        }


        if (inputs.ButtonXDown && !deleteMenuOpen)
        {
            if (myPlayer.GetIsRoomOwner())
            {
                if (currentMenu == null)
                {
                    OpenMenu1();
                }
                else
                {
                    CloseMenu();
                    graphOpen = false;
                }
            }
            else
            {
                if (currentMenu == null)
                {
                    OpenMenu2();
                }
                else
                {
                    CloseMenu();
                    graphOpen = false;
                }
            }
        }
        

        if (deleteMenuOpen)
        {
            if (inputs.RightMainTriggerDown && currentMenu != null)
            {
                if (currentPointer.currentKey != null)
                {
                    pressedKey = currentPointer.currentKey;
                    pressedKey.MakeBigger();
                }
            }
            else if (inputs.RightMainTriggerUp && pressedKey != null)
            {
                pressedKey.MakeSmaller();
                if (pressedKey.keyName == "Yes")
                {
                    drawLine.DeleteAllLinesOwned();
                    CloseMenu();
                    deleteMenuOpen = false;
                }
                else if (pressedKey.keyName == "No")
                {
                    CloseMenu();
                    deleteMenuOpen = false;
                }
            }
        }
        else if (!graphOpen)
        {
            if (inputs.RightMainTriggerDown && currentMenu != null)
            {
                if (currentPointer.currentKey != null)
                {
                    pressedKey = currentPointer.currentKey;
                    pressedKey.MakeBigger();
                }

                if (currentPointer.currentLetter == "On" && !seats.AssignedSeatsOn())
                {
                    
                }
                else if (currentPointer.currentLetter == "Off" && seats.AssignedSeatsOn())
                {
                    seats.TurnOffAssignedSeats();
                    OnKey.TurnOff();
                    OffKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Straight On")
                {
                    drawLine.drawStraight = true;
                    StraightOffKey.TurnOff();
                    StraightOnKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Straight Off")
                {
                    drawLine.drawStraight = false;
                    StraightOnKey.TurnOff();
                    StraightOffKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Delete All")
                {
                    
                }
                else if (currentPointer.currentLetter == "color")
                {
                    currentPointer.currentKey.MakeBigger();
                    for (int i = 0; i < 7; i++)
                    {
                        if (currentPointer.currentKey.gameObject == currentMenu.transform.GetChild(1).GetChild(i + 1).gameObject)
                        {
                            colorIndex = i;
                        }
                        else
                        {
                            currentMenu.transform.GetChild(1).GetChild(i + 1).GetComponent<keyInfo>().MakeSmaller();
                        }
                        drawLine.colorNumber = colorIndex;
                        RightDot.GetComponent<RightDotController>().ChangeColor(colorIndex);
                        LeftDot.GetComponent<LeftDotController>().ChangeColor(colorIndex);

                    }

                }
                else if (currentPointer.currentLetter == "Draw Grid")
                {
                    
                }
                else if (currentPointer.currentLetter == "Graph")
                {
                    Destroy(currentMenu);
                    float distance = 1.3f;
                    currentMenu = Instantiate(GraphMenu, myCam.position + distance*myCam.forward.normalized, Quaternion.identity);
                    currentMenu.transform.rotation = myCam.rotation;
                    currentMenu.transform.position += -.7f * currentMenu.transform.right;
                    functionText = currentMenu.transform.GetChild(0).GetChild(1).GetComponent<TextMeshPro>();
                    errorText = currentMenu.transform.GetChild(0).GetChild(8).GetComponent<TextMeshPro>();
                    incrementKeys = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).gameObject;
                    scaleSizeKeys = currentMenu.transform.GetChild(0).GetChild(9).GetChild(14).gameObject;
                    xMinText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(7).GetComponent<TextMeshPro>();
                    xMaxText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(8).GetComponent<TextMeshPro>();
                    yMinText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(9).GetComponent<TextMeshPro>();
                    yMaxText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(10).GetComponent<TextMeshPro>();
                    zStepText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(1).GetComponent<TextMeshPro>();
                    xStepText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(3).GetComponent<TextMeshPro>();
                    yStepText = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(5).GetComponent<TextMeshPro>();
                    FunctionKey = currentMenu.transform.GetChild(0).GetChild(0).GetComponent<keyInfo>();
                    XMinKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(2).GetComponent<keyInfo>();
                    XMaxKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(3).GetComponent<keyInfo>();
                    YMinKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(5).GetComponent<keyInfo>();
                    YMaxKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(6).GetComponent<keyInfo>();
                    ZScaleKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(11).GetComponent<keyInfo>();
                    ZStepKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(0).GetComponent<keyInfo>();
                    XStepKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(2).GetComponent<keyInfo>();
                    YStepKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(13).GetChild(4).GetComponent<keyInfo>();
                    BigScaleKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(14).GetChild(1).GetComponent<keyInfo>();
                    SmallScaleKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(14).GetChild(0).GetComponent<keyInfo>();
                    FunctionKey.KeepOn();
                    changingFunction = true;
                    functionText.SetText(functionString);
                    xMinText.SetText(xMin.ToString());
                    xMaxText.SetText(xMax.ToString());
                    yMinText.SetText(yMin.ToString());
                    yMaxText.SetText(yMax.ToString());
                    zStepText.SetText(zStepNum.ToString());
                    xStepText.SetText(xStepNum.ToString());
                    yStepText.SetText(yStepNum.ToString());

                    graphOpen = true;
                    if (autoScaleZ)
                    {
                        ZScaleKey.KeepOn();
                        incrementKeys.SetActive(false);
                        scaleSizeKeys.SetActive(true);
                    }
                    else
                    {
                        ZScaleKey.TurnOff();
                        incrementKeys.SetActive(true);
                        scaleSizeKeys.SetActive(false);
                    }
                    if (bigScale)
                    {
                        BigScaleKey.KeepOn();
                        SmallScaleKey.TurnOff();
                    }
                    else
                    {
                        BigScaleKey.TurnOff();
                        SmallScaleKey.KeepOn();
                    }
                }
            }
        }
        else  //when the graph menu is open
        {
            if (inputs.RightMainTriggerDown && currentMenu != null)
            {
                if (currentPointer.currentKey != null)
                {
                    pressedKey = currentPointer.currentKey;
                    pressedKey.MakeBigger();
                }

                if(currentPointer.currentLetter == "Enter")
                {

                }
                else if (currentPointer.currentLetter == "Back")
                {
                    if (changingFunction)
                    {
                        if (functionString.Length > 0)
                        {
                            functionString = functionString.Remove(functionString.Length - 1);
                            functionText.SetText(functionString);
                        }
                    }
                    else
                    {
                        if (tempString.Length > 0)
                        {
                            tempString = tempString.Remove(tempString.Length - 1);
                            currentTextBox.SetText(tempString);
                        }
                    }
                }
                else if (currentPointer.currentLetter == "Clear")
                {
                    changingFunction = true;
                    TurnGraphKeysOff();
                    FunctionKey.KeepOn();
                    functionString = "";
                    functionText.SetText(functionString);
                    xMin = -5;
                    xMax = 5;
                    yMin = -5;
                    yMax = 5;
                    xStepNum = 1;
                    yStepNum = 1;
                    zStepNum = 1;
                    autoScaleZ = true;
                    xMinText.SetText(xMin.ToString());
                    xMaxText.SetText(xMax.ToString());
                    yMinText.SetText(yMin.ToString());
                    yMaxText.SetText(yMax.ToString());
                    xStepText.SetText(xStepNum.ToString());
                    yStepText.SetText(yStepNum.ToString());
                    zStepText.SetText(zStepNum.ToString());
                    ZScaleKey.KeepOn();
                    incrementKeys.SetActive(false);
                    scaleSizeKeys.SetActive(true);
                    bigScale = false;
                    BigScaleKey.TurnOff();
                    SmallScaleKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "xmin")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = xMinText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    XMinKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "xmax")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = xMaxText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    XMaxKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "ymin")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = yMinText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    YMinKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "ymax")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = yMaxText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    YMaxKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "zStep")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = zStepText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    ZStepKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "xStep")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = xStepText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    XStepKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "yStep")
                {
                    changingFunction = false;
                    tempString = "";
                    currentTextBox = yStepText;
                    currentTextBox.SetText(tempString);
                    TurnGraphKeysOff();
                    YStepKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Function Name")
                {
                    changingFunction = true;
                    TurnGraphKeysOff();
                    FunctionKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Auto")
                {
                    autoScaleZ = !autoScaleZ;
                    if (autoScaleZ)
                    {
                        ZScaleKey.KeepOn();
                        incrementKeys.SetActive(false);
                        scaleSizeKeys.SetActive(true);
                    }
                    else
                    {
                        ZScaleKey.TurnOff();
                        incrementKeys.SetActive(true);
                        scaleSizeKeys.SetActive(false);
                    }
                    changingFunction = true;
                    TurnGraphKeysOff();
                    FunctionKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Small")
                {
                    bigScale = false;
                    BigScaleKey.TurnOff();
                    SmallScaleKey.KeepOn();
                }
                else if (currentPointer.currentLetter == "Big")
                {
                    bigScale = true;
                    BigScaleKey.KeepOn();
                    SmallScaleKey.TurnOff();
                }
                else if (currentPointer.currentLetter == "Plane")
                {

                }
                else
                {
                    if (changingFunction)
                    {
                        functionString += currentPointer.currentLetter;
                        functionText.SetText(functionString);
                    }
                    else
                    {
                        tempString += currentPointer.currentLetter;
                        currentTextBox.SetText(tempString);
                    }
                    
                }
            }
        }

        if (currentMenu != null)
        {
            if (inputs.RightMainTriggerUp && pressedKey != null && pressedKey.keyName != "color")
            {
                pressedKey.MakeSmaller();
                if (pressedKey.keyName == "Enter")
                {
                    bool parsingSuccessful = true;
                    try
                    {
                        xMin = int.Parse(xMinText.text);
                        xMax = int.Parse(xMaxText.text);
                        yMin = int.Parse(yMinText.text);
                        yMax = int.Parse(yMaxText.text);
                    }
                    catch (FormatException)
                    {
                        errorText.SetText("Bounds and step size must be integers.");
                        parsingSuccessful = false;
                    }
                    try
                    {
                        zStepNum = float.Parse(zStepText.text);
                        xStepNum = float.Parse(xStepText.text);
                        yStepNum = float.Parse(yStepText.text);
                    }
                    catch (FormatException)
                    {
                        errorText.SetText("Error reading the step sizes");
                        parsingSuccessful = false;
                    }
                    if (parsingSuccessful)
                    {
                        try
                        {
                            drawLine.DrawFunction(functionString, xMin, xMax, yMin, yMax, .12f * xStepNum, .12f * yStepNum, .12f * zStepNum, autoScaleZ, bigScale);
                            CloseMenu();
                            graphOpen = false;
                        }
                        catch (InvalidOperationException e)
                        {
                            errorText.SetText(e.Message);
                        }
                    }
                }
                else if (pressedKey.keyName == "On" && !seats.AssignedSeatsOn())
                {
                    seats.TurnOnAssignedSeats();
                    OnKey.KeepOn();
                    OffKey.TurnOff();
                    CloseMenu();
                }
                else if (pressedKey.keyName == "Passthrough")
                {
                    if (passthrough == null)
                    {
                        passthrough = FindFirstObjectByType<PassthroughController>(FindObjectsInactive.Include);
                    }
                    if (passthrough != null)
                    {
                        passthrough.Toggle();
                    }
                    CloseMenu();
                }
                else if (pressedKey.keyName == "Delete All")
                {
                    GameObject[] drawings = GameObject.FindGameObjectsWithTag("Drawings");
                    foreach (GameObject go in drawings)
                    {
                        NetworkObject no = go.GetComponent<NetworkObject>();
                        if (no != null)
                        {
                            netDrawLine.DeleteAllLines(no.NetworkObjectId);
                        }
                    }
                    netDrawLine.DeleteLocalDrawings();
                    CloseMenu();
                    netDrawLine.ClearLineLists();
                }
                else if (pressedKey.keyName == "Draw Grid")
                {
                    CloseMenu();
                    drawLine.DrawGraphAxes();
                }
                else if (pressedKey.keyName == "Spherical")
                {
                    CloseMenu();
                    drawLine.DrawSpherical();
                }
                else if (pressedKey.keyName == "Plane")
                {
                    CloseMenu();
                    graphOpen = false;
                    drawLine.MakePlane();
                }
            }
        }
        
    }

    public void moveMenu()
    {
        if(currentMenu != null)
        {
            currentMenu.transform.position = myCam.position + myCam.forward.normalized;
            currentMenu.transform.rotation = myCam.rotation;
        }
    }

    public void OpenMenu1()
    {
        float distance = 1.3f;
        currentMenu = Instantiate(Menu1, myCam.position + distance*myCam.forward.normalized, Quaternion.identity);
        currentMenu.transform.rotation = myCam.rotation;
        currentMenu.transform.position += -.7f*currentMenu.transform.right;
        OnKey = currentMenu.transform.GetChild(0).GetChild(0).Find("On").GetComponent<keyInfo>();
        OffKey = currentMenu.transform.GetChild(0).GetChild(0).Find("Off").GetComponent<keyInfo>();
        StraightOnKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(0).GetComponent<keyInfo>();
        StraightOffKey = currentMenu.transform.GetChild(0).GetChild(9).GetChild(1).GetComponent<keyInfo>();
        if (seats.AssignedSeatsOn() == false)
        {
            OffKey.KeepOn();
        }
        else
        {
            OnKey.KeepOn();
        }
        if (drawLine.drawStraight)
        {
            StraightOnKey.KeepOn();
        }
        else
        {
            StraightOffKey.KeepOn();
        }
        SceneManager.SetActive(false);
        RightDot.SetActive(false);
        LeftDot.SetActive(false);
        pointer.SetActive(true);
        currentPointer = pointer.GetComponent<pointerControl>();
        currentMenu.transform.GetChild(1).GetChild(colorIndex + 1).GetComponent<keyInfo>().MakeBigger();
    }

    public void CloseMenu()
    {
        StoreStaticVariables();
        Destroy(currentMenu);
        pointer.SetActive(false);
        currentMenu = null;
        SceneManager.SetActive(true);
        RightDot.SetActive(true);
        LeftDot.SetActive(true);
    }

    public void OpenMenu2()
    {
        float distance = 1.3f;
        currentMenu = Instantiate(Menu2, myCam.position + distance * myCam.forward.normalized, Quaternion.identity);
        currentMenu.transform.rotation = myCam.rotation;
        currentMenu.transform.position += -.7f * currentMenu.transform.right;
        StraightOnKey = currentMenu.transform.GetChild(7).GetChild(0).GetComponent<keyInfo>();
        StraightOffKey = currentMenu.transform.GetChild(7).GetChild(1).GetComponent<keyInfo>();
        if (drawLine.drawStraight)
        {
            StraightOnKey.KeepOn();
        }
        else
        {
            StraightOffKey.KeepOn();
        }
        SceneManager.SetActive(false);
        RightDot.SetActive(false);
        LeftDot.SetActive(false);
        pointer.SetActive(true);
        currentPointer = pointer.GetComponent<pointerControl>();
    }

    public void OpenDeleteMenu()
    {
        float distance = 1.3f;
        currentMenu = Instantiate(ConfirmDeleteMenu, myCam.position + distance * myCam.forward.normalized, Quaternion.identity);
        currentMenu.transform.rotation = myCam.rotation;
        currentMenu.transform.position += -.7f * currentMenu.transform.right;
        SceneManager.SetActive(false);
        RightDot.SetActive(false);
        LeftDot.SetActive(false);
        pointer.SetActive(true);
        currentPointer = pointer.GetComponent<pointerControl>();
    }

    public void Setup(PlayerControls newPlayer)
    {
        myPlayer = newPlayer;
    }

    private void TurnGraphKeysOff()
    {
        FunctionKey.TurnOff();
        XMinKey.TurnOff();
        YMinKey.TurnOff();
        XMaxKey.TurnOff();
        YMaxKey.TurnOff();
        ZStepKey.TurnOff();
        XStepKey.TurnOff();
        YStepKey.TurnOff();
    }

    private void StoreStaticVariables()
    {
        if (graphOpen)
        {
            xMin = int.Parse(xMinText.text);
            xMax = int.Parse(xMaxText.text);
            yMin = int.Parse(yMinText.text);
            yMax = int.Parse(yMaxText.text);
            zStepNum = float.Parse(zStepText.text);
            xStepNum = float.Parse(xStepText.text);
            yStepNum = float.Parse(yStepText.text);
        }
        
    }
}
