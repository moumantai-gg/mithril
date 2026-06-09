using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
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

    [Fact]
    public void Shadow_mode_emits_synthesis_summary_line()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        logger.Infos.Should().ContainSingle(m => m.Contains("Synthesis-J (shadow"));
    }

    [Fact]
    public void Off_mode_emits_no_synthesis_line()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        logger.Infos.Should().NotContain(m => m.Contains("Synthesis-J"));
    }

    [Fact]
    public void Enabled_mode_does_not_double_log_synthesis()
    {
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Enabled };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger, options);

        engine.Solve(request, refs);

        // The existing Enabled-mode line at lines 146-148/156 of MapCalibrationSolveEngine
        // already logs J in its own "Auto-calibration accepted/rejected (synthesis-J)"
        // message. The new Shadow-mode mirror MUST NOT also fire here.
        logger.Infos.Should().NotContain(m => m.Contains("Synthesis-J (shadow"));
    }

    [Fact]
    public void Shadow_mode_log_includes_disagree_property_when_gates_differ()
    {
        // The Hogan's case in miniature: legacy gate accepts a cal, synthesis-J would
        // reject (because J or RefsAboveHalf below the threshold). The "disagree=true"
        // signal is the bit threshold-tuning conversations want to grep on.
        var (detector, refs, request) = BuildSynthesisFixture();
        var options = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Shadow,
            // Force synthesis to reject by setting an unreachable Nmin floor.
            SynthesisNMin = 9999,
        };
        var logger = new CapturingLogger();
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysAcceptGate(), logger, options);

        engine.Solve(request, refs);

        var line = logger.Infos.Should().ContainSingle(m => m.Contains("Synthesis-J (shadow")).Subject;
        // The legacy gate accepted (AlwaysAcceptGate), synthesis would reject (Nmin=9999) → disagree.
        line.Should().Contain("disagrees-with-gate=True");
        line.Should().Contain("would-reject");
    }

    private static (ICalibrationDetector Detector, List<LandmarkReference> Refs, DetectionRequest Request) BuildSynthesisFixture()
    {
        // Four distinct landmark types, each with one detection at a well-separated
        // pixel and one matching reference at the corresponding world coord under an
        // identity 1:1 texture↔world mapping. The bounding box of detection pixels
        // spans 150 px in each dim — well above RANSAC's 100-px floor. With one
        // detection + one ref per type, the type-keyed correspondence is unambiguous
        // and RANSAC reliably picks the right seed pair → a real synthesis winner.
        var detections = new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["Portal"] = new[] { new TypedDetection("Portal", "landmark_portal", new CroppedFramePixel(50, 50), 0.9) },
            ["TeleportationPlatform"] = new[] { new TypedDetection("TeleportationPlatform", "landmark_telepad", new CroppedFramePixel(200, 50), 0.9) },
            ["MeditationPillar"] = new[] { new TypedDetection("MeditationPillar", "landmark_medipillar", new CroppedFramePixel(50, 200), 0.9) },
            ["Npc"] = new[] { new TypedDetection("Npc", "landmark_npc", new CroppedFramePixel(200, 200), 0.9) },
        };
        var detector = new FixedDetector(detections);
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Test Portal", new WorldCoord(50, 0, 50)),
            new("TeleportationPlatform", "Test Telepad", new WorldCoord(200, 0, 50)),
            new("MeditationPillar", "Test Pillar", new WorldCoord(50, 0, 200)),
            new("Npc", "Test NPC", new WorldCoord(200, 0, 200)),
        };
        // 1:1 cropped↔texture mapping so detection pixels land at the same texture-px
        // coords; with refs at those world coords, the similarity solve is the
        // identity transform — RANSAC's seed-pair-to-inliers loop is trivially happy.
        var img = new GrayImage(256, 256, new byte[256 * 256]);
        var rect = new MapRect(0, 0, 256, 256, 256, 256);
        var request = new DetectionRequest(img, img, rect, IconTemplateSet.Empty, RimMaskMode.None,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));
        return (detector, refs, request);
    }

    private sealed class AlwaysAcceptGate : ICalibrationConfidenceGate
    {
        public bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
        {
            rejectReason = null;
            return true;
        }
    }
}
