using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 24 (#914): the orchestrator. Persist via SaveUserRefinement (Source =
/// AutoCapture) ONLY on gate-accept; otherwise keep the prior calibration and
/// report a reason. Short-circuit (no capture, no solve) on the §11 conditions:
/// no current area, no bbox, PG not foreground, null base texture.
/// </summary>
public sealed class AutoCalibrationEngineTests
{
    private const string Area = "AreaEltibule";

    [Fact]
    public async Task Persists_with_AutoCapture_source_on_accept()
    {
        var svc = new FakeCalibrationService();
        var h = new EngineHarness { Solve = Accepted(residual: 0.65, inliers: 5), Service = svc };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        outcome.AreaKey.Should().Be(Area);
        svc.Saved.Should().ContainKey(Area);
        svc.Saved[Area].Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public async Task Keeps_prior_calibration_when_the_gate_rejects()
    {
        var svc = new FakeCalibrationService();
        svc.Seed(Area, SomeBaseline());
        var h = new EngineHarness { Solve = Rejected("residual 25.00 px exceeds threshold 12.00 px"), Service = svc };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("residual");
        svc.Saved.Should().NotContainKey(Area); // prior untouched (no Save call)
    }

    [Fact]
    public async Task No_bbox_short_circuits_without_capturing()
    {
        var h = new EngineHarness { Bbox = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("bbox");
        h.Capture.Called.Should().BeFalse();
        h.Solver.Called.Should().BeFalse();
    }

    [Fact]
    public async Task No_current_area_short_circuits()
    {
        var h = new EngineHarness { CurrentArea = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("not in-world");
        h.Capture.Called.Should().BeFalse();
    }

    [Fact]
    public async Task PG_not_foreground_short_circuits_without_capturing()
    {
        var h = new EngineHarness { GameWindow = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("Project Gorgon");
        h.Capture.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Null_base_texture_fails_soft_without_solving()
    {
        var h = new EngineHarness { BaseTexture = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("map assets");
        h.Solver.Called.Should().BeFalse("no base texture → never reach the solver");
    }

    // ── #949: same-session icon-template demand-trigger ──────────────────────

    [Fact]
    public async Task Empty_icons_with_sidecar_wired_demand_triggers_icons_once_then_re_resolves()
    {
        // Empty icon cache + a wired extractor + InstallRoot/cacheDir set → the engine
        // invokes --icons once and re-resolves the provider in the SAME attempt.
        var icons = new FakeIconTemplateProvider(IconTemplateSet.Empty);
        var extractor = new RecordingAssetExtractor(); // success, no artifacts
        var h = new EngineHarness
        {
            IconProvider = icons,
            Extractor = extractor,
            GameConfig = new GameConfig { InstallRoot = @"C:\PG" },
            AssetCacheDir = @"C:\cache",
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        extractor.Calls.Should().ContainSingle()
            .Which.Kind.Should().Be(ExtractKind.Icons);
        // GetTemplates() is called once before the trigger and once after (re-resolve).
        icons.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Populated_icons_do_not_demand_trigger_the_sidecar()
    {
        var icons = new FakeIconTemplateProvider(OneTemplate());
        var extractor = new RecordingAssetExtractor();
        var h = new EngineHarness
        {
            IconProvider = icons,
            Extractor = extractor,
            GameConfig = new GameConfig { InstallRoot = @"C:\PG" },
            AssetCacheDir = @"C:\cache",
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        extractor.Calls.Should().BeEmpty("templates were already present — no --icons run");
        icons.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Empty_icons_without_a_wired_extractor_fails_soft_no_trigger()
    {
        // No extractor / no InstallRoot → safe-degrade: still solves (base texture fine),
        // just with an empty template set; never throws.
        var icons = new FakeIconTemplateProvider(IconTemplateSet.Empty);
        var h = new EngineHarness { IconProvider = icons }; // Extractor null by default

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Should().NotBeNull();
        h.Solver.Called.Should().BeTrue("an empty icon set is not a hard stop — the gate decides");
        icons.Calls.Should().Be(1, "no extractor → no re-resolve");
    }

    [Fact]
    public async Task Sidecar_throwing_on_icons_fails_soft_without_throwing()
    {
        var icons = new FakeIconTemplateProvider(IconTemplateSet.Empty);

        // Build an engine with a throwing extractor directly to prove fail-soft.
        var solver = new SpySolver(new CalibrationSolveResult(new AreaCalibration(1, 0, 0, 0, 6, 0.5), 6, null));
        var engine = new AutoCalibrationEngine(
            new FakeAreaState(Area),
            new FakeWindowLocator(new GameWindow(1, new CaptureRect(0, 0, 1920, 1080))),
            new FakeRegionProvider(new CaptureRect(0, 0, 64, 64)),
            new SpyCapture(new GrayImage(64, 64, new byte[64 * 64])),
            new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64)),
            new FakeBaseTextureProvider(new GrayImage(64, 64, new byte[64 * 64])),
            new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
            solver,
            icons,
            new FakeCalibrationService(),
            logger: null,
            assetExtractor: new ThrowingAssetExtractor(),
            gameConfig: new GameConfig { InstallRoot = @"C:\PG" },
            assetCacheDir: @"C:\cache");

        var act = async () => await engine.TryCalibrateCurrentAreaAsync(default);
        await act.Should().NotThrowAsync();
    }

    // ── Bundle-sink (#984 Block D) ───────────────────────────────────────────

    [Fact]
    public async Task TryCalibrate_passes_populated_context_to_sink_on_accept()
    {
        var captured = new List<CalibrationAttemptContext>();
        var capturingSink = new CapturingSink(captured);
        var selector = MakeSinkSelector(capturingSink);
        var h = new EngineHarness
        {
            Solve = Accepted(residual: 0.65, inliers: 5),
            SinkSelector = selector,
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        var ctx = captured[0];
        ctx.Outcome.Should().Be(OutcomeVocabulary.Accepted);
        ctx.RawCapture.Should().NotBeNull();
        ctx.GrayCapture.Should().NotBeNull();
        ctx.MapRect.Should().NotBeNull();
        ctx.AlignedCrop.Should().NotBeNull();
        ctx.AlignedTexture.Should().NotBeNull();
        ctx.References.Should().NotBeNull();
        ctx.Result.Should().NotBeNull();
        ctx.Result!.Calibration.Should().NotBeNull();
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_no_area_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness { CurrentArea = null, SinkSelector = selector };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedNoArea);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_no_bbox_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness { Bbox = null, SinkSelector = selector };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedNoBbox);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_pg_not_foreground_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness { GameWindow = null, SinkSelector = selector };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedPgNotForeground);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_capture_failed_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        // SpyCapture(null) → gray == null → capture-failed path
        var h = new EngineHarness { SinkSelector = selector };
        // Arrange: override capture to return null gray
        var captureSpy = new SpyCapture(null);
        var solver = new SpySolver(Accepted(0.5, 4));
        var engine = new AutoCalibrationEngine(
            new FakeAreaState(Area),
            new FakeWindowLocator(h.GameWindow),
            new FakeRegionProvider(h.Bbox),
            captureSpy,
            new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64)),
            new FakeBaseTextureProvider(h.BaseTexture),
            new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
            solver,
            h.IconProvider,
            h.Service,
            logger: null,
            sinkSelector: selector);

        await engine.TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedCaptureFailed);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_no_base_texture_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness { BaseTexture = null, SinkSelector = selector };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedNoBaseTexture);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_solve_rejection_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness
        {
            Solve = Rejected("insufficient inliers (2 < 4 required)"),
            SinkSelector = selector,
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedSolveInsufficientInliers);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CalibrationAttemptBundleSinkSelector MakeSinkSelector(ICalibrationAttemptBundleSink sink) =>
        new(new CaptureDiagnosticsOptions { DumpCalibrationBundles = true }, sink, sink);

    /// <summary>Test-double sink: records every Write call.</summary>
    private sealed class CapturingSink : ICalibrationAttemptBundleSink
    {
        private readonly List<CalibrationAttemptContext> _captured;
        public CapturingSink(List<CalibrationAttemptContext> captured) => _captured = captured;
        public void Write(CalibrationAttemptContext context) => _captured.Add(context);
    }

    private static IconTemplateSet OneTemplate() => new(new[]
    {
        new IconTemplate("landmark_npc", "Npc", 0.5, 0.5,
            new GrayImage(2, 2, new byte[4]), new GrayImage(2, 2, new byte[4])),
    });

    private static CalibrationSolveResult Accepted(double residual, int inliers) =>
        new(new AreaCalibration(1.2, 0.1, 100, 100, inliers, residual), inliers, null);

    private static CalibrationSolveResult Rejected(string reason) => new(null, 0, reason);

    private static AreaCalibration SomeBaseline() =>
        new(1.0, 0, 50, 50, 4, 3.0) { Source = CalibrationSource.BundledBaseline };

    /// <summary>
    /// Mutable harness: each property has a sensible "happy path" default; a test
    /// overrides exactly the one input it exercises. Setting a reference-type
    /// property to <c>null</c> models the absence of that input.
    /// </summary>
    private sealed class EngineHarness
    {
        public string? CurrentArea { get; init; } = Area;
        public CaptureRect? Bbox { get; init; } = new CaptureRect(0, 0, 64, 64);
        public GameWindow? GameWindow { get; init; } = new GameWindow(1, new CaptureRect(0, 0, 1920, 1080));
        public GrayImage? BaseTexture { get; init; } = new GrayImage(64, 64, new byte[64 * 64]);
        public CalibrationSolveResult Solve { get; init; } = new(new AreaCalibration(1, 0, 0, 0, 6, 0.5), 6, null);
        public FakeCalibrationService Service { get; init; } = new();

        // #949: icon templates resolve per attempt via IIconTemplateProvider.
        public FakeIconTemplateProvider IconProvider { get; init; } = new();

        // Optional sidecar wiring for the same-session --icons demand-trigger path.
        // Leave null (default) to model "no extractor wired" (the unit-branch shape).
        public RecordingAssetExtractor? Extractor { get; init; }
        public GameConfig? GameConfig { get; init; }
        public string? AssetCacheDir { get; init; }

        // Optional sink selector for bundle-sink tests. Null → engine default (null sink).
        public CalibrationAttemptBundleSinkSelector? SinkSelector { get; init; }

        public SpyCapture Capture { get; } = new(new GrayImage(64, 64, new byte[64 * 64]));
        public SpySolver Solver { get; private set; } = null!;

        public AutoCalibrationEngine Engine()
        {
            Solver = new SpySolver(Solve);
            return new AutoCalibrationEngine(
                new FakeAreaState(CurrentArea),
                new FakeWindowLocator(GameWindow),
                new FakeRegionProvider(Bbox),
                Capture,
                new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64)),
                new FakeBaseTextureProvider(BaseTexture),
                new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
                Solver,
                IconProvider,
                Service,
                logger: null,
                sinkSelector: SinkSelector,
                assetExtractor: Extractor,
                gameConfig: GameConfig,
                assetCacheDir: AssetCacheDir);
        }
    }
}
