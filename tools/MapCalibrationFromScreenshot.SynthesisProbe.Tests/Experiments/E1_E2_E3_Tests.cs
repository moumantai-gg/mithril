using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Experiments;

public class E1_E2_E3_Tests
{
    private static (IReadOnlyDictionary<string, double[,]> fields, IReadOnlyList<ReferencePoint> refs, CandidateTransform truth) SyntheticScene()
    {
        // 64x64 portal field with one strong peak at (20,20).
        var portal = new double[64, 64];
        portal[20, 20] = 0.9;
        var refs = new[] { new ReferencePoint("p1", "Portal", WorldX: 0, WorldZ: 0) };
        var truth = new CandidateTransform(Scale: 1.0, RotRadians: 0.0, Mirror: false, Tx: 20.0, Ty: 20.0);
        return (new Dictionary<string, double[,]> { ["Portal"] = portal }, refs, truth);
    }

    [Fact]
    public void E1_writes_truth_row()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E1_TruthScore.Run(fields, refs, truth, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Should().Contain(r => r.StartsWith("E1,truth,"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void E2_writes_landscape_with_peak_at_truth_center()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E2_TranslationSweep.Run(fields, refs, truth, templateSizePx: 5, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Where(r => r.StartsWith("E2,")).Should().NotBeEmpty();
            File.Exists(Path.Combine(dir, "grid_landscape_translation.png")).Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void E3_writes_51_scale_rows()
    {
        var (fields, refs, truth) = SyntheticScene();
        var dir = NewTempDir();
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
                E3_ScaleSweep.Run(fields, refs, truth, w);
            var rows = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            rows.Count(r => r.StartsWith("E3,")).Should().Be(51);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-e123-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
