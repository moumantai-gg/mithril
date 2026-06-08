using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration;

/// <summary>
/// World → base-texture-pixel projection. Owned by Capture/Detection — this is
/// the calibration shape produced by the AutoCalibration RANSAC solve and read
/// by the drift-check.
///
/// Sibling of <see cref="WorldToOverlayCalibration"/>; the two structs hold
/// the same math (delegated to <see cref="AreaProjectionCore"/>) but return
/// frame-typed pixel results so a texture-frame calibration cannot be
/// silently fed to overlay-frame code or vice versa.
/// </summary>
public readonly record struct WorldToTextureCalibration(
    double OriginX,
    double OriginY,
    double Scale,
    double RotationRadians,
    bool MirrorNorth)
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// SHA-256 (lowercase hex) of the base texture this calibration was solved
    /// against. Mirrors <see cref="AreaCalibration.PixelSha256"/> — see that
    /// doc. Originally consumed by <c>ProjectThroughOverlay</c> for catalogue
    /// dim lookup (#1081); post-#1107 the composer is a direct rebrand of the
    /// texture-frame transform so the sha is informational only (kept for
    /// drift-check / catalogue-versioning consumers).
    /// </summary>
    public string? PixelSha256 { get; init; }

    public TexturePixel ToTexture(WorldCoord world)
    {
        var (x, y) = AreaProjectionCore.Project(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth, world);
        return new TexturePixel(x, y);
    }

    public WorldCoord? FromTexture(TexturePixel pixel) =>
        AreaProjectionCore.Unproject(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth, pixel.X, pixel.Y);

    // mithril#1107 review fix: ProjectThroughOverlay was deleted. Pre-#1107 it scaled
    // the texture cal by surfaceWidth/textureWidth, producing a "surface-scaled"
    // overlay-frame cal whose ToOverlay returned current-surface pixels. That fought
    // #1095's layer model: the wizard-solved overlay-frame cal's ToOverlay returns
    // CANONICAL-TEXTURE-pixel coords, and ToLiveOverlay applies the layer-2 fix
    // (pan + viewScale) to translate texture-pixel → live-overlay-pixel. The two
    // shapes disagreed, causing double-scaling whenever a composed-from-texture cal
    // was fed to ToLiveOverlay. Post-#1107 the composer is a direct rebrand: the
    // texture cal's fields are constructed into a WorldToOverlayCalibration as-is
    // (same Scale, Origin, rotation, mirror), so ToOverlay returns texture-pixel
    // coords and ToLiveOverlay handles surface scaling via the fix. The composer
    // therefore no longer needs surface dims or IMapTextureDimensions.
}
