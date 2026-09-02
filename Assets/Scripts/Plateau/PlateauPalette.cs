using UnityEngine;

/// <summary>
/// Owner colours, indexed by PlayerControls.spawnSlot.
///
/// plateauRules.md: "Every piece sits on a colored circle indicating its owner." The circle is the
/// Cube child on each piece prefab, which already carries the alpha-blended ClearWhite material —
/// so this is written into a MaterialPropertyBlock rather than into 36 hand-authored materials.
/// </summary>
public static class PlateauPalette
{
    /// <summary>The owner disc is authored at alpha 0.035 (invisible). Opaque enough to read.</summary>
    public const float DiscAlpha = 0.85f;

    public static Color ForSeat(int seat)
    {
        int s = seat < 0 ? 0 : seat % PlateauConst.MaxSeats;

        // Stride 5 is coprime with 12, so consecutive seats land far apart on the wheel. Seats
        // 0-3 — the order PlayerRing.PickFreeSlot hands them out — get 0, 150, 300 and 90 degrees.
        float hue = ((s * 5) % PlateauConst.MaxSeats) / (float)PlateauConst.MaxSeats;

        // Alternating value, so neighbouring seats differ in brightness as well as hue. Passthrough
        // washes colour out badly, and hue alone is no use to a red/green colour-blind player.
        float value = (s % 2 == 0) ? 1f : 0.68f;

        return Color.HSVToRGB(hue, 0.85f, value);
    }

    public static Color DiscFor(int seat)
    {
        if (seat < 0 || seat >= PlateauConst.MaxSeats)
        {
            // PlateauConst.NeutralSeat (Gemheart/Chasmfiend) and any other out-of-range seat: no
            // owner, so no owner-colour ring. The disc's own authored colour, alpha 0.035 — see the
            // class doc comment — already reads as invisible.
            return new Color(1f, 1f, 1f, 0.035f);
        }

        Color c = ForSeat(seat);
        c.a = DiscAlpha;
        return c;
    }
}
