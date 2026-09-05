using System.Collections.Generic;

/// <summary>
/// plateauRules.md "Movement", as graph searches.
///
/// This is the ONE place the movement rules are written down, and the same call runs on the client
/// to decide what to highlight and on the server to decide what to allow — so a client can never
/// highlight a plateau the server will refuse, and a modified client cannot talk the server into a
/// move by disagreeing about the rules.
///
/// The scratch buffers are static and reused. These searches run whenever a selection changes, and
/// allocating a few arrays per selection on a Quest is a habit worth not starting.
///
/// Readings taken where plateauRules.md is ambiguous — all four are flagged at their use site:
///  - "bridges" always means bridges owned by the moving player. The Troop line says so outright
///    ("owned by that player") and there is no reason the other two would differ.
///  - Shardbearer's "crosses two bridges" is UP TO two. A piece forced to move exactly two would be
///    bizarre, and inconsistent with Troop's explicit "up to two".
///  - The one jump may be taken at any point, before or between bridge crossings. "plus one jump
///    per turn" reads as a budget, not an ordering.
///  - "A plateau already connected by that player's bridges" is connected TO something, and which
///    something depends on the move. Placing from reserve: the central plateau, because with no
///    bridges placed nothing is connected to anything, so without that seed no first bridge could
///    ever be placed and the game would deadlock on turn one. Moving a bridge already on the board:
///    that bridge's own two ends, because a bridge is local to the plateau system it is touching.
///    See View.movingEdge and ConnectedComponent, and bridgeMovementUpdate.md.
/// </summary>
public static class PlateauMoveRules
{
    /// <summary>
    /// A read-only slice of the board for one player. Built by PlateauGame, which owns the
    /// authoritative (server-published) edge list.
    /// </summary>
    public struct View
    {
        public IReadOnlyList<BridgeEdge> edges;
        /// <summary>Edge indices touching each plateau.</summary>
        public List<int>[] incidence;
        /// <summary>Per edge: does this seat have a bridge on it?</summary>
        public bool[] ownBridge;
        /// <summary>Per edge: does anyone have a bridge on it? One bridge per gap.</summary>
        public bool[] anyBridge;
        public int plateauCount;
        public int central;

        /// <summary>
        /// The edge a bridge is being lifted FROM, or -1 when a bridge is being placed from reserve
        /// (and for every non-bridge move). This is the ONLY thing that decides where the bridge
        /// network is grown from — the moving bridge's own two ends, or the central plateau. See
        /// ConnectedComponent.
        ///
        /// It does NOT remove the bridge from the graph: ownBridge and anyBridge still carry it, so
        /// the gap it is in is never offered back as a destination.
        ///
        /// View is a struct, so default(View) leaves this 0 — a VALID edge index, meaning "edge 0 is
        /// in hand". IsValid cannot catch that, because -1 and 0 are both legitimate. Every
        /// construction site must set it explicitly; there is exactly one (PlateauGame.TryBuildView).
        /// </summary>
        public int movingEdge;

        public bool IsValid => edges != null && incidence != null && ownBridge != null &&
                               anyBridge != null && plateauCount > 0 &&
                               central >= 0 && central < plateauCount &&
                               movingEdge >= -1;
    }

    static bool[] s_visited = new bool[0];
    static bool[] s_reached = new bool[0];
    /// <summary>
    /// Scratch for the bridge component. BridgeDestinations, CandidateBridgeEdges,
    /// TryResolveBridgeEdge and IsLegalBridgeEdge each recompute the whole component into it before
    /// reading it, so its contents are NEVER valid across a call boundary — do not cache a component
    /// computed by one of them and read it after calling another.
    /// </summary>
    static bool[] s_component = new bool[0];
    static int[] s_queue = new int[0];

    /// <summary>
    /// Every plateau this stack may legally move to this move. Empty is a legitimate answer — a
    /// troop with no bridges owned has nowhere to go, which is the correct state of the board
    /// before anybody has laid one.
    /// </summary>
    public static void LegalDestinations(in View v, PieceKind kind, int from, List<int> results)
    {
        results.Clear();
        if (!v.IsValid || from < 0 || from >= v.plateauCount)
        {
            return;
        }

        if (kind == PieceKind.Bridge)
        {
            BridgeDestinations(v, results);
            return;
        }

        Search(v, from, PlateauConst.BridgeBudget(kind), PlateauConst.JumpBudget(kind), results);
    }

    public static bool IsLegal(in View v, PieceKind kind, int from, int to, List<int> scratch)
    {
        LegalDestinations(v, kind, from, scratch);
        return scratch.Contains(to);
    }

    // ------------------------------------------------------------------ troops, parshendi, shardbearers

    /// <summary>
    /// BFS over (plateau, bridges spent, jump spent). 41 plateaus x 3 x 2 is 246 states, so this is
    /// cheap enough to redo on every selection rather than cache and invalidate.
    ///
    /// A bridge crossing needs one of THIS player's bridges on the edge. A jump crosses any faint
    /// line, bridge or not — that is what makes parshendi and shardbearers mobile on turn one.
    /// </summary>
    static void Search(in View v, int from, int bridgeBudget, int jumpBudget, List<int> results)
    {
        int bridgeStates = bridgeBudget < 0 ? 1 : bridgeBudget + 1;
        int jumpStates = jumpBudget + 1;
        int n = v.plateauCount;
        int stateCount = n * bridgeStates * jumpStates;

        EnsureSize(ref s_visited, stateCount);
        EnsureSize(ref s_reached, n);
        EnsureSize(ref s_queue, stateCount);
        System.Array.Clear(s_visited, 0, stateCount);
        System.Array.Clear(s_reached, 0, n);

        int head = 0;
        int tail = 0;

        int start = (from * bridgeStates) * jumpStates;
        s_visited[start] = true;
        s_queue[tail++] = start;

        while (head < tail)
        {
            int state = s_queue[head++];
            int j = state % jumpStates;
            int rest = state / jumpStates;
            int b = rest % bridgeStates;
            int p = rest / bridgeStates;

            s_reached[p] = true;

            List<int> at = v.incidence[p];
            if (at == null)
            {
                continue;
            }

            for (int i = 0; i < at.Count; i++)
            {
                int e = at[i];
                int u = v.edges[e].Other(p);

                if (v.ownBridge[e])
                {
                    int nb = bridgeBudget < 0 ? 0 : b + 1;
                    if (nb < bridgeStates)
                    {
                        Push(u, nb, j, bridgeStates, jumpStates, ref tail);
                    }
                }

                if (j + 1 < jumpStates)
                {
                    Push(u, b, j + 1, bridgeStates, jumpStates, ref tail);
                }
            }
        }

        for (int p = 0; p < n; p++)
        {
            if (s_reached[p] && p != from)
            {
                results.Add(p);
            }
        }
    }

    static void Push(int p, int b, int j, int bridgeStates, int jumpStates, ref int tail)
    {
        int state = (p * bridgeStates + b) * jumpStates + j;
        if (s_visited[state])
        {
            return;
        }
        s_visited[state] = true;
        s_queue[tail++] = state;
    }

    // ------------------------------------------------------------------ bridges

    /// <summary>
    /// plateauRules.md: a bridge "must be repositioned to span from a plateau already connected by
    /// that player's bridges to a new plateau."
    ///
    /// So: exactly one end inside the relevant connected component, and the gap must be free — a
    /// Bridge Spot is a physical slot and two bridges cannot share it. Requiring the far end to be
    /// OUTSIDE the component is what "to a new plateau" means.
    ///
    /// EXCEPT the twin bar of a pair this player has already bridged: both ends are then already
    /// inside the component, which the rule above would read as "nothing new" and drop — but the
    /// whole point of a duplicated pair (the six central connections) is that a second, still-empty
    /// bar to the SAME plateau is its own legal spot for that player's other bridge. See
    /// HasOwnBridgedTwin.
    ///
    /// WHICH component — rooted at the centre, or at the moving bridge's own two ends — is
    /// View.movingEdge's job, not this method's. See ConnectedComponent.
    /// </summary>
    static void BridgeDestinations(in View v, List<int> results)
    {
        EnsureSize(ref s_component, v.plateauCount);
        ConnectedComponent(v, s_component);

        for (int e = 0; e < v.edges.Count; e++)
        {
            if (!IsCandidateEdge(v, s_component, e))
            {
                continue;
            }
            int far = FarEnd(v, s_component, e);
            if (!results.Contains(far))
            {
                results.Add(far);
            }
        }
    }

    /// <summary>
    /// Whether a bridge may be laid across gap <paramref name="e"/>, given a component already
    /// computed into <paramref name="component"/>. The single statement of the bridge rule:
    /// BridgeDestinations, CandidateBridgeEdges, TryResolveBridgeEdge and IsLegalBridgeEdge (which
    /// is what the server validates with) all run this and nothing else, so there is one place to
    /// change it and no way for two of them to disagree.
    /// </summary>
    static bool IsCandidateEdge(in View v, bool[] component, int e)
    {
        if (v.anyBridge[e])
        {
            return false;                       // one bridge per gap, whoever owns it
        }

        int a = v.edges[e].a;
        int b = v.edges[e].b;
        if (a >= v.plateauCount || b >= v.plateauCount)
        {
            return false;
        }

        // A bridge cannot slide sideways onto a bar spanning the pair it already spans. Its own bar
        // is already excluded by anyBridge above; this is about the OTHER bar of a duplicated pair
        // (the six central connections), which HasOwnBridgedTwin would otherwise offer as a legal
        // spot — and moving a bridge onto its own twin bar connects nothing that was not already
        // connected.
        if (v.movingEdge >= 0 && v.movingEdge < v.edges.Count &&
            SamePair(v.edges[e], v.edges[v.movingEdge]))
        {
            return false;
        }

        bool inA = component[a];
        bool inB = component[b];
        if (!inA && !inB)
        {
            return false;                       // neither end is in this bridge's local system
        }
        if (inA && inB && !HasOwnBridgedTwin(v, e))
        {
            return false;                       // both inside, and not the free twin of an owned bar
        }
        return true;
    }

    /// <summary>Which end of a candidate gap the player points at to name it.</summary>
    static int FarEnd(in View v, bool[] component, int e)
    {
        int a = v.edges[e].a;
        int b = v.edges[e].b;
        return (component[a] && component[b]) ? (a == v.central ? b : a)     // twin bar
                                              : (component[a] ? b : a);
    }

    /// <summary>BridgeEdge always stores a &lt; b, so an unordered pair is a two-field compare.</summary>
    static bool SamePair(BridgeEdge x, BridgeEdge y) => x.a == y.a && x.b == y.b;

    /// <summary>
    /// True when some OTHER edge spanning the exact same pair of plateaus as <paramref name="edge"/>
    /// already carries this player's own bridge. MaxEdgesPerPair (PlateauBoard) is 2, so there is at
    /// most one such twin. This is what lets a player place their second bridge on the second bar of
    /// a duplicated connection, spanning the same two plateaus their first bridge already does.
    /// </summary>
    static bool HasOwnBridgedTwin(in View v, int edge)
    {
        BridgeEdge e = v.edges[edge];
        for (int i = 0; i < v.edges.Count; i++)
        {
            if (i == edge || !v.ownBridge[i])
            {
                continue;
            }
            if (SamePair(v.edges[i], e))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The plateaus this player has reached with their own bridges, from wherever the search is
    /// seeded.
    ///
    /// Placing from reserve (movingEdge &lt; 0): seeded with the central plateau, because that is
    /// where everybody starts and where a first bridge must come from — with no bridges placed,
    /// nothing is "already connected by that player's bridges", so without the seed no first bridge
    /// could ever be placed.
    ///
    /// Moving a bridge already on the board: seeded with that bridge's OWN two ends. A bridge is
    /// local to the plateau system it is touching. Seeding both ends is what makes the far side of
    /// the bridge count (so lifting the centre-most bridge no longer discards everything hanging off
    /// it) and what stops a bridge in a detached piece of the network from being teleported back
    /// onto the piece that still reaches the centre. See bridgeMovementUpdate.md §1.
    /// </summary>
    public static void ConnectedComponent(in View v, bool[] into)
    {
        System.Array.Clear(into, 0, v.plateauCount);
        EnsureSize(ref s_queue, v.plateauCount);

        int head = 0;
        int tail = 0;

        if (v.movingEdge >= 0 && v.movingEdge < v.edges.Count)
        {
            BridgeEdge held = v.edges[v.movingEdge];
            Seed(v, into, held.a, ref tail);
            Seed(v, into, held.b, ref tail);
        }
        else
        {
            Seed(v, into, v.central, ref tail);
        }

        while (head < tail)
        {
            int p = s_queue[head++];
            List<int> at = v.incidence[p];
            if (at == null)
            {
                continue;
            }
            for (int i = 0; i < at.Count; i++)
            {
                int e = at[i];
                if (!v.ownBridge[e])
                {
                    continue;
                }
                int u = v.edges[e].Other(p);
                if (u < v.plateauCount && !into[u])
                {
                    into[u] = true;
                    s_queue[tail++] = u;
                }
            }
        }
    }

    /// <summary>
    /// Mark one starting plateau. s_queue still needs no more than plateauCount slots however many
    /// seeds there are: a plateau already marked is refused, so each is enqueued at most once.
    /// </summary>
    static void Seed(in View v, bool[] into, int p, ref int tail)
    {
        if (p < 0 || p >= v.plateauCount || into[p])
        {
            return;
        }
        into[p] = true;
        s_queue[tail++] = p;
    }

    /// <summary>
    /// Which gap the player means, given the plateau they pointed at. The client's fallback when the
    /// beam landed on a plateau rather than on a bar; a bar hit names its own gap directly and does
    /// not come through here. Two candidate gaps can reach the same plateau from two different
    /// plateaus in the system, and the lowest edge index wins — deterministic, so a re-resolve never
    /// wanders.
    /// </summary>
    public static bool TryResolveBridgeEdge(in View v, int newPlateau, out int edge)
    {
        edge = -1;
        if (!v.IsValid || newPlateau < 0 || newPlateau >= v.plateauCount)
        {
            return false;
        }

        EnsureSize(ref s_component, v.plateauCount);
        ConnectedComponent(v, s_component);

        List<int> at = v.incidence[newPlateau];
        if (at == null)
        {
            return false;
        }

        for (int i = 0; i < at.Count; i++)
        {
            int e = at[i];
            // The FarEnd clause is what makes this agree with BridgeDestinations exactly: a gap with
            // one end in the system is named by its OUTSIDE end, so pointing at the inside end must
            // not resolve it.
            if (!IsCandidateEdge(v, s_component, e) || FarEnd(v, s_component, e) != newPlateau)
            {
                continue;
            }
            if (edge < 0 || e < edge)
            {
                edge = e;
            }
        }

        return edge >= 0;
    }

    /// <summary>Every gap this player could legally bridge, for the faint "candidate" tint.</summary>
    public static void CandidateBridgeEdges(in View v, List<int> results)
    {
        results.Clear();
        if (!v.IsValid)
        {
            return;
        }

        EnsureSize(ref s_component, v.plateauCount);
        ConnectedComponent(v, s_component);

        for (int e = 0; e < v.edges.Count; e++)
        {
            if (IsCandidateEdge(v, s_component, e))
            {
                results.Add(e);
            }
        }
    }

    /// <summary>
    /// The server's check, and the reason a move can name a gap directly: given the edge the client
    /// sent, is a bridge allowed there? Same predicate the client highlighted from.
    /// </summary>
    public static bool IsLegalBridgeEdge(in View v, int edge)
    {
        if (!v.IsValid || edge < 0 || edge >= v.edges.Count)
        {
            return false;
        }

        EnsureSize(ref s_component, v.plateauCount);
        ConnectedComponent(v, s_component);
        return IsCandidateEdge(v, s_component, edge);
    }

    static void EnsureSize(ref bool[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            buffer = new bool[size];
        }
    }

    static void EnsureSize(ref int[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            buffer = new int[size];
        }
    }
}
