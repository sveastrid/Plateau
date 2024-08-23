using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TMPro;
using Unity.Collections;

public class PlayerControls : NetworkBehaviour
{
    public InputReader inputs;
    public TextMeshPro username;
    public Transform usernameTransform;
    public Transform myCam;
    public RelayVivox relayVivoxInfo;
    public Transform localLeft;
    public Transform localRight;
    public Transform playerLeft;
    public Transform playerRight;
    public Transform face;
    public seatControl theSeats;
    public Transform rig;

    public NetworkVariable<FixedString32Bytes> playerName = new NetworkVariable<FixedString32Bytes>("username", NetworkVariableReadPermission.Everyone,NetworkVariableWritePermission.Server);
    public NetworkVariable<Vector3> lHPos = new NetworkVariable<Vector3>(new Vector3(0,0,0),NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> lHRot = new NetworkVariable<Vector3>(new Vector3(1, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> rHPos = new NetworkVariable<Vector3>(new Vector3(0, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> rHRot = new NetworkVariable<Vector3>(new Vector3(1, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> facePos = new NetworkVariable<Vector3>(new Vector3(1, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> faceRot = new NetworkVariable<Vector3>(new Vector3(1, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> mySeat = new NetworkVariable<Vector3>(new Vector3(0,0,0), NetworkVariableReadPermission.Everyone,NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> roomOwner = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        this.mySeat.OnValueChanged += (oldVal, newVal) =>
        {
            if (IsOwner)
            {
                GameObject.Find("XRRig").GetComponent<CameraController2>().InitializePosition();
            }
        };

        relayVivoxInfo = GameObject.Find("Network Manager").GetComponent<RelayVivox>();
        inputs = GameObject.Find("Input Reader").GetComponent<InputReader>();
        usernameTransform = this.GetComponent<Transform>().GetChild(1);
        myCam = Camera.main.transform;
        localLeft = GameObject.Find("Left Hand").transform;
        playerLeft = this.transform.GetChild(2);
        localRight = GameObject.Find("Right Hand").transform;
        playerRight = this.transform.GetChild(3);
        face = this.transform.GetChild(4);
        theSeats = GameObject.Find("Seating Manager").GetComponent<seatControl>();
        rig = GameObject.Find("XRRig").transform;


        if (IsOwner)
        {
            SetRoomOwnerServerRpc(relayVivoxInfo.roomOwner);
            SetPlayerNameServerRpc(relayVivoxInfo.myUserDisplayName);
            GameObject.Find("Seating Manager").GetComponent<seatControl>().Setup(this);
            CameraController2 mainRig = GameObject.Find("XRRig").GetComponent<CameraController2>();
            mainRig.Setup(this);
            GameObject.Find("Menu Manager").GetComponent<MenuControl>().Setup(this);
            FindMySeatServerRpc(roomOwner.Value, this.GetComponent<NetworkObject>().OwnerClientId);
        }
        
        username = this.GetComponent<Transform>().GetChild(1).GetComponent<TextMeshPro>();
        username.SetText(playerName.Value.ToString());
        
        if (IsOwner)
        {
            for (int i = 0; i<this.transform.childCount; i++)
            {
                this.transform.GetChild(i).gameObject.SetActive(false);
            }
        }

        this.playerName.OnValueChanged += (oldVal, newVal) =>
        {
            username.SetText(playerName.Value.ToString());
        };
    }

    // Update is called once per frame
    void Update()
    {
        if (!IsOwner)
        {
            Vector3 lookDirection = myCam.position - usernameTransform.position;
            usernameTransform.rotation = Quaternion.LookRotation(-lookDirection);
            playerLeft.position = lHPos.Value;
            if (lHRot.Value != Vector3.zero)
            {
                playerLeft.rotation = Quaternion.LookRotation(lHRot.Value);
            }
            playerRight.position = rHPos.Value;
            if (rHRot.Value != Vector3.zero)
            {
                playerRight.rotation = Quaternion.LookRotation(rHRot.Value);
            }
            face.position = facePos.Value;
            if (faceRot.Value != Vector3.zero)
            {
                face.rotation = Quaternion.LookRotation(faceRot.Value);
            }
            

        }
        else
        {
            Vector3 faceOffset = new Vector3(0, .36f, 0);
            // Floor-level tracking origin: the rig's y IS the real floor, so project the head
            // straight down onto it. The old code subtracted a hardcoded 1.36 m eye height,
            // which now double-counts and leaves the avatar root floating ~0.3 m up.
            this.transform.position = new Vector3(myCam.position.x, rig.position.y, myCam.position.z);
            facePos.Value = myCam.position - faceOffset;
            faceRot.Value = myCam.forward;
            lHPos.Value = localLeft.position + 0.2f * localLeft.forward;
            rHPos.Value = localRight.position + 0.2f * localRight.forward;
            lHRot.Value = localLeft.forward;
            rHRot.Value = localRight.forward;
        }
    }

    [ServerRpc(RequireOwnership =false)]
    private void SetPlayerNameServerRpc(string name)
    {
        playerName.Value = name;
    }
    
    [ServerRpc(RequireOwnership =false)]
    private void FindMySeatServerRpc(bool IsRoomOwner, ulong myId)
    {
        if (IsRoomOwner)
        {
            mySeat.Value = new Vector3(0, 0, -1f);
        }
        else
        {
            List<ulong> clientIds = new List<ulong>();

            foreach (var client in NetworkManager.Singleton.ConnectedClients)
            {
                clientIds.Add(client.Key);
            }
            mySeat.Value = theSeats.GetSeat(clientIds.IndexOf(myId));
        }
    }

    public Vector3 GetMySeat()
    {
        return mySeat.Value;
    }

    [ServerRpc(RequireOwnership =false)]
    public void SetRoomOwnerServerRpc(bool IsRoomOwner)
    {
        roomOwner.Value = IsRoomOwner;
    }

    public bool GetIsRoomOwner()
    {
        return roomOwner.Value;
    }
}
