using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// stepsRules.md, in one place, used by the client to draw the highlight and by the server to
/// validate the move. The client's highlight is a hint; the server's answer is the rule — and the
/// only way those two cannot disagree is for there to be one implementation.
///
/// Readings taken where the rules are ambiguous, all commented at their use site:
///
///  - "Elevation Limits" is exactly one level, both ways. stepsRules.md's capture clause says
///    "from an adjacent space that is at least one level higher", which under an exact-one-level
///    movement rule can only ever be exactly one higher, so the two collapse into the same test.
///  - A capture takes the WHOLE tower and the pawn lands on the bare cell it vacated.
///  - Level 0 is the bare board, so a pawn on the board can always be described by the height of
///    the cell it stands on and nothing else. There is no separate "on the ground" state.
/// </summary>
public static class StairsMoveRules
{
    /// <summary>Momentum, stepsRules.md "Momentum Rule". 0 is a turn that has not moved yet.</summary>
    public const int DirectionFree = 0;
    public const int DirectionUp = 1;
    public const int DirectionDown = -1;

    /// <summary>
    /// A flat read-only snapshot of the board, filled from StairsGame's NetworkLists.
    ///
    /// Caller-owned arrays reused across calls: this is built once per frame on every client with a
    /// pawn selected, and once per RPC on the server, and a fresh 64-entry pair each time is garbage
    /// on a mobile GC.
    /// </summary>
    public struct View
    {
        public int[] height;        // StairsConst.CellCount
        public int[] owner;         // StairsConst.CellCount, StairsConst.NoSeat where empty
        public int[] pawnCell;      // StairsConst.Seats, StairsConst.NoCell before Setup places one

        public bool IsReady => height != null && owner != null && pawnCell != null;

        public void EnsureCapacity()
        {
            if (height == null || height.Length != StairsConst.CellCount)
            {
                height = new int[StairsConst.CellCount];
                owner = new int[StairsConst.CellCount];
            }
            if (pawnCell == null || pawnCell.Length != StairsConst.Seats)
            {
                pawnCell = new int[StairsConst.Seats];
            }
        }

        public bool HasPawnOn(int cell)
        {
            for (int s = 0; s < StairsConst.Seats; s++)
            {
                if (pawnCell[s] == cell)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // One shared scratch buffer for the eight neighbours. Every call site is on the main thread and
    // none of them holds the contents across a call, so eight ints is enough for all of them.
    static readonly int[] s_neighbours = new int[8];

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// stepsRules.md "Setup": a pawn goes on an empty space. Empty means bare board with nobody on
    /// it — not "a space with no pawn", or the second player could start on top of the first
    /// player's tiles, which no later rule would ever let them stand on.
    /// </summary>
    public static bool IsLegalPawnPlacement(View v, int cell)
    {
        if (!v.IsReady || !StairsConst.IsCell(cell))
        {
            return false;
        }
        return v.height[cell] == 0 && !v.HasPawnOn(cell);
    }

    // ------------------------------------------------------------------ movement

    /// <summary>
    /// Whether <paramref name="seat"/> may step onto <paramref name="to"/> right now, and whether
    /// doing so is a capture.
    ///
    /// <paramref name="direction"/> is the turn's momentum so far: DirectionFree for the first step
    /// of a turn, then locked to whichever way that step went.
    /// </summary>
    public static bool IsLegalMove(View v, int seat, int to, int direction, out bool capture)
    {
        capture = false;

        if (!v.IsReady || !StairsConst.IsSeat(seat) || !StairsConst.IsCell(to))
        {
            return false;
        }

        int from = v.pawnCell[seat];
        if (!StairsConst.IsCell(from) || !StairsBoard.AreAdjacent(from, to))
        {
            return false;
        }

        int level = v.height[from];
        int target = v.height[to];
        int step = target - level;

        // "The space you move to must be exactly one level higher or one level lower."
        if (step != DirectionUp && step != DirectionDown)
        {
            return false;
        }

        // "You cannot change vertical direction during a normal movement phase."
        if (direction != DirectionFree && step != direction)
        {
            return false;
        }

        // "You cannot move onto a space occupied by the opponent's pawn."
        int opponent = StairsConst.Opponent(seat);
        if (v.pawnCell[opponent] == to)
        {
            return false;
        }

        // "...or the opponent's tiles (unless you are making a capture)", and a capture is by
        // definition downward. Climbing onto their tiles is what this forbids; dropping onto them
        // is the whole of Action 1.
        if (target > 0 && v.owner[to] == opponent)
        {
            if (step != DirectionDown)
            {
                return false;
            }
            capture = true;
        }

        return true;
    }

    /// <summary>Every space the pawn may step to from where it stands. Cleared first.</summary>
    public static void CollectMoves(View v, int seat, int direction, List<int> into)
    {
        into.Clear();

        if (!v.IsReady || !StairsConst.IsSeat(seat))
        {
            return;
        }

        int from = v.pawnCell[seat];
        if (!StairsConst.IsCell(from))
        {
            return;
        }

        int count = StairsBoard.Neighbours(from, s_neighbours);
        for (int i = 0; i < count; i++)
        {
            if (IsLegalMove(v, seat, s_neighbours[i], direction, out bool _))
            {
                into.Add(s_neighbours[i]);
            }
        }
    }

    /// <summary>Which way a legal step went, so the turn's momentum can be locked to it.</summary>
    public static int DirectionOf(View v, int from, int to)
    {
        return v.height[to] > v.height[from] ? DirectionUp : DirectionDown;
    }

    // ------------------------------------------------------------------ building

    /// <summary>
    /// Whether <paramref name="seat"/> may lift the top tile off <paramref name="cell"/> and re-lay it
    /// somewhere else.
    ///
    /// Not in stepsRules.md — this is the "fix a misplacement" affordance from bugFixesStairsGame.md §3,
    /// and it is deliberately narrow: your own tiles only, the top of the stack only, and never a stack
    /// with a pawn standing on it, which would move the floor out from under somebody. Where the tile
    /// may then land is plain IsLegalBuild, so a re-laid tile can never end up on the opponent's tower
    /// and "a tower has exactly one owner" still holds.
    /// </summary>
    public static bool IsLegalStepLift(View v, int seat, int cell)
    {
        if (!v.IsReady || !StairsConst.IsSeat(seat) || !StairsConst.IsCell(cell))
        {
            return false;
        }

        if (v.height[cell] == 0 || v.owner[cell] != seat)
        {
            return false;
        }

        return !v.HasPawnOn(cell);
    }

    /// <summary>
    /// stepsRules.md "Where to Build": empty squares, or on top of your own tiles. You may build
    /// under your own pawn, and may not build under the opponent's.
    ///
    /// There is no height cap. Nothing in the rules gives one, a tower can only ever be as tall as
    /// the 40 tiles its owner started with, and a very tall tower is self-defeating anyway — it can
    /// only be climbed one level at a time.
    /// </summary>
    public static bool IsLegalBuild(View v, int seat, int cell)
    {
        if (!v.IsReady || !StairsConst.IsSeat(seat) || !StairsConst.IsCell(cell))
        {
            return false;
        }

        if (v.pawnCell[StairsConst.Opponent(seat)] == cell)
        {
            return false;
        }

        return v.height[cell] == 0 || v.owner[cell] == seat;
    }

    /// <summary>
    /// Whether there is anywhere at all left to build. On a 64-cell board this is effectively always
    /// true, but "effectively always" is how a turn ends up unable to finish and the room wedges, so
    /// StairsGame asks rather than assuming.
    /// </summary>
    public static bool AnyLegalBuild(View v, int seat)
    {
        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            if (IsLegalBuild(v, seat, cell))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Every space this player may drop a tile on. Cleared first.</summary>
    public static void CollectBuilds(View v, int seat, List<int> into)
    {
        into.Clear();
        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            if (IsLegalBuild(v, seat, cell))
            {
                into.Add(cell);
            }
        }
    }

    /// <summary>Every empty space a pawn may be dropped on during Setup. Cleared first.</summary>
    public static void CollectPawnPlacements(View v, List<int> into)
    {
        into.Clear();
        for (int cell = 0; cell < StairsConst.CellCount; cell++)
        {
            if (IsLegalPawnPlacement(v, cell))
            {
                into.Add(cell);
            }
        }
    }
}
