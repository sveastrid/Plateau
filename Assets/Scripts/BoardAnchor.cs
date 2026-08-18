using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The common anchoring point: one shared spatial anchor that every headset in the room localizes,
/// and a per-frame alignment that moves THIS client's rig so that anchor lands on the world origin.
/// After that, world space is the same physical frame on every headset and every networked value in
/// the project — hands, head, nametag, avatar root — means the same place in the real room.
///
/// Lives on the Network Manager object, which Netcode marks DontDestroyOnLoad, because
/// GameSelector's LoadSceneMode.Single game switch destroys the rig, the camera, the hands and the
/// Menu Manager (PlayerControls.BindToScene, :214-251). The GameObject carrying the OVRSpatialAnchor
/// is DontDestroyOnLoad for the same reason: re-loading and re-localising it on every game switch
/// would be seconds of a wrong room every time somebody changes game.
///
/// Must run AFTER OVRSpatialAnchor.Update(), which is what refreshes the bound anchor's world pose
/// for this frame from the runtime's tracking-space pose. Reading it at the default order gets last
/// frame's rig baked in. Same value and same reason as Meta's own AlignCameraToAnchor.cs:28.
/// </summary>
[DefaultExecutionOrder(10)]
public class BoardAnchor : MonoBehaviour
{
    public static BoardAnchor Instance { get; private set; }

    /// <summary>Room Anchor.prefab — NetworkObject + RoomAnchor. Spawned once by the host.</summary>
    public GameObject roomAnchorPrefab;

    /// <summary>Seconds to wait for a downloaded anchor to localize before retrying.</summary>
    public double LocalizeTimeoutSeconds = 8;

    /// <summary>How many times to retry a failed load before giving up until the next request.</summary>
    public int LoadAttempts = 3;

    /// <summary>
    /// How long the anchor may be untracked before saying so. A one-frame dropout is normal and
    /// silent; the interesting events are "it stopped" and "it came back".
    /// </summary>
    public float UntrackedWarningSeconds = 2f;

    // The name is load-bearing only for reading logs; nothing looks these up.
    const string AnchorObjectName = "Room Anchor Point";

    OVRSpatialAnchor boundAnchor;
    GameObject anchorObject;

    // What the room says the anchor is, and what we actually got bound to. They differ while a load
    // is in flight, and while a load has failed.
    string desiredGroup = "";
    string desiredUuid = "";
    string loadedUuid = "";      // the pump's dedup key
    string boundUuid = "";       // what boundAnchor actually is; only ever set by AdoptAnchor

    // The latch. A load can be in flight for a minute with retries and an 8 second localize
    // timeout, which is a wide window to drop things in, and a dropped request leaves this one
    // client bound to a stale anchor permanently with nothing to tell it otherwise.
    bool loading;
    bool hasPending;
    string pendingGroup = "";
    string pendingUuid = "";

    // Set the moment we publish an anchor of our own, cleared when the server echoes it back. See
    // PollRoomAnchor: until then, what is on RoomAnchor is still the PREVIOUS anchor.
    bool placing;
    string publishedUuid = "";
    float publishWaitUntil;
    const float PublishEchoTimeoutSeconds = 15f;

    // Null in the Editor with no headset, and on any device where OVRManager never came up. Every
    // anchor call fails differently in that state, so the whole per-frame half of this component is
    // switched off — but the RoomAnchor spawn below is NOT, because the world grab, the content
    // frame and the lock are not anchor-dependent and have to stay testable without a headset.
    bool runtimeAvailable;

    // Both are scene objects, rebuilt on every game switch, so both are cleared and re-resolved on
    // SceneManager.activeSceneChanged — the same pattern PlayerControls.BindToScene already uses.
    CameraController2 rig;
    InputReader inputs;

    float untrackedSince = -1f;
    bool untrackedReported;

    void Awake()
    {
        Instance = this;

        // Static, so it outlives the rig that set it — and therefore also outlives a play session
        // in an Editor with domain reload switched off. Start from a known state.
        CameraController2.SetAligned(false);

        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
    }

    void OnDestroy()
    {
        SceneManager.activeSceneChanged -= HandleActiveSceneChanged;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnServerStarted -= HandleServerStarted;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    void Start()
    {
        // Subscribed here rather than in Awake: NetworkManager sets Singleton in its OnEnable
        // (NetworkManager.cs:1093-1096), and Awake ordering between two components on one
        // GameObject is not something to lean on. Start is after every Awake and OnEnable in the
        // scene, and the host cannot have pressed Enter yet.
        //
        // This runs even on the no-runtime path below, because the RoomAnchor spawn is what the
        // world grab and the shared board placement need, and neither of those is anchor-dependent.
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnServerStarted += HandleServerStarted;
        }
        else
        {
            Debug.LogError("BoardAnchor: no NetworkManager. It belongs on the same object as this " +
                           "component; without it nothing shared will ever spawn.");
        }

        runtimeAvailable = OVRManager.instance != null && OVRPlugin.initialized;
        if (!runtimeAvailable)
        {
            // Not an error. This is the Editor-without-a-headset path, and it has to keep working:
            // everything except the anchor itself is exercisable there.
            Debug.Log("BoardAnchor: no Meta runtime, so no colocation. Everything else — the world " +
                      "grab, the shared board placement, the ring — still runs.");
            enabled = false;
        }
    }

    void HandleServerStarted()
    {
        // Deliberately not in GameController, so the host path and any future path both get it.
        if (RoomAnchor.Instance != null)
        {
            return;
        }

        if (roomAnchorPrefab == null)
        {
            Debug.LogError("BoardAnchor: roomAnchorPrefab is not assigned, so no client will ever " +
                           "get an anchor or a shared board placement.");
            return;
        }

        GameObject go = Instantiate(roomAnchorPrefab);
        NetworkObject netObj = go.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            Debug.LogError("BoardAnchor: roomAnchorPrefab has no NetworkObject.");
            Destroy(go);
            return;
        }

        // destroyWithScene: false — this has to survive GameSelector's LoadSceneMode.Single switch,
        // which is the whole reason it is not a scene object.
        netObj.Spawn(false);
    }

    void HandleActiveSceneChanged(Scene from, Scene to)
    {
        // The rig and the Input Reader are new instances in the new scene and have never heard of
        // this component. The HoldAlignment early-out on a null rig means a frame or two of no
        // alignment during the load, which is invisible.
        rig = null;
        inputs = null;
    }

    void Update()
    {
        HoldAlignment();
        PollRoomAnchor();
        ReadInput();
    }

    // ---------------------------------------------------------------- alignment

    private void HoldAlignment()
    {
        if (boundAnchor == null)
        {
            return;
        }

        // Do not align to an anchor the runtime cannot currently locate. UpdateTransform only
        // writes the transform when it got a pose, so an untracked anchor leaves a stale one in
        // place — and for a freshly created GameObject that stale pose is the world origin with
        // identity rotation, which AlignRigToAnchor would happily accept as "already aligned".
        // Holding the last good rig pose is right: the user has not moved, the tracker has stopped
        // reporting. LocalIsAligned deliberately stays true through a dropout, so a two-second
        // occlusion does not hand locomotion and the ring slot back and teleport somebody.
        if (!boundAnchor.IsTracked)
        {
            NoteUntracked();
            return;
        }

        ClearUntracked();

        CameraController2 currentRig = Rig;
        if (currentRig != null)
        {
            currentRig.AlignRigToAnchor(boundAnchor.transform);
        }
    }

    private CameraController2 Rig
    {
        get
        {
            if (rig == null)
            {
                GameObject go = GameObject.Find("XRRig");
                rig = go != null ? go.GetComponent<CameraController2>() : null;
            }
            return rig;
        }
    }

    private void NoteUntracked()
    {
        if (untrackedSince < 0f)
        {
            untrackedSince = Time.realtimeSinceStartup;
            return;
        }

        if (!untrackedReported &&
            Time.realtimeSinceStartup - untrackedSince >= UntrackedWarningSeconds)
        {
            untrackedReported = true;
            Debug.LogWarning("BoardAnchor: the room anchor has been untracked for " +
                             UntrackedWarningSeconds.ToString("F0") + " s. Holding the last good " +
                             "alignment. Look around the room to re-acquire it.");
        }
    }

    private void ClearUntracked()
    {
        if (untrackedReported)
        {
            Debug.Log("BoardAnchor: room anchor tracking recovered.");
        }
        untrackedSince = -1f;
        untrackedReported = false;
    }

    // ---------------------------------------------------------------- input

    private void ReadInput()
    {
        if (inputs == null)
        {
            GameObject go = GameObject.Find("Input Reader");
            inputs = go != null ? go.GetComponent<InputReader>() : null;
            if (inputs == null)
            {
                return;
            }
        }

        // A recovery action a user needs quickly, mid-game, without a menu in their face. ButtonA,
        // ButtonB, ButtonY and both grips are read nowhere else in Assets/Scripts; the world grab
        // takes both grips, which leaves A free.
        if (inputs.ButtonADown)
        {
            RequestReAlign();
        }
    }

    /// <summary>
    /// Re-download and re-localize whatever anchor the room is currently published on. The user
    /// facing recovery for "my room is in the wrong place" and for a load that failed earlier.
    /// </summary>
    public void RequestReAlign()
    {
        if (!runtimeAvailable)
        {
            return;
        }

        if (string.IsNullOrEmpty(desiredUuid))
        {
            Debug.Log("BoardAnchor: nothing to re-align to — the room owner has not placed an " +
                      "anchor yet.");
            return;
        }

        // Drop the current binding first. An anchor that is already bound to an OVRSpatialAnchor is
        // filtered out of the query results (OVRSpatialAnchor.TryGetUnbound, :1470-1476), so asking
        // for the same UUID again while still holding it comes back empty and the re-align would
        // report a download failure that never happened. OnDestroy is what removes it from the
        // SDK's bound set, and the fetch below spans frames, so the ordering works out.
        ReleaseAnchor();

        // Forget what we think we are bound to, so the pending != current guard in the pump does
        // not swallow a deliberate press.
        loadedUuid = "";
        Debug.Log("BoardAnchor: re-aligning to the room anchor.");
        RequestLoad(desiredGroup, desiredUuid);
    }

    /// <summary>
    /// Let go of the current anchor. Alignment stops until a new one is adopted, which is honest:
    /// this client no longer has a shared frame, so locomotion and the recentre button come back.
    /// </summary>
    private void ReleaseAnchor()
    {
        if (anchorObject != null)
        {
            Destroy(anchorObject);
        }
        anchorObject = null;
        boundAnchor = null;
        boundUuid = "";
        untrackedSince = -1f;
        untrackedReported = false;
        CameraController2.SetAligned(false);
    }

    // ---------------------------------------------------------------- placing

    /// <summary>
    /// Put the shared anchor at the placer's feet and share it with the room. Room owner only, so
    /// two people cannot place two anchors.
    ///
    /// The anchor only has to be a stable point every headset agrees on. It does NOT have to be
    /// where the board is — the board is placed afterwards with both hands (WorldGrab), and that
    /// placement is networked, so neither the anchor's position nor its yaw needs to mean anything.
    /// Feet, not table: nothing to aim at and nothing to measure. Stand anywhere, press the key.
    /// </summary>
    public void RequestPlaceAnchor()
    {
        if (!runtimeAvailable)
        {
            Debug.Log("BoardAnchor: no Meta runtime, so there is nothing to anchor to.");
            return;
        }

        if (RoomAnchor.Instance == null)
        {
            Debug.LogWarning("BoardAnchor: no session yet, so an anchor cannot be shared.");
            return;
        }

        if (!LocalPlayerIsRoomOwner())
        {
            Debug.Log("BoardAnchor: only the room owner places the anchor.");
            return;
        }

        if (placing)
        {
            Debug.Log("BoardAnchor: already placing an anchor.");
            return;
        }

        PlaceAnchorAsync();
    }

    private static bool LocalPlayerIsRoomOwner()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
        {
            return false;
        }

        PlayerControls me = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        return me != null && me.GetIsRoomOwner();
    }

    private async void PlaceAnchorAsync()
    {
        placing = true;

        try
        {
            CameraController2 currentRig = Rig;
            Transform head = Camera.main != null ? Camera.main.transform : null;
            if (currentRig == null || head == null)
            {
                Debug.LogWarning("BoardAnchor: no rig or no camera, so the anchor cannot be placed.");
                return;
            }

            Vector3 anchorPos = new Vector3(head.position.x, currentRig.transform.position.y,
                                            head.position.z);
            Quaternion anchorRot = Quaternion.Euler(0f, head.eulerAngles.y, 0f);

            GameObject go = new GameObject(AnchorObjectName);

            // Pose BEFORE the component. OVRSpatialAnchor.Start() calls CreateSpatialAnchor(),
            // which captures the tracking-space pose at that moment; this order closes the window
            // rather than narrowing it.
            go.transform.SetPositionAndRotation(anchorPos, anchorRot);
            DontDestroyOnLoad(go);
            OVRSpatialAnchor anchor = go.AddComponent<OVRSpatialAnchor>();

            // Order matters throughout: create -> localize -> save -> share. The SDK is explicit
            // that anchors must exist, be localized and be saved before sharing; skipping the save
            // is the most common way for ShareAsync to fail with something unhelpful.
            if (!await anchor.WhenLocalizedAsync())
            {
                Debug.LogError("BoardAnchor: the new anchor never localized. Nothing was shared.");
                Destroy(go);
                return;
            }

            var saved = await anchor.SaveAnchorAsync();
            if (!saved.Success)
            {
                Debug.LogError("BoardAnchor: could not save the anchor (" + saved.Status +
                               "). Nothing was shared.");
                Destroy(go);
                return;
            }

            Guid group = Guid.NewGuid();
            var shared = await OVRSpatialAnchor.ShareAsync(new[] { anchor }, group);
            if (!shared.Success)
            {
                // FailureCloudStorageDisabled here means Share Point Cloud Data is off in the OS:
                // Settings > Privacy and Safety > Device Permissions.
                Debug.LogError("BoardAnchor: could not share the anchor (" + shared.Status +
                               "). Check Share Point Cloud Data in the headset's privacy settings.");
                Destroy(go);
                return;
            }

            if (RoomAnchor.Instance == null)
            {
                Debug.LogWarning("BoardAnchor: the session went away while the anchor was being " +
                                 "shared. Nothing was published.");
                Destroy(go);
                return;
            }

            string groupText = group.ToString();
            string uuidText = anchor.Uuid.ToString();

            publishedUuid = uuidText;
            publishWaitUntil = Time.realtimeSinceStartup + PublishEchoTimeoutSeconds;
            RoomAnchor.Instance.PublishAnchorServerRpc(groupText, uuidText);

            // Adopt locally rather than waiting for our own publish to come back, and record it as
            // desired so the poll below does not immediately try to re-download it.
            desiredGroup = groupText;
            desiredUuid = uuidText;
            loadedUuid = uuidText;
            AdoptAnchor(go, anchor, uuidText);

            Debug.Log("BoardAnchor: room anchor placed and shared (" + uuidText + ").");
        }
        finally
        {
            placing = false;
        }
    }

    // ---------------------------------------------------------------- loading

    private void PollRoomAnchor()
    {
        RoomAnchor room = RoomAnchor.Instance;
        if (room == null)
        {
            return;
        }

        string group = room.anchorGroup.Value.ToString();
        string uuid = room.anchorUuid.Value.ToString();

        // We have just placed an anchor and published it. Until the server echoes it back, what is
        // on RoomAnchor is still the PREVIOUS anchor, and following it would tear down the one we
        // just placed and re-download the one we replaced. The timeout is there so a publish that
        // never lands cannot leave this client ignoring the room for the rest of the session.
        if (!string.IsNullOrEmpty(publishedUuid))
        {
            if (uuid == publishedUuid)
            {
                publishedUuid = "";
            }
            else if (Time.realtimeSinceStartup < publishWaitUntil)
            {
                return;
            }
            else
            {
                publishedUuid = "";
                Debug.LogWarning("BoardAnchor: the anchor this headset published never came back " +
                                 "from the server. Following whatever the room says instead.");
            }
        }

        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(uuid))
        {
            return;
        }

        if (uuid == desiredUuid && group == desiredGroup)
        {
            return;
        }

        // Polling rather than subscribing to OnValueChanged: RoomAnchor despawns and respawns on a
        // reconnect (NetworkReconnectHandler shuts the client down and restarts it), and a
        // subscription would have to be torn down and rebuilt around that. A comparison every frame
        // costs nothing and cannot miss an edge.
        desiredGroup = group;
        desiredUuid = uuid;
        RequestLoad(group, uuid);
    }

    private void RequestLoad(string group, string uuid)
    {
        // Latch, never drop. Anything that arrives while a load is in flight is remembered and
        // picked up when it finishes.
        pendingGroup = group;
        pendingUuid = uuid;
        hasPending = true;

        if (!loading)
        {
            PumpLoadsAsync();
        }
    }

    private async void PumpLoadsAsync()
    {
        loading = true;

        try
        {
            while (hasPending)
            {
                string group = pendingGroup;
                string uuid = pendingUuid;
                hasPending = false;

                // A repeated request during a successful load must not restart it.
                if (uuid == loadedUuid && boundAnchor != null)
                {
                    continue;
                }

                if (await LoadAndAdoptAsync(group, uuid))
                {
                    loadedUuid = uuid;
                }
            }
        }
        finally
        {
            loading = false;
        }
    }

    private async Task<bool> LoadAndAdoptAsync(string groupText, string uuidText)
    {
        if (!Guid.TryParse(groupText, out Guid group) || !Guid.TryParse(uuidText, out Guid uuid))
        {
            Debug.LogError("BoardAnchor: the room published an anchor id that is not a GUID.");
            return false;
        }

        List<OVRSpatialAnchor.UnboundAnchor> unbound = new List<OVRSpatialAnchor.UnboundAnchor>();

        for (int attempt = 1; attempt <= Mathf.Max(1, LoadAttempts); attempt++)
        {
            // The three-argument overload filters by UUID inside the query. Ask for the one anchor
            // the room owner published rather than loading the group and picking — a client on the
            // wrong anchor looks like working software, and looking like working software is worse
            // than failing, because a client that fails to align still gets a ring slot and behaves
            // correctly as a remote player.
            var result = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
                group, new[] { uuid }, unbound);

            if (this == null)
            {
                return false;                    // torn down mid-await
            }

            if (!result.Success || unbound.Count == 0)
            {
                Debug.LogWarning("BoardAnchor: could not download the room anchor (attempt " +
                                 attempt + " of " + LoadAttempts + ", " + result.Status + ").");
            }
            else if (await unbound[0].LocalizeAsync(LocalizeTimeoutSeconds))
            {
                if (this == null)
                {
                    return false;
                }

                GameObject go = new GameObject(AnchorObjectName);
                DontDestroyOnLoad(go);
                OVRSpatialAnchor anchor = go.AddComponent<OVRSpatialAnchor>();
                unbound[0].BindTo(anchor);
                AdoptAnchor(go, anchor, uuidText);
                Debug.Log("BoardAnchor: bound to the room anchor (" + uuidText + ").");
                return true;
            }
            else
            {
                if (this == null)
                {
                    return false;
                }
                Debug.LogWarning("BoardAnchor: the room anchor did not localize within " +
                                 LocalizeTimeoutSeconds + " s (attempt " + attempt + " of " +
                                 LoadAttempts + "). Walk around the room and look at it.");
            }

            // A request that came in while this attempt was running supersedes the retries.
            if (hasPending)
            {
                return false;
            }

            if (attempt < LoadAttempts)
            {
                await Task.Delay(1000 * attempt);
                if (this == null)
                {
                    return false;
                }
            }
        }

        Debug.LogError("BoardAnchor: gave up on the room anchor. This headset is not colocated — " +
                       "it still plays, as a player in a different room. Press A to try again.");
        return false;
    }

    private void AdoptAnchor(GameObject go, OVRSpatialAnchor anchor, string uuid)
    {
        if (anchorObject != null && anchorObject != go)
        {
            Destroy(anchorObject);
        }

        anchorObject = go;
        boundAnchor = anchor;
        boundUuid = uuid;
        untrackedSince = -1f;
        untrackedReported = false;

        // Never report alignment you did not verify. If it is not tracked yet, say so and let
        // HoldAlignment pick it up on the frame it becomes tracked — which is only safe because
        // alignment is continuous rather than a one-shot.
        if (!anchor.IsTracked)
        {
            Debug.Log("BoardAnchor: anchor adopted but not tracked yet. Alignment starts as soon " +
                      "as the runtime can see it.");
        }
    }

    // ---------------------------------------------------------------- probe support

    /// <summary>True once this client is bound to the room's shared anchor.</summary>
    public bool HasAnchor => boundAnchor != null;

    /// <summary>True while the runtime can currently locate the bound anchor.</summary>
    public bool AnchorIsTracked => boundAnchor != null && boundAnchor.IsTracked;

    /// <summary>The anchor this client is actually bound to, empty if none.</summary>
    public string BoundUuid => boundAnchor != null ? boundUuid : "";
}
