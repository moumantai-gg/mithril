using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 26 (#914): the auto-attempt trigger. On scene-change, fire the engine iff
/// a bbox is set AND the game is focused AND the scene is uncalibrated OR carries
/// only a BundledBaseline. NEVER overwrite an existing UserRefinement/AutoCapture
/// on the auto path (the manual hotkey always attempts). Debounce repeats.
/// </summary>
public sealed class AutoCalibrationTriggerTests
{
    private const string Area = "AreaEltibule";
    private const string AssetKey = "Map_AreaEltibule";

    private static MapSceneRef Scene() => new(Area, null, AssetKey);

    [Fact]
    public async Task Does_not_attempt_when_no_bbox()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: null, focused: true);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Attempts_when_bbox_present_and_focused_and_uncalibrated()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_attempt_when_game_unfocused()
    {
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: false);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Does_not_overwrite_an_existing_auto_capture()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        var cal = new AreaCalibration(1, 0, 0, 0, 6, 0.5) { Source = CalibrationSource.AutoCapture };
        svc.Seed(AssetKey, cal);
        svc.SeedAllSources(AssetKey, new[] { cal });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0, "an existing auto-capture is not re-attempted on every zone-in");
    }

    [Fact]
    public async Task A_persisted_success_is_not_re_attempted_on_re_entry()
    {
        // Fix C: a persisted area is marked "done" and a later re-entry to it does
        // NOT re-attempt (replaces the old debounce semantics — the suppression is
        // now keyed on a SUCCESSFUL persist, not merely on having attempted).
        var engine = new SpyAutoCalibrationEngine(persisted: true);
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true);
        await trigger.OnSceneChangedAsync(Scene());
        await trigger.OnSceneChangedAsync(Scene()); // genuine later re-entry to the same scene
        engine.Calls.Should().Be(1, "a persisted success is never re-attempted on re-entry");
    }

    [Fact]
    public async Task A_rejected_attempt_retries_on_a_later_re_entry()
    {
        // Fix C: a non-persisted (fail-soft) outcome leaves the area un-marked, so
        // a genuine later area-change event for that area gets a fresh attempt
        // (the "user zones out, zooms the map, zones back" recovery path).
        var engine = new SpyAutoCalibrationEngine(persisted: false);
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true);
        await trigger.OnSceneChangedAsync(Scene());
        await trigger.OnSceneChangedAsync(Scene()); // later re-entry after the reject
        engine.Calls.Should().Be(2, "a rejected auto-attempt must retry on a fresh re-entry");
    }

    [Fact]
    public async Task A_persisted_success_clears_the_status_chip()
    {
        var engine = new SpyAutoCalibrationEngine(persisted: true);
        var overlay = new FakeOverlayWindow();
        overlay.SetStatusMessage("a stale message");
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, overlay: overlay);
        await trigger.OnSceneChangedAsync(Scene());
        overlay.StatusMessage.Should().BeNull("a silent upgrade clears the chip on persist (spec §10)");
    }

    [Fact]
    public async Task A_reject_surfaces_the_actionable_reason_on_the_chip()
    {
        var engine = new SpyAutoCalibrationEngine(persisted: false);
        var overlay = new FakeOverlayWindow();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, overlay: overlay);
        await trigger.OnSceneChangedAsync(Scene());
        overlay.StatusMessage.Should().NotBeNullOrWhiteSpace("an actionable auto-reject tells the user why auto-cal isn't engaging");
    }

    // -------------------------------------------------------------------------
    // Group D1: pre-flight tests using GetAllSources (mithril#1046 §10.4).
    // These tests are written against the NEW pre-flight rule (D2) and are
    // intentionally RED under the old GetCalibration-based rule.
    // -------------------------------------------------------------------------

    private static AreaCalibration Cal(double residual, int refs, CalibrationSource source,
        CalibrationFrame frame = CalibrationFrame.Texture) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: refs, ResidualPixels: residual)
        { Source = source, Frame = frame };

    // mithril#1082 §6: the trigger gates on Frame=Texture && Source in {AutoCapture,
    // BundledBaseline}. An overlay-frame UserRefinement (Legolas-wizard) record must
    // NOT block the auto path — that's the bug this issue closes.
    [Fact]
    public async Task Trigger_StoreHasOverlayFrameUserRefinement_Fires()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.SeedAllSources(AssetKey, new[]
        {
            Cal(0.8, 6, CalibrationSource.UserRefinement, CalibrationFrame.Overlay),
        });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(1,
            "an overlay-frame UserRefinement does not satisfy the texture-frame gate (mithril#1082 regression)");
    }

    [Fact]
    public async Task Trigger_StoreHasTextureFrameAutoCapture_Skips()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.SeedAllSources(AssetKey, new[]
        {
            Cal(0.6, 5, CalibrationSource.AutoCapture, CalibrationFrame.Texture),
        });
        var logger = new CapturingLogger();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            service: svc, logger: logger);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0, "store has a converged texture-frame AutoCapture record");
        logger.Entries.Should().Contain(e => e.Message.Contains("converged texture-frame AutoCapture record"),
            "trigger must log why it skipped");
    }

    [Fact]
    public async Task Trigger_StoreHasTextureFrameBundledBaseline_Skips()
    {
        // Cold-boot retry-storm prevention (spec §6 / §10): a texture-frame BundledBaseline
        // is "good enough for v1 release"; AutoCal does not retry over it on every cold boot.
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.SeedAllSources(AssetKey, new[]
        {
            Cal(2.1, 6, CalibrationSource.BundledBaseline, CalibrationFrame.Texture),
        });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0,
            "texture-frame BundledBaseline is converged; one-shot-per-install respected");
    }

    [Fact]
    public async Task Trigger_StoreHasBothFrames_TextureSatisfied_Skips()
    {
        // Overlay-frame UserRefinement + texture-frame BundledBaseline coexist on
        // the same scene (the SceneRefinements shape mithril#1082 introduces). The
        // texture-frame record satisfies the trigger gate; the overlay record is
        // orthogonal.
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.SeedAllSources(AssetKey, new[]
        {
            Cal(0.8, 6, CalibrationSource.UserRefinement, CalibrationFrame.Overlay),
            Cal(2.1, 6, CalibrationSource.BundledBaseline, CalibrationFrame.Texture),
        });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0,
            "the texture-frame baseline satisfies the gate even though an overlay-frame record also exists");
    }

    [Fact]
    public async Task Trigger_StoreEmpty_Fires()
    {
        // Store returns empty (cold install); engine must be invoked.
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        // SeedAllSources not called → GetAllSources returns empty
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(1, "no solve in store; cold install should trigger the engine");
    }

    [Fact]
    public async Task Trigger_PickerReturnsBaselineButStoreHasAuto_Skips()
    {
        // Picker prefers the higher-quality BundledBaseline (lower residual, more refs),
        // but the store ALSO holds an AutoCapture. The new pre-flight reads GetAllSources
        // and sees AutoCapture → must skip. The old rule reads GetCalibration (picker)
        // which returns BundledBaseline → would fire. This test is RED under the old rule.
        var auto = Cal(1.2, 5, CalibrationSource.AutoCapture);
        var baseline = Cal(0.5, 8, CalibrationSource.BundledBaseline);
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.SeedAllSources(AssetKey, new[] { auto, baseline });
        svc.Seed(AssetKey, baseline); // picker returns baseline (preferred by residual)
        var logger = new CapturingLogger();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            service: svc, logger: logger);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0,
            "store has an AutoCapture even though the picker chose the better Baseline; trigger must respect the store");
        logger.Entries.Should().Contain(e => e.Message.Contains("picker returned BundledBaseline"),
            "trigger must emit the picker-disagrees-with-store log");
    }

    private static AutoCalibrationTrigger Build(
        SpyAutoCalibrationEngine engine, CaptureRect? bbox, bool focused,
        FakeCalibrationService? service = null, FakeOverlayWindow? overlay = null,
        ILogger? logger = null)
        => new(
            new FakeDomainEventSubscriber(),
            engine,
            new FakeRegionProvider(bbox),
            new FakeWindowLocator(focused ? new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)) : null),
            service ?? new FakeCalibrationService(),
            new FakeMapState { CurrentArea = Area, CurrentMapScene = new MapSceneRef(Area, null, AssetKey) },
            new FakeSceneAssetCache(),
            overlay ?? new FakeOverlayWindow(),
            logger ?? NullLogger.Instance);
}
