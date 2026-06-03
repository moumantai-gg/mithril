using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Resizes raw icon templates (PG ships them at native sprite resolution,
/// ~256&#160;px) down to the single on-screen pixel size PG renders all map icons
/// at. Single-scale NCC only matches when the template and the rendered icon are
/// the same size, so this downscale is a prerequisite for any real-asset
/// detection — without it a 256&#160;px template can never correlate against a
/// ~16&#160;px rendered icon (and, in the blob detector, is simply larger than the
/// blob crop and skipped entirely).
///
/// <para>The synthetic end-to-end test authors its templates already at render
/// size (~24&#160;px), so its detection works without scaling; the
/// <see cref="SelectRenderSize"/> ladder only engages when the templates are
/// large (PG artwork). Lifted from the gate-study
/// <c>ScreenshotCalibrator.SelectGlobalRenderSize</c> / <c>DetectIconsByType</c>
/// (mithril#916 — the lift had ported this into
/// <see cref="WholeImageTemplateDetector"/> but not the deviation-blob detector,
/// so the real-asset replay returned zero detections).</para>
/// </summary>
public static class IconRenderScaler
{
    /// <summary>
    /// Render-size ladder swept when templates are large. PG renders map icons at
    /// a single consistent on-screen size; the swept value with the strongest
    /// aggregate NCC evidence is chosen.
    /// </summary>
    public static readonly int[] DefaultRenderSizeLadder = [12, 16, 20, 24, 30, 40, 56];

    /// <summary>Templates above this max dimension trigger a render-size search.</summary>
    public const int ScaleSearchThresholdPx = 64;

    /// <summary>
    /// Resize <paramref name="templates"/> to the chosen global render size,
    /// preserving each template's aspect ratio (max-dim scaled to the chosen
    /// size). Returns the templates unchanged when they're already small enough
    /// (the synthetic-fixture path). <paramref name="threshold"/> is the NCC
    /// acceptance used when scoring ladder candidates.
    /// </summary>
    public static IReadOnlyList<IconTemplate> RenderSized(
        GrayImage screenshot, IReadOnlyList<IconTemplate> templates, double threshold,
        int? pinnedSize = null, int[]? ladder = null)
    {
        int maxTemplateDim = 0;
        foreach (var t in templates) maxTemplateDim = Math.Max(maxTemplateDim, Math.Max(t.Gray.Width, t.Gray.Height));
        if (maxTemplateDim <= ScaleSearchThresholdPx)
        {
            return templates; // already at render size (synthetic fixtures)
        }

        // Prefer the caller's pinned render size (the gate-study recipe pins 16);
        // fall back to the aggregate-NCC sweep only when unset.
        int chosen = pinnedSize ?? SelectRenderSize(screenshot, templates, ladder ?? DefaultRenderSizeLadder, threshold);

        var scaled = new List<IconTemplate>(templates.Count);
        foreach (var t in templates)
        {
            var (rw, rh) = ScaledDims(t.Gray.Width, t.Gray.Height, chosen);
            var grayD = (rw == t.Gray.Width && rh == t.Gray.Height) ? t.Gray : ImageOps.Resize(t.Gray, rw, rh);
            var alphaD = (rw == t.Alpha.Width && rh == t.Alpha.Height) ? t.Alpha : ImageOps.Resize(t.Alpha, rw, rh);
            scaled.Add(t with { Gray = grayD, Alpha = alphaD });
        }
        return scaled;
    }

    /// <summary>
    /// Pick the render size (from <paramref name="candidates"/>) whose resized
    /// templates produce the strongest aggregate best-match NCC score against the
    /// screenshot. PG renders every map icon at one size, so a single global
    /// choice gives the solver consistent geometry.
    /// </summary>
    public static int SelectRenderSize(
        GrayImage screenshot, IReadOnlyList<IconTemplate> templates, int[] candidates, double threshold)
    {
        if (candidates.Length == 1) return candidates[0];

        int best = candidates[0];
        double bestEvidence = double.NegativeInfinity;
        foreach (var target in candidates)
        {
            double evidence = 0;
            foreach (var t in templates)
            {
                var (rw, rh) = ScaledDims(t.Gray.Width, t.Gray.Height, target);
                var grayD = ImageOps.Resize(t.Gray, rw, rh);
                var alphaD = ImageOps.Resize(t.Alpha, rw, rh);
                var top = NccTemplateMatch.FindBest(screenshot, grayD, alphaD, threshold);
                if (top is null) continue;
                evidence += top.Value.Score;
            }
            if (evidence > bestEvidence)
            {
                bestEvidence = evidence;
                best = target;
            }
        }
        return best;
    }

    /// <summary>Aspect-preserving (max-dim) scaling of (w, h) to a target size.</summary>
    public static (int W, int H) ScaledDims(int width, int height, int target)
    {
        int maxDim = Math.Max(width, height);
        int rw = Math.Max(1, width * target / maxDim);
        int rh = Math.Max(1, height * target / maxDim);
        return (rw, rh);
    }
}
