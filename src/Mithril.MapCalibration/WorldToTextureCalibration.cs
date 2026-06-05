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
    bool MirrorNorth,
    double CalibrationZoom)
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// SHA-256 (lowercase hex) of the base texture this calibration was solved
    /// against. Mirrors <see cref="AreaCalibration.PixelSha256"/> — see that
    /// doc. Read by the Legolas overlay (mithril#1081) to look up the
    /// texture's native pixel dimensions via <see cref="IMapTextureDimensions"/>
    /// when composing through <see cref="ProjectThroughOverlay(MapRect)"/>.
    /// </summary>
    public string? PixelSha256 { get; init; }

    public TexturePixel ToTexture(WorldCoord world, double currentZoom)
    {
        var (x, y) = AreaProjectionCore.Project(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, world, currentZoom);
        return new TexturePixel(x, y);
    }

    public TexturePixel ToTexture(WorldCoord world) => ToTexture(world, CalibrationZoom);

    public WorldCoord? FromTexture(TexturePixel pixel, double currentZoom) =>
        AreaProjectionCore.Unproject(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, pixel.X, pixel.Y, currentZoom);

    public WorldCoord? FromTexture(TexturePixel pixel) => FromTexture(pixel, CalibrationZoom);

    /// <summary>
    /// Compose this texture-frame calibration with a base-texture placement on
    /// the overlay window, yielding the equivalent overlay-frame calibration.
    /// This is the ONE named place where texture-frame and overlay-frame
    /// calibrations talk to each other (spec §6.2); rendering an
    /// AutoCalibration-derived calibration onto the overlay goes through here.
    /// </summary>
    public WorldToOverlayCalibration ProjectThroughOverlay(MapRect overlayRect)
    {
        var sx = overlayRect.Width / (double)overlayRect.TextureWidth;
        // The composed scale uses sx — overlay-frame X and Y scale identically in
        // the canonical case. If sx != sy ever becomes a real consumer need, the
        // texture↔overlay placement is anisotropic and this composition is wrong;
        // fail loudly there instead of silently picking one axis.
        var sy = overlayRect.Height / (double)overlayRect.TextureHeight;
        return new WorldToOverlayCalibration(
            OriginX: overlayRect.OriginX + OriginX * sx,
            OriginY: overlayRect.OriginY + OriginY * sy,
            Scale: Scale * sx,
            RotationRadians: RotationRadians,
            MirrorNorth: MirrorNorth,
            CalibrationZoom: CalibrationZoom);
    }
}
