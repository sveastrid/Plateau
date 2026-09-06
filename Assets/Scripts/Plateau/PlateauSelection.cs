using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Point at one of your own pieces, choose how many, and move them — the interaction from
/// plateauRules.md "VR Interaction", entirely local to this client.
///
/// Two states, not three. Choosing the number and choosing the destination are live at the same
/// time, because that is how the game is meant to read: the count sits on the selected piece while
/// the legal plateaus glow, and either can be changed before committing.
///
/// Order 25: consumes the hit PointerBeam (24) produced this frame.
/// </summary>
[DefaultExecutionOrder(25)]
public class PlateauSelection : MonoBehaviour
{
    [Header("Scene (resolved by name if empty, and re-resolved after a game switch)")]
    public InputReader inputs;
    public MenuControl menu;
    public PointerBeam beam;
    public PlateauSpawnMenu spawnMenu;

    [Header("Count selection")]
    [Tooltip("Joystick deflection that steps the count. Hysteresis against the release threshold.")]
    public float JoystickOn = 0.60f;
    public float JoystickOff = 0.35f;
    [Tooltip("Seconds before a held joystick starts repeating, then between repeats.")]
    public float FirstRepeat = 0.45f;
    public float RepeatInterval = 0.12f;

    [Tooltip("Seconds after a move is sent before another can be. Stops a double trigger sending twice.")]
    public float SendLockout = 1f;

    [Header("Highlights")]
    public Color hoverTint = Color.white;
    [Range(0f, 1f)] public float hoverStrength = 0.45f;
    public Color legalTint = new Color(0.45f, 0.95f, 1f);
    [Range(0f, 1f)] public float legalStrength = 0.55f;
    [Range(0f, 1f)] public float legalHoverStrength = 0.9f;
    [Range(0f, 1f)] public float candidateStrength = 0.35f;

    [Header("Plateau selection (targets Gemheart/Chasmfiend placement)")]
    public Color plateauSelectTint = new Color(0f, 0.7f, 0.93f);
    [Range(0f, 1f)] public float plateauSelectStrength = 0.65f;
    [Tooltip("How far the tint pulses above/below plateauSelectStrength, so the selected plateau " +
             "keeps reading clearly instead of blending into a static tint.")]
    [Range(0f, 1f)] public float plateauSelectPulseDepth = 0.25f;
    public float plateauSelectPulseSpeed = 5f;

    enum State { Idle, PlateauSelected, Selected }

    State state = State.Idle;

    PlateauPieceTag selected;
    PlateauPieceTag hovered;
    PlateauPieceTag pressedPiece;
    int pressedPlateau = -1;
    /// <summary>The BAR the press landed on, when it landed on one — not just the plateau that bar
    /// names. Two free bars can reach the same plateau from two different plateaus in your system,
    /// and the server no longer guesses between them. -1 when the press was on bare plateau.</summary>
    int pressedEdge = -1;

    /// <summary>The plateau chosen with nothing selected, for the spawn menu's Gemheart/Chasmfiend
    /// keys. Independent of `selected` — see TryGetSelectedPlateau.</summary>
    int selectedPlateau = -1;
    int hoveredPlateau = -1;
    int pressedIdlePlateau = -1;

    int count = 1;
    int armedEpoch;
    int seat = -1;

    int detent;
    float nextStepAt;
    float sendLockUntil;

    int hoveredLegalPlateau = -1;
    bool legalDirty;

    readonly List<int> legal = new List<int>();
    readonly List<int> candidateEdges = new List<int>();
    readonly List<PlateauTint> spotTints = new List<PlateauTint>();

    PlateauGame subscribed;

    void OnEnable()
    {
        SceneManager.activeSceneChanged += HandleSceneChanged;
    }

    void OnDisable()
    {
        SceneManager.activeSceneChanged -= HandleSceneChanged;
        Unsubscribe();
        // A game switch destroys the rig mid-selection. Same reason WorldGrab cancels in OnDisable.
        Cancel();
    }

    void HandleSceneChanged(Scene from, Scene to)
    {
        inputs = null;
        menu = null;
        beam = null;
        Cancel();
    }

    void Unsubscribe()
    {
        if (subscribed != null)
        {
            subscribed.BoardChanged -= HandleBoardChanged;
            subscribed = null;
        }
    }

    void HandleBoardChanged()
    {
        // Somebody else moved something; what this piece can reach may have changed.
        legalDirty = true;
    }

    void Update()
    {
        PlateauGame game = PlateauGame.Instance;
        if (game != subscribed)
        {
            Unsubscribe();
            subscribed = game;
            if (subscribed != null)
            {
                subscribed.BoardChanged += HandleBoardChanged;
            }
            legalDirty = true;
        }

        if (!Bind() || !Playable(game))
        {
            // The spawn menu borrows the right trigger the same way Menu1 does while it is open
            // (Playable already stands down for it via the LeftGrip check below), but unlike Menu1
            // it must not drop whatever is currently selected -- its "-" key acts on exactly that
            // piece (TryGetSelectedStack) and its Gemheart/Chasmfiend keys on that plateau
            // (TryGetSelectedPlateau). Everything else that fails Playable() still hard-cancels.
            //
            // IsOpenOrOpening, not IsOpen: the menu waits 0.15 s on the grip before it opens, so
            // for about ten frames the grip is down and the menu is still shut -- which is exactly
            // where this cancel used to land, leaving the state Idle by the time a key was
            // pressable.
            if (spawnMenu == null || !spawnMenu.IsOpenOrOpening)
            {
                Cancel();
            }
            return;
        }

        ResolveHit(out PlateauPieceTag hitPiece, out int hitPlateau, out int hitEdge);

        if (state == State.Idle)
        {
            UpdateIdle(hitPiece, hitPlateau);
        }
        else if (state == State.PlateauSelected)
        {
            UpdatePlateauSelected(hitPiece, hitPlateau);
        }
        else
        {
            UpdateSelected(game, hitPiece, hitPlateau, hitEdge);
        }
    }

    /// <summary>
    /// The plateau under the cursor for PURPOSES OF PLATEAU SELECTION, unlike hitPlateau alone:
    /// ResolveHit's PlateauPieceTag check short-circuits before it ever reaches the PlateauTag
    /// fallback, so a plateau with anything sitting on it — a neutral Gemheart/Chasmfiend, or
    /// another player's stack — would otherwise be unreachable by the beam. A piece already
    /// carries the plateau it stands on, so fall back to that instead of re-raycasting.
    /// </summary>
    static int PlateauUnderCursor(PlateauPieceTag hitPiece, int hitPlateau)
    {
        if (hitPlateau >= 0)
        {
            return hitPlateau;
        }
        if (hitPiece != null && !hitPiece.IsPlacedBridge)
        {
            return hitPiece.plateau;
        }
        return -1;
    }

    /// <summary>
    /// Everything that must be true before a piece can be touched. Any of these going false drops
    /// the selection rather than leaving a half-committed move behind.
    /// </summary>
    bool Playable(PlateauGame game)
    {
        if (game == null || !game.IsSpawned || !game.boardLive.Value)
        {
            return false;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null || !board.IsBaked)
        {
            return false;
        }

        seat = PlateauGame.LocalSeat();
        if (seat < 0)
        {
            return false;                       // the server has not seated this player yet
        }

        // The menu owns the right trigger while it is up, and it is instantiated between the player
        // and the board, so its keys are what the beam is on anyway.
        if (menu != null && menu.IsOpen)
        {
            return false;
        }

        if (WorldGrab.IsActive)
        {
            return false;
        }

        // Levels, not the gesture's own flag. WorldGrab only goes active on BOTH grips, so without
        // this a player squeezing one grip in preparation still has a live selection beam and the
        // grab can swallow a trigger press.
        if (inputs.LeftGrip || inputs.RightGrip)
        {
            return false;
        }

        return true;
    }

    void ResolveHit(out PlateauPieceTag piece, out int plateau, out int edge)
    {
        piece = null;
        plateau = -1;
        edge = -1;

        if (beam == null || !beam.HasHit || beam.Hit.collider == null)
        {
            return;
        }

        // GetComponentInParent both times: a piece may be hit on its root collider, its owner
        // disc, or its model, and Plateau.prefab's collider is on a nested grandchild.
        piece = beam.Hit.collider.GetComponentInParent<PlateauPieceTag>();
        if (piece != null)
        {
            return;
        }

        PlateauTag tag = beam.Hit.collider.GetComponentInParent<PlateauTag>();
        if (tag != null)
        {
            plateau = tag.index;
            return;
        }

        PlateauEdgeTag spotTag = beam.Hit.collider.GetComponentInParent<PlateauEdgeTag>();
        if (spotTag != null)
        {
            edge = spotTag.edge;
        }
    }

    // ------------------------------------------------------------------ idle

    void UpdateIdle(PlateauPieceTag hitPiece, int hitPlateau)
    {
        // Only your own pieces light up. A highlight on a piece you cannot select would be a lie,
        // and plateauRules.md is explicit: "the piece highlights when targeted" is about yours.
        PlateauPieceTag mine = (hitPiece != null && hitPiece.seat == seat) ? hitPiece : null;
        SetHover(mine);

        // With no piece of yours under the beam, bare board (or anything neutral/someone else's
        // sitting on it) is a candidate for plateau selection instead.
        int plateauTarget = mine == null ? PlateauUnderCursor(hitPiece, hitPlateau) : -1;
        SetHoverPlateau(plateauTarget);

        if (inputs.RightMainTriggerDown)
        {
            pressedPiece = mine;
            pressedIdlePlateau = plateauTarget;
        }
        else if (inputs.RightMainTriggerUp)
        {
            // Act on the piece that was pressed, and only if the pointer is still on it — sliding
            // off cancels, exactly as it does on a menu key.
            if (pressedPiece != null && pressedPiece == mine)
            {
                Select(mine);
            }
            else if (pressedIdlePlateau >= 0 && pressedIdlePlateau == plateauTarget)
            {
                SelectPlateau(pressedIdlePlateau);
            }
            pressedPiece = null;
            pressedIdlePlateau = -1;
        }
    }

    /// <summary>
    /// Mirrors UpdateIdle while a plateau (rather than a piece) is selected: your own piece still
    /// wins and switches straight into Selected; a different plateau replaces the current one;
    /// the current one again lets it go — same "press it again" idiom as Select()/Cancel().
    /// </summary>
    void UpdatePlateauSelected(PlateauPieceTag hitPiece, int hitPlateau)
    {
        UpdateSelectedPlateauPulse();

        PlateauPieceTag mine = (hitPiece != null && hitPiece.seat == seat) ? hitPiece : null;
        SetHover(mine);

        int plateauTarget = mine == null ? PlateauUnderCursor(hitPiece, hitPlateau) : -1;
        SetHoverPlateau(plateauTarget);

        if (inputs.RightMainTriggerDown)
        {
            pressedPiece = mine;
            pressedIdlePlateau = plateauTarget;
        }
        else if (inputs.RightMainTriggerUp)
        {
            if (pressedPiece != null && pressedPiece == mine)
            {
                Select(mine);
            }
            else if (pressedIdlePlateau >= 0 && pressedIdlePlateau == plateauTarget)
            {
                if (pressedIdlePlateau == selectedPlateau)
                {
                    DeselectPlateau();
                }
                else
                {
                    SelectPlateau(pressedIdlePlateau);
                }
            }
            pressedPiece = null;
            pressedIdlePlateau = -1;
        }
    }

    // ------------------------------------------------------------------ selected

    void UpdateSelected(PlateauGame game, PlateauPieceTag hitPiece, int hitPlateau, int hitEdge)
    {
        if (selected == null || game.boardEpoch.Value != armedEpoch)
        {
            Cancel();
            return;
        }

        if (legalDirty)
        {
            legalDirty = false;
            RecomputeLegal(game);
        }

        int max = MaxCount();
        count = Mathf.Clamp(count, 1, max);

        StepCount(max);

        PlateauPieceTag mine = (hitPiece != null && hitPiece.seat == seat) ? hitPiece : null;
        SetHover(mine);                          // so the piece you would switch to lights up too

        // A Bridge Spot resolves to the plateau its edge would connect to, but only when that edge
        // is one of THIS selection's current candidates -- an occupied, unreachable or irrelevant
        // spot resolves to nothing, the same inertness as missing a piece or a plateau outright.
        // No-op for any non-bridge selection, since candidateEdges is only ever populated for
        // PieceKind.Bridge.
        int hitCandidateEdge = -1;
        if (hitEdge >= 0)
        {
            hitPlateau = ResolveBridgeSpotPlateau(game, hitEdge);
            // Keep the bar itself, not just the plateau it names.
            hitCandidateEdge = hitPlateau >= 0 ? hitEdge : -1;
        }

        bool overLegal = hitPlateau >= 0 && legal.Contains(hitPlateau);
        SetHoveredLegal(overLegal ? hitPlateau : -1);

        PlateauPieceView.Instance?.SetSelectedOverlay(selected, count, max, legal.Count == 0);

        if (inputs.ButtonBDown)
        {
            Cancel();
            return;
        }

        if (inputs.RightMainTriggerDown)
        {
            pressedPiece = mine;
            bool onGap = mine == null && overLegal;
            pressedPlateau = onGap ? hitPlateau : -1;
            pressedEdge = onGap ? hitCandidateEdge : -1;
        }
        else if (inputs.RightMainTriggerUp)
        {
            if (pressedPiece != null && pressedPiece == mine)
            {
                if (pressedPiece == selected)
                {
                    Cancel();                   // pressing the selected piece again lets it go
                }
                else
                {
                    Select(mine);               // straight from one of your pieces to another
                }
            }
            else if (pressedPlateau >= 0 && pressedPlateau == hitPlateau)
            {
                Send(game, pressedPlateau, pressedEdge);
            }

            pressedPiece = null;
            pressedPlateau = -1;
            pressedEdge = -1;
        }
    }

    /// <summary>
    /// plateauRules.md: "they can't choose a number higher than the number of pieces on that
    /// particular plateau" — that is this stack's own size, per (plateau, owner, kind), not the
    /// plateau's total across every player. Bridges move one at a time.
    /// </summary>
    int MaxCount()
    {
        if (selected == null)
        {
            return 1;
        }
        if (selected.IsPlacedBridge || (PieceKind)selected.kind == PieceKind.Bridge)
        {
            return 1;
        }
        return Mathf.Max(1, selected.count);
    }

    void StepCount(int max)
    {
        if (max <= 1)
        {
            detent = 0;
            return;
        }

        // Vertical, not horizontal: rightJoystick.x's Editor keyboard fallback is bound to A and D,
        // and A is BoardAnchor's re-align. The vertical axis falls back to the arrow keys and W/S,
        // neither of which anything else reads.
        float axis = inputs.rightJoystick.y;
        float magnitude = Mathf.Abs(axis);

        if (magnitude < JoystickOff)
        {
            detent = 0;
            return;
        }

        if (magnitude < JoystickOn)
        {
            return;                              // inside the hysteresis band
        }

        int direction = axis > 0f ? 1 : -1;

        if (detent != direction)
        {
            detent = direction;
            Step(direction, max);
            nextStepAt = Time.unscaledTime + FirstRepeat;
        }
        else if (Time.unscaledTime >= nextStepAt)
        {
            Step(direction, max);
            nextStepAt = Time.unscaledTime + RepeatInterval;
        }
    }

    // Clamp, never wrap. A 6 -> 1 wrap on an accidental extra nudge silently drops five pieces
    // out of the move.
    void Step(int direction, int max) => count = Mathf.Clamp(count + direction, 1, max);

    void Select(PlateauPieceTag tag)
    {
        ClearHighlights();

        selected = tag;
        state = State.Selected;
        count = 1;                               // start at one: a mis-trigger moves a single piece
        detent = 0;
        armedEpoch = PlateauGame.Instance != null ? PlateauGame.Instance.boardEpoch.Value : 0;

        if (selected != null && selected.tint != null)
        {
            selected.tint.SetHighlight(hoverTint, hoverStrength);
        }

        RecomputeLegal(PlateauGame.Instance);
    }

    /// <summary>
    /// Commit the move. <paramref name="destinationEdge"/> is the bar the click landed on, or -1
    /// when it landed on the plateau instead — bridges travel as a gap index, everything else as a
    /// destination plateau.
    /// </summary>
    void Send(PlateauGame game, int destination, int destinationEdge)
    {
        if (Time.unscaledTime < sendLockUntil || selected == null)
        {
            return;
        }

        bool isBridge = selected.IsPlacedBridge || (PieceKind)selected.kind == PieceKind.Bridge;
        if (isBridge)
        {
            int movingEdge = selected.IsPlacedBridge ? selected.edge : -1;
            int edge = destinationEdge;

            // destinationEdge < 0 means the click landed on the plateau, not on one of its bars.
            // Name the gap here rather than on the server: the client is the side that knows which
            // bar was tinted, and resolving in one place means the two can never pick differently.
            if (edge < 0 &&
                (!game.TryBuildView(seat, movingEdge, out PlateauMoveRules.View view) ||
                 !PlateauMoveRules.TryResolveBridgeEdge(view, destination, out edge)))
            {
                return;                      // deliberately BEFORE the lockout: a failed resolve is
            }                                // not a move, and must not eat the player's next second

            sendLockUntil = Time.unscaledTime + SendLockout;

            // No local prediction — see the note below.
            if (selected.IsPlacedBridge)
            {
                game.RequestMoveBridgeServerRpc(selected.edge, (byte)edge, armedEpoch);
            }
            else
            {
                game.RequestPlaceBridgeServerRpc(selected.plateau, (byte)edge, armedEpoch);
            }

            Cancel();
            return;
        }

        sendLockUntil = Time.unscaledTime + SendLockout;

        // No local prediction. This project only predicts state a client holds an exclusive
        // server-granted lock on (RoomAnchor.worldHolder); there is no such lock for a move, and a
        // piece that snaps back is worse than the tick of latency nobody notices.
        game.RequestMoveServerRpc(selected.plateau, selected.kind, (byte)count,
                                  (byte)destination, armedEpoch);
        Cancel();
    }

    /// <summary>
    /// The plateau, seat and kind of the currently selected stack, for the spawn menu's "-" key.
    /// False when nothing is selected, or the selection is a placed bridge -- which sits on an
    /// edge, not "on a plateau" the way plateauRules.md and that key mean it.
    /// </summary>
    public bool TryGetSelectedStack(out int plateau, out int seat, out int kind)
    {
        plateau = seat = kind = -1;
        if (selected == null || selected.IsPlacedBridge)
        {
            return false;
        }
        plateau = selected.plateau;
        seat = selected.seat;
        kind = selected.kind;
        return true;
    }

    void Cancel()
    {
        ClearHighlights();
        PlateauPieceView.Instance?.ClearSelectedOverlay();

        state = State.Idle;
        selected = null;
        pressedPiece = null;
        pressedPlateau = -1;
        pressedEdge = -1;
        pressedIdlePlateau = -1;
        hoveredLegalPlateau = -1;
        detent = 0;
        count = 1;
    }

    // ------------------------------------------------------------------ legality and highlights

    void RecomputeLegal(PlateauGame game)
    {
        // Clear the tints FIRST — ClearPlateauTints walks `legal` to find what it lit up, so
        // emptying that list before calling it would strand the previous highlight on the board.
        ClearPlateauTints();

        PlateauBoard board = PlateauBoard.Instance;
        if (game == null || board == null || selected == null)
        {
            return;
        }

        // Relocating a bridge seeds the network from the two plateaus that bridge currently spans,
        // rather than from the central plateau. The bridge is NOT lifted out of the graph: the gap
        // it sits in stays occupied, and everything on the far side of it stays in the system. See
        // bridgeMovementUpdate.md.
        int movingEdge = selected.IsPlacedBridge ? selected.edge : -1;
        if (!game.TryBuildView(seat, movingEdge, out PlateauMoveRules.View view))
        {
            return;
        }

        PieceKind kind = selected.IsPlacedBridge ? PieceKind.Bridge : (PieceKind)selected.kind;
        // A laid bridge is not standing on a plateau; the bridge rules ignore the origin anyway,
        // but the call still wants a valid index.
        int from = selected.IsPlacedBridge ? board.CentralPlateau : selected.plateau;

        PlateauMoveRules.LegalDestinations(view, kind, from, legal);

        if (kind == PieceKind.Bridge)
        {
            PlateauMoveRules.CandidateBridgeEdges(view, candidateEdges);
        }

        ApplyLegalHighlights(game, board);
    }

    void ApplyLegalHighlights(PlateauGame game, PlateauBoard board)
    {
        for (int i = 0; i < legal.Count; i++)
        {
            PlateauTint tint = board.TintFor(legal[i]);
            if (tint != null)
            {
                tint.SetHighlight(legalTint, legalStrength);
            }
        }

        // Faintly mark every gap this bridge could land in, so the plateau-to-gap step is visible
        // rather than magic.
        for (int i = 0; i < candidateEdges.Count; i++)
        {
            Transform spot = game.SpotForEdge(candidateEdges[i]);
            if (spot == null)
            {
                continue;
            }
            PlateauTint tint = spot.GetComponent<PlateauTint>();
            if (tint == null)
            {
                tint = spot.gameObject.AddComponent<PlateauTint>();
            }
            tint.Capture();
            tint.SetHighlight(legalTint, candidateStrength);
            spotTints.Add(tint);
        }
    }

    /// <summary>
    /// The plateau a Bridge Spot hit actually targets: the endpoint of its edge that lies OUTSIDE
    /// this selection's current bridge network -- exactly the plateau
    /// PlateauMoveRules.BridgeDestinations would have added to `legal` for this same edge. Resolves
    /// only when the edge is one of the already-cached candidates, so an occupied, unreachable or
    /// irrelevant spot is inert.
    ///
    /// This now only feeds the HIGHLIGHT: the destination actually sent is the edge itself, carried
    /// as pressedEdge, so a mis-pick here can no longer put a bridge in the wrong gap.
    /// </summary>
    int ResolveBridgeSpotPlateau(PlateauGame game, int edge)
    {
        if (!candidateEdges.Contains(edge))
        {
            return -1;
        }
        if (!game.TryGetEdgeEnds(edge, out int a, out int b))
        {
            return -1;
        }
        if (legal.Contains(a))
        {
            return a;
        }
        if (legal.Contains(b))
        {
            return b;
        }
        return -1;                          // should not happen for a genuine candidate edge
    }

    void SetHoveredLegal(int plateau)
    {
        if (hoveredLegalPlateau == plateau)
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;
        if (board == null)
        {
            hoveredLegalPlateau = plateau;
            return;
        }

        if (hoveredLegalPlateau >= 0)
        {
            PlateauTint previous = board.TintFor(hoveredLegalPlateau);
            if (previous != null)
            {
                previous.SetHighlight(legalTint, legalStrength);
            }
        }

        hoveredLegalPlateau = plateau;

        if (hoveredLegalPlateau >= 0)
        {
            PlateauTint tint = board.TintFor(hoveredLegalPlateau);
            if (tint != null)
            {
                tint.SetHighlight(legalTint, legalHoverStrength);
            }
        }
    }

    void SetHover(PlateauPieceTag tag)
    {
        if (hovered == tag)
        {
            return;
        }

        if (hovered != null && hovered != selected && hovered.tint != null)
        {
            hovered.tint.ClearHighlight();
        }

        hovered = tag;

        if (hovered != null && hovered.tint != null)
        {
            hovered.tint.SetHighlight(hoverTint, hoverStrength);
        }
    }

    void ClearHighlights()
    {
        if (hovered != null && hovered.tint != null)
        {
            hovered.tint.ClearHighlight();
        }
        hovered = null;

        if (selected != null && selected.tint != null)
        {
            selected.tint.ClearHighlight();
        }

        ClearPlateauTints();
        ClearPlateauSelection();
    }

    // ------------------------------------------------------------------ plateau selection

    /// <summary>The spawn menu's Gemheart/Chasmfiend keys read this, not `selected` — see the class
    /// doc comment on selectedPlateau.</summary>
    public bool TryGetSelectedPlateau(out int plateau)
    {
        plateau = selectedPlateau;
        return state == State.PlateauSelected && selectedPlateau >= 0;
    }

    void SelectPlateau(int plateau)
    {
        ClearHighlights();               // drops any piece hover/selection and legal-destination tints

        state = State.PlateauSelected;
        selectedPlateau = plateau;

        UpdateSelectedPlateauPulse();    // immediate feedback; UpdatePlateauSelected keeps it pulsing
    }

    /// <summary>Re-applies the selected plateau's tint with a gentle sine pulse on top of
    /// plateauSelectStrength, so it keeps reading clearly on a busy board instead of blending into
    /// a static highlight.</summary>
    void UpdateSelectedPlateauPulse()
    {
        PlateauBoard board = PlateauBoard.Instance;
        PlateauTint tint = board != null ? board.TintFor(selectedPlateau) : null;
        if (tint == null)
        {
            return;
        }
        float pulse = plateauSelectPulseDepth * Mathf.Sin(Time.unscaledTime * plateauSelectPulseSpeed);
        tint.SetHighlight(plateauSelectTint, Mathf.Clamp01(plateauSelectStrength + pulse));
    }

    void DeselectPlateau()
    {
        ClearPlateauSelection();
        state = State.Idle;
    }

    /// <summary>Clears both the selected plateau's own tint and any leftover hover tint. Folded into
    /// ClearHighlights() so Select()/Cancel()/a scene change all pick it up for free.</summary>
    void ClearPlateauSelection()
    {
        PlateauBoard board = PlateauBoard.Instance;
        if (selectedPlateau >= 0 && board != null)
        {
            PlateauTint tint = board.TintFor(selectedPlateau);
            if (tint != null)
            {
                tint.ClearHighlight();
            }
        }
        selectedPlateau = -1;

        SetHoverPlateau(-1);
    }

    /// <summary>
    /// A light preview on whatever plateau the beam is over, the same idiom as SetHover for pieces.
    /// Never overwrites the selected plateau's own (stronger) tint with this fainter one.
    /// </summary>
    void SetHoverPlateau(int plateau)
    {
        if (hoveredPlateau == plateau)
        {
            return;
        }

        PlateauBoard board = PlateauBoard.Instance;

        if (hoveredPlateau >= 0 && hoveredPlateau != selectedPlateau && board != null)
        {
            PlateauTint previous = board.TintFor(hoveredPlateau);
            if (previous != null)
            {
                previous.ClearHighlight();
            }
        }

        hoveredPlateau = plateau;

        if (hoveredPlateau >= 0 && hoveredPlateau != selectedPlateau && board != null)
        {
            PlateauTint tint = board.TintFor(hoveredPlateau);
            if (tint != null)
            {
                tint.SetHighlight(hoverTint, hoverStrength);
            }
        }
    }

    void ClearPlateauTints()
    {
        PlateauBoard board = PlateauBoard.Instance;
        if (board != null)
        {
            for (int i = 0; i < legal.Count; i++)
            {
                PlateauTint tint = board.TintFor(legal[i]);
                if (tint != null)
                {
                    tint.ClearHighlight();
                }
            }
        }
        legal.Clear();

        for (int i = 0; i < spotTints.Count; i++)
        {
            if (spotTints[i] != null)
            {
                spotTints[i].ClearHighlight();
            }
        }
        spotTints.Clear();
        candidateEdges.Clear();
        hoveredLegalPlateau = -1;
    }

    // ------------------------------------------------------------------ binding

    /// <summary>
    /// Re-resolve everything this scene owns. A game switch is LoadSceneMode.Single and destroys
    /// the rig, the Input Reader and the Menu Manager — the same contract PlayerControls.BindToScene
    /// follows.
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
        }

        if (spawnMenu == null)
        {
            spawnMenu = FindFirstObjectByType<PlateauSpawnMenu>();
        }

        if (beam == null)
        {
            GameObject rig = GameObject.Find("XRRig");
            Transform pointer = rig != null ? HierarchyUtils.FindDescendant(rig.transform, "Pointer") : null;
            beam = pointer != null ? pointer.GetComponent<PointerBeam>() : null;
        }

        // The pointer is switched off outside the menu unless the scene asked for it to stay on.
        // Honour that flag here too, so a scene that sets it does not also depend on the Pointer
        // instance having been left active in the Hierarchy.
        if (beam != null && menu != null && menu.keepPointerAlwaysOn && !beam.gameObject.activeSelf)
        {
            beam.gameObject.SetActive(true);
        }

        return inputs != null && beam != null;
    }
}
