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
///  - **The rules left the menu entirely.** BuildRulesPanel built the rules as a wing off this
///    panel and CloseMenu destroyed it, so the rules were reachable only by knowing X exists and
///    only while the menu was in front of the board. They are now RulesBoard, present for the whole
///    game; the Rules row here only brings it back when it has been dragged out of reach.
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
    private const string RulesKey = "Rules";

    private GameObject currentMenu;
    private Panel currentPanel;
    private RulesBoard rules;

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
            OpenMenu1();
        }
        else
        {
            CloseMenu();
        }
    }

    /// <summary>
    /// Four keys belong to the room itself and are handled here. Everything else belongs to a
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

            case RulesKey:
                // Not a game's key even though it shows a game's text: the board is a fixture of
                // the room like the pointer, and the text it shows comes from GameCatalog.
                EnsureRulesBoard().Recall();
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

    public void OpenMenu1()
    {
        if (roomMenu == null || myCam == null)
        {
            Debug.LogWarning("MenuControl: roomMenu or myCam is not assigned; cannot open the menu.");
            return;
        }

        // Yaw-only, off a flattened forward. Two reasons, and neither is cosmetic:
        //
        //  - A world-space Canvas is read from its -Z side — the reader looks ALONG the text's own
        //    forward, which is why the standard billboard is forward = camera.forward and why
        //    LookAt(camera) mirrors text. There used to be a further 180 here on the opposite
        //    theory; it mirrored every glyph and turned the panel away. See docs/UIBugFixes.md §1
        //    and Assets/Scripts/Stairs/CLAUDE.md, "TMP is read from its -Z side".
        //  - myCam.rotation carried the head's pitch, so a menu opened while looking down at the
        //    board came up tipped — and the rules wing beside it is placed off a flattened axis
        //    (RulesBoard.FacePlayer), so the two ended up in different planes.
        Vector3 flat = myCam.forward;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
        Vector3 rightAxis = Vector3.Cross(Vector3.up, flat);

        currentMenu = Instantiate(roomMenu, myCam.position + menuDistance * flat,
                                  Quaternion.identity);

        currentMenu.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
        currentMenu.transform.position += -menuLeftOffset * rightAxis;

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
        list.SetCaption("Games");

        List<RowData> rows = new List<RowData>();
        string locked = RoomAnchor.LockedGameKey;
        string activeScene = SceneManager.GetActiveScene().name;
        string code = RoomCode();

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
            currentPanel.SetStatus(WithCode(code, "Public room — locked to one game."));
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
                bool here = module.sceneName == activeScene;

                // A state cell, because the row is otherwise a bare word with nothing saying that
                // pressing it moves the whole room into that game.
                rows.Add(new RowData(module.gameKey, module.MenuLabel, here ? "Playing" : "Play")
                {
                    selected = here,
                });
            }

            currentPanel.SetStatus(WithCode(code, rows.Count == 0
                                                     ? "Nobody in this room owns a game yet."
                                                     : ""));
        }

        list.SetData(rows);
    }

    /// <summary>
    /// The room's Relay join code, or "". Resolved the way VisibleWhenLooking resolves it — the
    /// Network Manager is DontDestroyOnLoad, so it is reachable from a game scene.
    ///
    /// It belongs on this panel: before this, the only way to read the code you needed to give
    /// somebody was to notice the InfoBlock on your own right hand and stare at it.
    /// </summary>
    private static string RoomCode()
    {
        GameObject manager = GameObject.Find("Network Manager");
        RelayVivox relay = manager != null ? manager.GetComponent<RelayVivox>() : null;
        return relay != null && !string.IsNullOrEmpty(relay.relayRoomCode) ? relay.relayRoomCode : "";
    }

    /// <summary>The status line: the room code, then whatever else the panel had to say.</summary>
    private static string WithCode(string code, string message)
    {
        string left = string.IsNullOrEmpty(code) ? "" : "Code " + code;

        if (left.Length == 0)
        {
            return message;
        }
        return message.Length == 0 ? left : left + "   ·   " + message;
    }

    private void BuildActionRows()
    {
        ScrollList list = currentPanel.List(1);
        if (list == null)
        {
            return;
        }

        list.SetCaption("Room");

        bool owner = LocalPlayerIsRoomOwner();

        // Place Anchor is room-owner only and the check lives in BoardAnchor, which for anybody
        // else logs "only the room owner places the anchor" and returns — so the row looked live,
        // closed the menu and did nothing. Say so on the row instead.
        RowData anchor = new RowData(PlaceAnchorKey, PlaceAnchorKey, owner ? "" : "Host only")
        {
            subtitle = "Line every headset up to this room",
            pressable = owner,
        };

        // Passthrough is a toggle and used to be the only one that did not show its state, so
        // pressing it was a coin toss. Voice Chat below has always shown ON/OFF.
        bool seeThrough = PassthroughController.IsPassthroughOn();

        List<RowData> rows = new List<RowData>
        {
            anchor,
            // Reachable at last. HandleKey has handled Passthrough since it was written and nothing
            // ever built a key for it.
            new RowData(PassthroughKey, PassthroughKey, seeThrough ? "ON" : "OFF")
            {
                subtitle = "See the real room, or go fully virtual",
                selected = seeThrough,
            },
        };

        rows.Add(new RowData(RulesKey, RulesKey, "")
        {
            subtitle = "Bring the rules back in front of you",
        });

        // Voice is a room-wide billed service, so only the host is offered the switch. That used to
        // be the entire reason there were two menu prefabs.
        if (owner)
        {
            bool on = RoomAnchor.Instance != null && RoomAnchor.Instance.voiceEnabled.Value;
            rows.Add(new RowData(VoiceKey, VoiceKey, on ? "ON" : "OFF")
            {
                subtitle = "Talk to everybody in this room",
                selected = on,
            });
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

    /// <summary>
    /// Keep the menu glued to the camera. Call from a game that needs it. Called by nothing today —
    /// kept in step with OpenMenu1 rather than left holding the old, mirrored rotation for somebody
    /// to copy.
    /// </summary>
    public void moveMenu()
    {
        if (currentMenu == null || myCam == null)
        {
            return;
        }

        Vector3 flat = myCam.forward;
        flat.y = 0f;
        flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;

        currentMenu.transform.position = myCam.position + flat;
        currentMenu.transform.rotation = Quaternion.LookRotation(flat, Vector3.up);
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
