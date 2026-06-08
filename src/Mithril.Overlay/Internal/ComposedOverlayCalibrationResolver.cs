using Mithril.MapCalibration;

namespace Mithril.Overlay.Internal;

/// <summary>Default <see cref="IComposedOverlayCalibrationResolver"/>.
///
/// <para>mithril#1107 review fix: the post-PR-review impl is a direct rebrand of
/// the texture-frame transform, not a surface-scaled composition. See the
/// behaviour note on <c>WorldToTextureCalibration</c> (where <c>ProjectThroughOverlay</c>
/// used to live) for the layer-1/layer-2 rationale — short version: a
/// texture-frame cal's <c>ToOverlay</c> returns texture-pixel coords, and
/// <c>ToLiveOverlay</c> applies the layer-2 fix (pan + viewScale) to translate
/// those to live overlay pixels. The rebrand is a type tag, not a surface
/// rescale.</para>
///
/// <para><b>Frame preference (mithril#1107 manual-verify fix).</b>
/// Prefers <see cref="IMapCalibrationService.GetTextureCalibration"/> over
/// <see cref="IMapCalibrationService.GetOverlayCalibration"/>. The
/// pre-#1095 wizard solved in canonical-overlay-pixel units, NOT texture-pixel
/// units (e.g. the user's Serbule Overlay-frame cal had scale 0.385 vs the
/// matching Texture-frame cal's 0.822 — a ~2.13× factor matching the
/// texture/canonical-overlay width ratio). Feeding such a cal into
/// <c>ToLiveOverlay</c> applies the layer-2 fix on coords in the wrong unit
/// space, scaling every marker by that factor and rendering them at wildly
/// wrong screen positions.
/// Texture-frame cals (BundledBaseline, AutoCapture, CommunitySync, any future
/// AutoCapture refinement) carry coordinates in the right units by construction.
/// They're preferred. Overlay-frame cals are used only as a last-resort
/// fallback when no Texture-frame cal exists for the area — which is rare,
/// since the BundledBaseline ships Texture-frame entries for every supported
/// area. The fallback path will produce wrong dots for pre-#1095 wizard cals;
/// the right escape is for the user to re-run AutoCapture (which writes
/// Texture-frame). The wizard is being retired in #1113.</para></summary>
internal sealed class ComposedOverlayCalibrationResolver : IComposedOverlayCalibrationResolver
{
    private readonly IMapCalibrationService _calibration;

    public ComposedOverlayCalibrationResolver(IMapCalibrationService calibration)
    {
        _calibration = calibration;
    }

    public ComposedCalResolution Resolve(MapSceneRef? scene)
    {
        if (scene is not { } s)
            return new(null, CalPath.None, "no_scene");

        // Prefer a texture-frame record — its (origin, scale) are in
        // texture-pixel units, which is the input space ToLiveOverlay's
        // layer-2 fix expects. See the type doc for the unit-mismatch
        // rationale behind this ordering.
        var textureCal = _calibration.GetTextureCalibration(s);
        if (textureCal is { } tex)
        {
            // Rebrand-only. The texture cal's transform is already correct for
            // ToLiveOverlay; the layer-2 fix handles surface scaling.
            var composed = new WorldToOverlayCalibration(
                OriginX: tex.OriginX,
                OriginY: tex.OriginY,
                Scale: tex.Scale,
                RotationRadians: tex.RotationRadians,
                MirrorNorth: tex.MirrorNorth);
            return new(composed, CalPath.ComposedFromTexture, null);
        }

        // Fallback: Overlay-frame record. Only correct if the cal was written
        // post-#1095 (texture-pixel units). Pre-#1095 wizard cals will project
        // through ToLiveOverlay with a unit mismatch — but that's strictly
        // better than no projection, and the BundledBaseline ships Texture-frame
        // for every supported area so this fallback rarely fires in practice.
        var overlayCal = _calibration.GetOverlayCalibration(s);
        if (overlayCal is not null)
            return new(overlayCal, CalPath.DirectOverlay, null);

        return new(null, CalPath.None, "no_usable_calibration");
    }
}
