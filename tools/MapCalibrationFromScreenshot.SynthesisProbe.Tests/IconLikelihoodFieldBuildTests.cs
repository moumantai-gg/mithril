using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests;

public class IconLikelihoodFieldBuildTests
{
    [Fact]
    public void Field_peaks_at_known_icon_location()
    {
        // 64x64 black background. Base is also black.
        // Using fill=0 means deviation = max(0, screenshot - base) = screenshot itself,
        // so the stamped template pixels propagate unchanged into the deviation image.
        const int W = 64, H = 64;
        var screenshot = NewGray(W, H, fill: 0);
        var baseTex = NewGray(W, H, fill: 0);

        // 5x5 template: bright cross with varying pixel values so NCC is well-defined.
        // Opaque region: center (2,2) = 255, arms = 200. Transparent corners = 0.
        // Alpha mirrors the cross shape (opaque where cross arm pixels are).
        // IconTemplate uses GrayImage Gray + GrayImage Alpha (not flat byte arrays).
        var crossPixels = new byte[]
        {
              0,  0,200,  0,  0,
              0,  0,200,  0,  0,
            200,200,255,200,200,
              0,  0,200,  0,  0,
              0,  0,200,  0,  0
        };
        var crossAlpha = new byte[]
        {
            0,0,255,0,0,
            0,0,255,0,0,
            255,255,255,255,255,
            0,0,255,0,0,
            0,0,255,0,0
        };
        var template = new IconTemplate(
            Name: "x",
            LandmarkType: "Portal",
            PivotX: 0.5,
            PivotY: 0.5,
            Gray: new GrayImage(5, 5, crossPixels),
            Alpha: new GrayImage(5, 5, crossAlpha));

        // Stamp the template on the screenshot at center (32,32).
        StampTemplate(screenshot, template, cx: 32, cy: 32);

        var field = IconLikelihoodField.Build(screenshot, baseTex, template);

        field.GetLength(0).Should().Be(H);
        field.GetLength(1).Should().Be(W);
        var (maxX, maxY) = Argmax(field);
        maxX.Should().BeInRange(31, 33);
        maxY.Should().BeInRange(31, 33);
        field[maxY, maxX].Should().BeGreaterThan(0.8);
    }

    private static GrayImage NewGray(int w, int h, byte fill)
    {
        var p = new byte[w * h];
        Array.Fill(p, fill);
        return new GrayImage(w, h, p);
    }

    private static void StampTemplate(GrayImage img, IconTemplate t, int cx, int cy)
    {
        int tw = t.Gray.Width;
        int th = t.Gray.Height;
        int x0 = cx - tw / 2;
        int y0 = cy - th / 2;
        for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                if (t.Alpha.Pixels[ty * tw + tx] < 128) continue;
                int x = x0 + tx, y = y0 + ty;
                if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                img.Pixels[y * img.Width + x] = t.Gray.Pixels[ty * tw + tx];
            }
    }

    private static (int X, int Y) Argmax(double[,] field)
    {
        int bestX = 0, bestY = 0;
        double bestV = double.NegativeInfinity;
        for (int y = 0; y < field.GetLength(0); y++)
            for (int x = 0; x < field.GetLength(1); x++)
                if (field[y, x] > bestV) { bestV = field[y, x]; bestX = x; bestY = y; }
        return (bestX, bestY);
    }
}
