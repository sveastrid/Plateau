using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using TMPro;

public class CameraController2 : MonoBehaviour
{
    public InputReader Inputs;
    public Transform RightHand;
    float yAngle;
    float movement;
    public seatControl theSeats;
    public PlayerControls myPlayer;
    public Transform head;

    public float RotationAngle;
    public float RotationSpeed;
    public float MovingSpeed;

    private bool rightJoystickReleased = true;
    private bool recentered = false;

    // Start is called before the first frame update
    void Start()
    {
        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (theSeats.AssignedSeatsOn())
        {
            // Seat assignment is a one-shot recenter in MR, not a per-frame lock.
            // Physical walking must remain the user's own — re-setting the rig every frame
            // cancels out real steps and is instantly nauseating in passthrough.
            if (!recentered && myPlayer != null)
            {
                RecenterToSeat();
                recentered = true;
            }
        }
        else
        {
            recentered = false;
            //rotation
            if (Inputs.RightControllerFound)
            {
                if ((Inputs.rightJoystick[0] > .95 || Inputs.rightJoystick[0] < -.95) && rightJoystickReleased)
                {
                    rightJoystickReleased = false;
                    this.transform.Rotate(0f, Inputs.rightJoystick[0] * RotationAngle, 0f, Space.World);
                }
                if (Inputs.rightJoystick[0] < .95 && Inputs.rightJoystick[0] > - .95)
                {
                    rightJoystickReleased = true;
                }
            }
            else
            {
                yAngle = Inputs.rightJoystick[0];
                this.transform.Rotate(0f, yAngle * RotationSpeed, 0f, Space.World);
            }
            
            

            //translation
            movement = Inputs.rightJoystick[1];
            this.transform.Translate(RightHand.forward * MovingSpeed * movement * Time.deltaTime, Space.World);

            //If the player presses right joystick, the classroom is re-placed around them.
            //This is the single most important control in an MR app.
            if (Inputs.RightJoystickButtonDown && myPlayer != null)
            {
                RecenterToSeat();
            }

            //Need Keyboard method of tilting up and down for debugging purposes, M tilts down, N tilts up
            if (Input.GetKey(KeyCode.M))
            {
                this.transform.Rotate(RotationSpeed, 0f, 0f, Space.Self);
            }
            if (Input.GetKey(KeyCode.N))
            {
                this.transform.Rotate(-RotationSpeed, 0f, 0f, Space.Self);
            }
        }



    }

    public void Setup(PlayerControls newPlayer)
    {
        myPlayer = newPlayer;
    }

    public void InitializePosition()
    {
        RecenterToSeat();
        recentered = true;
    }

    /// <summary>
    /// Move the rig so that the user's CURRENT real-world standing position maps onto
    /// their assigned seat in the shared classroom, facing the classroom origin.
    /// The shared virtual world never moves — only this client's rig does — so every
    /// networked value in the project stays in world space and needs no conversion.
    /// </summary>
    public void RecenterToSeat()
    {
        if (myPlayer == null) return;

        Vector3 seat = myPlayer.GetMySeat();

        // Face the centre of the classroom.
        Vector3 lookDir = new Vector3(-seat.x, 0f, -seat.z);
        if (lookDir.sqrMagnitude < 0.0001f) lookDir = Vector3.forward;
        transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);

        if (head == null && Camera.main != null)
        {
            head = Camera.main.transform;
        }

        if (head == null)
        {
            // No head to anchor against — fall back to the old absolute placement.
            transform.position = new Vector3(seat.x, 0f, seat.z);
            return;
        }

        // Cancel out where the user's head currently is inside their real room,
        // so the seat lands under their actual feet. Rotation must be set first.
        Vector3 headLocal = transform.InverseTransformPoint(head.position);
        headLocal.y = 0f;
        transform.position = new Vector3(seat.x, 0f, seat.z) - transform.TransformVector(headLocal);
    }

}
