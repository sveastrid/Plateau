using Unity.Netcode;
using UnityEngine;

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
    public GameObject Menu1;
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

    private pointerControl currentPointer;
    private GameObject currentMenu;
    private keyInfo pressedKey;

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

            case "Stairs":
            case "Chasms":
                RequestGame(keyName);
                CloseMenu();
                break;

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
        // myPlayer is set by PlayerControls.Setup(). After a scene switch this MenuControl is a
        // brand-new instance in a brand-new scene, so fall back to asking Netcode directly rather
        // than depending on rebind order.
        if (myPlayer == null)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
            {
                myPlayer = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
            }
        }

        GameSelector selector = myPlayer != null ? myPlayer.GetComponent<GameSelector>() : null;
        if (selector == null)
        {
            Debug.LogWarning("MenuControl: no local player yet, cannot switch to '" + gameKey + "'.");
            return;
        }

        selector.RequestGame(gameKey);
    }

    public void OpenMenu1()
    {
        if (Menu1 == null || myCam == null)
        {
            Debug.LogWarning("MenuControl: Menu1 prefab or myCam is not assigned; cannot open the menu.");
            return;
        }

        currentMenu = Instantiate(Menu1, myCam.position + menuDistance * myCam.forward.normalized, Quaternion.identity);
        currentMenu.transform.rotation = myCam.rotation;
        currentMenu.transform.position += -menuLeftOffset * currentMenu.transform.right;

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

        if (pointer != null)
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

    /// <summary>Called by PlayerControls once the local player has spawned.</summary>
    public void Setup(PlayerControls newPlayer)
    {
        myPlayer = newPlayer;
    }
}
