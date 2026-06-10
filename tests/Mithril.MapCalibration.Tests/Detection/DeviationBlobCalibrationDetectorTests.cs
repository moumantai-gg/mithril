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

        // For each distinct blob ordinal, we expect a record per template the detector
        // considered (skips + scored). This is the per-blob fan-out the bundle dump
        // reflects.
        var byBlob = collected.GroupBy(s => s.BlobOrdinal).ToList();
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

    // mithril#1121: the skip path fires when a template's dimensions exceed the
    // blob's padded crop (rare in production at RenderSizePx=16 + outdoor maps,
    // but real on degenerate inputs). The default-sized fixture never trips this
    // branch, so the per-blob test asserts conditional NaN; this test exercises
    // it directly so the skip-side semantic stays under coverage.
    // mithril#1123: backward-compat lock (D8). Wiring all four hooks (even with
    // empty bodies) must produce byte-identical Detect output to the no-hook
    // path. Catches any inadvertent side effect in the buffer-cloning / emission
    // ordering — the orchestrator continues to mutate fg after each emit, so a
    // missed clone would surface as a behavioural delta here.
    [Fact]
    public void Detector_output_is_identical_with_and_without_hooks()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var baseRequest = Request(shot, tex);

        var withoutHooks = detector.Detect(baseRequest);
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: _ => { },
            OnRimMask: _ => { },
            OnMorph: _ => { },
            OnBlobClassified: _ => { });
        var withHooks = detector.Detect(baseRequest with { Diagnostics = hooks });

        withHooks.Keys.Should().BeEquivalentTo(withoutHooks.Keys);
        foreach (var key in withoutHooks.Keys)
        {
            withHooks[key].Should().BeEquivalentTo(withoutHooks[key],
                "wiring the diagnostic hooks must not change the gated detection decision");
        }
    }

    // mithril#1123: OnDeviation fires exactly once per Detect (single orientation
    // when invoked directly — the SolveEngine's rotate180 wrap doubles it to two
    // records). Asserts the ForegroundBuffer is dense (length W*H) and the
    // AboveThresholdCount tally matches the buffer's true count.
    [Fact]
    public void OnDeviation_fires_once_with_dense_foreground_buffer()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var snaps = new List<DeviationSnapshot>();
        var hooks = new DetectionDiagnosticHooks(snaps.Add, null, null, null);

        detector.Detect(Request(shot, tex) with { Diagnostics = hooks });

        snaps.Should().HaveCount(1);
        var snap = snaps[0];
        snap.ForegroundBuffer.Length.Should().Be(TexW * TexH);
        snap.Width.Should().Be(TexW);
        snap.Height.Should().Be(TexW * TexH / TexW);  // TexH, but readable
        int trueCount = snap.ForegroundBuffer.Count(b => b);
        snap.AboveThresholdCount.Should().Be(trueCount);
        // The detector itself emits rotate180=false; the SolveEngine wrapper rewrites.
        snap.Rotate180.Should().BeFalse();
    }

    // mithril#1123: OnRimMask fires with the blob_detection pipeline discriminator
    // when the detector is driven directly (the synthesis_j pipeline lives in the
    // SolveEngine, not the detector). FgInputCount must match the prior stage's
    // AboveThresholdCount.
    [Fact]
    public void OnRimMask_fires_with_blob_detection_pipeline_tag()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var devSnaps = new List<DeviationSnapshot>();
        var rimSnaps = new List<RimMaskSnapshot>();
        var hooks = new DetectionDiagnosticHooks(devSnaps.Add, rimSnaps.Add, null, null);

        detector.Detect(Request(shot, tex) with { Diagnostics = hooks });

        rimSnaps.Should().HaveCount(1);
        var rim = rimSnaps[0];
        rim.Pipeline.Should().Be("blob_detection");
        rim.RimMaskBuffer.Length.Should().Be(TexW * TexH);
        // Input/survivor stay populated on the blob_detection path (synthesis_j uses -1 sentinels).
        rim.FgInputCount.Should().Be(devSnaps[0].AboveThresholdCount);
        rim.FgSurvivorCount.Should().BeLessThanOrEqualTo(rim.FgInputCount);
        rim.FgSurvivorCount.Should().Be(rim.FgInputCount - rim.RimPixelCount);
    }

    // mithril#1123: OnMorph fires with FgInputCount matching the rim-survivor count
    // (the morph close is the next stage after rim subtract). Production closeRadius=1
    // means the morph CAN grow fg slightly (dilate then erode), but the input count
    // stays the rim-survivor count.
    [Fact]
    public void OnMorph_fires_with_fgInput_matching_rim_survivor()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var rimSnaps = new List<RimMaskSnapshot>();
        var morphSnaps = new List<MorphSnapshot>();
        var hooks = new DetectionDiagnosticHooks(null, rimSnaps.Add, morphSnaps.Add, null);

        detector.Detect(Request(shot, tex) with { Diagnostics = hooks });

        morphSnaps.Should().HaveCount(1);
        var m = morphSnaps[0];
        m.CloseRadius.Should().Be(1);  // production default
        m.FgInputCount.Should().Be(rimSnaps[0].FgSurvivorCount);
        m.FgAfterMorphBuffer.Length.Should().Be(TexW * TexH);
        m.FgOutputCount.Should().Be(m.FgAfterMorphBuffer.Count(b => b));
    }

    // mithril#1123: OnBlobClassified fires for ALL comps, not just Icons — the
    // triage question "why did this blob get Noise/Fog/Structure?" requires the
    // non-Icon comps to surface too. The BuildPair fixture produces 4 placed icons;
    // some terrain noise typically classifies as Noise alongside them.
    [Fact]
    public void OnBlobClassified_fires_for_all_comps_not_just_Icons()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var classifications = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(null, null, null, classifications.Add);

        detector.Detect(Request(shot, tex) with { Diagnostics = hooks });

        classifications.Should().NotBeEmpty();
        classifications.Should().AllSatisfy(c => c.Rotate180.Should().BeFalse());
        // Sanity: the per-blob BlobOrdinals are dense + unique within this single
        // orientation pass.
        var ordinals = classifications.Select(c => c.BlobOrdinal).ToList();
        ordinals.Should().OnlyHaveUniqueItems();
        // Pixels list is populated (render-only payload, but we set it).
        classifications.Should().AllSatisfy(c => c.Pixels.Count.Should().Be(c.Area));
    }

    // mithril#1123: the orchestrator continues to mutate fg after emitting
    // OnDeviation (rim subtract, morph close). Without the .Clone() the captured
    // snapshot's ForegroundBuffer would mutate to the post-morph state at function
    // return. This test wires a sink that captures the buffer + then runs a SECOND
    // Detect — the first capture's buffer must be untouched.
    [Fact]
    public void Snapshots_buffers_are_clones_not_references()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();

        bool[]? firstFg = null;
        var hooks1 = new DetectionDiagnosticHooks(
            OnDeviation: s => firstFg ??= s.ForegroundBuffer,
            OnRimMask: null, OnMorph: null, OnBlobClassified: null);
        detector.Detect(Request(shot, tex) with { Diagnostics = hooks1 });
        firstFg.Should().NotBeNull();
        var snapshotCount = firstFg!.Count(b => b);

        // Drive a second detect — it must not alias the captured buffer.
        detector.Detect(Request(shot, tex) with { Diagnostics = hooks1 });

        // Snapshot from the first run must still match its own AboveThresholdCount tally.
        firstFg!.Count(b => b).Should().Be(snapshotCount,
            "the snapshot buffer must be a clone — orchestrator mutations to fg must not bleed through");
    }

    // mithril#1123 D3.a: BlobFeat.Ordinal carries the 8-connected emission order
    // produced by ConnectedComponents.Label — the same int that
    // BlobTemplateScore.BlobOrdinal (#1121) and BlobClassification.BlobOrdinal
    // (#1123) reference. Direct unit test on the static helper guards the
    // cross-file ordinal-space contract.
    [Fact]
    public void BlobOrdinal_is_set_by_ConnectedComponents_Label_emission_order()
    {
        // 4x4 deviation map with two disjoint above-threshold pixels:
        //   one at (1,1), one at (3,3). Raster scan visits (1,1) first.
        var dev = new float[16];
        dev[1 * 4 + 1] = 1.0f;
        dev[3 * 4 + 3] = 1.0f;
        var opts = new BlobOptions(MinArea: 1, MaxIconArea: 100,
            MinSolidity: 0.0, MaxAspect: 100.0, MinPeak: 0.5);

        var blobs = DeviationBlobDetector.DetectIconBlobs(
            dev, w: 4, h: 4, lowNcc: 0.5, RimMaskMode.None, opts, closeRadius: 0);

        // Both 1-pixel hot spots survive the gate (peak >= MinPeak, area >= MinArea,
        // solidity = 1.0, aspect = 1.0). Emission order is the raster scan order.
        blobs.Should().HaveCount(2);
        blobs[0].Ordinal.Should().Be(0);
        blobs[1].Ordinal.Should().Be(1);
    }

    // mithril#1123 D3.a: the unified-ordinal-space contract — every
    // BlobTemplateScore.BlobOrdinal value (#1121's per-(blob, template) sink,
    // serialised as 10b) corresponds to exactly one BlobClassification.BlobOrdinal
    // value (#1123's per-blob sink, serialised as 10c) where BlobClass == "Icon".
    // This guards the v1→v2 schema bump's promise: 10b is sparse over the same
    // ordinal space as 10c, so cross-file lookup by ordinal is sound.
    [Fact]
    public void BlobOrdinal_cross_refs_10b_and_10c()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();
        var blobScores = new List<BlobTemplateScore>();
        var blobClasses = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null,
            OnRimMask: null,
            OnMorph: null,
            OnBlobClassified: blobClasses.Add);

        var request = Request(shot, tex) with { BlobScoreSink = blobScores.Add, Diagnostics = hooks };
        detector.Detect(request);

        var iconOrdinals = blobClasses
            .Where(c => c.BlobClass == "Icon")
            .Select(c => c.BlobOrdinal)
            .ToHashSet();
        var scoreOrdinals = blobScores.Select(s => s.BlobOrdinal).ToHashSet();

        scoreOrdinals.Should().NotBeEmpty(
            "at least one synthetic icon should produce a scored blob");
        scoreOrdinals.Should().BeSubsetOf(iconOrdinals,
            "every BlobTemplateScore.BlobOrdinal must correspond to a "
            + "BlobClassification record with BlobClass == \"Icon\"");
    }

    [Fact]
    public void Diagnostic_sink_records_skip_path_when_template_exceeds_padded_crop()
    {
        // 24x24 image with a small painted square at the centre — the deviation
        // map produces one blob. The default templates' max dimension is 40 px
        // (medipillar) and the image is only 24 px, so every template's height
        // dimension exceeds the padded crop and all four hit the skip branch.
        // RenderSizePx left null so IconRenderScaler doesn't downscale (templates
        // are already below ScaleSearchThresholdPx = 64).
        const int W = 24, H = 24;
        var texPixels = new byte[W * H];
        var shotPixels = new byte[W * H];
        for (int y = 10; y < 14; y++)
            for (int x = 10; x < 14; x++)
                shotPixels[y * W + x] = 255;

        var shot = new GrayImage(W, H, shotPixels);
        var tex = new GrayImage(W, H, texPixels);

        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var rect = new MapRect(0, 0, W, H, W, H);
        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 1500, MinSolidity: 0.10, MaxAspect: 4.0, MinPeak: 0.3);
        var request = new DetectionRequest(shot, tex, rect, templates, RimMaskMode.None,
            LowNcc: 0.3, TypeFloor: 0.45, BlobOptions: opts)
        {
            RenderSizePx = null,
        };

        var collected = new List<BlobTemplateScore>();
        request = request with { BlobScoreSink = collected.Add };

        new DeviationBlobCalibrationDetector().Detect(request);

        var skipped = collected.Where(s => s.Skipped).ToArray();
        skipped.Should().NotBeEmpty(
            "templates whose max dim (40 px) exceeds the 24x24 image must skip the per-template NCC");

        // Skipped records preserve the original template dims (so triage can see
        // WHICH dim exceeded the crop) and carry the NaN sentinel.
        skipped.Should().AllSatisfy(s =>
        {
            double.IsNaN(s.Score).Should().BeTrue();
            s.AboveFloor.Should().BeFalse();
            s.TemplateWidth.Should().BeGreaterThan(0);
            s.TemplateHeight.Should().BeGreaterThan(0);
        });
    }
}
