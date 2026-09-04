using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One per base: the four gamepieces (boat 0, plane 1, sub 2, helicopter 3), their four pads at
/// child index n + 4, and the RPC pairs that keep piece visibility, selection rings and resets in
/// step across the room.
///
/// activePos/activeRot are owner-written, which is right and matches this project's rule that
/// hand- and head-like per-player values are owner-written. What changed in the port is the
/// space: both are now measured in BashRoot's local frame rather than world space, because the
/// board moves, turns and rescales under the two-grip world grab. See BashRoot.
/// </summary>
public class NetworkBaseControl : NetworkBehaviour
{
    public Material[] cannonMaterials = new Material[4];
    public GameObject activeGamepiece;
    public ControlListener controls;
    public Material safeBase;
    public Material destroyedBase;

    // Board-local position, and a board-local forward direction.
    public NetworkVariable<Vector3> activePos = new NetworkVariable<Vector3>(new Vector3(0, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> activeRot = new NetworkVariable<Vector3>(new Vector3(0, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private void Start()
    {
        if (IsOwner)
        {
            AttachToControls();
        }

        this.activePos.OnValueChanged += (oldVal, newVal) =>
        {
            if (activeGamepiece != null)
            {
                activeGamepiece.transform.position = BashRoot.ToWorldPoint(newVal);
                //turn off the yellow ring if it is still on
                if (activeGamepiece.transform.GetChild(1).gameObject.activeSelf)
                {
                    activeGamepiece.transform.GetChild(1).gameObject.SetActive(false);
                }
            }

        };

        this.activeRot.OnValueChanged += (oldVal, newVal) =>
        {
            if (activeGamepiece != null)
            {
                Look(activeGamepiece.transform, newVal);
            }

        };

        if (!IsOwner)
        {
            SyncOverNetworkServerRpc();
        }
    }

    /// <summary>
    /// A seat is reused when the player in it reconnects (PlayerRing.PickFreeSlot), and
    /// SpawnManager hands the standing base to the new client id rather than spawning a second
    /// one. Start() has long since run by then, so the wiring has to happen here too.
    /// </summary>
    public override void OnGainedOwnership()
    {
        base.OnGainedOwnership();
        AttachToControls();
    }

    public override void OnLostOwnership()
    {
        base.OnLostOwnership();
        if (controls != null && controls.netBaseControl == this)
        {
            controls.ConnectBaseToControls(null);
        }
        controls = null;
    }

    void AttachToControls()
    {
        if (controls == null)
        {
            GameObject go = GameObject.Find("Controls");
            controls = go != null ? go.GetComponent<ControlListener>() : null;
        }

        if (controls == null)
        {
            // "Controls" is one of this project's load-bearing Find-by-name objects. Warn rather
            // than throw: a base with nothing driving it is a cosmetic problem, an exception in
            // Start() takes the rest of the spawn with it.
            Debug.LogWarning("NetworkBaseControl: no 'Controls' object with a ControlListener in " +
                             "this scene, so this base cannot be played.");
            return;
        }

        controls.ConnectBaseToControls(this);
    }

    /// <summary>Point a piece along a board-local direction, ignoring a degenerate one.</summary>
    static void Look(Transform piece, Vector3 localForward)
    {
        Vector3 world = BashRoot.ToWorldDirection(localForward);
        if (world.sqrMagnitude > 0f)
        {
            piece.rotation = Quaternion.LookRotation(world);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void SyncOverNetworkServerRpc(ServerRpcParams rpcParams = default)
    {
        int childNumber = -1;
        if (activeGamepiece != null)
        {
            childNumber = activeGamepiece.transform.GetSiblingIndex();
        }

        bool[] baseActive = new bool[4];
        Vector3[] gamepiecePos = new Vector3[4];
        Vector3[] gamepieceRot = new Vector3[4];

        for (int i = 0; i < 4; i++)
        {
            Transform tempGamePiece = transform.GetChild(i);
            baseActive[i] = tempGamePiece.gameObject.activeSelf;
            gamepiecePos[i] = BashRoot.ToLocalPoint(tempGamePiece.position);
            gamepieceRot[i] = BashRoot.ToLocalDirection(tempGamePiece.forward);
        }

        SyncOverNetworkClientRpc(childNumber, baseActive[0], gamepiecePos[0], gamepieceRot[0], baseActive[1], gamepiecePos[1], gamepieceRot[1], baseActive[2], gamepiecePos[2], gamepieceRot[2], baseActive[3], gamepiecePos[3], gamepieceRot[3], new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { rpcParams.Receive.SenderClientId }
            }
        });
    }

    [ClientRpc]
    private void SyncOverNetworkClientRpc(int activeGamepieceNumber, bool base1Active, Vector3 boatPos, Vector3 boatRot, bool base2Active, Vector3 airplanePos, Vector3 airplaneRot, bool base3Active, Vector3 subPos, Vector3 subRot, bool base4Active, Vector3 heliPos, Vector3 heliRot, ClientRpcParams rpcParams = default)
    {
        if (activeGamepieceNumber != -1)
        {
            TurnOnSelectionRing(activeGamepieceNumber);
            activeGamepiece = transform.GetChild(activeGamepieceNumber).gameObject;
        }

        if (base1Active)
        {
            transform.GetChild(0).position = BashRoot.ToWorldPoint(boatPos);
            Look(transform.GetChild(0), boatRot);
        }
        else
        {
            TurnOffGamepiece(0);
        }
        if (base2Active)
        {
            transform.GetChild(1).position = BashRoot.ToWorldPoint(airplanePos);
            Look(transform.GetChild(1), airplaneRot);
        }
        else
        {
            TurnOffGamepiece(1);
        }
        if (base3Active)
        {
            transform.GetChild(2).position = BashRoot.ToWorldPoint(subPos);
            Look(transform.GetChild(2), subRot);
        }
        else
        {
            TurnOffGamepiece(2);
        }
        if (base4Active)
        {
            transform.GetChild(3).position = BashRoot.ToWorldPoint(heliPos);
            Look(transform.GetChild(3), heliRot);
        }
        else
        {
            TurnOffGamepiece(3);
        }
    }

    public void TurnOffMyColliders()
    {
        transform.GetChild(0).GetComponent<Collider>().enabled = false;
        transform.GetChild(1).GetComponent<Collider>().enabled = false;
        transform.GetChild(2).GetComponent<Collider>().enabled = false;
        transform.GetChild(3).GetComponent<Collider>().enabled = false;
    }

    public void TurnOnMyColliders()
    {
        transform.GetChild(0).GetComponent<Collider>().enabled = true;
        transform.GetChild(1).GetComponent<Collider>().enabled = true;
        transform.GetChild(2).GetComponent<Collider>().enabled = true;
        transform.GetChild(3).GetComponent<Collider>().enabled = true;
    }

    public void ChangeMaterial(int n)
    {
        if (cannonMaterials == null || cannonMaterials.Length == 0)
        {
            return;
        }

        // A seat past the four playing ones never gets a base, but clamp anyway rather than
        // throw inside a ClientRpc.
        n = Mathf.Clamp(n, 0, cannonMaterials.Length - 1);

        transform.GetChild(0).GetChild(3).GetComponent<Renderer>().material = cannonMaterials[n];
        transform.GetChild(1).GetChild(3).GetComponent<Renderer>().material = cannonMaterials[n];
        transform.GetChild(2).GetChild(3).GetComponent<Renderer>().material = cannonMaterials[n];
        transform.GetChild(3).GetChild(3).GetComponent<Renderer>().material = cannonMaterials[n];
    }

    public void SetActiveGamepiece(int n)
    {
        if (n == -1)
        {
            activeGamepiece = null;
        }
        else
        {
            activeGamepiece = transform.GetChild(n).gameObject;
            if (IsOwner)
            {
                // Owner-written, so only the player whose base this is may publish the pose.
                activeRot.Value = BashRoot.ToLocalDirection(activeGamepiece.transform.forward);
                activePos.Value = BashRoot.ToLocalPoint(activeGamepiece.transform.position);
            }
        }
        SetActiveGamepieceServerRpc(n);
    }

    [ServerRpc(RequireOwnership = false)]
    private void SetActiveGamepieceServerRpc(int n)
    {
        if (n == -1)
        {
            activeGamepiece = null;
        }
        else
        {
            activeGamepiece = transform.GetChild(n).gameObject;
        }
        SetActiveGamepieceClientRpc(n);
    }

    [ClientRpc]
    private void SetActiveGamepieceClientRpc(int n)
    {
        if (n == -1)
        {
            activeGamepiece = null;
        }
        else
        {
            activeGamepiece = transform.GetChild(n).gameObject;
        }
    }

    public void TurnOffGamepiece(int n)
    {
        transform.GetChild(n).gameObject.SetActive(false);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = destroyedBase;
        TurnOffGamepieceServerRpc(n);
    }

    [ServerRpc(RequireOwnership = false)]
    private void TurnOffGamepieceServerRpc(int n)
    {
        transform.GetChild(n).gameObject.SetActive(false);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = destroyedBase;
        TurnOffGamepieceClientRpc(n);
    }

    [ClientRpc]
    private void TurnOffGamepieceClientRpc(int n)
    {
        TurnOffSelectionRing(n);
        transform.GetChild(n).gameObject.SetActive(false);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = destroyedBase;
    }

    public void TurnOnGamepiece(int n)
    {
        transform.GetChild(n).gameObject.SetActive(true);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = safeBase;
        TurnOnGamepieceServerRpc(n);
    }

    [ServerRpc(RequireOwnership = false)]
    private void TurnOnGamepieceServerRpc(int n)
    {
        transform.GetChild(n).gameObject.SetActive(true);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = safeBase;
        TurnOnGamepieceClientRpc(n);
    }

    [ClientRpc]
    private void TurnOnGamepieceClientRpc(int n)
    {
        transform.GetChild(n).gameObject.SetActive(true);
        transform.GetChild(n + 4).GetComponent<Renderer>().material = safeBase;
    }

    public void ChangeGamePiece(GameObject newGamepiece)
    {
        // A destroyed piece is SetActive(false) rather than deleted, and pointerControl gets no
        // OnTriggerExit when its target is switched off under it — so without this a stale
        // currentGamepiece would let a player re-select a piece that is already dead.
        if (newGamepiece == null || !newGamepiece.activeInHierarchy)
        {
            return;
        }

        NetworkObject owner = newGamepiece.GetComponentInParent<NetworkObject>();
        if (owner == null || !owner.IsOwner)
        {
            return;                     // somebody else's piece, or not part of a base at all
        }

        if (activeGamepiece != null)
        {
            TurnOffSelectionRing(activeGamepiece.transform.GetSiblingIndex());
        }
        SetActiveGamepiece(newGamepiece.transform.GetSiblingIndex());
        TurnOnSelectionRing(activeGamepiece.transform.GetSiblingIndex());
    }

    //piece numbers are boat="0", plane = "1", sub = "2", heli = "3"
    public void TurnOffSelectionRing(int pieceNumber)
    {
        TurnOffSelectionRingServerRpc(pieceNumber);
    }

    [ServerRpc(RequireOwnership = false)]
    private void TurnOffSelectionRingServerRpc(int pieceNumber)
    {
        transform.GetChild(pieceNumber).GetChild(2).gameObject.SetActive(false);
        TurnOffSelectionRingClientRpc(pieceNumber);
    }

    [ClientRpc]
    private void TurnOffSelectionRingClientRpc(int pieceNumber)
    {
        transform.GetChild(pieceNumber).GetChild(2).gameObject.SetActive(false);
    }

    //piece numbers are boat="0", plane = "1", sub = "2", heli = "3"
    public void TurnOnSelectionRing(int pieceNumber)
    {
        TurnOnSelectionRingServerRpc(pieceNumber);
    }

    //piece numbers are boat="0", plane = "1", sub = "2", heli = "3"
    [ServerRpc(RequireOwnership = false)]
    private void TurnOnSelectionRingServerRpc(int pieceNumber)
    {
        transform.GetChild(pieceNumber).GetChild(2).gameObject.SetActive(true);
        TurnOnSelectionRingClientRpc(pieceNumber);
    }

    [ClientRpc]
    private void TurnOnSelectionRingClientRpc(int pieceNumber)
    {
        transform.GetChild(pieceNumber).GetChild(2).gameObject.SetActive(true);
    }

    public void ResetBases()
    {
        ResetBasesServerRpc();
        DeleteAllLinesServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void DeleteAllLinesServerRpc()
    {
        // Find all objects with the tag "line"
        GameObject[] lineObjects = GameObject.FindGameObjectsWithTag("line");

        foreach (GameObject obj in lineObjects)
        {
            if (obj.TryGetComponent(out NetworkObject networkObject))
            {
                // If it's a NetworkObject, despawn it from the network
                if (networkObject.IsSpawned)
                {
                    networkObject.Despawn(true); // true to destroy it after despawning
                }
            }
            else
            {
                // If it's not a NetworkObject, destroy it locally
                Destroy(obj);
            }
        }
        DeleteAllLinesClientRpc();
    }

    [ClientRpc]
    private void DeleteAllLinesClientRpc()
    {
        // Find all objects with the tag "line" on the client
        GameObject[] lineObjects = GameObject.FindGameObjectsWithTag("line");

        foreach (GameObject obj in lineObjects)
        {
            // Destroy any object that is not a NetworkObject
            if (!obj.TryGetComponent<NetworkObject>(out _))
            {
                Destroy(obj);
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void ResetBasesServerRpc()
    {
        if (activeGamepiece != null)
        {
            TurnOffGamepiece(activeGamepiece.transform.GetSiblingIndex());
        }

        SetActiveGamepiece(-1);
        for (int i = 0; i < 4; i++)
        {
            TurnOnGamepiece(i);
            transform.GetChild(i).localRotation = Quaternion.Euler(0, 0, 0);
        }
        transform.GetChild(0).localPosition = new Vector3(-.1448f, .011f, .0102f);
        transform.GetChild(1).localPosition = new Vector3(-.0466f, .011f, .008f);
        transform.GetChild(2).localPosition = new Vector3(.0556f, .011f, .0103f);
        transform.GetChild(3).localPosition = new Vector3(.1549f, .011f, .0105f);
        ResetBasesClientRpc();
    }

    [ClientRpc]
    private void ResetBasesClientRpc()
    {
        if (activeGamepiece != null)
        {
            TurnOffGamepiece(activeGamepiece.transform.GetSiblingIndex());
        }

        SetActiveGamepiece(-1);
        for (int i = 0; i < 4; i++)
        {
            TurnOnGamepiece(i);
            transform.GetChild(i).localRotation = Quaternion.Euler(0, 0, 0);
        }
        // Already relative to the base, so untouched by the port: a piece's home is where it was
        // authored on its pad, whatever the board is doing.
        transform.GetChild(0).localPosition = new Vector3(-.1448f, .011f, .0102f);
        transform.GetChild(1).localPosition = new Vector3(-.0466f, .011f, .008f);
        transform.GetChild(2).localPosition = new Vector3(.0556f, .011f, .0103f);
        transform.GetChild(3).localPosition = new Vector3(.1549f, .011f, .0105f);
    }
}
