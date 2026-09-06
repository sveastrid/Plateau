using UnityEngine;

/// <summary>What a thing the pointer ray hit actually is.</summary>
public enum StairsPieceRole
{
    /// <summary>A step in a tower on the board. <see cref="StairsPieceTag.cell"/> is which cell.</summary>
    TowerStep = 0,
    /// <summary>A player's pawn, on the board or still on their console.</summary>
    Pawn = 1,
    /// <summary>An unplaced step on a player's console. The one thing Build drags onto the board.</summary>
    SupplyStep = 2,
    /// <summary>A captured opponent tile sitting in front of a player. Inert.</summary>
    CapturedStep = 3,
    /// <summary>The End Turn slab on a player's console.</summary>
    EndTurnKey = 4,
}

/// <summary>
/// Added by StairsView to everything it builds, so a raycast hit answers "what is this and whose"
/// in one GetComponentInParent rather than by walking names or comparing transforms.
///
/// GetComponentInParent, always: GamePiece.prefab carries colliders on both its root and its Cube
/// disc, and a step's collider is on the step root while its Height label is a child. A hit can
/// legitimately land on any of them.
/// </summary>
[DisallowMultipleComponent]
public class StairsPieceTag : MonoBehaviour
{
    public StairsPieceRole role;

    /// <summary>The seat this belongs to, or StairsConst.NoSeat for something unowned.</summary>
    public int seat = StairsConst.NoSeat;

    /// <summary>The board cell this stands on, or StairsConst.NoCell for anything on a console.</summary>
    public int cell = StairsConst.NoCell;

    public static StairsPieceTag Attach(GameObject go, StairsPieceRole role, int seat, int cell)
    {
        StairsPieceTag tag = go.GetComponent<StairsPieceTag>();
        if (tag == null)
        {
            tag = go.AddComponent<StairsPieceTag>();
        }
        tag.role = role;
        tag.seat = seat;
        tag.cell = cell;
        return tag;
    }
}
