using System;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class DeviationBlobCalibrationDetectorTests
{
    private const int TexW = 300, TexH = 240;

    // Place each icon type at a well-spread anchor on the texture.
    private static readonly (string Type, int W, int H, int Lum, double Ax, double Ay)[] Placements =
    [
        ("Portal", 24, 32, 60, 70, 70),
        ("TeleportationPlatform", 28, 22, 180, 220, 60),
        ("MeditationPillar", 18, 40, 110, 90, 180),
        ("Npc", 20, 28, 220, 230, 190),
    ];

    private static (GrayImage shot, GrayImage tex) BuildPair()
    {
        var texPixels = SyntheticMap.MakeTexture(TexW, TexH, seed: 4242);
        var shotPixels = (byte[])texPixels.Clone();
        foreach (var p in Placements)
            SyntheticMap.BlitTeardrop(shotPixels, TexW, TexH, p.Ax, p.Ay, p.W, p.H, p.Lum);
        return (new GrayImage(TexW, TexH, shotPixels), new GrayImage(TexW, TexH, texPixels));
    }

    private static DetectionRequest Request(GrayImage shot, GrayImage tex)
    {
        var templates = SyntheticMap.BuildTemplates(SyntheticMap.DefaultIcons);
        // The screenshot is already the cropped map; the texture is aligned 1:1.
        var rect = new MapRect(0, 0, TexW, TexH, TexW, TexH);
        var opts = new BlobOptions(MinArea: 8, MaxIconArea: 1500, MinSolidity: 0.25, MaxAspect: 3.5, MinPeak: 0.5);
        return new DetectionRequest(shot, tex, rect, templates, RimMaskMode.DeviationFlood,
            LowNcc: 0.5, TypeFloor: 0.45, BlobOptions: opts);
    }

    [Fact]
    public void Types_at_least_three_of_four_icons()
    {
        var (shot, tex) = BuildPair();
        var detector = new DeviationBlobCalibrationDetector();

        var byType = detector.Detect(Request(shot, tex));

        // Count how many placements were detected near their anchor with the
        // correct landmark type.
        int correct = 0;
        foreach (var p in Placements)
        {
            if (!byType.TryGetValue(p.Type, out var dets)) continue;
            bool near = dets.Any(d => Math.Abs(d.Anchor.X - p.Ax) <= 6 && Math.Abs(d.Anchor.Y - p.Ay) <= 6);
            if (near) correct++;
        }
        correct.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Black_screenshot_yields_no_detections()   // negative control
    {
        var tex = new GrayImage(TexW, TexH, SyntheticMap.MakeTexture(TexW, TexH, 99));
        var black = new GrayImage(TexW, TexH, new byte[TexW * TexH]);
        var detector = new DeviationBlobCalibrationDetector();

        var byType = detector.Detect(Request(black, tex));

        byType.Values.Sum(v => v.Count).Should().Be(0);
    }
}
