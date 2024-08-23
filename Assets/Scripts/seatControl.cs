using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.VisualScripting.Antlr3.Runtime;
using UnityEngine;

public class seatControl : NetworkBehaviour
{
    public List<Vector3> seats;
    public PlayerControls player;
    public InputReader inputs;
    
    NetworkVariable<bool> assignedSeats = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Start is called before the first frame update
    void Start()
    {
        makeSeats();
    }

    private void makeSeats()
    {
        seats = new List<Vector3>();
        // Concentric floor arcs instead of stacked vertical rows — MR has a real floor
        // and a real ceiling, so seats must all sit at y = 0.
        float[] radii = { 2.0f, 2.9f, 3.8f };

        foreach (float r in radii)
        {
            for (float theta = Mathf.PI / 2; theta < Mathf.PI; theta += Mathf.PI / 10)
            {
                if (theta > Mathf.PI / 2)
                {
                    seats.Add(new Vector3(r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
                    seats.Add(new Vector3(-r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
                }
                else
                {
                    seats.Add(new Vector3(r * Mathf.Cos(theta), 0, r * Mathf.Sin(theta)));
                }
            }
        }
    }

    /// <summary>
    /// Seat lookup that cannot throw. Relay allocates 12 slots but the arc geometry and the
    /// client list can disagree, and PlayerControls indexes this by connection order — an out
    /// of range or missing client id used to throw ArgumentOutOfRangeException on join.
    /// </summary>
    public Vector3 GetSeat(int index)
    {
        if (seats == null || seats.Count == 0)
        {
            makeSeats();
        }

        if (index < 0)
        {
            Debug.LogWarning("seatControl.GetSeat: client id not found in the connected client list; using the last seat.");
            return seats[seats.Count - 1];
        }

        if (index >= seats.Count)
        {
            Debug.LogWarning($"seatControl.GetSeat: seat {index} requested but only {seats.Count} exist; wrapping.");
            return seats[index % seats.Count];
        }

        return seats[index];
    }

    public void Setup(PlayerControls newPlayer)
    {
        player = newPlayer;
    }

    public bool AssignedSeatsOn()
    {
        return assignedSeats.Value;
    }

    public void TurnOnAssignedSeats()
    {
        ChangeAssignedSeatsServerRpc(true);
    }

    public void TurnOffAssignedSeats()
    {
        ChangeAssignedSeatsServerRpc(false);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ChangeAssignedSeatsServerRpc(bool assigned)
    {
        assignedSeats.Value = assigned;
    }
}
