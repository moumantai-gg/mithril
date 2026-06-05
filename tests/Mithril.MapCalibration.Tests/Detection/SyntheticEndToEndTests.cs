using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Internal;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Always-on CI gate: the gate-study SelfTest ported to xUnit against the headless
/// <see cref="MapCalibrationSolveEngine"/>. Synthesises a texture + typed icons +
/// landmarks, composites a "screenshot", runs detect→solve→gate, and asserts the
/// recovered transform ≈ truth, then round-trips the result through the internal
/// baseline JSON context. No PG install, no study/ assets.
/// </summary>
public sealed class SyntheticEndToEndTests
{
    private const int TexW = 800, TexH = 600;

    // #1076 Phase 6.5: ground truth in texture-pixel frame.
    private static readonly WorldToTextureCalibration Truth = new(
        OriginX: 400.0, OriginY: 300.0, Scale: 1.2, RotationRadians: 0.35,
        MirrorNorth: false, CalibrationZoom: 1.0);

    private static readonly (string Type, string Icon, int W, int H, int Lum, double X, double Z)[] Landmarks =
    [
        ("Portal", "landmark_portal", 24, 32, 60, -50.0, 80.0),
        ("Portal", "landmark_portal", 24, 32, 60, 75.0, -40.0),
        ("TeleportationPlatform", "landmark_telepad", 28, 22, 180, 100.0, 20.0),
        ("MeditationPillar", "landmark_medipillar", 18, 40, 110, 0.0, -10.0),
        ("Npc", "landmark_npc", 20, 28, 220, 60.0, 55.0),
    ];

    [Fact]
    public void Engine_recovers_truth_from_synthetic_composite()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 1234);
        var shotPixels = (byte[])texPixels.Clone();
        var refs = new List<LandmarkReference>();
        foreach (var l in Landmarks)
        {
            var tex = Truth.ToTexture(new WorldCoord(l.X, 0, l.Z));
            SyntheticMap.BlitTeardrop(shotPixels, TexW, TexH, tex.X, tex.Y, l.W, l.H, l.Lum);
            refs.Add(new LandmarkReference(l.Type, l.Icon, new WorldCoord(l.X, 0, l.Z)));
        }

        var shot = new GrayImage(TexW, TexH, shotPixels);
        var tex2 = new GrayImage(TexW, TexH, texPixels);
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        var rect = new MapRect(0, 0, TexW, TexH, TexW, TexH);
        var request = new DetectionRequest(shot, tex2, rect, templates, RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.45,
            BlobOptions: new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5));

        var engine = new MapCalibrationSolveEngine(new DeviationBlobCalibrationDetector(), new CalibrationConfidenceGate());
        var result = engine.Solve(request, refs);

        result.Calibration.Should().NotBeNull("the engine must cold-solve the synthetic fixture");
        result.RejectReason.Should().BeNull();
        var cal = result.Calibration!;
        Math.Abs(cal.Scale - 1.2).Should().BeLessThan(0.05);
        Math.Abs(NormaliseAngle(cal.RotationRadians - 0.35)).Should().BeLessThan(0.02);
        Math.Abs(cal.OriginX - 400.0).Should().BeLessThan(5.0);
        Math.Abs(cal.OriginY - 300.0).Should().BeLessThan(5.0);
        cal.MirrorNorth.Should().BeFalse();
        result.InlierCount.Should().BeGreaterThanOrEqualTo(4);

        // Round-trip through the internal baseline JSON context (the same shape
        // BundledBaselineLoader reads), proving the solved record serialises.
        var file = new BundledBaselineFile(1, new Dictionary<string, AreaCalibration>
        {
            ["AreaSelfTest"] = cal with { Source = CalibrationSource.BundledBaseline },
        });
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.BundledBaselineFile);
        json.Should().Contain("AreaSelfTest").And.Contain("scale").And.Contain("residualPixels");

        var back = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.BundledBaselineFile);
        back!.Anchors.Should().ContainKey("AreaSelfTest");
        back.Anchors["AreaSelfTest"].Scale.Should().BeApproximately(cal.Scale, 1e-9);
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
