using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #1093 Task 3 (D9 shape test). The <see cref="MapOverlayViewModel"/>
/// projection paths emit a stable set of log entries on the toggle / rebuild
/// paths so a triager can grep one canonical line per state change. Two
/// assertions:
/// <list type="number">
///   <item>D7 anchor — <c>SetCalibrationValidation(true)</c> with a non-null
///   <see cref="WorldToOverlayCalibration"/> emits exactly one Information
///   record matching the toggle template, with the property bag populated
///   and <c>CalibrationGhosts.Count &gt; 0</c>.</item>
///   <item>Success-path <c>RebuildCalibrationGhosts</c> Information record
///   carries the ghosts / refs counts plus the Source / residual triplet
///   (sourced from <see cref="AreaCalibration"/> when
///   <see cref="WorldToOverlayCalibration"/> doesn't carry them).</item>
/// </list>
/// The skip-path Trace shape (and the new 3-arg <c>LogCalibrationFallback</c>
/// signature) is covered by the extended
/// <see cref="MapOverlayCalibrationFallbackDedupTests"/>.
/// </summary>
public class MapOverlayCalibrationLoggingTests
{
    private static AreaCalibration Cal(double scale) =>
        new(scale, 0.0, 100, 200, 3, 1.5);

    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    private static (MapOverlayViewModel map, FakeAreaCalibrationService cal,
                    SessionState session, CapturingLoggerFactory loggers) Build()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        var loggers = new CapturingLoggerFactory();
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null,
            areaCalibration: cal, loggerFactory: loggers);
        return (map, cal, session, loggers);
    }

    [Fact]
    public void Toggle_on_with_overlay_calibration_emits_D7_anchor_information_log()
    {
        var (map, cal, _, loggers) = Build();
        cal.SetReferences(Ref("Statue", 10, 5), Ref("Well", -4, 12), Ref("Tower", 2, -3));
        cal.SetCalibration(Cal(2.0));
        loggers.Entries.Clear(); // drop the Changed-event chatter the setup emits

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.CalibrationGhosts.Count.Should().BeGreaterThan(0);

        var toggleLogs = loggers.Entries
            .Where(e => e.Category == "Legolas.MapOverlay"
                        && e.Level == LogLevel.Information
                        && e.Message.StartsWith("SetCalibrationValidation"))
            .ToList();

        toggleLogs.Should().HaveCount(1,
            "D7 — the toggle is the lifecycle anchor; exactly one Information " +
            "record per click fires from SetCalibrationValidation.");
        toggleLogs[0].Message.Should().Contain("on=True")
            .And.Contain("Map_AreaTest")
            .And.Contain("isCalibrated=True")
            .And.Contain("overlayCalUsable=True")
            .And.Contain("shown_and_rebuilt")
            .And.Contain("ghostsBuilt=3");
    }

    [Fact]
    public void RebuildCalibrationGhosts_success_emits_information_with_source_and_residual()
    {
        var (map, cal, _, loggers) = Build();
        cal.SetReferences(Ref("Statue", 10, 5), Ref("Well", -4, 12), Ref("Tower", 2, -3));
        cal.SetCalibration(Cal(2.0));
        loggers.Entries.Clear();

        map.ToggleCalibrationValidationCommand.Execute(null);

        var rebuildLogs = loggers.Entries
            .Where(e => e.Category == "Legolas.MapOverlay"
                        && e.Level == LogLevel.Information
                        && e.Message.StartsWith("RebuildCalibrationGhosts"))
            .ToList();

        rebuildLogs.Should().HaveCount(1,
            "the success path emits one Information record per call — driven by " +
            "the user toggling validation on (a state-change event).");
        rebuildLogs[0].Message.Should().Contain("Map_AreaTest")
            .And.Contain("built 3")
            .And.Contain("from 3 refs")
            // CalibrationSource defaults to UserRefinement on AreaCalibration
            // records constructed in the fake (the residual matches Cal(...).ResidualPixels = 1.5).
            .And.Contain("UserRefinement")
            .And.Contain("residual=1.50px");
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> _sink;
            public CapturingLogger(string c, System.Collections.Concurrent.ConcurrentQueue<TestLogEntry> s) { _category = c; _sink = s; }
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
