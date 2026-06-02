using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class AttemptBundleVisualizerTests
{
    [Fact]
    public void RenderDeviation_returns_max_positive_diff_per_pixel()
    {
        // 2x2 fixtures with known pairwise diffs.
        var a = new GrayImage(2, 2, new byte[] { 100, 200, 50, 75 });
        var b = new GrayImage(2, 2, new byte[] { 90, 250, 50, 100 });
        // Expected: max(0, a - b) = { 10, 0, 0, 0 }

        var visualizer = new AttemptBundleVisualizer();
        var src = visualizer.RenderDeviation(a, b);

        src.PixelWidth.Should().Be(2);
        src.PixelHeight.Should().Be(2);
        var pixels = new byte[4];
        src.CopyPixels(pixels, stride: 2, offset: 0);
        pixels.Should().Equal(10, 0, 0, 0);
    }
}
