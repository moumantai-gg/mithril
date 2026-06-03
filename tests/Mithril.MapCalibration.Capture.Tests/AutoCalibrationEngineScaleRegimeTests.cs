using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationEngineScaleRegimeTests
{
    [Theory]
    [InlineData(0.408, 0.408)]      // identical
    [InlineData(0.408, 0.416)]      // +1.96% — inside ±2%
    [InlineData(0.408, 0.400)]      // -1.96%
    [InlineData(1.250, 1.250)]
    public void Same_regime_when_factors_within_2_percent(double existing, double candidate)
    {
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.408, 0.420)]      // +2.94% — outside
    [InlineData(0.408, 0.395)]      // -3.19%
    [InlineData(0.408, 0.800)]      // wildly different
    [InlineData(0.200, 1.500)]      // far apart
    public void Different_regime_when_factors_differ_more_than_2_percent(double existing, double candidate)
    {
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0.408)]
    [InlineData(0.408, null)]
    [InlineData(null, null)]
    public void Null_on_either_side_skips_the_gate(double? existing, double? candidate)
    {
        // "Regime unknown" → return false → gate skipped → accept unconditionally.
        // This is the legacy-record escape hatch: a pre-#1005 stored cal stamped
        // null cannot block a new capture forever.
        AutoCalibrationEngine.IsSameScaleRegime(existing, candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Degenerate_value_on_either_side_skips_the_gate(double bad)
    {
        // Defensive: a non-positive/non-finite stored factor can't anchor a ratio
        // comparison. Treat as regime-unknown rather than throwing or asserting.
        AutoCalibrationEngine.IsSameScaleRegime(bad, 0.408).Should().BeFalse();
        AutoCalibrationEngine.IsSameScaleRegime(0.408, bad).Should().BeFalse();
    }
}
