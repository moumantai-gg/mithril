// SynthesisProbeWriterTests.cs
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class SynthesisProbeWriterTests
{
    [Fact]
    public void Csv_writes_header_and_row()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var w = new SynthesisProbeWriter(dir))
            {
                var t = new CandidateTransform(0.5, 0.0, false, 100, 200);
                var jr = new JResult(J: 1.7, RefsAboveHalf: 2, RefsOffCrop: 0, PerRefScores: new[] { 0.9, 0.8 });
                w.AppendCsvRow("E1", "truth", t, jr, dominanceVsRunnerUp: double.NaN);
            }
            var lines = File.ReadAllLines(Path.Combine(dir, "synthesis_probe.csv"));
            lines[0].Should().Be("experiment,label,scale,rot,mirror,tx,ty,J,refs_above_0.5,dominance_vs_runner_up");
            lines[1].Should().StartWith("E1,truth,0.5,0,false,100,200,1.7,2,");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Field_png_written_with_expected_dims()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var field = new double[10, 20];
            field[5, 10] = 0.9;
            w.WriteFieldPng("Portal", field);
            File.Exists(Path.Combine(dir, "field_Portal.png")).Should().BeTrue();
            using var img = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(Path.Combine(dir, "field_Portal.png"));
            img.Width.Should().Be(20);
            img.Height.Should().Be(10);
            // field[5, 10] = 0.9 in NCC space [-1, 1] → 8-bit gray ≈ (0.9 - (-1)) / 2 * 255 ≈ 242
            var peak = img.GetPixel(10, 5);
            peak.R.Should().BeGreaterThan(200, "peak pixel should be bright");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Landscape_png_dimensions_match_input()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-land-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var w = new SynthesisProbeWriter(dir);
            var landscape = new double[65, 65];
            w.WriteLandscapePng("translation", landscape);
            using var img = System.Drawing.Image.FromFile(Path.Combine(dir, "grid_landscape_translation.png"));
            img.Width.Should().Be(65);
            img.Height.Should().Be(65);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
