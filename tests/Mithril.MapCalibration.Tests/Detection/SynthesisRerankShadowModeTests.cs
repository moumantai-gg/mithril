using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class SynthesisRerankShadowModeTests
{
    private const int TexW = 320, TexH = 260;
    private static readonly AreaCalibration Truth = new(
        Scale: 1.1, RotationRadians: 0.25, OriginX: 160, OriginY: 130,
        ReferenceCount: 0, ResidualPixels: 0.0)
    { MirrorNorth = false, CalibrationZoom = 1.0 };

    private static readonly (string Type, string Icon, int W, int H, int Lum, double X, double Z)[] Landmarks =
    [
        ("Portal", "landmark_portal", 24, 32, 60, -60, 70),
        ("Portal", "landmark_portal", 24, 32, 60, 70, -50),
        ("TeleportationPlatform", "landmark_telepad", 28, 22, 180, 90, 30),
        ("MeditationPillar", "landmark_medipillar", 18, 40, 110, -20, -40),
        ("Npc", "landmark_npc", 20, 28, 220, 40, 55),
    ];

    private static (GrayImage shot, GrayImage tex, System.Collections.Generic.List<LandmarkReference> refs) Build()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 7777);
        var shotPixels = (byte[])texPixels.Clone();
        var refs = new System.Collections.Generic.List<LandmarkReference>();
        foreach (var l in Landmarks)
        {
            var tex = Truth.WorldToWindow(new WorldCoord(l.X, 0, l.Z));
            SyntheticMap.BlitTeardrop(shotPixels, TexW, TexH, tex.X, tex.Y, l.W, l.H, l.Lum);
            refs.Add(new LandmarkReference(l.Type, l.Icon, new WorldCoord(l.X, 0, l.Z)));
        }
        return (new GrayImage(TexW, TexH, shotPixels), new GrayImage(TexW, TexH, texPixels), refs);
    }

    private static MapCalibrationSolveEngine EngineWith(MapCalibrationSolverOptions opts) =>
        new(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate(), null, opts);

    private static IconTemplateSet Templates() => SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);

    private static MapRect Rect() => new(0, 0, TexW, TexH, TexW, TexH);

    private static DetectionRequest Request(GrayImage shot, GrayImage tex) =>
        new(shot, tex, Rect(), Templates(), RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.80,
            BlobOptions: new BlobOptions(MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35, MaxAspect: 2.5, MinPeak: 0.7))
            { RenderSizePx = 16 };

    [Fact]
    public void Mode_Off_emits_no_synthesis_span()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off };
        var engine = EngineWith(opts);

        var spans = new System.Collections.Generic.List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Mithril.MapCalibration.Detection",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => spans.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        _ = engine.Solve(Request(shot, tex), refs);

        spans.Should().NotContain(a => a.OperationName == "calibration.synthesis_rerank");
    }

    [Fact]
    public void Mode_Shadow_emits_span_but_legacy_gate_is_source_of_truth()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Shadow };
        var shadowEngine = EngineWith(opts);
        var offEngine = EngineWith(new MapCalibrationSolverOptions { SynthesisRerankMode = SynthesisRerankMode.Off });

        var spans = new System.Collections.Generic.List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Mithril.MapCalibration.Detection",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => spans.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var shadowResult = shadowEngine.Solve(Request(shot, tex), refs);
        var offResult    = offEngine.Solve(Request(shot, tex), refs);

        // Legacy verdict is unchanged from Off (Shadow keeps the legacy gate as source-of-truth).
        (shadowResult.Calibration is not null).Should().Be(offResult.Calibration is not null);

        // Synthesis span emitted.
        spans.Should().Contain(a => a.OperationName == "calibration.synthesis_rerank");
        var synth = spans.First(a => a.OperationName == "calibration.synthesis_rerank");
        synth.GetTagItem("synth.mode").Should().Be("shadow");
        synth.GetTagItem("synth.verdict").Should().BeOneOf("accept", "reject");
    }

    [Fact]
    public void Mode_Enabled_rejects_when_J_below_threshold()
    {
        var (shot, tex, refs) = Build();
        // Set J_min absurdly high so Enabled rejects even the truth fit.
        var opts = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Enabled,
            SynthesisJMin = 1_000_000.0,
            SynthesisNMin = 1_000_000,
        };
        var engine = EngineWith(opts);

        var result = engine.Solve(Request(shot, tex), refs);
        result.Calibration.Should().BeNull();
        result.RejectReason.Should().Contain("synthesis-J below threshold");
    }

    [Fact]
    public void Mode_Enabled_accepts_when_synthesis_thresholds_clear()
    {
        var (shot, tex, refs) = Build();
        var opts = new MapCalibrationSolverOptions
        {
            SynthesisRerankMode = SynthesisRerankMode.Enabled,
            SynthesisJMin = 0.0,
            SynthesisNMin = 0,
        };
        var engine = EngineWith(opts);

        var result = engine.Solve(Request(shot, tex), refs);
        result.Calibration.Should().NotBeNull(
            "synthesis-J with zero thresholds must accept the synthetic truth fit");
    }
}
