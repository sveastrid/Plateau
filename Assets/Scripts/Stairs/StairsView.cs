using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// Everything Stairs draws, rebuilt from StairsGame's replicated state.
///
/// No piece is a NetworkObject. Towers, pawns, supply stacks and captured piles are local visuals
/// reconciled from the lists on StairsGame — the same choice Chasms made, and for the same reason:
/// DefaultNetworkPrefabs.asset is global by construction under ForceSamePrefabs, and 160 steps would
/// be 160 spawn messages besides.
///
/// Everything hangs under `Stairs Root`, which is a direct child of `World Root` at identity, so the
/// world grab moves, turns and rescales all of it for free and every position here is a plain
/// frame-local vector that never needs recomputing. It is deliberately NOT under `Board`, whose
/// (2, 0.02, 2) would flatten every piece parented to it.
///
/// Order 20: after RoomContent (15) has put the frame where it goes this frame, and before
/// StairsSelection (25) reads the objects to highlight them. The per-viewer label billboard is in
/// LateUpdate, so it sees the final frame pose whatever else moved.
/// </summary>
[DefaultExecutionOrder(20)]
[DisallowMultipleComponent]
public class StairsView : MonoBehaviour
{
    public static StairsView Instance { get; private set; }

    [Header("Prefabs (Assets/Prefabs/Stairs/)")]
    [Tooltip("GamePiece.prefab — one pawn per seat. Its mesh material is swapped per seat.")]
    public GameObject pawnPrefab;
    [Tooltip("Step1.prefab then Step2.prefab, indexed by seat. Steps1/Steps2 are the seat colours.")]
    public GameObject[] stepPrefabs = new GameObject[StairsConst.Seats];

    [Header("Materials (Assets/Materials/Stairs/)")]
    [Tooltip("Player1.mat then Player2.mat, indexed by seat. Assigned to sharedMaterial, which " +
             "points the renderer at the asset rather than instancing it the way .material would.")]
    public Material[] pawnMaterials = new Material[StairsConst.Seats];

    // --- Console layout, in console-local metres at board scale 1. The console's +Z faces the
    // board, so +X is the player's right. These are the tuning knobs for the whole player area;
    // nothing else in Stairs has a hand-placed number in it.
    const float SupplyFirstX = -0.84f;      // leftmost supply stack
    const float SupplyStackPitch = 0.24f;   // one cell pitch, so the stacks read as board-sized
    const float PawnHomeX = 0.12f;          // where a pawn waits during Setup
    const float CapturedX = 0.40f;
    const float EndTurnX = 0.80f;
    const float StatusX = 0f;
    const float StatusY = 0.42f;            // above the supply stacks, which top out around 0.30

    const float EndTurnWidth = 0.34f;
    const float EndTurnDepth = 0.16f;
    const float EndTurnHeight = 0.02f;

    // TextMeshPro's 3D metrics: a line is roughly fontSize/10 units tall, so these are chosen for a
    // ~3.5 cm status line and a ~2.8 cm key label at board scale 1.
    const float StatusFontSize = 3.5f;
    const float StatusScale = 0.1f;
    const float KeyFontSize = 3.5f;
    const float KeyScale = 0.08f;
    static readonly Vector2 LabelRect = new Vector2(16f, 4f);

    /// <summary>Metres above a tower's top face for its height label. Small: it has to read as
    /// sitting ON the tower, not floating over it.</summary>
    const float LabelLift = 0.004f;

    /// <summary>How many captured tiles are actually drawn. The count in the status line is the
    /// truth; a capture of a very tall tower would otherwise build a pillar taller than the board.</summary>
    const int CapturedVisualCap = 20;

    StairsBoard board;
    StairsGame game;
    StairsGame subscribed;

    Transform pieces;                       // towers and placed pawns
    Transform labels;                       // tower height numbers, see BuildLabel for why separate
    Transform[] consoles = new Transform[StairsConst.Seats];
    Transform[] supplyRoots = new Transform[StairsConst.Seats];
    Transform[] capturedRoots = new Transform[StairsConst.Seats];
    Transform[] pawnHomes = new Transform[StairsConst.Seats];
    TMP_Text[] statusLabels = new TMP_Text[StairsConst.Seats];
    Transform[] endTurnKeys = new Transform[StairsConst.Seats];
    StairsTint[] endTurnTints = new StairsTint[StairsConst.Seats];

    readonly List<GameObject>[] towerSteps = new List<GameObject>[StairsConst.CellCount];
    readonly int[] towerOwnerShown = new int[StairsConst.CellCount];
    readonly TMP_Text[] towerLabels = new TMP_Text[StairsConst.CellCount];

    readonly List<GameObject>[] supplySteps = new List<GameObject>[StairsConst.Seats];
    readonly List<GameObject>[] capturedSteps = new List<GameObject>[StairsConst.Seats];
    readonly GameObject[] pawns = new GameObject[StairsConst.Seats];
    readonly StairsTint[] pawnTints = new StairsTint[StairsConst.Seats];

    TMP_Text labelTemplate;                 // the Height child on stepPrefabs[0], cloned per tower
    readonly Color[] labelColors = new Color[StairsConst.Seats];

    float stepThickness = 0.03f;
    float labelScale = 0.2f;
    Camera viewer;
    bool dirty = true;
    bool scaffolded;

    readonly StringBuilder text = new StringBuilder(96);

    /// <summary>Frame-local thickness of one step. Read from the prefab, never assumed.</summary>
    public float StepThickness => stepThickness;

    public bool IsReady => board != null && board.IsBaked && scaffolded;

    void Awake()
    {
        Instance = this;

        board = GetComponent<StairsBoard>();
        if (board == null)
        {
            board = FindAnyObjectByType<StairsBoard>();
        }
        if (board != null)
        {
            board.EnsureBaked();
        }

        MeasureFromPrefabs();
        BuildScaffolding();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void MeasureFromPrefabs()
    {
        GameObject step = StepPrefab(0);
        if (step == null)
        {
            Debug.LogError("StairsView: stepPrefabs is not assigned on '" + name + "'. No tower, " +
                           "supply stack or captured pile can be built. It wants Step1.prefab then " +
                           "Step2.prefab, in seat order.");
            return;
        }

        // The step is a unit cube scaled flat, so its frame-local thickness is that scale times the
        // mesh height. Measured rather than written down: the two have to agree, and the prefab is
        // the one somebody will edit.
        Renderer r = step.GetComponent<Renderer>();
        float meshHeight = r != null ? r.localBounds.size.y : 1f;
        stepThickness = step.transform.localScale.y * meshHeight;
        labelScale = step.transform.localScale.x;

        // The height number is cloned from the prefab's own Height child rather than built from
        // code, so its font, size and colour stay where an author can see them.
        Transform authored = HierarchyUtils.FindDescendant(step.transform, StairsConst.StepLabelName);
        labelTemplate = authored != null ? authored.GetComponent<TMP_Text>() : null;

        if (labelTemplate == null)
        {
            Debug.LogWarning("StairsView: Step1.prefab has no '" + StairsConst.StepLabelName +
                             "' TextMeshPro child, so towers will not show their height.");
        }

        for (int s = 0; s < StairsConst.Seats; s++)
        {
            GameObject prefab = StepPrefab(s);
            Transform child = prefab != null
                ? HierarchyUtils.FindDescendant(prefab.transform, StairsConst.StepLabelName)
                : null;
            TMP_Text tmp = child != null ? child.GetComponent<TMP_Text>() : null;
            labelColors[s] = tmp != null ? tmp.color : Color.white;
        }
    }

    // ------------------------------------------------------------------ the fixed furniture

    void BuildScaffolding()
    {
        if (board == null || !board.IsBaked)
        {
            return;
        }

        pieces = NewChild(transform, "Pieces", Vector3.zero, Quaternion.identity);
        labels = NewChild(transform, "Labels", Vector3.zero, Quaternion.identity);

        for (int seat = 0; seat < StairsConst.Seats; seat++)
        {
            Transform console = NewChild(transform, "Console " + seat,
                                         board.ConsoleOrigin(seat), board.ConsoleRotation(seat));
            consoles[seat] = console;

            supplyRoots[seat] = NewChild(console, "Supply", Vector3.zero, Quaternion.identity);
            capturedRoots[seat] = NewChild(console, "Captured", Vector3.zero, Quaternion.identity);
            pawnHomes[seat] = NewChild(console, "Home", new Vector3(PawnHomeX, 0f, 0f), Quaternion.identity);

            statusLabels[seat] = BuildStatusLabel(console);
            BuildEndTurnKey(console, seat);

            supplySteps[seat] = new List<GameObject>();
            capturedSteps[seat] = new List<GameObject>();
        }

        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            towerSteps[cell] = new List<GameObject>();
            towerOwnerShown[cell] = StairsConst.NoSeat;
        }

        scaffolded = true;
    }

    static Transform NewChild(Transform parent, string name, Vector3 localPosition, Quaternion localRotation)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = localRotation;
        go.transform.localScale = Vector3.one;
        return go.transform;
    }

    /// <summary>
    /// The line of text over a player's console, standing up and facing them.
    ///
    /// Built from code rather than authored because there is no console prefab to author it in — the
    /// console is derived from the grid so that a board of a different size still gets one in the
    /// right place. It is a sibling of everything else on the console and never a child of a scaled
    /// object; see BuildLabel.
    /// </summary>
    TMP_Text BuildStatusLabel(Transform console)
    {
        GameObject go = new GameObject("Status");
        RectTransform rt = go.AddComponent<RectTransform>();
        TextMeshPro tmp = go.AddComponent<TextMeshPro>();

        rt.SetParent(console, false);
        rt.sizeDelta = LabelRect;
        rt.localScale = Vector3.one * StatusScale;
        rt.localPosition = new Vector3(StatusX, StatusY, 0f);
        // The console's +Z faces the board, so the reader is at -Z: turn the text round to meet them.
        rt.localRotation = Quaternion.Euler(0f, 180f, 0f);

        tmp.fontSize = StatusFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.text = "";

        return tmp;
    }

    void BuildEndTurnKey(Transform console, int seat)
    {
        Transform key = NewChild(console, "End Turn", new Vector3(EndTurnX, 0f, 0f), Quaternion.identity);
        StairsPieceTag.Attach(key.gameObject, StairsPieceRole.EndTurnKey, seat, StairsConst.NoCell);

        GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        slab.name = "Slab";
        slab.transform.SetParent(key, false);
        slab.transform.localPosition = new Vector3(0f, EndTurnHeight * 0.5f, 0f);
        slab.transform.localScale = new Vector3(EndTurnWidth, EndTurnHeight, EndTurnDepth);

        Renderer slabRenderer = slab.GetComponent<Renderer>();
        GameObject stepPrefab = StepPrefab(seat);
        Renderer stepRenderer = stepPrefab != null ? stepPrefab.GetComponent<Renderer>() : null;
        if (slabRenderer != null && stepRenderer != null)
        {
            // The player's own step colour, so a glance says whose key it is. sharedMaterial, not
            // material: the instancing accessor would leak one material per console per scene load.
            slabRenderer.sharedMaterial = stepRenderer.sharedMaterial;
        }

        endTurnTints[seat] = StairsTint.Attach(slab);

        // A sibling of the slab, not a child: the slab is scaled (0.34, 0.02, 0.16), and a rotated
        // child of a non-uniformly scaled parent is sheared by Unity. See BuildLabel.
        GameObject go = new GameObject("Label");
        RectTransform rt = go.AddComponent<RectTransform>();
        TextMeshPro tmp = go.AddComponent<TextMeshPro>();

        rt.SetParent(key, false);
        rt.sizeDelta = LabelRect;
        rt.localScale = Vector3.one * KeyScale;
        rt.localPosition = new Vector3(0f, EndTurnHeight + LabelLift, 0f);
        // Lying on the slab, reading face-up. -90 about X, not +90: +90 points a transform's
        // forward at the floor, which would put the text face-down under the key.
        rt.localRotation = Quaternion.Euler(-90f, 0f, 0f);

        tmp.fontSize = KeyFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.text = "End Turn";

        endTurnKeys[seat] = key;
        key.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ reconciling

    void Update()
    {
        StairsGame current = StairsGame.Instance;
        if (current != subscribed)
        {
            if (subscribed != null)
            {
                subscribed.BoardChanged -= MarkDirty;
            }
            subscribed = current;
            if (subscribed != null)
            {
                subscribed.BoardChanged += MarkDirty;
            }
            dirty = true;
        }
        game = current;

        if (!scaffolded)
        {
            // The board can finish baking a frame after this component woke up — a scene load and a
            // Netcode spawn do not have to land together.
            BuildScaffolding();
        }

        if (!dirty || !IsReady || game == null)
        {
            return;
        }

        dirty = false;
        Reconcile();
    }

    void OnDisable()
    {
        if (subscribed != null)
        {
            subscribed.BoardChanged -= MarkDirty;
            subscribed = null;
        }
    }

    void MarkDirty()
    {
        // A dirty flag, not a diff. A NetworkList's initial contents arrive with no per-element
        // change events at all, so the only thing that works for both the first sync and every later
        // delta is to reconcile the whole board once — which is also what makes a reset, 64 element
        // writes in one frame, cost one rebuild rather than 64.
        dirty = true;
    }

    void Reconcile()
    {
        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            ReconcileTower(cell);
        }

        for (int seat = 0; seat < StairsConst.Seats; seat++)
        {
            ReconcilePawn(seat);
            ReconcileSupply(seat);
            ReconcileCaptured(seat);
            ReconcileConsole(seat);
        }
    }

    void ReconcileTower(int cell)
    {
        StairsTower tower = game.TowerAt(cell);
        List<GameObject> steps = towerSteps[cell];

        // A tower only ever changes hands by being captured, which empties it — so an owner change
        // means every step in it is the wrong colour and the cheap path is to start again.
        if (towerOwnerShown[cell] != tower.Owner)
        {
            ClearList(steps);
            towerOwnerShown[cell] = tower.Owner;
        }

        while (steps.Count > tower.height)
        {
            Destroy(steps[steps.Count - 1]);
            steps.RemoveAt(steps.Count - 1);
        }

        Vector3 surface = board.CellSurface(cell);

        while (steps.Count < tower.height)
        {
            GameObject step = BuildStep(tower.Owner, pieces, StairsPieceRole.TowerStep, tower.Owner, cell);
            if (step == null)
            {
                break;
            }
            step.transform.localPosition = surface + Vector3.up * ((steps.Count + 0.5f) * stepThickness);
            steps.Add(step);
        }

        UpdateTowerLabel(cell, tower, surface);
    }

    /// <summary>
    /// The number on top of a tower — stepsRules.md has no such thing, but a stack of identical
    /// tiles is unreadable at a glance across a real table.
    ///
    /// **The label is not a child of the step it sits on.** Step1/Step2 are scaled (0.2, 0.03, 0.2),
    /// and Unity shears a rotated child of a non-uniformly scaled parent — so a number that turns to
    /// face each player would squash and skew as it turned. Parenting it to `Labels`, which is at
    /// identity under `Stairs Root`, keeps the scale uniform and the rotation free. The authored
    /// Height child on the prefab is the template it is cloned from and is switched off on every
    /// instantiated step.
    /// </summary>
    void UpdateTowerLabel(int cell, StairsTower tower, Vector3 surface)
    {
        if (labelTemplate == null)
        {
            return;
        }

        if (tower.IsEmpty)
        {
            if (towerLabels[cell] != null)
            {
                towerLabels[cell].gameObject.SetActive(false);
            }
            return;
        }

        TMP_Text label = towerLabels[cell];
        if (label == null)
        {
            GameObject clone = Instantiate(labelTemplate.gameObject, labels, false);
            clone.name = "Height " + cell;
            label = clone.GetComponent<TMP_Text>();
            label.rectTransform.localScale = Vector3.one * labelScale;
            towerLabels[cell] = label;
        }

        label.gameObject.SetActive(true);
        label.rectTransform.localPosition =
            surface + Vector3.up * (tower.height * stepThickness + LabelLift);
        label.color = labelColors[Mathf.Clamp(tower.Owner, 0, StairsConst.Seats - 1)];
        label.SetText("{0}", tower.height);
    }

    void ReconcilePawn(int seat)
    {
        StairsSeat state = game.SeatState(seat);

        if (!state.IsOccupied && !state.HasPawnOnBoard)
        {
            // An empty seat shows no pawn at all, so "nobody is sitting there" is visible from the
            // table rather than only in the status line.
            if (pawns[seat] != null)
            {
                Destroy(pawns[seat]);
                pawns[seat] = null;
                pawnTints[seat] = null;
            }
            return;
        }

        if (pawns[seat] == null)
        {
            pawns[seat] = BuildPawn(seat);
            if (pawns[seat] == null)
            {
                return;
            }
            pawnTints[seat] = StairsTint.Attach(pawns[seat]);
        }

        Transform pawn = pawns[seat].transform;

        // StairsSelection resolves the cell under the pointer straight off the tag, so a pawn that
        // has moved and a tag that still names the old square would light the wrong cell.
        StairsPieceTag tag = pawns[seat].GetComponent<StairsPieceTag>();
        if (tag != null)
        {
            tag.cell = state.pawnCell;
        }

        if (state.HasPawnOnBoard)
        {
            if (pawn.parent != pieces)
            {
                pawn.SetParent(pieces, false);
            }
            pawn.localPosition = PawnPoint(state.pawnCell);
            pawn.localRotation = board.ConsoleRotation(seat);   // facing across the board, as placed
        }
        else
        {
            if (pawn.parent != pawnHomes[seat])
            {
                pawn.SetParent(pawnHomes[seat], false);
            }
            pawn.localPosition = Vector3.zero;
            pawn.localRotation = Quaternion.identity;
        }
    }

    void ReconcileSupply(int seat)
    {
        List<GameObject> steps = supplySteps[seat];
        int want = game.SeatState(seat).IsOccupied ? game.SeatState(seat).supply : 0;

        while (steps.Count > want)
        {
            Destroy(steps[steps.Count - 1]);
            steps.RemoveAt(steps.Count - 1);
        }

        int perStack = Mathf.Max(1, Mathf.CeilToInt(StairsConst.StepsPerPlayer / (float)StairsConst.SupplyStacks));

        while (steps.Count < want)
        {
            GameObject step = BuildStep(seat, supplyRoots[seat], StairsPieceRole.SupplyStep, seat,
                                        StairsConst.NoCell);
            if (step == null)
            {
                break;
            }

            int index = steps.Count;
            int stack = Mathf.Min(index / perStack, StairsConst.SupplyStacks - 1);
            int level = index - stack * perStack;

            step.transform.localPosition = new Vector3(
                SupplyFirstX + stack * SupplyStackPitch,
                (level + 0.5f) * stepThickness,
                0f);

            steps.Add(step);
        }
    }

    void ReconcileCaptured(int seat)
    {
        List<GameObject> steps = capturedSteps[seat];

        // Captured tiles are the OPPONENT'S, so they are drawn in the opponent's colour. Half the
        // point of a pile in front of you is that everyone can see whose tiles they were.
        int opponent = StairsConst.Opponent(seat);
        int want = Mathf.Min(game.SeatState(seat).captured, CapturedVisualCap);

        while (steps.Count > want)
        {
            Destroy(steps[steps.Count - 1]);
            steps.RemoveAt(steps.Count - 1);
        }

        while (steps.Count < want)
        {
            GameObject step = BuildStep(opponent, capturedRoots[seat], StairsPieceRole.CapturedStep,
                                        seat, StairsConst.NoCell);
            if (step == null)
            {
                break;
            }
            step.transform.localPosition =
                new Vector3(CapturedX, (steps.Count + 0.5f) * stepThickness, 0f);
            steps.Add(step);
        }
    }

    void ReconcileConsole(int seat)
    {
        TMP_Text status = statusLabels[seat];
        if (status == null)
        {
            return;
        }

        StairsSeat state = game.SeatState(seat);
        StairsPhase phase = game.CurrentPhase;

        text.Length = 0;
        text.Append(StairsConst.SeatName(seat));

        if (!state.IsOccupied)
        {
            text.Append("\nseat open");
            status.SetText(text);
            SetEndTurnVisible(seat, false);
            return;
        }

        // Plain ASCII throughout. The console labels are code-built TextMeshPro on TMP's default
        // LiberationSans SDF atlas, which is generated over ASCII only — a typographic dash or dot
        // renders as a hollow box rather than failing, which is the worst way for it to go wrong.
        text.Append(" - captured ").Append(state.captured).Append('/').Append(StairsConst.CapturesToWin);
        text.Append(" - supply ").Append(state.supply);
        text.Append('\n');

        if (phase == StairsPhase.GameOver)
        {
            int won = game.winner.Value;
            text.Append(won == StairsConst.NoSeat
                ? "game over - a draw"
                : StairsConst.SeatName(won) + " wins");
        }
        else if (!game.SeatState(StairsConst.Opponent(seat)).IsOccupied)
        {
            text.Append("waiting for a second player");
        }
        else if (!game.IsSeatToAct(seat))
        {
            text.Append("waiting for ").Append(StairsConst.SeatName(StairsConst.Opponent(seat)));
        }
        else
        {
            switch (phase)
            {
                case StairsPhase.Setup:
                    text.Append("drag your pawn onto an empty space");
                    break;
                case StairsPhase.Move:
                    text.Append("move, then End Turn");
                    break;
                case StairsPhase.Build:
                    text.Append("place ").Append(game.stepsToPlace.Value).Append(" step");
                    if (game.stepsToPlace.Value != 1)
                    {
                        text.Append('s');
                    }
                    break;
            }
        }

        status.SetText(text);

        // The key only exists while it can be pressed, so it is never a target that does nothing —
        // and it appears the moment the player's move begins, which is what asks to be pressed.
        SetEndTurnVisible(seat, phase == StairsPhase.Move && game.IsSeatToAct(seat));
    }

    void SetEndTurnVisible(int seat, bool visible)
    {
        if (endTurnKeys[seat] != null && endTurnKeys[seat].gameObject.activeSelf != visible)
        {
            endTurnKeys[seat].gameObject.SetActive(visible);
        }
    }

    // ------------------------------------------------------------------ building one object

    GameObject BuildStep(int owner, Transform parent, StairsPieceRole role, int seat, int cell)
    {
        GameObject prefab = StepPrefab(owner);
        if (prefab == null || parent == null)
        {
            return null;
        }

        GameObject step = Instantiate(prefab, parent, false);
        step.transform.localRotation = Quaternion.identity;

        // The prefab's own Height child is a template, cloned into Labels for the top of a tower and
        // never drawn on the step itself. See UpdateTowerLabel.
        Transform authoredLabel = HierarchyUtils.FindDescendant(step.transform, StairsConst.StepLabelName);
        if (authoredLabel != null)
        {
            authoredLabel.gameObject.SetActive(false);
        }

        StairsPieceTag.Attach(step, role, seat, cell);
        StairsTint.Attach(step);
        return step;
    }

    GameObject BuildPawn(int seat)
    {
        if (pawnPrefab == null)
        {
            Debug.LogError("StairsView: pawnPrefab is not assigned on '" + name + "', so no pawn " +
                           "can be placed. It wants Assets/Prefabs/Stairs/GamePiece.prefab.");
            return null;
        }

        GameObject pawn = Instantiate(pawnPrefab, pawnHomes[seat], false);
        pawn.name = "Pawn " + seat;
        pawn.transform.localPosition = Vector3.zero;
        pawn.transform.localRotation = Quaternion.identity;

        ApplyPawnMaterial(pawn, seat);
        StairsPieceTag.Attach(pawn, StairsPieceRole.Pawn, seat, StairsConst.NoCell);
        return pawn;
    }

    /// <summary>
    /// GamePiece.prefab is authored in Player1.mat. Seat 1 gets Player2.mat by pointing the renderer
    /// at the other asset — sharedMaterial, so nothing is instanced and nothing leaks. The pawn's
    /// ClearWhite disc is left alone: it is the alpha-0.035 shadow under the piece, not a colour.
    /// </summary>
    void ApplyPawnMaterial(GameObject pawn, int seat)
    {
        Material material = seat >= 0 && seat < pawnMaterials.Length ? pawnMaterials[seat] : null;
        if (material == null)
        {
            return;
        }

        foreach (Renderer r in pawn.GetComponentsInChildren<Renderer>(true))
        {
            if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("Player"))
            {
                r.sharedMaterial = material;
            }
        }
    }

    void ClearList(List<GameObject> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null)
            {
                Destroy(list[i]);
            }
        }
        list.Clear();
    }

    // ------------------------------------------------------------------ per-viewer labels

    /// <summary>
    /// Lay every height number flat on its tower, turned so the local player reads it the right way
    /// up. Two players stand on opposite sides of the board, so a fixed orientation is upside down
    /// for one of them; this is local and deliberately not networked, like the passthrough toggle.
    ///
    /// LateUpdate, so the frame has finished moving under the world grab.
    /// </summary>
    void LateUpdate()
    {
        if (!IsReady)
        {
            return;
        }

        if (viewer == null)
        {
            viewer = Camera.main;
            if (viewer == null)
            {
                return;
            }
        }

        Vector3 eye = viewer.transform.position;
        Vector3 up = transform.up;

        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            TMP_Text label = towerLabels[cell];
            if (label == null || !label.gameObject.activeSelf)
            {
                continue;
            }

            Vector3 away = Vector3.ProjectOnPlane(label.transform.position - eye, up);
            if (away.sqrMagnitude < 0.000001f)
            {
                continue;                   // looking straight down the pillar: leave it as it is
            }

            // Forward along the board's up so the text faces the ceiling, and text-up pointing away
            // from the reader, which is what makes a number on a table read correctly.
            label.transform.rotation = Quaternion.LookRotation(up, away.normalized);
        }
    }

    // ------------------------------------------------------------------ what StairsSelection needs

    public GameObject StepPrefab(int seat)
    {
        return seat >= 0 && stepPrefabs != null && seat < stepPrefabs.Length ? stepPrefabs[seat] : null;
    }

    public Material PawnMaterial(int seat)
    {
        return seat >= 0 && pawnMaterials != null && seat < pawnMaterials.Length ? pawnMaterials[seat] : null;
    }

    public GameObject PawnPrefab => pawnPrefab;

    /// <summary>Frame-local position for a step dropped on this cell — the top of what is there.</summary>
    public Vector3 PlacementPoint(int cell)
    {
        int height = game != null ? game.TowerAt(cell).height : 0;
        return board.CellSurface(cell) + Vector3.up * ((height + 0.5f) * stepThickness);
    }

    /// <summary>Frame-local position for a pawn standing on this cell — the top of the tower.</summary>
    public Vector3 PawnPoint(int cell)
    {
        int height = game != null ? game.TowerAt(cell).height : 0;
        return board.CellSurface(cell) + Vector3.up * (height * stepThickness);
    }

    /// <summary>
    /// What to light up for a cell: the top step of whatever stands on it, or the cell itself when
    /// it is bare. Highlighting the cell under a tower would be invisible.
    /// </summary>
    public StairsTint HighlightTarget(int cell)
    {
        List<GameObject> steps = StairsConst.IsCell(cell) ? towerSteps[cell] : null;
        if (steps != null && steps.Count > 0)
        {
            GameObject top = steps[steps.Count - 1];
            if (top != null)
            {
                StairsTint tint = top.GetComponent<StairsTint>();
                if (tint != null)
                {
                    return tint;
                }
            }
        }

        return board != null ? board.CellTint(cell) : null;
    }

    public StairsTint PawnTint(int seat)
    {
        return StairsConst.IsSeat(seat) ? pawnTints[seat] : null;
    }

    public GameObject PawnObject(int seat)
    {
        return StairsConst.IsSeat(seat) ? pawns[seat] : null;
    }

    public StairsTint EndTurnTint(int seat)
    {
        return StairsConst.IsSeat(seat) ? endTurnTints[seat] : null;
    }

    /// <summary>The topmost step of a player's supply — the one a drag takes.</summary>
    public GameObject SupplyTop(int seat)
    {
        if (!StairsConst.IsSeat(seat) || supplySteps[seat] == null || supplySteps[seat].Count == 0)
        {
            return null;
        }
        return supplySteps[seat][supplySteps[seat].Count - 1];
    }

    /// <summary>
    /// A dimmed copy of a piece that follows the pointer while it is being dragged.
    ///
    /// Its colliders come off and every one of its objects goes on layer Ignore Raycast, because the
    /// ghost hangs on the end of the very ray that decides where it goes — left solid, it would sit
    /// in front of the board and the drag would freeze the moment it started.
    /// </summary>
    public GameObject CreateGhost(GameObject prefab)
    {
        if (prefab == null)
        {
            return null;
        }

        GameObject ghost = Instantiate(prefab, pieces, false);
        ghost.name = "Ghost";
        ghost.transform.localRotation = Quaternion.identity;

        foreach (Collider c in ghost.GetComponentsInChildren<Collider>(true))
        {
            // Disabled *and* destroyed: Destroy is deferred to the end of the frame, so the collider
            // is still in the physics scene for the rest of this one. The layer below is what really
            // protects the ray, but a live collider on the end of it is not worth leaving to chance.
            c.enabled = false;
            Destroy(c);
        }
        foreach (Transform t in ghost.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.layer = StairsConst.IgnoreRaycastLayer;
        }

        Transform authoredLabel = HierarchyUtils.FindDescendant(ghost.transform, StairsConst.StepLabelName);
        if (authoredLabel != null)
        {
            authoredLabel.gameObject.SetActive(false);
        }

        return ghost;
    }
}
