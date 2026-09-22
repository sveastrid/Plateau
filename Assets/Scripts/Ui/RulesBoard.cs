using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The rules board: one world-space Canvas per player, present for as long as a game scene is
/// loaded, that the player can minimise and drag.
///
/// This replaces MenuControl.BuildRulesPanel, which built the same text as a wing off the room menu
/// and destroyed it with the menu. The rules were therefore reachable only by knowing that X opens
/// a menu, and only while that menu was in front of the board — so a player dropped into a game by
/// somebody else's game switch had no way to find out what the game was.
///
/// Networked to nothing. Position, scroll offset and minimised state are this client's business
/// alone: no NetworkVariable, no prefab registration, no NetworkConfig field, so this change does
/// NOT move the join handshake's config hash and does not need both headsets reflashed.
///
/// Order 23: it moves a BoxCollider, so it has to run before PointerBeam (24) does its
/// Physics.SyncTransforms(). At any later order the beam tests against where the board was last
/// frame, which is visible as a lag exactly while you are dragging it. See
/// Assets/Scripts/CLAUDE.md, "Execution-order contract".
/// </summary>
[DefaultExecutionOrder(23)]
[RequireComponent(typeof(MenuControl))]
public class RulesBoard : MonoBehaviour
{
    /// <summary>
    /// Reserved key names, in the same sense as ScrollList.ScrollUpKey: a GameModule.menuActions
    /// entry must never use one, or the room menu's row would be swallowed by this board.
    /// </summary>
    public const string MoveKey = "RulesMove";
    public const string ToggleKey = "RulesToggle";
    public const string BodyKey = "RulesBody";

    [Header("Where it appears")]
    [Tooltip("Metres ahead of the eyes. The room menu opens at 1.3 and 0.7 to the LEFT, so this " +
             "sits nearer and to the right and the two never overlap.")]
    public float boardDistance = 1.1f;
    public float boardRightOffset = 0.5f;

    [Tooltip("Metres above the eyes, measured at the board's TOP edge — the pivot is top-centre. " +
             "Positive, so the expanded panel hangs down across the view rather than down across " +
             "the table.")]
    public float boardHeightOffset = 0.18f;

    [Header("Behaviour")]
    [Tooltip("Start as the small card. False brings the board up expanded; nothing else changes.")]
    public bool startMinimized = true;

    [Tooltip("Seconds after a scene load during which the board keeps re-placing itself in front " +
             "of the camera. CameraController2.PlaceAtRingSlot may teleport the rig to its ring " +
             "slot after the scene is active, and a board placed before that lands behind you.")]
    public float placeSettleSeconds = 0.75f;

    public float minDragDistance = 0.45f;
    public float maxDragDistance = 3f;

    [Tooltip("Metres per second the right stick pushes the board away while you are dragging it.")]
    public float dragPushSpeed = 0.8f;

    // UI units. 1 unit = 1 mm at the toolkit's localScale of 0.001 — see Assets/Scripts/Ui/Panel.cs.
    // HeaderHeight is 84 because that is the toolkit's row pitch; the card is exactly one row tall.
    const float Width = 720f;
    const float ExpandedHeight = 700f;
    const float HeaderHeight = 84f;
    const float MinimizedWidth = 360f;
    const float ToggleWidth = 84f;
    const float ColliderDepth = 20f;

    MenuControl menu;
    InputReader inputs;
    Transform myCam;
    pointerControl beamKeys;

    GameObject root;
    RectTransform rootRt;
    Panel panel;
    GameObject body;
    RectTransform bodyRt;
    BoxCollider bodyBox;
    RectTransform gripRt;
    BoxCollider gripBox;
    RectTransform toggleRt;
    BoxCollider toggleBox;
    TMP_Text titleLabel;
    TMP_Text toggleLabel;
    TMP_Text prose;
    ScrollRect scroll;

    bool minimized;
    bool dragging;
    float dragDistance;
    float placeTimer;
    string gameLabel = "";
    bool warnedNoPointer;

    public bool IsMinimized => minimized;

    void Awake()
    {
        menu = GetComponent<MenuControl>();
        minimized = startMinimized;
    }

    /// <summary>
    /// Same shape as MenuControl.Start, and for the same reason: this component is on Menu Manager,
    /// which is part of PersistentRig and therefore DontDestroyOnLoad, so Start() runs exactly once
    /// for the life of the app — in OpeningScene. Everything per-scene hangs off activeSceneChanged.
    /// </summary>
    void Start()
    {
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        Refresh();
    }

    void OnDestroy()
    {
        SceneManager.activeSceneChanged -= HandleActiveSceneChanged;

        if (panel != null)
        {
            panel.KeyPressed -= HandleKey;
        }

        // The Canvas is its own DontDestroyOnLoad root, so nothing else would ever take it down.
        if (root != null)
        {
            Destroy(root);
        }
    }

    void HandleActiveSceneChanged(Scene from, Scene to)
    {
        Refresh();
    }

    // ------------------------------------------------------------------ per-scene state

    /// <summary>Build if needed, retarget at the loaded game, show or hide, and re-place.</summary>
    void Refresh()
    {
        if (!EnsureBuilt())
        {
            return;
        }

        ApplyModule();
        ApplyState();
        ApplyVisibility();

        if (root.activeSelf)
        {
            placeTimer = placeSettleSeconds;
        }
    }

    /// <summary>
    /// The loaded game's rules text and label. Read through GameCatalog.ActiveModule, never through
    /// a scene-name if/else: the lookup BuildRulesPanel replaced fell through to BASH's rules, so a
    /// game that was not one of the three silently showed somebody else's.
    /// </summary>
    void ApplyModule()
    {
        GameModule module = GameCatalog.ActiveModule;
        gameLabel = module != null ? module.MenuLabel : "";

        if (prose != null)
        {
            if (module != null && module.rulesText != null)
            {
                prose.SetText(module.rulesText.text);
            }
            else if (module != null)
            {
                prose.SetText("No rules asset is set on " + module.name + ".");
            }
            else
            {
                prose.SetText("");
            }
        }

        if (scroll != null)
        {
            scroll.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    /// A game scene only. The lobby has its own two panels and no GameModule to read rules from,
    /// and the legacy GameScene is not in GameRoutes either — same gate MenuControl's X uses.
    /// </summary>
    void ApplyVisibility()
    {
        if (root == null)
        {
            return;
        }

        bool inGame = GameRoutes.IsGameScene(SceneManager.GetActiveScene().name);
        root.SetActive(inGame);

        if (!inGame)
        {
            dragging = false;
        }
    }

    /// <summary>
    /// Minimised and expanded differ in the root's size and in whether the body exists. The root's
    /// pivot is top-centre, so shrinking the height leaves the title bar exactly where it was and
    /// the panel folds up into it rather than jumping.
    /// </summary>
    void ApplyState()
    {
        if (root == null)
        {
            return;
        }

        rootRt.sizeDelta = new Vector2(minimized ? MinimizedWidth : Width,
                                       minimized ? HeaderHeight : ExpandedHeight);

        if (body != null)
        {
            body.SetActive(!minimized);
        }

        if (toggleLabel != null)
        {
            toggleLabel.SetText(minimized ? "+" : "–");
        }

        if (titleLabel != null)
        {
            titleLabel.SetText(minimized || string.IsNullOrEmpty(gameLabel)
                                   ? "Rules"
                                   : "Rules — " + gameLabel);
        }

        // A BoxCollider does not track its RectTransform — the same fact ScrollList.EnsureSlots
        // exists for. ForceUpdateCanvases flushes the size change into the child rects first, or
        // the colliders are sized from the state we have just left.
        Canvas.ForceUpdateCanvases();
        SyncCollider(gripRt, gripBox);
        SyncCollider(toggleRt, toggleBox);
        SyncCollider(bodyRt, bodyBox);
    }

    // ------------------------------------------------------------------ public controls

    /// <summary>Expand the board and put it back in front of the player. The room menu's Rules row.</summary>
    public void Recall()
    {
        if (!EnsureBuilt())
        {
            return;
        }

        minimized = false;
        ApplyState();
        ApplyVisibility();

        if (root.activeSelf)
        {
            PlaceInFrontOfCamera();
            placeTimer = 0f;
        }
    }

    public void SetMinimized(bool value)
    {
        minimized = value;
        ApplyState();
    }

    // ------------------------------------------------------------------ per frame

    void Update()
    {
        if (root == null)
        {
            Refresh();
            return;
        }

        if (!root.activeSelf || inputs == null || myCam == null)
        {
            return;
        }

        if (placeTimer > 0f && !dragging)
        {
            placeTimer -= Time.deltaTime;
            PlaceInFrontOfCamera();
        }

        UpdateDrag();
    }

    void UpdateDrag()
    {
        if (beamKeys == null)
        {
            return;
        }

        if (!dragging)
        {
            if (inputs.RightMainTriggerDown && BeamIsOn(MoveKey))
            {
                dragging = true;
                placeTimer = 0f;
                dragDistance = Mathf.Clamp(Vector3.Distance(BeamOrigin(), root.transform.position),
                                           minDragDistance, maxDragDistance);
            }
            return;
        }

        // Read the level, not the Up edge: a drag is a held gesture, and an edge missed in a frame
        // where the panel was inactive would leave the board welded to the pointer.
        if (!inputs.RightMainTrigger)
        {
            dragging = false;
            return;
        }

        // The stick pushes the board away and pulls it back. No conflict with the text scroll: that
        // is hover-gated to the body (§2) and while you are dragging the beam is on the title bar.
        float push = inputs.rightJoystick.y;
        if (Mathf.Abs(push) > 0.2f)
        {
            dragDistance = Mathf.Clamp(dragDistance + push * dragPushSpeed * Time.deltaTime,
                                       minDragDistance, maxDragDistance);
        }

        root.transform.position = BeamOrigin() + BeamDirection() * dragDistance;
        FacePlayer();
    }

    void PlaceInFrontOfCamera()
    {
        Vector3 flat = myCam.forward;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, flat);

        root.transform.position = myCam.position
                                  + flat * boardDistance
                                  + rightAxis * boardRightOffset
                                  + Vector3.up * boardHeightOffset;
        FacePlayer();
    }

    /// <summary>
    /// A world-space Canvas is read from its -Z side: the reader looks ALONG the text's own
    /// forward. So forward points AWAY from the camera, which both un-mirrors the glyphs and turns
    /// a board dragged off to one side back in towards the player, with no hand-tuned yaw term.
    /// See docs/UIBugFixes.md §1 and PlayerControls.cs (the nametags), which is the same rule.
    ///
    /// Flattened, so a board dragged while looking down at the table does not come up tipped.
    /// </summary>
    void FacePlayer()
    {
        Vector3 away = root.transform.position - myCam.position;
        away.y = 0f;

        if (away.sqrMagnitude < 0.0001f)
        {
            return;
        }

        root.transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
    }

    bool BeamIsOn(string keyName)
    {
        keyInfo key = beamKeys.currentKey;
        return key != null && key.keyName == keyName && key.transform.IsChildOf(root.transform);
    }

    /// <summary>
    /// The beam's near end, not the hand: the beam is drawn below and offset from the hand, and a
    /// ray that does not start where the beam starts drags the board somewhere the player is not
    /// pointing. These two lines are PointerBeam.cs:65-66 rather than PointerBeam.Origin/Direction,
    /// because PointerBeam publishes those at order 24 and this runs at 23 — the published value
    /// would be one frame stale exactly while you are dragging.
    /// </summary>
    Vector3 BeamOrigin()
    {
        Transform p = menu != null ? menu.pointer : null;
        return p != null ? p.TransformPoint(new Vector3(0f, -1f, 0f)) : myCam.position;
    }

    Vector3 BeamDirection()
    {
        Transform p = menu != null ? menu.pointer : null;
        return p != null ? p.up : myCam.forward;
    }

    // ------------------------------------------------------------------ presses

    /// <summary>
    /// Panel dispatches by keyName on trigger release, with sliding off the key cancelling it.
    ///
    /// MoveKey and BodyKey are pressable only so the beam stops on them — the drag reads the
    /// trigger itself (a press-and-hold is not a press), and the body is a key so that the joystick
    /// knows the rules are what it is meant to scroll. Their release does nothing on purpose.
    /// </summary>
    void HandleKey(string keyName)
    {
        if (keyName == ToggleKey)
        {
            SetMinimized(!minimized);
        }
    }

    // ------------------------------------------------------------------ construction

    bool Resolve()
    {
        if (menu == null)
        {
            menu = GetComponent<MenuControl>();
        }
        if (menu == null)
        {
            return false;
        }

        if (inputs == null)
        {
            inputs = menu.inputs;
        }
        if (myCam == null)
        {
            myCam = menu.myCam;
        }
        if (beamKeys == null && menu.pointer != null)
        {
            beamKeys = menu.pointer.GetComponent<pointerControl>();
        }

        return inputs != null && myCam != null;
    }

    bool EnsureBuilt()
    {
        if (root != null)
        {
            return true;
        }

        if (!Resolve())
        {
            return false;
        }

        if (beamKeys == null && !warnedNoPointer)
        {
            warnedNoPointer = true;
            Debug.LogWarning("RulesBoard: MenuControl.pointer has no pointerControl, so the rules " +
                             "board will draw but nothing on it can be pressed or dragged.");
        }

        root = new GameObject("Rules Board");

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        rootRt = root.GetComponent<RectTransform>();
        // Top-centre, so minimising folds the panel up into its own title bar instead of moving it.
        rootRt.pivot = new Vector2(0.5f, 1f);
        rootRt.sizeDelta = new Vector2(Width, ExpandedHeight);
        // 1 UI unit = 1 mm, the toolkit's one convention. See Assets/Scripts/Ui/Panel.cs.
        rootRt.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        // Its own root, so it survives LoadSceneMode.Single and keeps the position and the
        // minimised state the player chose across a game switch.
        DontDestroyOnLoad(root);

        BuildBacking();
        BuildHeader();
        BuildBody();

        panel = root.AddComponent<Panel>();
        panel.Bind(inputs, beamKeys);
        panel.KeyPressed += HandleKey;

        return true;
    }

    void BuildBacking()
    {
        GameObject backing = NewRect("Backing", root.transform);
        StretchFull(backing.GetComponent<RectTransform>());

        Image image = backing.AddComponent<Image>();
        // Dark, so the text is readable against whatever the room looks like through passthrough.
        image.color = new Color(0.04f, 0.05f, 0.07f, 0.88f);
        image.raycastTarget = false;
    }

    void BuildHeader()
    {
        GameObject header = NewRect("Header", root.transform);
        RectTransform headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 1f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.pivot = new Vector2(0.5f, 1f);
        headerRt.sizeDelta = new Vector2(0f, HeaderHeight);
        headerRt.anchoredPosition = Vector2.zero;

        // Anchored to the band's edges, never offset from its centre: the header is 720 units wide
        // expanded and 360 minimised, and a centre-relative layout would put both widgets off the
        // card in one of the two states. Same lesson as docs/UIBugFixes.md §2.
        GameObject grip = NewRect("Grip", header.transform);
        gripRt = grip.GetComponent<RectTransform>();
        gripRt.anchorMin = Vector2.zero;
        gripRt.anchorMax = Vector2.one;
        gripRt.offsetMin = Vector2.zero;
        gripRt.offsetMax = new Vector2(-ToggleWidth, 0f);

        Image gripImage = grip.AddComponent<Image>();
        titleLabel = MakeLabel(grip.transform, "Title", 32f, TextAlignmentOptions.Left,
                               new Vector4(24f, 0f, 12f, 0f));
        gripBox = MakeKey(grip, MoveKey, gripImage, null);

        GameObject toggle = NewRect("Toggle", header.transform);
        toggleRt = toggle.GetComponent<RectTransform>();
        toggleRt.anchorMin = new Vector2(1f, 0f);
        toggleRt.anchorMax = new Vector2(1f, 1f);
        toggleRt.pivot = new Vector2(1f, 0.5f);
        toggleRt.sizeDelta = new Vector2(ToggleWidth, 0f);
        toggleRt.anchoredPosition = Vector2.zero;

        Image toggleImage = toggle.AddComponent<Image>();
        toggleLabel = MakeLabel(toggle.transform, "Label", 40f, TextAlignmentOptions.Center,
                                Vector4.zero);
        toggleBox = MakeKey(toggle, ToggleKey, toggleImage, null);
    }

    void BuildBody()
    {
        body = NewRect("Body", root.transform);
        bodyRt = body.GetComponent<RectTransform>();
        bodyRt.anchorMin = Vector2.zero;
        bodyRt.anchorMax = Vector2.one;
        bodyRt.offsetMin = Vector2.zero;
        bodyRt.offsetMax = new Vector2(0f, -HeaderHeight);

        GameObject viewport = NewRect("Viewport", body.transform);
        RectTransform viewportRt = viewport.GetComponent<RectTransform>();
        StretchFull(viewportRt);
        viewport.AddComponent<RectMask2D>();

        GameObject content = NewRect("Content", viewport.transform);
        RectTransform contentRt = content.GetComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.sizeDelta = new Vector2(0f, 2000f);
        contentRt.anchoredPosition = Vector2.zero;

        prose = content.AddComponent<TextMeshProUGUI>();
        prose.fontSize = 24f;
        prose.color = Color.white;
        prose.alignment = TextAlignmentOptions.TopLeft;
        prose.margin = new Vector4(24f, 16f, 24f, 16f);
        prose.raycastTarget = false;

        ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll = body.AddComponent<ScrollRect>();
        scroll.content = contentRt;
        scroll.viewport = viewportRt;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 15f;

        // On the Body and not on the root, so "the beam is over the text" is the transform test
        // itself — see §2. The body is deactivated when minimised, which stops the scroller with it.
        ScrollTextWithJoystick scroller = body.AddComponent<ScrollTextWithJoystick>();
        scroller.scrollRect = scroll;
        scroller.inputs = inputs;
        scroller.pointer = beamKeys;
        scroller.joystickNeedsHover = true;
        scroller.scrollSpeed = 1.5f;

        // A key with no Graphic is legal and still presses (keyInfo). The body is one so that the
        // beam stops on it, the joystick knows the rules are what it should scroll, and the panel
        // occludes the game board behind it rather than letting a press through to a cell.
        bodyBox = MakeKey(body, BodyKey, null, null);
    }

    /// <summary>
    /// The project's one pressable-widget recipe: tag "key" + a kinematic Rigidbody (Unity trigger
    /// callbacks need one on the other collider) + a solid BoxCollider + keyInfo. The same four
    /// things PanelRow carries, added in code because this Canvas is built in code.
    ///
    /// The collider is deliberately NOT a trigger. PointerBeam raycasts with
    /// QueryTriggerInteraction.Ignore, so a solid collider is what stops the beam at the board
    /// instead of letting it target the game board through it.
    /// </summary>
    static BoxCollider MakeKey(GameObject go, string keyName, Graphic tint, TMP_Text label)
    {
        go.tag = "key";

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = false;

        keyInfo key = go.AddComponent<keyInfo>();
        key.keyName = keyName;
        // Growing a 636-unit title bar by 20% pushes 64 units off each side. Rows have the same
        // problem and solve it the same way — the tint is the feedback.
        key.growOnPress = false;
        // keyInfo.Start() rewrites keyLabel with keyName unless this is set, and ApplyState writes
        // these labels itself.
        key.overrideNameChange = true;
        key.keyLabel = label;
        key.targetGraphic = tint;
        key.offColor = new Color(0.13f, 0.15f, 0.19f, 0.92f);
        key.onColor = new Color(0.16f, 0.42f, 0.62f, 0.98f);

        return box;
    }

    static void SyncCollider(RectTransform rt, BoxCollider box)
    {
        if (rt == null || box == null)
        {
            return;
        }

        Rect r = rt.rect;
        box.size = new Vector3(Mathf.Max(1f, r.width), Mathf.Max(1f, r.height), ColliderDepth);
        // rect is pivot-relative, so its centre is the offset the collider needs.
        box.center = new Vector3(r.center.x, r.center.y, 0f);
    }

    static GameObject NewRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        if (go.GetComponent<RectTransform>() == null)
        {
            go.AddComponent<RectTransform>();
        }

        return go;
    }

    static void StretchFull(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static TMP_Text MakeLabel(Transform parent, string name, float size,
                              TextAlignmentOptions align, Vector4 margin)
    {
        GameObject go = NewRect(name, parent);
        StretchFull(go.GetComponent<RectTransform>());

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.color = new Color(0.94f, 0.96f, 1f, 1f);
        text.alignment = align;
        text.margin = margin;
        text.raycastTarget = false;

        return text;
    }
}
