# bridgeMovementUpdate — moving a bridge that is already on the board

Working document for the bridge-relocation rule, in the style of the other docs in `docs/`: the
change, the evidence for why the current code does something else, and the code to write.

Nothing here has been applied. Everything below was checked against the working tree at `1d265be`
plus the untracked `docs/bigFixes1.md`.

This **supersedes `bigFixes1.md` §4**, which posed the same problem and offered three readings. You
picked the third — *seed the reachability search from the moved bridge's own endpoints* — which that
document ranked last and argued against. It was wrong to: §4 was reasoning about which bridges
count, and the actual disagreement was about **where the search starts**. See
[§2](#2--why-the-current-code-does-something-else) for why that distinction is the whole bug.

| | |
| --- | --- |
| **The rule** | A laid bridge may be re-laid across any free gap with exactly one end in the plateau system **it currently touches** — not the system rooted at the central plateau |
| **Files** | `PlateauMoveRules.cs`, `PlateauGame.cs`, `PlateauSelection.cs` |
| **Unchanged** | Placing a bridge from reserve; troop / parshendi / shardbearer movement; one bridge per gap |
| **Also in scope** | A move now names a **gap**, not a plateau, on the wire — see [§4.5](#45--the-rpcs-now-target-a-gap-not-a-plateau) |
| **Accepted consequence** | A player may still cut their own network off from the centre. Allowed, by your call — see [§6](#6--the-consequence-you-accepted) |

---

## 1 — The rule, stated precisely

`plateauRules.md:61`: *"One bridge may be moved per turn. It must be repositioned to span from a
plateau already connected by that player's bridges to a new plateau."*

"Already connected" is the ambiguous phrase. **Connected to what?** Your reading, and the one to
implement:

> Let the selected bridge span the gap between plateaus `a` and `b`. Let **S** — the *local plateau
> system* — be every plateau reachable from `a` or from `b` over that player's own bridges. The
> bridge may be re-laid across any gap that has no bridge on it and has **exactly one end in S**.

Three consequences fall straight out of that sentence, and they are the whole change:

1. **The bridge's own reach still counts.** Everything hanging off the far side of the bridge is in
   S, because `b` seeds the search. Picking up the bridge nearest the centre no longer throws away
   the rest of your network.
2. **The centre is not privileged.** If your network has been split into two pieces, a bridge in the
   detached piece can only be re-laid around *that* piece. It cannot teleport across the board onto
   the piece that still reaches the central plateau.
3. **Both halves count as one system.** If lifting the bridge would split its own network in two,
   S is the union of both halves — the bridge is physically touching both, so both are local to it.

Placing a bridge from **reserve** is a different move and keeps its existing rule: the search is
seeded from the central plateau. Reserve bridges only ever sit on the central plateau
(`PlateauGame.GrantMissingArmies`, `RequestAddPieceServerRpc`), so in practice the two seeds agree
and nothing about first placement changes. You reported first placement as correct; it stays correct.

---

## 2 — Why the current code does something else

`PlateauMoveRules.ConnectedComponent` (`PlateauMoveRules.cs:250-283`) has exactly one seed, and it is
hard-coded:

```csharp
into[v.central] = true;
s_queue[tail++] = v.central;
```

Its doc comment defends that choice for the case it was written for — *"seeded with the central
plateau because that is where everybody starts and where a first bridge must come from"* — which is
right for **placement** and simply never revisited for **relocation**.

On top of that, `PlateauGame.TryBuildView` (`PlateauGame.cs:809-821`) lifts the moved bridge out of
the graph before the search runs:

```csharp
if (pb.edge >= edgeMirror.Count || pb.edge == excludeEdge)
{
    continue;                    // neither ownBridge nor anyBridge gets set for this edge
}
```

The two together produce the collapse. Two bridges, the ones every player starts with:

| Action | Bridges on the board | Component the code uses | Highlighted |
| --- | --- | --- | --- |
| Place A, `central–P1` | — | `{central}` | the 6 plateaus around the centre |
| Place B, `P1–P2` | A | `{central, P1}` | centre's 5 free neighbours + P1's |
| **Move A** | A, B | **`{central}`** — A is lifted, so P1 and P2 fall out with it | **the same 6 as the first placement** |
| **Move A again after it has left the centre** | A, B | **`{central}`** or `{}` | wrong, and now permissive in the other direction: a detached bridge is offered every gap at the centre |

The last two rows are both symptoms of one cause. Under the new rule the third row's component is
`{central} ∪ {P1, P2}` = `{central, P1, P2}`, and the fourth row's is whatever that bridge is
actually attached to.

**The graph is not the problem; the seed is.** That is why the fix below leaves the moved bridge in
`ownBridge` and `anyBridge` — the board is read as it stands — and changes only where the search
starts.

---

## 3 — The idea in one line

`View` gains one field, `movingEdge`, and `ConnectedComponent` reads it:

```
movingEdge < 0   ->  seed = { central }                    (placing from reserve — unchanged)
movingEdge >= 0  ->  seed = { edges[movingEdge].a, .b }    (a bridge is in hand)
```

Everything else — `BridgeDestinations`, `CandidateBridgeEdges`, `TryResolveBridgeEdge`, the faint
candidate-bar tint, the server's validation — already runs off that one component and needs no rule
changes, only the small refactor in §4.3 that stops the three of them from restating the same
predicate three times.

---

## 4 — The change, file by file

### 4.1 — `PlateauMoveRules.View` gains `movingEdge`

```csharp
public struct View
{
    public IReadOnlyList<BridgeEdge> edges;
    public List<int>[] incidence;
    public bool[] ownBridge;
    public bool[] anyBridge;
    public int plateauCount;
    public int central;

    /// <summary>
    /// The edge a bridge is being lifted from, or -1 when a bridge is being placed from reserve.
    /// This is the ONLY thing that decides where the bridge network is grown from — the moving
    /// bridge's own two ends, or the central plateau. See ConnectedComponent.
    ///
    /// It does NOT remove the bridge from the graph: ownBridge and anyBridge still carry it, so
    /// the gap it is in is never offered back as a destination.
    /// </summary>
    public int movingEdge;

    public bool IsValid => edges != null && incidence != null && ownBridge != null &&
                           anyBridge != null && plateauCount > 0 &&
                           central >= 0 && central < plateauCount &&
                           movingEdge >= -1;
}
```

> **`View` is a struct, so `default(View)` gives `movingEdge = 0` — a *valid edge index*, meaning
> "edge 0 is in hand".** `IsValid` cannot catch that, because -1 and 0 are both legitimate. Every
> construction site must set it explicitly. There is exactly one (`PlateauGame.TryBuildView`), and
> §4.4 sets it; keep it that way rather than adding a second.

### 4.2 — `ConnectedComponent` seeds from the bridge in hand

```csharp
/// <summary>
/// The plateaus this player has reached with their own bridges, from wherever the search is seeded.
///
/// Placing from reserve: seeded with the central plateau, because that is where everybody starts
/// and where a first bridge must come from — with no bridges placed, nothing is "already connected
/// by that player's bridges", so without the seed no first bridge could ever be placed.
///
/// Moving a bridge already on the board: seeded with that bridge's OWN two ends. A bridge is local
/// to the plateau system it is touching. Seeding both ends is what makes the far side of the bridge
/// count (so lifting the centre-most bridge no longer discards everything hanging off it) and what
/// stops a bridge in a detached piece of the network from being teleported back onto the piece that
/// still reaches the centre. See bridgeMovementUpdate.md §1.
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
        // ... unchanged ...
    }
}

static void Seed(in View v, bool[] into, int p, ref int tail)
{
    if (p < 0 || p >= v.plateauCount || into[p])
    {
        return;
    }
    into[p] = true;
    s_queue[tail++] = p;
}
```

`s_queue` still needs no more than `plateauCount` slots: `Seed` refuses a plateau already marked, so
each is enqueued at most once however many seeds there are.

### 4.3 — One predicate for "is this gap a legal place to put a bridge"

`BridgeDestinations` (`:184-220`), `CandidateBridgeEdges` (`:341-378`) and `TryResolveBridgeEdge`
(`:296-338`) each restate the same four conditions in slightly different words today. Under the new
rule they gain a fifth, and three copies of a five-clause rule is how client and server drift apart.
Extract it:

```csharp
/// <summary>
/// Whether a bridge may be laid across gap <paramref name="e"/>, given a component already computed
/// into <paramref name="component"/>. The single statement of the bridge rule: BridgeDestinations,
/// CandidateBridgeEdges, TryResolveBridgeEdge and the server's IsLegalBridgeEdge all run this and
/// nothing else, so there is one place to change it and no way for two of them to disagree.
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

    // A bridge cannot slide sideways onto a bar spanning the pair it already spans. Its own bar is
    // already excluded by anyBridge above; this is about the OTHER bar of a duplicated pair (the
    // six central connections), which HasOwnBridgedTwin would otherwise offer as a legal spot —
    // and moving a bridge onto its own twin bar connects nothing that was not already connected.
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

static bool SamePair(BridgeEdge x, BridgeEdge y) => x.a == y.a && x.b == y.b;
```

`BridgeEdge` always stores `a < b` (`PlateauTypes.cs:95-99`), so `SamePair` is a two-field compare
and needs no ordering care. Fold the same helper into `HasOwnBridgedTwin` (`:228-244`), which is
doing that comparison by hand today.

The three public entry points then shrink to loops over the predicate:

```csharp
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
/// Which gap the player means, given the plateau they pointed at. The client's fallback when the
/// beam landed on a plateau rather than on a bar; a bar hit names its own gap directly and does not
/// come through here. Two candidate gaps can reach the same plateau from two different plateaus in
/// the system, and the lowest edge index wins — deterministic, so a re-resolve never wanders.
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
        if (!IsCandidateEdge(v, s_component, e) || FarEnd(v, s_component, e) != newPlateau)
        {
            continue;                       // this gap is named by its other end, not by this one
        }
        if (edge < 0 || e < edge)
        {
            edge = e;
        }
    }

    return edge >= 0;
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
```

The `FarEnd(...) != newPlateau` clause in `TryResolveBridgeEdge` is new and is what makes it agree
with `BridgeDestinations` exactly: a gap with one end in the system is named by its **outside** end,
so pointing at the inside end must not resolve it.

### 4.4 — `PlateauGame.TryBuildView` stops lifting the bridge

```csharp
/// <summary>
/// Assemble the board slice one seat's movement search needs. <paramref name="movingEdge"/> is the
/// edge a bridge is being lifted FROM, or -1 for every other move.
///
/// It does not remove that bridge from the graph — the board is read as it stands, so the gap the
/// bridge is in stays flagged and is never offered back. All it does is move the seed of
/// PlateauMoveRules.ConnectedComponent onto that bridge's own two ends. See bridgeMovementUpdate.md.
/// </summary>
public bool TryBuildView(int seat, int movingEdge, out PlateauMoveRules.View view)
{
    // ... unchanged down to the flag loop ...

    for (int i = 0; i < placedBridges.Count; i++)
    {
        PlacedBridge pb = placedBridges[i];
        if (pb.edge >= edgeMirror.Count)          // was: || pb.edge == excludeEdge
        {
            continue;
        }
        anyFlags[pb.edge] = true;
        if (pb.seat == seat)
        {
            ownFlags[pb.edge] = true;
        }
    }

    view = new PlateauMoveRules.View
    {
        edges = edgeMirror,
        incidence = incidence,
        ownBridge = ownFlags,
        anyBridge = anyFlags,
        plateauCount = board.PlateauCount,
        central = board.CentralPlateau,
        movingEdge = movingEdge,          // never omit: this is a struct, and the default is 0
    };
    return true;
}
```

Rename the parameter from `excludeEdge`. It no longer excludes anything, and a name that lies about
that is exactly how the §4 misreading survived as long as it did.

### 4.5 — The RPCs now target a gap, not a plateau

Today a bridge move sends a destination **plateau** and the server re-derives the gap. With two
detached halves in one local system, one plateau can be adjacent to your network through two
different free bars, and the server's lowest-index tiebreak can put the bridge on the bar you did
not click. Sending the edge removes the guess: the bar you aimed at is the bar you get, and the
server validates that exact gap rather than reconstructing a different one.

Three changes in `PlateauGame`:

```csharp
/// <summary>
/// Move pieces from one plateau to another. Bridges are NOT moved through here — they target a gap,
/// not a plateau, and have their own two RPCs below.
/// </summary>
[ServerRpc(RequireOwnership = false)]
public void RequestMoveServerRpc(byte from, byte kind, byte count, byte to, int epoch,
                                 ServerRpcParams rpcParams = default)
{
    if (kind == (byte)PieceKind.Bridge)
    {
        return;
    }

    // ... unchanged: epoch, board, seat, bounds, FindStack, TryBuildView(seat, -1), IsLegal ...

    int n = Mathf.Clamp(count, 1, stacks[idx].count);
    RemovePieces(idx, n);
    AddPieces(to, seat, kind, n);
}
```

Delete the `if ((PieceKind)kind == PieceKind.Bridge)` branch (`PlateauGame.cs:498-507`) and the
"count is ignored" clause from its doc comment with it.

```csharp
/// <summary>
/// Lay a bridge from reserve across <paramref name="toEdge"/>. The network is seeded from the
/// central plateau — plateauRules.md's "a plateau already connected by that player's bridges",
/// with the centre as the seed because that is where a first bridge must come from.
/// </summary>
[ServerRpc(RequireOwnership = false)]
public void RequestPlaceBridgeServerRpc(byte fromPlateau, byte toEdge, int epoch,
                                        ServerRpcParams rpcParams = default)
{
    if (!boardLive.Value || epoch != boardEpoch.Value)
    {
        return;
    }

    PlateauBoard board = PlateauBoard.Instance;
    if (board == null || !board.IsBaked || fromPlateau >= board.PlateauCount)
    {
        return;
    }

    int seat = SeatForClient(rpcParams.Receive.SenderClientId);
    if (seat < 0)
    {
        return;
    }

    int idx = FindStack(fromPlateau, seat, (int)PieceKind.Bridge);
    if (idx < 0)
    {
        return;                          // no bridge of the sender's in reserve there
    }

    if (!TryBuildView(seat, -1, out PlateauMoveRules.View view) ||
        !PlateauMoveRules.IsLegalBridgeEdge(view, toEdge))
    {
        return;                          // the client's highlight is a hint; this is the rule
    }

    RemovePieces(idx, 1);
    placedBridges.Add(new PlacedBridge(toEdge, seat));
}

/// <summary>
/// Pick up a bridge that has already been laid and put it in another gap. Legality is computed with
/// the network seeded from the two plateaus this bridge currently spans, so it can only be re-laid
/// around the plateau system it is actually touching. See bridgeMovementUpdate.md.
/// </summary>
[ServerRpc(RequireOwnership = false)]
public void RequestMoveBridgeServerRpc(byte fromEdge, byte toEdge, int epoch,
                                       ServerRpcParams rpcParams = default)
{
    if (!boardLive.Value || epoch != boardEpoch.Value)
    {
        return;
    }

    PlateauBoard board = PlateauBoard.Instance;
    if (board == null || !board.IsBaked)
    {
        return;
    }

    int seat = SeatForClient(rpcParams.Receive.SenderClientId);
    if (seat < 0)
    {
        return;
    }

    int existing = FindPlacedBridge(fromEdge);
    if (existing < 0 || placedBridges[existing].seat != seat)
    {
        return;                          // not the sender's bridge to move
    }

    if (!TryBuildView(seat, fromEdge, out PlateauMoveRules.View view) ||
        !PlateauMoveRules.IsLegalBridgeEdge(view, toEdge))
    {
        return;
    }

    placedBridges[existing] = new PlacedBridge(toEdge, seat);
}
```

`toEdge == fromEdge` needs no explicit guard: the bridge is still in `anyBridge`, so
`IsCandidateEdge` refuses its own gap, and `SamePair` refuses its twin bar.

### 4.6 — `PlateauSelection` resolves the gap client-side

**`RecomputeLegal` (`:551-584`)** — the call is unchanged; the comment is not, because its claim is
now the opposite of what happens:

```csharp
// The network is seeded from the two plateaus this bridge currently spans, rather than from the
// central plateau. The bridge is NOT lifted out of the graph: the gap it sits in stays occupied,
// and everything on the far side of it stays in the system. See bridgeMovementUpdate.md.
int movingEdge = selected.IsPlacedBridge ? selected.edge : -1;
if (!game.TryBuildView(seat, movingEdge, out PlateauMoveRules.View view))
{
    return;
}
```

`from` (`:574`) can stay `board.CentralPlateau` for a placed bridge — `BridgeDestinations` ignores
the origin entirely, and `LegalDestinations` only bounds-checks it. Leave the comment there that
says so.

**`UpdateSelected` (`:341-410`)** — remember which *bar* the cursor is on, not only which plateau it
names:

```csharp
// A Bridge Spot resolves to the plateau its edge would connect to, but only when that edge is one
// of THIS selection's current candidates.
int hitCandidateEdge = -1;
if (hitEdge >= 0)
{
    hitPlateau = ResolveBridgeSpotPlateau(game, hitEdge);
    // Keep the bar itself, not just the plateau it names. Two free bars can reach the same plateau
    // from two different plateaus in your system, and the server no longer guesses between them.
    hitCandidateEdge = hitPlateau >= 0 ? hitEdge : -1;
}

// ...

if (inputs.RightMainTriggerDown)
{
    pressedPiece = mine;
    bool onGap = mine == null && overLegal;
    pressedPlateau = onGap ? hitPlateau : -1;
    pressedEdge = onGap ? hitCandidateEdge : -1;
}
else if (inputs.RightMainTriggerUp)
{
    // ... piece branch unchanged ...
    else if (pressedPlateau >= 0 && pressedPlateau == hitPlateau)
    {
        Send(game, pressedPlateau, pressedEdge);
    }

    pressedPiece = null;
    pressedPlateau = -1;
    pressedEdge = -1;
}
```

Add `int pressedEdge = -1;` beside `pressedPlateau` (`:58`) and reset it in `Cancel()` (`:534-547`)
alongside the rest.

**`Send` (`:492-514`)**:

```csharp
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

        // destinationEdge < 0 means the click landed on the plateau, not on one of its bars. Name
        // the gap here rather than on the server: the client is the side that knows which bar was
        // tinted, and resolving in one place means the two can never pick differently.
        if (edge < 0 &&
            (!game.TryBuildView(seat, movingEdge, out PlateauMoveRules.View view) ||
             !PlateauMoveRules.TryResolveBridgeEdge(view, destination, out edge)))
        {
            return;                      // deliberately BEFORE the lockout: a failed resolve is
        }                                // not a move, and must not eat the player's next second

        sendLockUntil = Time.unscaledTime + SendLockout;

        // No local prediction — see the note below on RoomAnchor.worldHolder.
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
    game.RequestMoveServerRpc(selected.plateau, selected.kind, (byte)count,
                              (byte)destination, armedEpoch);
    Cancel();
}
```

---

## 5 — What deliberately does not change

- **Troop, parshendi and shardbearer movement.** `Search` (`PlateauMoveRules.cs:90-154`) never calls
  `ConnectedComponent` and never reads `central`. `RecomputeLegal` passes `movingEdge = -1` for
  every non-bridge selection, and `RequestMoveServerRpc` passes -1 too.
- **Placing a bridge from reserve.** Seeded from the central plateau, exactly as now. Reserve
  bridges only ever sit on the central plateau, so this is the same set of destinations it produces
  today, and your "first placement is correct" stays correct.
- **One bridge per gap, whoever owns it.** `anyBridge` is untouched.
- **The twin-bar allowance** for the six duplicated central connections, for placement. It is only
  narrowed for a *move*, and only for the pair the moving bridge already spans (§4.3).
- **The server re-runs the client's rule.** That contract is the point of `PlateauMoveRules`; the
  change keeps it by giving the server `IsLegalBridgeEdge` rather than a second implementation.
- **No local prediction.** `Send` still fires the RPC and cancels; the board updates when the server
  says so.
- **Turn limits.** `plateauRules.md`'s "one bridge may be moved per turn" is still unimplemented —
  any player may move any of their bridges at any time, exactly as before.

---

## 6 — The consequence you accepted

A bridge can still be moved so that your own network is cut off from the centre. With A on
`central–P1` and B on `P1–P2`, A's local system is `{central, P1, P2}`, so `P2–P3` is a legal
destination — and afterwards nothing connects `central` to anything.

You chose to allow it. Two things make that cheaper than it was in `bigFixes1.md` §4, where the same
outcome was described as permanently orphaning the network:

- **A detached network is not frozen.** Every bridge in it seeds its own search from its own ends, so
  the player can walk the network one gap at a time — including back toward the centre, as soon as a
  gap with one end in the detached system reaches a plateau adjacent to it. §4 believed otherwise
  because it was still assuming a central-seeded component.
- **A detached network cannot be jumped back to.** That is the second half of your rule, and it is
  what makes the first half safe to allow: the mistake is recoverable by play, not by teleporting a
  bridge across the board.

New bridges bought from reserve still only extend from the centre, so a player who strands
themselves has to walk back, not rebuild. That asymmetry is deliberate and worth keeping.

If this turns out to be too punishing at the table, the guard is one extra `ConnectedComponent` call
in `IsCandidateEdge` — "after this move, is every one of my bridges still reachable from the central
plateau?" — and it belongs there, beside the rest of the rule, so client and server keep agreeing.
Do not add it speculatively.

---

## 7 — Verification

Editor, keyboard fallback, one player. `.` is the right trigger, arrow keys are the right joystick
(`CLAUDE.md` §"Build and test"). Select `World Root > Plateau Game` first so the graph gizmo is
visible while testing.

Read the "Expect" column as the **candidate gap set**. On screen that shows up two ways at once, both
from the same set: each candidate gap's outside end **glows** as a destination plateau, and the bar
itself takes the faint candidate tint. "Free gaps off `P1`" therefore means P1's empty bars tint and
the plateaus on their far side glow — P1 itself does not, because it is already in the system.

| # | Setup | Select | Expect |
| --- | --- | --- | --- |
| 1 | Fresh Chasms | bridge in reserve | The 6 plateaus around the centre glow, and their bars tint faintly. **Unchanged from today** |
| 2 | A on `central–P1` | second bridge in reserve | Centre's 5 remaining free neighbours + P1's free neighbours. **Unchanged from today** |
| 3 | A on `central–P1`, B on `P1–P2` | **A** | Free gaps off `central`, `P1` **and** `P2`. Today this shows only the 6 central neighbours — this row is the bug being fixed |
| 4 | as 3 | **B** | Free gaps off `central`, `P1` and `P2` — the same set, since lifting B does not split anything |
| 5 | Move A to `P2–P3` (legal per §6) | **A** | Free gaps off `P1`, `P2`, `P3` only. The now-empty `central–P1` bar **is** a candidate (one end in the system) and `central` glows. Every other gap at `central` — both ends outside the system — must stay dark. **This row is the new restriction** |
| 6 | as 5 | **B** (`P1–P2`) | Free gaps off `P1`, `P2`, `P3`. Same reasoning: `P3` is reachable from `P2` over A |
| 7 | A bridge whose local system has no free adjacent gap | that bridge | Nothing glows and the count label turns **red** (`SetSelectedOverlay`'s `legal.Count == 0`) |
| 8 | A plateau adjacent to your system through **two** free bars | any bridge | Click one tinted bar → the bridge lands on **that** bar. Today it lands on the lower edge index regardless |
| 9 | Two clients, seats 0 and 1 | either | A gap the other player has bridged never glows; the other player's bridges never appear in your component |
| 10 | Selection live, press `Chasms` | any | Epoch bumps, selection cancels, no RPC lands (`armedEpoch` check) |

Rows 3, 5 and 8 are the change. Rows 1, 2, 9 and 10 are the regression net.

**Server parity.** The client and server now share `IsLegalBridgeEdge`, so a highlight the server
refuses can only come from a graph disagreement — which already logs (`PlateauGame.SyncMirror`'s
`graphHash` error). If a bridge visibly refuses to move with no console output, the bug is in the
`View` construction, not in the rule: log `view.movingEdge` on both sides first.

---

## 8 — Documentation to update in the same commit

**`plateauRules.md:61`** — the sentence this whole document is a reading of. Sharpen it:

> **Bridge** | One bridge may be moved per turn. A bridge already on the board may be re-laid across
> any empty gap with one end in the group of plateaus it currently connects — that is, the plateaus
> reachable from either of its own ends over that player's bridges. A bridge placed from reserve is
> laid from the group connected to the central plateau.

**`CLAUDE.md` §"Movement — `PlateauMoveRules`"** — the Bridge row of that table currently reads
*"closure of the **central plateau** over their own bridges"*, which becomes true only for placement.
Split it in two:

> | Bridge (from reserve) | exactly one end inside the closure of the **central plateau** over the player's own bridges, the other outside, and no bridge of anyone's already there — EXCEPT the free twin bar of a pair this player already bridged |
> | Bridge (already laid) | the same, but the closure is seeded from **that bridge's own two ends**, so it can only be re-laid around the plateau system it is touching. Its own pair is excluded |

Also update the paragraph below that table — *"the bridge network is seeded with the central plateau
(without that seed no first bridge could ever be placed and the game deadlocks)"* — to say the seed
is the central plateau **for placement** and the bridge's own ends **for a move**, and the
"**A bridge is targeted by plateau, like everything else**" paragraph, which is no longer true for
bridges: the client resolves the gap and the RPC carries an edge index.

**`bigFixes1.md` §4** — add one line at its head pointing at this document, so the three ranked
options are not read later as still open.

---

## 9 — Smaller things noticed while in here

- **Edge indices are `byte`s, and `PlateauConst.NoIndex` is 255.** With `toEdge` now also on the
  wire as a byte, both ends of every bridge RPC are capped at 255 edges, and index 255 is unusable
  because `PlateauPieceTag.edge` uses it as "not placed". 81 bars today, so this is not live — but
  it now has two more callers than when `bigFixes1.md` §4 flagged it. One `Debug.Assert` at the end
  of `PlateauBoard`'s bake (`edges.Count < PlateauConst.NoIndex`) costs nothing and turns a silent
  refusal-to-move into a named failure.
- **`s_component` is a single shared static.** `BridgeDestinations`, `CandidateBridgeEdges`,
  `TryResolveBridgeEdge` and now `IsLegalBridgeEdge` each recompute the component into it. That is
  fine today — single-threaded, sequential, and each call fully computes before it reads — but
  `IsLegalBridgeEdge` makes it four callers, so it is worth the one-line comment saying the array is
  never valid across a call boundary.
- **`ResolveBridgeSpotPlateau` (`:624-643`)** picks the end that is in `legal`, which is the same
  answer as `FarEnd` for every genuine candidate. It stays correct, and it now only feeds the
  *highlight* — the actual destination travels as `pressedEdge` — so a mis-pick there can no longer
  put a bridge in the wrong gap. Worth rewriting it in terms of `FarEnd` if `PlateauMoveRules` ever
  exposes it publicly; not worth widening that surface for on its own.
- **`RecomputeLegal` runs on every `BoardChanged`.** Unchanged by this work, and still cheap: 81
  edges, 41 plateaus, all static buffers.

---

## 10 — Order of work

1. §4.1 – §4.3, `PlateauMoveRules` alone. Self-contained and the only file with real logic in it.
2. §4.4, `TryBuildView`. Two lines and a rename; the compiler finds the three call sites.
3. §4.5 – §4.6 together — the RPC signatures and `PlateauSelection` must change in one step or
   nothing moves. Compile-check before entering Play mode; the RPC surface is code-generated.
4. Verification table §7, rows 1–7 first (single client, Editor), then 8–10.
5. §8, the three documentation edits, in the same commit as the code.
6. §9's assert, if you want it. Independent of everything above.
