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
    /// <summary>
    /// Untyped texture→screenshot projection retained for diagnostic-only
    /// consumers (the AttemptBundleVisualizer overlays texture-frame inliers
    /// back onto the captured screenshot for the bundle dump). The forward
    /// direction is typed — use <see cref="CroppedToTexture"/> /
    /// <see cref="TextureToCropped"/> for production paths.
    /// </summary>
    public (double Sx, double Sy) TextureToScreenshot(double tx, double ty)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return (tx / scaleX + OriginX, ty / scaleY + OriginY);
    }

    /// <summary>
    /// Typed projection from a cropped-frame pixel (the screenshot the detector
    /// consumed) into the base-texture frame. Only valid for crop-aligned
    /// <see cref="MapRect"/> instances — see §5.1 of the pixel-frame-typing
    /// spec (#1076). For located-rect cases use <see cref="LocatedMapRect"/>.
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
