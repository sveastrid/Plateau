using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The in-headset menu. X opens and closes it; the laser pointer plus the right trigger
/// picks a key.
///
/// Keys are dispatched by <see cref="keyInfo.keyName"/>, never by child index. The menu this
/// replaced resolved every widget with expressions like GetChild(0).GetChild(9).GetChild(13),
/// so re-skinning the prefab broke it with no compile error. Adding a game to this template
/// should mean adding a key to the prefab and a case to <see cref="HandleKey"/>, nothing else.
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

    // A game where the pointer is also used to touch the board itself, rather than only the menu,
    // leaves it switched on all the time. Off by default so the lobby and StairsGame keep the
    // menu-only behaviour they were authored with.
    public bool keepPointerAlwaysOn = false;

    // Matches the keyName on the key in Menu1.prefab exactly, space included.
    private const string VoiceKey = "Voice Chat";

    /// <summary>
    /// Keys that only mean something in one scene, so that Menu1/Menu2 can stay a single pair of
    /// prefabs shared by every game. Anything not listed here shows everywhere, which is the case
    /// for all five of the original keys — a game switch and Place Anchor are always meaningful.
    ///
    /// Without this, BASH's Reset Game and Random Islands would sit in the lobby, Stairs and
    /// Chasms doing nothing, which CLAUDE.md already flags as a wart in the mirror-image case
    /// (Passthrough: a live handler with no key).
    /// </summary>
    static readonly Dictionary<string, string> KeyScene = new Dictionary<string, string>
    {
        { "Reset Game",     GameRoutes.BashSceneName },
        { "Random Islands", GameRoutes.BashSceneName },
    };

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
    /// OpeningScene — and could never reset anything for StairsGame or ChasmGame. Same pattern as
    /// PlayerControls.BindToScene and BoardAnchor.HandleActiveSceneChanged.
    /// </summary>
    void Start()
    {
        SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        ApplyPointerDefault();
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

        ApplyPointerDefault();
    }

    /// <summary>
    /// A game scene shows the pointer only while the menu is open, unless it opted out with
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
    /// A game whose pointer also touches the board — ChasmGame, via PointerBeam — opts in here
    /// rather than by changing the shared PersistentRig default and taking the other scenes down
    /// with it.
    ///
    /// Set the flag through this, never by assigning the field. HandleActiveSceneChanged has
    /// already run and switched the pointer off by the time any scene component's first Update
    /// calls this, so a bare assignment would leave the pointer dead until the player opened and
    /// closed the menu.
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
                OpenMenu1();
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
    /// One case per key in the menu prefab. Add games here.
    /// </summary>
    private void HandleKey(string keyName)
    {
        switch (keyName)
        {
            case "Passthrough":
                if (passthrough == null)
                {
                    passthrough = FindFirstObjectByType<PassthroughController>(FindObjectsInactive.Include);
                }
                if (passthrough != null)
                {
                    passthrough.Toggle();
                }
                CloseMenu();
                break;

            case VoiceKey:
                ToggleVoice();
                CloseMenu();
                break;

            case "Stairs":
            case "Chasms":
            case "BASH":
                RequestGame(keyName);
                CloseMenu();
                break;

            case "Reset Game":
            {
                // BASH's own BASHMenu is gone; these two keys are the shared menu's now. They
                // resolve their target in the active scene and log-and-no-op when there is not
                // one, the same way Place Anchor handles a missing BoardAnchor. Outside BASH the
                // scene filter in OpenMenu1 hides them, so this is the belt to that's braces.
                ControlListener bash = FindFirstObjectByType<ControlListener>();
                if (bash != null)
                {
                    bash.ResetBoard();
                }
                else
                {
                    Debug.Log("MenuControl: 'Reset Game' pressed, but there is no BASH board " +
                              "in this scene.");
                }
                CloseMenu();
                break;
            }

            case "Random Islands":
            {
                IslandManager islands = FindFirstObjectByType<IslandManager>();
                if (islands != null)
                {
                    islands.RandomizeIslands();
                }
                else
                {
                    Debug.Log("MenuControl: 'Random Islands' pressed, but there is no BASH board " +
                              "in this scene.");
                }
                CloseMenu();
                break;
            }

            case "Place Anchor":
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
                break;

            default:
                // A key with no game behind it. Deliberately inert — add a case here and a row
                // in GameRoutes to give it one.
                Debug.Log("MenuControl: '" + keyName + "' pressed — no game is wired to that key yet.");
                break;
        }
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

    public void OpenMenu1()
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

        ApplySceneKeyFilter();
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
    /// Hide the keys that belong to a game the room is not currently in. Done on the instantiated
    /// menu rather than in the prefabs, so a game's keys cost one row in <see cref="KeyScene"/>
    /// rather than a third menu prefab to keep in step with the other two.
    /// </summary>
    private void ApplySceneKeyFilter()
    {
        if (currentMenu == null)
        {
            return;
        }

        string scene = SceneManager.GetActiveScene().name;
        keyInfo[] keys = currentMenu.GetComponentsInChildren<keyInfo>(true);

        for (int i = 0; i < keys.Length; i++)
        {
            bool applies = !KeyScene.TryGetValue(keys[i].keyName, out string only) || only == scene;
            keys[i].gameObject.SetActive(applies);
        }
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
