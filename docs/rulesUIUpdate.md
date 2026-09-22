# rulesUIUpdate.md — the rules stop being a thing you have to know about

**Status: applied, §1–§4 and §6.** Written as small, mechanical steps. Every step says which file to
touch and gives the text to write; no step asks you to design anything.

The code went in exactly as written: `Assets/Scripts/Ui/RulesBoard.cs` is new, `ScrollTextWithJoystick`
gained the hover gate, `MenuControl` lost `BuildRulesPanel` and the `showRules` parameter and gained
the `Rules` row, and `StairsSelection.ResolveHit` refuses a collider tagged `key`. All four
assemblies compile headlessly with no errors and only the pre-existing `CS0618` warnings. **§5's
Editor pass and §9's optional `PersistentRig.prefab` authoring have not been done** — `MenuControl`
adds the component at runtime, so the board works, but its fields cannot be tuned in the Inspector
until somebody adds it to the prefab by hand.

## What is wrong today

The rules for a game exist, they are good, and almost nobody will ever see them.

`MenuControl.BuildRulesPanel` (`Assets/Scripts/MenuControl.cs:499-590`) builds a world-space Canvas
holding `GameModule.rulesText`, parents it to the room menu, and `CloseMenu` destroys it along with
the menu. So the rules are reachable only by:

1. knowing that `X` opens the room menu at all, and
2. keeping the menu open — which also puts a 1.2 m panel in front of the board — while you read.

A player who has just been dropped into `StairsGame` by somebody else's game switch has no way to
find out what Stairs is. That is the whole defect.

## What replaces it

A **rules board**: one world-space Canvas per player, present the whole time a game scene is
loaded, that

- **starts minimized** — a 36 cm × 8.4 cm card reading `Rules`, with a `+` — placed in the player's
  view when the scene loads, so it is impossible to miss and impossible to be in the way of;
- **expands** to a 72 cm × 70 cm scrolling panel when the `+` is pressed, and collapses again on
  `–`;
- **moves**: hold the right trigger on its title bar and the board follows the laser pointer; the
  right stick pushes it nearer and further while held;
- **is per-player and networked to nothing.** Nobody else sees your rules board, its position is
  not replicated, and no `NetworkVariable`, prefab or config hash moves. Two headsets do **not**
  need reflashing for this change.

The menu wing goes away — two rules surfaces would be one too many — and the room menu gains a
`Rules` row that brings the board back in front of you if you have pushed it somewhere you can no
longer reach.

**Two things have to change for a panel to be safe to leave on screen**, and both are in here
because neither is optional: the right stick has to stop scrolling the rules when it is aiming
something (§2), and Stairs has to stop resolving a board cell through the panel (§3h). Skip either
and this change makes two of the three games worse.

---

## §0 — Decisions taken, and the one that was ambiguous

**"Start out small, but easy to see."** These pull opposite ways, and the reading taken is: the
board starts **minimized**, and the minimized card carries the discoverability instead of the size.
It is 36 cm wide at 1.1 m (about 18° of view), it is placed at eye level + 18 cm slightly to the
right of centre every time a game scene loads, and it says the word `Rules`. That is a large, named,
permanently present target — which is more discoverable than a big panel the player dismisses once
and never sees again.

If that reading is wrong, it is **one field**: `RulesBoard.startMinimized`, defaulting to `true`.
Set it `false` and the board comes up expanded. Nothing else changes.

Three smaller decisions, so nobody re-litigates them later:

| Decision | Why |
| --- | --- |
| Built in **code**, not from a prefab | This is what `BuildRulesPanel` already did, and it keeps the whole change to three `.cs` files — no prefab surgery, no Inspector wiring, no scene edit, and the whole thing is headlessly compile-checkable. §9 has the optional prefab follow-up. |
| Reuses **`Panel`** for the press model | `Panel` with no header, no status and no lists is a legal `Panel` — its `lists` default to an empty array and every setter null-guards. So the board gets "pressed on trigger down, acted on trigger up, sliding off cancels" for free, and cannot drift from the lobby and the room menu on what a press means. |
| Colliders are **solid, not triggers** | `PointerBeam` raycasts with `QueryTriggerInteraction.Ignore`, so a solid collider is what makes the rules board **occlude the game board behind it**. A trigger would let the beam straight through and make every press on the rules also a press on whatever is behind it. Chasms and BASH are then safe for free, because both resolve a target by looking for a component or a tag on the collider that was hit and the rules Canvas has neither. **Stairs is not** — see §3h. |

**Rejected: the grab system.** `GrabControl` and the `Grabbable` tag exist and nothing uses them.
Grabbing the board with a grip would collide with `WorldGrab` (both grips) and with Chasms' spawn
menu (left grip alone), and would need a new tag on a runtime-built object. The trigger-on-the-
title-bar drag needs nothing that does not already work.

---

## §1 — Create `Assets/Scripts/Ui/RulesBoard.cs`

New file. Nothing else in the project is touched by this step, so it can be done and compiled on its
own. Write it exactly as below.

```csharp
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
```

**Two things in that file will look wrong and are not.**

`Panel` is added with no header, no status, no detail and no lists. That is legal — `Panel.lists`
initialises to `new ScrollList[0]`, `Bind` iterates it zero times, and every setter null-guards. It
is there purely as the press model, which is the point: the board cannot drift from the lobby's
panels on what a press means.

The `Image` is added to `Grip` and `Toggle` **before** `MakeKey`. `keyInfo.Awake` fills
`targetGraphic` from `GetComponent<Graphic>()` when it is left empty, and `AddComponent` runs
`Awake` immediately — so adding `keyInfo` first would leave it with nothing to tint. `MakeKey` also
assigns `targetGraphic` explicitly, so it is right either way; keep the order anyway, because the
next person to add a widget here will copy it.

---

## §2 — Gate `ScrollTextWithJoystick` on hover

**File: `Assets/Scripts/ScrollTextWithJoystick.cs`.** This is the one change that is a
*correctness* fix and not a feature, so do it in the same commit.

Today the scroller reads `rightJoystick.y` unconditionally. That is safe only because the rules wing
exists for as long as the room menu is open and no longer. An always-present board would mean that
in **Chasms** the stick that sets how many pieces to move also scrolls the rules, and in **BASH** the
stick that aims the artillery arc also scrolls the rules — every time, all game.

`ScrollList` already solved exactly this (`joystickNeedsHover`). Copy it.

Replace the whole file with:

```csharp
using UnityEngine;
using UnityEngine.UI;

public class ScrollTextWithJoystick : MonoBehaviour
{
    public InputReader inputs;
    public ScrollRect scrollRect;
    public float scrollSpeed = 2f;

    [Tooltip("The beam, so the stick only scrolls the text it is resting on. Assigned by " +
             "RulesBoard alongside inputs, the way Panel.Bind pushes it into a ScrollList.")]
    public pointerControl pointer;

    [Tooltip("Only scroll while the beam is on this object or a child of it. The rules board is " +
             "present for the whole game now, and rightJoystick.y is also Chasms' move count and " +
             "BASH's artillery aim — without this, aiming a shot scrolls the rules.")]
    public bool joystickNeedsHover = true;

    void Update()
    {
        if (inputs == null || scrollRect == null) return;

        if (joystickNeedsHover && !PointerIsOverMe()) return;

        // Right joystick only. There used to be a fallback to the left stick whenever the right one
        // was centred — and the left stick is locomotion and snap-turn (CameraController2), so for
        // a player who is not colocated, walking forward also scrolled the rules. The right stick
        // is the documented control; see Assets/Scripts/CLAUDE.md, Input.
        float scrollInput = inputs.rightJoystick.y;

        if (Mathf.Abs(scrollInput) > 0.1f)
        {
            // The ScrollRect verticalNormalizedPosition goes from 0 (bottom) to 1 (top)
            // If we push joystick up (positive), we want to read further down, which means moving the position towards 0
            float newPos = scrollRect.verticalNormalizedPosition - scrollInput * scrollSpeed * Time.deltaTime;
            scrollRect.verticalNormalizedPosition = Mathf.Clamp01(newPos);
        }
    }

    /// <summary>
    /// Transform.IsChildOf is true for the transform itself, and RulesBoard puts this component on
    /// the same object as the body's own "RulesBody" key — so resting the beam anywhere on the
    /// rules text is the hover.
    /// </summary>
    private bool PointerIsOverMe()
    {
        if (pointer == null || pointer.currentKey == null)
        {
            return false;
        }

        return pointer.currentKey.transform.IsChildOf(transform);
    }
}
```

`joystickNeedsHover` defaults to **`true`** rather than `false`, because after §3 `RulesBoard` is the
only thing in the project that creates one of these and no scene or prefab carries the component —
verified by grepping `Assets/` for `ScrollTextWithJoystick`, which hits only the `.cs` file and the
docs. If that ever stops being true, the default is the thing to revisit.

---

## §3 — Wire it in

**Files: `Assets/Scripts/MenuControl.cs`** (3a–3g) **and `Assets/Scripts/Stairs/StairsSelection.cs`**
(3h). Eight edits, all small, in this order.

### 3a — stop asking for the wing

`MenuControl.cs:171`:

```csharp
            OpenMenu1(true);
```

becomes

```csharp
            OpenMenu1();
```

### 3b — drop the parameter

`MenuControl.cs:262`:

```csharp
    public void OpenMenu1(bool showRules = false)
```

becomes

```csharp
    public void OpenMenu1()
```

`Update` at 3a is the only caller — nothing in a scene or prefab invokes it, so no wiring breaks.

### 3c — delete the call site

`MenuControl.cs:305-308`. Delete these four lines entirely:

```csharp
        if (showRules)
        {
            BuildRulesPanel(currentMenu);
        }
```

### 3d — delete the builder

Delete everything from the banner comment at `MenuControl.cs:490`

```csharp
    // ------------------------------------------------------------------ the rules panel
```

down to and including the closing brace of `BuildRulesPanel` at `MenuControl.cs:590`. That is the
whole section — about a hundred lines — and nothing else references it.

While you are in the file, `MenuControl.cs:278-279` has a comment ending
`"...and the rules wing beside it is placed off a flattened axis (BuildRulesPanel), so the two ended
up in different planes."` Change the parenthetical to `(RulesBoard.FacePlayer)`, since that is where
the rule lives now.

### 3e — own the board, and add the `Rules` key

Add the constant beside the other three (`MenuControl.cs:59-61`):

```csharp
    private const string RulesKey = "Rules";
```

Add the field beside `currentPanel` (`MenuControl.cs:63-64`):

```csharp
    private RulesBoard rules;
```

Extend `Start()` (`MenuControl.cs:81-85`) to:

```csharp
    void Start()
    {
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        AdoptSceneDefaults();
        EnsureRulesBoard();
    }

    /// <summary>
    /// The rules board lives on this object because everything it needs — the Input Reader, the
    /// camera and the pointer — is already resolved here, and because Menu Manager is part of
    /// PersistentRig, so it survives the scene switches the board has to follow.
    ///
    /// Added at runtime when it is not authored, the way PassthroughController.EnsureOvrComponents
    /// adds a missing OVRManager: authoring it on PersistentRig.prefab is preferred and is the only
    /// way to tune its fields in the Inspector, but nothing breaks if that has not been done.
    /// </summary>
    private RulesBoard EnsureRulesBoard()
    {
        if (rules == null)
        {
            rules = GetComponent<RulesBoard>();
        }
        if (rules == null)
        {
            rules = gameObject.AddComponent<RulesBoard>();
        }
        return rules;
    }
```

Add a case to `HandleKey`, after the `PlaceAnchorKey` case (`MenuControl.cs:207-221`):

```csharp
            case RulesKey:
                // Not a game's key even though it shows a game's text: the board is a fixture of
                // the room like the pointer, and the text it shows comes from GameCatalog.
                EnsureRulesBoard().Recall();
                CloseMenu();
                return;
```

### 3f — add the row

In `BuildActionRows`, after the `Passthrough` entry in the `rows` initialiser
(`MenuControl.cs:447-457`), add:

```csharp
        rows.Add(new RowData(RulesKey, RulesKey, "")
        {
            subtitle = "Bring the rules back in front of you",
        });
```

Put it immediately after the `List<RowData> rows = new List<RowData> { ... };` block and before the
`if (owner)` block that adds `Voice Chat`.

The action list has three slots and arrows, so a fourth room row simply scrolls — that is what
`UIBugFixes.md` §3 put the arrows there for.

### 3g — the class comment

`MenuControl`'s summary lists three things the panel conversion bought. Add a fourth bullet after
them:

```csharp
///  - **The rules left the menu entirely.** BuildRulesPanel built the rules as a wing off this
///    panel and CloseMenu destroyed it, so the rules were reachable only by knowing X exists and
///    only while the menu was in front of the board. They are now RulesBoard, present for the whole
///    game; the Rules row here only brings it back when it has been dragged out of reach.
```

### 3h — stop Stairs resolving a cell through the rules board

**File: `Assets/Scripts/Stairs/StairsSelection.cs`.** This is a correctness fix, not polish, and it
is the one place where an always-present panel breaks a game.

`StairsSelection.ResolveHit` (`StairsSelection.cs:194-212`) looks for a `StairsPieceTag` on the hit
collider's parents and, finding none, **maps the hit point onto the grid**:

```csharp
        board.TryCellAt(board.Frame.InverseTransformPoint(beam.Hit.point), out cell);
```

`StairsBoard.TryCellAt` (`StairsBoard.cs:312-331`) rounds `framePoint.x` and `framePoint.z` to the
nearest lattice point and **ignores y entirely**. That is deliberate — it is what makes a bare square
a target at all, and `Assets/Scripts/Stairs/CLAUDE.md` states it outright: *"Bare squares are
targeted by where the ray landed, not by what it hit."*

The consequence for this change: a rules board floating anywhere above the board's footprint resolves
to whatever cell is underneath it. A player standing on the 2 m `PlayerRing` with the board at the
origin has the default placement — 1.1 m ahead, 0.5 m right — sitting about 0.9 m from the board
centre, i.e. **inside** the 1 m half-width of the 8×8 grid. So without this step, hovering the rules
lights up a cell and pressing `+` can place a pawn.

Chasms and BASH do not have this problem: `PlateauSelection` resolves through `PlateauTag` /
`PlateauPieceTag` components and `ControlListener` through the tags `gamepiece`, `obstacle`,
`island`, `base` and `line`. A hit on something carrying none of those is already nothing to them.

Insert one guard, between the null check and the `StairsPieceTag` lookup:

```csharp
        if (beam == null || !beam.HasHit || beam.Hit.collider == null)
        {
            return;
        }

        // A UI surface is not a board square. Cells are resolved from where the ray LANDED and not
        // from what it hit, and TryCellAt ignores y — so any collider between the player and the
        // board maps to whatever cell is under it. That was harmless while the only panels were the
        // room menu and its rules wing, both of which also set menu.IsOpen and stood Playable()
        // down; the rules board (RulesBoard) is present the whole game and does not.
        if (beam.Hit.collider.CompareTag("key"))
        {
            return;
        }

        piece = beam.Hit.collider.GetComponentInParent<StairsPieceTag>();
```

This is the only edit outside `MRBoardGame.Shared`, and it is a Stairs-specific weakness being fixed
in Stairs — not shared code reaching into a game. It also closes the same hole for the room menu
itself, which is opened 1.3 m ahead and can already sit over the board.

---

## §4 — Compile it

No Editor needed. From the repo root, following the recipe in the root `CLAUDE.md` under
**Build and test**:

- take `<DefineConstants>` and the `<HintPath>` references from `MRBoardGame.Shared.csproj`;
- glob `Assets/Scripts/**/*.cs` for the source list yourself — the `<Compile Include=>` list in the
  `.csproj` goes stale;
- write a csc response file and run Unity's **.NET** Roslyn:
  `Editor/Data/DotNetSdk/dotnet.exe Editor/Data/DotNetSdk/sdk/<ver>/Roslyn/bincore/csc.dll @rsp`.

**Pass an explicit `-out:` pointing outside the repo.** Without one, Roslyn names the assembly after
the first source file, and a list beginning with `BoardAnchor.cs` silently overwrites the tracked
`BoardAnchor.dll` at the repo root.

Expect the pre-existing `CS0618` warnings on `ServerRpcAttribute.RequireOwnership` and
`FindFirstObjectByType`. Anything else is yours.

Two assemblies change: `MRBoardGame.Shared` (§1, §2, §3a–3g) and `MRBoardGame.Stairs` (§3h, one
`if`). `MRBoardGame.Plateau` and `MRBoardGame.Bash` are not touched at all. A game assembly needs a
`-r:` on the Shared DLL you have just built. If Plateau or BASH fails to build after this, something
was put in the wrong folder.

Then, with the Editor open, trigger *Assets > Refresh*, wait for the domain reload and read the
console. **Filter on the text `error CS`, not on severity** — compiler messages arrive typed as
`Log`.

---

## §5 — Test it in the Editor, no headset

`InputReader` falls back to the keyboard per hand, so all of this is playable on a desk. Press Play
from `OpeningScene`, host a private room, and let it load `StairsGame`.

| | Expected |
| --- | --- |
| The scene loads | A small dark card reading **`Rules`** with a `+`, slightly right of centre and a little above eye level |
| Point at the `+` and press `.` (right trigger) | The card unfolds **downwards** — the title bar does not move — into a panel headed `Rules — Stairs` with the text of `Assets/Resources/stepsRules.txt` |
| Rest the beam on the text, arrow **up**/**down** | The rules scroll |
| Point at the board instead, arrow up/down | The rules do **not** scroll (this is §2 working) |
| Hold `.` on the title bar and move the mouse/hand | The board follows the beam and keeps facing you |
| While holding, arrow up/down | The board pushes away and pulls back, clamped between 0.45 m and 3 m |
| Press `–` | It folds back to the card, in place |
| Press `X`, pick a different game | The new game's rules are in the board, in the state and at roughly the position you left it |
| Drag it behind you, then `X` → `Rules` | It comes back in front of you, expanded |
| Leave the game for the lobby | The board is gone; no card in `OpeningScene` |

Two specific things to look at rather than glance past:

- **The glyphs must read left-to-right.** If the text is mirrored, `FacePlayer` has been written
  with an extra 180° — see `docs/UIBugFixes.md` §1, and do not put it back.
- **Point the beam at the game board *through* the rules panel.** Nothing on the board should
  highlight, and the trigger should not place or select anything. Two different things make that
  true and they fail differently: if the beam reaches the board at all, `isTrigger` got set to
  `true` in `MakeKey`; if the beam stops at the panel but a **Stairs cell still lights up green or
  white underneath it**, §3h was not applied.
- **Drag the board so it hangs directly over the middle of the Stairs grid and press `+` and `–` a
  few times.** No pawn should be placed and no tile built. This is the §3h case at its worst.

In Chasms, check the stick still sets the move count with the beam on a piece, and in BASH that it
still aims the arc. Both are §2.

**No two-headset session is needed for this change.** Nothing here is networked, no prefab is
registered, and `NetworkConfig` does not move — so the join handshake's config hash is unchanged and
an existing build can still join. This is one of the few changes in this project where that is true;
say so in the commit message.

---

## §6 — Update the docs in the same commit

This is the step that gets skipped, and `docs/CLAUDE.md` rotted precisely because it was. Eleven
edits across three files, all small.

**`Assets/Scripts/CLAUDE.md`:**

1. **Menus, pointer, keys** opens with *"Three surfaces — the lobby's two canvases, the code pad,
   and the in-room menu — are one toolkit"*. It is four now; add the rules board.
2. **The toolkit table** gains a row:

   | `RulesBoard` | The always-present rules Canvas: a draggable, minimisable board built in code on `Menu Manager`. Uses `Panel` purely as the press model — no header, no lists. |

3. The paragraph ending *"`BuildRulesPanel` was moved onto this one"* — reword; the builder is gone
   and `RulesBoard` is what holds the 1 mm convention now.
4. The paragraph beginning *"Panels are placed off a **flattened** forward"* ends by describing the
   rules wing being placed in world space and parented with `worldPositionStays`. Replace that
   sentence: the board is never parented at all, and `FacePlayer` derives the yaw from where the
   board actually is instead of from a hand-tuned −40°.
5. **The room menu** section: the `Actions` list row now reads `Place Anchor`, `Passthrough`,
   `Rules`, then `Voice Chat` for the host, then the game's keys. And *"`HandleKey` knows exactly
   three keys"* becomes four.
6. **"Three things once shared `rightJoystick.y`"** is the paragraph §2 invalidates. Rewrite it:
   all three now gate on hover, and the stick is unclaimed when the beam is on neither a list nor
   the rules.
7. **Execution-order contract** table gains a row between `PlateauSpawnMenu` (23) and `PointerBeam`
   (24):

   | **23** | `RulesBoard` | moves the rules board's colliders, so it must run before `PointerBeam`'s `Physics.SyncTransforms()`; at any later order the beam tests last frame's position while you drag |

8. **Input** table gains a row: *Right trigger held on the rules title bar — drag the rules board;
   the right stick pushes it nearer and further while held.*

**`Assets/Scripts/Stairs/CLAUDE.md`:**

9. The bullet under **The board grid** beginning *"Bare squares are targeted by where the ray
   landed, not by what it hit"* is the paragraph §3h qualifies. Add the exception: a collider tagged
   `key` is refused before the lattice mapping, because a UI surface between the player and the
   board would otherwise resolve to the cell underneath it.

**Root `CLAUDE.md`:**

10. **Conventions that break silently** — `ScrollList.ScrollUpKey`/`ScrollDownKey` are already
    described as ids a row must never reuse. Add `Rules`, `RulesMove`, `RulesToggle` and
    `RulesBody` to that sentence: a `GameModule.menuActions` entry using one would be eaten.
11. **Design docs** table gains a row:

    | [`rulesUIUpdate.md`](docs/rulesUIUpdate.md) | The rules leaving the room menu and becoming an always-present, draggable, minimisable board. |

Then, because the four `CLAUDE.md` files cross-link heavily and markdown has no compiler:

```powershell
powershell -File Tools/Check-DocLinks.ps1
```

---

## §7 — Order of work

Each line compiles on its own, so stop and build at any of them.

1. §1 — `RulesBoard.cs`. Nothing references it yet; it compiles and does nothing.
2. §2 — `ScrollTextWithJoystick.cs`. Two new fields and one `if`.
3. §3a–3d — the wing comes out of `MenuControl`. At this point the rules are unreachable; that is
   expected and lasts one step.
4. §3e–3g — `MenuControl` owns the board and the `Rules` row appears.
5. §3h — the Stairs guard. One `if`, in `MRBoardGame.Stairs`.
6. §4 — compile.
7. §5 — Editor test.
8. §6 — docs, `Check-DocLinks.ps1`.
9. Commit. Use `git commit -F <file>` — the here-string form breaks in this shell.

**Do not stop after step 4.** Steps 1–4 leave a rules board that works and a Stairs board that
places a pawn when you press `+` over it, which is worse than what is there today.

---

## §8 — Tuning, all in one place

Every number worth arguing about is a field or a `const` at the top of `RulesBoard`:

| Want | Change |
| --- | --- |
| It should come up **open** | `startMinimized = false` |
| The card is too small to notice | `MinimizedWidth` (360), and the title in `ApplyState` — `"Rules"` can become `"Rules — press +"` |
| The expanded panel covers the table | `ExpandedHeight` (700) down, or `boardHeightOffset` (0.18) up |
| It sits where the board is | `boardRightOffset` (0.5); the room menu is 0.7 to the **left**, so keep this positive |
| The text is too small at arm's length | `prose.fontSize` in `BuildBody` (24). The toolkit's scale is header 44 / status 26 / row title 32 / row subtitle 24 |
| Dragging feels sluggish or twitchy | `dragPushSpeed` (0.8 m/s), `minDragDistance` / `maxDragDistance` (0.45–3 m) |
| It lands somewhere odd on load | `placeSettleSeconds` (0.75) — it re-places every frame for that long, which is what outlasts `CameraController2.PlaceAtRingSlot` teleporting the rig to its ring slot |

---

## §9 — Known limits, and the optional follow-ups

**Not done, on purpose:**

- **The board is world-locked, not head-locked.** Walk away and it stays where you left it. That is
  deliberate — an involuntarily moving panel in passthrough is the same nausea problem
  `PlayerRing` refuses to re-space players for. `X` → `Rules` is the recovery.
- **Its fields cannot be tuned in the Inspector** until somebody adds the component to
  `PersistentRig.prefab` by hand, because `MenuControl` adds it at runtime. Adding it is a one-line
  prefab edit and changes no behaviour; the `AddComponent` then finds it and does nothing. Worth
  doing once the numbers in §8 have been argued about in a headset.
- **A game whose `GameModule.keepPointerAlwaysOn` is false gets a board it can only press while the
  room menu is open**, because `CloseMenu` switches the pointer off. All three games today set it
  true, so this is latent rather than broken. If a fourth game wants a menu-only pointer, that is
  the moment to decide whether the rules board should force the pointer on for itself.
- **The board does not hide while the room menu is open.** They are placed 1.2 m apart (menu 0.7
  left, board 0.5 right) and do not overlap. If a future menu grows wide enough to reach it, hide
  the board in `OpenMenu1` and show it in `CloseMenu`.

**Worth doing later, not now:**

- A **per-game start state** on `GameModule` — a long or unusual game could come up expanded while
  a familiar one comes up as the card. One `bool` on the module, read in `ApplyModule`.
- **Section headings** in the rules text. The three `.txt` assets are prose; a board that could jump
  to "Movement" would be worth more than one that scrolls. That is a text-format change first and a
  UI change second, so it belongs in its own pass.
