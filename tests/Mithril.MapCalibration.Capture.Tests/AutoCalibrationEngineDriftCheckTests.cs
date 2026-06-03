using System.Diagnostics;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Tests for <see cref="IAutoCalibrationRunner.CheckDriftAsync"/> (mithril#1046 §6).
/// </summary>
public sealed class AutoCalibrationEngineDriftCheckTests
{
    private const string Asset = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Asset);

    /// <summary>
    /// Stored calibration with Scale=1, Origin=(100,100), MirrorNorth=false,
    /// RotationRadians=0. With these values WorldToWindow collapses to:
    ///   screenX = 100 + worldX
    ///   screenY = 100 - worldZ
    /// which is the same formula <see cref="TestDetections"/> uses.
    /// </summary>
    private static AreaCalibration Stored(double residual = 0.7) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: residual)
        {
            Source = CalibrationSource.AutoCapture,
        };

    // ---- outcome cases ----

    [Fact]
    public async Task DriftCheck_NoStoredCalibration_ReturnsNoStoredCalibration()
    {
        var engine = NewEngine(cal: null);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.NoStoredCalibration>();
    }

    [Fact]
    public async Task DriftCheck_PredictedMatchesDetections_ReturnsOk()
    {
        var engine = NewEngine(
            cal: Stored(),
            seededDetections: TestDetections.AtPredictedPositions(offsetPx: 0.5));
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.Ok>()
            .Which.MatchedReferences.Should().Be(6);
    }

    [Fact]
    public async Task DriftCheck_PredictedMissesDetections_ReturnsDrift()
    {
        // threshold = DriftToleranceFactor(3.0) * stored.ResidualPixels(0.7) = 2.1
        // offset of 5.0 px exceeds the 20 px gate — so those landmarks are NOT
        // matched. Use an offset large enough to exceed threshold but small enough
        // to be within the 20 px pairing gate.
        var engine = NewEngine(
            cal: Stored(residual: 0.7),
            seededDetections: TestDetections.AtPredictedPositions(offsetPx: 5.0));
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        var drift = outcome.Should().BeOfType<DriftCheckOutcome.Drift>().Subject;
        drift.MaxResidualPx.Should().BeGreaterThan(2.1); // 3.0 × 0.7
        drift.ThresholdPx.Should().BeApproximately(2.1, 0.01);
    }

    [Fact]
    public async Task DriftCheck_FewerThan3Matched_ReturnsInconclusive()
    {
        // Only 2 detections provided; the rest have no pair within 20 px.
        var engine = NewEngine(
            cal: Stored(),
            seededDetections: TestDetections.AtFirstNPredictions(2, offsetPx: 0.5));
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.Inconclusive>()
            .Which.MatchedReferences.Should().Be(2);
    }

    [Fact]
    public async Task DriftCheck_LocatorFails_ReturnsMapNotLocated()
    {
        var engine = NewEngine(
            cal: Stored(),
            refiner: FakeMapRegionRefinerDrift.Reject("low inlier count"));
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.MapNotLocated>();
    }

    [Fact]
    public async Task DriftCheck_CaptureFails_ReturnsCaptureFailed()
    {
        var engine = NewEngine(
            cal: Stored(),
            captureReturnsNull: true);
        var outcome = await engine.CheckDriftAsync(CancellationToken.None);
        outcome.Should().BeOfType<DriftCheckOutcome.CaptureFailed>();
    }

    // ---- observability cases ----

    [Fact]
    public async Task DriftCheck_LogsExpectedSequence()
    {
        var logger = new CapturingLogger();
        var engine = NewEngine(
            cal: Stored(),
            seededDetections: TestDetections.AtPredictedPositions(offsetPx: 0.5),
            logger: logger);
        await engine.CheckDriftAsync(CancellationToken.None);
        logger.Entries.Should().Contain(e => e.Message.Contains("Drift check"));
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("locator scale=") ||
            e.Message.Contains("scale=") ||
            e.Message.Contains("Scale=") ||
            e.Message.Contains("locate") ||
            e.Message.Contains("refin"));
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Ok") ||
            e.Message.Contains("ok") ||
            e.Message.Contains("matched") ||
            e.Message.Contains("residual"));
    }

    [Fact]
    public async Task DriftCheck_EmitsCalibrationDriftCheckSpan()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name.StartsWith("Mithril.MapCalibration", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var engine = NewEngine(
            cal: Stored(),
            seededDetections: TestDetections.AtPredictedPositions(offsetPx: 0.5));
        await engine.CheckDriftAsync(CancellationToken.None);

        var span = spans.Should()
            .ContainSingle(s => s.OperationName == "calibration.drift_check")
            .Subject;
        span.GetTagItem("outcome").Should().Be("Ok");
        span.GetTagItem("refs.matched").Should().NotBeNull();
    }

    // ---- factory ----

    private static AutoCalibrationEngine NewEngine(
        AreaCalibration? cal,
        IReadOnlyList<TypedDetection>? seededDetections = null,
        IMapRegionRefiner? refiner = null,
        bool captureReturnsNull = false,
        CapturingLogger? logger = null)
    {
        // Wire the scene so SceneResolution.ResolveCurrentScene returns Scene.
        var mapState = new FakeMapState
        {
            CurrentArea = Scene.ParentAreaKey,
            CurrentMapScene = Scene,
        };
        var sceneCache = new FakeSceneAssetCache();

        // The drift-check path needs: foreground window + bbox so capture gate passes.
        var windowLocator = new FakeWindowLocator(
            new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)));
        var regionProvider = new FakeRegionProvider(
            new CaptureRect(0, 0, 400, 400));

        // Capture: null-gray drives the CaptureFailed branch.
        GrayImage? gray = captureReturnsNull
            ? null
            : new GrayImage(400, 400, new byte[400 * 400]);
        var capture = new SpyCapture(gray);

        // Refiner: default to Accept.
        var actualRefiner = refiner ?? FakeMapRegionRefinerDrift.Accept();

        // Base texture: any non-null GrayImage.
        var baseTextures = new FakeBaseTextureProvider(
            new GrayImage(400, 400, new byte[400 * 400]));

        // References: six landmarks.
        var references = new FakeDriftAreaRefs();

        // Solver stub: DetectOnly returns seededDetections; Solve throws.
        var solver = new FakeCalibrationSolverDrift
        {
            SeededDetections = seededDetections ?? Array.Empty<TypedDetection>(),
        };

        // Icon templates: empty (the stub solver short-circuits before templates are used).
        var iconTemplates = new FakeIconTemplateProvider(IconTemplateSet.Empty);

        // Calibration service: returns `cal` for the test scene.
        var calService = new FakeCalibrationService();
        if (cal is not null)
            calService.Seed(Asset, cal);

        return new AutoCalibrationEngine(
            mapState,
            sceneCache,
            windowLocator,
            regionProvider,
            capture,
            actualRefiner,
            baseTextures,
            references,
            solver,
            iconTemplates,
            calService,
            logger: logger);
    }
}
