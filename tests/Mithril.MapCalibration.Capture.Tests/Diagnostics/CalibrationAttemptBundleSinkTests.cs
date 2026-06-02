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
