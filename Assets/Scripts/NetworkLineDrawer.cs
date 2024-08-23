using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

public class NetworkLineDrawer : NetworkBehaviour
{
    public GameObject NetworkLinePrefab;
    public GameObject NetworkDrawings;
    public GameObject NetworkGraphPrefab;
    public GameObject NetworkGraphAxesPrefab;
    public GameObject NetworkPlane;
    public GameObject NetworkSphericalAxesPrefab;

    public override void OnNetworkSpawn()
    {
        SpawnDrawingsObjectServerRpc();
        base.OnNetworkSpawn();
        LateSyncLinePointsServerRpc();
        GameObject.Find("Scene Two Manager").GetComponent<LineDrawer>().UnpairedLines.Clear();
    }

    [ClientRpc]
    private void DebugClientRpc(string s)
    {
        Debug.Log(s);
    }

    [ServerRpc(RequireOwnership = false)]
    private void LateSyncLinePointsServerRpc(ServerRpcParams rpcParams = default)
    {
        GameObject[] allDrawings = GameObject.FindGameObjectsWithTag("Drawings");
        foreach (GameObject networkDrawings in allDrawings)
        {
            for (int i = 0; i < networkDrawings.transform.childCount; i++)
            {
                Transform nextDrawing = networkDrawings.transform.GetChild(i);
                if (nextDrawing.tag == "Graph")
                {
                    FunctionRenderer functionDraw = nextDrawing.GetComponent<FunctionRenderer>();
                    int xmax = functionDraw.xmax;
                    int ymax = functionDraw.ymax;
                    int xmin = functionDraw.xmin;
                    int ymin = functionDraw.ymin;
                    string function = functionDraw.function;
                    bool scaleZ = functionDraw.scaleZ;
                    float zIncrement = functionDraw.zIncrement;
                    float xIncrement = functionDraw.xStep;
                    float yIncrement = functionDraw.yStep;
                    bool bigScale = functionDraw.bigBox;

                    LateSyncGraphClientRpc(nextDrawing.GetComponent<NetworkObject>().NetworkObjectId, function, xmin, xmax, ymin, ymax, xIncrement, yIncrement, zIncrement, scaleZ, bigScale, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams
                        {
                            TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
                        }
                    });

                    GraphAxisControl graphControl = nextDrawing.transform.GetChild(0).GetComponent<GraphAxisControl>();
                    float xmin2 = graphControl.axesXMin;
                    float xmax2 = graphControl.axesXMax;
                    float ymin2 = graphControl.axesYMin;
                    float ymax2 = graphControl.axesYMax;
                    float zmin2 = graphControl.axesZMin;
                    float zmax2 = graphControl.axesZMax;
                    float zIncrement2 = graphControl.zIncrement;
                    float xIncrement2 = graphControl.xIncrement;
                    float yIncrement2 = graphControl.yIncrement;
                    LateSyncAxesClientRpc(nextDrawing.transform.GetChild(0).GetComponent<NetworkObject>().NetworkObjectId, xmin2, xmax2, ymin2, ymax2, zmin2, zmax2, xIncrement2, yIncrement2, zIncrement2, scaleZ, bigScale, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams
                        {
                            TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
                        }
                    });
                }
                else if (nextDrawing.tag == "Axes")
                {
                    GraphAxisControl graphControl = nextDrawing.GetComponent<GraphAxisControl>();
                    float xmin = graphControl.axesXMin;
                    float xmax = graphControl.axesXMax;
                    float ymin = graphControl.axesYMin;
                    float ymax = graphControl.axesYMax;
                    float zmin = graphControl.axesZMin;
                    float zmax = graphControl.axesZMax;
                    float zIncrement = graphControl.zIncrement;
                    float xIncrement = graphControl.xIncrement;
                    float yIncrement = graphControl.yIncrement;
                    LateSyncAxesClientRpc(nextDrawing.GetComponent<NetworkObject>().NetworkObjectId, xmin, xmax, ymin, ymax, zmin, zmax, xIncrement, yIncrement, zIncrement, true, false, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams
                        {
                            TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
                        }
                    });
                }
                else
                {
                    PipeRenderer lineDraw = networkDrawings.transform.GetChild(i).GetComponent<PipeRenderer>();
                    Vector3[] positions = new Vector3[lineDraw.positionCount];
                    lineDraw.GetPositions(ref positions);
                    int materialNum = lineDraw.GetComponent<NetLineControl>().MaterialNumber;
                    LateSyncLinePointsClientRpc(networkDrawings.transform.GetChild(i).GetComponent<NetworkObject>().NetworkObjectId, materialNum, positions, lineDraw.startWidth, new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams
                        {
                            TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
                        }
                    });
                }
                
            }
        }
    }

    [ClientRpc]
    private void LateSyncAxesClientRpc(ulong netId, float xmin, float xmax, float ymin, float ymax, float zmin, float zmax, float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale, ClientRpcParams rpcParams)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkAxes)) 
        {
            GraphAxisControl axesDraw = networkAxes.GetComponent<GraphAxisControl>();
            if (scaleZ)
            {
                if (bigScale)
                {
                    axesDraw.SetAxesAutoScale(xmin, xmax, ymin, ymax, zmin, zmax, 3, 3, 1.25f);
                }
                else
                {
                    axesDraw.SetAxesAutoScale(xmin, xmax, ymin, ymax, zmin, zmax, 1.2f, 1.2f, 1.2f);
                }
            }
            else
            {
                axesDraw.SetAxes(xmin, xmax, ymin, ymax, zmin, zmax, xIncrement, yIncrement, zIncrement);
            }
        }
    }

    [ClientRpc]
    private void LateSyncGraphClientRpc(ulong netId, string function, int xmin, int xmax, int ymin, int ymax, float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale, ClientRpcParams rpcParams)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkGraph))
        {
            FunctionRenderer functionDraw = networkGraph.GetComponent<FunctionRenderer>();
            functionDraw.bigBox = bigScale;
            functionDraw.SetFunction(function, xmin, xmax, ymin, ymax, scaleZ, xIncrement, yIncrement, zIncrement);
        }
    }

    [ClientRpc]
    private void LateSyncLinePointsClientRpc(ulong netId, int materialNum, Vector3[] positions, float lineWidth, ClientRpcParams rpcParams = default)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkLine))
        {
            PipeRenderer netLineRend = networkLine.GetComponent<PipeRenderer>();
            netLineRend.positionCount = positions.Length;
            netLineRend.startWidth = lineWidth;
            netLineRend.SetPositions(positions);
            netLineRend.GetComponent<NetLineControl>().ChangeMaterial(materialNum);
            
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SpawnDrawingsObjectServerRpc(ServerRpcParams rpcParams = default)
    {
        GameObject netDraw = Instantiate(NetworkDrawings);
        netDraw.GetComponent<NetworkObject>().SpawnWithOwnership(rpcParams.Receive.SenderClientId);
    }

    public void CreateNetworkGraph(ulong netDrawingsId, string function, Vector3 center, int xmin, int xmax, int ymin, int ymax, float zmin, float zmax, float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale)
    {
        CreateNetworkGraphServerRpc(netDrawingsId, function, center, xmin, xmax, ymin, ymax, zmin, zmax, xIncrement, yIncrement, zIncrement, scaleZ, bigScale);
    }

    [ServerRpc(RequireOwnership = false)]
    private void CreateNetworkGraphServerRpc(ulong netDrawingsId, string function, Vector3 center, int xmin, int xmax, int ymin, int ymax, float zmin, float zmax, float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale, ServerRpcParams rpcParams = default)
    {
        GameObject NewGraph = Instantiate(NetworkGraphPrefab, Vector3.zero, Quaternion.identity);
        NetworkObject netObj = NewGraph.GetComponent<NetworkObject>();
        netObj.SpawnWithOwnership(rpcParams.Receive.SenderClientId);

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netDrawingsId, out var networkDrawings))
        {
            NewGraph.transform.SetParent(networkDrawings.transform);
            UpdateGraphParentClientRpc(netObj.NetworkObjectId, netDrawingsId);
        }
        
        if (scaleZ)
        {
            netObj.GetComponent<FunctionRenderer>().bigBox = bigScale;
            netObj.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, scaleZ, zIncrement);
        }
        else
        {
            netObj.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, scaleZ, xIncrement, yIncrement, zIncrement);
        }
        
        NewGraph.transform.position += center;
        SetFunctionClientRpc(netObj.NetworkObjectId, function, center, xmin, xmax, ymin, ymax, xIncrement, yIncrement, zIncrement, scaleZ, bigScale);

        CreateNetworkGraphAxesWithOwnerServerRpc(rpcParams.Receive.SenderClientId, netObj.NetworkObjectId, center, xmin, xmax, ymin, ymax, zmin, zmax, xIncrement, yIncrement, zIncrement, scaleZ, bigScale);
    }

    [ClientRpc]
    private void UpdateGraphParentClientRpc(ulong graphNetId, ulong drawingsNetId)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(drawingsNetId, out var networkDrawings))
        {
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(graphNetId, out var networkGraph))
            {
                networkGraph.transform.SetParent(networkDrawings.transform);
            }
        }
    }

    [ClientRpc]
    private void SetFunctionClientRpc(ulong netId, string function, Vector3 center, int xmin, int xmax, int ymin, int ymax,  float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale)
    {
        if (IsServer)
        {
            return;
        }
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkGraph))
        {
            if (scaleZ)
            {
                networkGraph.GetComponent<FunctionRenderer>().bigBox = bigScale;
                networkGraph.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, scaleZ, zIncrement);
            }
            else
            {
                networkGraph.GetComponent<FunctionRenderer>().SetFunction(function, xmin, xmax, ymin, ymax, scaleZ, xIncrement, yIncrement, zIncrement);
            }
            
            networkGraph.transform.position += center;
        }
    }

    public void MakeNetworkPlane(ulong netDrawingsId, Vector3 center)
    {
        MakeNetworkPlaneServerRpc(netDrawingsId, center);
    }

    [ServerRpc(RequireOwnership = false)]
    private void MakeNetworkPlaneServerRpc(ulong netDrawingsId, Vector3 center, ServerRpcParams rpcParams = default)
    {
        GameObject NewPlane = Instantiate(NetworkPlane);
        NetworkObject netNewPlane = NewPlane.GetComponent<NetworkObject>();
        netNewPlane.SpawnWithOwnership(rpcParams.Receive.SenderClientId);

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netDrawingsId, out var networkDrawings))
        {
            NewPlane.transform.SetParent(networkDrawings.transform);
            UpdateParentClientRpc(netNewPlane.NetworkObjectId, netDrawingsId);
        }

        NewPlane.transform.position += center;
        MoveObjectClientRpc(netNewPlane.NetworkObjectId, center);
    }

    [ClientRpc]
    private void MoveObjectClientRpc(ulong netObjectId, Vector3 center)
    {
        if (IsServer)
        {
            return;
        }

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netObjectId, out var netObj))
        {
            netObj.transform.position += center;
        }
    }

    public void CreateNetworkGraphAxes(ulong netParentId, Vector3 center, float x1, float x2, float y1, float y2, float z1, float z2)
    {
        CreateNetworkGraphAxesServerRpc(netParentId, center, x1, x2, y1, y2, z1, z2);
    }

    public void CreateNetworkSphericalAxes(ulong netParentId, Vector3 center)
    {
        CreateNetworkSphericalAxesServerRpc(netParentId, center);
    }

    [ServerRpc(RequireOwnership = false)]
    private void CreateNetworkGraphAxesServerRpc(ulong netParentId, Vector3 center, float x1, float x2, float y1, float y2, float z1, float z2, ServerRpcParams rpcParams = default)
    {
        GameObject NewAxes = Instantiate(NetworkGraphAxesPrefab);
        NetworkObject netObjNewAxes = NewAxes.GetComponent<NetworkObject>();
        netObjNewAxes.SpawnWithOwnership(rpcParams.Receive.SenderClientId);

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netParentId, out var networkDrawings))
        {
            NewAxes.transform.SetParent(networkDrawings.transform);
            UpdateParentClientRpc(netObjNewAxes.NetworkObjectId, netParentId);
        }

        netObjNewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 1.2f, 1.2f, 1.2f);
        netObjNewAxes.transform.position += center;
        SetAxesClientRpc(netObjNewAxes.NetworkObjectId, center, x1, x2, y1, y2, z1, z2, .12f, .12f, .12f, true, false);

    }

    [ServerRpc(RequireOwnership = false)]
    private void CreateNetworkSphericalAxesServerRpc(ulong netParentId, Vector3 center, ServerRpcParams rpcParams = default)
    {
        GameObject NewAxes = Instantiate(NetworkSphericalAxesPrefab);
        NetworkObject netObjNewAxes = NewAxes.GetComponent<NetworkObject>();
        netObjNewAxes.SpawnWithOwnership(rpcParams.Receive.SenderClientId);

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netParentId, out var networkDrawings))
        {
            NewAxes.transform.SetParent(networkDrawings.transform);
            UpdateParentClientRpc(netObjNewAxes.NetworkObjectId, netParentId);
        }

        netObjNewAxes.GetComponent<SphericalAxisControl>().SetAxes();
        netObjNewAxes.transform.position += center;
        SetSphericalAxesClientRpc(netObjNewAxes.NetworkObjectId, center);

    }

    [ServerRpc(RequireOwnership = false)]
    private void CreateNetworkGraphAxesWithOwnerServerRpc(ulong ownerClientId, ulong netParentId, Vector3 center, float x1, float x2, float y1, float y2, float z1, float z2,  float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale)
    {
        GameObject NewAxes = Instantiate(NetworkGraphAxesPrefab);
        NetworkObject netObjNewAxes = NewAxes.GetComponent<NetworkObject>();
        netObjNewAxes.SpawnWithOwnership(ownerClientId);

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netParentId, out var networkDrawings))
        {
            NewAxes.transform.SetParent(networkDrawings.transform);
            UpdateParentClientRpc(netObjNewAxes.NetworkObjectId, netParentId);
        }

        if (scaleZ)
        {
            if (bigScale)
            {
                netObjNewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 3, 3, 1.25f);
            }
            else
            {
                netObjNewAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 1.2f, 1.2f, 1.2f);
            }
        }
        else
        {
            netObjNewAxes.GetComponent<GraphAxisControl>().SetAxes(x1, x2, y1, y2, z1, z2, xIncrement, yIncrement, zIncrement);
        }

        netObjNewAxes.transform.position += center;
        SetAxesClientRpc(netObjNewAxes.NetworkObjectId, center, x1, x2, y1, y2, z1, z2, xIncrement, yIncrement, zIncrement, scaleZ, bigScale);

    }

    [ClientRpc]
    private void UpdateParentClientRpc(ulong childNetId, ulong parentNetId)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(parentNetId, out var networkParent))
        {
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(childNetId, out var networkChild))
            {
                networkChild.transform.SetParent(networkParent.transform);
            }
        }
    }

    [ClientRpc]
    private void SetAxesClientRpc(ulong netId, Vector3 center, float x1, float x2, float y1, float y2, float z1, float z2, float xIncrement, float yIncrement, float zIncrement, bool scaleZ, bool bigScale)
    {
        if (IsServer)
        {
            return;
        }
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkGraphAxes))
        {
            if (scaleZ)
            {
                if (bigScale)
                {
                    networkGraphAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 3, 3, 1.25f);
                }
                else
                {
                    networkGraphAxes.GetComponent<GraphAxisControl>().SetAxesAutoScale(x1, x2, y1, y2, z1, z2, 1.2f, 1.2f, 1.2f);
                }
            }
            else
            {
                networkGraphAxes.GetComponent<GraphAxisControl>().SetAxes(x1, x2, y1, y2, z1, z2, xIncrement, yIncrement, zIncrement);
            }
            networkGraphAxes.transform.position += center;
        }
    }

    [ClientRpc]
    private void SetSphericalAxesClientRpc(ulong netId, Vector3 center)
    {
        if (IsServer)
        {
            return;
        }
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkGraphAxes))
        {
            networkGraphAxes.GetComponent<SphericalAxisControl>().SetAxes();
            networkGraphAxes.transform.position += center;
        }
    }

    public void CreateNetworkLine(ulong netDrawingsId, int materialNumber, Vector3[] positions, float lineWidth)
    {
        CreateNetworkLineServerRpc(netDrawingsId, materialNumber, positions, lineWidth);
    }

    [ServerRpc(RequireOwnership = false)]
    private void CreateNetworkLineServerRpc(ulong netDrawingsId, int materialNumber, Vector3[] positions, float lineWidth, ServerRpcParams rpcParams = default)
    {
        GameObject NewLine = Instantiate(NetworkLinePrefab);
        NewLine.GetComponent<NetLineControl>().ChangeMaterial(materialNumber);
        NetworkObject networkObject = NewLine.GetComponent<NetworkObject>();
        networkObject.SpawnWithOwnership(rpcParams.Receive.SenderClientId);

        //parenting part
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netDrawingsId, out var networkDrawings))
        {
            NewLine.transform.SetParent(networkDrawings.transform);
            NewLine.transform.position = Vector3.zero;
            UpdateLineParentClientRpc(networkObject.NetworkObjectId, netDrawingsId, materialNumber);
        }
        networkObject.GetComponent<NetLineControl>().SyncLinePoints(positions, lineWidth);
        SyncLinePointsClientRpc(networkObject.NetworkObjectId, positions, lineWidth);
    }

    [ClientRpc]
    private void UpdateLineParentClientRpc(ulong lineNetId, ulong drawingsNetId, int materialNumber)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(drawingsNetId, out var networkDrawings))
        {
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(lineNetId, out var networkLine))
            {
                networkLine.transform.SetParent(networkDrawings.transform);
                networkLine.transform.position = Vector3.zero;
                networkLine.GetComponent<NetLineControl>().ChangeMaterial(materialNumber);
            }
        }
    }

    [ClientRpc]
    private void SyncLinePointsClientRpc(ulong networkLineId, Vector3[] positions, float lineWidth)
    { 
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkLineId, out var networkLineObject))
        {
            networkLineObject.GetComponent<NetLineControl>().SyncLinePoints(positions, lineWidth);
        }
    }

    public void MoveNetworkLines(ulong[] netIds, Vector3[] positions, Quaternion[] rotations, Vector3[] scales)
    {
        for (int i = 0; i < netIds.Length; i++)
        {
            if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netIds[i], out var networkLineObject))
            {
                networkLineObject.GetComponent<NetLineControl>().SyncTransforms(positions[i], rotations[i], scales[i]);
            }
        }
    }

    public void MoveNetworkDrawingsObject(ulong netId, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out var networkDrawingsObject))
        {
            networkDrawingsObject.GetComponent<NetDrawControl>().SyncTransforms(position, rotation, scale);
        }
    }

    public void RemoveNetworkLine(ulong netId)
    {
        RemoveNetworkLineServerRpc(netId);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RemoveNetworkLineServerRpc(ulong netId)
    {
        if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out NetworkObject networkLineObject))
        {
            if (networkLineObject.transform.childCount > 0)
            {
                if (networkLineObject.transform.GetChild(0).tag == "Axes")
                {
                    networkLineObject.transform.GetChild(0).GetComponent<NetworkObject>().Despawn(true);
                }
            }
            
            networkLineObject.Despawn(true);
        }
    }

    public void DeleteAllLines(ulong netDrawingsId)
    {
        DeleteAllLinesServerRpc(netDrawingsId);
    }

    [ServerRpc(RequireOwnership =false)]
    private void DeleteAllLinesServerRpc(ulong netDrawingsId)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netDrawingsId, out var networkDrawings))
        {
            for (int i = 0; i < networkDrawings.transform.childCount; i++)
            {
                NetworkObject line = networkDrawings.transform.GetChild(i).GetComponent<NetworkObject>();
                if (line.transform.childCount > 0)
                {
                    if (line.transform.GetChild(0).tag == "Axes")
                    {
                        line.transform.GetChild(0).GetComponent<NetworkObject>().Despawn(true);
                    }
                }
                line.Despawn(true);
            }
        }
    }

    public void DeleteLocalDrawings()
    {
        RequestDeleteLocalDrawingsServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestDeleteLocalDrawingsServerRpc()
    {
        DeleteLocalDrawingsClientRpc();
    }

    [ClientRpc]
    private void DeleteLocalDrawingsClientRpc()
    {
        MenuControl menu = GameObject.Find("Menu Manager").GetComponent<MenuControl>();
        menu.SceneManager.SetActive(true);
        LineDrawer lineDraw = GameObject.Find("Scene Two Manager").GetComponent<LineDrawer>();
        for (int i = 0; i < lineDraw.Drawings.childCount; i++)
        {
            Transform line = lineDraw.Drawings.GetChild(i).transform;
            Destroy(line.gameObject);
        }
        if (menu.myPlayer.GetIsRoomOwner())
        {
            menu.SceneManager.SetActive(false);
        }
        
    }

    public void ClearLineLists()
    {
        ClearLineListsServerRpc();
    }

    [ServerRpc(RequireOwnership =false)]
    private void ClearLineListsServerRpc()
    {
        ClearLineListsClientRpc();
    }

    [ClientRpc]
    private void ClearLineListsClientRpc()
    {
        GameObject.Find("Scene Two Manager").GetComponent<LineDrawer>().ClearLineLists();
    }

    public void DisableObject(ulong netId)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var netObj))
        {
            netObj.gameObject.SetActive(false);
        }
    }

    public void EnableObject(ulong netId)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(netId, out var netObj))
        {
            netObj.gameObject.SetActive(true);
        }
    }

    
}