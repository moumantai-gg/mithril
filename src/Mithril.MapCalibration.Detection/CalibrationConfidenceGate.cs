using System.Globalization;
using Mithril.MapCalibration.DependencyInjection;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Default confidence gate: accept when <c>ResidualPixels &lt;= goodResidualThresholdPx</c>
/// AND <c>inlierCount &gt;= inlierFloor</c>.
///
/// <para>The residual threshold defaults to
/// <see cref="MapCalibrationServiceCollectionExtensions.DefaultGoodResidualThresholdPx"/>
/// (12.0) — the SAME value the manual path uses; a Phase-3 wiring will feed the
/// user-tunable setting here. The inlier floor defaults to 4 (the solver minimum
/// is 2; the gate-study sparse wins were 5–12 inliers, so 4 floors with a margin
/// per §9).</para>
/// </summary>
public sealed class CalibrationConfidenceGate : ICalibrationConfidenceGate
{
    /// <summary>Default minimum inlier count (§9).</summary>
    public const int DefaultInlierFloor = 4;

    private readonly double _goodResidualThresholdPx;
    private readonly int _inlierFloor;

    public CalibrationConfidenceGate(
        double goodResidualThresholdPx = MapCalibrationServiceCollectionExtensions.DefaultGoodResidualThresholdPx,
        int inlierFloor = DefaultInlierFloor)
    {
        _goodResidualThresholdPx = goodResidualThresholdPx;
        _inlierFloor = inlierFloor;
    }

    public bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
    {
        if (inlierCount < _inlierFloor)
        {
            rejectReason = string.Create(CultureInfo.InvariantCulture,
                $"only {inlierCount} inliers (need >= {_inlierFloor})");
            return false;
        }
        if (solve.ResidualPixels > _goodResidualThresholdPx)
        {
            rejectReason = string.Create(CultureInfo.InvariantCulture,
                $"residual {solve.ResidualPixels:0.00} px exceeds threshold {_goodResidualThresholdPx:0.00} px");
            return false;
        }
        rejectReason = null;
        return true;
    }
}
