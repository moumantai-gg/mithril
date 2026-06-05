using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #835 step 6 review iteration-1 B2 second test: the silent-fallback
/// LogTrace dedup in <see cref="MapOverlayViewModel.RefreshCalibrationMarker"/>
/// fires exactly once per (area, reason) tuple, not per-marker. A busy
/// calibration walkthrough would otherwise flood the trace.
/// </summary>
public class MapOverlayCalibrationFallbackDedupTests
{
    [Fact]
    public void Repeated_calibration_fallbacks_same_area_log_trace_only_once()
    {
        var session = new SessionState();
        session.CurrentMapZoom = 1.0;
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var registry = new WorldOverlayMarkers(NullLogger.Instance) { CurrentArea = "AreaTest" };
        var areaState = new FakeAreaState { CurrentArea = "AreaTest" };
        var loggerFactory = new TestLoggerFactory();

        // Build a coordinator with the area uncalibrated so the
        // dedup-triggering path inside MapOverlayViewModel
        // (areaCalibration: null below) fires "No IAreaCalibrationService injected"
        // every Refresh call. Coordinator itself doesn't need a calibration
        // service for this test — we won't call Arm on it.
        var pinCoord = new PinCalibrationCoordinator(
            new StubAreaCalibrationService(),
            new FakeMapPinState(),
            new TestDomainEventBus(),
            settings);
        // Force the coordinator into the armed Pair phase directly via the
        // source-generated setters, so MapOverlayViewModel's
        // RefreshCalibrationMarker passes its `_pinCal?.IsPairing != true`
        // gate and proceeds to the next early-return (the test's hot path).
        pinCoord.IsArmed = true;
        pinCoord.Phase = CalibrationPhase.Pair;

        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: pinCoord,
            positionState: null,
            bus: null,
            areaCalibration: null, // ← drives the "No IAreaCalibrationService injected" early-return
            motherlode: null,
            characterPin: null,
            markers: registry,
            areaState: areaState,
            loggerFactory: loggerFactory);

        // Add five markers — every Refresh hits the same fallback. The
        // dedup must collapse them to exactly one Trace entry per
        // (area, reason). Five markers proves the dedup works across
        // multiple Refresh calls inside one frame.
        for (var i = 0; i < 5; i++)
            pinCoord.PlacedMarkers.Add(new CalibrationMarker(new OverlayPixel(10 + i, 20), i));

        var traceEntries = loggerFactory.Entries
            .Where(e => e.Level == LogLevel.Trace
                        && e.Category == "Legolas.MapOverlay"
                        && e.Message.Contains("RefreshCalibrationMarker fallback"))
            .ToList();

        traceEntries.Should().HaveCount(1,
            "the per-(area, reason) dedup must fire LogTrace exactly once " +
            "regardless of how many calibration markers walk the same fallback path " +
            "(review iter-1 B2 — a per-marker trace would flood diagnostics on a " +
            "busy walkthrough).");
        traceEntries[0].Message.Should().Contain("AreaTest")
            .And.Contain("No IAreaCalibrationService injected");
    }

    private sealed class StubAreaCalibrationService : IAreaCalibrationService
    {
        public MapSceneRef? CurrentScene => new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public string? CurrentAreaFriendlyName => "Test";
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
        public WorldToOverlayCalibration? CurrentOverlayCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements, double calibrationZoom = 1.0) => null;
        public event EventHandler? Changed { add { } remove { } }
        public void SelectScene(MapSceneRef scene) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class TestLogger : ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> _sink;
            public TestLogger(string c, System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> s) { _category = c; _sink = s; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => _sink.Enqueue(new TestLogEntry(_category, logLevel, formatter(state, exception), exception));
            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }

    private sealed record TestLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
}
