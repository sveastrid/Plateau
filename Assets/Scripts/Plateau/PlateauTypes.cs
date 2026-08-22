using System;
using Unity.Netcode;

/// <summary>
/// The four kinds of piece in plateauRules.md.
///
/// These numbers go on the wire inside <see cref="PieceStack"/>, so they may be appended to but
/// never renumbered — a client on an older build would read every stack as the wrong piece.
/// </summary>
public enum PieceKind : byte
{
    Bridge      = 0,
    Troop       = 1,
    Parshendi   = 2,
    Shardbearer = 3,
}

public static class PlateauConst
{
    public const int KindCount = 4;

    /// <summary>Matches PlayerRing.SlotCount and RelayVivox's allocation of 12.</summary>
    public const int MaxSeats = 12;

    /// <summary>"no plateau" / "no edge". 255, not 0 — 0 is the central plateau.</summary>
    public const byte NoIndex = 255;

    /// <summary>
    /// The starting plateau, resolved by exact name in ChasmGame. Name-based like every other
    /// cross-object reference in this project; a rename logs rather than silently misbehaving.
    /// </summary>
    public const string CentralPlateauName = "Central Plateau";

    /// <summary>The Bridge Spots prefab's bar. Its world transform, not the spot root's, is the
    /// segment — the root's position is offset from it inside the prefab.</summary>
    public const string BridgeBarName = "Cylinder";

    /// <summary>Children of the piece prefabs, resolved by name.</summary>
    public const string CountChildName = "Count";
    public const string DiscChildName  = "Cube";

    /// <summary>
    /// plateauRules.md "Starting Forces": 2 bridges, 6 troops, 2 parshendi, 1 shardbearer.
    /// Indexed by (int)PieceKind.
    /// </summary>
    public static readonly byte[] StartingForces = { 2, 6, 2, 1 };

    /// <summary>plateauRules.md "Movement". -1 means unlimited.</summary>
    public static int BridgeBudget(PieceKind kind)
    {
        switch (kind)
        {
            case PieceKind.Troop:       return 2;   // "up to two bridges owned by that player"
            case PieceKind.Parshendi:   return -1;  // "as many bridges as it wants"
            case PieceKind.Shardbearer: return 2;   // "two bridges plus one jump"
            default:                    return 0;
        }
    }

    /// <summary>plateauRules.md "Movement". A jump crosses a faint line with no bridge on it.</summary>
    public static int JumpBudget(PieceKind kind)
    {
        switch (kind)
        {
            case PieceKind.Troop:       return 0;   // troops need bridges, full stop
            case PieceKind.Parshendi:   return 1;
            case PieceKind.Shardbearer: return 1;
            default:                    return 0;
        }
    }
}

/// <summary>
/// One "faint line between two plateaus" (plateauRules.md), derived from a Bridge Spots bar.
/// Always stored with <see cref="a"/> &lt; <see cref="b"/> so an unordered pair has one
/// representation and duplicates collapse.
/// </summary>
public struct BridgeEdge : INetworkSerializable, IEquatable<BridgeEdge>
{
    public byte a;
    public byte b;

    public BridgeEdge(int one, int two)
    {
        a = (byte)(one < two ? one : two);
        b = (byte)(one < two ? two : one);
    }

    public bool Touches(int p) => p == a || p == b;

    public byte Other(int end) => end == a ? b : a;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref a);
        serializer.SerializeValue(ref b);
    }

    public bool Equals(BridgeEdge other) => a == other.a && b == other.b;
    public override bool Equals(object obj) => obj is BridgeEdge e && Equals(e);
    public override int GetHashCode() => (a << 8) | b;
    public override string ToString() => a + "<->" + b;
}

/// <summary>
/// A piece on the board is a STACK, not a unit: one entry per (plateau, seat, kind) carrying how
/// many are there. That is what plateauRules.md describes — "Each piece displays a number showing
/// how many of that piece type occupy the space" — and what the Count child on the piece prefabs
/// is for. Four bytes, so a whole board is a few hundred.
/// </summary>
public struct PieceStack : INetworkSerializable, IEquatable<PieceStack>
{
    public byte plateau;
    /// <summary>PlayerControls.spawnSlot — the only stable per-player index in the project.</summary>
    public byte seat;
    public byte kind;
    /// <summary>Never 0. A stack that empties is removed from the list instead.</summary>
    public byte count;

    public PieceStack(int plateau, int seat, int kind, int count)
    {
        this.plateau = (byte)plateau;
        this.seat    = (byte)seat;
        this.kind    = (byte)kind;
        this.count   = (byte)count;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref plateau);
        serializer.SerializeValue(ref seat);
        serializer.SerializeValue(ref kind);
        serializer.SerializeValue(ref count);
    }

    public bool Equals(PieceStack other) => plateau == other.plateau && seat == other.seat &&
                                            kind == other.kind && count == other.count;
    public override bool Equals(object obj) => obj is PieceStack s && Equals(s);
    public override int GetHashCode() => (plateau << 16) | (seat << 8) | (kind << 4) | count;
}

/// <summary>A bridge that has been laid across an edge. One per edge, whoever owns it.</summary>
public struct PlacedBridge : INetworkSerializable, IEquatable<PlacedBridge>
{
    public byte edge;
    public byte seat;

    public PlacedBridge(int edge, int seat)
    {
        this.edge = (byte)edge;
        this.seat = (byte)seat;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref edge);
        serializer.SerializeValue(ref seat);
    }

    public bool Equals(PlacedBridge other) => edge == other.edge && seat == other.seat;
    public override bool Equals(object obj) => obj is PlacedBridge p && Equals(p);
    public override int GetHashCode() => (edge << 8) | seat;
}
