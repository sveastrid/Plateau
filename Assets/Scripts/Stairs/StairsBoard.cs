using UnityEngine;

/// <summary>
/// The 8x8 grid, derived from the authored cells rather than authored twice.
///
/// Nothing in StairsGame.unity carries a cell index: the geometry IS the index. This reads
/// `World Root > Board > Cells`, sorts the rows by z and each row's cells by x, and hands out
/// cell 0 at the corner nearest seat 0 (-X, -Z) through cell 63 at the far corner.
///
/// **Sibling order is not grid order.** The eight cells in every row are named
/// `Cube, Cube (4), Cube (1), Cube (5), Cube (2), Cube (6), Cube (3), Cube (7)` and sit in that
/// order in the Hierarchy — the columns interleave. Reading `GetChild(i)` as column i puts the board
/// in a shuffled order that still looks plausible in play, so this sorts by position and never by
/// index. (Chasms does the opposite and takes sibling order as the plateau index; that is a
/// different board, authored differently, and the two must not be assumed to match.)
///
/// Every measurement is in **this transform's local space** — `Stairs Root`, a direct child of
/// `World Root` at identity — which is what makes it invariant under the two-grip world grab. The
/// board can be moved, turned and rescaled all game and nothing here is ever re-baked. It is
/// deliberately NOT under `Board`, which carries a non-uniform (2, 0.02, 2).
///
/// Order -10, matching PlateauBoard: bake before anything reads the table.
/// </summary>
[DefaultExecutionOrder(-10)]
[DisallowMultipleComponent]
public class StairsBoard : MonoBehaviour
{
    public static StairsBoard Instance { get; private set; }

    [Header("Scene (resolved by name if empty)")]
    [Tooltip("World Root. The content frame this game's Stairs Root hangs under.")]
    public Transform worldRoot;
    [Tooltip("World Root > Board > Cells. Its grandchildren are the 64 cells.")]
    public Transform cellsRoot;
    [Tooltip("World Root > Board. The slab the pointer ray lands on over a bare cell.")]
    public Transform boardSlab;

    [Header("Console")]
    [Tooltip("How far beyond the outermost row of cells a player's console sits, in grid pitches. " +
             "1.7 puts it about 1.30 m from the board centre at scale 1 — clear of the wall at 1.05, " +
             "and 0.7 m in front of a player standing on the 2 m PlayerRing.")]
    public float ConsolePitches = 1.7f;

    /// <summary>The eight adjacent spaces of stepsRules.md "Valid Direction", as (row, column)
    /// deltas. Diagonals included; the rules say all eight.</summary>
    static readonly int[] NeighbourRowDelta = { -1, -1, -1, 0, 0, 1, 1, 1 };
    static readonly int[] NeighbourColDelta = { -1, 0, 1, -1, 1, -1, 0, 1 };

    Vector3[] centres;              // frame-local, y on the cell's top surface
    Transform[] cells;
    StairsTint[] cellTints;
    float pitch;
    bool baked;

    public bool IsBaked => baked;

    /// <summary>The content frame itself. Everything Stairs builds is parented here.</summary>
    public Transform Frame => transform;

    /// <summary>Centre-to-centre spacing of the grid, in frame-local units. 0.25 as authored.</summary>
    public float Pitch => pitch;

    void Awake()
    {
        Instance = this;
        EnsureBaked();
    }

    void OnDestroy()
    {
        // Guarded like every other Instance singleton here: a scene switch can construct the next
        // one before destroying this one.
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>
    /// Idempotent. StairsGame's server tick can arrive in the same frame as the scene load, so
    /// anything that reads the table calls this first rather than assuming Awake has run.
    /// </summary>
    public void EnsureBaked()
    {
        if (baked)
        {
            return;
        }
        Bake();
    }

    [ContextMenu("Bake and Report")]
    public void Bake()
    {
        baked = false;
        ResolveRoots();

        if (cellsRoot == null)
        {
            Debug.LogError("StairsBoard: no '" + StairsConst.CellsName + "' under World Root, so " +
                           "there is no grid. Nothing in Stairs can be placed or moved.");
            return;
        }

        if (cellsRoot.childCount != StairsConst.Size)
        {
            Debug.LogError("StairsBoard: '" + StairsConst.CellsName + "' has " + cellsRoot.childCount +
                           " rows, expected " + StairsConst.Size + ". The grid is indexed " +
                           "row * " + StairsConst.Size + " + column and cannot be baked from this.");
            return;
        }

        centres = new Vector3[StairsConst.CellCount];
        cells = new Transform[StairsConst.CellCount];
        cellTints = new StairsTint[StairsConst.CellCount];

        // Rows first, ordered by their own z. Sorting the row transforms rather than trusting
        // Row1..Row8 to be in order costs eight comparisons and removes a rename from the list of
        // things that can quietly reverse the board.
        Transform[] rows = new Transform[StairsConst.Size];
        float[] rowZ = new float[StairsConst.Size];
        for (int i = 0; i < StairsConst.Size; i++)
        {
            rows[i] = cellsRoot.GetChild(i);
            rowZ[i] = transform.InverseTransformPoint(rows[i].position).z;
        }
        SortByKey(rows, rowZ);

        for (int r = 0; r < StairsConst.Size; r++)
        {
            Transform row = rows[r];
            if (row.childCount != StairsConst.Size)
            {
                Debug.LogError("StairsBoard: row '" + row.name + "' has " + row.childCount +
                               " cells, expected " + StairsConst.Size + ".");
                return;
            }

            Transform[] rowCells = new Transform[StairsConst.Size];
            float[] cellX = new float[StairsConst.Size];
            for (int i = 0; i < StairsConst.Size; i++)
            {
                rowCells[i] = row.GetChild(i);
                cellX[i] = transform.InverseTransformPoint(rowCells[i].position).x;
            }
            SortByKey(rowCells, cellX);

            for (int c = 0; c < StairsConst.Size; c++)
            {
                int index = Index(r, c);
                cells[index] = rowCells[c];
                centres[index] = TopCentre(rowCells[c]);
                cellTints[index] = StairsTint.Attach(rowCells[c].gameObject);
            }
        }

        pitch = Mathf.Abs(centres[Index(0, 1)].x - centres[Index(0, 0)].x);
        if (pitch < 0.0001f)
        {
            Debug.LogError("StairsBoard: the first two cells of row 0 are on top of each other, so " +
                           "the grid pitch is 0. Nothing can snap to a cell.");
            return;
        }

        // The cells themselves are scenery — they carry no tag and are never resolved off a hit.
        // A bare square is targeted by mapping the point where the ray met the board slab back onto
        // the grid, so a slab with no collider means half the board silently stops being clickable.
        if (boardSlab == null || boardSlab.GetComponent<Collider>() == null)
        {
            Debug.LogError("StairsBoard: '" + StairsConst.BoardName + "' has no collider, so the " +
                           "pointer cannot land on an empty square. Every cell with something " +
                           "stacked on it still works, which is what makes this easy to miss.");
        }

        baked = true;
    }

    void ResolveRoots()
    {
        if (worldRoot == null)
        {
            worldRoot = RoomContent.Instance != null ? RoomContent.Instance.transform : null;
        }
        if (worldRoot == null)
        {
            GameObject go = GameObject.Find("World Root");
            worldRoot = go != null ? go.transform : transform.parent;
        }
        if (worldRoot == null)
        {
            return;
        }
        if (cellsRoot == null)
        {
            cellsRoot = HierarchyUtils.FindDescendant(worldRoot, StairsConst.CellsName);
        }
        if (boardSlab == null)
        {
            boardSlab = HierarchyUtils.FindDescendant(worldRoot, StairsConst.BoardName);
        }
    }

    /// <summary>
    /// The middle of a cell's top face, in frame-local space.
    ///
    /// Taken from the renderer's LOCAL bounds and pushed through the cell's own transform, never
    /// from Renderer.bounds: that is a world-space AABB, and it grows the moment somebody yaws the
    /// board with the world grab, which would put the top surface in a different place depending on
    /// how the board happened to be turned when the bake ran.
    /// </summary>
    Vector3 TopCentre(Transform cell)
    {
        Renderer r = cell.GetComponent<Renderer>();
        Vector3 localTop = r != null
            ? r.localBounds.center + Vector3.up * r.localBounds.extents.y
            : new Vector3(0f, 0.5f, 0f);           // a Unity cube, if the cell has no renderer

        return transform.InverseTransformPoint(cell.TransformPoint(localTop));
    }

    static void SortByKey(Transform[] items, float[] keys)
    {
        // Insertion sort over eight entries. A comparison delegate here would allocate a closure
        // per bake, and this runs on a scene load on a Quest.
        for (int i = 1; i < items.Length; i++)
        {
            Transform item = items[i];
            float key = keys[i];
            int j = i - 1;
            while (j >= 0 && keys[j] > key)
            {
                items[j + 1] = items[j];
                keys[j + 1] = keys[j];
                j--;
            }
            items[j + 1] = item;
            keys[j + 1] = key;
        }
    }

    // ------------------------------------------------------------------ topology (no bake needed)

    public static int Index(int row, int col) => row * StairsConst.Size + col;
    public static int RowOf(int cell) => cell / StairsConst.Size;
    public static int ColOf(int cell) => cell % StairsConst.Size;

    /// <summary>
    /// The adjacent spaces of <paramref name="cell"/>, written into <paramref name="into"/>.
    /// Returns how many there are — 3 in a corner, 5 on an edge, 8 in the middle.
    ///
    /// Static and buffer-filling on purpose: StairsMoveRules calls this inside a loop over every
    /// cell on the board, on the server, for every move validation.
    /// </summary>
    public static int Neighbours(int cell, int[] into)
    {
        int row = RowOf(cell);
        int col = ColOf(cell);
        int n = 0;

        for (int i = 0; i < NeighbourRowDelta.Length; i++)
        {
            int r = row + NeighbourRowDelta[i];
            int c = col + NeighbourColDelta[i];
            if (r < 0 || r >= StairsConst.Size || c < 0 || c >= StairsConst.Size)
            {
                continue;
            }
            into[n++] = Index(r, c);
        }

        return n;
    }

    public static bool AreAdjacent(int a, int b)
    {
        if (a == b || !StairsConst.IsCell(a) || !StairsConst.IsCell(b))
        {
            return false;
        }
        int dr = Mathf.Abs(RowOf(a) - RowOf(b));
        int dc = Mathf.Abs(ColOf(a) - ColOf(b));
        return dr <= 1 && dc <= 1;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The middle of a cell's top surface, in frame-local space. Where a tower starts.</summary>
    public Vector3 CellSurface(int cell)
    {
        return StairsConst.IsCell(cell) && baked ? centres[cell] : Vector3.zero;
    }

    public Transform CellTransform(int cell)
    {
        return StairsConst.IsCell(cell) && baked ? cells[cell] : null;
    }

    /// <summary>The cell's own highlight, used when nothing is stacked on it.</summary>
    public StairsTint CellTint(int cell)
    {
        return StairsConst.IsCell(cell) && baked ? cellTints[cell] : null;
    }

    /// <summary>
    /// The cell a frame-local point is over, or false when it is off the grid.
    ///
    /// Rounding to the nearest lattice point rather than testing each cell's footprint is
    /// deliberate: it snaps the 1 cm gaps between cells to the nearer of the two, so a drag never
    /// dies in the cracks. A point past the outer row rounds to an index outside 0..7 and is
    /// rejected, which is what keeps a hit on the wall off the board.
    /// </summary>
    public bool TryCellAt(Vector3 framePoint, out int cell)
    {
        cell = StairsConst.NoCell;
        if (!baked)
        {
            return false;
        }

        Vector3 origin = centres[0];
        int col = Mathf.RoundToInt((framePoint.x - origin.x) / pitch);
        int row = Mathf.RoundToInt((framePoint.z - origin.z) / pitch);

        if (row < 0 || row >= StairsConst.Size || col < 0 || col >= StairsConst.Size)
        {
            return false;
        }

        cell = Index(row, col);
        return true;
    }

    // ------------------------------------------------------------------ consoles

    /// <summary>
    /// The middle of a player's console, in frame-local space: level with the board surface, a
    /// fixed distance beyond their edge of the grid.
    ///
    /// Which edge is a **board-local** fact keyed off the seat number, not off where the player
    /// happens to be standing. Seat 0 is the -Z edge and seat 1 the +Z edge, which is where
    /// PlayerRing puts the first two players (slots 0 and 6) by construction — see
    /// StairsGame.PreferredSeat, which seats people to match. The console turns with the board under
    /// the world grab, because it is part of the board.
    /// </summary>
    public Vector3 ConsoleOrigin(int seat)
    {
        if (!baked)
        {
            return Vector3.zero;
        }

        Vector3 near = centres[Index(0, 0)];
        Vector3 far = centres[Index(StairsConst.Size - 1, StairsConst.Size - 1)];
        float x = (near.x + far.x) * 0.5f;
        float y = (near.y + far.y) * 0.5f;
        float z = seat == 0 ? near.z - pitch * ConsolePitches : far.z + pitch * ConsolePitches;

        return new Vector3(x, y, z);
    }

    /// <summary>Frame-local. A console's +Z points at the board, so its +X is the player's right.</summary>
    public Quaternion ConsoleRotation(int seat)
    {
        return seat == 0 ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);
    }

    // ------------------------------------------------------------------ authoring aid

    /// <summary>
    /// Select `World Root > Stairs Root` in the Scene view before changing the board. A mis-sorted
    /// grid has to be visible rather than buried in a log: the line runs cell 0 to cell 63 in index
    /// order, so it should snake row by row from the seat 0 corner.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        if (!baked || centres == null)
        {
            return;
        }

        Gizmos.color = Color.cyan;
        for (int i = 1; i < centres.Length; i++)
        {
            Gizmos.DrawLine(transform.TransformPoint(centres[i - 1]), transform.TransformPoint(centres[i]));
        }

        Gizmos.color = Color.yellow;
        for (int seat = 0; seat < StairsConst.Seats; seat++)
        {
            Gizmos.DrawWireSphere(transform.TransformPoint(ConsoleOrigin(seat)), pitch * 0.3f);
        }
    }
}
