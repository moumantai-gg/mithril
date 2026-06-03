namespace Mithril.MapCalibration;

/// <summary>
/// Visible map's bounding box in the screenshot, plus the source texture's
/// native dimensions. Combined these give the screenshot↔texture transform.
/// </summary>
public sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return ((sx - OriginX) * scaleX, (sy - OriginY) * scaleY);
    }

    public (double Sx, double Sy) TextureToScreenshot(double tx, double ty)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return (tx / scaleX + OriginX, ty / scaleY + OriginY);
    }
}
