using Mithril.MapCalibration;

namespace Mithril.Overlay.Internal;

/// <summary>Default <see cref="IComposedOverlayCalibrationResolver"/>.
///
/// <para>mithril#1107 review fix: the post-PR-review impl is a direct rebrand of
/// the texture-frame transform, not a surface-scaled composition. See the
/// behaviour note on <c>WorldToTextureCalibration</c> (where <c>ProjectThroughOverlay</c>
/// used to live) for the layer-1/layer-2 rationale — short version: the wizard-
/// solved overlay-frame cal's <c>ToOverlay</c> returns canonical-texture-pixel
/// coords (per #1095), and <c>ToLiveOverlay</c> applies the layer-2 fix to translate
/// those to live overlay pixels. A composed-from-texture cal must do the same so
/// downstream <c>ToLiveOverlay</c> consumers get a consistent shape — that means
/// the composition is a type-rebrand of the texture cal's fields, NOT a surface
/// rescaling.</para></summary>
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

        // Prefer an overlay-frame record when present — direct path.
        var overlayCal = _calibration.GetOverlayCalibration(s);
        if (overlayCal is not null)
            return new(overlayCal, CalPath.DirectOverlay, null);

        var textureCal = _calibration.GetTextureCalibration(s);
        if (textureCal is null)
            return new(null, CalPath.None, "no_usable_calibration");

        var tex = textureCal.Value;

        // mithril#1107 review fix: rebrand-only. The texture cal's transform is
        // already correct for ToLiveOverlay (it returns canonical-texture-pixel
        // coords; the layer-2 fix handles surface scaling). No catalogue lookup,
        // no MapRect math, no surface dims — those were the pre-review #1081 path
        // that fought #1095's two-layer model.
        var composed = new WorldToOverlayCalibration(
            OriginX: tex.OriginX,
            OriginY: tex.OriginY,
            Scale: tex.Scale,
            RotationRadians: tex.RotationRadians,
            MirrorNorth: tex.MirrorNorth);
        return new(composed, CalPath.ComposedFromTexture, null);
    }
}
