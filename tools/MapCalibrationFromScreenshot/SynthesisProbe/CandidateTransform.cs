using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct CandidateTransform(double Scale, double RotRadians, bool Mirror, double Tx, double Ty)
{
    public PixelPoint Apply(WorldCoord world)
    {
        var east = world.X;
        var north = Mirror ? -world.Z : world.Z;
        var cos = Math.Cos(RotRadians);
        var sin = Math.Sin(RotRadians);
        var rotE = east * cos + north * sin;
        var rotN = -east * sin + north * cos;
        return new PixelPoint(Tx + Scale * rotE, Ty - Scale * rotN);
    }

    public static CandidateTransform FromAreaCalibration(AreaCalibration cal) =>
        new(cal.Scale, cal.RotationRadians, cal.MirrorNorth, cal.OriginX, cal.OriginY);
}
