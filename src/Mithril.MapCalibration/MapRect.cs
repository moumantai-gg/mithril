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

    /// <summary>
    /// Typed projection from a cropped-frame pixel (the screenshot the detector
    /// consumed) into the base-texture frame. Replaces the legacy
    /// double-based <see cref="ScreenshotToTexture"/> for crop-aligned cases
    /// (where the screenshot equals the crop and origin is (0,0)).
    ///
    /// Only valid for crop-aligned <see cref="MapRect"/> instances — see §5.1
    /// of the pixel-frame-typing spec (#1076). For located-rect cases use
    /// <see cref="LocatedMapRect"/>.
    /// </summary>
    public TexturePixel CroppedToTexture(CroppedFramePixel pixel)
    {
        var sx = TextureWidth / (double)Width;
        var sy = TextureHeight / (double)Height;
        return new TexturePixel((pixel.X - OriginX) * sx, (pixel.Y - OriginY) * sy);
    }

    /// <summary>Inverse of <see cref="CroppedToTexture"/>.</summary>
    public CroppedFramePixel TextureToCropped(TexturePixel pixel)
    {
        var sx = Width / (double)TextureWidth;
        var sy = Height / (double)TextureHeight;
        return new CroppedFramePixel(pixel.X * sx + OriginX, pixel.Y * sy + OriginY);
    }
}
