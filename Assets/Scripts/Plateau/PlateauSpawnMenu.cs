using TMPro;
using UnityEngine;

/// <summary>
/// Each player's personal piece-spawning menu — plateauRules.md's "Buying Pieces" stood in for by
/// a free, uncapped +/- (buying itself is not implemented; see CLAUDE.md). Holding the LEFT grip
/// BY ITSELF activates the menu; letting go, pressing the right grip too, or opening
/// Menu1 deactivates it again.
///
/// Unlike Menu1 this never drops the player's current board selection while it is open
/// (PlateauSelection.Update() special-cases IsOpen for exactly this), because its "-" key acts on
/// whatever piece the player already has selected on the board.
///
/// Tinted with the local player's own seat colour (PlateauPalette) every frame it is open, purely
/// so a player can tell at a glance that the menu hanging off their own wrist is theirs. Purely
/// local, never sent over the network -- the same as Menu1 and the passthrough toggle.
///
/// Keys are dispatched by keyInfo.keyName, exactly like MenuControl.HandleKey: "Add Troop" /
/// "Remove Troop", "Add Parshendi" / "Remove Parshendi", "Add Shardbearer" / "Remove Shardbearer",
/// "Add Bridge" / "Remove Bridge". The Chasmfiend and Gemheart columns are not wired to a
/// PieceKind -- neither is an implemented piece kind yet -- so their keys fall through to the
/// default case and are deliberately inert, the same contract as an unwired key in Menu1.
///
/// Order 23: must execute before PlateauSelection (25) so that the menu opens before PlateauSelection
/// checks IsOpen, preventing the selection from being cleared when the left grip is pressed.
/// </summary>
[DefaultExecutionOrder(23)]
public class PlateauSpawnMenu : MonoBehaviour
{
    [Tooltip("The manually placed SpawnMenu instance under the Left Hand (found automatically if left empty).")]
    public GameObject menuInstance;

    [Header("Scene (resolved by name if empty, and re-resolved after a game switch)")]
    public InputReader inputs;
    public MenuControl menu;
    public PlateauSelection selection;
    public Transform leftHand;
    public pointerControl pointer;

    Renderer backgroundRenderer;
    PlateauTint backgroundTint;
    TextMeshPro scoreText;
    keyInfo pressedKey;

    /// <summary>True while the menu is active on the hand. Read by PlateauSelection.</summary>
    public bool IsOpen => menuInstance != null && menuInstance.activeSelf;

    void OnDisable()
    {
        if (menuInstance != null)
        {
            menuInstance.SetActive(false);
            pressedKey = null;

            if (pointer != null && pointer.currentKey != null)
            {
                pointer.currentKey.ChangeToOffMaterial();
                pointer.currentLetter = "";
                pointer.currentKey = null;
            }
        }
    }

    void Update()
    {
        if (!Bind())
        {
            if (menuInstance != null)
            {
                menuInstance.SetActive(false);
                pressedKey = null;

                if (pointer != null && pointer.currentKey != null)
                {
                    pointer.currentKey.ChangeToOffMaterial();
                    pointer.currentLetter = "";
                    pointer.currentKey = null;
                }
            }
            return;
        }

        // "By itself": engaging the right grip too hands the gesture to WorldGrab instead, and
        // Menu1 already owns the right trigger while it is open, so this must not compete with it.
        bool wantOpen = inputs.LeftGrip && !inputs.RightGrip && (menu == null || !menu.IsOpen);

        if (wantOpen && !menuInstance.activeSelf)
        {
            menuInstance.SetActive(true);
        }
        else if (!wantOpen && menuInstance.activeSelf)
        {
            menuInstance.SetActive(false);
            pressedKey = null;

            if (pointer != null && pointer.currentKey != null)
            {
                pointer.currentKey.ChangeToOffMaterial();
                pointer.currentLetter = "";
                pointer.currentKey = null;
            }
        }

        if (!menuInstance.activeSelf)
        {
            return;
        }

        ApplySeatTint();
        ApplyScoreText();

        if (pointer == null)
        {
            return;
        }

        // Press on trigger down, act on trigger up, so sliding off a key cancels it -- the same
        // idiom as MenuControl and PlateauSelection.
        if (inputs.RightMainTriggerDown)
        {
            if (pointer.currentKey != null)
            {
                pressedKey = pointer.currentKey;
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

    /// <summary>One case per wired key in the prefab. Add PieceKinds here as they get implemented.</summary>
    void HandleKey(string keyName)
    {
        switch (keyName)
        {
            case "Add Troop":       RequestAdd(PieceKind.Troop); break;
            case "Add Parshendi":   RequestAdd(PieceKind.Parshendi); break;
            case "Add Shardbearer": RequestAdd(PieceKind.Shardbearer); break;
            case "Add Bridge":      RequestAdd(PieceKind.Bridge); break;

            case "Remove Troop":       RequestRemove(PieceKind.Troop); break;
            case "Remove Parshendi":   RequestRemove(PieceKind.Parshendi); break;
            case "Remove Shardbearer": RequestRemove(PieceKind.Shardbearer); break;
            case "Remove Bridge":      RequestRemove(PieceKind.Bridge); break;

            case "Add Gemheart":      RequestAddNeutral(PieceKind.Gemheart); break;
            case "Add Chasmfiend":    RequestAddNeutral(PieceKind.Chasmfiend); break;
            case "Remove Gemheart":   RequestRemoveNeutral(PieceKind.Gemheart); break;
            case "Remove Chasmfiend": RequestRemoveNeutral(PieceKind.Chasmfiend); break;

            case "Add to Score":        RequestAddScore(); break;
            case "Subtract From Score": RequestSubtractScore(); break;

            default:
                Debug.Log("PlateauSpawnMenu: '" + keyName + "' pressed — no piece kind wired to that key yet.");
                break;
        }
    }

    void RequestAdd(PieceKind kind)
    {
        PlateauGame game = PlateauGame.Instance;
        if (game == null || !game.IsSpawned)
        {
            return;
        }
        game.RequestAddPieceServerRpc((byte)kind);
    }

    /// <summary>
    /// Only acts when the piece currently selected on the board is this same kind -- pressing the
    /// Bridge column's "-" while a Parshendi is selected is inert, same as a key with nothing behind
    /// it. That match is what lets "that plateau" (plateauRules.md's own wording) resolve
    /// unambiguously: wherever the player currently has THIS kind selected.
    /// </summary>
    void RequestRemove(PieceKind kind)
    {
        PlateauGame game = PlateauGame.Instance;
        if (game == null || !game.IsSpawned || selection == null)
        {
            return;
        }

        if (!selection.TryGetSelectedStack(out int plateau, out int seat, out int selectedKind))
        {
            return;
        }

        if (selectedKind != (int)kind)
        {
            return;
        }

        game.RequestRemovePieceServerRpc((byte)plateau, (byte)kind);
    }

    /// <summary>
    /// Gemheart/Chasmfiend "+": unlike RequestAdd, this targets whichever plateau the player
    /// selected on the board (PlateauSelection.TryGetSelectedPlateau) rather than always the
    /// central plateau — neither kind is owned by a seat, so there is no "your" plateau to default to.
    /// Inert with nothing selected, same contract as every other key with nothing behind it.
    /// </summary>
    void RequestAddNeutral(PieceKind kind)
    {
        PlateauGame game = PlateauGame.Instance;
        if (game == null || !game.IsSpawned || selection == null)
        {
            return;
        }

        if (!selection.TryGetSelectedPlateau(out int plateau))
        {
            return;
        }

        game.RequestAddNeutralPieceServerRpc((byte)plateau, (byte)kind);
    }

    /// <summary>Gemheart/Chasmfiend "-": removes one from the currently selected plateau.</summary>
    void RequestRemoveNeutral(PieceKind kind)
    {
        PlateauGame game = PlateauGame.Instance;
        if (game == null || !game.IsSpawned || selection == null)
        {
            return;
        }

        if (!selection.TryGetSelectedPlateau(out int plateau))
        {
            return;
        }

        game.RequestRemoveNeutralPieceServerRpc((byte)plateau, (byte)kind);
    }

    /// <summary>
    /// Score "+"/"-": the player's own held-gemheart count, not tied to any plateau selection.
    /// </summary>
    void RequestAddScore()
    {
        PlateauGame game = PlateauGame.Instance;
        if (game != null && game.IsSpawned)
        {
            game.RequestAddScoreServerRpc();
        }
    }

    void RequestSubtractScore()
    {
        PlateauGame game = PlateauGame.Instance;
        if (game != null && game.IsSpawned)
        {
            game.RequestSubtractScoreServerRpc();
        }
    }

    void ApplySeatTint()
    {
        if (backgroundTint == null || backgroundRenderer == null)
        {
            return;
        }
        backgroundTint.SetBaseColor(backgroundRenderer, PlateauPalette.ForSeat(PlateauGame.LocalSeat()));
    }

    void ApplyScoreText()
    {
        if (scoreText == null)
        {
            return;
        }
        PlateauGame game = PlateauGame.Instance;
        int score = game != null && game.IsSpawned ? game.ScoreForSeat(PlateauGame.LocalSeat()) : 0;
        scoreText.SetText(score.ToString());
    }

    // ------------------------------------------------------------------ binding

    /// <summary>
    /// Re-resolve everything this scene owns. A game switch is LoadSceneMode.Single and destroys
    /// the rig, the Input Reader and the Menu Manager -- the same contract PlateauSelection.Bind()
    /// and PlayerControls.BindToScene follow.
    /// </summary>
    bool Bind()
    {
        if (inputs == null)
        {
            GameObject go = GameObject.Find("Input Reader");
            inputs = go != null ? go.GetComponent<InputReader>() : null;
        }

        if (menu == null)
        {
            GameObject go = GameObject.Find("Menu Manager");
            menu = go != null ? go.GetComponent<MenuControl>() : null;

            // Menu Manager now lives on PersistentRig and is shared with every other scene, which
            // authors keepPointerAlwaysOn = false (menu-only pointer). ChasmGame is the one game
            // where the pointer also touches the board itself (see PointerBeam), so force it here
            // rather than on the shared default.
            if (menu != null)
            {
                menu.SetKeepPointerAlwaysOn(true);
            }
        }

        if (selection == null)
        {
            selection = FindFirstObjectByType<PlateauSelection>();
        }

        if (leftHand == null || pointer == null)
        {
            GameObject rig = GameObject.Find("XRRig");
            if (rig != null)
            {
                if (leftHand == null)
                {
                    leftHand = PlateauBoard.FindDescendant(rig.transform, "Left Hand");
                }
                if (pointer == null)
                {
                    Transform p = PlateauBoard.FindDescendant(rig.transform, "Pointer");
                    pointer = p != null ? p.GetComponent<pointerControl>() : null;
                }
            }
        }
        
        if (leftHand != null && menuInstance == null)
        {
            Transform menuT = PlateauBoard.FindDescendant(leftHand, "SpawnMenu");
            if (menuT != null)
            {
                menuInstance = menuT.gameObject;
                menuInstance.SetActive(false); // default to off

                Transform background = PlateauBoard.FindDescendant(menuT, "Background");
                backgroundRenderer = background != null ? background.GetComponent<Renderer>() : null;
                backgroundTint = backgroundRenderer != null ? backgroundRenderer.gameObject.AddComponent<PlateauTint>() : null;

                Transform scoreTextTransform = PlateauBoard.FindDescendant(menuT, "ScoreText");
                scoreText = scoreTextTransform != null ? scoreTextTransform.GetComponent<TextMeshPro>() : null;
            }
        }

        return inputs != null && leftHand != null && menuInstance != null;
    }
}
