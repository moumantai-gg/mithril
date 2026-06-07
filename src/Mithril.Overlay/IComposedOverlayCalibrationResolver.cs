using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>Composes a <see cref="WorldToOverlayCalibration"/> for an
/// arbitrary surface size by reading <see cref="IMapCalibrationService"/>'s
/// frame-typed records: an overlay-frame record consumes directly; a
/// texture-frame record composes onto the surface rect via
/// <see cref="WorldToTextureCalibration.ProjectThroughOverlay(MapRect)"/>
/// with dims looked up from <see cref="IMapTextureDimensions"/>.
///
/// <para>Pure: the (scene, w, h) inputs fully determine the result given the
/// injected calibration + dim-catalogue state. The caller chooses the
/// surface (overlay window vs. wizard canvas vs. test).</para>
///
/// <para>mithril#1096: lifted from <c>OverlayWindowService.ResolveComposedOverlayCalibrationForTest</c>
/// so VM-side consumers + the marker projection block can share one resolver
/// and emit one <c>cal.path</c> vocabulary.</para></summary>
public interface IComposedOverlayCalibrationResolver
{
    /// <summary>Resolve the composed overlay calibration for <paramref name="scene"/>
    /// against a target surface of the given dimensions. See
    /// <see cref="ComposedCalResolution"/> for the result shape.</summary>
    ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight);
}
