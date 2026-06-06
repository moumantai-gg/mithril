using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class CrossCorrelationMapViewProbeTests
{
    [Fact]
    public void IdenticalScreenshot_ReturnsZeroPanAndUnitScale()
    {
        var texture = MakeStripedGray(256, 256);
        var screenshot = MakeStripedGray(256, 256);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.PanTexPxX.Should().BeApproximately(0, 1.0);
        fix.Value.PanTexPxY.Should().BeApproximately(0, 1.0);
        fix.Value.ViewScale.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void PannedScreenshot_ReturnsExpectedPan()
    {
        var texture = MakeStripedGray(256, 256);
        var screenshot = CropShifted(texture, 64, 32, 128, 128);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.PanTexPxX.Should().BeApproximately(64, 2.0);
        fix.Value.PanTexPxY.Should().BeApproximately(32, 2.0);
        fix.Value.ViewScale.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void ScaledScreenshot_ReturnsExpectedScale()
    {
        var texture = MakeStripedGray(256, 256);
        // 128×128 screenshot = texture rendered at 0.5× (viewScale)
        var screenshot = DownsampleHalf(texture);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.ViewScale.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void NoiseScreenshot_ReturnsNull()
    {
        var texture = MakeStripedGray(256, 256);
        var noise = MakeRandomGray(256, 256, seed: 42);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(noise, texture);

        fix.Should().BeNull();
    }

    private static GrayImage MakeStripedGray(int w, int h)
    {
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = (byte)((x ^ y) & 0xFF);
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage CropShifted(GrayImage src, int dx, int dy, int w, int h)
    {
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = (x + dx) % src.Width;
                int sy = (y + dy) % src.Height;
                pixels[y * w + x] = src.Pixels[sy * src.Width + sx];
            }
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage DownsampleHalf(GrayImage src)
    {
        int w = src.Width / 2, h = src.Height / 2;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int a = src.Pixels[(2 * y) * src.Width + 2 * x];
                int b = src.Pixels[(2 * y) * src.Width + 2 * x + 1];
                int c = src.Pixels[(2 * y + 1) * src.Width + 2 * x];
                int d = src.Pixels[(2 * y + 1) * src.Width + 2 * x + 1];
                pixels[y * w + x] = (byte)((a + b + c + d) / 4);
            }
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage MakeRandomGray(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[w * h];
        rng.NextBytes(pixels);
        return new GrayImage(w, h, pixels);
    }
}
