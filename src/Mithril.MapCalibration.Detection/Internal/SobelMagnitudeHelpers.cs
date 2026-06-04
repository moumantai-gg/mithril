using OpenCvSharp;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Shared OpenCV helpers for the Sobel-padded-pyramid locate fallback
/// (mithril#1061). Extracted into a static class so the refiner stays focused
/// on dispatch + the helpers are unit-testable on synthetic Mats without
/// constructing a refiner instance.
/// </summary>
internal static class SobelMagnitudeHelpers
{
    /// <summary>
    /// Sobel gradient magnitude normalised into 8-bit single-channel range.
    /// Continuous-valued (no binary thresholding) — the round-5 corpus measured
    /// a consistent 1.5–2× NCC strengthening over Canny binary edges.
    /// Caller owns the returned Mat.
    /// </summary>
    public static Mat SobelMagnitude8U(Mat src)
    {
        using var gx = new Mat();
        Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, ksize: 3);
        using var gy = new Mat();
        Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        var dst = new Mat();
        Cv2.Normalize(mag, dst, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        return dst;
    }

    /// <summary>
    /// 2D parabolic peak refinement on an NCC response map at the integer peak.
    /// Fits independent 1D parabolas through each axis's 3-pixel neighborhood
    /// and returns the vertex offsets clamped to ±1 px. Returns (0,0) when the
    /// peak sits on a boundary (no neighbors on one side) or when curvature is
    /// not concave-down on that axis.
    /// </summary>
    public static (double dx, double dy) RefineLocationSubPixel(Mat ncc, Point peakLoc)
    {
        int px = peakLoc.X, py = peakLoc.Y;
        if (px <= 0 || py <= 0 || px >= ncc.Cols - 1 || py >= ncc.Rows - 1)
            return (0, 0);
        var idx = ncc.GetGenericIndexer<float>();
        double c = idx[py, px];
        double left = idx[py, px - 1], right = idx[py, px + 1];
        double up = idx[py - 1, px], down = idx[py + 1, px];
        double denomX = left - 2 * c + right;
        double denomY = up - 2 * c + down;
        double dx = denomX < -1e-9 ? 0.5 * (left - right) / denomX : 0;
        double dy = denomY < -1e-9 ? 0.5 * (up - down) / denomY : 0;
        return (System.Math.Clamp(dx, -1.0, 1.0), System.Math.Clamp(dy, -1.0, 1.0));
    }
}
