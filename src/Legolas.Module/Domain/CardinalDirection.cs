namespace Legolas.Domain;

public enum CardinalDirection
{
    N,
    NE,
    E,
    SE,
    S,
    SW,
    W,
    NW
}

public static class CardinalDirectionExtensions
{
    public static double ToBearingRadians(this CardinalDirection dir) => dir switch
    {
        CardinalDirection.N => 0,
        CardinalDirection.NE => Math.PI / 4,
        CardinalDirection.E => Math.PI / 2,
        CardinalDirection.SE => 3 * Math.PI / 4,
        CardinalDirection.S => Math.PI,
        CardinalDirection.SW => 5 * Math.PI / 4,
        CardinalDirection.W => 3 * Math.PI / 2,
        CardinalDirection.NW => 7 * Math.PI / 4,
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null)
    };

    public static MetreOffset ToOffset(this CardinalDirection dir, double distanceMetres)
    {
        var rad = dir.ToBearingRadians();
        return new MetreOffset(
            East: Math.Sin(rad) * distanceMetres,
            North: Math.Cos(rad) * distanceMetres);
    }

    /// <summary>
    /// 8-point compass direction of a world-space delta, the inverse of
    /// <see cref="ToBearingRadians"/> (#113 Layer 1). Uses the established map
    /// convention — bearing is <c>atan2(East, North)</c> measured clockwise
    /// from map-north — so a target due +North reads <see cref="CardinalDirection.N"/>
    /// and due +East reads <see cref="CardinalDirection.E"/>. World axes:
    /// East = ΔX, North = ΔZ (the same ground-plane frame
    /// <see cref="WorldToOverlayCalibration.ToOverlay(WorldCoord)"/> consumes; the
    /// <c>MirrorNorth</c> handedness is a rendering-only concern and is
    /// deliberately not applied here — relative phrasing is frame-internal and
    /// reflection-invariant for the player reading it on their own screen).
    /// A zero vector resolves to <see cref="CardinalDirection.N"/> (callers pick
    /// the nearest non-coincident reference, so this is unreached in practice).
    /// </summary>
    public static CardinalDirection FromBearing(double east, double north)
    {
        // atan2(E, N): 0 = due north, +clockwise — matches ToBearingRadians.
        var angle = Math.Atan2(east, north);
        if (angle < 0) angle += 2 * Math.PI;
        // Round to the nearest 45° sector; the %8 wraps NW(7)→N(0).
        var sector = (int)Math.Round(angle / (Math.PI / 4), MidpointRounding.AwayFromZero) % 8;
        return (CardinalDirection)sector;
    }

    public static bool TryParse(ReadOnlySpan<char> s, out CardinalDirection direction)
    {
        if (s.Equals("N", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.N; return true; }
        if (s.Equals("NE", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.NE; return true; }
        if (s.Equals("E", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.E; return true; }
        if (s.Equals("SE", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.SE; return true; }
        if (s.Equals("S", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.S; return true; }
        if (s.Equals("SW", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.SW; return true; }
        if (s.Equals("W", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.W; return true; }
        if (s.Equals("NW", StringComparison.OrdinalIgnoreCase)) { direction = CardinalDirection.NW; return true; }
        direction = default;
        return false;
    }
}
