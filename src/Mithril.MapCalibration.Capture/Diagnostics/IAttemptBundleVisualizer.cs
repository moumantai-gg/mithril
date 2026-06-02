using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Seam for annotated-visualization rendering (deviation map, detections overlay,
/// projection overlay). Implementations MUST be WPF-only (DrawingVisual +
/// RenderTargetBitmap + PngBitmapEncoder) — no System.Drawing (#921 guard).
/// </summary>
public interface IAttemptBundleVisualizer
{
    /// <summary>
    /// Per-pixel max(0, screenshot − baseTexture) encoded as Gray8.
    /// Throws <see cref="System.ArgumentException"/> when dimensions differ.
    /// </summary>
    BitmapSource RenderDeviation(GrayImage screenshot, GrayImage baseTexture);

    /// <summary>
    /// Gray screenshot with cyan detection rects, red anchor crosses, and score labels.
    /// </summary>
    BitmapSource RenderDetectionsOverlay(
        GrayImage gray,
        IReadOnlyList<TypedDetection> detections,
        int renderSizePx);

    /// <summary>
    /// Color screenshot with yellow projected-reference crosses and green inlier rects.
    /// </summary>
    BitmapSource RenderProjectionOverlay(
        CapturedFrame rawColor,
        MapRect mapRect,
        AreaCalibration calibration,
        IReadOnlyList<LandmarkReference> references,
        IReadOnlyList<TypeAwareRansacSolver.AssignedReference> inliers,
        int renderSizePx);
}
