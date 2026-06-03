using System.Threading.Tasks;
using FluentAssertions;
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
    public async Task Attempts_when_only_a_bundled_baseline_exists()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(AssetKey, new AreaCalibration(1, 0, 0, 0, 4, 3) { Source = CalibrationSource.BundledBaseline });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(1, "a bundled baseline is upgradeable by the auto path");
    }

    [Fact]
    public async Task Does_not_overwrite_an_existing_user_refinement()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(AssetKey, new AreaCalibration(1, 0, 0, 0, 6, 0.5) { Source = CalibrationSource.UserRefinement });
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true, service: svc);
        await trigger.OnSceneChangedAsync(Scene());
        engine.Calls.Should().Be(0, "the auto path never displaces a user refinement");
    }

    [Fact]
    public async Task Does_not_overwrite_an_existing_auto_capture()
    {
        var engine = new SpyAutoCalibrationEngine();
        var svc = new FakeCalibrationService();
        svc.Seed(AssetKey, new AreaCalibration(1, 0, 0, 0, 6, 0.5) { Source = CalibrationSource.AutoCapture });
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

    private static AutoCalibrationTrigger Build(
        SpyAutoCalibrationEngine engine, CaptureRect? bbox, bool focused,
        FakeCalibrationService? service = null, FakeOverlayWindow? overlay = null)
        => new(
            new FakeDomainEventSubscriber(),
            engine,
            new FakeRegionProvider(bbox),
            new FakeWindowLocator(focused ? new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)) : null),
            service ?? new FakeCalibrationService(),
            new FakeMapState { CurrentArea = Area, CurrentMapScene = new MapSceneRef(Area, null, AssetKey) },
            new FakeSceneAssetCache(),
            overlay ?? new FakeOverlayWindow(),
            NullLogger.Instance);
}
