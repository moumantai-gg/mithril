using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Hand-rolled normalised cross-correlation (NCC) for template matching against
/// a single-channel byte image. PG's landmark icons are zoom-invariant per
/// issue #852 comment — they render at native pixel size regardless of the
/// in-game map zoom — so a single-scale NCC pass is enough; no image pyramid.
///
/// <para>NCC produces a score in [-1, +1]. Translation-invariant by construction
/// (subtracts per-window mean), illumination-invariant (divides by per-window
/// stddev). Robust to the soft gradient backgrounds PG draws under map icons.</para>
///
/// <para>Alpha-mask aware: pixels whose mask value is 0 (fully transparent
/// template pixels) are skipped from both the mean and the correlation, so a
/// teardrop pin doesn't get scored against the empty corners of its bounding
/// rectangle.</para>
/// </summary>
public static class NccTemplateMatch
{
    /// <summary>
    /// Slides <paramref name="template"/> over <paramref name="image"/> and
    /// returns every position whose NCC score exceeds <paramref name="minScore"/>,
    /// after non-maximum suppression keeps only the local-best in any radius =
    /// <c>max(template.Width, template.Height) / 2</c>.
    /// </summary>
    public static List<Detection> FindAll(
        GrayImage image, GrayImage template, GrayImage? templateMask, double minScore, int? maxResults = null)
    {
        if (template.Width > image.Width || template.Height > image.Height)
        {
            return [];
        }

        var raw = ScoreAll(image, template, templateMask);
        var nms = NonMaxSuppress(raw, image.Width, image.Height, template.Width, template.Height, minScore);
        nms.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (maxResults is { } cap && nms.Count > cap)
        {
            nms.RemoveRange(cap, nms.Count - cap);
        }
        return nms;
    }

    /// <summary>Single best match (or null if no position clears <paramref name="minScore"/>).</summary>
    public static Detection? FindBest(GrayImage image, GrayImage template, GrayImage? templateMask, double minScore)
    {
        var all = FindAll(image, template, templateMask, minScore, maxResults: 1);
        return all.Count == 0 ? null : all[0];
    }

    // ---- internals ---------------------------------------------------------

    private static double[,] ScoreAll(GrayImage image, GrayImage template, GrayImage? mask)
    {
        int tw = template.Width;
        int th = template.Height;

        // Template stats, mask-aware. Compute mean over mask-passing pixels only.
        // Then compute sum-of-squares centred on that mean. mask is treated as a
        // boolean (>= 128 → pass).
        double tSum = 0;
        int tCount = 0;
        for (int ty = 0; ty < th; ty++)
        {
            for (int tx = 0; tx < tw; tx++)
            {
                if (mask is not null && mask.Pixels[ty * tw + tx] < 128) continue;
                tSum += template.Pixels[ty * tw + tx];
                tCount++;
            }
        }
        if (tCount == 0)
        {
            return new double[image.Width, image.Height];
        }
        double tMean = tSum / tCount;
        double tSq = 0;
        for (int ty = 0; ty < th; ty++)
        {
            for (int tx = 0; tx < tw; tx++)
            {
                if (mask is not null && mask.Pixels[ty * tw + tx] < 128) continue;
                var v = template.Pixels[ty * tw + tx] - tMean;
                tSq += v * v;
            }
        }
        double tNorm = Math.Sqrt(tSq);
        if (tNorm < 1e-9)
        {
            // Template is a constant image; NCC undefined.
            return new double[image.Width, image.Height];
        }

        int sw = image.Width - tw + 1;
        int sh = image.Height - th + 1;
        var scores = new double[image.Width, image.Height];

        // Parallelise the outer search-position row loop — each (sx, sy) write
        // is independent. Cuts wall time by ~core-count on a modern CPU; the
        // brute-force ~1 billion ops per template stay the same, just spread.
        // Captures (image, template, mask, tw, th, tMean, tNorm, tCount, sw)
        // are all read-only.
        Parallel.For(0, sh, sy =>
        {
            for (int sx = 0; sx < sw; sx++)
            {
                double wSum = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    int srcRow = (sy + ty) * image.Width + sx;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        if (mask is not null && mask.Pixels[ty * tw + tx] < 128) continue;
                        wSum += image.Pixels[srcRow + tx];
                    }
                }
                double wMean = wSum / tCount;

                double cross = 0;
                double wSq = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    int srcRow = (sy + ty) * image.Width + sx;
                    int tplRow = ty * tw;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        if (mask is not null && mask.Pixels[tplRow + tx] < 128) continue;
                        double iv = image.Pixels[srcRow + tx] - wMean;
                        double tv = template.Pixels[tplRow + tx] - tMean;
                        cross += iv * tv;
                        wSq += iv * iv;
                    }
                }
                double wNorm = Math.Sqrt(wSq);
                scores[sx, sy] = (wNorm < 1e-9) ? 0 : cross / (tNorm * wNorm);
            }
        });
        return scores;
    }

    private static List<Detection> NonMaxSuppress(
        double[,] scores, int width, int height, int tw, int th, double minScore)
    {
        // Output point convention: detection (X, Y) is the TOP-LEFT of the match
        // rect; the centre is (X + tw/2, Y + th/2). NMS radius = half the
        // template's larger dimension — collapses overlapping detections of the
        // same icon to a single peak without dropping nearby distinct icons.
        int radius = Math.Max(tw, th) / 2;
        if (radius < 1) radius = 1;
        int sw = width - tw + 1;
        int sh = height - th + 1;

        var hits = new List<Detection>();
        for (int sy = 0; sy < sh; sy++)
        {
            for (int sx = 0; sx < sw; sx++)
            {
                var s = scores[sx, sy];
                if (s < minScore) continue;

                bool isLocalMax = true;
                int y0 = Math.Max(0, sy - radius);
                int y1 = Math.Min(sh - 1, sy + radius);
                int x0 = Math.Max(0, sx - radius);
                int x1 = Math.Min(sw - 1, sx + radius);
                for (int ny = y0; ny <= y1 && isLocalMax; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        if (nx == sx && ny == sy) continue;
                        if (scores[nx, ny] > s) { isLocalMax = false; break; }
                    }
                }
                if (!isLocalMax) continue;

                // Sub-pixel refinement: fit a 1D parabola through the 3 scores
                // straddling the peak on each axis and take its analytical max.
                // For y = ax² + bx + c through s(-1), s(0), s(+1):
                //   sub-pixel offset = (s_left - s_right) / (2 * (s_left - 2 s_centre + s_right))
                // Drops integer-pixel error (~1-2 px RMS) to ~0.1-0.5 px,
                // which dominates the calibration residual in real screenshots.
                // Skip refinement at search edges + when the neighborhood is
                // flat (denominator near zero → no defined peak).
                double subX = sx;
                double subY = sy;
                if (sx > 0 && sx < sw - 1)
                {
                    double sl = scores[sx - 1, sy], sc = s, sr = scores[sx + 1, sy];
                    double denom = sl - 2 * sc + sr;
                    if (Math.Abs(denom) > 1e-9)
                    {
                        double off = (sl - sr) / (2 * denom);
                        if (off > -1 && off < 1) subX = sx + off;
                    }
                }
                if (sy > 0 && sy < sh - 1)
                {
                    double su = scores[sx, sy - 1], sc = s, sd = scores[sx, sy + 1];
                    double denom = su - 2 * sc + sd;
                    if (Math.Abs(denom) > 1e-9)
                    {
                        double off = (su - sd) / (2 * denom);
                        if (off > -1 && off < 1) subY = sy + off;
                    }
                }

                hits.Add(new Detection(sx, sy, s, subX, subY));
            }
        }
        return hits;
    }
}
