using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal readonly record struct CandidateTransform(double Scale, double RotRadians, bool Mirror, double Tx, double Ty)
{
    // Mirror of AreaCalibration.WorldToWindow at CalibrationZoom = 1.0 (no
    // zoom factor). Intentional duplication — we don't allocate a full
    // AreaCalibration per candidate, and we hold the canonical method on the
    // persistable record. Keep in sync if AreaCalibration's projection math
    // changes; the parity test in CandidateTransformTests is the trip-wire.
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
