using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Draws the board. Every piece here is a plain GameObject that exists because PlateauGame's
/// replicated lists say it should — no NetworkObjects, no per-piece ownership, nothing to spawn.
///
/// Pieces hang under an UNSCALED container beneath World Root, never under Board (2.5, 1, 2.5) or
/// Plateaus (2, 0.02, 2), both of which are non-uniform and would squash them. Under World Root
/// they inherit the world grab's move, turn and uniform resize for free.
///
/// Order 30: after RoomContent (15) and WorldGrab (20) have settled World Root for this frame, so
/// the LateUpdate billboard faces where the camera actually is.
/// </summary>
[DefaultExecutionOrder(30)]
public class PlateauPieceView : MonoBehaviour
{
    public static PlateauPieceView Instance { get; private set; }

    [Header("Scene")]
    public Transform worldRoot;
    [Tooltip("An empty child of World Root at identity with scale 1. Created if left empty.")]
    public Transform piecesRoot;

    [Header("Piece prefabs, indexed by PieceKind")]
    public GameObject bridgePrefab;
    public GameObject troopPrefab;
    public GameObject parshendiPrefab;
    public GameObject shardbearerPrefab;
    public GameObject gemheartPrefab;
    public GameObject chasmfiendPrefab;

    [Header("Layout")]
    [Tooltip("Fraction of a plateau's radius the pieces are allowed to spread over.")]
    public float UsableFraction = 0.72f;
    [Tooltip("Metres above the plateau surface, in World Root units. Keeps the owner disc from " +
             "z-fighting and keeps the pointer hitting the piece before the plateau.")]
    public float SurfaceLift = 0.002f;
    [Tooltip("Global size tweak on top of each prefab's authored scale.")]
    public float pieceScaleMultiplier = 1f;
    [Tooltip("The native length of the bridge model's long (local X) axis before any scale is applied.")]
    public float BridgeNativeLength = 0.4f;
    [Tooltip("Seconds for a piece to slide to a new slot. Snaps on its first frame.")]
    public float SmoothTime = 0.15f;

    [Header("Count label")]
    public Color countNormalColor = Color.white;
    public Color countSelectedColor = new Color(1f, 0.82f, 0.25f);
    public Color countBlockedColor = new Color(1f, 0.35f, 0.3f);
    [Tooltip("Upper bound on how much the numbers grow back when the board is shrunk.")]
    public float MaxCountScaleCompensation = 3f;
    [Tooltip("How much the count grows while its piece is selected — keyInfo.MakeBigger's idiom.")]
    public float countSelectedScale = 1.25f;

    // 48 = 12 seats x 4 kinds, the most stacks one plateau can ever hold.
    const int MaxSlots = PlateauConst.MaxSeats * PlateauConst.KindCount;
    static readonly Vector2[] Spiral = BuildSpiral();

    readonly Dictionary<int, PlateauPieceTag> stackViews = new Dictionary<int, PlateauPieceTag>();
    readonly Dictionary<int, PlateauPieceTag> bridgeViews = new Dictionary<int, PlateauPieceTag>();
    readonly List<int> deadKeys = new List<int>();

    Transform cam;
    bool dirty = true;
    PlateauGame subscribed;

    PlateauPieceTag overlayTag;
    int overlayShown;
    int overlayTotal;
    bool overlayBlocked;

    void Awake()
    {
        Instance = this;
    }

    void OnEnable()
    {
        dirty = true;
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += HandleSceneChanged;
    }

    void OnDisable()
    {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= HandleSceneChanged;
        Unsubscribe();
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void HandleSceneChanged(UnityEngine.SceneManagement.Scene from, UnityEngine.SceneManagement.Scene to)
    {
        cam = null;
        dirty = true;
    }

    void Unsubscribe()
    {
        if (subscribed != null)
        {
            subscribed.BoardChanged -= MarkDirty;
            subscribed = null;
        }
    }

    void MarkDirty()
    {
        // A dirty flag, not a diff. The initial state of a NetworkList arrives with no per-element
        // change events at all, so the only thing that works for both the first sync and every
        // later delta is to reconcile the whole board once, here.
        dirty = true;
    }

    void LateUpdate()
    {
        PlateauGame game = PlateauGame.Instance;
        if (game != subscribed)
        {
            Unsubscribe();
            subscribed = game;
            if (subscribed != null)
            {
                subscribed.BoardChanged += MarkDirty;
            }
            dirty = true;
        }

        if (dirty)
        {
            dirty = false;
            Rebuild();
        }

        Animate();
    }

    // ------------------------------------------------------------------ reconcile

    void Rebuild()
    {
        PlateauGame game = PlateauGame.Instance;
        PlateauBoard board = PlateauBoard.Instance;

        if (game == null || !game.IsSpawned || board == null)
        {
            ClearAll();
            return;
        }

        board.EnsureBaked();
        if (!board.IsBaked || board.CentralPlateau < 0)
        {
            ClearAll();
            return;
        }

        if (!EnsureRoots())
        {
            return;
        }

        RebuildStacks(game, board);
        RebuildBridges(game, board);
    }

    void RebuildStacks(PlateauGame game, PlateauBoard board)
    {
        // Group by plateau, then order within a plateau by (seat, kind). Sorting integers over a
        // set that is byte-identical on every client is what makes every headset lay the pieces out
        // the same way.
        List<int>[] byPlateau = GetPlateauBuckets(board.PlateauCount);
        for (int i = 0; i < byPlateau.Length; i++)
        {
            byPlateau[i].Clear();
        }

        for (int i = 0; i < game.stacks.Count; i++)
        {
            PieceStack s = game.stacks[i];
            if (s.plateau < byPlateau.Length && s.count > 0)
            {
                byPlateau[s.plateau].Add(i);
            }
        }

        float referenceUsable = ReferenceUsable(board);

        // Retire views whose stack is gone.
        deadKeys.Clear();
        foreach (KeyValuePair<int, PlateauPieceTag> kv in stackViews)
        {
            int plateau = (kv.Key >> 12) & 0xFF;
            int seat = (kv.Key >> 4) & 0xFF;
            int kind = kv.Key & 0xF;
            if (game.FindStack(plateau, seat, kind) < 0)
            {
                deadKeys.Add(kv.Key);
            }
        }
        for (int i = 0; i < deadKeys.Count; i++)
        {
            Retire(stackViews, deadKeys[i]);
        }

        for (int p = 0; p < byPlateau.Length; p++)
        {
            List<int> here = byPlateau[p];
            if (here.Count == 0)
            {
                continue;
            }

            here.Sort((x, y) => SlotKey(game.stacks[x]).CompareTo(SlotKey(game.stacks[y])));

            float usableX = board.RadiusX(p) * UsableFraction;
            float usableZ = board.RadiusZ(p) * UsableFraction;
            float scale = PieceScale(Mathf.Min(usableX, usableZ), referenceUsable, here.Count);

            for (int slot = 0; slot < here.Count; slot++)
            {
                PieceStack s = game.stacks[here[slot]];
                int key = StackKey(s.plateau, s.seat, s.kind);

                if (!stackViews.TryGetValue(key, out PlateauPieceTag tag) || tag == null)
                {
                    tag = Spawn((PieceKind)s.kind);
                    if (tag == null)
                    {
                        continue;
                    }
                    stackViews[key] = tag;
                    ApplyOwner(tag, s.seat);
                }

                tag.plateau = s.plateau;
                tag.seat = s.seat;
                tag.kind = s.kind;
                tag.count = s.count;
                tag.edge = PlateauConst.NoIndex;

                Vector2 pt = Spiral[Mathf.Min(slot, MaxSlots - 1)];
                Vector3 local = new Vector3(
                    board.LocalCentre(p).x + pt.x * usableX,
                    board.TopLocalY(p) + SurfaceLift,
                    board.LocalCentre(p).z + pt.y * usableZ);

                tag.targetLocalPosition = ToPieceSpace(local);
                float pieceScale = scale * tag.prefabScale;
                tag.targetScale = new Vector3(pieceScale, pieceScale, pieceScale);
                tag.transform.localRotation = ToPieceSpace(Quaternion.identity);
                RefreshLabel(tag);
            }
        }
    }

    void RebuildBridges(PlateauGame game, PlateauBoard board)
    {
        deadKeys.Clear();
        foreach (KeyValuePair<int, PlateauPieceTag> kv in bridgeViews)
        {
            if (game.FindPlacedBridge(kv.Key) < 0)
            {
                deadKeys.Add(kv.Key);
            }
        }
        for (int i = 0; i < deadKeys.Count; i++)
        {
            Retire(bridgeViews, deadKeys[i]);
        }

        float referenceUsable = ReferenceUsable(board);

        for (int i = 0; i < game.placedBridges.Count; i++)
        {
            PlacedBridge pb = game.placedBridges[i];
            if (!bridgeViews.TryGetValue(pb.edge, out PlateauPieceTag tag) || tag == null)
            {
                tag = Spawn(PieceKind.Bridge);
                if (tag == null)
                {
                    continue;
                }
                bridgeViews[pb.edge] = tag;
            }

            ApplyOwner(tag, pb.seat);
            tag.seat = pb.seat;
            tag.kind = (byte)PieceKind.Bridge;
            tag.count = 1;
            tag.edge = pb.edge;
            tag.plateau = PlateauConst.NoIndex;

            if (game.TryGetEdgeEnds(pb.edge, out int a, out int b))
            {
                // The bar was hand-placed straight across the gap, so its midpoint and its axis are
                // better than anything derived from the two plateau centres, which are off-centre
                // whenever the plateaus differ in size.
                Transform spot = game.SpotForEdge(pb.edge);
                Transform bar = spot != null
                    ? (PlateauBoard.FindDescendant(spot, PlateauConst.BridgeBarName) ?? spot)
                    : null;

                Vector3 localMid;
                Vector3 localAxis;
                if (bar != null)
                {
                    localMid = worldRoot.InverseTransformPoint(bar.position);
                    localAxis = worldRoot.InverseTransformVector(bar.TransformVector(Vector3.up));
                }
                else
                {
                    Vector3 pa = board.LocalCentre(a);
                    Vector3 pb2 = board.LocalCentre(b);
                    localMid = (pa + pb2) * 0.5f;
                    // Half the span, matching the bar branch's convention (a unit arm off the
                    // midpoint) — gapLength below doubles it back out.
                    localAxis = (pb2 - pa) * 0.5f;
                }

                localMid.y = Mathf.Max(board.TopLocalY(a), board.TopLocalY(b)) + SurfaceLift;
                tag.targetLocalPosition = ToPieceSpace(localMid);

                // Yaw only. The bar carries a 90-degree roll that lays a cylinder flat; copying
                // that to a bridge model would stand it on its side.
                //
                // The extra -90 degree turn is the bridge model's own long axis: bridge.obj's mesh
                // runs along local X (about 0.4 units), not the Z that LookRotation points down the
                // gap, so without it the plank would lie broadside across the gap instead of along it.
                localAxis.y = 0f;
                Quaternion rot = localAxis.sqrMagnitude > 1e-8f
                    ? Quaternion.LookRotation(localAxis.normalized, Vector3.up) * Quaternion.Euler(0f, -90f, 0f)
                    : Quaternion.identity;
                tag.transform.localRotation = ToPieceSpace(rot);

                float gapLength = 2f * localAxis.magnitude;
                float xScale = gapLength / Mathf.Max(0.01f, BridgeNativeLength);
                tag.targetScale = new Vector3(xScale, tag.prefabScale, tag.prefabScale);
            }

            RefreshLabel(tag);
        }
    }

    void ClearAll()
    {
        foreach (KeyValuePair<int, PlateauPieceTag> kv in stackViews)
        {
            if (kv.Value != null)
            {
                Destroy(kv.Value.gameObject);
            }
        }
        foreach (KeyValuePair<int, PlateauPieceTag> kv in bridgeViews)
        {
            if (kv.Value != null)
            {
                Destroy(kv.Value.gameObject);
            }
        }
        stackViews.Clear();
        bridgeViews.Clear();
        overlayTag = null;
    }

    void Retire(Dictionary<int, PlateauPieceTag> from, int key)
    {
        if (from.TryGetValue(key, out PlateauPieceTag tag) && tag != null)
        {
            if (overlayTag == tag)
            {
                overlayTag = null;
            }
            Destroy(tag.gameObject);
        }
        from.Remove(key);
    }

    // ------------------------------------------------------------------ per frame

    void Animate()
    {
        if (piecesRoot == null)
        {
            return;
        }

        if (cam == null && Camera.main != null)
        {
            cam = Camera.main.transform;
        }

        float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, SmoothTime));
        float compensation = CountScaleCompensation();

        foreach (KeyValuePair<int, PlateauPieceTag> kv in stackViews)
        {
            Step(kv.Value, t, compensation);
        }
        foreach (KeyValuePair<int, PlateauPieceTag> kv in bridgeViews)
        {
            Step(kv.Value, t, compensation);
        }
    }

    void Step(PlateauPieceTag tag, float t, float compensation)
    {
        if (tag == null)
        {
            return;
        }

        Transform tr = tag.transform;

        // A piece that has just appeared snaps. It must not glide in from wherever Instantiate
        // happened to leave it — same rule as PlayerControls' remotePrimed and RoomContent's snap.
        float k = tag.primed ? t : 1f;
        tag.primed = true;

        tr.localPosition = Vector3.Lerp(tr.localPosition, tag.targetLocalPosition, k);
        tr.localScale = Vector3.Lerp(tr.localScale, tag.targetScale, k);

        if (tag.countTransform == null)
        {
            return;
        }

        // Billboard. The authored Count faces one fixed direction, and up to twelve players stand
        // in a ring around the board — half of them would read every number backwards.
        if (cam != null)
        {
            Vector3 away = tag.countTransform.position - cam.position;
            if (away.sqrMagnitude > 1e-8f)
            {
                tag.countTransform.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        // "the number highlights" — colour in RefreshLabel, size here.
        float emphasis = overlayTag == tag ? Mathf.Max(1f, countSelectedScale) : 1f;
        tag.countTransform.localScale = tag.countBaseScale * (compensation * emphasis);
    }

    /// <summary>
    /// Shrinking the board must not shrink the numbers out of legibility. Purely local and
    /// deliberately not networked — apparent text size is a per-user comfort setting, like
    /// PassthroughController's passthrough toggle.
    /// </summary>
    float CountScaleCompensation()
    {
        if (worldRoot == null)
        {
            return 1f;
        }
        float contentScale = Mathf.Max(0.0001f, worldRoot.localScale.x);
        return Mathf.Clamp(1f / contentScale, 1f, Mathf.Max(1f, MaxCountScaleCompensation));
    }

    // ------------------------------------------------------------------ selection overlay

    /// <summary>
    /// Show "n/total" on the selected stack. <paramref name="blocked"/> means the piece has nowhere
    /// legal to go — which is the correct state of every troop before anybody has laid a bridge, and
    /// needs to say so rather than looking like nothing happened.
    /// </summary>
    public void SetSelectedOverlay(PlateauPieceTag tag, int shown, int total, bool blocked)
    {
        if (overlayTag != null && overlayTag != tag)
        {
            PlateauPieceTag previous = overlayTag;
            overlayTag = null;
            RefreshLabel(previous);
        }

        overlayTag = tag;
        overlayShown = shown;
        overlayTotal = total;
        overlayBlocked = blocked;
        RefreshLabel(tag);
    }

    public void ClearSelectedOverlay()
    {
        if (overlayTag == null)
        {
            return;
        }
        PlateauPieceTag previous = overlayTag;
        overlayTag = null;
        RefreshLabel(previous);
    }

    void RefreshLabel(PlateauPieceTag tag)
    {
        if (tag == null || tag.countLabel == null || tag.countTransform == null)
        {
            return;
        }

        bool selected = overlayTag == tag;

        if (selected)
        {
            tag.countTransform.gameObject.SetActive(true);
            tag.countLabel.text = overlayShown + "/" + overlayTotal;
            tag.countLabel.color = overlayBlocked ? countBlockedColor : countSelectedColor;
            return;
        }

        // A lone piece does not need a "1" over it.
        bool show = tag.count > 1;
        tag.countTransform.gameObject.SetActive(show);
        if (show)
        {
            tag.countLabel.text = tag.count.ToString();
            tag.countLabel.color = countNormalColor;
        }
    }

    // ------------------------------------------------------------------ lookup

    public bool TryGetStackView(int plateau, int seat, int kind, out PlateauPieceTag tag)
    {
        return stackViews.TryGetValue(StackKey(plateau, seat, kind), out tag) && tag != null;
    }

    public bool TryGetBridgeView(int edge, out PlateauPieceTag tag)
    {
        return bridgeViews.TryGetValue(edge, out tag) && tag != null;
    }

    // ------------------------------------------------------------------ helpers

    static int StackKey(int plateau, int seat, int kind) => (plateau << 12) | (seat << 4) | kind;

    static int SlotKey(PieceStack s) => s.seat * PlateauConst.KindCount + s.kind;

    List<int>[] plateauBuckets;

    List<int>[] GetPlateauBuckets(int count)
    {
        if (plateauBuckets == null || plateauBuckets.Length != count)
        {
            plateauBuckets = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                plateauBuckets[i] = new List<int>();
            }
        }
        return plateauBuckets;
    }

    float ReferenceUsable(PlateauBoard board)
    {
        int c = board.CentralPlateau;
        return Mathf.Max(1e-4f, Mathf.Min(board.RadiusX(c), board.RadiusZ(c)) * UsableFraction);
    }

    /// <summary>
    /// Constant size with respect to the world regardless of plateau — the size a piece already had
    /// on the central plateau (where the old plateau-radius-based "fit" term was always exactly 1).
    /// Still shrinks as 1/sqrt(n) once a plateau holds more than four stacks — the rate at which the
    /// area each one gets falls, matching the phyllotaxis layout spiral's own packing density so
    /// pieces do not visually overlap as a plateau fills up. Floored so nothing vanishes.
    /// </summary>
    float PieceScale(float usable, float reference, int occupants)
    {
        float crowd = Mathf.Clamp(Mathf.Sqrt(4f / Mathf.Max(1, occupants)), 0.45f, 1f);
        return crowd * Mathf.Max(0.01f, pieceScaleMultiplier);
    }

    Vector3 ToPieceSpace(Vector3 worldRootLocal)
    {
        Vector3 world = worldRoot.TransformPoint(worldRootLocal);
        return piecesRoot.InverseTransformPoint(world);
    }

    /// <summary>
    /// Everything is measured in World Root local space, but pieces are parented to the container.
    /// Normally those are the same frame; going through both transforms means they need not be.
    /// </summary>
    Quaternion ToPieceSpace(Quaternion worldRootLocal)
    {
        return Quaternion.Inverse(piecesRoot.rotation) * worldRoot.rotation * worldRootLocal;
    }

    bool EnsureRoots()
    {
        if (worldRoot == null)
        {
            worldRoot = RoomContent.Instance != null ? RoomContent.Instance.transform : null;
        }
        if (worldRoot == null)
        {
            GameObject go = GameObject.Find("World Root");
            worldRoot = go != null ? go.transform : null;
        }
        if (worldRoot == null)
        {
            Debug.LogError("PlateauPieceView: no World Root, so there is nowhere to put pieces.");
            return false;
        }

        if (piecesRoot == null)
        {
            Transform found = worldRoot.Find("Pieces");
            if (found == null)
            {
                GameObject go = new GameObject("Pieces");
                go.transform.SetParent(worldRoot, false);
                found = go.transform;
            }
            piecesRoot = found;
        }

        // Scale here is the one mistake that produces skewed pieces with no error anywhere.
        if ((piecesRoot.localScale - Vector3.one).sqrMagnitude > 1e-6f)
        {
            Debug.LogWarning("PlateauPieceView: \"" + piecesRoot.name + "\" has local scale " +
                             piecesRoot.localScale + ". It must be 1,1,1 — pieces inherit it.");
        }

        return true;
    }

    GameObject PrefabFor(PieceKind kind)
    {
        switch (kind)
        {
            case PieceKind.Bridge:      return bridgePrefab;
            case PieceKind.Troop:       return troopPrefab;
            case PieceKind.Parshendi:   return parshendiPrefab;
            case PieceKind.Shardbearer: return shardbearerPrefab;
            case PieceKind.Gemheart:    return gemheartPrefab;
            case PieceKind.Chasmfiend:  return chasmfiendPrefab;
            default:                    return null;
        }
    }

    PlateauPieceTag Spawn(PieceKind kind)
    {
        GameObject prefab = PrefabFor(kind);
        if (prefab == null)
        {
            Debug.LogError("PlateauPieceView: no prefab assigned for " + kind + " on " + name + ".");
            return null;
        }

        GameObject go = Instantiate(prefab, piecesRoot, false);
        go.name = kind.ToString();
        Transform tr = go.transform;

        PlateauPieceTag tag = go.AddComponent<PlateauPieceTag>();

        // Children by name, with an error on a miss — the same contract as Player.prefab's avatar
        // parts. Renaming one of these compiles fine and would otherwise fail silently.
        tag.countTransform = PlateauBoard.FindDescendant(tr, PlateauConst.CountChildName);
        if (tag.countTransform == null)
        {
            Debug.LogError("PlateauPieceView: " + prefab.name + " has no child named \"" +
                           PlateauConst.CountChildName + "\"; it cannot show a count.");
        }
        else
        {
            tag.countLabel = tag.countTransform.GetComponent<TextMeshPro>();
            if (tag.countLabel == null)
            {
                Debug.LogError("PlateauPieceView: " + prefab.name + "/" +
                               PlateauConst.CountChildName + " has no TextMeshPro.");
            }
        }

        Transform disc = PlateauBoard.FindDescendant(tr, PlateauConst.DiscChildName);
        if (disc == null)
        {
            Debug.LogError("PlateauPieceView: " + prefab.name + " has no child named \"" +
                           PlateauConst.DiscChildName + "\"; it cannot show its owner.");
        }
        else
        {
            tag.disc = disc.GetComponent<Renderer>();
        }

        tag.tint = go.AddComponent<PlateauTint>();
        tag.tint.Capture();

        tag.countBaseScale = tag.countTransform != null ? tag.countTransform.localScale : Vector3.one;
        tag.prefabScale = prefab.transform.localScale.x;

        tr.localRotation = Quaternion.identity;
        tr.localScale = Vector3.one * prefab.transform.localScale.x;
        tag.targetScale = Vector3.one * prefab.transform.localScale.x;
        tag.primed = false;

        return tag;
    }

    void ApplyOwner(PlateauPieceTag tag, int seat)
    {
        if (tag.disc != null && tag.tint != null)
        {
            // The disc's authored material is ClearWhite at alpha 0.035 — invisible. This is the
            // "colored circle indicating its owner" from plateauRules.md, and it composes with a
            // highlight rather than being replaced by one.
            tag.tint.SetBaseColor(tag.disc, PlateauPalette.DiscFor(seat));
        }
    }

    /// <summary>
    /// A phyllotaxis spiral on the unit disc. Uniform coverage with no clustering and no lattice
    /// artefacts, and because slots are handed out in order, a plateau with four stacks uses the
    /// four innermost points rather than scattering them to the rim.
    /// </summary>
    static Vector2[] BuildSpiral()
    {
        Vector2[] points = new Vector2[MaxSlots];
        for (int i = 0; i < MaxSlots; i++)
        {
            float r = Mathf.Sqrt((i + 0.5f) / MaxSlots);
            float theta = i * 2.39996323f;          // the golden angle
            points[i] = new Vector2(r * Mathf.Cos(theta), r * Mathf.Sin(theta));
        }
        return points;
    }
}
