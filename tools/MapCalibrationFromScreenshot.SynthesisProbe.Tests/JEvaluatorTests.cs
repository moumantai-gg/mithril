// JEvaluatorTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class JEvaluatorTests
{
    [Fact]
    public void J_is_high_when_refs_project_onto_field_peaks()
    {
        // Two 64x64 fields with one peak each at known crop pixels.
        var portalField = ZerosWithPeak(h: 64, w: 64, peakX: 20, peakY: 20, value: 0.95);
        var npcField = ZerosWithPeak(h: 64, w: 64, peakX: 50, peakY: 40, value: 0.92);
        var fields = new Dictionary<string, double[,]>
        {
            ["Portal"] = portalField,
            ["Npc"] = npcField,
        };

        // Two refs in world coords that land at the peaks under identity-ish transform.
        var refs = new[]
        {
            new LandmarkReference("Portal", "p1", new WorldCoord(0, 0, 0)),
            new LandmarkReference("Npc", "n1", new WorldCoord(30, 0, -20)),
        };

        // Transform: identity-ish, picking origin so world (0,0) lands at (20,20)
        // and (30,-20) lands at (50,40). Scale = 1 px/unit, no rotation, no mirror.
        var truth = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 20.0, Ty: 20.0);

        var jTruth = JEvaluator.Evaluate(truth, fields, refs);
        jTruth.J.Should().BeGreaterThan(1.8); // sum of two ~0.9 peaks
        jTruth.RefsAboveHalf.Should().Be(2);
        jTruth.RefsOffCrop.Should().Be(0);

        // Shift origin so refs land far off the peaks.
        var wrong = truth with { Tx = -100.0, Ty = -100.0 };
        var jWrong = JEvaluator.Evaluate(wrong, fields, refs);
        jWrong.J.Should().BeLessThan(0.1);
        jWrong.RefsOffCrop.Should().Be(2);
    }

    private static double[,] ZerosWithPeak(int h, int w, int peakX, int peakY, double value)
    {
        var f = new double[h, w];
        f[peakY, peakX] = value;
        return f;
    }
}
