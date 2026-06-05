namespace Mithril.MapCalibration;

/// <summary>
/// A point in the Mithril overlay window's pixel space: origin at the overlay
/// window's top-left, X right, Y down. Source for all Legolas overlay rendering
/// and <c>IWorldOverlayMarkers</c> outputs.
/// </summary>
public readonly record struct OverlayPixel(double X, double Y, double Z) : IPixelPoint
{
    public OverlayPixel(double x, double y) : this(x, y, 0) { }
    public static OverlayPixel Zero => new(0, 0, 0);

    public double DistanceTo(OverlayPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(OverlayPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
