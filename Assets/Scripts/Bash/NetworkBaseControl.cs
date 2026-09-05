using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One per base: the four gamepieces (boat 0, plane 1, sub 2, helicopter 3), their four pads at
/// child index n + 4, and the RPC pairs that keep piece visibility, selection rings and resets in
/// step across the room.
///
/// activeRot is owner-written, which is right and matches this project's rule that hand- and
/// head-like per-player values are owner-written. What changed in the port is the space: it is now
/// measured in BashRoot's local frame rather than world space, because the board moves, turns and
/// rescales under the two-grip world grab. See BashRoot.
///
/// Where a piece ENDS UP, though, is an RPC — MoveGamepiece — and not a NetworkVariable. See the
/// comment there: a pose that only says "where" and not "which piece" cannot be applied by a
/// client that has already been told the piece was deselected, and that is exactly the order
/// Netcode delivers the two in.
/// </summary>
public class NetworkBaseControl : NetworkBehaviour
{
    public Material[] cannonMaterials = new Material[4];
    public GameObject activeGamepiece;
    public ControlListener controls;
    public Material safeBase;
    public Material destroyedBase;

    // A board-local forward direction, and the *committed* heading: while spinning is true the
    // displayed heading is this rotated by the elapsed angle, and freezing writes the derived
    // heading back into it.
    public NetworkVariable<Vector3> activeRot = new NetworkVariable<Vector3>(new Vector3(0, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Server time the spin began. Owner-written like activeRot above, and for the same reason.
    ///
    /// The heading is derived from this rather than pushed, so a spin costs one write per
    /// selection instead of one NetworkVariable delta per network tick for as long as anybody
    /// anywhere has a piece selected — and every headset draws the cannon dot in the same place
    /// because they are all evaluating the same formula against the same NetworkManager.ServerTime.
    /// </summary>
    public NetworkVariable<double> spinStartTime = new NetworkVariable<double>(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> spinning = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>A full sweep every four seconds. Shared, not per-base: every client derives the
    /// heading from it, so a value that differed between headsets would put the dot in a
    /// different place on each.</summary>
    public const float SpinDegreesPerSecond = 90f;

    private void Start()
    {
        if (IsOwner)
        {
            AttachToControls();
        }

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

    // ------------------------------------------------------------------ the spinning aim

    /// <summary>
    /// Draw the spin. Every client runs this for every base, so the first line is the whole cost
    /// for a base with nothing selected: one bool read per frame.
    ///
    /// The piece is what rotates — the coloured cannon dot is its fourth child and has no
    /// transform of its own that moves, so "the dot swings round the piece" and "the piece spins"
    /// are the same change.
    /// </summary>
    void Update()
    {
        if (!spinning.Value || activeGamepiece == null)
        {
            return;
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        float angle = (float)(nm.ServerTime.Time - spinStartTime.Value) * SpinDegreesPerSecond;
        Look(activeGamepiece.transform, BashRoot.RotateInXZ(activeRot.Value, angle));
    }

    /// <summary>Start sweeping from the committed heading. One write, not one per tick.</summary>
    public void StartSpin()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (!IsOwner || nm == null)
        {
            return;
        }

        spinStartTime.Value = nm.ServerTime.Time;
        spinning.Value = true;
    }

    /// <summary>
    /// Commit the spun heading, so everybody snaps to exactly the heading the shooter saw.
    ///
    /// Must run BEFORE the shot samples the muzzle: Netcode raises OnValueChanged synchronously on
    /// the writer, so writing activeRot here turns the piece in the same statement and the muzzle
    /// read that follows is already correct. Reverse the two and every shot leaves from one
    /// frame's worth of rotation behind where the player aimed.
    /// </summary>
    public void FreezeSpin()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (!IsOwner || !spinning.Value || nm == null)
        {
            return;
        }

        float angle = (float)(nm.ServerTime.Time - spinStartTime.Value) * SpinDegreesPerSecond;
        activeRot.Value = BashRoot.RotateInXZ(activeRot.Value, angle);
        spinning.Value = false;
    }

    // ------------------------------------------------------------------ the end of a move

    /// <summary>
    /// Put a piece where its move ended, on every client.
    ///
    /// An RPC pair rather than a NetworkVariable, and that is the whole point of it. It used to be
    /// a board-local `activePos` whose OnValueChanged moved `activeGamepiece` — but the frame that
    /// publishes the pose is the same frame that deselects, and Netcode does not deliver those two
    /// together: an RPC is queued the moment it is called, a NetworkVariable delta only at the end
    /// of the tick. So SetActiveGamepieceClientRpc(-1) reliably arrived FIRST and every client but
    /// the shooter applied the new position with activeGamepiece already null — the trail appeared
    /// and the piece stayed on its old square until its owner selected it again, which republished
    /// a pose at a moment when something was selected to receive it.
    ///
    /// Naming the piece in the message removes the dependency altogether: it no longer matters
    /// what is selected anywhere, or when this arrives relative to anything else.
    /// </summary>
    public void MoveGamepiece(int n, Vector3 localPos, Vector3 localForward)
    {
        // Locally first, so the shooter's own piece does not wait on the round trip. The ClientRpc
        // comes back to the shooter too and re-applies the same numbers, which is harmless.
        ApplyMove(n, localPos, localForward);
        MoveGamepieceServerRpc(n, localPos, localForward);
    }

    [ServerRpc(RequireOwnership = false)]
    private void MoveGamepieceServerRpc(int n, Vector3 localPos, Vector3 localForward)
    {
        MoveGamepieceClientRpc(n, localPos, localForward);
    }

    [ClientRpc]
    private void MoveGamepieceClientRpc(int n, Vector3 localPos, Vector3 localForward)
    {
        ApplyMove(n, localPos, localForward);
    }

    /// <summary>Board-local in, world out: a piece hangs under Bash Root, which the players are
    /// free to move, turn and rescale under it. See BashRoot.</summary>
    void ApplyMove(int n, Vector3 localPos, Vector3 localForward)
    {
        if (n < 0 || n > 3)
        {
            return;
        }

        Transform piece = transform.GetChild(n);
        piece.position = BashRoot.ToWorldPoint(localPos);
        Look(piece, localForward);

        //turn off the yellow ring if it is still on
        if (piece.GetChild(1).gameObject.activeSelf)
        {
            piece.GetChild(1).gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Let the piece go at the end of a turn: no selection ring, nothing spinning. SetActiveGamepiece
    /// alone is not enough — it does not touch the ring, which ChangeGamePiece turns off separately.
    ///
    /// Safe to call in any order relative to MoveGamepiece, which is the point of that being an
    /// RPC that names its piece. It was not always: while the move rode on an `activePos`
    /// NetworkVariable, deselecting first nulled the very reference the pose callback needed.
    /// </summary>
    public void Deselect()
    {
        if (activeGamepiece != null)
        {
            TurnOffSelectionRing(activeGamepiece.transform.GetSiblingIndex());
        }

        if (IsOwner)
        {
            spinning.Value = false;
        }

        SetActiveGamepiece(-1);
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
                // Owner-written, so only the player whose base this is may publish the heading.
                activeRot.Value = BashRoot.ToLocalDirection(activeGamepiece.transform.forward);
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

    /// <summary>
    /// Select one of this base's pieces. Returns whether the selection actually took: the early
    /// returns below are the normal case, not an error, and ControlListener has to know which
    /// phase to start (or whether to start one at all) from the answer.
    /// </summary>
    public bool ChangeGamePiece(GameObject newGamepiece)
    {
        // A destroyed piece is SetActive(false) rather than deleted, and pointerControl gets no
        // OnTriggerExit when its target is switched off under it — so without this a stale
        // currentGamepiece would let a player re-select a piece that is already dead.
        if (newGamepiece == null || !newGamepiece.activeInHierarchy)
        {
            return false;
        }

        NetworkObject owner = newGamepiece.GetComponentInParent<NetworkObject>();
        if (owner == null || !owner.IsOwner)
        {
            return false;               // somebody else's piece, or not part of a base at all
        }

        if (activeGamepiece != null)
        {
            TurnOffSelectionRing(activeGamepiece.transform.GetSiblingIndex());
        }

        // Stop any spin the outgoing piece was carrying before SetActiveGamepiece publishes the
        // incoming one's heading, so the new spin starts from that heading rather than inheriting
        // a stale spinStartTime and jumping.
        if (IsOwner)
        {
            spinning.Value = false;
        }

        SetActiveGamepiece(newGamepiece.transform.GetSiblingIndex());
        TurnOnSelectionRing(activeGamepiece.transform.GetSiblingIndex());
        return true;
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
