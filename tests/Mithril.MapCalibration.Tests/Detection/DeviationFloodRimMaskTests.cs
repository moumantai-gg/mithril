using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class DeviationFloodRimMaskTests
{
    [Fact]
    public void Build_EdgeFloodMasksEdgeTouchingForeground()
    {
        // 8x8 deviation. An L-shape foreground (all 1.0) touches the left edge
        // at column 0. devThr = 0.5 → fg = the L-shape pixels.
        const int W = 8, H = 8;
        var dev = new float[W * H];
        // L-shape: top-left corner cells (0,0)-(0,4) vertical + (0,4)-(3,4) horizontal.
        for (int y = 0; y < 5; y++) dev[y * W + 0] = 1f;       // left column 0..4
        for (int x = 0; x < 4; x++) dev[4 * W + x] = 1f;       // row 4, x=0..3
        // Add one interior "noise" foreground pixel that does NOT touch the edge
        // and is NOT 4-connected to any L-shape pixel.
        // L-shape occupies: (0,0)..(0,4) + (1,4)..(3,4). (5,2) is fully interior.
        dev[2 * W + 5] = 1f;  // (5, 2) — isolated, NOT 4-connected to the L

        var rim = DeviationFloodRimMask.Build(dev, W, H, devThr: 0.5);

        // Every L-shape pixel should be in the rim.
        for (int y = 0; y < 5; y++) rim[y * W + 0].Should().BeTrue($"L vertical at (0,{y})");
        for (int x = 0; x < 4; x++) rim[4 * W + x].Should().BeTrue($"L horizontal at ({x},4)");

        // The isolated interior pixel must NOT be in the rim.
        rim[2 * W + 5].Should().BeFalse("interior pixel not 4-connected to edge");
    }

    [Fact]
    public void Build_IsolatedInteriorForegroundNotMasked()
    {
        // 8x8: a 2x2 high-deviation blob in the dead center + a separate
        // edge-touching strip on the right.
        const int W = 8, H = 8;
        var dev = new float[W * H];
        // Interior 2x2 at (3,3)..(4,4):
        dev[3 * W + 3] = 1f; dev[3 * W + 4] = 1f;
        dev[4 * W + 3] = 1f; dev[4 * W + 4] = 1f;
        // Right-edge strip at column 7, rows 1..3:
        dev[1 * W + 7] = 1f; dev[2 * W + 7] = 1f; dev[3 * W + 7] = 1f;

        var rim = DeviationFloodRimMask.Build(dev, W, H, devThr: 0.5);

        // Interior 2x2 NOT masked.
        rim[3 * W + 3].Should().BeFalse();
        rim[3 * W + 4].Should().BeFalse();
        rim[4 * W + 3].Should().BeFalse();
        rim[4 * W + 4].Should().BeFalse();
        // Right-edge strip IS masked.
        rim[1 * W + 7].Should().BeTrue();
        rim[2 * W + 7].Should().BeTrue();
        rim[3 * W + 7].Should().BeTrue();
    }

    [Fact]
    public void Build_BelowThresholdNotMasked()
    {
        // Same shapes as the previous test but at deviation 0.3, below threshold.
        const int W = 8, H = 8;
        var dev = new float[W * H];
        dev[3 * W + 3] = 0.3f; dev[3 * W + 4] = 0.3f;
        dev[4 * W + 3] = 0.3f; dev[4 * W + 4] = 0.3f;
        dev[1 * W + 7] = 0.3f; dev[2 * W + 7] = 0.3f; dev[3 * W + 7] = 0.3f;

        var rim = DeviationFloodRimMask.Build(dev, W, H, devThr: 0.5);

        // Nothing should be masked because no pixel meets the threshold.
        for (int i = 0; i < W * H; i++) rim[i].Should().BeFalse();
    }
}
