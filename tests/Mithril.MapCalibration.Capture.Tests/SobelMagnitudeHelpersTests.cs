using FluentAssertions;
using Mithril.MapCalibration.Detection.Internal;
using OpenCvSharp;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class SobelMagnitudeHelpersTests
{
    [Fact]
    public void SobelMagnitude8U_returns_8U_single_channel_same_dims_as_input()
    {
        using var src = new Mat(64, 96, MatType.CV_8UC1, new Scalar(128));
        using var mag = SobelMagnitudeHelpers.SobelMagnitude8U(src);

        mag.Type().Should().Be(MatType.CV_8UC1);
        mag.Rows.Should().Be(64);
        mag.Cols.Should().Be(96);
    }

    [Fact]
    public void SobelMagnitude8U_emits_nonzero_response_at_a_vertical_edge()
    {
        using var src = new Mat(32, 32, MatType.CV_8UC1, new Scalar(0));
        // Left half black, right half white → strong vertical edge at x=16.
        Cv2.Rectangle(src, new Rect(16, 0, 16, 32), new Scalar(255), thickness: -1);

        using var mag = SobelMagnitudeHelpers.SobelMagnitude8U(src);
        var indexer = mag.GetGenericIndexer<byte>();

        indexer[16, 16].Should().BeGreaterThan((byte)50, "the edge column should be strongly lit");
        indexer[16, 0].Should().BeLessThan((byte)10, "the flat-black region should be near zero");
    }

    [Fact]
    public void RefineLocationSubPixel_returns_zero_at_a_boundary_peak()
    {
        using var ncc = new Mat(5, 5, MatType.CV_32FC1, new Scalar(0f));
        var (dx, dy) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(0, 0));
        dx.Should().Be(0);
        dy.Should().Be(0);
    }

    [Fact]
    public void RefineLocationSubPixel_finds_a_centered_offset_on_a_symmetric_parabolic_peak()
    {
        using var ncc = new Mat(5, 5, MatType.CV_32FC1, new Scalar(0f));
        var idx = ncc.GetGenericIndexer<float>();
        // Symmetric concave-down peak at (2,2) — vertex offset should be (0,0).
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                idx[y, x] = 1.0f - 0.1f * ((x - 2) * (x - 2) + (y - 2) * (y - 2));

        var (dx, dy) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(2, 2));
        dx.Should().BeApproximately(0.0, 1e-6);
        dy.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void RefineLocationSubPixel_clamps_to_unit_interval()
    {
        using var ncc = new Mat(3, 3, MatType.CV_32FC1, new Scalar(0f));
        var idx = ncc.GetGenericIndexer<float>();
        // Near-flat curvature → would return runaway value pre-clamp.
        idx[1, 0] = 0.50001f; idx[1, 1] = 0.50002f; idx[1, 2] = 0.50000f;
        idx[0, 1] = 0.50001f; idx[2, 1] = 0.50001f;
        var (dx, _) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(1, 1));
        dx.Should().BeInRange(-1.0, 1.0);
    }
}
