using System.Windows.Media;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Shared.Reference;
using Vortice.Direct2D1;

namespace Legolas.Tests.Rendering;

/// <summary>
/// #872 BLOCKER / #887: the calibration placement-pin pass
/// (<see cref="LegolasOverlaySceneDrawer"/>.<c>DrawCalibrationPlacementPins</c>)
/// is the dissolved-#868 headline behavior — it draws the in-flight Drop/Pair
/// markers pixel-native at the raw click pixel (no <c>Project()</c> call),
/// gated only on <see cref="MapOverlayViewModel.IsCalibrationCapturing"/>.
///
/// <para>The Drop/Pair walkthrough runs entirely in an UNCALIBRATED area
/// (calibration only persists at Confirm), so these pins must render with
/// <c>IsCalibrated == false</c>. The companion
/// <c>OverlaySceneHookTests.Scene_drawers_fire_on_uncalibrated_area_so_pixel_native_passes_render</c>
/// proves the <c>OverlayWindowService</c> dispatch reaches scene drawers
/// uncalibrated; this test proves the placement-pin pass itself draws when
/// capturing and short-circuits when not — closing the coverage gap #887
/// names (the test seam <c>DriveSceneForTest</c> carried the same gate as
/// production, so no test exercised this path before the fix).</para>
///
/// <para>The draw body issues <c>ID2D1RenderTarget</c> shape calls that can't
/// be observed without a real D2D device, but the pass fetches its fill/stroke
/// brushes (<see cref="IOverlayBrushes.Get"/>) inside <c>DrawPinLayer</c> only
/// after passing the capturing + non-empty guards. So a non-zero brush-fetch
/// count is a faithful proxy for "the pass drew", and the fake context returns
/// null brushes so the method bails before touching the (throwing) render
/// target. Mirrors <see cref="LegolasOverlaySceneDrawerGhostTests"/>.</para>
/// </summary>
public sealed class LegolasOverlaySceneDrawerPlacementPinTests
{
    [Fact]
    public void Placement_pins_draw_when_capturing_even_with_no_calibration()
    {
        var (drawer, _) = BuildDrawerCapturingWithMarker();

        var ctx = new CountingSceneContext();
        drawer.DrawCalibrationPlacementPinsForTest(ctx);

        ctx.BrushGetCalls.Should().BeGreaterThan(0,
            "with the Drop/Pair walkthrough live (IsCalibrationCapturing == true) and a placed " +
            "marker, the placement-pin pass must enter DrawPinLayer and fetch its center brush. " +
            "This pass draws pixel-native at the raw click pixel and never calls Project(), so it " +
            "is correct (and required) in an uncalibrated area — a zero fetch count is the #872 " +
            "blocker where placement pins never render during calibration.");
    }

    [Fact]
    public void Placement_pins_skipped_when_not_capturing()
    {
        // Coordinator present but not armed → IsCalibrationCapturing == false.
        var (drawer, _) = BuildDrawer(arm: false);

        var ctx = new CountingSceneContext();
        drawer.DrawCalibrationPlacementPinsForTest(ctx);

        ctx.BrushGetCalls.Should().Be(0,
            "outside the Drop/Pair walkthrough the placement-pin pass must short-circuit before " +
            "any brush fetch — drawing placement pins when the user isn't calibrating would " +
            "litter the overlay.");
    }

    private static (LegolasOverlaySceneDrawer drawer, MapOverlayViewModel vm) BuildDrawerCapturingWithMarker()
    {
        var (drawer, vm) = BuildDrawer(arm: true);
        // Pairing phase is live (≥3 seeded pins → Arm lands in Pair). Pair a
        // click so a CalibrationMarker exists in PlacedMarkers (which the VM
        // surfaces as CalibrationMarkers).
        vm.PairCalibrationClick(new PixelPoint(120, 240));
        vm.IsCalibrationCapturing.Should().BeTrue("Arm with ≥3 pins enters the Pair phase");
        vm.CalibrationMarkers.Should().NotBeNull();
        vm.CalibrationMarkers!.Count.Should().BeGreaterThan(0, "the paired click placed a marker");
        return (drawer, vm);
    }

    private static (LegolasOverlaySceneDrawer drawer, MapOverlayViewModel vm) BuildDrawer(bool arm)
    {
        var session = new SessionState { CurrentMapZoom = 1.0 };
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaState = new FakeAreaState { CurrentArea = "AreaUncalibrated" };

        var calib = new FakeCalib();
        var pins = new FakeMapPinState();
        var bus = new TestDomainEventBus();
        var coord = new PinCalibrationCoordinator(calib, pins, bus, settings, session);
        if (arm)
        {
            // ≥3 usable pins → Arm enters the Pair phase (IsPairing == true).
            pins.SeedExisting(
                FakeMapPinState.Pin(10, 10), FakeMapPinState.Pin(40, 60), FakeMapPinState.Pin(90, 20));
            coord.Arm();
        }

        var vm = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: coord, positionState: null, bus: null,
            areaCalibration: null, motherlode: null, characterPin: null,
            markers: null, areaState: areaState);
        return (new LegolasOverlaySceneDrawer(vm), vm);
    }

    /// <summary>Minimal <see cref="IAreaCalibrationService"/> so the
    /// <see cref="PinCalibrationCoordinator"/> ctor is satisfied. Arm + a
    /// single PairClick never reach the persisting calibrate path, so the
    /// members here are stubs. Mirrors the nested fake in
    /// <c>NudgePadViewModelTests</c>.</summary>
    private sealed class FakeCalib : IAreaCalibrationService
    {
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
            => new(1, 0, 0, 0, placements.Count, 0);
        public MapSceneRef? CurrentScene => new MapSceneRef("AreaUncalibrated", null, "Map_AreaUncalibrated");
        public string? CurrentAreaFriendlyName => "Test";
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => System.Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => System.Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public void SelectScene(MapSceneRef scene) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    /// <summary>Fake scene context whose brush surface returns null and counts
    /// how many times a brush was requested. Unlike the ghost pass (which
    /// guards on the brush before touching the target), the placement-pin pass
    /// hands <see cref="RenderTarget"/> / <see cref="Factory"/> straight into
    /// <c>DrawPinLayer</c> as arguments, so they're evaluated eagerly — they
    /// return <c>null</c> here. That's safe: with null brushes, DrawPinLayer
    /// never issues a draw call against the target, so the null target is
    /// never dereferenced; the non-zero brush-fetch count is the proxy for
    /// "the draw path was entered".</summary>
    private sealed class CountingSceneContext : IOverlaySceneContext, IOverlayBrushes
    {
        public int BrushGetCalls { get; private set; }

        public ID2D1SolidColorBrush? Get(Color color)
        {
            BrushGetCalls++;
            return null; // null brush ⇒ DrawPinLayer skips every RenderTarget call
        }

        public IOverlayBrushes Brushes => this;

        public ID2D1RenderTarget RenderTarget => null!;
        public ID2D1Factory Factory => null!;
        public string CurrentAreaKey => "AreaUncalibrated";
        public MapSceneRef CurrentScene => new MapSceneRef("AreaUncalibrated", null, "Map_AreaUncalibrated");
        public PixelPoint? Project(double worldX, double worldZ) => new PixelPoint(worldX, worldZ);
    }
}
