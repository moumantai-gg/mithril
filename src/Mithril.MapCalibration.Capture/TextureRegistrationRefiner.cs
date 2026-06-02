using System;
using Mithril.MapCalibration.Detection;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapRegionRefiner"/> over the Phase-1
/// <see cref="MapRectLocator.AutoDetect(GrayImage, GrayImage, double, int)"/>
/// (multi-scale NCC of the base texture against the captured frame) followed by a
/// sub-pixel <b>ECC affine refine</b> (issue #978). The seam exists so the
/// orchestrator depends on an interface rather than a static, and so a
/// Windows.Graphics.Capture-era refiner can swap in.
///
/// <para><b>#966 unblock.</b> The live path hands native-resolution inputs
/// (~1257×1049 capture vs 2048×2033 texture); the native ladder is ~1–2e12
/// multiply-adds and takes minutes. The coarse step runs the search at the
/// <see cref="MapRectLocator.DefaultWorkingLongEdgePx"/> working resolution
/// (~1000× fewer ops ⇒ sub-second) and unscales the resulting
/// <see cref="MapRect"/> back to full-capture coordinates.</para>
///
/// <para><b>#978 precise registration.</b> The coarse locator lands ~10–60px /
/// 2–7% off — at that rect the screenshot↔texture deviation never cancels terrain,
/// so the deviation map floods with terrain false-positives and the real icons
/// drown (RANSAC scraped 3–4 inliers / ~7.6px). After the coarse seed, this refiner
/// runs <see cref="Cv2.FindTransformECC(InputArray, InputArray, InputOutputArray, MotionTypes, TermCriteria, InputArray, int)"/>
/// (Enhanced Correlation Coefficient, affine) seeded from the coarse rect to
/// sub-pixel-align the base texture into the captured frame, then reads the
/// translation + scale back out as the refined <see cref="MapRect"/>.</para>
///
/// <para><b>Fail-soft.</b> ECC throws <see cref="OpenCVException"/> on
/// non-convergence; on that path the refiner returns the coarse seed unchanged so
/// behaviour never regresses below the pre-#978 locator-only path. The exception
/// never propagates into the engine.</para>
/// </summary>
public sealed class TextureRegistrationRefiner : IMapRegionRefiner
{
    // ECC stopping criteria: ported verbatim from the #938 ECC prototype. 200
    // iterations / 1e-6 epsilon converges in ~230–642ms on the real Eltibule frames.
    private const int MaxIterations = 200;
    private const double Epsilon = 1e-6;
    private const int GaussFiltSize = 5;

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore)
    {
        // Always run the threshold-free locator so the coarse seed is preserved on
        // both accept and reject; the threshold gate used to be applied here using
        // the seed's AutoDetectScore field, but Task 13 stripped that field off the
        // MapRect record. The refiner no longer threshold-gates — the engine
        // observes acceptance via AcceptedRect being non-null and triages via the
        // locator metrics (Task 15) rather than via a score baked into the rect.
        _ = minScore;
        var seed = MapRectLocator.AutoDetectBest(
            capturedGray, baseTexture, MapRectLocator.DefaultWorkingLongEdgePx);
        if (seed is null)
        {
            return MapRegionRefineResult.None; // no viable rung — degenerate input
        }

        int sw = seed.Width, sh = seed.Height;

        try
        {
            using var texFull = ToMat32F(baseTexture);
            using var tmpl = new Mat();
            Cv2.Resize(texFull, tmpl, new Size(sw, sh)); // texture at coarse map scale = ECC template
            using var input = ToMat32F(capturedGray);

            using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
            warp.Set(0, 2, (float)seed.OriginX); // init: template origin → seed origin in frame
            warp.Set(1, 2, (float)seed.OriginY);

            var crit = new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, MaxIterations, Epsilon);
            Cv2.FindTransformECC(tmpl, input, warp, MotionTypes.Affine, crit, null, GaussFiltSize);

            float a = warp.At<float>(0, 0), b = warp.At<float>(0, 1), tx = warp.At<float>(0, 2);
            float c = warp.At<float>(1, 0), d = warp.At<float>(1, 1), ty = warp.At<float>(1, 2);
            double scaleX = Math.Sqrt((double)a * a + (double)c * c);
            double scaleY = Math.Sqrt((double)b * b + (double)d * d);

            var refined = new MapRect(
                OriginX: (int)Math.Round(tx),
                OriginY: (int)Math.Round(ty),
                Width: (int)Math.Round(sw * scaleX),
                Height: (int)Math.Round(sh * scaleY),
                TextureWidth: baseTexture.Width,
                TextureHeight: baseTexture.Height);
            return new MapRegionRefineResult(AcceptedRect: refined, BestCoarseRect: seed);
        }
        catch (OpenCVException)
        {
            // Non-convergence (and any other OpenCV-internal failure): fall back to
            // the coarse seed so the engine sees no worse than the pre-#978 behaviour.
            return new MapRegionRefineResult(AcceptedRect: seed, BestCoarseRect: seed);
        }
    }

    /// <summary>
    /// <see cref="GrayImage"/> → single-channel float <see cref="Mat"/>, ported from
    /// the #938 prototype's <c>ToMat32F</c>. The intermediate 8-bit Mat wraps the
    /// pixel buffer; <c>ConvertTo</c> copies into an owned float Mat the caller
    /// disposes.
    /// </summary>
    private static Mat ToMat32F(GrayImage g)
    {
        using var m8 = Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels);
        var m = new Mat();
        m8.ConvertTo(m, MatType.CV_32FC1);
        return m;
    }
}
