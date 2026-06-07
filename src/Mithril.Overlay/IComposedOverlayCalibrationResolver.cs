using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Resolves a <see cref="WorldToOverlayCalibration"/> for a scene
/// from the underlying frame-typed records: an overlay-frame record consumes
/// directly; a texture-frame record is rebranded into an overlay-frame cal
/// (same transform fields, different type — the layer-2 <c>MapViewFix</c>
/// applied by <see cref="WorldToOverlayCalibration.ToLiveOverlay"/> handles
/// surface scaling).
///
/// <para>mithril#1107 post-review note: the original mithril#1081 design
/// applied <c>MapRect</c> + surface-dim scaling here. That fought #1095's
/// two-layer projection model — the surface-scaled output of <c>ToOverlay</c>
/// double-scaled through <c>ToLiveOverlay</c>'s fix-application. The
/// rebrand-only shape produces consistent canonical-texture-pixel output for
/// both wizard-solved and composed cals, so the layer-2 fix correctly
/// translates downstream regardless of which path produced the cal.</para>
///
/// <para>Pure: the scene input + the injected calibration store fully
/// determine the result.</para></summary>
public interface IComposedOverlayCalibrationResolver
{
    /// <summary>Resolve the overlay-frame calibration for <paramref name="scene"/>.
    /// See <see cref="ComposedCalResolution"/> for the result shape.</summary>
    ComposedCalResolution Resolve(MapSceneRef? scene);
}
