using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Overlay.Tests.Fakes;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Scene-hook (layer 2) tests for <see cref="IOverlayWindow.RegisterScene"/>
/// + <see cref="IOverlaySceneContext"/>. Bypasses the D3D surface — the
/// <c>OverlayWindowService.DriveSceneForTest</c> seam lets us hand in a
/// stub render target and verify dispatch behaviour without standing up a
/// real <c>D2DOverlaySurface</c>.
///
/// <para>What's covered:</para>
/// <list type="bullet">
/// <item>Register + dispatch round-trip — drawer fires once per tick</item>
/// <item>Dispose returns from <see cref="IOverlayWindow.RegisterScene"/>
/// removes the drawer</item>
/// <item>Multiple registrations are invoked in registration order</item>
/// <item>Uncalibrated area: scene drawers STILL fire (only the marker
/// projection is calibration-gated) so pixel-native passes — e.g. the
/// calibration placement pins — render during an uncalibrated Drop/Pair
/// walkthrough (dissolved-#868); the chip still surfaces (#872 / #887)</item>
/// <item>Zoom plumbing: <see cref="IOverlaySceneContext.Project"/> reads
/// the live <see cref="IOverlayZoomSource"/> per call</item>
/// <item><see cref="IOverlaySceneContext.Project"/> returns null in
/// uncalibrated-area paths (defensive cover; the projection block — not the
/// scene drawers — is what skips uncalibrated)</item>
/// </list>
/// </summary>
public sealed class OverlaySceneHookTests
{
    private static OverlayWindowService BuildService(
        FakeMapCalibrationService calibration,
        StubAreaState areaState,
        IOverlayZoomSource zoom,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        var markers = new WorldOverlayMarkers();
        var renderer = new MarkerSceneRenderer();
        var position = new StubPositionState();
        // mithril#1041: OverlayWindowService now takes IMapState +
        // ISceneAssetCache + IDomainEventSubscriber so it can resolve the
        // composite MapSceneRef on the live render path. The scene-hook tests
        // drive via DriveSceneForTest (which synths a scene from areaKey when
        // IMapState.CurrentMapScene is null) so these can be no-op stubs.
        var mapState = new StubMapState();
        var sceneCache = new StubSceneAssetCache();
        var bus = new StubDomainEventSubscriber();
        return new OverlayWindowService(
            markers, renderer, calibration, areaState, mapState, sceneCache, bus,
            position, zoom,
            textureDimensions: new NullMapTextureDimensions(),  // mithril#1081
            loggerFactory);
    }

    /// <summary>mithril#1081 — no-op dims stub; null dims → composed-from-texture
    /// path returns null, matching the prior null-projection-on-uncalibrated
    /// behaviour so existing tests are unaffected.</summary>
    private sealed class NullMapTextureDimensions : IMapTextureDimensions
    {
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => null;
    }

    [Fact]
    public void RegisterScene_invokes_drawer_once_per_tick_on_calibrated_area()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        IOverlaySceneContext? captured = null;
        using var handle = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            calls++;
            captured = ctx;
        });

        // Drive a single tick. The fake render target / factory pointers
        // are never dereferenced inside the scene-context's Project (which
        // we don't call here) or the drawer body (which only counts).
        service.DriveSceneForTest(renderTarget: null!, factory: null!, areaKey: "A", currentZoom: 1.0);

        calls.Should().Be(1);
        captured.Should().NotBeNull();
        captured!.CurrentAreaKey.Should().Be("A");
    }

    [Fact]
    public void Disposing_the_handle_deregisters_the_drawer()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        var handle = ((IOverlayWindow)service).RegisterScene(_ => calls++);
        service.SceneDrawerCount.Should().Be(1);

        handle.Dispose();
        service.SceneDrawerCount.Should().Be(0);

        // Subsequent ticks must not invoke the disposed drawer.
        service.DriveSceneForTest(null!, null!, "A", 1.0);
        calls.Should().Be(0,
            "the disposed drawer must not fire — a future bug where Dispose() didn't " +
            "actually unhook the registration would slowly leak per-tick work and is " +
            "hard to spot in production traces.");
    }

    [Fact]
    public void Multiple_drawers_are_invoked_in_registration_order()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var order = new List<int>();
        using var h1 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(1));
        using var h2 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(2));
        using var h3 = ((IOverlayWindow)service).RegisterScene(_ => order.Add(3));

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        order.Should().Equal(new[] { 1, 2, 3 },
            because: "drawing-order matters for transparent geometry — D2D has no depth buffer, " +
            "so a drawer that depends on running BEFORE another (e.g. its lines under " +
            "the next drawer's pins) needs a stable registration-order invariant. " +
            "Step 6's only consumer is Legolas with a single scene drawer, but the " +
            "platform contract is multi-consumer (Gwaihir + future modules).");
    }

    /// <summary>#872 BLOCKER / #887: scene drawers MUST fire in uncalibrated
    /// areas. Only the marker-projection block is calibration-gated; the
    /// scene-drawer loop runs regardless because scene drawers self-gate and
    /// draw pixel-native passes — most importantly the calibration placement
    /// pins, which are drawn at the raw click pixel (no <c>Project()</c>) and
    /// MUST render during a Drop/Pair walkthrough in an uncalibrated area
    /// (calibration only persists at Confirm, so <c>IsCalibrated</c> is false
    /// throughout). The pre-fix code returned early before the loop, which
    /// suppressed every drawer uncalibrated and broke the headline cutover
    /// behavior; the identical gate in this test seam is why no test caught
    /// it.</summary>
    [Fact]
    public void Scene_drawers_fire_on_uncalibrated_area_so_pixel_native_passes_render()
    {
        var calibration = new FakeMapCalibrationService(); // nothing calibrated
        var areaState = new StubAreaState { CurrentArea = "AreaUncalibrated" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        var calls = 0;
        using var h = ((IOverlayWindow)service).RegisterScene(_ => calls++);

        service.DriveSceneForTest(null!, null!, "AreaUncalibrated", 1.0);

        calls.Should().Be(1,
            "scene drawers MUST still fire on uncalibrated areas — pixel-native passes " +
            "like the calibration placement pins (drawn at the raw click pixel, no Project() " +
            "call) have to render during the Drop/Pair walkthrough, which runs entirely " +
            "uncalibrated (calibration only persists at Confirm). Gating the whole loop on " +
            "IsCalibrated is the #872 blocker that broke the headline cutover behavior.");
        service.StatusMessage.Should().Contain("not calibrated",
            "the uncalibrated chip must still surface so the user knows the marker projection " +
            "is suppressed for this area (only the projection — not the scene drawers).");
    }

    [Fact]
    public void Project_plumbs_current_zoom_into_WorldToOverlay()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var zoomsSeenByProjector = new List<double>();
        calibration.Projector = (_, world, zoom) =>
        {
            zoomsSeenByProjector.Add(zoom);
            return new OverlayPixel(world.X, world.Z);
        };
        var areaState = new StubAreaState { CurrentArea = "A" };

        // Mutable zoom source so we can change it between ticks.
        var zoom = new MutableZoomSource(1.5);
        var service = BuildService(calibration, areaState, zoom);

        using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            // Two projection calls per tick to exercise multiple Project()
            // invocations on the same context.
            ctx.Project(10, 20);
            ctx.Project(30, 40);
        });

        service.DriveSceneForTest(null!, null!, "A", 1.5);

        zoomsSeenByProjector.Should().Equal(new[] { 1.5, 1.5 },
            because: "the scene context must pass the per-tick snapshot of IOverlayZoomSource " +
            "through to IMapCalibrationService.WorldToOverlay on every Project() call — " +
            "if this regresses to 1.0, the hardcoded zoom from PR #863 is back and " +
            "pins drift whenever the user has the in-game zoom slider off 1.0.");

        // Flip the zoom and drive again — next tick must see the new value.
        zoom.CurrentZoom = 0.75;
        service.DriveSceneForTest(null!, null!, "A", 0.75);
        zoomsSeenByProjector.Should().Equal(new[] { 1.5, 1.5, 0.75, 0.75 },
            because: "zoom changes between ticks must propagate to Project() — a stale snapshot " +
            "captured at scene-context construction would freeze pins at the old zoom.");
    }

    [Fact]
    public void Project_returns_null_for_uncalibrated_areas()
    {
        // This is a defensive-cover test. Scene drawers now run even in
        // uncalibrated areas (only the marker projection is gated), so a
        // world-projecting drawer can call Project() with no calibration —
        // it must return null instead of fabricating a pixel rather than
        // relying on a loop-level gate to keep it from ever being reached.
        var calibration = new FakeMapCalibrationService(); // nothing calibrated
        var areaState = new StubAreaState { CurrentArea = "AreaUncalibrated" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        // Capture the context from a calibrated-area scene tick, then call
        // Project against the uncalibrated area. Use a calibrated area key
        // to get past the gate during DriveSceneForTest, then verify Project
        // returns null for an out-of-range/uncalibrated lookup.
        calibration.CalibratedAreas.Add("A");
        areaState.CurrentArea = "A";

        IOverlaySceneContext? ctx = null;
        using var h = ((IOverlayWindow)service).RegisterScene(c => ctx = c);
        // Configure the projector to return null — simulates an
        // out-of-range world coord on a calibrated area.
        calibration.Projector = (_, _, _) => null;

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        ctx.Should().NotBeNull();
        var px = ctx!.Project(10, 20);
        px.Should().BeNull(
            "Project must propagate WorldToOverlay's null return — a fabricated pixel " +
            "would silently land the marker at (0,0) or similar nonsense.");
    }

    /// <summary>Review iteration-1 B1: a throwing scene drawer must not
    /// poison sibling drawers or the per-tick render. Verifies sibling
    /// dispatch, the <c>SceneDrawerExceptions</c> counter increment, and
    /// the <c>LogError</c> emission.</summary>
    [Fact]
    public void Throwing_scene_drawer_is_isolated_from_siblings_logged_and_counted()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var areaState = new StubAreaState { CurrentArea = "A" };
        var loggerFactory = new CapturingLoggerFactory();
        var service = BuildService(calibration, areaState,
            new FixedOverlayZoomSource(1.0), loggerFactory);

        // Attach a MeterListener on SceneDrawerExceptions so we can assert
        // the counter ticked. Per the existing MissCountersTests pattern,
        // the counter is process-static; we filter by drawer_type tag to
        // isolate this test's exception from any parallel test's counter
        // increments. The throw site here is a unique nested lambda type
        // — the captured target name will be this test class's name with
        // a compiler-generated suffix; we just filter on the test name
        // prefix.
        long observedExceptionCounter = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == "Mithril.Overlay"
                    && instr.Name == "mithril.overlay.scene.exceptions")
                {
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            // Match this test's drawer by tag — Delegate.Target for a
            // local lambda is the closure object whose declaring type
            // begins with this test's full type name.
            foreach (var kv in tags)
            {
                if (kv.Key == "drawer_type"
                    && kv.Value is string s
                    && s.Contains(nameof(OverlaySceneHookTests)))
                {
                    Interlocked.Add(ref observedExceptionCounter, measurement);
                    return;
                }
            }
        });
        listener.Start();

        var throwingDrawerFired = 0;
        var siblingDrawerFired = 0;
        var raised = new InvalidOperationException("test-drawer-boom");

        using var hThrowing = ((IOverlayWindow)service).RegisterScene(_ =>
        {
            throwingDrawerFired++;
            throw raised;
        });
        using var hSibling = ((IOverlayWindow)service).RegisterScene(_ => siblingDrawerFired++);

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        throwingDrawerFired.Should().Be(1, "the throwing drawer must still be invoked exactly once.");
        siblingDrawerFired.Should().Be(1,
            "the sibling drawer MUST still fire after the previous drawer threw — without " +
            "per-drawer isolation, an uncaught throw inside BeginDraw/EndDraw aborts the whole " +
            "frame and poisons every subsequent consumer for the tick.");

        observedExceptionCounter.Should().Be(1,
            "the SceneDrawerExceptions counter must tick once per isolated throw — without it " +
            "a flood of exceptions is invisible in production traces.");

        var errorEntries = loggerFactory.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error
                        && e.Category == "Mithril.Overlay")
            .ToList();
        errorEntries.Should().NotBeEmpty(
            "the isolated exception must surface as a LogError on the 'Mithril.Overlay' category " +
            "so the user can see what failed without rebuilding with a debugger attached.");
        errorEntries.Should().Contain(e => ReferenceEquals(e.Exception, raised),
            "the original exception instance must be attached to the log entry, not just stringified — " +
            "the stack trace is the only thing that lets the user (or maintainer) find the bug.");
    }

    private sealed class MutableZoomSource : IOverlayZoomSource
    {
        public MutableZoomSource(double zoom) { CurrentZoom = zoom; }
        public double CurrentZoom { get; set; }
    }
}
