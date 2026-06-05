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
using Vortice.Direct2D1;
using Xunit;

namespace Legolas.Tests.Rendering;

/// <summary>
/// #835 step 6 review iteration-1 B3: the #495 calibration-validation ghost
/// markers were ported from the deleted <c>MapOverlayView.xaml</c>
/// <c>ItemsControl</c> into <see cref="LegolasOverlaySceneDrawer"/>'s D2D
/// pass (dots-only this iteration; labels tracked in #875).
///
/// <para>The draw body issues <c>ID2D1RenderTarget.DrawEllipse</c> /
/// <c>FillEllipse</c> calls, which can't be observed without a real D2D
/// device. But the method fetches its brush (<see cref="IOverlayBrushes.Get"/>)
/// exactly once before the per-ghost loop, and ONLY after passing the
/// <see cref="MapOverlayViewModel.ShowCalibrationGhosts"/> + non-empty
/// guards. So the brush-fetch count is a faithful proxy for "did the draw
/// path run": ≥1 ⇒ the pass drew (non-zero pixels); 0 ⇒ short-circuited
/// (zero pixels). The fake context returns a null brush so the method
/// short-circuits before ever touching the (throwing) render target.</para>
/// </summary>
public sealed class LegolasOverlaySceneDrawerGhostTests
{
    [Fact]
    public void Ghost_pass_draws_when_populated_and_visible()
    {
        var (drawer, vm) = BuildDrawer();
        vm.CalibrationGhosts.Add(new GhostMarker("Landmark", new PixelPoint(100, 200), ShowLabel: true));
        vm.CalibrationGhosts.Add(new GhostMarker("NPC", new PixelPoint(140, 260), ShowLabel: false));
        vm.ShowCalibrationGhosts = true;

        var ctx = new CountingSceneContext();
        drawer.DrawCalibrationGhostsForTest(ctx);

        ctx.BrushGetCalls.Should().BeGreaterThan(0,
            "with ghosts populated and validation visible, the ghost pass must enter the draw " +
            "path and fetch its magenta brush — a zero fetch count means the #495 dots never " +
            "render and calibration alignment validation is silently broken.");
    }

    [Fact]
    public void Ghost_pass_is_skipped_when_validation_off()
    {
        var (drawer, vm) = BuildDrawer();
        vm.CalibrationGhosts.Add(new GhostMarker("Landmark", new PixelPoint(100, 200), ShowLabel: true));
        vm.ShowCalibrationGhosts = false; // user hasn't toggled validation

        var ctx = new CountingSceneContext();
        drawer.DrawCalibrationGhostsForTest(ctx);

        ctx.BrushGetCalls.Should().Be(0,
            "validation off must short-circuit before any draw work — drawing ghosts when the " +
            "user hasn't asked for them would litter the overlay with magenta dots.");
    }

    [Fact]
    public void Ghost_pass_is_skipped_when_no_ghosts()
    {
        var (drawer, vm) = BuildDrawer();
        vm.ShowCalibrationGhosts = true; // visible, but nothing projected

        var ctx = new CountingSceneContext();
        drawer.DrawCalibrationGhostsForTest(ctx);

        ctx.BrushGetCalls.Should().Be(0,
            "an empty ghost collection must short-circuit — no brush fetch, no draw calls.");
    }

    private static (LegolasOverlaySceneDrawer drawer, MapOverlayViewModel vm) BuildDrawer()
    {
        var session = new SessionState();
        session.CurrentMapZoom = 1.0;
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaState = new FakeAreaState { CurrentArea = "AreaTest" };
        var vm = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: null, positionState: null, bus: null,
            areaCalibration: null, motherlode: null, characterPin: null,
            markers: null, areaState: areaState);
        return (new LegolasOverlaySceneDrawer(vm), vm);
    }

    /// <summary>Fake scene context whose brush surface returns null (so the
    /// ghost pass short-circuits before the render target) and counts how
    /// many times a brush was requested. <see cref="RenderTarget"/> /
    /// <see cref="Factory"/> throw so any accidental draw attempt past the
    /// null-brush guard fails loudly instead of NRE-ing on a null target.</summary>
    private sealed class CountingSceneContext : IOverlaySceneContext, IOverlayBrushes
    {
        public int BrushGetCalls { get; private set; }

        public ID2D1SolidColorBrush? Get(Color color)
        {
            BrushGetCalls++;
            return null; // forces DrawCalibrationGhosts to bail before RenderTarget use
        }

        public IOverlayBrushes Brushes => this;

        public ID2D1RenderTarget RenderTarget => throw new System.InvalidOperationException(
            "RenderTarget must not be touched — the null brush short-circuits the ghost pass first.");
        public ID2D1Factory Factory => throw new System.InvalidOperationException(
            "Factory must not be touched in the ghost-pass test.");
        public string CurrentAreaKey => "AreaTest";
        public MapSceneRef CurrentScene => new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public OverlayPixel? Project(double worldX, double worldZ) => new OverlayPixel(worldX, worldZ);
    }
}
