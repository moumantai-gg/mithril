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
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        IMapTextureDimensions? dims = null)   // mithril#1081 Task 11: optional dims
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
            textureDimensions: dims ?? new NullMapTextureDimensions(),  // mithril#1081
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

    /// <summary>
    /// mithril#1081 Task 11 — replaces <c>Project_plumbs_current_zoom_into_WorldToOverlay</c>.
    /// Same invariant (live zoom flows into projection); new seam. Post-#1081 the
    /// calibration service is no longer on the per-marker render path — the seam
    /// moved to <see cref="OverlaySceneContext._composedCal"/>'s <c>ToOverlay</c>
    /// call. Verify the invariant by checking the projected <see cref="OverlayPixel"/>
    /// changes as zoom changes (Scale=10, CalibrationZoom=1 → output scales with
    /// per-tick zoom ratio).
    /// </summary>
    [Fact]
    public void Project_plumbs_current_zoom_into_bound_composed_cal()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("Map_A");
        // Scale=10, CalibrationZoom=1.0 → ToOverlay output is 10 * (currentZoom/1.0) * world.
        // Different zooms produce observably different pixels.
        calibration.OverlayCalForScene = _ =>
            new WorldToOverlayCalibration(
                OriginX: 0, OriginY: 0, Scale: 10.0,
                RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);

        var areaState = new StubAreaState { CurrentArea = "Map_A" };
        var zoom = new MutableZoomSource(1.5);
        var service = BuildService(calibration, areaState, zoom);

        var projectedPoints = new List<OverlayPixel?>();
        using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            projectedPoints.Add(ctx.Project(10, 20));
        });

        service.DriveSceneForTest(null!, null!, "Map_A", 1.5);
        var firstAtZoom1_5 = projectedPoints[^1];

        zoom.CurrentZoom = 0.75;
        service.DriveSceneForTest(null!, null!, "Map_A", 0.75);
        var secondAtZoom0_75 = projectedPoints[^1];

        firstAtZoom1_5.Should().NotBe(secondAtZoom0_75,
            because: "Project must pass the per-tick live zoom into the bound " +
            "WorldToOverlayCalibration.ToOverlay call. If this regresses to a hardcoded " +
            "zoom (or the bound cal's CalibrationZoom only), pins drift whenever the " +
            "in-game zoom slider is off the calibration zoom. mithril#1081 moved the " +
            "seam from IMapCalibrationService.WorldToOverlay to OverlaySceneContext's " +
            "bound _composedCal.ToOverlay call, but the live-zoom invariant from PR #863 remains.");
    }

    [Fact]
    public void Project_returns_null_for_uncalibrated_areas()
    {
        // This is a defensive-cover test. Scene drawers now run even in
        // uncalibrated areas (only the marker projection is gated), so a
        // world-projecting drawer can call Project() with no calibration —
        // it must return null instead of fabricating a pixel rather than
        // relying on a loop-level gate to keep it from ever being reached.
        //
        // mithril#1081 Task 11: Project() uses _composedCal?.ToOverlay(…) on
        // the bound composed cal. To get null out of Project() we need a null
        // composed cal — i.e. GetOverlayCalibration returns null AND
        // GetTextureCalibration returns null (no texture-frame record either).
        // Use OverlayCalForScene = _ => null to force the null-cal path while
        // still having IsCalibrated = true so DriveSceneForTest runs drawers.
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A"); // IsCalibrated = true so drawers fire
        calibration.OverlayCalForScene = _ => null; // no overlay-frame cal
        // TextureCalForScene is null by default (no hook) → no texture-frame cal
        // → ResolveComposedOverlayCalibration returns (null, None)
        var areaState = new StubAreaState { CurrentArea = "A" };
        var service = BuildService(calibration, areaState, new FixedOverlayZoomSource(1.0));

        IOverlaySceneContext? ctx = null;
        using var h = ((IOverlayWindow)service).RegisterScene(c => ctx = c);

        service.DriveSceneForTest(null!, null!, "A", 1.0);

        ctx.Should().NotBeNull();
        var px = ctx!.Project(10, 20);
        px.Should().BeNull(
            "Project must return null when the composed cal is null (no usable calibration " +
            "this frame) — a fabricated pixel would silently land the marker at (0,0) or " +
            "similar nonsense. mithril#1081: the seam moved from WorldToOverlay to " +
            "OverlaySceneContext._composedCal?.ToOverlay; the null path is driven by " +
            "OverlayCalForScene = _ => null with no texture-frame record.");
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

    /// <summary>
    /// mithril#1081 Task 11 — texture-frame composition integration fact.
    /// <para>Skipped because <see cref="OverlayWindowService.DriveSceneForTest"/>
    /// doesn't realize the overlay surface — <c>_window</c> is null in tests, so
    /// <c>ResolveOverlaySurfaceSize</c> returns (0,0), and F2 in
    /// <c>ResolveComposedOverlayCalibration</c> short-circuits to
    /// <c>CalPath.None</c> before the texture-frame path can be exercised.
    /// The decision-table coverage for the composition path lives in
    /// <see cref="ResolveComposedOverlayCalibrationTests"/> (Task 8), which
    /// exercises the logic directly without a live surface. That is the
    /// substantive net; this test would be a duplicate of its integration seam
    /// rather than an independent check.</para>
    /// </summary>
    [Fact(Skip =
        "DriveSceneForTest doesn't realize the overlay surface; composed-from-texture " +
        "path is exercised through ResolveComposedOverlayCalibrationTests (Task 8) " +
        "which covers the decision table without a live surface.")]
    public void Project_composes_texture_frame_when_only_AutoCal_record_exists()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("Map_A");
        calibration.OverlayCalForScene = _ => null; // no overlay-frame record
        calibration.TextureCalForScene = _ =>
            new WorldToTextureCalibration(
                OriginX: 0, OriginY: 0, Scale: 1.0,
                RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0)
            {
                PixelSha256 = "test-sha",
            };

        var stubDims = new StubMapTextureDimensions((1000, 1000));
        var areaState = new StubAreaState { CurrentArea = "Map_A" };
        var service = BuildService(calibration, areaState,
            new FixedOverlayZoomSource(1.0), dims: stubDims);

        var projected = new List<OverlayPixel?>();
        using var h = ((IOverlayWindow)service).RegisterScene(ctx =>
        {
            projected.Add(ctx.Project(100, 200));
        });

        service.DriveSceneForTest(null!, null!, "Map_A", 1.0);

        projected.Single().Should().NotBeNull(
            because: "Project must compose the texture-frame record onto the overlay " +
            "surface via WorldToTextureCalibration.ProjectThroughOverlay with dims " +
            "from IMapTextureDimensions. A null return here means #1081's composition " +
            "path is broken or the overlay-surface size lookup returned 0.");
    }

    private sealed class StubMapTextureDimensions((int W, int H)? result) : IMapTextureDimensions
    {
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => result;
    }

    private sealed class MutableZoomSource : IOverlayZoomSource
    {
        public MutableZoomSource(double zoom) { CurrentZoom = zoom; }
        public double CurrentZoom { get; set; }
    }
}
