using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;


//this takes just gives values of the controller inputs from the right and left hand
//there is a corresponding keyboard key for each for debugging purposes
//Keyboard keys used: A,B,U,H,J,K,arrow keys, comma, period, X, Y, P, Q, O, W
//Also, M and N are used to tilt up and down in the camera controller
public class InputReader : MonoBehaviour
{
    //Store whether or not controllers are actually detected
    public bool LeftControllerFound;
    public bool RightControllerFound;

    //Down is only true on the frame when button gets pressed and Up is only true on the first frame button is released.
    //A on controller, A on keyboard
    public bool ButtonA;
    public bool ButtonADown;
    public bool ButtonAUp;
    //left joystick on controller, uhjk on the keyboard, left-right then up-down vector2
    public Vector2 leftJoystick;
    //B on controller, B on keyboard
    public bool ButtonB;
    public bool ButtonBDown;
    public bool ButtonBUp;
    //right joystick on controller, arrow keys on the keyboard, left-right then up-down vector2
    public Vector2 rightJoystick;
    //left pointer trigger on controller, ',' on the keyboard
    public bool LeftMainTrigger;
    public float LeftMainTriggerValue;
    public bool LeftMainTriggerDown;
    public bool LeftMainTriggerUp;
    //right pointer trigger on controller, '.' on the keyboard
    public bool RightMainTrigger;
    public float RightMainTriggerValue;
    public bool RightMainTriggerDown;
    public bool RightMainTriggerUp;
    //X on controller, X on keyboard
    public bool ButtonX;
    public bool ButtonXDown;
    public bool ButtonXUp;
    //Y on controller, Y on keyboard
    public bool ButtonY;
    public bool ButtonYDown;
    public bool ButtonYUp;
    //right grip button on controller, 'p' on the keyboard
    public float RightGripValue;
    public bool RightGrip;
    public bool RightGripDown;
    public bool RightGripUp;
    //left grip button on controller, 'q' on the keyboard
    public float LeftGripValue;
    public bool LeftGrip;
    public bool LeftGripDown;
    public bool LeftGripUp;
    //right joystick button on controller, 'i' on the keyboard
    public bool RightJoystickButton;
    public bool RightJoystickButtonDown;
    public bool RightJoystickButtonUp;
    //left joystick button on controller, 'e' on the keyboard
    public bool LeftJoystickButton;
    public bool LeftJoystickButtonDown;
    public bool LeftJoystickButtonUp;

    //Some private variables
    private bool ButtonAOn;
    private bool ButtonBOn;
    private bool LeftMainTriggerOn;
    private bool RightMainTriggerOn;
    private bool ButtonXOn;
    private bool ButtonYOn;
    private bool RightGripOn;
    private bool LeftGripOn;
    private bool RightJoystickButtonOn;
    private bool LeftJoystickButtonOn;


    // Start is called before the first frame update
    void Start()
    {
        RightControllerFound=false;
        LeftControllerFound=false;
        ButtonA=false;
        ButtonADown=false;
        ButtonAUp=false;
        leftJoystick=new Vector2(0f,0f);
        rightJoystick=new Vector2(0f,0f);
        ButtonAOn=false;
        ButtonBOn=false;
        LeftMainTriggerDown=false;
        LeftMainTriggerUp=false;
        LeftMainTriggerValue=0;
        LeftMainTriggerOn=false;
        RightMainTriggerDown=false;
        RightMainTriggerUp=false;
        RightMainTriggerValue=0;
        RightMainTriggerOn=false;
        ButtonX=false;
        ButtonXDown=false;
        ButtonXUp=false;
        ButtonXOn=false;
        ButtonY=false;
        ButtonYDown=false;
        ButtonYUp=false;
        ButtonYOn=false;
        RightGripValue=0;
        LeftGripValue=0;
        RightGrip=false;
        RightGripDown=false;
        RightGripUp=false;
        RightGripOn=false;
        LeftGrip=false;
        LeftGripDown=false;
        LeftGripUp=false;
        LeftGripOn=false;
        RightJoystickButton=false;
        RightJoystickButtonDown=false;
        RightJoystickButtonUp=false;
        RightJoystickButtonOn=false;
        LeftJoystickButton=false;
        LeftJoystickButtonDown=false;
        LeftJoystickButtonUp=false;
        LeftJoystickButtonOn=false;
        LeftMainTrigger=false;
        RightMainTrigger=false;
    }

    // Update is called once per frame
    void Update()
    {
        //All of the stuff for the Right Controller
        List<InputDevice> devices = new List<InputDevice>();
        // Filter on Controller as well as Right, and accept more than one match. Asking for
        // bare "Right" also matches a tracked right hand, so enabling hand tracking alongside
        // controllers used to push Count to 2 and silently kill all right-hand input.
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right, devices);
        if (devices.Count >= 1)
        {
            RightControllerFound=true;
            InputDevice rightController;
            rightController=devices[0];

            //the A button
            rightController.TryGetFeatureValue(CommonUsages.primaryButton, out ButtonA);
            if (ButtonA)
            {
                if (!ButtonAOn)
                {
                    ButtonADown=true;
                }
                else
                {
                    ButtonADown=false;
                }
                ButtonAOn=true;
            }
            else if (ButtonAOn)
            {
                ButtonAUp=true;
                ButtonAOn=false;
                // Clearing Down here as well as Up below is what stops a press that lasted exactly
                // one frame from leaving Down stuck true for the rest of the session: the branch
                // above only ever writes Down while the button is held. Every button in this file
                // had the same hole.
                ButtonADown=false;
            }
            else
            {
                ButtonAUp=false;
            }

            //the right grip button
            rightController.TryGetFeatureValue(CommonUsages.gripButton, out RightGrip);
            if (RightGrip)
            {
                if (!RightGripOn)
                {
                    RightGripDown=true;
                }
                else
                {
                    RightGripDown=false;
                }
                RightGripOn=true;
            }
            else if (RightGripOn)
            {
                RightGripUp=true;
                RightGripOn=false;
                RightGripDown=false;
            }
            else
            {
                RightGripUp=false;
            }


            //the right joystick button
            rightController.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out RightJoystickButton);
            if (RightJoystickButton)
            {
                if (!RightJoystickButtonOn)
                {
                    RightJoystickButtonDown=true;
                }
                else
                {
                    RightJoystickButtonDown=false;
                }
                RightJoystickButtonOn=true;
            }
            else if (RightJoystickButtonOn)
            {
                RightJoystickButtonUp=true;
                RightJoystickButtonOn=false;
                RightJoystickButtonDown=false;
            }
            else
            {
                RightJoystickButtonUp=false;
            }
           

            //the B button
            rightController.TryGetFeatureValue(CommonUsages.secondaryButton, out ButtonB);
            if (ButtonB)
            {
                if (!ButtonBOn)
                {
                    ButtonBDown=true;
                }
                else
                {
                    ButtonBDown=false;
                }
                ButtonBOn=true;
            }
            else if (ButtonBOn)
            {
                ButtonBUp=true;
                ButtonBOn=false;
                ButtonBDown=false;
            }
            else
            {
                ButtonBUp=false;
            }

            //right Joystick-note that left-right is the first value, then up-down
            rightController.TryGetFeatureValue(CommonUsages.primary2DAxis, out rightJoystick);

            //right grip button
            rightController.TryGetFeatureValue(CommonUsages.grip, out RightGripValue);

            //Right pointer finger trigger
            rightController.TryGetFeatureValue(CommonUsages.triggerButton, out RightMainTrigger);
            if (RightMainTrigger)
            {
                if (!RightMainTriggerOn)
                {
                    RightMainTriggerDown=true;
                }
                else
                {
                    RightMainTriggerDown=false;
                }
                RightMainTriggerOn=true;
            }
            else if (RightMainTriggerOn)
            {
                RightMainTriggerUp=true;
                RightMainTriggerOn=false;
                RightMainTriggerDown=false;
            }
            else
            {
                RightMainTriggerUp=false;
            }
        }
        else
        {
            //the A button with keyboard
            ButtonA=Input.GetKey(KeyCode.A);
            ButtonADown=Input.GetKeyDown(KeyCode.A);
            ButtonAUp=Input.GetKeyUp(KeyCode.A);

            //the B button with keyboard
            ButtonB=Input.GetKey(KeyCode.B);
            ButtonBDown=Input.GetKeyDown(KeyCode.B);
            ButtonBUp=Input.GetKeyUp(KeyCode.B);

            //the right grip with keyboard
            RightGrip=Input.GetKey(KeyCode.P);
            RightGripDown=Input.GetKeyDown(KeyCode.P);
            RightGripUp=Input.GetKeyUp(KeyCode.P);

            //the right joystick button with keyboard
            RightJoystickButton=Input.GetKey(KeyCode.I);
            RightJoystickButtonDown=Input.GetKeyDown(KeyCode.I);
            RightJoystickButtonUp=Input.GetKeyUp(KeyCode.I);

            //right Joystick with keyboard
            float HorizontalInput = Input.GetAxis("Horizontal");
            float VerticalInput = Input.GetAxis("Vertical");
            rightJoystick = new Vector2(HorizontalInput, VerticalInput);

            //right pointer trigger with keyboard
            if (Input.GetKey(KeyCode.Period))
            {
                RightMainTrigger=true;
                RightMainTriggerValue=1;
            }
            else
            {
                RightMainTrigger=false;
                RightMainTriggerValue=0;
            }
            RightMainTriggerDown=Input.GetKeyDown(KeyCode.Period);
            RightMainTriggerUp=Input.GetKeyUp(KeyCode.Period);

            //right grip button with keyboard (the value)
            if (Input.GetKey(KeyCode.P))
            {
                RightGripValue=1;
            }
            else
            {
                RightGripValue=0;
            }
        }



        //Change to the left controller
        // Same filtering as the right controller above.
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left, devices);
        if (devices.Count >= 1)
        {
            LeftControllerFound=true;
            InputDevice leftController=devices[0];

            //left Joystick-note that left-right is the first value, then up-down
            leftController.TryGetFeatureValue(CommonUsages.primary2DAxis, out leftJoystick);

            //left grip button (value)
            leftController.TryGetFeatureValue(CommonUsages.grip, out LeftGripValue);

            //the left joystick button
            leftController.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out LeftJoystickButton);
            if (LeftJoystickButton)
            {
                if (!LeftJoystickButtonOn)
                {
                    LeftJoystickButtonDown=true;
                }
                else
                {
                    LeftJoystickButtonDown=false;
                }
                LeftJoystickButtonOn=true;
            }
            else if (LeftJoystickButtonOn)
            {
                LeftJoystickButtonUp=true;
                LeftJoystickButtonOn=false;
                LeftJoystickButtonDown=false;
            }
            else
            {
                LeftJoystickButtonUp=false;
            }

            //left pointer finger trigger
            leftController.TryGetFeatureValue(CommonUsages.triggerButton, out LeftMainTrigger);
            if (LeftMainTrigger)
            {
                if (!LeftMainTriggerOn)
                {
                    LeftMainTriggerDown=true;
                }
                else
                {
                    LeftMainTriggerDown=false;
                }
                LeftMainTriggerOn=true;
            }
            else if (LeftMainTriggerOn)
            {
                LeftMainTriggerUp=true;
                LeftMainTriggerOn=false;
                LeftMainTriggerDown=false;
            }
            else
            {
                LeftMainTriggerUp=false;
            }


            //the X button
            leftController.TryGetFeatureValue(CommonUsages.primaryButton, out ButtonX);
            if (ButtonX)
            {
                if (!ButtonXOn)
                {
                    ButtonXDown=true;
                }
                else
                {
                    ButtonXDown=false;
                }
                ButtonXOn=true;
            }
            else if (ButtonXOn)
            {
                ButtonXUp=true;
                ButtonXOn=false;
                ButtonXDown=false;
            }
            else
            {
                ButtonXUp=false;
            }


            //the Y button
            leftController.TryGetFeatureValue(CommonUsages.secondaryButton, out ButtonY);
            if (ButtonY)
            {
                if (!ButtonYOn)
                {
                    ButtonYDown=true;
                }
                else
                {
                    ButtonYDown=false;
                }
                ButtonYOn=true;
            }
            else if (ButtonYOn)
            {
                ButtonYUp=true;
                ButtonYOn=false;
                ButtonYDown=false;
            }
            else
            {
                ButtonYUp=false;
            }

            //the left grip button
            leftController.TryGetFeatureValue(CommonUsages.gripButton, out LeftGrip);
            if (LeftGrip)
            {
                if (!LeftGripOn)
                {
                    LeftGripDown=true;
                }
                else
                {
                    LeftGripDown=false;
                }
                LeftGripOn=true;
            }
            else if (LeftGripOn)
            {
                LeftGripUp=true;
                LeftGripOn=false;
                LeftGripDown=false;
            }
            else
            {
                LeftGripUp=false;
            }

        }
        else
        {
            //left Joystick on keyboard
            float HorizontalInput = GetHorizontal();
            float VerticalInput = GetVertical();
            leftJoystick = new Vector2(HorizontalInput, VerticalInput);

            //the X button with keyboard
            ButtonX=Input.GetKey(KeyCode.X);
            ButtonXDown=Input.GetKeyDown(KeyCode.X);
            ButtonXUp=Input.GetKeyUp(KeyCode.X);

            //the Y button with keyboard
            ButtonY=Input.GetKey(KeyCode.Y);
            ButtonYDown=Input.GetKeyDown(KeyCode.Y);
            ButtonYUp=Input.GetKeyUp(KeyCode.Y);

            //the left grip with keyboard
            LeftGrip=Input.GetKey(KeyCode.Q);
            LeftGripDown=Input.GetKeyDown(KeyCode.Q);
            LeftGripUp=Input.GetKeyUp(KeyCode.Q);

            //the left joystick button with keyboard
            LeftJoystickButton=Input.GetKey(KeyCode.E);
            LeftJoystickButtonDown=Input.GetKeyDown(KeyCode.E);
            LeftJoystickButtonUp=Input.GetKeyUp(KeyCode.E);

            //left grip button (value) on keyboard
            if (Input.GetKey(KeyCode.Q))
            {
                LeftGripValue=1;
            }
            else
            {
                LeftGripValue=0;
            }

            //left pointer trigger on keyboard
            if (Input.GetKey(KeyCode.Comma))
            {
                LeftMainTrigger=true;
                LeftMainTriggerValue=1;
            }
            else
            {
                LeftMainTrigger=false;
                LeftMainTriggerValue=0;
            }
            LeftMainTriggerDown=Input.GetKeyDown(KeyCode.Comma);
            LeftMainTriggerUp=Input.GetKeyUp(KeyCode.Comma);
        }
        
        
    }

    


    static float GetHorizontal()
    {
        if (Input.GetKey(KeyCode.H))
        {
            return -1;
        }
        else if (Input.GetKey(KeyCode.K))
        {
            return 1;
        }
        else
        {
            return 0;
        }
    }

    static float GetVertical()
    {
        if (Input.GetKey(KeyCode.J))
        {
            return -1;
        }
        else if (Input.GetKey(KeyCode.U))
        {
            return 1;
        }
        else
        {
            return 0;
        }
    }



    // Down here is old code that I think I might need sometime:

    //Old way of checking keydown with keyboard or Oculus controller
    /*
        if (Input.GetKeyDown(KeyCode.A) || OVRInput.GetDown(OVRInput.Button.One))
        {
            Debug.Log("A was pressed");
            ButtonA = true;
            ButtonADown = true;
        }
        
        if (Input.GetKeyUp(KeyCode.A) || OVRInput.GetUp(OVRInput.Button.One))
        {
            Debug.Log("A was released");
            ButtonA=false;
        }

        */


    //Another old way of checking keydown but only with keyboard
    /*
        if (Input.GetKey(KeyCode.A))
        {
            if (ButtonA)
            {
                ButtonADown=false;
            }
            else
            {
                ButtonA=true;
                ButtonADown=true;
            }
        }

        if (Input.GetKeyUp(KeyCode.A))
        {
            if (ButtonA)
            {
                ButtonA=false;
                ButtonAUp=true;
            }
            else
            {
                ButtonAUp=false;
            }
        }
        
        */



        //How to write to debugger screen
        /*
        if (ButtonADown)
        {
            debuggerText.SetText(debuggerText.text +"\n"+ "What up?");
        }

        //here leftJoystick is a Vector2
        debuggerText.SetText(debuggerText.text +"\n"+leftJoystick.ToString());
        */

}
