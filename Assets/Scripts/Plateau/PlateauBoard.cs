using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// The static board: which plateaus exist, how big each one is, and which pairs of them have a
/// "faint line" between them (plateauRules.md) that a bridge can span or a parshendi can jump.
///
/// None of that is authored anywhere. ChasmGame's 41 Plateau instances and 81 Bridge Spots
/// instances carry no scripts, no ids and no neighbour lists — the bars ARE the lines, so the
/// graph is derived from their geometry here. Derived at runtime rather than baked to an asset
/// because the board is still being hand-authored and a bake step would need re-running after
/// every edit.
///
/// Order -10 so the table exists before anything reads it. EnsureBaked() is also idempotent and
/// lazy, because PlateauGame's server tick can arrive in the same frame as the scene load and
/// execution order alone is not a guarantee — latch, do not depend on ordering.
///
/// Everything is measured in WORLD ROOT LOCAL SPACE. That is the trick that makes the rest simple:
/// the numbers are invariant under the world grab, so nothing is ever re-baked when the board is
/// moved, turned or resized.
/// </summary>
[DefaultExecutionOrder(-10)]
public class PlateauBoard : MonoBehaviour
{
    public static PlateauBoard Instance { get; private set; }

    [Tooltip("The content frame (the object carrying RoomContent). Resolved automatically if empty.")]
    public Transform worldRoot;
    [Tooltip("Parent of the Plateau instances. Resolved by name if empty.")]
    public Transform plateausRoot;
    [Tooltip("Parent of the Bridge Spots instances. Resolved by name if empty.")]
    public Transform bridgesRoot;

    [Tooltip("How far outside a plateau a bar's end may land and still count as touching it, as a " +
             "squared multiple of that plateau's radius. 4 = two radii. Endpoints normally land " +
             "right on the rim, near 1.")]
    public float MaxEndpointScore = 4f;

    /// <summary>
    /// How many bars may share one plateau pair before the extra ones collapse. Two, not one: the six
    /// central exits were re-authored on top of the originals and both bars are still in the scene, and
    /// each should host its own bridge. Every other pair has exactly one bar in the scene, so this cap
    /// never engages there.
    /// </summary>
    const int MaxEdgesPerPair = 2;

    struct Tile
    {
        public Transform root;
        public Vector3 centre;      // World Root local
        public float radiusX;       // World Root local
        public float radiusZ;
        public float topY;          // World Root local — the surface pieces stand on
        public PlateauTint tint;
    }

    Tile[] tiles = new Tile[0];

    readonly List<BridgeEdge> edges = new List<BridgeEdge>();
    readonly List<Transform> edgeSpots = new List<Transform>();
    readonly Dictionary<int, List<int>> edgeByPair = new Dictionary<int, List<int>>();
    List<int>[] incidence = new List<int>[0];

    readonly List<string> rejected = new List<string>();
    readonly List<string> duplicates = new List<string>();

    int central = -1;
    uint graphHash;
    bool baked;
    string report = "";

    public bool IsBaked => baked;
    public int PlateauCount => tiles.Length;
    public int CentralPlateau => central;
    public uint GraphHash => graphHash;
    public IReadOnlyList<BridgeEdge> Edges => edges;
    public string Report => report;

    public Vector3 LocalCentre(int p) => InRange(p) ? tiles[p].centre : Vector3.zero;
    public float RadiusX(int p) => InRange(p) ? tiles[p].radiusX : 0f;
    public float RadiusZ(int p) => InRange(p) ? tiles[p].radiusZ : 0f;
    public float TopLocalY(int p) => InRange(p) ? tiles[p].topY : 0f;
    public Transform PlateauRoot(int p) => InRange(p) ? tiles[p].root : null;
    public PlateauTint TintFor(int p) => InRange(p) ? tiles[p].tint : null;
    public Transform ContentRoot => worldRoot;

    bool InRange(int p) => p >= 0 && p < tiles.Length;

    public IReadOnlyList<int> EdgesAt(int p) =>
        p >= 0 && p < incidence.Length && incidence[p] != null ? incidence[p] : System.Array.Empty<int>();

    /// <summary>
    /// The bar to draw a laid bridge on, looked up by the pair of plateaus rather than by edge
    /// index. PlateauGame publishes the authoritative edge list from the server, and looking up by
    /// content rather than by index means a client whose own bake ordered things differently still
    /// draws the bridge in the right place.
    ///
    /// A pair can now have up to MaxEdgesPerPair bars (the six re-authored central connections);
    /// this returns the lowest-indexed one. Callers that need a SPECIFIC one of the two, given an
    /// edge index, want SpotForEdge instead.
    /// </summary>
    public Transform SpotForPair(int a, int b)
    {
        return edgeByPair.TryGetValue(PairKey(a, b), out List<int> list) && list.Count > 0
            ? edgeSpots[list[0]]
            : null;
    }

    public bool TryFindEdge(int a, int b, out int edge)
    {
        if (edgeByPair.TryGetValue(PairKey(a, b), out List<int> list) && list.Count > 0)
        {
            edge = list[0];
            return true;
        }
        edge = -1;
        return false;
    }

    /// <summary>
    /// The bar for one edge, by its own index. Unlike SpotForPair this stays unambiguous once a pair
    /// can have two edges (the six re-authored central connections) -- each edge index names exactly
    /// one bar.
    /// </summary>
    public Transform SpotForEdge(int edge) =>
        edge >= 0 && edge < edgeSpots.Count ? edgeSpots[edge] : null;

    static int PairKey(int a, int b) => a < b ? (a << 8) | b : (b << 8) | a;

    void Awake()
    {
        Instance = this;
        EnsureBaked();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void EnsureBaked()
    {
        if (baked)
        {
            return;
        }
        Rebuild();
    }

    [ContextMenu("Bake and Report")]
    public void Rebuild()
    {
        ResolveRoots();
        if (worldRoot == null || plateausRoot == null || bridgesRoot == null)
        {
            Debug.LogError("PlateauBoard: could not resolve World Root / Plateaus / Bridges. " +
                           "Assign them on " + name + ".");
            return;
        }

        BakeTiles();
        BakeEdges();
        BuildReport();

        if (Application.isPlaying)
        {
            baked = true;
            Debug.Log(report);
        }
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
        if (plateausRoot == null)
        {
            plateausRoot = HierarchyUtils.FindDescendant(worldRoot, "Plateaus");
        }
        if (bridgesRoot == null)
        {
            bridgesRoot = HierarchyUtils.FindDescendant(worldRoot, "Bridges");
        }
    }

    // ------------------------------------------------------------------ plateaus

    void BakeTiles()
    {
        int n = plateausRoot.childCount;
        tiles = new Tile[n];
        central = -1;

        for (int i = 0; i < n; i++)
        {
            Transform child = plateausRoot.GetChild(i);
            Tile t = new Tile { root = child };

            if (TryLocalBounds(child, worldRoot, out Vector3 min, out Vector3 max))
            {
                t.centre = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (min.z + max.z) * 0.5f);
                t.radiusX = Mathf.Max(1e-4f, (max.x - min.x) * 0.5f);
                t.radiusZ = Mathf.Max(1e-4f, (max.z - min.z) * 0.5f);
                t.topY = max.y;
            }
            else
            {
                // No renderer under it. Fall back to the transform so the index still exists and
                // the board does not silently lose a plateau.
                Vector3 p = worldRoot.InverseTransformPoint(child.position);
                t.centre = p;
                t.radiusX = 0.05f;
                t.radiusZ = 0.05f;
                t.topY = p.y;
                Debug.LogWarning("PlateauBoard: \"" + child.name + "\" has no renderer; using its " +
                                 "transform, which is NOT the plateau's centre.");
            }

            if (child.name == PlateauConst.CentralPlateauName)
            {
                central = i;
            }

            if (Application.isPlaying)
            {
                PlateauTag tag = child.GetComponent<PlateauTag>();
                if (tag == null)
                {
                    tag = child.gameObject.AddComponent<PlateauTag>();
                }
                tag.index = i;

                t.tint = child.GetComponent<PlateauTint>();
                if (t.tint == null)
                {
                    t.tint = child.gameObject.AddComponent<PlateauTint>();
                }
                t.tint.Capture();
            }

            tiles[i] = t;
        }

        if (central < 0)
        {
            // Everything starts here, and the bridge rules are seeded from it, so a board without
            // one is unplayable. Fall back to the largest plateau rather than throwing.
            float best = -1f;
            for (int i = 0; i < tiles.Length; i++)
            {
                float area = tiles[i].radiusX * tiles[i].radiusZ;
                if (area > best)
                {
                    best = area;
                    central = i;
                }
            }
            Debug.LogError("PlateauBoard: no plateau named \"" + PlateauConst.CentralPlateauName +
                           "\". Falling back to the largest, \"" +
                           (central >= 0 ? tiles[central].root.name : "none") + "\".");
        }
    }

    /// <summary>
    /// The axis-aligned bounds of everything renderable under <paramref name="target"/>, expressed
    /// in <paramref name="frame"/>'s local space.
    ///
    /// Built from each renderer's LOCAL bounds pushed through its own matrix, not from
    /// Renderer.bounds — the latter is already a world AABB, and re-projecting that through World
    /// Root's yaw would inflate it. This matters here because Plateau.prefab's collider and mesh
    /// live on a nested child that is offset and rotated 180 degrees, so the Plateau root's own
    /// position is nowhere near the plateau's centre.
    /// </summary>
    static bool TryLocalBounds(Transform target, Transform frame, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;

        Renderer[] found = target.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < found.Length; i++)
        {
            Renderer r = found[i];
            if (r == null || r is ParticleSystemRenderer)
            {
                continue;
            }

            Bounds local = r.localBounds;
            Transform rt = r.transform;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3(
                    (c & 1) == 0 ? local.min.x : local.max.x,
                    (c & 2) == 0 ? local.min.y : local.max.y,
                    (c & 4) == 0 ? local.min.z : local.max.z);
                Vector3 p = frame.InverseTransformPoint(rt.TransformPoint(corner));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        return any;
    }

    // ------------------------------------------------------------------ adjacency

    void BakeEdges()
    {
        edges.Clear();
        edgeSpots.Clear();
        edgeByPair.Clear();
        rejected.Clear();
        duplicates.Clear();

        for (int i = 0; i < bridgesRoot.childCount; i++)
        {
            Transform spot = bridgesRoot.GetChild(i);
            Transform bar = HierarchyUtils.FindDescendant(spot, PlateauConst.BridgeBarName);
            if (bar == null)
            {
                // Any single renderer will do — the bar is the only thing under a Bridge Spot.
                Renderer r = spot.GetComponentInChildren<Renderer>(true);
                bar = r != null ? r.transform : null;
            }
            if (bar == null)
            {
                rejected.Add(spot.name + " (no bar)");
                continue;
            }

            // Unity's built-in Cylinder spans -1..+1 on its local Y. TransformPoint applies the
            // whole chain, so this stays correct through Board's non-uniform (2.5, 1, 2.5) scale
            // and the bar's 90-degree roll, which naive half-length arithmetic does not.
            Vector3 endA = worldRoot.InverseTransformPoint(bar.TransformPoint(new Vector3(0f, 1f, 0f)));
            Vector3 endB = worldRoot.InverseTransformPoint(bar.TransformPoint(new Vector3(0f, -1f, 0f)));

            int a = NearestTile(endA, out float scoreA, out int secondA);
            int b = NearestTile(endB, out float scoreB, out int secondB);

            if (a < 0 || b < 0)
            {
                rejected.Add(spot.name + " (no plateau)");
                continue;
            }

            if (a == b)
            {
                // Both ends picked the same plateau — a short bar lying mostly over one of them.
                // Keep the end that fits better and give the other its runner-up.
                if (scoreA <= scoreB && secondB >= 0)
                {
                    b = secondB;
                }
                else if (secondA >= 0)
                {
                    a = secondA;
                }
                else
                {
                    rejected.Add(spot.name + " (both ends on " + tiles[a].root.name + ")");
                    continue;
                }
            }

            if (Mathf.Max(scoreA, scoreB) > MaxEndpointScore)
            {
                rejected.Add(spot.name + " (end " + Mathf.Max(scoreA, scoreB).ToString("0.0") +
                             " radii^2 from anything)");
                continue;
            }

            int key = PairKey(a, b);
            if (!edgeByPair.TryGetValue(key, out List<int> onThisPair))
            {
                onThisPair = new List<int>();
                edgeByPair.Add(key, onThisPair);
            }

            if (onThisPair.Count >= MaxEdgesPerPair)
            {
                // A third+ bar on the same pair -- still collapsed, exactly as every duplicate was
                // before this, just at a threshold of two instead of one.
                duplicates.Add(spot.name + " = " + tiles[a].root.name + " <-> " + tiles[b].root.name);
                continue;
            }

            int newEdgeIndex = edges.Count;
            onThisPair.Add(newEdgeIndex);
            edges.Add(new BridgeEdge(a, b));
            edgeSpots.Add(spot);

            if (Application.isPlaying)
            {
                // Same guard BakeTiles uses for PlateauTag/PlateauTint: Rebuild() can also run from
                // the "Bake and Report" context menu in Edit mode via OnDrawGizmosSelected, and that
                // must not leave runtime-only components on the scene.
                PlateauEdgeTag edgeTag = spot.GetComponent<PlateauEdgeTag>();
                if (edgeTag == null)
                {
                    edgeTag = spot.gameObject.AddComponent<PlateauEdgeTag>();
                }
                edgeTag.edge = newEdgeIndex;
            }
        }

        incidence = new List<int>[tiles.Length];
        for (int i = 0; i < incidence.Length; i++)
        {
            incidence[i] = new List<int>();
        }
        for (int e = 0; e < edges.Count; e++)
        {
            incidence[edges[e].a].Add(e);
            incidence[edges[e].b].Add(e);
        }

        // FNV-1a over the edge list, so a client whose bake disagrees with the server's says so
        // instead of silently highlighting destinations the server will refuse.
        unchecked
        {
            uint h = 2166136261u;
            for (int e = 0; e < edges.Count; e++)
            {
                h = (h ^ edges[e].a) * 16777619u;
                h = (h ^ edges[e].b) * 16777619u;
            }
            graphHash = h;
        }

        // Edge indices travel as bytes on the wire (PlacedBridge.edge, both bridge RPCs' toEdge/
        // fromEdge) and PlateauConst.NoIndex is 255, which PlateauPieceTag.edge uses as "not
        // placed" — so index 255 is unusable and the board is capped at 255 edges. 81 bars today,
        // so this is not live; the assert turns a bridge that silently refuses to move into a named
        // failure the day somebody authors the 256th bar.
        Debug.Assert(edges.Count < PlateauConst.NoIndex,
                     "PlateauBoard: " + edges.Count + " edges, but edge indices are bytes and " +
                     PlateauConst.NoIndex + " is reserved. Bridges on edge " + PlateauConst.NoIndex +
                     " and above cannot be placed or moved.");
    }

    int NearestTile(Vector3 q, out float bestScore, out int second)
    {
        int best = -1;
        second = -1;
        bestScore = float.MaxValue;
        float secondScore = float.MaxValue;

        for (int i = 0; i < tiles.Length; i++)
        {
            // "How many plateau-radii away", on each axis independently. Raw distance is wrong
            // here: footprints vary fourfold in x and z separately, so a large plateau's CENTRE is
            // far from a short bar that is sitting right on its rim.
            float dx = (q.x - tiles[i].centre.x) / tiles[i].radiusX;
            float dz = (q.z - tiles[i].centre.z) / tiles[i].radiusZ;
            float score = dx * dx + dz * dz;

            if (score < bestScore)
            {
                secondScore = bestScore;
                second = best;
                bestScore = score;
                best = i;
            }
            else if (score < secondScore)
            {
                secondScore = score;
                second = i;
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ diagnostics

    void BuildReport()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("PlateauBoard: ").Append(tiles.Length).Append(" plateaus, ")
          .Append(edges.Count).Append(" edges, central = ")
          .Append(central >= 0 && central < tiles.Length ? tiles[central].root.name : "NONE")
          .Append(" [").Append(central).Append("], hash ").Append(graphHash);

        if (duplicates.Count > 0)
        {
            sb.Append("\n  ").Append(duplicates.Count).Append(" duplicate bar(s), ignored: ")
              .Append(string.Join(", ", duplicates));
        }

        if (rejected.Count > 0)
        {
            sb.Append("\n  ").Append(rejected.Count)
              .Append(" UNRESOLVED bar(s) — these gaps cannot be bridged or jumped: ")
              .Append(string.Join(", ", rejected));
        }

        int isolated = 0;
        for (int i = 0; i < incidence.Length; i++)
        {
            if (incidence[i] == null || incidence[i].Count == 0)
            {
                isolated++;
                sb.Append("\n  ISOLATED: ").Append(tiles[i].root.name).Append(" has no connections.");
            }
        }
        if (isolated == 0 && rejected.Count == 0)
        {
            sb.Append("\n  every plateau connected, no unresolved bars.");
        }

        report = sb.ToString();
    }

    /// <summary>
    /// Select this object in the Scene view to see the derived graph. A green line is a connection
    /// the code believes in; a red sphere is a bar it could not place. The board is still being
    /// hand-authored, so a mis-resolved bar has to be visible here rather than buried in a log.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying)
        {
            Rebuild();
        }
        if (worldRoot == null || tiles.Length == 0)
        {
            return;
        }

        Gizmos.color = Color.green;
        for (int e = 0; e < edges.Count; e++)
        {
            Vector3 a = worldRoot.TransformPoint(TopPoint(edges[e].a));
            Vector3 b = worldRoot.TransformPoint(TopPoint(edges[e].b));
            Gizmos.DrawLine(a, b);
        }

        Gizmos.color = Color.cyan;
        for (int i = 0; i < tiles.Length; i++)
        {
            Vector3 c = worldRoot.TransformPoint(TopPoint(i));
            float r = Mathf.Min(tiles[i].radiusX, tiles[i].radiusZ) * worldRoot.lossyScale.x;
            Gizmos.DrawWireSphere(c, Mathf.Max(0.002f, r * 0.25f));
        }

        if (central >= 0 && central < tiles.Length)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(worldRoot.TransformPoint(TopPoint(central)),
                                  tiles[central].radiusX * worldRoot.lossyScale.x);
        }

        // Anything the bake could not place. These are the ones to nudge in the scene.
        if (bridgesRoot == null)
        {
            return;
        }
        Gizmos.color = Color.red;
        for (int i = 0; i < bridgesRoot.childCount; i++)
        {
            Transform spot = bridgesRoot.GetChild(i);
            if (!rejected.Exists(s => s.StartsWith(spot.name + " ")))
            {
                continue;
            }
            Gizmos.DrawWireSphere(spot.position, 0.02f);
        }
    }

    Vector3 TopPoint(int p) => new Vector3(tiles[p].centre.x, tiles[p].topY, tiles[p].centre.z);

}
