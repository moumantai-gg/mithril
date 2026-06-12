using System;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1116 Task 3 — <see cref="FogOfWarDetector"/> identifies fog-of-war
/// regions in a screenshot by combining a local-variance ceiling and a
/// luminance window. Used as residual coverage for fog-region edges that
/// <c>LocalNccDeviation</c>'s <c>addedOnly:true</c> doesn't fully suppress.
/// </summary>
public sealed class FogOfWarDetectorTests
{
    [Fact]
    public void Detect_marks_uniform_low_variance_fog_color_region()
    {
        // Half "fog" (uniform 125) + half "floor detail" (mid-range noise).
        var img = SplitImage(20, 20, fogValue: 125, detailMean: 125, detailRange: 80, detailSeed: 42);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0,
            FogColorMin = 110,
            FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);

        var fog = detector.Detect(img);

        // Center of fog half: marked (well inside, all neighbours uniform).
        fog.Pixels[10 * 20 + 5].Should().Be((byte)255);
        // Center of detail half: not marked (high variance).
        fog.Pixels[10 * 20 + 15].Should().Be((byte)0);
    }

    [Fact]
    public void Detect_rejects_uniform_bright_region_outside_color_window()
    {
        // Uniform grey 200 → low variance, but above FogColorMax = 140.
        var img = UniformImage(20, 20, 200);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0,
            FogColorMin = 110,
            FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);
        var fog = detector.Detect(img);

        fog.Pixels.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void Detect_rejects_uniform_dark_region_outside_color_window()
    {
        // Uniform grey 50 → low variance, but below FogColorMin = 110.
        var img = UniformImage(20, 20, 50);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0,
            FogColorMin = 110,
            FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);
        var fog = detector.Detect(img);

        fog.Pixels.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void Detect_returns_all_zeros_when_disabled()
    {
        var img = UniformImage(20, 20, 125);
        var opts = new MapCalibrationDetectorOptions { FogOfWarDetectionEnabled = false };
        var detector = new FogOfWarDetector(opts);
        var fog = detector.Detect(img);

        fog.Pixels.Should().AllBeEquivalentTo((byte)0);
    }

    private static GrayImage UniformImage(int w, int h, byte v)
        => new(w, h, Enumerable.Repeat(v, w * h).ToArray());

    private static GrayImage SplitImage(int w, int h, byte fogValue, byte detailMean, byte detailRange, int detailSeed)
    {
        var rng = new Random(detailSeed);
        var p = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                p[y * w + x] = x < w / 2
                    ? fogValue
                    : (byte)Math.Clamp(detailMean + rng.Next(-detailRange / 2, detailRange / 2 + 1), 0, 255);
            }
        return new GrayImage(w, h, p);
    }
}
