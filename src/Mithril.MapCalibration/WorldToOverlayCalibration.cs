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
    bool MirrorNorth)
{
    public int SchemaVersion { get; init; } = 1;

    public OverlayPixel ToOverlay(WorldCoord world)
    {
        var (x, y) = AreaProjectionCore.Project(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth, world);
        return new OverlayPixel(x, y);
    }

    public WorldCoord? FromOverlay(OverlayPixel pixel) =>
        AreaProjectionCore.Unproject(
            OriginX, OriginY, Scale, RotationRadians, MirrorNorth, pixel.X, pixel.Y);

    /// <summary>
    /// Layer-2 composition: project a world coordinate through this canonical
    /// overlay-frame calibration and then apply the live <see cref="MapViewFix"/>
    /// to translate from texture-pan-relative coordinates to live overlay pixels.
    /// Use this on the hot path where <paramref name="fix"/> is refreshed each
    /// tick by the <see cref="Detection.IMapViewProbe"/>.
    /// </summary>
    public OverlayPixel ToLiveOverlay(WorldCoord world, MapViewFix fix)
    {
        var canonical = ToOverlay(world);
        var (lx, ly) = fix.TextureToOverlay(canonical.X, canonical.Y);
        return new OverlayPixel(lx, ly);
    }
}
