using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationAttemptBundleSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "MithrilBundleTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private FilesystemCalibrationAttemptBundleSink NewSink() =>
        new(_root, logger: null, visualizer: new AttemptBundleVisualizer());

    private static CalibrationAttemptContext PopulatedAccepted()
    {
        var ctx = new CalibrationAttemptContext("AreaEltibule",
            new DateTimeOffset(2026, 6, 1, 12, 30, 12, 696, TimeSpan.Zero));
        var color = new byte[4 * 4 * 4]; // 4x4 BGRA32
        ctx.RawCapture = new CapturedFrame(4, 4, color);
        ctx.GrayCapture = new GrayImage(4, 4, new byte[16]);
        ctx.MapRect = new MapRect(0, 0, 4, 4, 8, 8);
        ctx.AlignedCrop = new GrayImage(4, 4, new byte[16]);
        ctx.AlignedTexture = new GrayImage(4, 4, new byte[16]);
        ctx.BaseTextureResampled = ctx.AlignedTexture;
        ctx.References = Array.Empty<LandmarkReference>();
        var cal = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: 0, ResidualPixels: 0.5)
        { Source = CalibrationSource.AutoCapture };
        ctx.Result = new CalibrationSolveResult(
            Calibration: cal, InlierCount: 0, RejectReason: null,
            Inliers: Array.Empty<TypeAwareRansacSolver.AssignedReference>())
        { Detections = Array.Empty<TypedDetection>() };
        ctx.Outcome = OutcomeVocabulary.Accepted;
        return ctx;
    }

    private static CalibrationAttemptContext PopulatedAcceptedWithShadowSynthesis()
    {
        var ctx = PopulatedAccepted();
        ctx.Result = ctx.Result! with
        {
            Synthesis = new SynthesisDiagnostics(
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
                DisagreeChange: "accept_to_reject"),
        };
        return ctx;
    }

    [Fact]
    public void Writes_per_attempt_subdir_with_expected_name_on_accepted()
    {
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dirs = Directory.GetDirectories(_root);
        dirs.Should().HaveCount(1);
        Path.GetFileName(dirs[0]).Should().StartWith("AreaEltibule-20260601-123012-696-accepted");
    }

    [Fact]
    public void Writes_all_11_files_on_accepted_attempt()
    {
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        files.Should().Contain(new[]
        {
            "01-attempt.json",
            "02-screenshot-raw.png",
            "03-screenshot-gray.png",
            "04-maprect.json",
            "05-base-texture-resampled.png",
            "06-aligned-screenshot.png",
            "07-deviation.png",
            "08-detections.png",
            "09-projection-overlay.png",
            "10-detections.json",
            "11-recovered-cal.json",
        });

        var attemptJsonPath = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(attemptJsonPath);
        var attempt = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt.Should().NotBeNull();
        attempt!.Files.RawScreenshot.Should().Be("02-screenshot-raw.png");
        attempt.Files.GrayScreenshot.Should().Be("03-screenshot-gray.png");
        attempt.Files.MapRect.Should().Be("04-maprect.json");
        attempt.Files.BaseTextureResampled.Should().Be("05-base-texture-resampled.png");
        attempt.Files.AlignedScreenshot.Should().Be("06-aligned-screenshot.png");
        attempt.Files.Deviation.Should().Be("07-deviation.png");
        attempt.Files.DetectionsImage.Should().Be("08-detections.png");
        attempt.Files.ProjectionOverlay.Should().Be("09-projection-overlay.png");
        attempt.Files.Detections.Should().Be("10-detections.json");
        attempt.Files.RecoveredCalibration.Should().Be("11-recovered-cal.json");
    }

    [Fact]
    public void Writes_only_header_on_capture_failed()
    {
        var ctx = new CalibrationAttemptContext("AreaSerbule",
            new DateTimeOffset(2026, 6, 1, 13, 0, 0, 0, TimeSpan.Zero));
        ctx.Outcome = OutcomeVocabulary.RejectedCaptureFailed;
        ctx.Result = new CalibrationSolveResult(null, 0, "capture failed");

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().BeEquivalentTo(new[] { "01-attempt.json" });
    }

    [Fact]
    public void Writes_partial_set_on_solve_rejection()
    {
        var ctx = PopulatedAccepted();
        ctx.Result = new CalibrationSolveResult(
            Calibration: null, InlierCount: 3, RejectReason: "insufficient inliers (3 < 4 required)")
        { Detections = Array.Empty<TypedDetection>() };
        ctx.Outcome = OutcomeVocabulary.RejectedSolveInsufficientInliers;

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().NotContain("09-projection-overlay.png");
        files.Should().NotContain("11-recovered-cal.json");
        files.Should().Contain("10-detections.json"); // detections list was present

        var attemptJsonPath = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(attemptJsonPath);
        var attempt = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.Files.ProjectionOverlay.Should().BeNull();      // calibration was null
        attempt.Files.RecoveredCalibration.Should().BeNull();    // calibration was null
        attempt.Files.Detections.Should().Be("10-detections.json");  // detections list was present
    }

    /// <summary>
    /// mithril#1121: per-blob × per-template NCC observation dump. When the
    /// AutoCalibrationEngine wires the diagnostic sink, the resulting context
    /// carries a populated list and the bundle writer emits
    /// <c>10b-blob-template-scores.json</c> alongside the existing files.
    /// </summary>
    [Fact]
    public void Writes_blobTemplateScores_when_context_populated()
    {
        var ctx = PopulatedAccepted();
        ctx.BlobTemplateScores = new[]
        {
            new BlobTemplateScore(
                BlobOrdinal: 0, BlobMinX: 239, BlobMinY: 106, BlobWidth: 16, BlobHeight: 17,
                BlobArea: 230,
                TemplateName: "landmark_npc", TemplateLandmarkType: "Npc",
                TemplateWidth: 15, TemplateHeight: 16,
                Score: 0.78, TypeFloor: 0.80, AboveFloor: false, Skipped: false,
                Rotate180: false),
            new BlobTemplateScore(
                BlobOrdinal: 0, BlobMinX: 239, BlobMinY: 106, BlobWidth: 16, BlobHeight: 17,
                BlobArea: 230,
                TemplateName: "landmark_portal", TemplateLandmarkType: "Portal",
                TemplateWidth: 16, TemplateHeight: 16,
                Score: 0.65, TypeFloor: 0.80, AboveFloor: false, Skipped: false,
                Rotate180: false),
            new BlobTemplateScore(
                BlobOrdinal: 1, BlobMinX: 249, BlobMinY: 149, BlobWidth: 16, BlobHeight: 17,
                BlobArea: 220,
                TemplateName: "landmark_telepad", TemplateLandmarkType: "TeleportationPlatform",
                TemplateWidth: 0, TemplateHeight: 0,
                Score: double.NaN, TypeFloor: 0.80, AboveFloor: false, Skipped: true,
                Rotate180: true),
        };

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        files.Should().Contain("10b-blob-template-scores.json");

        var path = Path.Combine(dir, "10b-blob-template-scores.json");
        using var fs = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.BlobTemplateScoresJson);
        dto.Should().NotBeNull();
        // mithril#1123 D3.a: BlobOrdinal rename = schema v1→v2 bump on 10b.
        dto!.SchemaVersion.Should().Be(2);
        dto.Scores.Should().HaveCount(3);

        // First record round-trips faithfully.
        dto.Scores[0].BlobOrdinal.Should().Be(0);
        dto.Scores[0].TemplateName.Should().Be("landmark_npc");
        dto.Scores[0].TemplateLandmarkType.Should().Be("Npc");
        dto.Scores[0].Score.Should().BeApproximately(0.78, 1e-9);
        dto.Scores[0].TypeFloor.Should().BeApproximately(0.80, 1e-9);
        dto.Scores[0].AboveFloor.Should().BeFalse();
        dto.Scores[0].Skipped.Should().BeFalse();

        // Skip path round-trips with NaN score.
        dto.Scores[2].Skipped.Should().BeTrue();
        dto.Scores[2].Score.Should().Match(d => double.IsNaN(d));
        dto.Scores[2].Rotate180.Should().BeTrue();

        // 01-attempt.json carries the new file slot.
        var attemptPath = Path.Combine(dir, "01-attempt.json");
        using var attemptFs = File.OpenRead(attemptPath);
        var attempt = JsonSerializer.Deserialize(attemptFs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.Files.BlobTemplateScores.Should().Be("10b-blob-template-scores.json");
    }

    /// <summary>
    /// mithril#1121: when the context's blob-score list is null OR empty, the
    /// sink omits the dump file. Distinguishes "diagnostic wiring not active"
    /// from "diagnostic ran but found nothing" — both produce no file (the
    /// detector emits zero records only on the empty-deviation-map path, which
    /// is observable elsewhere via 07-deviation.png).
    /// </summary>
    [Fact]
    public void Omits_blobTemplateScores_when_context_null_or_empty()
    {
        var nullCtx = PopulatedAccepted();
        nullCtx.BlobTemplateScores = null;
        NewSink().Write(nullCtx);
        var nullDir = Directory.GetDirectories(_root).Single();
        Directory.GetFiles(nullDir).Select(Path.GetFileName)
            .Should().NotContain("10b-blob-template-scores.json");

        // Re-run with empty list.
        Directory.Delete(nullDir, recursive: true);
        var emptyCtx = PopulatedAccepted();
        emptyCtx.BlobTemplateScores = Array.Empty<BlobTemplateScore>();
        NewSink().Write(emptyCtx);
        var emptyDir = Directory.GetDirectories(_root).Single();
        Directory.GetFiles(emptyDir).Select(Path.GetFileName)
            .Should().NotContain("10b-blob-template-scores.json");

        // The 01-attempt.json's Files.BlobTemplateScores field is null in both cases.
        var attemptPath = Path.Combine(emptyDir, "01-attempt.json");
        using var attemptFs = File.OpenRead(attemptPath);
        var attempt = JsonSerializer.Deserialize(attemptFs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.Files.BlobTemplateScores.Should().BeNull();
    }

    /// <summary>
    /// mithril#1123: detector-pipeline observability dump (10c-blob-pipeline.json)
    /// + per-stage PNG masks. When the context's four per-stage lists are
    /// populated, the sink writes 10c + 10 PNGs and the 01-attempt.json's
    /// Files block carries 11 new slots.
    /// </summary>
    [Fact]
    public void Writes_blob_pipeline_json_and_pngs_when_context_populated()
    {
        var ctx = PopulatedAccepted();
        const int W = 4, H = 4;

        // One DeviationSnapshot per orientation. ForegroundBuffer is dense (W*H).
        ctx.DeviationSnapshots = new[]
        {
            new DeviationSnapshot(Rotate180: false, Width: W, Height: H, Win: 11,
                Threshold: 0.5, MeanNcc: 0.82,
                Min: 0.0, Max: 0.9, Mean: 0.2,
                P50: 0.1, P95: 0.7, P99: 0.85,
                AboveThresholdCount: 4,
                ForegroundBuffer: new[] { true, false, true, false, false, true, false, true,
                                          true, false, true, false, false, true, false, true }),
            new DeviationSnapshot(Rotate180: true, Width: W, Height: H, Win: 11,
                Threshold: 0.5, MeanNcc: 0.81,
                Min: 0.0, Max: 0.9, Mean: 0.2,
                P50: 0.1, P95: 0.7, P99: 0.85,
                AboveThresholdCount: 3,
                ForegroundBuffer: new bool[W * H]),
        };

        // Four RimMaskSnapshots: 2 orientations × 2 pipelines.
        ctx.RimMaskSnapshots = new[]
        {
            new RimMaskSnapshot(Pipeline: RimMaskPipeline.BlobDetection, Rotate180: false,
                Width: W, Height: H, Threshold: 0.5,
                RimPixelCount: 1, FgInputCount: 4, FgSurvivorCount: 3,
                RimMaskBuffer: new bool[W * H]),
            new RimMaskSnapshot(Pipeline: RimMaskPipeline.BlobDetection, Rotate180: true,
                Width: W, Height: H, Threshold: 0.5,
                RimPixelCount: 1, FgInputCount: 3, FgSurvivorCount: 2,
                RimMaskBuffer: new bool[W * H]),
            new RimMaskSnapshot(Pipeline: RimMaskPipeline.SynthesisJ, Rotate180: false,
                Width: W, Height: H, Threshold: 0.5,
                RimPixelCount: 0, FgInputCount: null, FgSurvivorCount: null,
                RimMaskBuffer: new bool[W * H]),
            new RimMaskSnapshot(Pipeline: RimMaskPipeline.SynthesisJ, Rotate180: true,
                Width: W, Height: H, Threshold: 0.5,
                RimPixelCount: 0, FgInputCount: null, FgSurvivorCount: null,
                RimMaskBuffer: new bool[W * H]),
        };

        ctx.MorphSnapshots = new[]
        {
            new MorphSnapshot(Rotate180: false, Width: W, Height: H,
                CloseRadius: 1, FgInputCount: 3, FgOutputCount: 4,
                FgAfterMorphBuffer: new bool[W * H]),
            new MorphSnapshot(Rotate180: true, Width: W, Height: H,
                CloseRadius: 1, FgInputCount: 2, FgOutputCount: 3,
                FgAfterMorphBuffer: new bool[W * H]),
        };

        // Two blobs per orientation — one Icon, one Noise. Pixels list covers
        // a couple of pixels per blob (the render PNG uses these).
        ctx.BlobClassifications = new[]
        {
            new BlobClassification(Rotate180: false, BlobOrdinal: 0,
                MinX: 0, MinY: 0, W: 2, H: 2, Area: 2,
                Cx: 0.5, Cy: 0.5,
                MeanDev: 0.6, PeakDev: 0.9,
                Solidity: 0.5, Aspect: 1.0,
                BlobClass: BlobClass.Icon,
                Pixels: new[] { 0, 1 }),
            new BlobClassification(Rotate180: false, BlobOrdinal: 1,
                MinX: 2, MinY: 2, W: 1, H: 1, Area: 1,
                Cx: 2, Cy: 2,
                MeanDev: 0.5, PeakDev: 0.5,
                Solidity: 1.0, Aspect: 1.0,
                BlobClass: BlobClass.Noise,
                Pixels: new[] { 10 }),
            new BlobClassification(Rotate180: true, BlobOrdinal: 0,
                MinX: 0, MinY: 0, W: 1, H: 1, Area: 1,
                Cx: 0, Cy: 0,
                MeanDev: 0.6, PeakDev: 0.6,
                Solidity: 1.0, Aspect: 1.0,
                BlobClass: BlobClass.Icon,
                Pixels: new[] { 0 }),
        };

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();

        // 10c JSON + 10 PNGs land on disk.
        files.Should().Contain("10c-blob-pipeline.json");
        files.Should().Contain("07b-foreground.png");
        files.Should().Contain("07b-r180-foreground.png");
        files.Should().Contain("07c-rim-mask.png");
        files.Should().Contain("07c-r180-rim-mask.png");
        files.Should().Contain("07c-synth-rim-mask.png");
        files.Should().Contain("07c-r180-synth-rim-mask.png");
        files.Should().Contain("07d-morphed.png");
        files.Should().Contain("07d-r180-morphed.png");
        files.Should().Contain("07e-blob-classification.png");
        files.Should().Contain("07e-r180-blob-classification.png");

        // 10c content round-trips.
        var path = Path.Combine(dir, "10c-blob-pipeline.json");
        using var fs = File.OpenRead(path);
        var dto = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.BlobPipelineJson);
        dto.Should().NotBeNull();
        dto!.SchemaVersion.Should().Be(1);
        dto.Deviation.Should().HaveCount(2);
        dto.RimMasks.Should().HaveCount(4);
        dto.RimMasks.Select(r => r.Pipeline).Distinct().Should()
            .BeEquivalentTo(new[] { "blob_detection", "synthesis_j" });
        dto.Morph.Should().HaveCount(2);
        dto.Blobs.Should().HaveCount(3);
        dto.Blobs.Select(b => b.BlobClass).Should()
            .Contain(new[] { "Icon", "Noise" });

        // 01-attempt.json carries the 11 new file slots.
        var attemptPath = Path.Combine(dir, "01-attempt.json");
        using var attemptFs = File.OpenRead(attemptPath);
        var attempt = JsonSerializer.Deserialize(attemptFs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.Files.BlobPipeline.Should().Be("10c-blob-pipeline.json");
        attempt.Files.Foreground.Should().Be("07b-foreground.png");
        attempt.Files.ForegroundR180.Should().Be("07b-r180-foreground.png");
        attempt.Files.RimMask.Should().Be("07c-rim-mask.png");
        attempt.Files.RimMaskR180.Should().Be("07c-r180-rim-mask.png");
        attempt.Files.SynthRimMask.Should().Be("07c-synth-rim-mask.png");
        attempt.Files.SynthRimMaskR180.Should().Be("07c-r180-synth-rim-mask.png");
        attempt.Files.Morphed.Should().Be("07d-morphed.png");
        attempt.Files.MorphedR180.Should().Be("07d-r180-morphed.png");
        attempt.Files.BlobClassification.Should().Be("07e-blob-classification.png");
        attempt.Files.BlobClassificationR180.Should().Be("07e-r180-blob-classification.png");
    }

    /// <summary>
    /// mithril#1123: when all four pipeline lists are null OR empty, the sink
    /// omits 10c + every PNG slot. Mirrors the #1121 "null-or-empty → omit"
    /// convention.
    /// </summary>
    [Fact]
    public void Omits_blob_pipeline_when_context_null_or_empty()
    {
        var nullCtx = PopulatedAccepted();
        nullCtx.DeviationSnapshots = null;
        nullCtx.RimMaskSnapshots = null;
        nullCtx.MorphSnapshots = null;
        nullCtx.BlobClassifications = null;
        NewSink().Write(nullCtx);
        var nullDir = Directory.GetDirectories(_root).Single();
        Directory.GetFiles(nullDir).Select(Path.GetFileName)
            .Should().NotContain("10c-blob-pipeline.json");

        Directory.Delete(nullDir, recursive: true);
        var emptyCtx = PopulatedAccepted();
        emptyCtx.DeviationSnapshots = Array.Empty<DeviationSnapshot>();
        emptyCtx.RimMaskSnapshots = Array.Empty<RimMaskSnapshot>();
        emptyCtx.MorphSnapshots = Array.Empty<MorphSnapshot>();
        emptyCtx.BlobClassifications = Array.Empty<BlobClassification>();
        NewSink().Write(emptyCtx);
        var emptyDir = Directory.GetDirectories(_root).Single();
        Directory.GetFiles(emptyDir).Select(Path.GetFileName)
            .Should().NotContain("10c-blob-pipeline.json");

        // 01-attempt.json's 11 new slots are null in both cases.
        var attemptPath = Path.Combine(emptyDir, "01-attempt.json");
        using var attemptFs = File.OpenRead(attemptPath);
        var attempt = JsonSerializer.Deserialize(attemptFs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.Files.BlobPipeline.Should().BeNull();
        attempt.Files.Foreground.Should().BeNull();
        attempt.Files.RimMask.Should().BeNull();
        attempt.Files.SynthRimMask.Should().BeNull();
        attempt.Files.Morphed.Should().BeNull();
        attempt.Files.BlobClassification.Should().BeNull();
    }

    /// <summary>
    /// Observability: on a <c>rejected-map-not-located</c> outcome the bundle's
    /// <c>01-attempt.json</c> must carry the coarse locator's best origin+size (under
    /// <c>LocatorBest</c>), so triaging close-miss vs catastrophic-mismatch via the
    /// captured rect is possible. (Score/factor metadata used to ride on MapRect
    /// itself; Task 13 stripped those fields, and Task 15 re-surfaces equivalents
    /// via LocatorMetrics.)
    /// </summary>
    [Fact]
    public void Writes_locatorBest_on_map_not_located_reject()
    {
        var ctx = new CalibrationAttemptContext("AreaKurMountains",
            new DateTimeOffset(2026, 6, 2, 17, 15, 30, 866, TimeSpan.Zero));
        ctx.RawCapture = new CapturedFrame(4, 4, new byte[4 * 4 * 4]);
        ctx.GrayCapture = new GrayImage(4, 4, new byte[16]);
        // The locator found a raw fit below the gate — preserved on the context
        // alongside its FM metrics so the bundle's LocatorBest carries both.
        ctx.LocatorRawFit = new MapRect(192, 100, 909, 909, 2048, 2048);
        ctx.LocatorMetrics = new LocateMetrics(
            InlierCount: 42,
            CandidateCount: 731,
            InlierRatio: 0.057,
            Scale: 1.0007,
            RotationDegrees: 0.12,
            Mirror: false,
            Tx: 191.4,
            Ty: 99.8,
            ResidualPixels: 2.41);
        ctx.Outcome = OutcomeVocabulary.RejectedMapNotLocated;

        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var attemptJsonPath = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(attemptJsonPath);
        var attempt = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt.Should().NotBeNull();
        attempt!.LocatorBest.Should().NotBeNull();
        attempt.LocatorBest!.OriginX.Should().Be(192);
        attempt.LocatorBest.OriginY.Should().Be(100);
        attempt.LocatorBest.Width.Should().Be(909);
        attempt.LocatorBest.Height.Should().Be(909);
        // FM metrics from ctx.LocatorMetrics flow through to LocatorBest.
        attempt.LocatorBest.InlierCount.Should().Be(42);
        attempt.LocatorBest.CandidateCount.Should().Be(731);
        attempt.LocatorBest.InlierRatio.Should().BeApproximately(0.057, 1e-9);
        attempt.LocatorBest.Scale.Should().BeApproximately(1.0007, 1e-9);
        attempt.LocatorBest.RotationDegrees.Should().BeApproximately(0.12, 1e-9);
        attempt.LocatorBest.Tx.Should().BeApproximately(191.4, 1e-9);
        attempt.LocatorBest.Ty.Should().BeApproximately(99.8, 1e-9);
        attempt.LocatorBest.ResidualPixels.Should().BeApproximately(2.41, 1e-9);
        // Map-not-located outcome → GateAccepted=false on the locator block.
        attempt.LocatorBest.GateAccepted.Should().BeFalse();
    }

    /// <summary>
    /// mithril#1061: when the Sobel-padded-pyramid fallback produced the fit, the
    /// bundle's LocatorBest carries Algorithm = "sobel-padded-pyramid" + FallbackNcc +
    /// PadPx. PadPx reads MapCalibrationLocateOptions.FallbackPadPx when the sink
    /// was injected with the options, so a user who customises the pad sees that
    /// value in the bundle (not the option default).
    /// </summary>
    [Fact]
    public void Writes_sobel_padded_pyramid_fields_on_fallback_attempt_reading_options_padPx()
    {
        var customisedOptions = new MapCalibrationLocateOptions { FallbackPadPx = 150 };
        var sink = new FilesystemCalibrationAttemptBundleSink(
            _root, logger: null, visualizer: new AttemptBundleVisualizer(),
            options: customisedOptions);

        var ctx = new CalibrationAttemptContext("Map_HogansKeepBasement",
            new DateTimeOffset(2026, 6, 3, 22, 31, 19, 130, TimeSpan.Zero));
        ctx.RawCapture = new CapturedFrame(4, 4, new byte[4 * 4 * 4]);
        ctx.GrayCapture = new GrayImage(4, 4, new byte[16]);
        ctx.LocatorRawFit = new MapRect(127, 35, 591, 740, 819, 1024);
        ctx.LocatorMetrics = new LocateMetrics(
            InlierCount: 0,
            CandidateCount: 0,
            InlierRatio: 0,
            Scale: 0.7227,
            RotationDegrees: 0,
            Mirror: false,
            Tx: 127.5,
            Ty: 35.8,
            ResidualPixels: 0,
            Provenance: LocateProvenance.SobelPaddedPyramid,
            Confidence: 0.680);
        ctx.MapRect = ctx.LocatorRawFit;
        ctx.Outcome = OutcomeVocabulary.Accepted;

        sink.Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        using var fs = File.OpenRead(Path.Combine(dir, "01-attempt.json"));
        var attempt = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt.Should().NotBeNull();
        attempt!.LocatorBest.Should().NotBeNull();
        attempt.LocatorBest!.SchemaVersion.Should().Be(2);
        attempt.LocatorBest.Algorithm.Should().Be("sobel-padded-pyramid");
        attempt.LocatorBest.FallbackNcc.Should().BeApproximately(0.680, 1e-9);
        attempt.LocatorBest.PadPx.Should().Be(150,
            "the sink reads FallbackPadPx live from the injected options, not the option default");
    }

    /// <summary>
    /// mithril#1061: the sink also constructs without injected options (test graphs
    /// pre-PR). In that path PadPx falls back to the static default (100) so the
    /// behaviour matches what shipped before the wiring landed.
    /// </summary>
    [Fact]
    public void Writes_sobel_padded_pyramid_fields_with_default_padPx_when_options_not_injected()
    {
        var sink = NewSink();  // no options injected

        var ctx = new CalibrationAttemptContext("Map_HogansKeepBasement",
            new DateTimeOffset(2026, 6, 3, 22, 31, 19, 130, TimeSpan.Zero));
        ctx.RawCapture = new CapturedFrame(4, 4, new byte[4 * 4 * 4]);
        ctx.GrayCapture = new GrayImage(4, 4, new byte[16]);
        ctx.LocatorRawFit = new MapRect(127, 35, 591, 740, 819, 1024);
        ctx.LocatorMetrics = new LocateMetrics(
            InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
            Scale: 0.7227, RotationDegrees: 0, Mirror: false,
            Tx: 127.5, Ty: 35.8, ResidualPixels: 0,
            Provenance: LocateProvenance.SobelPaddedPyramid,
            Confidence: 0.680);
        ctx.MapRect = ctx.LocatorRawFit;
        ctx.Outcome = OutcomeVocabulary.Accepted;

        sink.Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        using var fs = File.OpenRead(Path.Combine(dir, "01-attempt.json"));
        var attempt = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);
        attempt!.LocatorBest!.PadPx.Should().Be(100);
    }

    [Theory]
    [InlineData("rejected-no-area")]
    [InlineData("rejected-pg-not-foreground")]
    [InlineData("rejected-no-bbox")]
    public void Skips_write_on_pre_capture_outcomes(string outcome)
    {
        var ctx = new CalibrationAttemptContext("AreaEltibule",
            new DateTimeOffset(2026, 6, 1, 13, 0, 0, 0, TimeSpan.Zero));
        ctx.Outcome = outcome;
        NewSink().Write(ctx);

        // Root dir should not exist at all (sink skips before creating it).
        var subdirs = Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
            : Array.Empty<string>();
        subdirs.Should().BeEmpty();
    }

    [Fact]
    public void Swallows_visualizer_exceptions_and_writes_what_it_can()
    {
        var ctx = PopulatedAccepted();
        var sink = new FilesystemCalibrationAttemptBundleSink(
            _root, logger: null, visualizer: new ThrowingVisualizer());

        Action act = () => sink.Write(ctx);
        act.Should().NotThrow();

        var dir = Directory.GetDirectories(_root).SingleOrDefault();
        dir.Should().NotBeNull();
        Directory.GetFiles(dir!).Select(Path.GetFileName).Should().Contain("01-attempt.json");
    }

    [Fact]
    public void NullSink_no_ops_on_populated_context()
    {
        var ctx = PopulatedAccepted();
        Action act = () => NullCalibrationAttemptBundleSink.Instance.Write(ctx);
        act.Should().NotThrow();

        // Nothing was written anywhere.
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public void V3_bundle_has_synthesis_section_when_synthesis_ran()
    {
        var ctx = PopulatedAcceptedWithShadowSynthesis();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var path = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(path);
        var parsed = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(3);
        parsed.Synthesis.Should().NotBeNull();
        parsed.Synthesis!.Mode.Should().Be("shadow");
        parsed.Synthesis.J.Should().Be(7.5);
        parsed.Synthesis.RefsAboveHalf.Should().Be(6);
        parsed.Synthesis.Verdict.Should().Be("reject");
        parsed.Synthesis.GateVerdict.Should().Be("accept");
        parsed.Synthesis.Disagree.Should().BeTrue();
        parsed.Synthesis.DisagreeChange.Should().Be("accept_to_reject");
    }

    [Fact]
    public void V3_bundle_omits_synthesis_when_mode_was_off()
    {
        // PopulatedAccepted leaves Result.Synthesis null — same as mode == Off.
        var ctx = PopulatedAccepted();
        NewSink().Write(ctx);

        var dir = Directory.GetDirectories(_root).Single();
        var path = Path.Combine(dir, "01-attempt.json");
        using var fs = File.OpenRead(path);
        var parsed = JsonSerializer.Deserialize(fs, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed!.SchemaVersion.Should().Be(3);
        parsed.Synthesis.Should().BeNull();
    }

    [Fact]
    public void V3_code_reads_pre_v3_bundle_with_null_synthesis()
    {
        // Hand-write a v2 bundle JSON (no `synthesis` field at all). This is exactly
        // what a pre-#1117 engine version wrote to disk; users may have these
        // bundles from before they updated.
        const string preV3Json = """
        {
          "schemaVersion": 2,
          "area": "Map_Test",
          "attemptStartedUtc": "2026-06-08T19:37:13.0000000Z",
          "attemptFinalizedUtc": "2026-06-08T19:37:14.0000000Z",
          "outcome": "accepted",
          "rejectReason": null,
          "engineVersion": "3.0.0.103+pre1117",
          "files": {
            "rawScreenshot": null,
            "grayScreenshot": null,
            "mapRect": null,
            "baseTextureResampled": null,
            "alignedScreenshot": null,
            "deviation": null,
            "detectionsImage": null,
            "projectionOverlay": null,
            "detections": null,
            "recoveredCalibration": null
          },
          "locatorBest": null
        }
        """;

        var parsed = JsonSerializer.Deserialize(preV3Json, CalibrationBundleJsonContext.Default.AttemptJson);

        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(2);   // we preserve the on-disk value
        parsed.Area.Should().Be("Map_Test");
        parsed.Synthesis.Should().BeNull();     // missing field → default → null
    }

    /// <summary>
    /// Test-only stub that implements the interface directly and throws on all methods,
    /// exercising the sink's per-method swallow contract.
    /// </summary>
    private sealed class ThrowingVisualizer : IAttemptBundleVisualizer
    {
        public BitmapSource RenderDeviation(GrayImage screenshot, GrayImage baseTexture)
            => throw new InvalidOperationException("forced deviation failure");

        public BitmapSource RenderDetectionsOverlay(GrayImage gray,
            IReadOnlyList<TypedDetection> detections, int renderSizePx)
            => throw new InvalidOperationException("forced detections overlay failure");

        public BitmapSource RenderProjectionOverlay(CapturedFrame rawColor, MapRect mapRect,
            AreaCalibration calibration, IReadOnlyList<LandmarkReference> references,
            IReadOnlyList<TypeAwareRansacSolver.AssignedReference> inliers, int renderSizePx)
            => throw new InvalidOperationException("forced projection overlay failure");
    }
}
