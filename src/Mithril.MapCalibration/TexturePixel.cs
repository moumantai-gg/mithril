namespace Mithril.MapCalibration;

/// <summary>
/// A point in the canonical base-texture pixel frame: origin at the texture's
/// top-left, X right, Y down. Z is always 0 today; carried for symmetry with
/// <see cref="WorldCoord"/> and to keep the IPixelPoint shape uniform across
/// all frames.
/// </summary>
public readonly record struct TexturePixel(double X, double Y, double Z) : IPixelPoint
{
    public TexturePixel(double x, double y) : this(x, y, 0) { }
    public static TexturePixel Zero => new(0, 0, 0);

    public double DistanceTo(TexturePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public double DistanceSquaredTo(TexturePixel other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return dx * dx + dy * dy;
    }
}
