using System;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class DeviationBlobCalibrationDetectorTests
{
    private const int TexW = 300, TexH = 240;

    // Place each icon type at a well-spread anchor on the texture.
    private static readonly (string Type, int W, int H, int Lum, double Ax, double Ay)[] Placements =
    [
        ("Portal", 24, 32, 60, 70, 70),
        ("TeleportationPlatform", 28, 22, 180, 220, 60),
        ("MeditationPillar", 18, 40, 110, 90, 180),
        ("Npc", 20, 28, 220, 230, 190),
    ];

    private static (GrayImage shot, GrayImage tex) BuildPair()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 4242);
        var shotPixels = (byte[])texPixels.Clone();
        foreach (var p in Placements)
            SyntheticMap.BlitTeardrop(shotPixels, TexW, TexH, p.Ax, p.Ay, p.W, p.H, p.Lum);
        return (new GrayImage(TexW, TexH, shotPixels), new GrayImage(TexW, TexH, texPixels));
    }

    private static DetectionRequest Request(GrayImage shot, GrayImage tex)
    {
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        // The screenshot is already the cropped map; the texture is aligned 1:1.
        var rect = new MapRect(0, 0, TexW, TexH, TexW, TexH);
        var opts = new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5);
        return new DetectionRequest(shot, tex, rect, templates, RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.45, BlobOptions: opts);
    }

    [Fact]
    public void Types_at_least_three_of_four_icons()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();

        var byType = detector.Detect(Request(shot, tex));

        // Count how many placements were detected near their anchor with the
        // correct landmark type.
        int correct = 0;
        foreach (var p in Placements)
        {
            if (!byType.TryGetValue(p.Type, out var dets)) continue;
            bool near = dets.Any(d => Math.Abs(d.Anchor.X - p.Ax) <= 6 && Math.Abs(d.Anchor.Y - p.Ay) <= 6);
            if (near) correct++;
        }
        correct.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Black_screenshot_yields_no_detections()   // negative control
    {
        var tex = new GrayImage(TexW, TexH, SyntheticMap.MakeTexture(TexW, TexH, 99));
        var black = new GrayImage(TexW, TexH, new byte[TexW * TexH]);
        var detector = new DeviationBlobCalibrationDetector();

        var byType = detector.Detect(Request(black, tex));

        byType.Values.Sum(v => v.Count).Should().Be(0);
    }

    // mithril#1121: the diagnostic sink fires for every (blob, template) pair the
    // detector considers — both the gated decision path (above-floor / below-floor)
    // and the skip path (template too large for the padded crop). Used by the
    // calibration bundle to distinguish "blob NCC was 0.78 just below floor"
    // (threshold problem) from "blob NCC was 0.30" (template-vs-rendering problem).
    [Fact]
    public void Diagnostic_sink_fires_per_blob_per_template_when_wired()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var collected = new List<BlobTemplateScore>();
        var request = Request(shot, tex) with { BlobScoreSink = collected.Add };

        detector.Detect(request);

        collected.Should().NotBeEmpty("at least one synthetic icon should produce a blob");

        // Every record must reference one of the 4 placed templates.
        collected.Select(s => s.TemplateName).Distinct()
            .Should().BeSubsetOf(["landmark_portal", "landmark_telepad", "landmark_medipillar", "landmark_npc"]);

        // For each distinct blob index, we expect a record per template the detector
        // considered (skips + scored). This is the per-blob fan-out the bundle dump
        // reflects.
        var byBlob = collected.GroupBy(s => s.BlobIndex).ToList();
        byBlob.Should().NotBeEmpty();
        foreach (var grp in byBlob)
        {
            grp.Select(s => s.TemplateName).Distinct().Count()
                .Should().BeGreaterThanOrEqualTo(1,
                    "each blob considers at least one template (skipped or scored)");
        }

        // Scored records carry a finite score in [-1, 1]; skipped records carry NaN.
        // Whether any skip records exist depends on the test fixture (small synthetic
        // templates don't trigger the "template > crop" skip path), so the skip-side
        // assertion is conditional — when skips DO occur their score must be NaN.
        collected.Where(s => !s.Skipped).Select(s => s.Score)
            .Should().OnlyContain(v => !double.IsNaN(v) && v >= -1.0 && v <= 1.0001);
        foreach (var skipped in collected.Where(s => s.Skipped))
        {
            double.IsNaN(skipped.Score).Should().BeTrue(
                "skip-path records must carry the NaN sentinel");
        }

        // AboveFloor is the gate verdict: score >= TypeFloor on non-skipped records.
        collected.Where(s => !s.Skipped).Should().AllSatisfy(s =>
            s.AboveFloor.Should().Be(s.Score >= s.TypeFloor));

        // Rotate180 is left default-false at the detector layer — the SolveEngine
        // wrapper rewrites it per orientation pass. The detector by itself emits
        // rotate180=false.
        collected.Should().AllSatisfy(s => s.Rotate180.Should().BeFalse(
            "the detector doesn't know about orientation passes; that's the SolveEngine wrapper's job"));
    }

    // mithril#1121: backward-compat — without a sink, output is byte-identical to
    // the pre-#1121 detector. Any change to the detection-decision logic would
    // surface as a behavioural delta here.
    [Fact]
    public void Detector_output_is_identical_with_and_without_sink()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var baseRequest = Request(shot, tex);

        var withoutSink = detector.Detect(baseRequest);
        var collected = new List<BlobTemplateScore>();
        var withSink = detector.Detect(baseRequest with { BlobScoreSink = collected.Add });

        withSink.Keys.Should().BeEquivalentTo(withoutSink.Keys);
        foreach (var key in withoutSink.Keys)
        {
            withSink[key].Should().BeEquivalentTo(withoutSink[key],
                "wiring the sink must not change the gated detection decision");
        }
    }

    // mithril#1121: when the sink is null, the detector skips all per-blob/per-template
    // diagnostic work (zero producer cost). This is a contract guarantee, not just
    // an observation — null-sink path doesn't allocate the BlobTemplateScore record.
    [Fact]
    public void Null_sink_path_does_not_throw()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var req = Request(shot, tex);
        req.BlobScoreSink.Should().BeNull();

        var act = () => detector.Detect(req);
        act.Should().NotThrow();
    }
}
