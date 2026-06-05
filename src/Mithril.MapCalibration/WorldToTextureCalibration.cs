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
}
