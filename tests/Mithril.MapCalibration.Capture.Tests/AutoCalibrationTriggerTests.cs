using System.Threading;
using System.Threading.Tasks;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
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
        ILogger? logger = null,
        FakeDomainEventSubscriber? bus = null,
        FakeReplayProgress? replay = null)
        => new(
            bus ?? new FakeDomainEventSubscriber(),
            // mithril#1117: default to "replay complete" so the existing
            // OnSceneChangedAsync-direct tests aren't gated. The new replay-gate
            // tests pass their own FakeReplayProgress(completed: false) explicitly.
            replay ?? new FakeReplayProgress(completed: true),
            engine,
            new FakeRegionProvider(bbox),
            new FakeWindowLocator(focused ? new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)) : null),
            service ?? new FakeCalibrationService(),
            new FakeMapState { CurrentArea = Area, CurrentMapScene = new MapSceneRef(Area, null, AssetKey) },
            new FakeSceneAssetCache(),
            overlay ?? new FakeOverlayWindow(),
            logger ?? NullLogger.Instance);

    // -------------------------------------------------------------------------
    // mithril#1117: replay-gate tests. The trigger must NOT subscribe to
    // AreaChanged / MapAssetChanged while Player.log replay is in flight, because
    // replayed past scene transitions fire capture+solve against the CURRENT
    // screen against an UNRELATED historical scene's bundled texture and
    // contaminate the diagnostics store with rejected attempts for scenes the
    // user never visited this session.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_does_not_subscribe_while_replay_is_in_flight()
    {
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: false);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);

        bus.SubscriptionCount.Should().Be(0,
            "the trigger must defer Subscribe until ReplayComplete so replayed past scene transitions don't fire capture+solve");

        await trigger.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Events_published_during_replay_do_not_reach_the_handler()
    {
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: false);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);
        // Simulate Arda re-emitting a past scene transition during the replay catch-up phase.
        var replayMeta = new LogLineMetadata(
            Timestamp: new DateTimeOffset(2026, 6, 10, 10, 18, 36, TimeSpan.Zero),
            ReadOn: new DateTimeOffset(2026, 6, 10, 10, 18, 36, TimeSpan.Zero),
            IsReplay: true);
        bus.Publish(new AreaChanged(PreviousArea: null, CurrentArea: Area, replayMeta));
        bus.Publish(new MapAssetChanged(
            PreviousScene: null,
            CurrentScene: new MapSceneRef(Area, null, AssetKey),
            replayMeta));

        engine.Calls.Should().Be(0,
            "replay-time events must not reach the handler — the locator would screenshot the current scene against an unrelated historical scene's texture");

        await trigger.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Subscribes_after_replay_completes()
    {
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: false);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);
        bus.SubscriptionCount.Should().Be(0);

        replay.Complete();
        // The deferred Subscribe runs on the thread pool; wait until it lands.
        await WaitForAsync(() => bus.SubscriptionCount > 0);

        bus.HasSubscriber<AreaChanged>().Should().BeTrue(
            "AreaChanged subscription must be established once replay completes");
        bus.HasSubscriber<MapAssetChanged>().Should().BeTrue(
            "MapAssetChanged subscription must be established once replay completes");

        await trigger.StopAsync(CancellationToken.None);
        bus.SubscriptionCount.Should().Be(0,
            "StopAsync must dispose every subscription handle the trigger created");
    }

    [Fact]
    public async Task StopAsync_synchronises_deferred_subscribe_after_Complete()
    {
        // mithril#1130 review: closes the TOCTOU where ReplayComplete resolves
        // and the continuation passes the cancellation check, then StopAsync
        // runs to completion BEFORE SubscribeNow has actually called
        // bus.Subscribe. Without the _subscribeGate + StopAsync.await of the
        // deferred task, two handlers would land on the bus after StopAsync
        // returned. This test exercises the Complete-then-immediate-Stop ordering
        // and asserts the contract: after StopAsync returns, no handlers are live.
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: false);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);
        replay.Complete();
        await trigger.StopAsync(CancellationToken.None);

        bus.SubscriptionCount.Should().Be(0,
            "StopAsync must await the deferred subscribe task and dispose any handle the gap-window subscribe created");
    }

    [Fact]
    public async Task StartAsync_subscribes_synchronously_when_replay_already_complete()
    {
        // Headless tests, second-instance takeover, or a tail-only restart all
        // hit StartAsync with ReplayComplete already resolved. The trigger should
        // subscribe synchronously in that case — no thread-pool hop, no race.
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: true);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);

        bus.HasSubscriber<AreaChanged>().Should().BeTrue();
        bus.HasSubscriber<MapAssetChanged>().Should().BeTrue();

        await trigger.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_before_replay_completes_leaves_the_bus_un_subscribed()
    {
        var bus = new FakeDomainEventSubscriber();
        var replay = new FakeReplayProgress(completed: false);
        var engine = new SpyAutoCalibrationEngine();
        var trigger = Build(engine, bbox: new CaptureRect(0, 0, 64, 64), focused: true,
            bus: bus, replay: replay);

        await trigger.StartAsync(CancellationToken.None);
        await trigger.StopAsync(CancellationToken.None);
        replay.Complete();

        // Give the deferred-subscribe task a moment to observe the cancellation.
        await Task.Delay(50);
        bus.SubscriptionCount.Should().Be(0,
            "a Stop before ReplayComplete must cancel the deferred Subscribe — the trigger stays inert");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        condition().Should().BeTrue("the awaited condition should have flipped within {0}ms", timeoutMs);
    }
}
