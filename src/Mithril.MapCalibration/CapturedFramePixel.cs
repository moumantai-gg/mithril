namespace Mithril.MapCalibration;

/// <summary>
/// A point in the full OS-captured-frame pixel space: origin at the captured
/// frame's top-left, X right, Y down. Source for <see cref="MapRect.OriginX"/>/
/// <see cref="MapRect.OriginY"/> when describing a located rect, and for
/// <c>LocateMetrics.Tx/Ty</c> from refiner outputs.
/// </summary>
public readonly record struct CapturedFramePixel(double X, double Y, double Z) : IPixelPoint
{
    public CapturedFramePixel(double x, double y) : this(x, y, 0) { }
    public static CapturedFramePixel Zero => new(0, 0, 0);

    public double DistanceTo(CapturedFramePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(CapturedFramePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
