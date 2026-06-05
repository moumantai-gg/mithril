namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Frame-agnostic world ↔ pixel projection arithmetic shared by
/// <see cref="WorldToTextureCalibration"/> and <see cref="WorldToOverlayCalibration"/>.
/// Pulled out so both wrappers share one source of truth for the rotation +
/// scale + mirror + zoom math; the only difference between the two wrappers
/// is the return type tagging.
///
/// Math is bit-identical to the legacy AreaCalibration.WorldToWindow /
/// WindowToWorld (pre-#1076 refactor); see WorldToTextureCalibrationTests for
/// the equivalence assertions.
/// </summary>
internal static class AreaProjectionCore
{
    public static (double X, double Y) Project(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, double calibrationZoom,
        WorldCoord world, double currentZoom)
    {
        var effScale = scale * ZoomFactor(currentZoom, calibrationZoom);
        var east = world.X;
        var north = mirrorNorth ? -world.Z : world.Z;
        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return (originX + effScale * rotE, originY - effScale * rotN);
    }

    public static WorldCoord? Unproject(
        double originX, double originY, double scale, double rotationRadians,
        bool mirrorNorth, double calibrationZoom,
        double pixelX, double pixelY, double currentZoom)
    {
        var effScale = scale * ZoomFactor(currentZoom, calibrationZoom);
        if (effScale <= 1e-9) return null;

        var rotE = (pixelX - originX) / effScale;
        var rotN = -(pixelY - originY) / effScale;

        var cos = Math.Cos(rotationRadians);
        var sin = Math.Sin(rotationRadians);
        var east = rotE * cos - rotN * sin;
        var north = rotE * sin + rotN * cos;

        var worldX = east;
        var worldZ = mirrorNorth ? -north : north;
        return new WorldCoord(worldX, 0, worldZ);
    }

    private static double ZoomFactor(double currentZoom, double calibrationZoom) =>
        (currentZoom > 1e-6 && calibrationZoom > 1e-6)
            ? currentZoom / calibrationZoom
            : 1.0;
}
