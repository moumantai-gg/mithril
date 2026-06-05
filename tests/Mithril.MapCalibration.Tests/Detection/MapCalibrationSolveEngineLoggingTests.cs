using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#974 detect-stage diagnostics: the engine emits a per-orientation
/// detect summary and a targeted Warning when the detection type-key set and the
/// reference type-key set are disjoint (the failure mode where the icon-template
/// and reference vocabularies don't overlap → 0 correspondences possible).
/// </summary>
public sealed class MapCalibrationSolveEngineLoggingTests
{
    [Fact]
    public void Warns_when_detection_and_reference_vocabularies_are_disjoint()
    {
        // Detections keyed by a vocabulary the refs DON'T use → disjoint.
        var detector = new FixedDetector(new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["landmark_portal"] = new[] { new TypedDetection("landmark_portal", "icon", new CroppedFramePixel(10, 10), 0.9) },
        });
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Serbule Portal", new WorldCoord(1, 0, 2)),
        };

        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger);

        engine.Solve(BuildRequest(), refs);

        logger.Warnings.Should().Contain(m =>
            m.Contains("disjoint") && m.Contains("0 correspondences possible"));
    }

    [Fact]
    public void Does_not_warn_when_vocabularies_overlap()
    {
        // Detections + refs share the canonical "Portal" key → overlap, no warning.
        var detector = new FixedDetector(new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["Portal"] = new[] { new TypedDetection("Portal", "icon", new CroppedFramePixel(10, 10), 0.9) },
        });
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Serbule Portal", new WorldCoord(1, 0, 2)),
        };

        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger);

        engine.Solve(BuildRequest(), refs);

        logger.Warnings.Should().NotContain(m => m.Contains("disjoint"));
        // The per-orientation detect summary still fires (Information).
        logger.Infos.Should().Contain(m => m.Contains("typed detections"));
    }

    private static DetectionRequest BuildRequest()
    {
        var img = new GrayImage(8, 8, new byte[64]);
        var rect = new MapRect(0, 0, 8, 8, 8, 8);
        return new DetectionRequest(img, img, rect, IconTemplateSet.Empty, RimMaskMode.None,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));
    }

    /// <summary>Detector that ignores the request and returns a fixed detection map.</summary>
    private sealed class FixedDetector : ICalibrationDetector
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> _result;
        public FixedDetector(IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> result) => _result = result;
        public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request) => _result;
    }

    private sealed class AlwaysRejectGate : ICalibrationConfidenceGate
    {
        public bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
        {
            rejectReason = "test-reject";
            return false;
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();
        public List<string> Infos { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (logLevel == LogLevel.Warning) Warnings.Add(msg);
            else if (logLevel == LogLevel.Information) Infos.Add(msg);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
