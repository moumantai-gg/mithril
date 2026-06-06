// #1076 Phase 5a: P.3 audit found all Legolas test PixelPoint sites are
// overlay-frame, so this Rendering test file was migrated alongside the 5a
// scope (its PinScene/drawer dependencies now take OverlayPixel).
using System.Collections.Concurrent;
using System.Windows.Media;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging;
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
        vm.CalibrationGhosts.Add(new GhostMarker("Landmark", new OverlayPixel(100, 200), ShowLabel: true));
        vm.CalibrationGhosts.Add(new GhostMarker("NPC", new OverlayPixel(140, 260), ShowLabel: false));
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
        vm.CalibrationGhosts.Add(new GhostMarker("Landmark", new OverlayPixel(100, 200), ShowLabel: true));
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

/// <summary>
/// #1093 Task 4: the ghost-pass draws at ~60 fps with three short-circuit
/// branches (hidden / empty / brush_null). Per-frame logging would flood the
/// diagnostics ring buffer, so the drawer classifies each frame into one of
/// four buckets and emits a Trace log + meter increment ONLY when the bucket
/// changes between frames.
///
/// <para>This test drives the four-frame walk-through from the plan
/// (hidden → shown-but-empty → shown-with-2-ghosts → shown-with-2-ghosts-again)
/// and asserts exactly two Trace records fire: hidden→empty AND empty→drawing.
/// The fourth call is a no-change frame and must not emit anything; that's
/// the contract that keeps the log volume bounded under steady-state UI.</para>
///
/// <para>Reaching the <c>drawing</c> bucket requires a non-null brush, so this
/// test sets up a real <see cref="HeadlessD2DRenderTarget"/> + <see cref="D2DBrushCache"/>
/// — same pattern as the snapshot tests — and gates on
/// <c>HeadlessD2DRenderTarget.TryCreate</c> via <see cref="Xunit.SkippableFact"/>
/// so CI runs without a D3D11 driver are skipped, not failed.</para>
/// </summary>
public sealed class LegolasOverlaySceneDrawerGhostTransitionsTests
{
    private const string SkipNoD3DPrefix = "No usable D3D11 driver: ";

    [SkippableFact]
    public void Ghost_pass_logs_one_trace_per_bucket_transition()
    {
        using var rt = HeadlessD2DRenderTarget.TryCreate(64, 64, out var driverError);
        Skip.If(rt is null, SkipNoD3DPrefix + (driverError?.Message ?? "(unknown)"));

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt!.RenderTarget);
        rt.RenderTarget.BeginDraw();

        var (drawer, vm, sink) = BuildDrawer();
        var ctx = new BoundSceneContext(rt.RenderTarget, rt.Factory, brushes);

        // Frame 1: hidden (toggle off, no ghosts) → bucket 0. First observation
        // (prev = -1) seeds state; spec says NO transition log.
        vm.ShowCalibrationGhosts = false;
        drawer.DrawCalibrationGhostsForTest(ctx);

        // Frame 2: shown-but-empty → bucket 1. Transition 0→1 logs Trace.
        vm.ShowCalibrationGhosts = true;
        drawer.DrawCalibrationGhostsForTest(ctx);

        // Frame 3: shown with 2 ghosts → bucket 2. Transition 1→2 logs Trace.
        vm.CalibrationGhosts.Add(new GhostMarker("Landmark", new OverlayPixel(10, 12), ShowLabel: false));
        vm.CalibrationGhosts.Add(new GhostMarker("NPC", new OverlayPixel(20, 22), ShowLabel: false));
        drawer.DrawCalibrationGhostsForTest(ctx);

        // Frame 4: same → bucket 2. No transition, no log.
        drawer.DrawCalibrationGhostsForTest(ctx);

        rt.RenderTarget.EndDraw();

        var traceRecords = sink.Records
            .Where(r => r.Level == LogLevel.Trace
                        && r.Message.StartsWith("DrawCalibrationGhosts:"))
            .ToList();

        traceRecords.Should().HaveCount(2,
            "the four-frame walk-through fires exactly two bucket transitions " +
            "(hidden→empty, empty→drawing); the first frame seeds state without " +
            "logging, and the fourth frame is a no-change re-entry.");
        traceRecords[0].Message.Should().Contain("shown but empty",
            "hidden(0)→empty(1) takes the (0,1) branch in the switch.");
        traceRecords[1].Message.Should().Contain("drawing 2 ghost(s)",
            "empty(1)→drawing(2) takes the (1,2) branch and carries the live ghost count.");

        sink.Records
            .Where(r => r.Level == LogLevel.Warning)
            .Should().BeEmpty("brush_null bucket is never hit on this path.");
    }

    private static (LegolasOverlaySceneDrawer drawer, MapOverlayViewModel vm, RecordingLoggerSink sink) BuildDrawer()
    {
        var session = new SessionState { CurrentMapZoom = 1.0 };
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
        var sink = new RecordingLoggerSink();
        var logger = new RecordingLogger("Legolas.Overlay.GhostDrawer", sink);
        return (new LegolasOverlaySceneDrawer(vm, logger), vm, sink);
    }

    /// <summary>Scene context wired to a real <see cref="HeadlessD2DRenderTarget"/>
    /// so the drawer's <c>drawing</c> branch can actually execute against a
    /// live D2D surface without throwing.</summary>
    private sealed class BoundSceneContext : IOverlaySceneContext
    {
        public BoundSceneContext(ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
        {
            RenderTarget = rt;
            Factory = factory;
            Brushes = brushes;
        }

        public ID2D1RenderTarget RenderTarget { get; }
        public ID2D1Factory Factory { get; }
        public IOverlayBrushes Brushes { get; }
        public string CurrentAreaKey => "AreaTest";
        public MapSceneRef CurrentScene => new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public OverlayPixel? Project(double worldX, double worldZ) => new OverlayPixel(worldX, worldZ);
    }

    private sealed record RecordedLog(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerSink
    {
        public ConcurrentQueue<RecordedLog> Records { get; } = new();
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly RecordingLoggerSink _sink;
        public RecordingLogger(string category, RecordingLoggerSink sink)
        {
            _category = category;
            _sink = sink;
        }
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, System.Exception? exception,
            Func<TState, System.Exception?, string> formatter)
            => _sink.Records.Enqueue(new RecordedLog(_category, logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
