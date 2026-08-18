using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where the players stand: a ring of evenly spaced slots around the game board.
///
/// The board sits at the centre of the scene — the world origin — so the ring is centred on
/// (0, 0, 0) and every slot is on the floor at y = 0, because MR has a real floor. Nothing here
/// is networked and nothing here moves the shared world: a slot is only ever used as the anchor
/// for *this client's* rig (CameraController2.PlaceAtRingSlot), the way the seating system it
/// replaced worked, so every networked value in the project stays in world space.
///
/// Adding a game should mean putting its board at the origin and nothing else. If a board needs
/// more elbow room, change <see cref="Radius"/> here rather than per scene, so the ring and the
/// board cannot drift apart.
/// </summary>
public static class PlayerRing
{
    /// <summary>
    /// Places on the ring. 12 matches the Relay allocation in RelayVivox.CreateRelay, so a full
    /// room can never contain more players than there are places to stand.
    /// </summary>
    public const int SlotCount = 12;

    /// <summary>
    /// Metres from the centre of the board to a player's feet. 2 m is where the rig was authored
    /// relative to the board, and it was the innermost seat arc before that.
    /// </summary>
    public const float Radius = 2f;

    /// <summary>
    /// Slot 0 is on the -Z side of the board — where the room owner stood under the old seating
    /// code — and the rest run clockwise seen from above.
    /// </summary>
    public static Vector3 SlotPosition(int slot)
    {
        float theta = Wrap(slot) * 2f * Mathf.PI / SlotCount;
        return new Vector3(Radius * Mathf.Sin(theta), 0f, -Radius * Mathf.Cos(theta));
    }

    /// <summary>Facing the middle of the board, level with the floor.</summary>
    public static Quaternion SlotRotation(int slot)
    {
        Vector3 towardCentre = -SlotPosition(slot);
        if (towardCentre.sqrMagnitude < 0.0001f)
        {
            return Quaternion.identity;
        }
        return Quaternion.LookRotation(towardCentre.normalized, Vector3.up);
    }

    /// <summary>
    /// The middle of the widest empty stretch of ring. One player stands at slot 0, the second
    /// opposite them, the third and fourth on the quarters, and after that nobody is ever more
    /// than a quarter-circle from their neighbours.
    ///
    /// This deliberately does not re-space the players who are already here. Teleporting somebody
    /// mid-game because a third player joined is an involuntary rig move, and those are nauseating
    /// in passthrough — the same reason CameraController2 never re-places the rig per frame. The
    /// cost is that counts a 12-slot ring cannot divide evenly (5, 7, 8, …) come out approximate;
    /// the gaps stay within 30-90 degrees of each other, which reads as a circle around a board.
    /// </summary>
    public static int PickFreeSlot(ICollection<int> occupied)
    {
        if (occupied == null || occupied.Count == 0)
        {
            return 0;
        }

        List<int> taken = new List<int>(occupied);
        taken.Sort();

        int bestSlot = -1;
        // A stretch one slot wide has no free slot inside it, so it can never win. Starting here
        // means bestSlot stays -1 exactly when the ring is full.
        int widest = 1;

        for (int i = 0; i < taken.Count; i++)
        {
            int from = taken[i];
            int to = i + 1 < taken.Count ? taken[i + 1] : taken[0] + SlotCount;
            int width = to - from;

            if (width > widest)
            {
                widest = width;
                bestSlot = Wrap(from + width / 2);
            }
        }

        // Every place is taken, which means the room holds more players than Relay allocates for.
        // Doubling up on slot 0 beats dropping somebody at an undefined position.
        return bestSlot < 0 ? 0 : bestSlot;
    }

    private static int Wrap(int slot)
    {
        return ((slot % SlotCount) + SlotCount) % SlotCount;
    }
}
