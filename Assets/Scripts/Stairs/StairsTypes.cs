using System;
using Unity.Netcode;

/// <summary>
/// The four states a Stairs turn can be in. These numbers go on the wire, so they may be appended
/// to but never renumbered.
/// </summary>
public enum StairsPhase
{
    /// <summary>Each seated player drops their pawn on an empty space. stepsRules.md "Setup".</summary>
    Setup = 0,
    /// <summary>The player whose turn it is may walk their pawn, then press End Turn.</summary>
    Move = 1,
    /// <summary>They place exactly <see cref="StairsGame.stepsToPlace"/> steps. "Action 2: Building".</summary>
    Build = 2,
    /// <summary>Somebody reached 12 captures, or a supply ran out.</summary>
    GameOver = 3,
}

public static class StairsConst
{
    /// <summary>This game's menu key, matching the gameKey on StairsModule.asset.</summary>
    public const string GameKey = "Stairs";

    /// <summary>The extra key StairsModule.asset declares. Dispatched by StairsGame.InvokeMenuAction.</summary>
    public const string NewGameKey = "New Game";

    /// <summary>StairsGame's board is 8x8. Both the row count and the cells per row are checked
    /// against this at bake time, because everything downstream indexes row * Size + column.</summary>
    public const int Size = 8;
    public const int CellCount = Size * Size;

    /// <summary>Stairs is a two-player game. Everybody else in the room spectates.</summary>
    public const int Seats = 2;

    /// <summary>stepsRules.md gives no supply size; 40 a side is what the room was asked for.</summary>
    public const int StepsPerPlayer = 40;

    /// <summary>How the supply is stacked on a player's console. 4 x 10 keeps each stack about
    /// 30 cm tall at board scale 1 — tall enough to point at, short enough not to hide the board.</summary>
    public const int SupplyStacks = 4;

    /// <summary>stepsRules.md "Winning the Game": first to twelve captured tiles.</summary>
    public const int CapturesToWin = 12;

    /// <summary>"no cell". Not 0 — 0 is the corner nearest seat 0.</summary>
    public const int NoCell = -1;

    /// <summary>"nobody sits here" for StairsSeat.ringSlot, and "no winner" for StairsGame.winner.</summary>
    public const int NoSeat = -1;

    // --- Load-bearing object names. Every one of these is resolved at runtime by exact name, so a
    // rename compiles fine and fails in the headset. They are listed in Assets/Scripts/Stairs/CLAUDE.md.

    /// <summary>The content frame: World Root > Stairs Root. Carries StairsGame and StairsBoard, and
    /// everything this game builds hangs under it. NOT under Board, which is scaled (2, 0.02, 2).</summary>
    public const string RootName = "Stairs Root";

    /// <summary>World Root > Board > Cells. Its grandchildren are the 64 cells.</summary>
    public const string CellsName = "Cells";

    /// <summary>World Root > Board — the slab the pointer ray lands on for a bare cell.</summary>
    public const string BoardName = "Board";

    /// <summary>The TextMeshPro child on Step1/Step2 that shows the tower height.</summary>
    public const string StepLabelName = "Height";

    /// <summary>Unity's built-in "Ignore Raycast". Physics.DefaultRaycastLayers already excludes it,
    /// which is what keeps a drag ghost from blocking the beam that is positioning it.</summary>
    public const int IgnoreRaycastLayer = 2;

    /// <summary>Seat 0 is Steps1/Player1 (blue), seat 1 is Steps2/Player2 (green). The console
    /// labels say the colour rather than a username: a player's console is already on their own
    /// side of the board, and a spectator sees both.</summary>
    public static string SeatName(int seat)
    {
        return seat == 0 ? "Blue" : seat == 1 ? "Green" : "-";
    }

    /// <summary>The other seat. Two players, so this is the whole of "opponent".</summary>
    public static int Opponent(int seat)
    {
        return 1 - seat;
    }

    public static bool IsSeat(int seat)
    {
        return seat >= 0 && seat < Seats;
    }

    public static bool IsCell(int cell)
    {
        return cell >= 0 && cell < CellCount;
    }
}

/// <summary>
/// What is stacked on one cell.
///
/// <see cref="owner"/> is only meaningful when <see cref="height"/> is non-zero, and a tower only
/// ever has ONE owner: stepsRules.md lets you build on empty squares or on your own tiles, never on
/// the opponent's, and a capture takes the whole tower off at once. Everything that writes this goes
/// through StairsGame.SetTower so that invariant has one place to hold.
/// </summary>
public struct StairsTower : INetworkSerializable, IEquatable<StairsTower>
{
    public byte height;
    public byte owner;

    public static StairsTower Empty => new StairsTower();

    public StairsTower(int owner, int height)
    {
        this.height = (byte)height;
        this.owner = (byte)(height == 0 ? 0 : owner);
    }

    public bool IsEmpty => height == 0;

    /// <summary>The seat whose tiles these are, or StairsConst.NoSeat for a bare cell.</summary>
    public int Owner => height == 0 ? StairsConst.NoSeat : owner;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref height);
        serializer.SerializeValue(ref owner);
    }

    public bool Equals(StairsTower other) => height == other.height && owner == other.owner;
    public override bool Equals(object obj) => obj is StairsTower t && Equals(t);
    public override int GetHashCode() => (height << 8) | owner;
    public override string ToString() => height == 0 ? "empty" : owner + "x" + height;
}

/// <summary>
/// One of the two playing seats.
///
/// <see cref="ringSlot"/> is a PlayerControls.spawnSlot — a place on PlayerRing, the only stable
/// per-player index in the project — and NOT the seat number. Keying the seat off the ring slot is
/// what lets a player who drops out and rejoins land back in their own game: PlayerRing.PickFreeSlot
/// hands a reconnecting player the slot they vacated, and StairsGame.ServeSeats matches on it.
/// A seat is therefore never released on disconnect; a third player in the room always spectates.
/// </summary>
public struct StairsSeat : INetworkSerializable, IEquatable<StairsSeat>
{
    /// <summary>PlayerControls.spawnSlot, or StairsConst.NoSeat while nobody holds this seat.</summary>
    public int ringSlot;

    /// <summary>Where this player's pawn stands, or StairsConst.NoCell before Setup places it.</summary>
    public int pawnCell;

    /// <summary>Steps still to build with. Reaching 0 in Build ends the game — stepsRules.md
    /// "Supply Exhaustion Win".</summary>
    public byte supply;

    /// <summary>Opponent tiles this player has taken. 12 wins.</summary>
    public byte captured;

    public static StairsSeat Fresh()
    {
        StairsSeat s = new StairsSeat();
        s.ringSlot = StairsConst.NoSeat;
        s.pawnCell = StairsConst.NoCell;
        s.supply = StairsConst.StepsPerPlayer;
        s.captured = 0;
        return s;
    }

    public bool IsOccupied => ringSlot >= 0;
    public bool HasPawnOnBoard => pawnCell >= 0;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ringSlot);
        serializer.SerializeValue(ref pawnCell);
        serializer.SerializeValue(ref supply);
        serializer.SerializeValue(ref captured);
    }

    public bool Equals(StairsSeat other) => ringSlot == other.ringSlot && pawnCell == other.pawnCell &&
                                            supply == other.supply && captured == other.captured;
    public override bool Equals(object obj) => obj is StairsSeat s && Equals(s);
    public override int GetHashCode() => (ringSlot << 20) ^ (pawnCell << 10) ^ (supply << 5) ^ captured;
}
