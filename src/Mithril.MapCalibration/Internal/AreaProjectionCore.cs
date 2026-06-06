namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Frame-agnostic world ↔ pixel projection arithmetic shared by
/// <see cref="WorldToTextureCalibration"/> and <see cref="WorldToOverlayCalibration"/>.
/// Pulled out so both wrappers share one source of truth for the rotation +
/// scale + mirror math; the only difference between the two wrappers is the
/// return type tagging.
///
/// Math: similarity transform (translate + scale + rotate + optional
/// MirrorNorth reflection). The transform is frame-pure: the caller's params
/// describe whichever pixel frame the projection outputs into (texture or
/// overlay). The closed-form inverse exists because the transform has no shear
/// or perspective component — see the projection tests in
/// WorldToTextureCalibrationTests for the round-trip contract.
/// </summary>
internal static class AreaProjectionCore
{
    public static (double X, double Y) Project(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, WorldCoord world)
    {
        var east = world.X;
        var north = mirrorNorth ? -world.Z : world.Z;
        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return (originX + scale * rotE, originY - scale * rotN);
    }

    public static WorldCoord? Unproject(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, double pixelX, double pixelY)
    {
        if (scale <= 1e-9) return null;
        var rotE = (pixelX - originX) / scale;
        var rotN = -(pixelY - originY) / scale;
        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var east = rotE * cos - rotN * sin;
        var north = rotE * sin + rotN * cos;
        var worldX = east;
        var worldZ = mirrorNorth ? -north : north;
        return new WorldCoord(worldX, 0, worldZ);
    }
}
