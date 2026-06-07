using Mithril.MapCalibration;

namespace Mithril.Overlay.Internal;

/// <summary>Default <see cref="IComposedOverlayCalibrationResolver"/>. Body
/// lifted verbatim from <c>OverlayWindowService.ResolveComposedOverlayCalibrationForTest</c>
/// + <c>ClassifyComposedMissReason</c> (the 8-case decision table already
/// proven by <c>ResolveComposedOverlayCalibrationTests</c>).</summary>
internal sealed class ComposedOverlayCalibrationResolver : IComposedOverlayCalibrationResolver
{
    private readonly IMapCalibrationService _calibration;
    private readonly IMapTextureDimensions _textureDimensions;

    public ComposedOverlayCalibrationResolver(
        IMapCalibrationService calibration,
        IMapTextureDimensions textureDimensions)
    {
        _calibration = calibration;
        _textureDimensions = textureDimensions;
    }

    public ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight)
    {
        if (scene is not { } s)
            return new(null, CalPath.None, "no_scene");

        // Prefer an overlay-frame record when present — direct path.
        var overlayCal = _calibration.GetOverlayCalibration(s);
        if (overlayCal is not null)
            return new(overlayCal, CalPath.DirectOverlay, null);

        var textureCal = _calibration.GetTextureCalibration(s);
        if (textureCal is null)
            return new(null, CalPath.None, "no_usable_calibration");

        var tex = textureCal.Value;

        // F1 — pre-#1081 record with no stamped sha. User recovers by re-running AutoCalibrate.
        if (string.IsNullOrWhiteSpace(tex.PixelSha256))
            return new(null, CalPath.None, "null_sha");

        // F2 — surface not yet laid out (or in a transient sub-pixel layout state).
        // mithril#1096 review fix: guard `< 1` instead of `<= 0` so fractional
        // ActualWidth/Height (rare WPF mid-DPI / mid-animation transient) doesn't
        // pass the guard then truncate to 0 in the (int) casts below, which would
        // build a MapRect with Width=Height=0 and a composed cal that collapses
        // every world coord to the overlay origin (Scale = Width/TextureWidth = 0).
        if (surfaceWidth < 1 || surfaceHeight < 1)
            return new(null, CalPath.None, "unsized_surface");

        var resolved = _textureDimensions.TryGetSizeBySha(tex.PixelSha256);
        if (resolved is not { } d)
            return new(null, CalPath.None, "catalogue_miss");

        var overlayRect = new MapRect(
            OriginX: 0, OriginY: 0,
            Width: (int)surfaceWidth, Height: (int)surfaceHeight,
            TextureWidth: d.Width, TextureHeight: d.Height);

        return new(tex.ProjectThroughOverlay(overlayRect), CalPath.ComposedFromTexture, null);
    }
}
