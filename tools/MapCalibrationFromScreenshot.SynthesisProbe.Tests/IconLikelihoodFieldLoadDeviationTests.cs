using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldLoadDeviationTests
{
    [Fact]
    public void LoadDeviationAsField_peaks_at_pre_subtracted_icon_location()
    {
        const int W = 64, H = 64;
        // Synthetic pre-subtracted deviation: black background, single 5x5
        // cross stamped at (32, 32). This is what the live engine produces
        // post-ECC, post-subtraction.
        var devPixels = new byte[W * H];
        StampCross(devPixels, W, cx: 32, cy: 32);
        var deviation = new GrayImage(W, H, devPixels);

        var template = MakeCrossTemplate();
        var field = IconLikelihoodField.LoadDeviationAsField(deviation, template);

        field.GetLength(0).Should().Be(H);
        field.GetLength(1).Should().Be(W);

        var (maxX, maxY) = Argmax(field);
        maxX.Should().BeInRange(31, 33);
        maxY.Should().BeInRange(31, 33);
        field[maxY, maxX].Should().BeGreaterThan(0.8);
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

    private static (int X, int Y) Argmax(double[,] field)
    {
        int bestX = 0, bestY = 0; double bestV = double.NegativeInfinity;
        for (int y = 0; y < field.GetLength(0); y++)
            for (int x = 0; x < field.GetLength(1); x++)
                if (field[y, x] > bestV) { bestV = field[y, x]; bestX = x; bestY = y; }
        return (bestX, bestY);
    }
}
