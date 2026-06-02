using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldLoadDeviationRimMaskTests
{
    [Fact]
    public void LoadDeviationAsField_RimMaskZerosFieldUnderRim()
    {
        // 64x64 deviation: an edge-touching strip on the right + an interior
        // cross-shaped icon-like blob. Rim mask should zero the right strip
        // (peak score there must be 0) but leave the interior blob scoreable.
        const int W = 64, H = 64;
        var devBytes = new byte[W * H];
        // Right-edge strip: column 63, rows 10..15, value 200.
        for (int y = 10; y <= 15; y++) devBytes[y * W + (W - 1)] = 200;
        // Interior cross at (32, 32): 5x5 plus pattern.
        StampCross(devBytes, W, cx: 32, cy: 32);
        var deviation = new GrayImage(W, H, devBytes);

        var template = MakeCrossTemplate();
        var fieldMasked = IconLikelihoodField.LoadDeviationAsField(
            deviation, template, applyRimMask: true, devThr: IconLikelihoodField.DefaultDevThr);

        // Field score directly UNDER the right strip's pixels must be zero
        // (rim was zeroed → NCC over an all-zero window has zero std → field=0).
        fieldMasked[12, W - 1].Should().Be(0.0);
        fieldMasked[13, W - 1].Should().Be(0.0);

        // Interior cross's peak score must be high.
        fieldMasked[32, 32].Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void LoadDeviationAsField_RimMaskDisabled_MatchesScoreAll()
    {
        // With the rim mask disabled, the overload should return exactly
        // the same field as ScoreAll on the raw deviation.
        const int W = 64, H = 64;
        var devBytes = new byte[W * H];
        for (int y = 10; y <= 15; y++) devBytes[y * W + (W - 1)] = 200;
        StampCross(devBytes, W, cx: 32, cy: 32);
        var deviation = new GrayImage(W, H, devBytes);

        var template = MakeCrossTemplate();
        var fieldUnmasked = IconLikelihoodField.LoadDeviationAsField(
            deviation, template, applyRimMask: false, devThr: IconLikelihoodField.DefaultDevThr);
        var fieldScoreAll = IconLikelihoodField.ScoreAll(deviation, template);

        // The two fields must be element-wise equal.
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                fieldUnmasked[y, x].Should().Be(fieldScoreAll[y, x]);
    }

    private static void StampCross(byte[] pixels, int width, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            pixels[(cy + dy) * width + cx] = 200;
        for (int dx = -2; dx <= 2; dx++)
            pixels[cy * width + (cx + dx)] = 200;
        pixels[cy * width + cx] = 255;
    }

    private static IconTemplate MakeCrossTemplate()
    {
        var gray = new byte[]   { 0,0,200,0,0,  0,0,200,0,0,  200,200,255,200,200,  0,0,200,0,0,  0,0,200,0,0 };
        var alpha = new byte[]  { 0,0,255,0,0,  0,0,255,0,0,  255,255,255,255,255,  0,0,255,0,0,  0,0,255,0,0 };
        return new IconTemplate(
            Name: "x", LandmarkType: "Portal", PivotX: 0.5, PivotY: 0.5,
            Gray: new GrayImage(5, 5, gray),
            Alpha: new GrayImage(5, 5, alpha));
    }
}
