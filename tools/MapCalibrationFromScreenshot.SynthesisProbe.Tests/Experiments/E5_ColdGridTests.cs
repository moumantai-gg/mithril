using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E5_ColdGridTests
{
    [Fact]
    public void Top8_includes_a_near_truth_entry_after_refine()
    {
        var f = new double[128, 128];
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                double dx = x - 64, dy = y - 64;
                f[y, x] = Math.Exp(-(dx * dx + dy * dy) / (2 * 3.0 * 3.0));
            }
        var fields = new Dictionary<string, double[,]> { ["Portal"] = f };
        var refs = new[] { new ReferencePoint("p1", "Portal", 0, 0) };
        var truth = new CandidateTransform(1.0, 0.0, false, 64, 64);

        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var report = E5_ColdGrid.Run(
                fields, refs, truth,
                scaleBracket: (0.5, 2.0),
                scaleSamples: 8,
                cropWidth: 128, cropHeight: 128,
                gridStepPx: 8,
                templateSizePx: 5,
                writer: w);
            report.BestDistanceToTruthPx.Should().BeLessThan(5.0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
