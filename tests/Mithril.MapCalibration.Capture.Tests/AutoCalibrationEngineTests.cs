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
    private const string Area = EngineHarness.DefaultArea;

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
    public async Task Recorded_MapRect_height_matches_clamped_height_when_ECC_rect_overshoots_frame()
    {
        // #989: per-attempt bundle's 04-maprect.json must record the height the
        // engine actually used (the clamped height), not the pre-clamp ECC-located
        // height — otherwise the JSON disagrees with the deviation/aligned/
        // base-texture image dims in the same bundle. Reproduce the overshoot:
        // a 100×100 captured frame + an ECC rect that asks for 150 rows starting
        // at row 50 (would extend to row 200; clamps to row 100, i.e. height 50).
        var capture = new GrayImage(100, 100, new byte[100 * 100]);
        var baseTex = new GrayImage(200, 200, new byte[200 * 200]);
        var overshootingRect = new MapRect(
            OriginX: 0, OriginY: 50, Width: 100, Height: 150,
            TextureWidth: 200, TextureHeight: 200);

        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        var h = new EngineHarness
        {
            BaseTexture = baseTex,
            Refiner = new FakeRefiner(overshootingRect),
            Solve = Accepted(residual: 0.65, inliers: 5),
            SinkSelector = selector,
        };
        // Override the default 64×64 SpyCapture with a 100×100 one matching the
        // overshoot scenario. EngineHarness.Capture is get-only, but a fresh
        // harness with the right capture isn't expressible without constructing
        // the engine directly (Capture is initialized in the field initializer).
        // So construct the engine inline using the same shape as the harness.
        var captureSpy = new SpyCapture(capture);
        var solver = new SpySolver(h.Solve);
        var engine = new AutoCalibrationEngine(
            new FakeAreaState(Area),
            new FakeWindowLocator(h.GameWindow),
            new FakeRegionProvider(h.Bbox),
            captureSpy,
            h.Refiner,
            new FakeBaseTextureProvider(baseTex),
            new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
            solver,
            h.IconProvider,
            h.Service,
            logger: null,
            sinkSelector: selector);

        await engine.TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        var ctx = captured[0];
        ctx.MapRect.Should().NotBeNull();
        ctx.MapRect!.Height.Should().Be(50,
            "the engine clamped 150→50 to stay within the 100-row frame; the bundle must record the height it actually used");
        ctx.MapRect.OriginY.Should().Be(50, "OriginY is in-bounds and untouched by the clamp");
    }

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
    public async Task TryCalibrate_sink_receives_map_not_located_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        // Refiner returns null → map-not-located reject (everything upstream succeeds).
        var h = new EngineHarness
        {
            Refiner = new FakeRefiner(MapRegionRefineResult.None),
            SinkSelector = selector,
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedMapNotLocated);
    }

    /// <summary>
    /// Observability: a sub-threshold locate must still surface what the locator
    /// found (origin + size on the rejection branch) via
    /// <see cref="CalibrationAttemptContext.LocatorRawFit"/> so the diagnostic
    /// bundle records it and a future close-miss vs catastrophic rejection is
    /// self-triaging. Task 15 also threads <see cref="CalibrationAttemptContext.LocatorMetrics"/>
    /// through from the refiner so FM-style inlier/transform metrics ride alongside
    /// the rect — verified here too.
    /// </summary>
    [Fact]
    public async Task TryCalibrate_map_not_located_surfaces_LocatorRawFit_to_attempt()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        // Below-threshold raw fit: AcceptedRect=null, RawFitRect carries the
        // origin/size (the close-miss the live Kur Mountains attempt hit). Metrics
        // ride alongside so the bundle's LocatorBest block is populated.
        var rawFit = new MapRect(192, 100, 909, 909, 2048, 2048);
        var metrics = new LocateMetrics(
            InlierCount: 42,
            CandidateCount: 731,
            InlierRatio: 0.057,
            Scale: 1.0007,
            RotationDegrees: 0.12,
            Mirror: false,
            Tx: 191.4,
            Ty: 99.8,
            ResidualPixels: 2.41);
        var h = new EngineHarness
        {
            Refiner = new FakeRefiner(new MapRegionRefineResult(AcceptedRect: null, RawFitRect: rawFit, Metrics: metrics)),
            SinkSelector = selector,
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedMapNotLocated);
        captured[0].LocatorRawFit.Should().NotBeNull();
        var best = captured[0].LocatorRawFit!;
        best.OriginX.Should().Be(192);
        best.OriginY.Should().Be(100);
        best.Width.Should().Be(909);
        best.Height.Should().Be(909);
        captured[0].LocatorMetrics.Should().NotBeNull();
        captured[0].LocatorMetrics!.InlierCount.Should().Be(42);
    }

    [Fact]
    public async Task TryCalibrate_sink_receives_clamp_degenerate_outcome()
    {
        var captured = new List<CalibrationAttemptContext>();
        var selector = MakeSinkSelector(new CapturingSink(captured));
        // Refiner returns a rect whose origin sits at/past the frame's far edge so
        // ClampToFrame degrades to empty (width/height ≤ 0) and the engine bails
        // with the clamp-degenerate outcome. Frame is 64x64 (the default capture).
        var h = new EngineHarness
        {
            Refiner = new FakeRefiner(new MapRect(64, 64, 64, 64, 64, 64)),
            SinkSelector = selector,
        };

        await h.Engine().TryCalibrateCurrentAreaAsync(default);

        captured.Should().HaveCount(1);
        captured[0].Outcome.Should().Be(OutcomeVocabulary.RejectedClampDegenerate);
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

    // ── #988 monotonicity gate (engine-level) ────────────────────────────────

    [Fact]
    public async Task Replaces_existing_when_new_fit_is_better()
    {
        var svc = new FakeCalibrationService();
        svc.Seed(Area, MakeCal(residual: 3.0, refs: 6));
        var h = new EngineHarness { Solve = Accepted(residual: 0.65, inliers: 8), Service = svc };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        svc.Saved.Should().ContainKey(Area);
        svc.Saved[Area].ResidualPixels.Should().Be(0.65);
    }

    [Fact]
    public async Task Rejects_when_new_residual_blows_up_vs_existing()
    {
        // Mirrors the PR #986 Eltibule case: existing residual 0.79 px, new 4.03 px.
        // Both sides must be at the same LocatorScale regime (#1005) for the
        // monotonicity gate to fire — same in-game zoom is the original #988 case.
        var svc = new FakeCalibrationService();
        svc.Seed(Area, MakeCal(residual: 0.79, refs: 10) with { LocatorScale = 0.408 });
        var h = new EngineHarness
        {
            Solve = Accepted(residual: 4.03, inliers: 4),
            Service = svc,
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("residual");
        svc.Saved.Should().NotContainKey(Area); // existing untouched
    }

    [Fact]
    public async Task Rejects_when_new_inlier_count_drops_vs_existing()
    {
        // Same in-game zoom (matching LocatorScale) so the #1005 regime predicate
        // does NOT skip the gate; the inlier-delta arm is then free to fire.
        var svc = new FakeCalibrationService();
        svc.Seed(Area, MakeCal(residual: 1.0, refs: 10) with { LocatorScale = 0.408 });
        var h = new EngineHarness
        {
            Solve = Accepted(residual: 1.0, inliers: 4),
            Service = svc,
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("inlier");
        svc.Saved.Should().NotContainKey(Area);
    }

    // ── #988 monotonicity gate (helper-level) ────────────────────────────────

    [Fact]
    public void Monotonicity_helper_accepts_better_residual_and_same_inliers()
    {
        var existing = MakeCal(residual: 2.0, refs: 8);
        var candidate = MakeCal(residual: 1.0, refs: 8);
        AutoCalibrationEngine.CheckMonotonicAccept(existing, candidate, candidateInlierCount: 8)
            .Should().BeNull();
    }

    [Fact]
    public void Monotonicity_helper_rejects_residual_blowup_beyond_ratio()
    {
        var existing = MakeCal(residual: 1.0, refs: 8);
        var candidate = MakeCal(residual: 3.0, refs: 8); // 3× > 2× threshold
        AutoCalibrationEngine.CheckMonotonicAccept(existing, candidate, candidateInlierCount: 8)
            .Should().Contain("residual");
    }

    [Fact]
    public void Monotonicity_helper_rejects_inlier_drop_beyond_delta()
    {
        var existing = MakeCal(residual: 1.0, refs: 10);
        var candidate = MakeCal(residual: 1.0, refs: 10); // ReferenceCount on the cal is metadata
        AutoCalibrationEngine.CheckMonotonicAccept(existing, candidate, candidateInlierCount: 4) // 10 − 4 = 6 > delta 2
            .Should().Contain("inlier");
    }

    [Fact]
    public void Monotonicity_helper_accepts_marginal_within_tolerances()
    {
        var existing = MakeCal(residual: 1.0, refs: 8);
        var candidate = MakeCal(residual: 1.8, refs: 8); // 1.8 < 1.0 × 2.0
        AutoCalibrationEngine.CheckMonotonicAccept(existing, candidate, candidateInlierCount: 7) // 8 − 7 = 1 ≤ delta 2
            .Should().BeNull();
    }

    // ── #1005: scale-aware monotonicity gate ─────────────────────────────────

    [Fact]
    public async Task Persisted_calibration_carries_LocatorScale_from_the_locate_metrics()
    {
        var svc = new FakeCalibrationService();
        var h = new EngineHarness
        {
            Solve = Accepted(residual: 0.65, inliers: 5),
            Service = svc,
            // Refiner returns a populated Metrics with a known scale — the
            // engine must stamp this onto the persisted AreaCalibration so the
            // gate has it to compare on the next attempt.
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        svc.Saved[Area].LocatorScale.Should().Be(0.408);
    }

    [Fact]
    public async Task Different_scale_regime_accepts_even_when_monotonicity_would_have_rejected()
    {
        var svc = new FakeCalibrationService();
        // Seed an EXISTING calibration at scale 0.408 with high quality.
        svc.Seed(Area, SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.5, ReferenceCount = 10 });

        // Capture at scale 0.800 (different regime) with a WORSE-looking fit
        // (would trip both monotonicity arms: residual much higher, inliers much lower).
        // Different regime → gate skipped → accept.
        var h = new EngineHarness
        {
            Service = svc,
            Solve = Accepted(residual: 3.5, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.800)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        outcome.RejectReason.Should().BeNull();
        svc.Saved[Area].LocatorScale.Should().Be(0.800);
    }

    [Fact]
    public async Task Same_scale_regime_still_protects_a_good_fit_from_a_worse_one()
    {
        // The original #988 protection: same in-game zoom, second wrong-fit
        // attempt seconds later. LocatorScale values match within tolerance,
        // gate fires, prior calibration kept.
        var svc = new FakeCalibrationService();
        svc.Seed(Area, SomeBaseline() with { LocatorScale = 0.408, ResidualPixels = 0.79, ReferenceCount = 10 });

        var h = new EngineHarness
        {
            Service = svc,
            Solve = Accepted(residual: 4.03, inliers: 4),
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.411)), // within ±2%
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        // Assert structurally on OutcomeCategory rather than substring-matching
        // RejectReason — the category is the contract, the human-readable
        // reason string is diagnostic. The Eltibule pair trips the residual
        // arm first (checked before inlier-delta), but either arm is the same
        // monotonicity-reject outcome from a router POV.
        outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedNotMonotonic);
        svc.Saved.Should().NotContainKey(Area); // prior preserved (no Save call)
    }

    [Fact]
    public async Task Legacy_null_LocatorScale_on_existing_skips_the_gate()
    {
        // Legacy record (pre-#1005) has null LocatorScale. A new capture's
        // candidate has a value. IsSameScaleRegime(null, _) → false → gate skipped.
        // First re-capture stamps a value and subsequent comparisons can gate normally.
        var svc = new FakeCalibrationService();
        svc.Seed(Area, SomeBaseline() with { LocatorScale = null, ResidualPixels = 0.5, ReferenceCount = 10 });

        var h = new EngineHarness
        {
            Service = svc,
            Solve = Accepted(residual: 5.0, inliers: 3), // would normally trip both gates
            Refiner = new FakeRefiner(
                new MapRect(0, 0, 64, 64, 64, 64),
                TestLocateMetrics.ForScale(0.408)),
        };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        svc.Saved[Area].LocatorScale.Should().Be(0.408); // legacy null replaced with stamped value
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

    private static AreaCalibration MakeCal(double residual, int refs) => new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: refs, ResidualPixels: residual);

}
