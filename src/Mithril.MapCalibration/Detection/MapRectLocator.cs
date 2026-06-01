using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Finds where the area map's rendered window sits inside a screenshot.
/// <see cref="MapRect"/> is the visible map's bounding box in screenshot
/// pixels; combined with the texture's known dimensions it gives the
/// screenshot↔texture transform under the v1 assumption (player has zoomed
/// the in-game map all the way out so the whole texture fits in view, pan = 0).
///
/// <para>Auto-detect attempts NCC of the downsampled area map against the
/// downsampled screenshot at several scales; if the best match's confidence is
/// below the threshold, the caller is expected to fall back to a user-supplied
/// region. Zoomed-in screenshots (where the visible map is only a pan window)
/// defeat this v1 approach — the user must zoom out first.</para>
///
/// <para>Lifted from the gate-study tool; the per-scale console diagnostics were
/// dropped (a library doesn't write to stdout).</para>
/// </summary>
public static class MapRectLocator
{
    /// <summary>
    /// Default working-resolution long-edge (px) for <see cref="AutoDetect(GrayImage, GrayImage, double, int)"/>.
    /// Both the captured frame and the base texture are box-downsampled so their
    /// long edge is at most this many pixels before the scale-ladder NCC runs, then
    /// the resulting <see cref="MapRect"/> is scaled back to full-capture pixels.
    ///
    /// <para><b>Why 384.</b> The live path hands native-resolution inputs (a ~1257×1049
    /// capture vs a 2048×2033 base texture). Each ladder rung is a full sliding-window
    /// NCC of the (downsampled) texture over the capture — O(capture · template) per
    /// rung — so at native resolution one attempt is ~1–2e12 multiply-adds and the
    /// whole ladder takes minutes (#966). Working at a 384px long edge shrinks both
    /// images by ~3–5× each ⇒ ~(3·5)²≈1000× fewer ops ⇒ sub-second, while leaving
    /// enough structure for the gradient/landmark layout to register. The recovered
    /// origin/size carry sub-pixel error after the unscale, which is well inside the
    /// 15px RANSAC inlier gate (the sole <see cref="MapRect"/> consumer), and the
    /// scale quantisation is removed by the parabolic <see cref="MapRect.SourceScaleFactor"/>
    /// refinement (Task 2). Lower values start to lose the layout; higher values
    /// re-introduce the cost without precision the inlier gate can use.</para>
    /// </summary>
    public const int DefaultWorkingLongEdgePx = 384;

    /// <summary>
    /// Tries NCC across a coarse scale ladder. Returns null on weak/no match.
    /// Scales chosen from typical PG map UI sizes — the rendered map fills
    /// 30%-80% of a 1080p screenshot's smaller dimension at the zoom levels
    /// most people screenshot at.
    ///
    /// <para>This overload runs the ladder at the <paramref name="screenshot"/>'s and
    /// <paramref name="texture"/>'s native resolution. Live callers should prefer the
    /// downsampling overload (<see cref="AutoDetect(GrayImage, GrayImage, double, int)"/>),
    /// which is the #966 unblock; this overload is retained for the synthetic tests and
    /// for callers that have already coarsened their inputs.</para>
    /// </summary>
    public static MapRect? AutoDetect(GrayImage screenshot, GrayImage texture, double minScore)
    {
        // Candidate downsample factors for the texture — each gives a different
        // "rendered map size" hypothesis. The match's score peak picks the
        // closest hypothesis. Fractional factors are essential when the user
        // zooms the in-game map all the way out so the entire texture fits
        // exactly in the visible window — the right factor is ~texture/screen
        // and integer-only resampling skips past it.
        var candidates = BuildCandidateScales(screenshot, texture);
        if (candidates.Count == 0) return null;

        // Per-rung peak NCC score, aligned with the candidate index, so the Task-2
        // parabola can read the best rung's neighbours without re-scoring. NaN marks
        // a rung that produced no hit (template didn't fit, etc.).
        var rungScores = new double[candidates.Count];

        int bestIndex = -1;
        MapRect? bestRect = null;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            var (factor, downsampledTexture) = candidates[i];
            var hit = NccTemplateMatch.FindBest(screenshot, downsampledTexture, templateMask: null, minScore: -1.0);
            if (hit is null)
            {
                rungScores[i] = double.NaN;
                continue;
            }
            rungScores[i] = hit.Value.Score;
            if (hit.Value.Score > bestScore)
            {
                bestScore = hit.Value.Score;
                bestIndex = i;
                bestRect = new MapRect(
                    OriginX: hit.Value.X,
                    OriginY: hit.Value.Y,
                    Width: downsampledTexture.Width,
                    Height: downsampledTexture.Height,
                    TextureWidth: texture.Width,
                    TextureHeight: texture.Height,
                    AutoDetectScore: hit.Value.Score,
                    SourceScaleFactor: factor);
            }
        }

        if (bestRect is null || bestScore < minScore) return null;

        // Task 2 (#966): the discrete ladder quantises SourceScaleFactor to ±2.5–5%.
        // Fit a parabola through the best rung and its two ladder neighbours on the
        // NCC-score curve to recover a continuous peak scale, removing the rung
        // snapping. Guarded for ladder ends + a degenerate/flat fit (keep discrete).
        double refinedFactor = RefineScaleFactor(candidates, rungScores, bestIndex);
        return bestRect with { SourceScaleFactor = refinedFactor };
    }

    /// <summary>
    /// Live-path overload (#966): box-downsamples both inputs so their long edge is at
    /// most <paramref name="workingLongEdgePx"/>, runs the scale-ladder NCC at that
    /// working resolution, then scales the resulting <see cref="MapRect"/> back to
    /// full-<paramref name="screenshot"/> coordinates.
    ///
    /// <para>Downsampling is a no-op for any input whose long edge is already at or
    /// under the working size — inputs are never upsampled, so a caller that hands
    /// pre-coarsened images (the synthetic tests) behaves exactly like the native
    /// overload. The capture and texture downsample ratios are tracked independently;
    /// the returned origin/size are unscaled by the <i>capture's</i> ratio (they are in
    /// capture pixels), while <see cref="MapRect.TextureWidth"/>/<see cref="MapRect.TextureHeight"/>
    /// stay at the FULL texture dimensions so the texture↔screenshot transform is
    /// unaffected.</para>
    /// </summary>
    public static MapRect? AutoDetect(
        GrayImage screenshot, GrayImage texture, double minScore, int workingLongEdgePx)
    {
        if (workingLongEdgePx <= 0) throw new ArgumentOutOfRangeException(nameof(workingLongEdgePx));

        var (workScreenshot, captureRatio) = DownsampleToLongEdge(screenshot, workingLongEdgePx);
        var (workTexture, _) = DownsampleToLongEdge(texture, workingLongEdgePx);

        var rect = AutoDetect(workScreenshot, workTexture, minScore);
        if (rect is null) return null;

        // Unscale origin/size from working-capture pixels back to full-capture
        // pixels. TextureWidth/Height must remain the FULL texture dimensions so the
        // ScreenshotToTexture transform maps into native texture space — restore them
        // explicitly (the ladder filled them with the *working* texture dims).
        return rect with
        {
            OriginX = (int)Math.Round(rect.OriginX * captureRatio),
            OriginY = (int)Math.Round(rect.OriginY * captureRatio),
            Width = (int)Math.Round(rect.Width * captureRatio),
            Height = (int)Math.Round(rect.Height * captureRatio),
            TextureWidth = texture.Width,
            TextureHeight = texture.Height,
        };
    }

    /// <summary>
    /// Box-downsamples <paramref name="src"/> so its long edge is at most
    /// <paramref name="longEdgePx"/>, returning the (possibly identical) image and the
    /// ratio that maps a working-resolution coordinate back to a source coordinate
    /// (i.e. <c>source = working * ratio</c>, ratio ≥ 1). A no-op (ratio 1.0) when the
    /// long edge is already at/under the target — never upsamples.
    /// </summary>
    private static (GrayImage Image, double Ratio) DownsampleToLongEdge(GrayImage src, int longEdgePx)
    {
        int longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= longEdgePx) return (src, 1.0);

        // Integer box-average factor (ceil so the long edge lands at/under the floor).
        int factor = (int)Math.Ceiling((double)longEdge / longEdgePx);
        if (factor < 2) factor = 2;
        var down = ImageOps.Downsample(src, factor);
        // The realised ratio is source/working (Downsample truncates, so derive it
        // from the actual produced width rather than the nominal factor).
        double ratio = (double)src.Width / down.Width;
        return (down, ratio);
    }

    /// <summary>
    /// Fits a 1D parabola through the NCC peak scores of the best ladder rung and its
    /// two neighbours and returns the continuous scale factor at the parabola's vertex,
    /// clamped to the neighbouring rungs. Falls back to the discrete rung factor when
    /// the best rung is at a ladder end or the parabola is degenerate/flat (the vertex
    /// would diverge or sit outside the bracket).
    /// </summary>
    private static double RefineScaleFactor(
        IReadOnlyList<(double Factor, double Score)> rungs, int bestIndex)
    {
        if (bestIndex <= 0 || bestIndex >= rungs.Count - 1)
        {
            return rungs[Math.Max(0, bestIndex)].Factor; // ladder end → keep discrete
        }

        // Parameterise the parabola over rung INDEX (-1, 0, +1) where the scores are
        // sampled, then map the sub-index vertex back through the (possibly uneven)
        // factor spacing. y = a·x² + b·x + c with x ∈ {-1, 0, 1}:
        //   a = (y_{-1} + y_{+1})/2 − y_0 ,  b = (y_{+1} − y_{-1})/2
        // vertex at x* = −b / (2a).
        double yL = rungs[bestIndex - 1].Score;
        double y0 = rungs[bestIndex].Score;
        double yR = rungs[bestIndex + 1].Score;

        double a = (yL + yR) * 0.5 - y0;
        double b = (yR - yL) * 0.5;

        // Flat / non-concave peak (a ≈ 0, or a > 0 ⇒ the bracket is a trough, not a
        // peak): the parabola gives no usable vertex → keep the discrete rung.
        if (a >= -1e-9) return rungs[bestIndex].Factor;

        double xStar = -b / (2.0 * a);
        // The true peak must lie within the bracketing rungs; outside means the fit
        // is unreliable (noise) → keep discrete.
        if (xStar < -1.0 || xStar > 1.0 || double.IsNaN(xStar) || double.IsInfinity(xStar))
        {
            return rungs[bestIndex].Factor;
        }

        double fL = rungs[bestIndex - 1].Factor;
        double f0 = rungs[bestIndex].Factor;
        double fR = rungs[bestIndex + 1].Factor;
        // Linearly interpolate the factor at the sub-index vertex using the relevant
        // (uneven) rung spacing on whichever side of the centre the vertex falls.
        return xStar >= 0 ? f0 + xStar * (fR - f0) : f0 + xStar * (f0 - fL);
    }

    /// <summary>
    /// Maps the candidate ladder + its captured per-rung scores onto the index-space
    /// parabola fit. A neighbour rung with no hit (NaN score) makes the fit
    /// unreliable → keep the discrete best rung.
    /// </summary>
    private static double RefineScaleFactor(
        List<(double Factor, GrayImage Downsampled)> candidates, double[] rungScores, int bestIndex)
    {
        if (bestIndex <= 0 || bestIndex >= candidates.Count - 1)
        {
            return candidates[Math.Max(0, bestIndex)].Factor; // ladder end → keep discrete
        }

        double sL = rungScores[bestIndex - 1];
        double s0 = rungScores[bestIndex];
        double sR = rungScores[bestIndex + 1];
        if (double.IsNaN(sL) || double.IsNaN(s0) || double.IsNaN(sR))
        {
            return candidates[bestIndex].Factor; // a neighbour didn't score → keep discrete
        }

        var trio = new (double Factor, double Score)[]
        {
            (candidates[bestIndex - 1].Factor, sL),
            (candidates[bestIndex].Factor, s0),
            (candidates[bestIndex + 1].Factor, sR),
        };
        return RefineScaleFactor(trio, 1);
    }

    private static List<(double Factor, GrayImage Downsampled)> BuildCandidateScales(
        GrayImage screenshot, GrayImage texture)
    {
        var result = new List<(double, GrayImage)>();
        int sw = screenshot.Width;
        int sh = screenshot.Height;

        // Fractional factors over the full range of plausible map-renders:
        //   - factor ≈ texture/screenshot when the in-game map fills the whole
        //     screenshot (the "zoomed all the way out" case — common, and where
        //     integer-only factors badly bracket the right scale)
        //   - factor in 3..6 when the map UI panel is a smaller region
        // Each candidate is bilinearly resampled so we can test factors like 2.1
        // that integer downsampling can't represent.
        var factors = new double[]
        {
            // Down to 1.0 to cover the "screenshot already at native scale"
            // case (e.g., the synthetic test, or any caller that pre-downsamples
            // both inputs to the same coarse resolution).
            1.0, 1.1, 1.2, 1.35, 1.5, 1.75, 2.0, 2.1, 2.2, 2.4, 2.6, 2.8,
            3.0, 3.25, 3.5, 4.0, 4.5, 5.0, 6.0, 8.0, 10.0,
        };
        foreach (var f in factors)
        {
            int dw = (int)Math.Round(texture.Width / f);
            int dh = (int)Math.Round(texture.Height / f);
            // Template must fit inside the search image; below 25% of screen
            // is unlikely to be a real map render and is dominated by noise.
            if (dw > sw || dh > sh) continue;
            int dsmaller = Math.Min(dw, dh);
            int smaller = Math.Min(sw, sh);
            if (dsmaller < smaller * 0.25) continue;
            result.Add((f, ImageOps.Resize(texture, dw, dh)));
        }
        return result;
    }
}

/// <summary>
/// Visible map's bounding box in the screenshot, plus the source texture's
/// native dimensions. Combined these give the screenshot↔texture transform.
/// </summary>
public sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    double? AutoDetectScore = null,
    double? SourceScaleFactor = null)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return ((sx - OriginX) * scaleX, (sy - OriginY) * scaleY);
    }

    public (double Sx, double Sy) TextureToScreenshot(double tx, double ty)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return (tx / scaleX + OriginX, ty / scaleY + OriginY);
    }
}
