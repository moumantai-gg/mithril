using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// #1117: CalibrationSolveResult.Synthesis is populated whenever synthesis-J ran,
/// regardless of which mode drove the gate verdict. Null only when mode == Off.
/// </summary>
public sealed class MapCalibrationSolveEngineSynthesisWiringTests
{
    [Fact]
    public void Off_mode_leaves_Synthesis_null()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().BeNull();
    }

    [Fact]
    public void Shadow_mode_populates_Synthesis_with_mode_shadow()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().NotBeNull();
        result.Synthesis!.Mode.Should().Be("shadow");
        result.Synthesis.JMin.Should().Be(options.SynthesisJMin);
        result.Synthesis.NMin.Should().Be(options.SynthesisNMin);
    }

    [Fact]
    public void Enabled_mode_populates_Synthesis_with_mode_enabled()
    {
        var (detector, refs, request) = BuildFixture();
        var options = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Enabled };
        var engine = new MapCalibrationSolveEngine(detector, new AlwaysRejectGate(), logger: null, options: options);

        var result = engine.Solve(request, refs);

        result.Synthesis.Should().NotBeNull();
        result.Synthesis!.Mode.Should().Be("enabled");
    }

    private static (ICalibrationDetector Detector, List<LandmarkReference> Refs, DetectionRequest Request) BuildFixture()
    {
        // One Portal detection, one Portal reference — both type vocabularies overlap so RANSAC
        // runs and synthesis scores ARE computed (degenerate fixture is fine — we only care
        // that the synthesis pathway executed and populated the diagnostics field).
        var detections = new Dictionary<string, IReadOnlyList<TypedDetection>>(StringComparer.Ordinal)
        {
            ["Portal"] = new[] { new TypedDetection("Portal", "icon", new CroppedFramePixel(2, 2), 0.9) },
        };
        var detector = new FixedDetector(detections);
        var refs = new List<LandmarkReference>
        {
            new("Portal", "Test Portal", new WorldCoord(1, 0, 2)),
        };
        var img = new GrayImage(8, 8, new byte[64]);
        var rect = new MapRect(0, 0, 8, 8, 8, 8);
        var request = new DetectionRequest(img, img, rect, IconTemplateSet.Empty, RimMaskMode.None,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));
        return (detector, refs, request);
    }

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
}
