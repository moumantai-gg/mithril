using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E5_ColdGrid_BracketedTests
{
    [Fact]
    public void Bracketed_scaleRange_centers_at_expected_scale()
    {
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var fields = new System.Collections.Generic.Dictionary<string, double[,]> { ["Portal"] = portal };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var truth = new CandidateTransform(0.5, 0.0, false, 20, 20);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "synth-probe-e5-bracket-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var report = E5_ColdGrid.Run(
                fields, refs, truth,
                scaleBracket: E5_ColdGrid.BracketAroundExpected(0.5, fractionAbove: 0.2),
                scaleSamples: 8,
                cropWidth: 64, cropHeight: 64,
                gridStepPx: 8,
                templateSizePx: 5,
                writer: w);

            // All explored scales must be within ±20% of 0.5 → [0.4, 0.6].
            foreach (var (t, _, _) in report.Top8AfterRefine)
            {
                t.Scale.Should().BeInRange(0.4 * 0.99, 0.6 * 1.01,
                    "bracketed E5 must not consider scales outside ±20% of expected");
            }
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }
}
