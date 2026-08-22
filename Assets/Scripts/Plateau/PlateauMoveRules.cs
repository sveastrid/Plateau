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
///  - A player's bridge network is seeded with the central plateau. With no bridges placed, nothing
///    is "already connected by that player's bridges", so without the seed no first bridge could
///    ever be placed and the game would deadlock on turn one.
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

        public bool IsValid => edges != null && incidence != null && ownBridge != null &&
                               anyBridge != null && plateauCount > 0 &&
                               central >= 0 && central < plateauCount;
    }

    static bool[] s_visited = new bool[0];
    static bool[] s_reached = new bool[0];
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
    /// So: exactly one end inside this player's connected component, and the gap must be free —
    /// a Bridge Spot is a physical slot and two bridges cannot share it. Requiring the far end to
    /// be OUTSIDE the component is what "to a new plateau" means, and it keeps each player's
    /// network a tree rooted at the centre.
    /// </summary>
    static void BridgeDestinations(in View v, List<int> results)
    {
        EnsureSize(ref s_component, v.plateauCount);
        ConnectedComponent(v, s_component);

        for (int e = 0; e < v.edges.Count; e++)
        {
            if (v.anyBridge[e])
            {
                continue;
            }

            int a = v.edges[e].a;
            int b = v.edges[e].b;
            if (a >= v.plateauCount || b >= v.plateauCount)
            {
                continue;
            }
            if (s_component[a] == s_component[b])
            {
                continue;                       // both inside, or both outside
            }

            int far = s_component[a] ? b : a;
            if (!results.Contains(far))
            {
                results.Add(far);
            }
        }
    }

    /// <summary>
    /// Which plateaus this player has reached with their own bridges, seeded with the central
    /// plateau because that is where everybody starts and where a first bridge must come from.
    /// </summary>
    public static void ConnectedComponent(in View v, bool[] into)
    {
        System.Array.Clear(into, 0, v.plateauCount);
        EnsureSize(ref s_queue, v.plateauCount);

        int head = 0;
        int tail = 0;
        into[v.central] = true;
        s_queue[tail++] = v.central;

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
    /// Which gap a bridge actually lands in, given the plateau the player pointed at.
    ///
    /// Every legal edge has exactly one end outside the player's component, so the far plateau
    /// names the edge. Two legal edges can reach the same new plateau from two different connected
    /// ones; the lowest edge index wins, deterministically, so the client that drew the highlight
    /// and the server that validates it always agree.
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

        if (s_component[newPlateau])
        {
            return false;                       // not a NEW plateau
        }

        List<int> at = v.incidence[newPlateau];
        if (at == null)
        {
            return false;
        }

        for (int i = 0; i < at.Count; i++)
        {
            int e = at[i];
            if (v.anyBridge[e])
            {
                continue;
            }
            int other = v.edges[e].Other(newPlateau);
            if (other < v.plateauCount && s_component[other] && (edge < 0 || e < edge))
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
            if (v.anyBridge[e])
            {
                continue;
            }
            int a = v.edges[e].a;
            int b = v.edges[e].b;
            if (a < v.plateauCount && b < v.plateauCount && s_component[a] != s_component[b])
            {
                results.Add(e);
            }
        }
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
