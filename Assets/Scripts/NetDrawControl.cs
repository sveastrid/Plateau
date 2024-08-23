using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Windows;

public class NetDrawControl : NetworkBehaviour
{
    public LineDrawer LineDrawerObject;
    public ulong myNetworkDrawingsId;

    // Start is called before the first frame update
    void Start()
    {
        myNetworkDrawingsId = this.GetComponent<NetworkObject>().NetworkObjectId;
        if (IsOwner)
        {
            LineDrawerObject = GameObject.Find("Scene Two Manager").GetComponent<LineDrawer>();
            LineDrawerObject.Setup(myNetworkDrawingsId, this.gameObject);
        }
    }

    public void SyncTransforms(Vector3 position, Quaternion rot, Vector3 scale)
    {
        SyncTransformsServerRpc(position, rot, scale);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SyncTransformsServerRpc(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        this.transform.position = pos;
        this.transform.rotation = rot;
        this.transform.localScale = scale;
        SyncTransformsClientRpc(pos, rot, scale);
    }

    [ClientRpc]
    private void SyncTransformsClientRpc(Vector3 pos, Quaternion rot, Vector3 scale)
    {
        this.transform.position = pos;
        this.transform.rotation = rot;
        this.transform.localScale = scale;
        if (IsOwner)
        {
            this.gameObject.SetActive(true);
        }
    }
}