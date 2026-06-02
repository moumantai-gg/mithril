using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldLoadDeviationRimMaskTests
{
    [Fact]
    public void LoadDeviationAsField_RimMaskAffectsScoredColumnAdjacentToEdge()
    {
        // 64x64 deviation with an edge-touching strip at column 63 (right edge),
        // rows 10-15, value 200 (above the 0.5 * 255 = 127.5 threshold). Plus an
        // interior cross at (32, 32) far from any edge.
        //
        // ScoreAll only writes field[y, cx] where (cx - ax) + tw <= W. For a 5x5
        // template with ax = Math.Round(0.5 * 5) = 2 (banker's rounding), the
        // last scored column is cx = 61 (window spans columns 59-63). The
        // template's opaque pixel at (ty=2, tx=4) reads source pixel (cy, 63).
        // So scoring at cx = 61 INCLUDES the rim-strip pixel; masking it
        // changes the field value there. cx = 62, 63 are never scored, so any
        // assertion at those columns would be a no-op (would pass even with
        // applyRimMask: false). That's why we assert at cx = 61.
        const int W = 64, H = 64;
        var devBytes = new byte[W * H];
        for (int y = 10; y <= 15; y++) devBytes[y * W + (W - 1)] = 200;
        StampCross(devBytes, W, cx: 32, cy: 32);
        var deviation = new GrayImage(W, H, devBytes);

        var template = MakeCrossTemplate();

        var raw = IconLikelihoodField.LoadDeviationAsField(
            deviation, template, applyRimMask: false, devThr: IconLikelihoodField.DefaultDevThr);
        var masked = IconLikelihoodField.LoadDeviationAsField(
            deviation, template, applyRimMask: true, devThr: IconLikelihoodField.DefaultDevThr);

        // At cx=61, the window spans cols 59-63 and the template reads (cy, 63).
        // raw includes the rim pixel (value 200); masked zeros it.
        masked[12, 61].Should().NotBe(raw[12, 61],
            "masking the rim pixel at (12, 63) must change the score at the adjacent scored column cx=61");

        // The interior cross peak must still score high under masking (the flood
        // shouldn't reach inland from any edge).
        masked[32, 32].Should().BeGreaterThan(0.8);
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
