using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E4_RansacSeedScoreTests
{
    [Fact]
    public void Reads_csv_and_scores_each_seed_with_dominance()
    {
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var fields = new Dictionary<string, double[,]> { ["Portal"] = portal };
        var refs = new[] { new LandmarkReference("Portal", "p1", new WorldCoord(0, 0, 0)) };
        var truth = new CandidateTransform(1.0, 0.0, false, 20, 20);

        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "seeds.csv");
            File.WriteAllLines(csv, new[]
            {
                "label,scale,rot,ox,oy,mirror",
                "near_truth,1.0,0.0,20.5,20.5,false",
                "far_off,1.0,0.0,-100,-100,false",
            });

            using (var w = new SynthesisProbeWriter(dir))
                E4_RansacSeedScore.Run(fields, refs, truth, csv, w);

            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv")).Where(r => r.StartsWith("E4,")).ToList();
            rows.Should().HaveCount(2);
            var nearRow = rows.Single(r => r.Contains(",near_truth,"));
            var farRow = rows.Single(r => r.Contains(",far_off,"));
            double JOf(string row) => double.Parse(row.Split(',')[7], System.Globalization.CultureInfo.InvariantCulture);
            JOf(nearRow).Should().BeGreaterThan(JOf(farRow));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
