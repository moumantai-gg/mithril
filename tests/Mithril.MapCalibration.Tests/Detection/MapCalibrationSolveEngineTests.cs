using System;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class MapCalibrationSolveEngineTests
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

    private static MapCalibrationSolveEngine Engine() =>
        new(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate());

    private static IconTemplateSet Templates() => SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);

    private static MapRect Rect() => new(0, 0, TexW, TexH, TexW, TexH);

    private static DetectionRequest Request(GrayImage shot, GrayImage tex) =>
        new(shot, tex, Rect(), Templates(), RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));

    [Fact]
    public void Recovers_truth_and_gates_green()
    {
        var (shot, tex, refs) = Build();
        var result = Engine().Solve(Request(shot, tex), refs);

        result.Calibration.Should().NotBeNull();
        result.RejectReason.Should().BeNull();
        Math.Abs(result.Calibration!.Scale - Truth.Scale).Should().BeLessThan(0.05);
        Math.Abs(NormaliseAngle(result.Calibration.RotationRadians - Truth.RotationRadians)).Should().BeLessThan(0.05);
        result.Calibration.ResidualPixels.Should().BeLessThan(12.0);
        result.InlierCount.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Black_screenshot_gates_red()   // negative control
    {
        var (_, tex, refs) = Build();
        var black = new GrayImage(TexW, TexH, new byte[TexW * TexH]);

        var result = Engine().Solve(Request(black, tex), refs);

        result.Calibration.Should().BeNull();
        result.RejectReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Wrong_area_refs_gate_red()   // negative control
    {
        var (shot, tex, _) = Build();
        // Refs that don't correspond to any placed icon geometry.
        var wrongRefs = Enumerable.Range(0, 5)
            .Select(i => new LandmarkReference("Portal", "landmark_portal", new WorldCoord(1000 + i * 37, 0, -900 - i * 53)))
            .ToList();

        var result = Engine().Solve(Request(shot, tex), wrongRefs);

        result.Calibration.Should().BeNull();
        result.RejectReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Solve_populates_Detections_on_accepted_result()
    {
        var (shot, tex, refs) = Build();
        var result = Engine().Solve(Request(shot, tex), refs);

        result.Calibration.Should().NotBeNull();
        result.Detections.Should().NotBeNull();
        result.Detections!.Should().NotBeEmpty();
    }

    private static double NormaliseAngle(double radians)
    {
        var twoPi = 2 * Math.PI;
        var r = radians % twoPi;
        if (r > Math.PI) r -= twoPi;
        if (r < -Math.PI) r += twoPi;
        return r;
    }
}
