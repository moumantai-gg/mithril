using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisDiagnosticsTests
{
    [Fact]
    public void SynthesisDiagnostics_record_carries_all_required_fields()
    {
        var d = new SynthesisDiagnostics(
            Mode: "shadow",
            Rotate180: false,
            J: 7.5,
            JMin: 8.0,
            RefsAboveHalf: 6,
            RefsTotal: 11,
            RefsOffCrop: 2,
            NMin: 8,
            Verdict: "reject",
            GateVerdict: "accept",
            Disagree: true,
            DisagreeChange: "accept_to_reject");

        d.Mode.Should().Be("shadow");
        d.Rotate180.Should().Be(false);
        d.J.Should().Be(7.5);
        d.RefsAboveHalf.Should().Be(6);
        d.RefsTotal.Should().Be(11);
        d.RefsOffCrop.Should().Be(2);
        d.Verdict.Should().Be("reject");
        d.GateVerdict.Should().Be("accept");
        d.Disagree.Should().BeTrue();
        d.DisagreeChange.Should().Be("accept_to_reject");
    }

    [Fact]
    public void CalibrationSolveResult_Synthesis_defaults_to_null()
    {
        var result = new CalibrationSolveResult(
            Calibration: null, InlierCount: 0, RejectReason: "no detections");

        result.Synthesis.Should().BeNull();
    }
}
