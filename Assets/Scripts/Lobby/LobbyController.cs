using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The lobby: a Library canvas dead ahead and a Play canvas angled to its right, plus the code pad
/// that opens over them.
///
/// It replaced a 40-key QWERTY keyboard and a state machine inside GameController.Update() that
/// read one character at a time off pointerControl.currentLetter. Nothing about the *connection*
/// changed — this calls GameController.HostRoom and GameController.JoinRoom and reads its status
/// line back; see that class.
///
/// Panels are placed relative to the camera, MenuControl's idiom, rather than at scene coordinates.
/// The rig rests at its authored pose in the lobby (PersistentRig.prefab's root is at (0, 0, -10)
/// and CameraController2.PlaceAtRingSlot early-outs for non-game scenes), so anything placed at
/// world coordinates has to be placed in front of *that*, which is a number nobody remembers to
/// update. Camera-relative is right by construction.
///
/// Nothing here names a game. The Library rows come from GameCatalog.games in catalog order and
/// the "new public room" rows from the player's library — the same contract MenuControl keeps.
/// </summary>
public class LobbyController : MonoBehaviour
{
    // Play-panel row ids. Dispatched as keyNames, like everything else in this project.
    const string NewPrivateKey = "New Private Room";
    const string JoinPrivateKey = "Join Private Room";
    const string NewPublicKey = "New Public Room";
    const string BrowsePublicKey = "Browse Public Rooms";
    const string EditNameKey = "Change Name";
    const string BackKey = "Back";
    const string RefreshKey = "Refresh";

    [Header("Prefabs")]
    public GameObject panelPrefab;
    public GameObject codePadPrefab;

    [Header("Wiring")]
    public GameController connection;

    [Header("Placement, camera-relative")]
    public float libraryDistance = 1.6f;
    public float libraryLeft = 0.45f;
    public float playDistance = 1.45f;
    public float playRight = 0.95f;
    public float playYaw = 35f;
    public float padDistance = 1.2f;
    public float panelHeightOffset = -0.15f;

    private Panel library;
    private Panel play;
    private CodePad pad;
    private Panel padPanel;

    private InputReader inputs;
    private pointerControl pointer;

    private GameModule selected;
    private string playerName = "Player";

    // What the Play panel is showing. The panel is one list with three different sets of rows
    // rather than three panels, so there is one place that draws and one that dispatches.
    private enum PlayView { Root, PickGameToHost, Browse }
    private PlayView view = PlayView.Root;

    private List<RoomListing> rooms = new List<RoomListing>();
    private bool busy;

    async void Start()
    {
        ResolveRig();
        BuildPanels();

        // The pointer is the only interaction in the lobby, exactly as it was with the keyboard, so
        // it has to be on from the first frame. MenuControl.ApplyPointerDefault agrees with this —
        // its lobby branch returns true — and the two must keep agreeing: when they disagreed the
        // pointer came up dead and no key could be pressed at all.
        if (pointer != null)
        {
            pointer.gameObject.SetActive(true);
        }

        if (connection != null)
        {
            connection.Status += HandleStatus;
            connection.Failed += HandleFailed;
        }

        StoreService.Changed += HandleStoreChanged;

        playerName = StoreService.DisplayName;
        DrawPlay();
        DrawLibrary();

        await StoreService.InitializeAsync();

        playerName = StoreService.DisplayName;
        DrawLibrary();
        DrawPlay();
    }

    void OnDestroy()
    {
        StoreService.Changed -= HandleStoreChanged;

        if (connection != null)
        {
            connection.Status -= HandleStatus;
            connection.Failed -= HandleFailed;
        }
    }

    // ------------------------------------------------------------------ construction

    private void ResolveRig()
    {
        // Find-by-name, the convention everything except the old GameController used. Those four
        // direct serialized references into PersistentRig were a liability, not a model to copy: a
        // rename anywhere in the rig broke GameController at the Inspector level with no runtime
        // fallback. This is the commit where changing them was free.
        GameObject reader = GameObject.Find("Input Reader");
        inputs = reader != null ? reader.GetComponent<InputReader>() : null;
        if (inputs == null)
        {
            Debug.LogError("LobbyController: no \"Input Reader\" in the scene. Nothing in the " +
                           "lobby can be pressed.");
        }

        GameObject manager = GameObject.Find("Menu Manager");
        MenuControl menu = manager != null ? manager.GetComponent<MenuControl>() : null;
        pointer = menu != null && menu.pointer != null
                      ? menu.pointer.GetComponent<pointerControl>()
                      : null;

        if (pointer == null)
        {
            Debug.LogError("LobbyController: could not reach the laser pointer through " +
                           "\"Menu Manager\". Nothing in the lobby can be pressed.");
        }
    }

    private void BuildPanels()
    {
        if (panelPrefab == null)
        {
            Debug.LogError("LobbyController: panelPrefab is not assigned, so the lobby has no UI.");
            return;
        }

        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null)
        {
            Debug.LogError("LobbyController: no main camera, so the panels cannot be placed.");
            return;
        }

        library = Place(panelPrefab, cam, libraryDistance, -libraryLeft, 0f).GetComponent<Panel>();
        play = Place(panelPrefab, cam, playDistance, playRight, playYaw).GetComponent<Panel>();

        if (library != null)
        {
            library.name = "Library Panel";
            library.Bind(inputs, pointer);
            library.KeyPressed += HandleLibraryKey;
        }

        if (play != null)
        {
            play.name = "Play Panel";
            play.Bind(inputs, pointer);
            play.KeyPressed += HandlePlayKey;
        }

        if (codePadPrefab != null)
        {
            GameObject padGo = Place(codePadPrefab, cam, padDistance, 0f, 0f);
            padGo.name = "Code Pad";
            pad = padGo.GetComponent<CodePad>();
            padPanel = padGo.GetComponent<Panel>();

            if (padPanel != null)
            {
                padPanel.Bind(inputs, pointer);
                padPanel.KeyPressed += HandlePadKey;
            }
            if (pad != null)
            {
                pad.Submitted += HandlePadSubmitted;
                pad.Close();
            }
        }
    }

    /// <summary>
    /// One panel, in front of the camera. Yaw turns a panel that sits to one side back in towards
    /// the player instead of leaving it edge-on.
    /// </summary>
    private GameObject Place(GameObject prefab, Transform cam, float distance, float right, float yaw)
    {
        Vector3 forward = cam.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

        Vector3 rightAxis = Vector3.Cross(Vector3.up, forward);

        Vector3 position = cam.position + forward * distance + rightAxis * right +
                           Vector3.up * panelHeightOffset;

        GameObject panel = Instantiate(prefab, position, Quaternion.identity, transform);

        // A world-space Canvas is read from its -Z side: the reader looks ALONG the text's own
        // forward, which is why the standard billboard is forward = camera.forward and why
        // LookAt(camera) mirrors text. So the panel's forward points AWAY from the player, exactly
        // as PlayerControls does for the nametags and PlateauPieceView for the plateau counts.
        //
        // There used to be a further 180 here, on the theory that a Canvas "draws on its +Z face".
        // It did two things, both wrong: it mirrored every glyph, and it turned the panel's good
        // side away so the yaw below angled a side panel further out instead of in. See
        // docs/UIBugFixes.md §1.
        //
        // The yaw turns a panel sitting to one side back in towards the player. Positive is to the
        // player's right, which is the side `right` is measured on, so the two signs agree.
        panel.transform.rotation = Quaternion.LookRotation(forward, Vector3.up) *
                                   Quaternion.Euler(0f, yaw, 0f);
        return panel;
    }

    // ------------------------------------------------------------------ the Library canvas

    private void HandleStoreChanged()
    {
        DrawLibrary();
        DrawPlay();
    }

    private void DrawLibrary()
    {
        if (library == null)
        {
            return;
        }

        library.SetHeader("Library");

        List<RowData> rows = new List<RowData>();
        GameCatalog catalog = GameCatalog.Instance;

        if (catalog != null)
        {
            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module == null || string.IsNullOrEmpty(module.productId))
                {
                    continue;
                }

                rows.Add(RowFor(module));
            }
        }

        ScrollList list = library.List(0);
        if (list != null)
        {
            list.SetData(rows);
        }

        ShowDetail();
    }

    /// <summary>
    /// One catalog row's state. Owned rows are still pressable — pressing one selects it and shows
    /// its rules — so the list never has a row that does nothing.
    /// </summary>
    private RowData RowFor(GameModule module)
    {
        RowData row = new RowData(module.gameKey, module.DisplayName)
        {
            thumbnail = module.thumbnail,
            subtitle = PlayerCount(module),
            selected = selected == module,
            payload = module,
        };

        if (StoreService.Owns(module))
        {
            row.state = "In Library";
        }
        else if (!module.isPaid)
        {
            // The state cell says what pressing the row DOES, then what it costs. "Free — Add" read
            // as a price with a stray word after it.
            row.state = "Add — Free";
        }
        else
        {
            // The platform's formatted, localized price and nothing composed here. Until it has
            // arrived the row says so rather than showing a placeholder that looks like a price.
            string price = StoreService.PriceFor(module);
            row.state = string.IsNullOrEmpty(price) ? "Price…" : price;
            row.pressable = !string.IsNullOrEmpty(price);
        }

        return row;
    }

    /// <summary>
    /// "2 players", not "2-2 players" — Stairs is exactly two and the range read as a typo.
    /// </summary>
    private static string PlayerCount(GameModule module)
    {
        return module.minPlayers == module.maxPlayers
                   ? module.minPlayers + " players"
                   : module.minPlayers + "–" + module.maxPlayers + " players";
    }

    private void ShowDetail()
    {
        if (library == null)
        {
            return;
        }

        if (selected == null)
        {
            // Not "point at" — pointing highlights a row and does nothing else. The trigger is what
            // selects, and the sentence has to say so or the panel looks inert.
            library.SetDetail("Pick a game to read what it is.");
            return;
        }

        // The blurb and nothing else. This used to concatenate the whole rulesText asset — 2.5 to
        // 5 KB — into a 1160 x 192 block with overflowMode Truncate, so the player got the blurb
        // and two lines of rules cut off mid-word with no sign there was more. The rules have a
        // home already, in the in-room panel MenuControl builds, which scrolls.
        library.SetDetail(selected.blurb ?? "");
    }

    private async void HandleLibraryKey(string keyName)
    {
        // The same latch the Play panel takes. A purchase landing mid-join redraws a panel that is
        // about to be destroyed by the scene switch, and the library it would show is the one
        // already sent in the connection approval payload.
        if (busy)
        {
            return;
        }

        GameCatalog catalog = GameCatalog.Instance;
        GameModule module = catalog != null ? catalog.ByKey(keyName) : null;
        if (module == null)
        {
            return;
        }

        selected = module;
        DrawLibrary();

        if (StoreService.Owns(module))
        {
            return;                           // already yours; selecting it is the whole action
        }

        library.SetStatus("Working…");

        if (!module.isPaid)
        {
            // Free games are added explicitly, not auto-granted. Which is why the Play panel must
            // never be a dead end: Join Private Room works with an empty library.
            bool added = await StoreService.AddFreeAsync(module);
            library.SetStatus(added ? "" : "Could not add " + module.DisplayName);
            DrawLibrary();
            return;
        }

        PurchaseResult result = await StoreService.PurchaseAsync(module);

        switch (result.outcome)
        {
            case PurchaseOutcome.Succeeded:
            case PurchaseOutcome.AlreadyOwned:
                library.SetStatus("");
                break;

            case PurchaseOutcome.Cancelled:
                // Not a failure. Telling somebody who pressed Back that their purchase failed is
                // exactly what the four-way PurchaseOutcome exists to prevent.
                library.SetStatus("");
                break;

            default:
                library.SetStatus("Could not buy " + module.DisplayName +
                                  (string.IsNullOrEmpty(result.reason) ? "" : " — " + result.reason));
                break;
        }

        DrawLibrary();
    }

    // ------------------------------------------------------------------ the Play canvas

    private void DrawPlay()
    {
        if (play == null)
        {
            return;
        }

        switch (view)
        {
            case PlayView.PickGameToHost:
                DrawPickGame();
                return;

            case PlayView.Browse:
                DrawBrowse();
                return;
        }

        play.SetHeader("Play");

        bool canHostPublic = RoomDirectory.Instance != null && RoomDirectory.Instance.Available;
        bool haveGames = HostableGames().Count > 0;

        // The state cell says what pressing the row does. Two of these used to be a bare ">", which
        // is not a word, and the same bare ">" on both — so the two rows were indistinguishable
        // once the row layout clipped their titles away. See docs/UIBugFixes.md §4.
        List<RowData> rows = new List<RowData>
        {
            new RowData(NewPrivateKey, "New Private Room", "Start")
            {
                subtitle = "Play with whoever you give the code to",
            },
            new RowData(JoinPrivateKey, "Join Private Room", "Enter code")
            {
                subtitle = "Six characters, from whoever opened the room",
            },
        };

        RowData newPublic = new RowData(NewPublicKey, "New Public Room", "Choose game")
        {
            subtitle = "Listed for anyone who owns that game",
        };
        RowData browse = new RowData(BrowsePublicKey, "Browse Public Rooms", "Find a room");

        if (!canHostPublic)
        {
            // Degrade, do not block. Private rooms keep working and the row says why the public
            // ones do not, rather than failing silently when pressed.
            newPublic.pressable = false;
            newPublic.state = "Unavailable";
            browse.pressable = false;
            browse.state = "Unavailable";
        }
        else if (!haveGames)
        {
            newPublic.pressable = false;
            newPublic.state = "Library empty";
        }

        rows.Add(newPublic);
        rows.Add(browse);
        rows.Add(new RowData(EditNameKey, "You are: " + playerName, "Change")
        {
            subtitle = "What the other players see over your head",
        });

        Fill(rows);
    }

    private List<GameModule> HostableGames()
    {
        List<GameModule> games = new List<GameModule>();
        GameCatalog catalog = GameCatalog.Instance;

        if (catalog != null)
        {
            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module != null && StoreService.Owns(module))
                {
                    games.Add(module);
                }
            }
        }

        return games;
    }

    private void DrawPickGame()
    {
        play.SetHeader("New Public Room");

        List<RowData> rows = new List<RowData>();
        foreach (GameModule module in HostableGames())
        {
            rows.Add(new RowData(module.gameKey, module.DisplayName, "Host")
            {
                thumbnail = module.thumbnail,
                subtitle = PlayerCount(module),
            });
        }

        // No "<" in the state cell: the word "Back" is the affordance and the arrow was noise.
        rows.Add(new RowData(BackKey, "Back", ""));
        Fill(rows);
    }

    private void DrawBrowse()
    {
        play.SetHeader("Public Rooms");

        List<RowData> rows = new List<RowData>();
        GameCatalog catalog = GameCatalog.Instance;
        ulong mine = StoreService.OwnedMask;

        for (int i = 0; i < rooms.Count; i++)
        {
            RoomListing room = rooms[i];
            GameModule module = catalog != null ? catalog.ByKey(room.gameKey) : null;

            RowData row = new RowData(room.id, string.IsNullOrEmpty(room.hostName) ? room.name : room.hostName)
            {
                subtitle = module != null ? module.DisplayName : room.gameKey,
                state = room.players + "/" + room.maxPlayers,
                payload = room,
            };

            if (!room.SameBuild)
            {
                // This is what turns the project's single most mystifying failure — a join that
                // hangs on "Joining room..." for ever, because ForceSamePrefabs refused it with no
                // reason string — into a greyed row with three readable words.
                row.pressable = false;
                row.state = "Different version";
            }
            else if (room.Full)
            {
                row.pressable = false;
                row.state = "Full";
            }
            else if (module == null || !StoreService.MaskAllows(mine, module))
            {
                row.pressable = false;
                row.state = "Not in your library";
            }

            rows.Add(row);
        }

        if (rooms.Count == 0)
        {
            rows.Add(new RowData("", "No public rooms right now", "")
            {
                subtitle = "Anyone can open one from the New Public Room row",
                pressable = false,
            });
        }

        bool canRefresh = RoomDirectory.Instance != null && RoomDirectory.Instance.CanQuery;
        rows.Add(new RowData(RefreshKey, "Refresh", canRefresh ? "" : "Refreshing…")
        {
            pressable = canRefresh
        });
        rows.Add(new RowData(BackKey, "Back", ""));

        Fill(rows);
    }

    private void Fill(List<RowData> rows)
    {
        ScrollList list = play.List(0);
        if (list != null)
        {
            list.SetData(rows);
        }
    }

    private async void HandlePlayKey(string keyName)
    {
        if (busy)
        {
            return;
        }

        if (view == PlayView.PickGameToHost)
        {
            if (keyName == BackKey)
            {
                view = PlayView.Root;
                DrawPlay();
                return;
            }

            GameCatalog catalog = GameCatalog.Instance;
            GameModule module = catalog != null ? catalog.ByKey(keyName) : null;
            if (module != null)
            {
                RoomOptions.SetPublic(module.gameKey, playerName + " — " + module.DisplayName);
                Host();
            }
            return;
        }

        if (view == PlayView.Browse)
        {
            switch (keyName)
            {
                case BackKey:
                    view = PlayView.Root;
                    DrawPlay();
                    return;

                case RefreshKey:
                    await RefreshRooms();
                    return;
            }

            RoomListing room = FindRoom(keyName);
            if (room != null && !string.IsNullOrEmpty(room.joinCode))
            {
                // The unchanged join path. Lobby was only ever the noticeboard.
                Join(room.joinCode);
            }
            return;
        }

        switch (keyName)
        {
            case NewPrivateKey:
                RoomOptions.SetPrivate();
                Host();
                return;

            case JoinPrivateKey:
                OpenPad("Room Code", "Join", JoinCodeLength, "", PadPurpose.JoinCode);
                return;

            case NewPublicKey:
                view = PlayView.PickGameToHost;
                DrawPlay();
                return;

            case BrowsePublicKey:
                view = PlayView.Browse;
                rooms.Clear();
                DrawPlay();
                await RefreshRooms();
                return;

            case EditNameKey:
                OpenPad("Your Name", "OK", 20, playerName, PadPurpose.PlayerName);
                return;
        }
    }

    private RoomListing FindRoom(string id)
    {
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].id == id)
            {
                return rooms[i];
            }
        }
        return null;
    }

    private async Task RefreshRooms()
    {
        RoomDirectory directory = RoomDirectory.Instance;
        if (directory == null)
        {
            play.SetStatus("Public rooms are unavailable.");
            return;
        }

        play.SetStatus("Looking for rooms…");
        rooms = await directory.QueryAsync();

        play.SetStatus(directory.Available ? "" : directory.LastError);
        DrawPlay();
    }

    // ------------------------------------------------------------------ the code pad

    /// <summary>
    /// What the open pad is for. This used to be the submit key's *label* compared against the
    /// literal "Join", so two pads that happened to share a button word would have dispatched to
    /// each other.
    /// </summary>
    private enum PadPurpose { None, JoinCode, PlayerName }

    /// <summary>Relay join codes are always six characters. RelayVivox does not vary this.</summary>
    private const int JoinCodeLength = 6;

    private PadPurpose padPurpose = PadPurpose.None;

    private void OpenPad(string title, string submitLabel, int maxChars, string seed,
                         PadPurpose purpose)
    {
        if (pad == null)
        {
            Debug.LogWarning("LobbyController: no CodePad, so there is no way to type here.");
            return;
        }

        padPurpose = purpose;
        pad.Open(title, submitLabel, maxChars, seed);
    }

    private void HandlePadKey(string keyName)
    {
        if (pad != null)
        {
            pad.HandleKey(keyName);
        }
    }

    private void HandlePadSubmitted(string value)
    {
        if (value == null)
        {
            return;                           // Cancel
        }

        if (padPurpose == PadPurpose.JoinCode)
        {
            // The pad knows the length, so say so here rather than spending a Relay round trip to
            // come back with "Wrong room code" for something that was never going to be one.
            if (value.Length != JoinCodeLength)
            {
                play.SetStatus(value.Length == 0
                                   ? "Enter a room code, or start a new room."
                                   : "A room code is " + JoinCodeLength + " characters.");
                return;
            }

            RoomOptions.SetPrivate();
            Join(value);
            return;
        }

        playerName = string.IsNullOrEmpty(value) ? "Player" : value;
        StoreService.RememberDisplayName(playerName);
        DrawPlay();
    }

    // ------------------------------------------------------------------ the connection

    private void Host()
    {
        if (connection == null)
        {
            return;
        }

        busy = true;
        play.SetStatus("Creating room…");
        connection.HostRoom(playerName);
    }

    private void Join(string code)
    {
        if (connection == null)
        {
            return;
        }

        busy = true;
        play.SetStatus("Joining room…");
        connection.JoinRoom(playerName, code);
    }

    private void HandleStatus(string message)
    {
        if (play != null)
        {
            play.SetStatus(message);
        }
    }

    private void HandleFailed()
    {
        busy = false;
        view = PlayView.Root;
        DrawPlay();
    }
}
