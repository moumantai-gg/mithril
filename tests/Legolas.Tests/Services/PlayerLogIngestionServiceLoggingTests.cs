using System.Collections.Concurrent;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// #1093 Task 5 / D10. The pre-fix ctor signature took a non-generic
/// <see cref="ILogger"/>? which DI never registered, so the optional defaulted
/// to null in production and the "Subscribed to Arda domain events" lifecycle
/// log was silently dead. Swapping the parameter to <see cref="ILoggerFactory"/>?
/// + a category-named <c>CreateLogger("Legolas.Ingestion")</c> lights the line
/// up because <see cref="ILoggerFactory"/> IS in DI. This test would have
/// failed against the pre-D10 ctor.
/// </summary>
public sealed class PlayerLogIngestionServiceLoggingTests : IDisposable
{
    [Fact]
    public async Task StartAsync_logs_subscribed_lifecycle_via_factory()
    {
        var bus = new TestDomainEventBus();
        var spy = new StubAreaCalibration();
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(session), bus);
        var loggers = new CapturingLoggerFactory();

        var svc = new PlayerLogIngestionService(
            bus, spy, flow, session, motherlode, settings, loggers);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        try
        {
            loggers.Entries.Should().Contain(e =>
                e.Category == "Legolas.Ingestion"
                && e.Level == LogLevel.Information
                && e.Message == "Subscribed to Arda domain events",
                "the D10 ILoggerFactory swap lights up the previously-dead lifecycle line; " +
                "before the fix the optional ILogger? defaulted to null in production.");
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            svc.Dispose();
        }
    }

    public void Dispose() { }

    private sealed class StubAreaCalibration : IAreaCalibrationService
    {
        public void SelectScene(MapSceneRef scene) { }
        public AreaCalibration? CurrentCalibration => null;
        public WorldToOverlayCalibration? CurrentOverlayCalibration => null;
        public bool IsCurrentAreaCalibrated => false;
        public MapSceneRef? CurrentScene => null;
        public string? CurrentAreaFriendlyName => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences =>
            Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
            double calibrationZoom = 1.0) => null;
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    private sealed record TestLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<TestLogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<TestLogEntry> _sink;
            public CapturingLogger(string c, ConcurrentQueue<TestLogEntry> s) { _category = c; _sink = s; }
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
}
