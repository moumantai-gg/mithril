namespace Mithril.Overlay;

/// <summary>How a usable <see cref="Mithril.MapCalibration.WorldToOverlayCalibration"/>
/// was resolved for the current scene. Surfaced as the <c>cal.path</c> tag on
/// calibration consumer spans (post-#1093). Public so cross-project producers
/// (Legolas VM paths, OverlayWindowService) emit the same vocabulary.
///
/// <para>mithril#1096: lifted from <c>OverlayWindowService</c> internal so the
/// VM-side consumer chain can emit the same enum values that
/// <see cref="IComposedOverlayCalibrationResolver"/> returns.</para></summary>
public enum CalPath
{
    /// <summary>No usable cal this frame (uncalibrated, null-sha cal,
    /// catalogue miss, or surface unsized). The companion
    /// <see cref="ComposedCalResolution.MissReason"/> names which sub-case.</summary>
    None,

    /// <summary>An overlay-frame record exists; consumed directly.</summary>
    DirectOverlay,

    /// <summary>Only a texture-frame record exists; composed onto the
    /// overlay surface via
    /// <see cref="Mithril.MapCalibration.WorldToTextureCalibration.ProjectThroughOverlay(Mithril.MapCalibration.MapRect)"/>
    /// with dims looked up from
    /// <see cref="Mithril.MapCalibration.IMapTextureDimensions"/>.</summary>
    ComposedFromTexture,
}
