using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Diagnostics;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1093 Task 1 — picker telemetry shape tests for
/// <see cref="MapCalibrationService"/>'s frame-typed picker (<c>PickByFrame</c>,
/// reached via <c>GetTextureCalibration</c> / <c>GetOverlayCalibration</c>).
///
/// <para>Sister to <see cref="MapCalibrationServicePickerTests"/> which exercises
/// the legacy <see cref="MapCalibrationService.GetCalibration"/> picker. The
/// frame-typed picker was silent before #1093; these tests assert the three log
/// shapes (hit-Trace, below-floor-Information, miss-Trace) plus the
/// <c>PickerOutcomes</c> meter ticks once per call with the right
/// <c>outcome</c>/<c>frame</c> tags.</para>
///
/// <para>The picker is consumed via the public <see cref="MapCalibrationService.GetTextureCalibration"/>
/// (Texture frame) and <see cref="MapCalibrationService.GetOverlayCalibration"/>
/// (Overlay frame) — those routes give us a behavioural-surface trigger for the
/// private <c>PickByFrame</c>.</para>
/// </summary>
[Collection("MapCalibrationTelemetry")]
public sealed class MapCalibrationServicePickerTelemetryTests
{
    private const string Key = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Key);

    private static AreaCalibration Cal(double residual, int refs, CalibrationSource source, CalibrationFrame frame) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 0, OriginY: 0,
            ReferenceCount: refs, ResidualPixels: residual)
        {
            Source = source,
            Frame = frame,
        };

    [Fact]
    public void PickByFrame_HitPath_LogsTraceAndTicksHitMeter()
    {
        var logger = new CapturingLogger();
        var svc = new MapCalibrationService(
            baseline: new Dictionary<string, AreaCalibration>
            {
                [Key] = Cal(2.1, 6, CalibrationSource.BundledBaseline, CalibrationFrame.Texture),
            },
            userStore: UserRefinementStore.ForTests(new Dictionary<string, AreaCalibration>
            {
                [Key] = Cal(0.6, 5, CalibrationSource.AutoCapture, CalibrationFrame.Texture),
            }),
            logger: logger);

        using var capture = PickerMeterCapture.Start();

        var picked = svc.GetTextureCalibration(Scene);

        picked.Should().NotBeNull();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Trace
            && e.Message.Contains("PickByFrame(")
            && e.Message.Contains("frame=Texture")
            && e.Message.Contains("picked source=AutoCapture"));
        capture.Measurements.Should().ContainSingle(m =>
            m.Frame == "texture" && m.Outcome == "hit" && m.Value == 1);
    }

    [Fact]
    public void PickByFrame_BelowFloor_LogsInformationAndTicksFallbackMeter()
    {
        var logger = new CapturingLogger();
        // Both candidates have ReferenceCount < MinReferences (4).
        var svc = new MapCalibrationService(
            baseline: new Dictionary<string, AreaCalibration>
            {
                [Key] = Cal(0.5, 3, CalibrationSource.BundledBaseline, CalibrationFrame.Texture),
            },
            userStore: UserRefinementStore.ForTests(new Dictionary<string, AreaCalibration>
            {
                // UserRefinement defaults to Overlay frame; we explicitly stamp Texture
                // so it lands on the same frame as the baseline (both candidates).
                [Key] = Cal(0.3, 2, CalibrationSource.UserRefinement, CalibrationFrame.Texture),
            }),
            logger: logger);

        using var capture = PickerMeterCapture.Start();

        var picked = svc.GetTextureCalibration(Scene);

        picked.Should().NotBeNull();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("PickByFrame(")
            && e.Message.Contains("frame=Texture")
            && e.Message.Contains("no candidate cleared MinReferences=")
            && e.Message.Contains("best-source-precedence fallback"));
        capture.Measurements.Should().ContainSingle(m =>
            m.Frame == "texture" && m.Outcome == "fallback_below_floor" && m.Value == 1);
    }

    [Fact]
    public void PickByFrame_NoCandidates_LogsTraceAndTicksMissMeter()
    {
        var logger = new CapturingLogger();
        // Empty stores → no candidates for either frame.
        var svc = new MapCalibrationService(
            baseline: new Dictionary<string, AreaCalibration>(),
            userStore: UserRefinementStore.ForTests(),
            logger: logger);

        using var capture = PickerMeterCapture.Start();

        var picked = svc.GetOverlayCalibration(Scene);

        picked.Should().BeNull();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Trace
            && e.Message.Contains("PickByFrame(")
            && e.Message.Contains("frame=Overlay")
            && e.Message.Contains("no candidates")
            && e.Message.Contains("user-store absent")
            && e.Message.Contains("baseline absent"));
        capture.Measurements.Should().ContainSingle(m =>
            m.Frame == "overlay" && m.Outcome == "miss" && m.Value == 1);
    }

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    /// <summary>
    /// MeterListener that captures every PickerOutcomes measurement for the
    /// duration of a single Arrange-Act phase. Filtered by meter name +
    /// instrument name so cross-test pollution can't perturb the assertion;
    /// the [Collection] attribute also forces serial execution across these
    /// telemetry tests (parallel listeners share a process-wide registry).
    /// </summary>
    private sealed class PickerMeterCapture : IDisposable
    {
        public readonly record struct Captured(string Frame, string Outcome, long Value);

        public readonly List<Captured> Measurements = new();
        private readonly MeterListener _listener;
        private readonly object _gate = new();

        private PickerMeterCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instr, l) =>
                {
                    if (instr.Meter.Name == "Mithril.Legolas.Calibration"
                        && instr.Name == "mithril.legolas.calibration.picker.outcomes")
                    {
                        l.EnableMeasurementEvents(instr);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>(OnMeasurement);
            _listener.Start();
            // Force the static field cctor so InstrumentPublished fires for it.
            _ = MapCalibrationDiagnostics.LegolasCalibrationPickerMeter.PickerOutcomes;
        }

        public static PickerMeterCapture Start() => new();

        private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        {
            string? frame = null, outcome = null;
            foreach (var kv in tags)
            {
                if (kv.Key == "frame") frame = kv.Value as string;
                else if (kv.Key == "outcome") outcome = kv.Value as string;
            }
            lock (_gate) Measurements.Add(new Captured(frame ?? string.Empty, outcome ?? string.Empty, measurement));
        }

        public void Dispose() => _listener.Dispose();
    }
}

/// <summary>
/// Serializes the picker meter tests so a parallel test's MeterListener
/// doesn't capture another test's PickerOutcomes increments. The
/// PickerOutcomes counter is process-static.
/// </summary>
[CollectionDefinition("MapCalibrationTelemetry", DisableParallelization = true)]
public sealed class MapCalibrationTelemetryTestCollection { }
