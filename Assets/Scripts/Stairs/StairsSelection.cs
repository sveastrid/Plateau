using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Pointing, dragging and choosing, entirely local to this client.
///
/// Two idioms, and which one applies is decided by the phase rather than by the button, so they can
/// never be ambiguous:
///
///  - **Drag** — a piece that is not on the board yet. Hover it (it lights up), hold the right
///    trigger, and it follows the pointer as a ghost snapping to the middle of whichever cell is
///    under the beam; release over a legal cell to place it. That is the pawn during Setup and a
///    supply step during Build.
///  - **Click** — a piece that is already on the board, and the End Turn key. Trigger down and up on
///    the same thing. Clicking your own pawn selects it and lights its legal moves; clicking one of
///    those moves it, and it stays selected because a multi-step move is the normal case.
///
/// No local prediction. This project only predicts state a client holds an exclusive server-granted
/// lock on (RoomAnchor.worldHolder), and there is no such lock for a move; every highlight here is a
/// hint that StairsGame re-derives from the same StairsMoveRules call before it changes anything.
///
/// Order 25, matching PlateauSelection and ControlListener: it consumes the hit PointerBeam (24)
/// produced this same frame, after StairsView (20) has reconciled the objects being lit.
/// </summary>
[DefaultExecutionOrder(25)]
[DisallowMultipleComponent]
public class StairsSelection : MonoBehaviour
{
    [Header("Scene (resolved by name if empty, and re-resolved after a game switch)")]
    public InputReader inputs;
    public MenuControl menu;
    public PointerBeam beam;

    [Header("Highlight")]
    [Tooltip("Whatever the pointer is resting on that this player could act on.")]
    public Color HoverColor = new Color(1f, 1f, 1f);
    public float HoverStrength = 0.6f;

    [Tooltip("A legal destination, or a legal place to drop the piece being dragged.")]
    public Color LegalColor = new Color(0.35f, 1f, 0.45f);
    public float LegalStrength = 0.45f;

    [Tooltip("A legal move that takes the opponent's tower. Worth telling apart from an ordinary step down.")]
    public Color CaptureColor = new Color(1f, 0.3f, 0.25f);
    public float CaptureStrength = 0.6f;

    [Tooltip("The player's own pawn while it is selected.")]
    public Color SelectedColor = new Color(1f, 0.92f, 0.3f);
    public float SelectedStrength = 0.65f;

    [Tooltip("The translucent copy that follows the pointer during a drag.")]
    public Color GhostColor = new Color(1f, 1f, 1f);
    public float GhostStrength = 0.5f;

    [Tooltip("Metres out along the beam to hold a dragged piece while it is off the board.")]
    public float OffBoardDragDistance = 1.5f;

    enum Mode
    {
        Idle,
        /// <summary>A ghost is on the end of the pointer, waiting for a cell to be released over.</summary>
        Dragging,
        /// <summary>This player's pawn is selected and its legal moves are lit.</summary>
        PawnSelected,
    }

    Mode mode = Mode.Idle;

    GameObject ghost;
    StairsPieceRole dragRole;
    /// <summary>Where a dragged TowerStep came from, or NoCell for anything else.</summary>
    int dragFromCell = StairsConst.NoCell;

    // Press on trigger down, act on trigger up, so sliding off a target cancels it — the same
    // contract MenuControl gives every key in the project.
    StairsPieceTag pressedPiece;
    int pressedCell = StairsConst.NoCell;

    StairsMoveRules.View view;
    readonly List<int> legalCells = new List<int>(StairsConst.CellCount);
    readonly List<int> captureCells = new List<int>(8);
    readonly List<StairsTint> lit = new List<StairsTint>(StairsConst.CellCount);

    void OnEnable()
    {
        SceneManager.activeSceneChanged += HandleSceneChanged;
    }

    void OnDisable()
    {
        SceneManager.activeSceneChanged -= HandleSceneChanged;
        // A game switch destroys the rig mid-gesture. Same reason WorldGrab cancels in OnDisable.
        Cancel();
    }

    void HandleSceneChanged(Scene from, Scene to)
    {
        inputs = null;
        menu = null;
        beam = null;
        Cancel();
    }

    // ------------------------------------------------------------------ the root

    void Update()
    {
        ClearHighlights();
        if (!Bind())
        {
            Cancel();
            return;
        }

        StairsGame game = StairsGame.Instance;
        StairsView pieces = StairsView.Instance;
        StairsBoard board = StairsBoard.Instance;

        if (game == null || !game.IsSpawned || pieces == null || !pieces.IsReady || board == null)
        {
            Cancel();
            return;
        }

        if (!Bind())
        {
            Cancel();
            return;
        }

        int seat = StairsGame.LocalSeat();
        if (!StairsConst.IsSeat(seat) || !Playable())
        {
            Cancel();
            return;
        }

        ResolveHit(board, out StairsPieceTag hit, out int cell);

        // The server can end this player's turn out from under them — a capture does exactly that —
        // so a selection or a drag that is no longer allowed is dropped before it can be acted on.
        if (!StillValid(game, seat))
        {
            Cancel();
        }

        HandleInput(game, pieces, seat, hit, cell);
        RefreshLegal(game, seat);
        DrawHighlights(pieces, seat, hit, cell);

        if (mode == Mode.Dragging)
        {
            MoveGhost(pieces, seat, cell);
        }
    }

    bool StillValid(StairsGame game, int seat)
    {
        switch (mode)
        {
            case Mode.PawnSelected:
                return game.PhaseOf(seat) == StairsPhase.Move && game.SeatState(seat).HasPawnOnBoard;

            case Mode.Dragging:
                switch (dragRole)
                {
                    case StairsPieceRole.Pawn:
                        return game.CanPlacePawn(seat);
                    case StairsPieceRole.SupplyStep:
                        return game.PhaseOf(seat) == StairsPhase.Build && game.StepsToPlaceOf(seat) > 0;
                    case StairsPieceRole.TowerStep:
                        return game.CanLiftStep(seat, dragFromCell);
                    default:
                        return false;
                }

            default:
                return true;
        }
    }

    // ------------------------------------------------------------------ what the beam is on

    /// <summary>
    /// What the pointer is resting on, and which cell that means.
    ///
    /// GetComponentInParent, always: a pawn can be hit on its root collider or on its ClearWhite
    /// disc, and the End Turn key's collider is on a child slab. A hit that is not one of this
    /// game's objects falls through to the board slab, whose hit point maps to the nearest cell —
    /// which is what makes a bare square a target at all, since the cells themselves are only
    /// scenery.
    /// </summary>
    void ResolveHit(StairsBoard board, out StairsPieceTag piece, out int cell)
    {
        piece = null;
        cell = StairsConst.NoCell;

        if (beam == null || !beam.HasHit || beam.Hit.collider == null)
        {
            return;
        }

        piece = beam.Hit.collider.GetComponentInParent<StairsPieceTag>();
        if (piece != null)
        {
            cell = piece.cell;
            return;
        }

        board.TryCellAt(board.Frame.InverseTransformPoint(beam.Hit.point), out cell);
    }

    // ------------------------------------------------------------------ input

    void HandleInput(StairsGame game, StairsView pieces, int seat, StairsPieceTag hit, int cell)
    {
        if (inputs.ButtonBDown)
        {
            Cancel();
            return;
        }

        if (mode == Mode.Dragging)
        {
            // Levels, not the edge: a drag that loses its trigger to anything at all should land or
            // be dropped rather than staying stuck to the pointer.
            if (!inputs.RightMainTrigger)
            {
                CommitDrag(game, seat, cell);
            }
            return;
        }

        if (inputs.RightMainTriggerDown)
        {
            pressedPiece = hit;
            pressedCell = cell;

            if (CanDrag(game, seat, hit))
            {
                BeginDrag(pieces, hit);
            }
            return;
        }

        if (inputs.RightMainTriggerUp)
        {
            // Released on the same thing it was pressed on, or the press is thrown away.
            if (pressedPiece == hit && pressedCell == cell)
            {
                Click(game, seat, hit, cell);
            }

            pressedPiece = null;
            pressedCell = StairsConst.NoCell;
        }
    }

    void Click(StairsGame game, int seat, StairsPieceTag hit, int cell)
    {
        // The End Turn key is only ever present while it can be pressed, so reaching it is enough.
        if (hit != null && hit.role == StairsPieceRole.EndTurnKey)
        {
            if (hit.seat == seat)
            {
                game.RequestEndMoveServerRpc();
            }
            Deselect();
            return;
        }

        if (game.PhaseOf(seat) != StairsPhase.Move)
        {
            return;
        }

        // Your own pawn toggles: pressing it again lets it go, the same as Chasms.
        if (hit != null && hit.role == StairsPieceRole.Pawn && hit.seat == seat)
        {
            mode = mode == Mode.PawnSelected ? Mode.Idle : Mode.PawnSelected;
            return;
        }

        if (mode == Mode.PawnSelected && legalCells.Contains(cell))
        {
            game.RequestMoveServerRpc(cell);
            // Deliberately still selected. A turn is usually several steps, and the momentum rule
            // makes the next legal set different — dropping the selection after every step would
            // mean re-clicking the pawn to take the second one.
        }
    }

    bool CanDrag(StairsGame game, int seat, StairsPieceTag piece)
    {
        if (piece == null || piece.seat != seat)
        {
            return false;
        }

        if (mode == Mode.PawnSelected)
        {
            return false;
        }

        switch (piece.role)
        {
            case StairsPieceRole.Pawn:
                return game.CanPlacePawn(seat);

            case StairsPieceRole.SupplyStep:
                return game.PhaseOf(seat) == StairsPhase.Build && game.StepsToPlaceOf(seat) > 0;

            case StairsPieceRole.TowerStep:
                // Re-laying a tile you have already placed, at any time. bugFixesStairsGame.md §3.
                return game.CanLiftStep(seat, piece.cell);

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ the drag

    void BeginDrag(StairsView pieces, StairsPieceTag piece)
    {
        // Cloned from the live piece rather than from its prefab, so the ghost is exactly what the
        // player grabbed — seat colour included, which a bare prefab would not carry. It also has to
        // be cloned BEFORE the tile is hidden below: Instantiate copies activeSelf, so a ghost
        // cloned from a deactivated tile would itself be invisible.
        ghost = pieces.CreateGhost(piece.gameObject);
        if (ghost == null)
        {
            return;
        }

        StairsTint tint = StairsTint.Attach(ghost);
        tint.SetHighlight(GhostColor, GhostStrength);

        dragRole = piece.role;
        dragFromCell = piece.role == StairsPieceRole.TowerStep ? piece.cell : StairsConst.NoCell;
        mode = Mode.Dragging;

        // Now take it off the board, so the player has one tile in hand rather than two on screen.
        // NoCell for the other two roles, which were never on the board to begin with.
        pieces.SetLiftedCell(dragFromCell);
    }

    void MoveGhost(StairsView pieces, int seat, int cell)
    {
        if (ghost == null)
        {
            return;
        }

        if (legalCells.Contains(cell))
        {
            Vector3 point = dragRole == StairsPieceRole.Pawn
                ? pieces.PawnPoint(cell)
                : pieces.PlacementPoint(cell);

            // A lifted tile is still in the tower as far as the state is concerned — only the local
            // visual came off — so its own square reads one level too tall while it is in hand.
            if (dragRole == StairsPieceRole.TowerStep && cell == dragFromCell)
            {
                point.y -= pieces.StepThickness;
            }

            ghost.transform.localPosition = point;
            return;
        }

        // Off the grid, or over a square it may not go on: keep it on the end of the beam so it is
        // visibly still in hand, rather than parking it on the last legal square it passed over and
        // reading as already placed.
        float distance = beam.HasHit ? beam.Hit.distance : OffBoardDragDistance;
        ghost.transform.position = beam.Origin + beam.Direction * distance;
    }

    void CommitDrag(StairsGame game, int seat, int cell)
    {
        if (legalCells.Contains(cell))
        {
            if (dragRole == StairsPieceRole.Pawn)
            {
                game.RequestPlacePawnServerRpc(cell);
            }
            else if (dragRole == StairsPieceRole.SupplyStep)
            {
                game.RequestPlaceStepServerRpc(cell);
            }
            else if (dragRole == StairsPieceRole.TowerStep)
            {
                game.RequestMoveStepServerRpc(dragFromCell, cell);
            }
        }

        EndDrag();
    }

    void EndDrag()
    {
        if (ghost != null)
        {
            Destroy(ghost);
            ghost = null;
        }

        // Put back whatever was taken off the board. Read through the singleton rather than a passed
        // reference because Cancel() reaches here from OnDisable and from a scene change, when the
        // view may already be gone.
        if (StairsView.Instance != null)
        {
            StairsView.Instance.SetLiftedCell(StairsConst.NoCell);
        }

        dragFromCell = StairsConst.NoCell;
        mode = Mode.Idle;
    }

    void Deselect()
    {
        mode = Mode.Idle;
    }

    void Cancel()
    {
        EndDrag();
        Deselect();
        pressedPiece = null;
        pressedCell = StairsConst.NoCell;
        legalCells.Clear();
        captureCells.Clear();
        ClearHighlights();
    }

    // ------------------------------------------------------------------ what is legal right now

    /// <summary>
    /// Recomputed every frame rather than cached against a dirty flag. It is one pass over 64 cells
    /// and at most eight rule checks, it only runs while something is selected or in hand, and it
    /// removes the whole class of bug where the lit squares are one server message out of date.
    /// </summary>
    void RefreshLegal(StairsGame game, int seat)
    {
        legalCells.Clear();
        captureCells.Clear();

        if (mode == Mode.Idle)
        {
            return;
        }

        game.FillView(ref view);

        if (mode == Mode.Dragging)
        {
            if (dragRole == StairsPieceRole.Pawn)
            {
                StairsMoveRules.CollectPawnPlacements(view, legalCells);
            }
            else
            {
                StairsMoveRules.CollectBuilds(view, seat, legalCells);
            }
            return;
        }

        int direction = game.MoveDirectionOf(seat);
        StairsMoveRules.CollectMoves(view, seat, direction, legalCells);

        for (int i = 0; i < legalCells.Count; i++)
        {
            if (StairsMoveRules.IsLegalMove(view, seat, legalCells[i], direction, out bool capture) && capture)
            {
                captureCells.Add(legalCells[i]);
            }
        }
    }

    // ------------------------------------------------------------------ highlighting

    void DrawHighlights(StairsView pieces, int seat, StairsPieceTag hit, int cell)
    {
        ClearHighlights();

        for (int i = 0; i < legalCells.Count; i++)
        {
            int c = legalCells[i];
            bool capture = captureCells.Contains(c);
            Light(pieces.HighlightTarget(c),
                  capture ? CaptureColor : LegalColor,
                  capture ? CaptureStrength : LegalStrength);
        }

        if (mode == Mode.PawnSelected)
        {
            Light(pieces.PawnTint(seat), SelectedColor, SelectedStrength);
        }

        // The hover goes on last, so pointing at one of several lit squares says which one a trigger
        // would take.
        StairsTint hovered = HoverTint(pieces, seat, hit, cell);
        if (hovered != null)
        {
            Light(hovered, HoverColor, HoverStrength);
        }
    }

    StairsTint HoverTint(StairsView pieces, int seat, StairsPieceTag hit, int cell)
    {
        StairsGame game = StairsGame.Instance;
        if (game == null)
        {
            return null;
        }

        if (hit != null)
        {
            if (hit.role == StairsPieceRole.EndTurnKey)
            {
                return hit.seat == seat ? pieces.EndTurnTint(seat) : null;
            }

            if (CanDrag(game, seat, hit))
            {
                return hit.gameObject.GetComponent<StairsTint>();
            }

            // Your own pawn, standing on the board, on your own move: the thing a click selects.
            if (hit.role == StairsPieceRole.Pawn && hit.seat == seat && game.PhaseOf(seat) == StairsPhase.Move)
            {
                return pieces.PawnTint(seat);
            }
        }

        return legalCells.Contains(cell) ? pieces.HighlightTarget(cell) : null;
    }

    void Light(StairsTint tint, Color color, float strength)
    {
        if (tint == null)
        {
            return;
        }
        tint.SetHighlight(color, strength);
        lit.Add(tint);
    }

    void ClearHighlights()
    {
        for (int i = 0; i < lit.Count; i++)
        {
            if (lit[i] != null)
            {
                lit[i].ClearHighlight();
            }
        }
        lit.Clear();
    }

    // ------------------------------------------------------------------ standing down, and binding

    /// <summary>
    /// The same three conditions PlateauSelection and ControlListener stand down on, and for the
    /// same reasons: the menu owns the right trigger while it is up and is instantiated between the
    /// player and the board; the world grab owns both hands; and a grip merely *held* means a grab
    /// is being prepared, which the gesture's own IsActive does not report until both are down.
    /// </summary>
    bool Playable()
    {
        if (menu != null && menu.IsOpen)
        {
            return false;
        }

        if (WorldGrab.IsActive)
        {
            return false;
        }

        return !inputs.LeftGrip && !inputs.RightGrip;
    }

    /// <summary>
    /// Re-resolve everything the rig owns. A game switch is LoadSceneMode.Single; since PersistentRig
    /// these are the same instances as before, but the contract is the one PlayerControls.BindToScene
    /// follows and a future scene that sources its own rig still has to work.
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

        if (beam == null)
        {
            GameObject rig = GameObject.Find("XRRig");
            Transform pointer = rig != null ? HierarchyUtils.FindDescendant(rig.transform, "Pointer") : null;
            beam = pointer != null ? pointer.GetComponent<PointerBeam>() : null;
        }

        // StairsModule.asset sets keepPointerAlwaysOn, so the pointer is live outside the menu from
        // the first frame of the scene. Honour it here too rather than depending on the Pointer
        // instance having been left active by whatever scene ran before this one.
        if (beam != null && menu != null && menu.keepPointerAlwaysOn && !beam.gameObject.activeSelf)
        {
            beam.gameObject.SetActive(true);
        }

        return inputs != null && beam != null;
    }
}
