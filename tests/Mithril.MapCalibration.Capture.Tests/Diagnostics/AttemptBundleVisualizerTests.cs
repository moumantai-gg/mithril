using System.Windows.Media.Imaging;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
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

    [Fact]
    public void RenderDetectionsOverlay_returns_bitmap_of_input_dims()
    {
        var gray = new GrayImage(32, 24, new byte[32 * 24]);
        var detections = new[]
        {
            new TypedDetection("Portal", "landmark_portal", new CroppedFramePixel(10, 12), 0.91),
            new TypedDetection("Npc", "landmark_npc", new CroppedFramePixel(20, 18), 0.85),
        };

        var visualizer = new AttemptBundleVisualizer();
        var src = visualizer.RenderDetectionsOverlay(gray, detections, renderSizePx: 16);

        src.PixelWidth.Should().Be(32);
        src.PixelHeight.Should().Be(24);
    }

    [Fact]
    public void RenderProjectionOverlay_returns_bitmap_of_input_dims()
    {
        var raw = new CapturedFrame(32, 24, new byte[32 * 24 * 4]);
        var rect = new MapRect(0, 0, 32, 24, 64, 48);
        var cal = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0, OriginX: 16, OriginY: 12,
            ReferenceCount: 1, ResidualPixels: 0.5);
        var refs = new[]
        {
            new LandmarkReference("Portal", "X", new WorldCoord(0, 0, 0)),
        };
        var inliers = new[]
        {
            new TypeAwareRansacSolver.AssignedReference("Portal:X", 0, 0, 16, 12, 0.9),
        };

        var visualizer = new AttemptBundleVisualizer();
        var src = visualizer.RenderProjectionOverlay(raw, rect, cal, refs, inliers, renderSizePx: 16);

        src.PixelWidth.Should().Be(32);
        src.PixelHeight.Should().Be(24);
    }
}
