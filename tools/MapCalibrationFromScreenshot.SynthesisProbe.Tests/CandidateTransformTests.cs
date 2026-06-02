using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class CandidateTransformTests
{
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 0.0)]
    [InlineData(false, Math.PI)]
    [InlineData(true, Math.PI)]
    [InlineData(false, Math.PI / 6)]
    public void Apply_matches_AreaCalibration_WorldToWindow(bool mirror, double rot)
    {
        var t = new CandidateTransform(Scale: 0.82, RotRadians: rot, Mirror: mirror, Tx: 100.0, Ty: 200.0);
        var cal = new AreaCalibration(0.82, rot, 100.0, 200.0, ReferenceCount: 1, ResidualPixels: 0.0) { MirrorNorth = mirror };
        var world = new WorldCoord(50, 0, 30);

        var fromCandidate = t.Apply(world);
        var fromCalibration = cal.WorldToWindow(world);

        fromCandidate.X.Should().BeApproximately(fromCalibration.X, 1e-9);
        fromCandidate.Y.Should().BeApproximately(fromCalibration.Y, 1e-9);
    }

    [Fact]
    public void FromAreaCalibration_copies_all_fields()
    {
        var cal = new AreaCalibration(0.5, Math.PI, 12.0, 34.0, 5, 0.7) { MirrorNorth = true };
        var t = CandidateTransform.FromAreaCalibration(cal);

        t.Scale.Should().Be(0.5);
        t.RotRadians.Should().Be(Math.PI);
        t.Mirror.Should().BeTrue();
        t.Tx.Should().Be(12.0);
        t.Ty.Should().Be(34.0);
    }
}
