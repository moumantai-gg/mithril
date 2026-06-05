namespace Mithril.MapCalibration;

/// <summary>
/// A point in the PG game-window client-area pixel space: origin at the DWM
/// client-area top-left. Convert via <c>MapCaptureRect</c> before crossing
/// into captured-frame code.
/// </summary>
public readonly record struct GameWindowPixel(double X, double Y, double Z) : IPixelPoint
{
    public GameWindowPixel(double x, double y) : this(x, y, 0) { }
    public static GameWindowPixel Zero => new(0, 0, 0);

    public double DistanceTo(GameWindowPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(GameWindowPixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
