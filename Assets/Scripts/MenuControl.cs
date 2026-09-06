using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The in-headset menu. X opens and closes it; the laser pointer plus the right trigger
/// picks a key.
///
/// Keys are dispatched by <see cref="keyInfo.keyName"/>, never by child index. The menu this
/// replaced resolved every widget with expressions like GetChild(0).GetChild(9).GetChild(13),
/// so re-skinning the prefab broke it with no compile error.
///
/// **The game keys are built at runtime, not authored.** They used to be one object per game in
/// Menu1.prefab and another in Menu2.prefab, with a case in HandleKey, a row in a KeyScene
/// dictionary, and a branch in the rules lookup — five edits across three shared files, and two
/// prefabs to keep in step by hand. Now the catalog is the list: MenuControl clones a template key
/// once per <see cref="GameModule"/>, and this class no longer knows the name of a single game.
/// </summary>
public class MenuControl : MonoBehaviour
{
    public InputReader inputs;
    // Two menus, differing only in the Voice Chat key: Menu1 has it, Menu2 does not. Voice is a
    // room-wide billed service, so only the host is offered the switch — see OpenMenu1.
    public GameObject Menu1;
    public GameObject Menu2;
    public Transform myCam;
    public Transform pointer;
    // Optional. Switched off while the menu is open so the game underneath cannot be
    // interacted with through it. Leave empty if a game does not need it.
    public GameObject worldRoot;
    public PlayerControls myPlayer;
    // Passthrough is a per-user comfort setting, like brightness — deliberately NOT a
    // NetworkVariable. One player switching to full VR must not drag the room with them.
    public PassthroughController passthrough;

    public float menuDistance = 1.3f;
    public float menuLeftOffset = 0.7f;

    // Whether the pointer stays live outside the menu. This is the *current* value, not a setting:
    // AdoptSceneDefaults overwrites it from the incoming scene's GameModule on every scene change,
    // and a game may still override it within its own scene through SetKeepPointerAlwaysOn.
    public bool keepPointerAlwaysOn = false;

    // Matches the keyName on the key in the menu prefabs exactly, spaces included.
    private const string VoiceKey = "Voice Chat";
    private const string PassthroughKey = "Passthrough";
    private const string PlaceAnchorKey = "Place Anchor";

    // Objects inside the menu prefabs. The two templates are inactive keys that exist only to be
    // cloned — one game-key sized, one action-key sized — so the generated keys keep the authored
    // collider, rigidbody, materials and label scale rather than having them set from code.
    private const string RowName = "Row1";
    private const string GameKeyTemplateName = "GameKeyTemplate";
    private const string ActionKeyTemplateName = "ActionKeyTemplate";

    // Row1-local layout, lifted from where the keys used to be authored. The column ran
    // 0.163 -> 0.014 -> -0.135 -> -0.282 (Place Anchor), and the action row sat one more step down
    // at -0.431 with its two keys at x 0.62 and 1.2.
    private const float KeyColumnX = 0.9f;
    private const float KeyColumnTopZ = 0.163f;
    private const float KeyRowSpacing = 0.149f;
    private const float KeyY = 0.011f;
    private const float ActionRowCentreX = 0.91f;
    private const float ActionRowSpacingX = 0.58f;

    // Past this many games the column pushes the action row down into the authored Voice Chat key
    // at z = -0.631, and then off the Background quad. Not enforced — a warning you can act on beats
    // a menu that silently drops the game you just added. Fixing it properly means either laying the
    // column out in two, or generating Voice Chat too and dropping the Menu1/Menu2 split with it.
    private const int ComfortableColumnKeys = 4;

    private pointerControl currentPointer;
    private GameObject currentMenu;
    private keyInfo pressedKey;

    /// <summary>
    /// True while the menu is up. Read by anything that also wants the right trigger — the menu
    /// owns it whenever it is open.
    /// </summary>
    public bool IsOpen => currentMenu != null;

    /// <summary>
    /// The pointer lives on PersistentRig and survives scene switches, so it can arrive in a scene
    /// in whatever state the previous one left it. Reset it to the incoming scene's default.
    ///
    /// This hangs off activeSceneChanged, not Start(). Menu Manager is part of PersistentRig and is
    /// therefore DontDestroyOnLoad, so Start() runs exactly once for the life of the app — in
    /// OpeningScene — and could never reset anything for another scene. Same pattern as
    /// PlayerControls.BindToScene and BoardAnchor.HandleActiveSceneChanged.
    /// </summary>
    void Start()
    {
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        AdoptSceneDefaults();
    }

    void OnDestroy()
    {
        SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
    }

    private void HandleActiveSceneChanged(Scene from, Scene to)
    {
        // LoadSceneMode.Single already destroyed the open menu — it is instantiated unparented into
        // the active scene — so these are dangling. Clearing them keeps IsOpen honest rather than
        // relying on Unity's destroyed-object null.
        currentMenu = null;
        currentPointer = null;
        pressedKey = null;

        AdoptSceneDefaults();
    }

    /// <summary>
    /// Take the incoming scene's pointer behaviour from its GameModule.
    ///
    /// This used to be a sticky field that only ever got set *true*, by ChasmGame, from
    /// PlateauSpawnMenu.Bind() on some later frame — so the pointer was dead for the first frames
    /// of the scene, and once ChasmGame had switched it on it stayed on in Stairs too. Reading it
    /// from the module makes it right from the first frame and correct in both directions.
    /// </summary>
    private void AdoptSceneDefaults()
    {
        GameModule module = GameCatalog.ActiveModule;
        keepPointerAlwaysOn = module != null && module.keepPointerAlwaysOn;
        ApplyPointerDefault();
    }

    /// <summary>
    /// A game scene shows the pointer only while the menu is open, unless its module opted out with
    /// keepPointerAlwaysOn. The lobby is the exception: its keyboard IS the interaction, and it has
    /// no menu to gate the pointer behind.
    ///
    /// The lobby case is not belt and braces. GameController.Start() switches the pointer on for the
    /// keyboard, and PersistentRig put a MenuControl in OpeningScene alongside it — so before this
    /// check the two raced on the same frame with no ordering guarantee, and MenuControl won: the
    /// pointer came up dead and no key on the keyboard could be pressed.
    /// </summary>
    private void ApplyPointerDefault()
    {
        if (pointer == null)
        {
            return;
        }

        bool lobby = !GameRoutes.IsGameScene(SceneManager.GetActiveScene().name);
        pointer.gameObject.SetActive(keepPointerAlwaysOn || lobby);
    }

    /// <summary>
    /// A game whose pointer also touches the board can force it on for the rest of its scene.
    ///
    /// Set the flag through this, never by assigning the field: AdoptSceneDefaults has already run
    /// and switched the pointer off by the time any scene component's first Update calls this, so a
    /// bare assignment would leave the pointer dead until the player opened and closed the menu.
    /// Prefer setting keepPointerAlwaysOn on the game's GameModule, which does the same thing a few
    /// frames earlier and does not need a component to be alive to say it.
    /// </summary>
    public void SetKeepPointerAlwaysOn(bool value)
    {
        keepPointerAlwaysOn = value;
        ApplyPointerDefault();
    }

    void Update()
    {
        if (inputs == null)
        {
            return;
        }

        if (inputs.ButtonXDown)
        {
            if (currentMenu == null)
            {
                OpenMenu1(true);
            }
            else
            {
                CloseMenu();
            }
        }

        if (currentMenu == null || currentPointer == null)
        {
            return;
        }

        // Press on trigger down, act on trigger up, so sliding off a key cancels it.
        if (inputs.RightMainTriggerDown)
        {
            if (currentPointer.currentKey != null)
            {
                pressedKey = currentPointer.currentKey;
                pressedKey.MakeBigger();
            }
        }
        else if (inputs.RightMainTriggerUp && pressedKey != null)
        {
            pressedKey.MakeSmaller();
            HandleKey(pressedKey.keyName);
            pressedKey = null;
        }
    }

    /// <summary>
    /// Three keys belong to the room itself and are handled here. Everything else belongs to a
    /// game: either it names one in the catalog, in which case it is a game switch, or it is one of
    /// the loaded game's own menuActions and goes to that game's IGameSession.
    ///
    /// Nothing in this method names a game, and adding one must never change that.
    /// </summary>
    private void HandleKey(string keyName)
    {
        switch (keyName)
        {
            case PassthroughKey:
                if (passthrough == null)
                {
                    passthrough = FindFirstObjectByType<PassthroughController>(FindObjectsInactive.Include);
                }
                if (passthrough != null)
                {
                    passthrough.Toggle();
                }
                CloseMenu();
                return;

            case VoiceKey:
                ToggleVoice();
                CloseMenu();
                return;

            case PlaceAnchorKey:
                // Puts the room's shared spatial anchor at the placer's feet and shares it, which
                // is what makes every headset's world space the same physical room. Room-owner
                // only, but the check lives in BoardAnchor so there is one place that decides.
                if (BoardAnchor.Instance != null)
                {
                    BoardAnchor.Instance.RequestPlaceAnchor();
                }
                else
                {
                    Debug.LogWarning("MenuControl: no BoardAnchor, so there is nothing to place. " +
                                     "It belongs on the Network Manager object.");
                }
                CloseMenu();
                return;
        }

        if (GameRoutes.IsGameKey(keyName))
        {
            RequestGame(keyName);
            CloseMenu();
            return;
        }

        // A game's own key. Only the loaded game's actions are ever built, so reaching here with a
        // live session means the game declared the key in its GameModule and forgot to handle it.
        IGameSession session = GameSessionRegistry.Active;
        if (session != null)
        {
            session.InvokeMenuAction(keyName);
            CloseMenu();
            return;
        }

        Debug.Log("MenuControl: '" + keyName + "' pressed — no game is wired to that key yet.");
    }

    /// <summary>
    /// Ask the server to move the whole room into a game. Any player may do this, not just the
    /// room owner — the request is validated server-side against GameRoutes.
    /// </summary>
    private void RequestGame(string gameKey)
    {
        PlayerControls me = ResolveMyPlayer();
        GameSelector selector = me != null ? me.GetComponent<GameSelector>() : null;
        if (selector == null)
        {
            Debug.LogWarning("MenuControl: no local player yet, cannot switch to '" + gameKey + "'.");
            return;
        }

        selector.RequestGame(gameKey);
    }

    public void OpenMenu1(bool showRules = false)
    {
        // The host gets the menu with the Voice Chat key; everybody else gets the one without it.
        // If the local player cannot be resolved yet — it arrives a moment after a scene switch —
        // this reads as "not the host", which is the safe way round.
        bool isRoomOwner = LocalPlayerIsRoomOwner();
        GameObject prefab = isRoomOwner ? Menu1 : Menu2;

        if (prefab == null && !isRoomOwner)
        {
            // An unassigned Menu2 must not stop a client opening the menu at all — they would lose
            // game switching and Place Anchor with it. SetVoiceEnabledServerRpc rejects a non-host
            // sender regardless, so the worst case here is a key that does nothing.
            Debug.LogWarning("MenuControl: Menu2 is not assigned on " + name + ", so the client " +
                             "menu falls back to the host one. The Voice Chat key will show but " +
                             "will be inert.");
            prefab = Menu1;
        }

        if (prefab == null || myCam == null)
        {
            Debug.LogWarning("MenuControl: menu prefab or myCam is not assigned; cannot open the menu.");
            return;
        }

        currentMenu = Instantiate(prefab, myCam.position + menuDistance * myCam.forward.normalized, Quaternion.identity);
        currentMenu.transform.rotation = myCam.rotation;
        currentMenu.transform.position += -menuLeftOffset * currentMenu.transform.right;

        BuildKeys(currentMenu);

        if (showRules)
        {
            BuildRulesPanel(currentMenu);
        }

        ShowVoiceState();

        if (worldRoot != null)
        {
            worldRoot.SetActive(false);
        }

        if (pointer != null)
        {
            pointer.gameObject.SetActive(true);
            currentPointer = pointer.GetComponent<pointerControl>();
        }
    }

    // ------------------------------------------------------------------ the keys

    /// <summary>
    /// Build this menu instance's keys from the catalog and the loaded game.
    ///
    /// Clone-a-template rather than instantiate Key.prefab: Key.prefab is the *lobby keyboard's*
    /// key and its label sits at a different offset and scale, and Row1 carries a non-uniform
    /// (0.5, 50, 0.815) scale that the authored key scales were chosen against. Cloning a key that
    /// is already correct in this hierarchy avoids reproducing any of that in code.
    /// </summary>
    private void BuildKeys(GameObject menu)
    {
        Transform row = HierarchyUtils.FindDescendant(menu.transform, RowName);
        if (row == null)
        {
            Debug.LogError("MenuControl: no '" + RowName + "' in " + menu.name + ", so no keys can " +
                           "be built. The menu will open empty.");
            return;
        }

        Transform gameTemplate = HierarchyUtils.FindDescendant(row, GameKeyTemplateName);
        Transform actionTemplate = HierarchyUtils.FindDescendant(row, ActionKeyTemplateName);

        if (gameTemplate == null)
        {
            Debug.LogError("MenuControl: no '" + GameKeyTemplateName + "' under " + RowName +
                           ". It is an inactive key kept in the prefab purely to be cloned; " +
                           "without it there are no game keys and no Place Anchor.");
            return;
        }

        float z = KeyColumnTopZ;

        GameCatalog catalog = GameCatalog.Instance;
        if (catalog != null)
        {
            if (catalog.games.Count > ComfortableColumnKeys)
            {
                Debug.LogWarning("MenuControl: " + catalog.games.Count + " games is more than the " +
                                 "menu column comfortably fits (" + ComfortableColumnKeys + "). The " +
                                 "action row will start colliding with the Voice Chat key and then " +
                                 "run off the panel; the menu needs re-laying out.");
            }

            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module == null || string.IsNullOrEmpty(module.gameKey))
                {
                    Debug.LogWarning("MenuControl: catalog row " + i + " is empty or has no game " +
                                     "key, so it gets no menu key.");
                    continue;
                }

                CloneKey(gameTemplate, row, module.gameKey, module.MenuLabel,
                         new Vector3(KeyColumnX, KeyY, z));
                z -= KeyRowSpacing;
            }
        }

        // Place Anchor continues the same column, so it stays below the games however many there
        // are. It used to be authored at a fixed z, which a fourth game would have landed on top of.
        CloneKey(gameTemplate, row, PlaceAnchorKey, PlaceAnchorKey, new Vector3(KeyColumnX, KeyY, z));
        z -= KeyRowSpacing;

        BuildActionKeys(row, actionTemplate, z);
    }

    /// <summary>
    /// The loaded game's own keys, spread along one row under the column. Only the game that is
    /// actually loaded contributes any, which is what replaced the KeyScene dictionary and the
    /// filter pass that used to hide the other games' keys after the fact.
    /// </summary>
    private void BuildActionKeys(Transform row, Transform actionTemplate, float z)
    {
        GameModule module = GameCatalog.ActiveModule;
        if (module == null || module.menuActions == null || module.menuActions.Length == 0)
        {
            return;
        }

        if (actionTemplate == null)
        {
            Debug.LogError("MenuControl: " + module.gameKey + " declares " + module.menuActions.Length +
                           " menu action(s), but there is no '" + ActionKeyTemplateName + "' under " +
                           RowName + " to build them from.");
            return;
        }

        int count = module.menuActions.Length;
        for (int i = 0; i < count; i++)
        {
            GameMenuAction action = module.menuActions[i];
            if (string.IsNullOrEmpty(action.keyName))
            {
                continue;
            }

            float x = ActionRowCentreX + (i - (count - 1) * 0.5f) * ActionRowSpacingX;
            CloneKey(actionTemplate, row, action.keyName, action.Label, new Vector3(x, KeyY, z));
        }
    }

    /// <summary>
    /// One key, cloned from a template that is inactive in the prefab.
    ///
    /// overrideNameChange is set because keyInfo.Start() rewrites a key's label with its keyName,
    /// and on a menu instantiated this frame that has not run yet — without the flag a menuLabel
    /// that differs from the gameKey would be silently overwritten a moment later. Same reason
    /// ShowVoiceState sets it.
    /// </summary>
    private void CloneKey(Transform template, Transform row, string keyName, string label, Vector3 localPosition)
    {
        GameObject clone = Instantiate(template.gameObject, row);
        clone.name = keyName;
        clone.transform.localPosition = localPosition;
        clone.transform.localRotation = template.localRotation;
        clone.transform.localScale = template.localScale;
        clone.SetActive(true);

        keyInfo info = clone.GetComponent<keyInfo>();
        if (info == null)
        {
            Debug.LogError("MenuControl: the key template '" + template.name + "' has no keyInfo, " +
                           "so '" + keyName + "' can never be pressed.");
            return;
        }

        info.keyName = keyName;
        info.overrideNameChange = true;
        if (info.keyLabel != null)
        {
            info.keyLabel.SetText(label);
        }
    }

    // ------------------------------------------------------------------ the rules panel

    /// <summary>
    /// The scrollable rules panel beside the menu, built procedurally rather than from a prefab.
    ///
    /// The text is the loaded game's GameModule.rulesText — a direct asset reference. It used to be
    /// a Resources.Load keyed off an if/else over scene names whose fall-through was "BASHRules",
    /// so a game that was not one of the three showed BASH's rules with no warning.
    /// </summary>
    private void BuildRulesPanel(GameObject menu)
    {
        GameModule module = GameCatalog.ActiveModule;

        GameObject rulesCanvasGo = new GameObject("RulesCanvas");
        rulesCanvasGo.transform.SetParent(menu.transform, false);
        // A wing to the menu's left, turned back in towards the player rather than lying flat
        // alongside it. Both numbers are menu-local, so they follow wherever OpenMenu1 puts the menu.
        rulesCanvasGo.transform.localPosition = new Vector3(-0.6f, 0, -0.75f);
        rulesCanvasGo.transform.localRotation = Quaternion.Euler(0, -75, 0);

        Canvas canvas = rulesCanvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRt = rulesCanvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(800, 800);
        canvasRt.localScale = new Vector3(0.002f, 0.002f, 0.002f);

        // A dark background so the text is readable against passthrough.
        UnityEngine.UI.Image bgImage = rulesCanvasGo.AddComponent<UnityEngine.UI.Image>();
        bgImage.color = new Color(0, 0, 0, 0.85f);

        GameObject viewportGo = new GameObject("Viewport");
        viewportGo.transform.SetParent(rulesCanvasGo.transform, false);
        RectTransform viewportRt = viewportGo.AddComponent<RectTransform>();
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.sizeDelta = Vector2.zero;
        viewportRt.pivot = new Vector2(0.5f, 0.5f);

        viewportGo.AddComponent<UnityEngine.UI.RectMask2D>();

        GameObject contentGo = new GameObject("Content");
        contentGo.transform.SetParent(viewportGo.transform, false);
        RectTransform contentRt = contentGo.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 1);
        contentRt.anchorMax = new Vector2(1, 1);
        contentRt.pivot = new Vector2(0.5f, 1);
        contentRt.sizeDelta = new Vector2(0, 2000);
        contentRt.anchoredPosition = Vector2.zero;

        TMPro.TextMeshProUGUI text = contentGo.AddComponent<TMPro.TextMeshProUGUI>();
        text.fontSize = 24;
        text.color = Color.white;
        text.margin = new Vector4(20, 20, 20, 20);

        if (module != null && module.rulesText != null)
        {
            text.text = module.rulesText.text;
        }
        else if (module != null)
        {
            text.text = "No rules asset is set on " + module.name + ".";
        }
        else
        {
            text.text = "";
        }

        UnityEngine.UI.ContentSizeFitter csf = contentGo.AddComponent<UnityEngine.UI.ContentSizeFitter>();
        csf.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;

        UnityEngine.UI.ScrollRect scrollRect = rulesCanvasGo.AddComponent<UnityEngine.UI.ScrollRect>();
        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 15f;

        ScrollTextWithJoystick scroller = rulesCanvasGo.AddComponent<ScrollTextWithJoystick>();
        scroller.scrollRect = scrollRect;
        scroller.inputs = inputs;
        scroller.scrollSpeed = 1.5f;
    }

    // ------------------------------------------------------------------ the rest

    public void CloseMenu()
    {
        Destroy(currentMenu);
        currentMenu = null;
        pressedKey = null;
        currentPointer = null;

        if (pointer != null && !keepPointerAlwaysOn)
        {
            pointer.gameObject.SetActive(false);
        }

        if (worldRoot != null)
        {
            worldRoot.SetActive(true);
        }
    }

    /// <summary>Keep the menu glued to the camera. Call from a game that needs it.</summary>
    public void moveMenu()
    {
        if (currentMenu != null && myCam != null)
        {
            currentMenu.transform.position = myCam.position + myCam.forward.normalized;
            currentMenu.transform.rotation = myCam.rotation;
        }
    }

    /// <summary>
    /// myPlayer is set by PlayerControls.Setup(). After a scene switch this MenuControl is a
    /// brand-new instance in a brand-new scene, so fall back to asking Netcode directly rather
    /// than depending on rebind order.
    /// </summary>
    private PlayerControls ResolveMyPlayer()
    {
        if (myPlayer == null)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
            {
                myPlayer = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
            }
        }

        return myPlayer;
    }

    private bool LocalPlayerIsRoomOwner()
    {
        PlayerControls me = ResolveMyPlayer();
        return me != null && me.GetIsRoomOwner();
    }

    /// <summary>
    /// Ask the server to flip voice chat for the whole room. This deliberately does not call
    /// RelayVivox: the host reacts to its own replicated change through the same RoomAnchor path
    /// as everybody else, so there is one code path and the host cannot end up out of step with
    /// the room it is setting.
    /// </summary>
    private void ToggleVoice()
    {
        if (RoomAnchor.Instance == null)
        {
            Debug.LogWarning("MenuControl: no RoomAnchor, so there is no room to switch voice for. " +
                             "BoardAnchor spawns it once the server has started.");
            return;
        }

        RoomAnchor.Instance.SetVoiceEnabledServerRpc(!RoomAnchor.Instance.voiceEnabled.Value);
    }

    /// <summary>
    /// Label the Voice Chat key with the room's current setting, so the host can tell what state
    /// they are in without asking somebody. The menu is rebuilt on every open and destroyed on
    /// close, so doing this once here is enough — there is no live menu to update if the value
    /// changes while the menu is shut.
    /// </summary>
    private void ShowVoiceState()
    {
        if (currentMenu == null || RoomAnchor.Instance == null)
        {
            return;
        }

        bool on = RoomAnchor.Instance.voiceEnabled.Value;
        keyInfo[] keys = currentMenu.GetComponentsInChildren<keyInfo>(true);

        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i].keyName != VoiceKey)
            {
                continue;
            }

            // keyInfo.Start() rewrites a key's label with its keyName, and on a menu instantiated
            // this frame it has not run yet. overrideNameChange is the flag that stops it, so the
            // ON/OFF text set here is not silently overwritten a moment later.
            keys[i].overrideNameChange = true;
            if (keys[i].keyLabel != null)
            {
                keys[i].keyLabel.SetText(on ? "Voice Chat: ON" : "Voice Chat: OFF");
            }

            if (on)
            {
                keys[i].KeepOn();
            }
            else
            {
                keys[i].TurnOff();
            }

            return;
        }
    }

    /// <summary>Called by PlayerControls once the local player has spawned.</summary>
    public void Setup(PlayerControls newPlayer)
    {
        myPlayer = newPlayer;
    }
}
