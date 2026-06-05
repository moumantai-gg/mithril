namespace Mithril.MapCalibration;

/// <summary>
/// A point in WPF Canvas pixel space (mouse-event coordinates): origin at the
/// canvas top-left, X right, Y down. Convert via <c>CanvasOverlayMapping</c>
/// before crossing into overlay-frame code.
/// </summary>
public readonly record struct CanvasPixel(double X, double Y, double Z) : IPixelPoint
{
    public CanvasPixel(double x, double y) : this(x, y, 0) { }
    public static CanvasPixel Zero => new(0, 0, 0);

    public double DistanceTo(CanvasPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(CanvasPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
