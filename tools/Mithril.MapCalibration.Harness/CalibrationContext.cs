using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// What a calibration method receives when activated. Carries the area, the
/// landmark reference data, the loaded in-game-map image + its texture extent,
/// the base-texture output dimensions, and the current calibration (for
/// refinement methods). Owns the screenshot&#8596;texture transform so methods
/// emit in texture space without re-deriving the map-rect math.
///
/// <para>Coordinate frames (see the design spec): <b>screenshot pixel</b> is
/// what the user clicks / green-pixel detects; <b>texture pixel</b> is what the
/// solver and baseline JSON use. The transform maps a point inside
/// <see cref="MapRect"/> on the screenshot onto the
/// <see cref="TextureSize"/> output frame:
/// <c>texturePx = (screenshotPx − rect.origin) · textureSize / rect.size</c>.</para>
/// </summary>
public sealed class CalibrationContext
{
    public CalibrationContext(
        string area,
        IReadOnlyList<LandmarkRef> landmarks,
        ICalibrationImage? mapImage,
        MapRect? mapRect,
        (int W, int H) textureSize,
        AreaCalibration? currentCalibration)
    {
        Area = area;
        Landmarks = landmarks;
        MapImage = mapImage;
        MapRect = mapRect;
        TextureSize = textureSize;
        CurrentCalibration = currentCalibration;
    }

    public string Area { get; }

    /// <summary>The area's landmarks, from the common-lib readers.</summary>
    public IReadOnlyList<LandmarkRef> Landmarks { get; }

    /// <summary>The loaded in-game-map screenshot, WPF-free. Null in pure-solve tests.</summary>
    public ICalibrationImage? MapImage { get; }

    /// <summary>The base-texture extent within <see cref="MapImage"/>, in screenshot pixels.</summary>
    public MapRect? MapRect { get; }

    /// <summary>Base-texture dimensions — the output (texture-pixel) frame.</summary>
    public (int W, int H) TextureSize { get; }

    /// <summary>The stored calibration, for refinement methods. May be null.</summary>
    public AreaCalibration? CurrentCalibration { get; }

    /// <summary>
    /// Maps a screenshot-pixel coordinate to texture space using
    /// <see cref="MapRect"/> + <see cref="TextureSize"/>. Throws if
    /// <see cref="MapRect"/> is unset (a method that emits texture-space coords
    /// requires the map-rect to have been drawn).
    /// </summary>
    public TexturePixel ScreenshotToTexture(double sx, double sy)
    {
        var rect = RequireRect();
        var tx = (sx - rect.Left) * TextureSize.W / rect.Width;
        var ty = (sy - rect.Top) * TextureSize.H / rect.Height;
        return new TexturePixel(tx, ty);
    }

    /// <summary>
    /// Inverse of <see cref="ScreenshotToTexture"/>: maps a texture-pixel back
    /// onto the screenshot (used by the projection overlay to draw on the loaded
    /// image). Returns a screenshot-frame pair as <c>(double Sx, double Sy)</c>;
    /// the harness's own <c>MapRect</c> is screenshot-frame and the rendering
    /// surface doesn't (yet) have a dedicated frame-typed pixel.
    /// Throws if <see cref="MapRect"/> is unset.
    /// </summary>
    public (double Sx, double Sy) TextureToScreenshot(TexturePixel texture)
    {
        var rect = RequireRect();
        var sx = rect.Left + texture.X * rect.Width / TextureSize.W;
        var sy = rect.Top + texture.Y * rect.Height / TextureSize.H;
        return (sx, sy);
    }

    private MapRect RequireRect()
    {
        var rect = MapRect ?? throw new InvalidOperationException(
            "ScreenshotToTexture / TextureToScreenshot require a MapRect; none was set on the context.");
        // A zero/negative dimension would divide to Infinity/NaN silently; fail
        // loudly with a message distinct from the missing-rect case.
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            throw new InvalidOperationException(
                $"MapRect has a non-positive dimension (Width={rect.Width}, Height={rect.Height}); " +
                "the texture extent must be a positive rectangle.");
        }
        return rect;
    }
}
