using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration;

/// <summary>
/// World → overlay-pixel projection. Owned by Mithril.Overlay / Legolas — this
/// is the calibration shape produced by the Legolas wizard and consumed by the
/// overlay renderer.
///
/// Sibling of <see cref="WorldToTextureCalibration"/>; the two structs hold
/// the same math (delegated to <see cref="AreaProjectionCore"/>) but return
/// frame-typed pixel results so an overlay-frame calibration cannot be
/// silently fed to texture-frame code or vice versa.
/// </summary>
public readonly record struct WorldToOverlayCalibration(
    double OriginX,
    double OriginY,
    double Scale,
    double RotationRadians,
    bool MirrorNorth,
    double CalibrationZoom)
{
    public int SchemaVersion { get; init; } = 1;

    public OverlayPixel ToOverlay(WorldCoord world, double currentZoom)
    {
        var (x, y) = AreaProjectionCore.Project(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, world, currentZoom);
        return new OverlayPixel(x, y);
    }

    public OverlayPixel ToOverlay(WorldCoord world) => ToOverlay(world, CalibrationZoom);

    public WorldCoord? FromOverlay(OverlayPixel pixel, double currentZoom) =>
        AreaProjectionCore.Unproject(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth,
            CalibrationZoom, pixel.X, pixel.Y, currentZoom);

    public WorldCoord? FromOverlay(OverlayPixel pixel) => FromOverlay(pixel, CalibrationZoom);
}
