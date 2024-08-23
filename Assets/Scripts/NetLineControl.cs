using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;

public class NetLineControl : NetworkBehaviour
{
    public Transform myLocalLine;
    public Material[] materials;
    public int MaterialNumber;
    // Start is called before the first frame update
    void Start()
    {
        if (IsOwner)
        {
            myLocalLine = GameObject.Find("Scene Two Manager").GetComponent<LineDrawer>().UnpairedLines.Dequeue().transform;
            myLocalLine.GetComponent<LineControl>().pairedNetLineId = this.GetComponent<NetworkObject>().NetworkObjectId;
        }
    }

    // Update is called once per frame
    void Update()
    {

    }

    public void ChangeMaterial(int matNum)
    {
        MaterialNumber = matNum;
        UpdateMaterial();
    }

    public void UpdateMaterial()
    {
        this.GetComponent<Renderer>().material = materials[MaterialNumber];
    }

    public void SyncLinePoints(Vector3[] positions, float lineWidth)
    {
        SyncLinePointsServerRpc(positions, lineWidth);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SyncLinePointsServerRpc(Vector3[] positions, float lineWidth)
    {
        PipeRenderer networkRenderer = this.GetComponent<PipeRenderer>();
        networkRenderer.positionCount = positions.Length;
        networkRenderer.startWidth = lineWidth;
        networkRenderer.SetPositions(positions);
        this.transform.parent.gameObject.SetActive(true);
        SyncLinePointsClientRpc(positions, lineWidth, this.GetComponent<NetworkObject>().NetworkObjectId);
    }

    [ClientRpc]
    private void SyncLinePointsClientRpc(Vector3[] positions, float lineWidth, ulong netId)
    {
        if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out NetworkObject networkLineObject))
        {
            PipeRenderer networkRenderer = networkLineObject.GetComponent<PipeRenderer>();
            networkRenderer.positionCount = positions.Length;
            networkRenderer.startWidth = lineWidth;
            networkRenderer.SetPositions(positions);
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
            this.transform.parent.gameObject.SetActive(true);
        }
    }
}
