namespace Mithril.MapCalibration;

/// <summary>
/// A point in the cropped-frame pixel space the detector consumed: origin at
/// the located map rect's top-left within the captured frame, X right, Y down.
/// Source for <c>TypedDetection.AnchorX/AnchorY</c>.
/// </summary>
public readonly record struct CroppedFramePixel(double X, double Y, double Z) : IPixelPoint
{
    public CroppedFramePixel(double x, double y) : this(x, y, 0) { }
    public static CroppedFramePixel Zero => new(0, 0, 0);

    public double DistanceTo(CroppedFramePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(CroppedFramePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
