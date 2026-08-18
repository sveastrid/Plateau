using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using TMPro;
using Unity.Collections;

/// <summary>
/// Order 20: read the hand and head world poses AFTER BoardAnchor (10) has aligned the rig this
/// frame. At the default order this samples myCam and the hand transforms before the rig has moved,
/// so every pose it broadcasts is one frame stale in the shared frame — about 13 ms at 72 Hz, on top
/// of the network latency.
/// </summary>
[DefaultExecutionOrder(20)]
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
    public Transform body;
    public Transform rig;

    // The scene's rig. Cached because the slot below and the rig arrive independently, so the
    // placement has to be retriable without repeating the GameObject.Find.
    private CameraController2 mainRig;

    // The mainFace mesh pivot sits this far below the eye point. Used by the owner to write
    // facePos and by everyone else to recover the eye point from it, so it is one constant
    // rather than the same magic number in two places.
    private const float FaceBelowEyes = 0.36f;

    // Nametag clearance above the eye point. Must clear the top of a REAL head seen through
    // passthrough, not the top of the virtual one — the virtual head is never drawn.
    public float NameTagAboveEyes = 0.28f;

    // Master switch for the remote avatar's head and body. Off: they never render, for anybody.
    // A remote player is two cones and a name. In passthrough their real head and body are
    // already there to look at, and a virtual copy of them is at best noise and at worst drawn
    // somewhere they are not.
    public bool ShowRemoteHeadAndBody = false;

    // Convergence time for remote hands and head. Long enough to hide the 33 ms gap between ticks,
    // short enough not to add lag of its own on top of what the network already costs.
    public float RemoteSmoothTime = 0.06f;

    // The filtered head position. Kept as a value rather than read back off `face`, because the
    // nametag is derived from it too and the two must not diverge.
    private Vector3 smoothFacePos;
    private bool remotePrimed;

    public NetworkVariable<FixedString32Bytes> playerName = new NetworkVariable<FixedString32Bytes>("username", NetworkVariableReadPermission.Everyone,NetworkVariableWritePermission.Server);
    public NetworkVariable<Vector3> lHPos = new NetworkVariable<Vector3>(new Vector3(0,0,0),NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> rHPos = new NetworkVariable<Vector3>(new Vector3(0, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Vector3> facePos = new NetworkVariable<Vector3>(new Vector3(1, 0, 0), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // Rotations, not forward vectors. A forward vector is three numbers and a rotation is four:
    // rebuilding one with Quaternion.LookRotation discards roll — spin your wrist and the remote
    // cone does not — and is ill-conditioned when the direction is near vertical, because the roll
    // comes from cross(up, f), which vanishes there. Pointing a controller straight down at the
    // board is not an edge case in a board game, it is the default posture. Netcode serializes
    // Quaternion natively, and it is 16 bytes against 12.
    public NetworkVariable<Quaternion> lHRot = new NetworkVariable<Quaternion>(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Quaternion> rHRot = new NetworkVariable<Quaternion>(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    public NetworkVariable<Quaternion> faceRot = new NetworkVariable<Quaternion>(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> roomOwner = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Which place on the ring around the board this player stands on — see PlayerRing.
    /// Server-written so that two players joining at the same moment cannot claim the same
    /// place, and -1 until the server has picked one.
    /// </summary>
    public NetworkVariable<int> spawnSlot = new NetworkVariable<int>(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        // --- Prefab-local. Resolved once; these children never go away. ---
        usernameTransform = FindChild("Username");
        playerLeft        = FindChild("PlayerLeft");
        playerRight       = FindChild("PlayerRight");
        face              = FindChild("mainFace");
        body              = FindChild("tornado");
        username          = usernameTransform != null
                                ? usernameTransform.GetComponent<TextMeshPro>()
                                : null;

        if (username != null)
        {
            username.SetText(playerName.Value.ToString());
        }
        playerName.OnValueChanged += HandleNameChanged;

        // Where this player stands. Assigned once, on the server, and then left alone: nobody
        // who is already in the room gets moved when somebody else joins.
        if (IsServer)
        {
            spawnSlot.Value = PlayerRing.PickFreeSlot(OccupiedSlots());
        }
        spawnSlot.OnValueChanged += HandleSpawnSlotChanged;

        // RelayVivox rides the NetworkManager object, which is DontDestroyOnLoad, so this one
        // survives scene changes too. GameObject.Find does search the DontDestroyOnLoad scene.
        relayVivoxInfo = FindComponent<RelayVivox>("Network Manager");

        if (IsOwner)
        {
            if (relayVivoxInfo != null)
            {
                SetRoomOwnerServerRpc(relayVivoxInfo.roomOwner);
                SetPlayerNameServerRpc(Clamp(relayVivoxInfo.myUserDisplayName));
            }

            // You are inside your own avatar; do not render it for yourself.
            for (int i = 0; i < transform.childCount; i++)
            {
                transform.GetChild(i).gameObject.SetActive(false);
            }
        }
        else
        {
            ApplyAvatarVisibility();
        }

        // --- Scene-local. Re-resolved after every game switch. ---
        BindToScene();
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
    }

    public override void OnNetworkDespawn()
    {
        SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
        playerName.OnValueChanged -= HandleNameChanged;
        spawnSlot.OnValueChanged -= HandleSpawnSlotChanged;
    }

    /// <summary>
    /// Remote-only. One place decides whether the head and body render, so the prefab's authored
    /// state and the runtime state cannot disagree. Called once at spawn; add callers if
    /// ShowRemoteHeadAndBody ever becomes something other than a constant.
    ///
    /// Deactivating rather than deleting is deliberate: Update() goes on writing face.position and
    /// face.rotation unguarded every frame, and Transform.Find returns inactive children, so an
    /// inactive mainFace is free where a missing one is a NullReferenceException per frame per
    /// remote avatar. It also makes turning the head back on a single SetActive.
    /// </summary>
    private void ApplyAvatarVisibility()
    {
        if (body != null)
        {
            body.gameObject.SetActive(ShowRemoteHeadAndBody);
        }
        if (face != null)
        {
            face.gameObject.SetActive(ShowRemoteHeadAndBody);
        }
    }

    private void HandleNameChanged(FixedString32Bytes oldVal, FixedString32Bytes newVal)
    {
        if (username != null)
        {
            username.SetText(newVal.ToString());
        }
    }

    private void HandleSpawnSlotChanged(int oldVal, int newVal)
    {
        PlaceRigAtSlot();
    }

    /// <summary>
    /// Server only. Which places on the ring the players already in the room are standing on.
    /// Read from the live players rather than tracked in a static, so a slot cannot leak if a
    /// client drops in a way that skips OnNetworkDespawn, and so it survives a game switch for
    /// free. A player whose slot is still -1 — including this one, mid-spawn — contributes
    /// nothing and is skipped.
    /// </summary>
    private static HashSet<int> OccupiedSlots()
    {
        HashSet<int> occupied = new HashSet<int>();
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return occupied;
        }

        foreach (NetworkClient client in nm.ConnectedClientsList)
        {
            if (client == null || client.PlayerObject == null)
            {
                continue;
            }

            PlayerControls other = client.PlayerObject.GetComponent<PlayerControls>();
            if (other == null)
            {
                continue;
            }

            int slot = other.spawnSlot.Value;
            if (slot >= 0 && slot < PlayerRing.SlotCount)
            {
                occupied.Add(slot);
            }
        }

        return occupied;
    }

    /// <summary>
    /// Stand this client's user on the slot the server picked for them. Only the owner has a rig
    /// to move. The slot and the rig arrive independently — the slot from the server, the rig
    /// from whichever scene is loaded — so this runs from both BindToScene and
    /// spawnSlot.OnValueChanged, and does nothing until it has both.
    /// </summary>
    private void PlaceRigAtSlot()
    {
        if (!IsOwner || mainRig == null || spawnSlot.Value < 0)
        {
            return;
        }

        mainRig.PlaceAtRingSlot(spawnSlot.Value);
    }

    private void HandleActiveSceneChanged(Scene from, Scene to)
    {
        BindToScene();
    }

    /// <summary>
    /// Re-resolve everything that lives in the current scene. The Player survives the scene loads
    /// GameSelector triggers; the rig, the camera, the hands and the Menu Manager do not. Without
    /// this, myCam is a destroyed object after the first game switch, PlayerControls.Update()
    /// early-returns forever, and every remote avatar freezes with its hands and face at the
    /// origin while the tornado body keeps tracking. That asymmetry is the tell.
    /// </summary>
    private void BindToScene()
    {
        inputs     = FindComponent<InputReader>("Input Reader", required: false);
        myCam      = Camera.main != null ? Camera.main.transform : null;
        localLeft  = FindTransform("Left Hand");
        localRight = FindTransform("Right Hand");
        rig        = FindTransform("XRRig");

        if (!IsOwner)
        {
            return;
        }

        // Both of these are new instances in the new scene and have never heard of this player.
        // The rig also needs the hand it moves along, which is the one just resolved above.
        mainRig = FindComponent<CameraController2>("XRRig");
        if (mainRig != null)
        {
            mainRig.Setup(this, localLeft);
        }

        MenuControl menu = FindComponent<MenuControl>("Menu Manager", required: false);
        if (menu != null)
        {
            menu.Setup(this);
        }

        // The new scene's rig starts at its authored transform, so put the user back on their
        // place around the board. No-op until the server has assigned one.
        PlaceRigAtSlot();
    }

    /// <summary>
    /// playerName is a FixedString32Bytes — 29 bytes of UTF-8. The lobby keyboard has no length
    /// limit, so an over-long username throws inside SetPlayerNameServerRpc on the server and
    /// takes the name down for everybody.
    /// </summary>
    private static string Clamp(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "player";
        }
        return name.Length <= 29 ? name : name.Substring(0, 29);
    }

    // Update is called once per frame
    void Update()
    {
        if (!IsOwner)
        {
            // All four are dereferenced unguarded below, every frame. Without this a renamed child
            // in Player.prefab turns FindChild's one-time error into a NullReferenceException per
            // frame per remote avatar.
            if (myCam == null || usernameTransform == null || playerLeft == null ||
                playerRight == null || face == null)
            {
                return;
            }

            // NetworkVariable delivers at the tick rate (30 Hz) and the headset renders at 72-90, so
            // remote hand values arrive on roughly every third frame and assigning them raw makes
            // the cones step. NetworkVariable does no interpolation of its own — the Interpolate
            // flag on the Player root applies to ClientNetworkTransform, and the hands do not go
            // through it. This removes the stepping; it does not remove the latency, which is a
            // different problem and is bounded by the tick rate.
            //
            // A player who has just spawned must snap, not glide in from the origin.
            float t = 1f;
            if (remotePrimed)
            {
                t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, RemoteSmoothTime));
            }
            else
            {
                remotePrimed = true;
                smoothFacePos = facePos.Value;
            }

            // Smooth the VALUE once and drive both the head and the tag from it. The tag is
            // computed from facePos rather than from face.position, so smoothing `face` alone would
            // leave the tag stepping while the (invisible) head glided.
            smoothFacePos = Vector3.Lerp(smoothFacePos, facePos.Value, t);

            // The tag used to sit at a fixed 1.43 m above the avatar root, and the root is on the
            // real floor — so on anyone eye-height 1.45 m or taller it rendered across their face.
            // Drive it off the head pose instead, which is already on the wire. Position before
            // rotation: the billboard is derived from where the tag IS, so computing it first also
            // removes a one-frame lag that was there before.
            usernameTransform.position = smoothFacePos +
                new Vector3(0f, FaceBelowEyes + NameTagAboveEyes, 0f);

            Vector3 lookDirection = myCam.position - usernameTransform.position;
            usernameTransform.rotation = Quaternion.LookRotation(-lookDirection);

            // No LookRotation reconstruction, no degenerate case, no lost roll.
            playerLeft.position = Vector3.Lerp(playerLeft.position, lHPos.Value, t);
            playerLeft.rotation = Quaternion.Slerp(playerLeft.rotation, lHRot.Value, t);
            playerRight.position = Vector3.Lerp(playerRight.position, rHPos.Value, t);
            playerRight.rotation = Quaternion.Slerp(playerRight.rotation, rHRot.Value, t);

            face.position = smoothFacePos;
            face.rotation = Quaternion.Slerp(face.rotation, faceRot.Value, t);
        }
        else
        {
            if (myCam == null || rig == null || localLeft == null || localRight == null)
            {
                return;
            }
            Vector3 faceOffset = new Vector3(0, FaceBelowEyes, 0);
            // Floor-level tracking origin: the rig's y IS the real floor, so project the head
            // straight down onto it. The old code subtracted a hardcoded 1.36 m eye height,
            // which now double-counts and leaves the avatar root floating ~0.3 m up.
            this.transform.position = new Vector3(myCam.position.x, rig.position.y, myCam.position.z);
            facePos.Value = myCam.position - faceOffset;
            faceRot.Value = myCam.rotation;
            // Was localLeft.position + 0.2f * localLeft.forward. That +0.2 very nearly cancelled the
            // cones' -1.22/-1.23 local z in Player.prefab, and what survived was a ~18 cm lever arm
            // along the grip's forward axis: rotate a wrist and the remote cone swept an arc. The
            // visual offset now lives entirely in the prefab, so what goes on the wire is the pose
            // the runtime actually reported and the remote cone matches the local controller model.
            lHPos.Value = localLeft.position;
            rHPos.Value = localRight.position;
            lHRot.Value = localLeft.rotation;
            rHRot.Value = localRight.rotation;
        }
    }

    /// <summary>
    /// A child of the Player prefab, by name rather than by index. The four avatar parts used to
    /// be resolved with GetChild(1)..GetChild(4), so reordering them in the Hierarchy compiled
    /// fine and produced a scrambled avatar. A rename now says so instead.
    /// </summary>
    private Transform FindChild(string childName)
    {
        Transform child = transform.Find(childName);
        if (child == null)
        {
            Debug.LogError("PlayerControls: Player.prefab has no child named \"" + childName + "\".");
        }
        return child;
    }

    private static Transform FindTransform(string objectName)
    {
        GameObject go = GameObject.Find(objectName);
        if (go == null)
        {
            // A warning, not an error: BindToScene also runs during the lobby -> game
            // transition, and a one-frame miss there is normal.
            Debug.LogWarning("PlayerControls: no GameObject named \"" + objectName + "\" in this scene.");
            return null;
        }
        return go.transform;
    }

    private static T FindComponent<T>(string objectName, bool required = true) where T : Component
    {
        GameObject go = GameObject.Find(objectName);
        if (go == null)
        {
            if (required)
            {
                Debug.LogError("PlayerControls: no GameObject named \"" + objectName + "\" in this scene.");
            }
            return null;
        }

        T component = go.GetComponent<T>();
        if (component == null && required)
        {
            Debug.LogError("PlayerControls: \"" + objectName + "\" has no " + typeof(T).Name + " component.");
        }
        return component;
    }

    [ServerRpc(RequireOwnership =false)]
    private void SetPlayerNameServerRpc(string name)
    {
        playerName.Value = name;
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
