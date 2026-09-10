using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The in-headset room menu. X opens and closes it; the laser pointer plus the right trigger picks
/// a row.
///
/// Keys are dispatched by <see cref="keyInfo.keyName"/>, never by child index. The menu this
/// replaced resolved every widget with expressions like GetChild(0).GetChild(9).GetChild(13), so
/// re-skinning the prefab broke it with no compile error.
///
/// **The rows are built at runtime, not authored.** They used to be one 3D key per game in
/// Menu1.prefab and another in Menu2.prefab; then a cloned template key per catalog entry, laid out
/// down a column that ran out of room past about four games. They are now rows in a
/// <see cref="ScrollList"/> on the shared <see cref="Panel"/> — the same toolkit the lobby's two
/// canvases use — so the catalog can grow without the layout being re-solved.
///
/// Three things the conversion bought, beyond the scrolling:
///
///  - **Menu1 and Menu2 collapsed into one prefab.** The split existed only to hide Voice Chat from
///    non-hosts, which with generated rows is one `if`. OpenMenu1's prefab-picking branch and its
///    "Menu2 is not assigned" fallback both went with it.
///  - **Passthrough became reachable.** HandleKey has handled it all along and nothing ever built a
///    key for it — a live handler with no way to press it.
///  - **X no longer opens the menu in the lobby**, where it resolved no local player, warned, and
///    would now open an empty list.
///
/// Nothing in this class names a game, and adding one must never change that.
/// </summary>
public class MenuControl : MonoBehaviour
{
    public InputReader inputs;

    [Tooltip("RoomMenu.prefab. One prefab for host and client alike — Voice Chat is a row that is " +
             "generated or not, not a second prefab.")]
    public GameObject roomMenu;

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

    // Matches the keyName on the generated row exactly, spaces included.
    private const string VoiceKey = "Voice Chat";
    private const string PassthroughKey = "Passthrough";
    private const string PlaceAnchorKey = "Place Anchor";

    private GameObject currentMenu;
    private Panel currentPanel;

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
        currentPanel = null;

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
    /// keepPointerAlwaysOn. The lobby is the exception: its panels ARE the interaction, and it has
    /// no menu to gate the pointer behind.
    ///
    /// The lobby case is not belt and braces. LobbyController.Start switches the pointer on for its
    /// panels, and PersistentRig put a MenuControl in OpeningScene alongside it — so before this
    /// check the two raced on the same frame with no ordering guarantee, and MenuControl won: the
    /// pointer came up dead and nothing in the lobby could be pressed.
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
        if (inputs == null || !inputs.ButtonXDown)
        {
            return;
        }

        // Not in the lobby. There is no local player there to resolve, no room to place an anchor
        // in, and with a library-filtered game list the menu would open empty. The lobby has its
        // own two panels; X is not one of its controls.
        if (!GameRoutes.IsGameScene(SceneManager.GetActiveScene().name))
        {
            return;
        }

        if (currentMenu == null)
        {
            OpenMenu1(true);
        }
        else
        {
            CloseMenu();
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
    /// room owner — the request is validated server-side against GameRoutes, the room's public
    /// lock and the room's combined library.
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
        if (roomMenu == null || myCam == null)
        {
            Debug.LogWarning("MenuControl: roomMenu or myCam is not assigned; cannot open the menu.");
            return;
        }

        currentMenu = Instantiate(roomMenu, myCam.position + menuDistance * myCam.forward.normalized,
                                  Quaternion.identity);

        // The 180 is new and is not cosmetic: a world-space Canvas draws on its +Z face, so a menu
        // given the camera's own rotation shows the player its back. The offset is taken off the
        // CAMERA's right, not the menu's — after the flip those point opposite ways, and using the
        // menu's would put it on the wrong side.
        currentMenu.transform.rotation = myCam.rotation * Quaternion.Euler(0f, 180f, 0f);
        currentMenu.transform.position += -menuLeftOffset * myCam.right;

        currentPanel = currentMenu.GetComponent<Panel>();
        if (currentPanel == null)
        {
            Debug.LogError("MenuControl: " + roomMenu.name + " has no Panel component, so the menu " +
                           "has no rows and no way to press one.");
            return;
        }

        pointerControl beam = pointer != null ? pointer.GetComponent<pointerControl>() : null;
        currentPanel.Bind(inputs, beam);
        currentPanel.KeyPressed += HandleKey;

        BuildRows();

        if (showRules)
        {
            BuildRulesPanel(currentMenu);
        }

        if (worldRoot != null)
        {
            worldRoot.SetActive(false);
        }

        if (pointer != null)
        {
            pointer.gameObject.SetActive(true);
        }
    }

    // ------------------------------------------------------------------ the rows

    /// <summary>
    /// The games this room may switch into, then the room's own actions, then the loaded game's.
    ///
    /// Two lists on one panel rather than one: the game list scrolls and the action list does not,
    /// and putting the actions in the scrolling list would let a fourth game push Place Anchor off
    /// the bottom — which is precisely the failure the old fixed column had.
    /// </summary>
    private void BuildRows()
    {
        BuildGameRows();
        BuildActionRows();
    }

    private void BuildGameRows()
    {
        ScrollList list = currentPanel.List(0);
        if (list == null)
        {
            Debug.LogError("MenuControl: the room menu's Panel has no list, so no game can be " +
                           "chosen from it.");
            return;
        }

        currentPanel.SetHeader("Room");

        List<RowData> rows = new List<RowData>();
        string locked = RoomAnchor.LockedGameKey;
        string activeScene = SceneManager.GetActiveScene().name;

        if (locked != null)
        {
            GameCatalog catalog = GameCatalog.Instance;
            GameModule module = catalog != null ? catalog.ByKey(locked) : null;
            if (module != null)
            {
                rows.Add(new RowData(module.gameKey, module.MenuLabel, "Playing")
                {
                    subtitle = "This is a public room for one game",
                    selected = module.sceneName == activeScene,
                    pressable = false,
                });
            }
            currentPanel.SetStatus("Public room — locked to one game.");
        }
        else
        {
            // Filtered by the room's combined library, and still naming no game. The filter is
            // cosmetic: GameSelector.RequestGameServerRpc runs the same check server-side, which is
            // what actually enforces it.
            List<GameModule> playable = RoomLibrary.Playable();
            for (int i = 0; i < playable.Count; i++)
            {
                GameModule module = playable[i];
                rows.Add(new RowData(module.gameKey, module.MenuLabel, "")
                {
                    selected = module.sceneName == activeScene,
                });
            }

            currentPanel.SetStatus(rows.Count == 0
                                       ? "Nobody in this room owns a game yet."
                                       : "");
        }

        list.SetData(rows);
    }

    private void BuildActionRows()
    {
        ScrollList list = currentPanel.List(1);
        if (list == null)
        {
            return;
        }

        List<RowData> rows = new List<RowData>
        {
            new RowData(PlaceAnchorKey, PlaceAnchorKey, ""),
            // Reachable at last. HandleKey has handled Passthrough since it was written and nothing
            // ever built a key for it.
            new RowData(PassthroughKey, PassthroughKey, ""),
        };

        // Voice is a room-wide billed service, so only the host is offered the switch. That used to
        // be the entire reason there were two menu prefabs.
        if (LocalPlayerIsRoomOwner())
        {
            bool on = RoomAnchor.Instance != null && RoomAnchor.Instance.voiceEnabled.Value;
            rows.Add(new RowData(VoiceKey, VoiceKey, on ? "ON" : "OFF") { selected = on });
        }

        // The loaded game's own keys. Only the loaded game contributes any, which is what replaced
        // the KeyScene dictionary and the filter pass that used to hide the other games' keys after
        // the fact.
        GameModule module = GameCatalog.ActiveModule;
        if (module != null && module.menuActions != null)
        {
            for (int i = 0; i < module.menuActions.Length; i++)
            {
                GameMenuAction action = module.menuActions[i];
                if (!string.IsNullOrEmpty(action.keyName))
                {
                    rows.Add(new RowData(action.keyName, action.Label, ""));
                }
            }
        }

        list.SetData(rows);
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

        Canvas canvas = rulesCanvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform canvasRt = rulesCanvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(800, 800);
        // 1 UI unit = 1 mm, the toolkit's convention. This panel used to be at 0.002 and Text Input
        // at 0.01, which is exactly how panels end up subtly different sizes.
        canvasRt.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        // A wing beyond the menu's left, nearer the player and turned back in towards them rather
        // than lying flat alongside it.
        //
        // Placed in WORLD space off the camera and then parented keeping that pose, rather than in
        // menu-local coordinates. Two things made the old menu-local offsets wrong: the menu root is
        // now flipped 180 degrees to face the player, so its local X and Z both run backwards, and
        // it is itself a Canvas at scale 0.001, so menu-local units are millimetres. Setting the
        // scale before parenting and passing worldPositionStays leaves both to Unity.
        Vector3 flat = myCam.forward;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, flat);

        rulesCanvasGo.transform.position = myCam.position + flat * (menuDistance - 0.55f) +
                                           rightAxis * -(menuLeftOffset + 0.6f);
        rulesCanvasGo.transform.rotation = Quaternion.LookRotation(flat, Vector3.up) *
                                           Quaternion.Euler(0f, 180f - 40f, 0f);
        rulesCanvasGo.transform.SetParent(menu.transform, true);

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
        if (currentPanel != null)
        {
            currentPanel.KeyPressed -= HandleKey;
            currentPanel = null;
        }

        Destroy(currentMenu);
        currentMenu = null;

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
            currentMenu.transform.rotation = myCam.rotation * Quaternion.Euler(0f, 180f, 0f);
        }
    }

    /// <summary>
    /// myPlayer is set by PlayerControls.Setup(). After a scene switch this MenuControl is the same
    /// instance but the player object may not be, so fall back to asking Netcode directly rather
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

    /// <summary>Called by PlayerControls once the local player has spawned.</summary>
    public void Setup(PlayerControls newPlayer)
    {
        myPlayer = newPlayer;
    }
}
