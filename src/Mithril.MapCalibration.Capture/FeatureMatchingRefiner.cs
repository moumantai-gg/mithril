using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapRegionRefiner"/> using ORB + BFMatcher + Lowe-ratio +
/// <see cref="Cv2.EstimateAffinePartial2D(InputArray, InputArray, OutputArray, RobustEstimationAlgorithms, double, ulong, double, ulong)"/>
/// (RANSAC similarity). Replaces the NCC scale ladder + ECC sub-pixel
/// refine: robust to fog of war, pin overlay, and non-coastline maps
/// (spec §"The criterion that rules approaches in/out").
///
/// <para><b>Output transform direction.</b> <c>EstimateAffinePartial2D</c>
/// is called with <c>texturePoints → screenshotPoints</c>, so the recovered
/// 2×3 affine maps a texture-pixel to its position in the captured frame.
/// The four texture corners run through the affine become the four corners
/// of the located rect in the frame.</para>
///
/// <para><b>Axis-alignment assumption.</b> Under PG's axis-aligned UI the
/// recovered rotation is ~0°. We assert this via the
/// <see cref="MapCalibrationLocateOptions.MaxRotationDegrees"/> gate; any
/// fit beyond that threshold is rejected as "no fit", not "rotated fit",
/// because every downstream consumer (crop, resize, ScreenshotToTexture)
/// assumes axis-alignment.</para>
///
/// <para><b>Fail-soft.</b> A <see cref="OpenCVException"/> from any
/// OpenCvSharp call returns <see cref="MapRegionRefineResult.None"/>; the
/// engine sees the same shape as "no fit found", surfaces the
/// rejected-map-not-located outcome.</para>
/// </summary>
public sealed class FeatureMatchingRefiner : IMapRegionRefiner
{
    private readonly MapCalibrationLocateOptions _options;
    private readonly ILogger? _logger;

    public FeatureMatchingRefiner(
        MapCalibrationLocateOptions options,
        ILogger<FeatureMatchingRefiner>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore)
    {
        // The minScore arg is a leftover from the NCC interface and is
        // ignored by FM — PR-3 drops it from IMapRegionRefiner entirely.
        // The gate that matters lives in _options.
        _ = minScore;

        try
        {
            using var orb = ORB.Create(nFeatures: _options.OrbNFeatures);
            using var capMat = ToMat8U(capturedGray);
            using var texMat = ToMat8U(baseTexture);

            using var capDescriptors = new Mat();
            using var texDescriptors = new Mat();
            orb.DetectAndCompute(capMat, null, out var capKeypoints, capDescriptors);
            orb.DetectAndCompute(texMat, null, out var texKeypoints, texDescriptors);

            if (capDescriptors.Rows < 2 || texDescriptors.Rows < 2)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: too few descriptors (capture={CapCount}, texture={TexCount}).",
                    capDescriptors.Rows, texDescriptors.Rows);
                return MapRegionRefineResult.None;
            }

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            // texture descriptors are the "train" set; capture descriptors are the "query".
            // We want texture keypoints → screenshot keypoints, so the match queues map
            // capture→texture; we re-pair them below.
            var knn = matcher.KnnMatch(capDescriptors, texDescriptors, k: 2);

            // Lowe ratio: keep m if m.distance < ratio * second.distance.
            var loweRatio = _options.LoweRatio;
            var goodPairs = knn
                .Where(pair => pair.Length == 2 && pair[0].Distance < loweRatio * pair[1].Distance)
                .Select(pair => pair[0])
                .ToList();

            if (goodPairs.Count < 4)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: only {GoodCount} Lowe survivors (need ≥4).",
                    goodPairs.Count);
                return MapRegionRefineResult.None;
            }

            // EstimateAffinePartial2D direction: src → dst = texture → capture.
            var texPoints = goodPairs.Select(m => texKeypoints[m.TrainIdx].Pt).ToArray();
            var capPoints = goodPairs.Select(m => capKeypoints[m.QueryIdx].Pt).ToArray();

            using var srcMat = InputArray.Create(texPoints);
            using var dstMat = InputArray.Create(capPoints);
            using var inlierMask = new Mat();
            using var affine = Cv2.EstimateAffinePartial2D(
                srcMat, dstMat,
                inlierMask,
                method: RobustEstimationAlgorithms.RANSAC,
                ransacReprojThreshold: _options.RansacReprojectionThresholdPx,
                maxIters: 2000UL,
                confidence: 0.99,
                refineIters: 10UL);

            if (affine is null || affine.Empty())
            {
                _logger?.LogInformation("Feature-matching locate: RANSAC did not converge.");
                return MapRegionRefineResult.None;
            }

            // Decompose 2×3 partial-affine: [a -b tx; b a ty]
            float a = (float)affine.At<double>(0, 0);
            float b = (float)affine.At<double>(1, 0);
            float tx = (float)affine.At<double>(0, 2);
            float ty = (float)affine.At<double>(1, 2);
            double scale = Math.Sqrt(a * (double)a + b * (double)b);
            double rotationRadians = Math.Atan2(b, a);
            double rotationDegrees = rotationRadians * 180.0 / Math.PI;

            int candidateCount = goodPairs.Count;
            int inlierCount = CountNonZero(inlierMask);
            double inlierRatio = candidateCount == 0 ? 0.0 : (double)inlierCount / candidateCount;
            double residualPixels = ComputeMedianResidual(
                texPoints, capPoints, inlierMask, a, b, tx, ty);

            // Texture corners → screenshot corners. Under axis-aligned PG UI the
            // four-corner image is an axis-aligned rect; we read off origin + size.
            var (originX, originY, width, height) = RectFromCorners(
                baseTexture.Width, baseTexture.Height, a, b, tx, ty);

            var rawFit = new MapRect(
                OriginX: originX, OriginY: originY,
                Width: width, Height: height,
                TextureWidth: baseTexture.Width,
                TextureHeight: baseTexture.Height);

            var metrics = new LocateMetrics(
                InlierCount: inlierCount,
                CandidateCount: candidateCount,
                InlierRatio: inlierRatio,
                Scale: scale,
                RotationDegrees: rotationDegrees,
                Mirror: false,                            // AffinePartial2D never flips
                Tx: tx, Ty: ty,
                ResidualPixels: residualPixels);

            // Gate
            string? rejectReason =
                inlierCount < _options.InlierFloor
                    ? $"inliers={inlierCount} < floor={_options.InlierFloor}"
                : inlierRatio < _options.InlierRatioFloor
                    ? $"ratio={inlierRatio:0.000} < floor={_options.InlierRatioFloor:0.00}"
                : Math.Abs(rotationDegrees) > _options.MaxRotationDegrees
                    ? $"|rotation|={Math.Abs(rotationDegrees):0.000}° > max={_options.MaxRotationDegrees:0.00}°"
                : null;

            if (rejectReason is not null)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: rejected — {Reason}. "
                    + "(inliers={Inliers}/{Candidates} ratio={Ratio:0.000} scale={Scale:0.000} rot={Rot:0.000}°)",
                    rejectReason, inlierCount, candidateCount, inlierRatio, scale, rotationDegrees);
                return new MapRegionRefineResult(AcceptedRect: null, RawFitRect: rawFit, Metrics: metrics);
            }

            return new MapRegionRefineResult(AcceptedRect: rawFit, RawFitRect: rawFit, Metrics: metrics);
        }
        catch (OpenCVException ex)
        {
            _logger?.LogWarning(ex, "Feature-matching locate: OpenCV failure. Safe-degrade.");
            return MapRegionRefineResult.None;
        }
    }

    private static Mat ToMat8U(GrayImage g)
    {
        // Caller owns lifetime; we copy so Mat is independently disposable.
        return Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels).Clone();
    }

    private static int CountNonZero(Mat mask)
    {
        // 1×N or N×1 8U mask from RANSAC; nonzero entries are inliers.
        return Cv2.CountNonZero(mask);
    }

    private static double ComputeMedianResidual(
        Point2f[] texPoints, Point2f[] capPoints, Mat inlierMask,
        float a, float b, float tx, float ty)
    {
        // Median per-inlier ||T·p_T − p_S|| in screenshot pixels (spec §"Open
        // questions" — median chosen for robustness to RANSAC-tolerated tail).
        var residuals = new List<double>(texPoints.Length);
        for (int i = 0; i < texPoints.Length; i++)
        {
            if (inlierMask.At<byte>(i, 0) == 0) continue;
            double projX = a * texPoints[i].X - b * texPoints[i].Y + tx;
            double projY = b * texPoints[i].X + a * texPoints[i].Y + ty;
            double dx = projX - capPoints[i].X;
            double dy = projY - capPoints[i].Y;
            residuals.Add(Math.Sqrt(dx * dx + dy * dy));
        }
        if (residuals.Count == 0) return 0;
        residuals.Sort();
        return residuals.Count % 2 == 1
            ? residuals[residuals.Count / 2]
            : (residuals[residuals.Count / 2 - 1] + residuals[residuals.Count / 2]) * 0.5;
    }

    /// <summary>
    /// Project the texture's four corners through the recovered affine and
    /// read off the axis-aligned bounding box in screenshot space. Under PG's
    /// axis-aligned UI this is tight (the rotation gate caught everything
    /// else); under a small residual rotation that escaped the gate by being
    /// just under threshold, the bbox is the tightest conservative carrier.
    /// </summary>
    private static (int OriginX, int OriginY, int Width, int Height) RectFromCorners(
        int textureWidth, int textureHeight, float a, float b, float tx, float ty)
    {
        static void Project(double x, double y, float a, float b, float tx, float ty, out double px, out double py)
        {
            px = a * x - b * y + tx;
            py = b * x + a * y + ty;
        }
        Project(0, 0, a, b, tx, ty, out var x0, out var y0);
        Project(textureWidth, 0, a, b, tx, ty, out var x1, out var y1);
        Project(0, textureHeight, a, b, tx, ty, out var x2, out var y2);
        Project(textureWidth, textureHeight, a, b, tx, ty, out var x3, out var y3);
        double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return (
            (int)Math.Round(minX),
            (int)Math.Round(minY),
            (int)Math.Round(maxX - minX),
            (int)Math.Round(maxY - minY));
    }
}
