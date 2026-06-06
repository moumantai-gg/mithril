using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class MapRectConversion
{
    /// <summary>
    /// Thin adapter: build an in-memory <see cref="AreaCalibration"/> from the
    /// bundle's <see cref="RecoveredCalibrationJson"/> DTO and delegate to the
    /// shared <see cref="CandidateTransform.FromCalibration(AreaCalibration, MapRect, out double)"/>.
    /// Two consumers (production + probe), one piece of math; this method is
    /// the probe-side adapter.
    /// </summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal,
        MapRect mapRect,
        out double anisotropyPercent)
    {
        var inMemory = new AreaCalibration(
            Scale: cal.Scale,
            RotationRadians: cal.RotationRadians,
            OriginX: cal.OriginX,
            OriginY: cal.OriginY,
            ReferenceCount: cal.ReferenceCount,
            ResidualPixels: cal.ResidualPixels)
        {
            MirrorNorth = cal.MirrorNorth,
        };
        return CandidateTransform.FromCalibration(inMemory, mapRect, out anisotropyPercent);
    }

    /// <summary>Overload without the anisotropy out-param.</summary>
    public static CandidateTransform FromRecoveredCalibration(
        RecoveredCalibrationJson cal, MapRect mapRect)
        => FromRecoveredCalibration(cal, mapRect, out _);
}
