using System;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Per-pixel local normalised cross-correlation between an in-game map
/// screenshot and the icon-free base texture, aligned to the same extent. The
/// base map texture is icon-free; the screenshot draws the same artwork with
/// icons (and fog) on top. Terrain matches with high local NCC even though PG
/// restyles/tints the in-game map (NCC is invariant to per-window linear
/// brightness/contrast); an icon disrupts the local match → low NCC → "added
/// content" candidate. The deviation value reported is <c>1 - ncc</c> clamped to
/// [0, 1], computed in O(WH) via integral images (independent of window size).
///
/// <para>Lifted verbatim from the gate-study <c>MapTextureDeviationProbe</c>'s
/// <c>LocalNcc</c>. BCL-only.</para>
/// </summary>
public static class LocalNccDeviation
{
    /// <summary>
    /// Converts a single-channel <see cref="GrayImage"/> to a float buffer. The
    /// gray byte is already a BT.601 luma (the decoder produced it); this just
    /// widens to <c>float[]</c> so callers feed <see cref="GrayImage"/> rather
    /// than a raw BGRA buffer.
    /// </summary>
    public static float[] ToGrayFloat(GrayImage img)
    {
        var g = new float[img.Width * img.Height];
        for (int i = 0; i < g.Length; i++) g[i] = img.Pixels[i];
        return g;
    }

    /// <summary>
    /// a = screenshot, b = aligned texture. addedOnly: only flag deviation where the
    /// SCREENSHOT carries the structure (va high) — i.e. content ADDED on the
    /// screenshot side (icons, labels). Regions where the texture is detailed but the
    /// screenshot has flattened it (fog-of-war — e.g. Kur's two unexplored patches,
    /// a mottled overlay that the blob shape-filter's "fog" class misses) are
    /// "obscured", not added, and hold no detectable icon, so they're treated as a
    /// match. Distinguishes added-content from obscured-content by which side the
    /// detail is on — the correct fog discriminator (shape isn't).
    /// </summary>
    public static float[] DeviationMap(float[] a, float[] b, int w, int h, int win, out double meanNcc, bool addedOnly = false)
    {
        int r = win / 2;
        double[] ia = Integral(a, w, h);
        double[] ib = Integral(b, w, h);
        double[] iaa = IntegralOf(a, a, w, h);
        double[] ibb = IntegralOf(b, b, w, h);
        double[] iab = IntegralOf(a, b, w, h);

        const double flatVar = 3.0;   // below this variance a window has no structure
        const double eps = 1e-6;
        var dev = new float[w * h];
        double nccSum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r);
                int x1 = Math.Min(w - 1, x + r), y1 = Math.Min(h - 1, y + r);
                double n = (x1 - x0 + 1) * (double)(y1 - y0 + 1);
                double sa = Box(ia, w, x0, y0, x1, y1);
                double sb = Box(ib, w, x0, y0, x1, y1);
                double saa = Box(iaa, w, x0, y0, x1, y1);
                double sbb = Box(ibb, w, x0, y0, x1, y1);
                double sab = Box(iab, w, x0, y0, x1, y1);
                double ma = sa / n, mb = sb / n;
                double va = saa / n - ma * ma;
                double vb = sbb / n - mb * mb;
                double cov = sab / n - ma * mb;

                double ncc;
                if (va < flatVar && vb < flatVar) ncc = 1.0;               // both featureless -> terrain match
                else if (va < flatVar) ncc = addedOnly ? 1.0 : 0.0;        // screenshot smooth, texture detailed -> OBSCURED (fog/grey blob): match if addedOnly
                else if (vb < flatVar) ncc = 0.0;                          // screenshot detailed, texture smooth -> ADDED (icon-like)
                else
                {
                    ncc = cov / Math.Sqrt(va * vb + eps);
                    // both sides textured but uncorrelated: an added icon raises the
                    // screenshot variance above the terrain it covers (va > vb); an
                    // obscured patch does the opposite. In addedOnly, only count the
                    // former as deviation.
                    if (addedOnly && ncc < 0.5 && va <= vb) ncc = 1.0;
                }

                ncc = Math.Clamp(ncc, -1, 1);
                nccSum += ncc;
                dev[y * w + x] = (float)Math.Clamp(1.0 - ncc, 0, 1);
            }
        meanNcc = nccSum / (w * h);
        return dev;
    }

    private static double[] Integral(float[] src, int w, int h)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += src[y * w + x];
                ii[(y + 1) * (w + 1) + (x + 1)] = ii[y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        return ii;
    }

    private static double[] IntegralOf(float[] a, float[] b, int w, int h)
    {
        var ii = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += (double)a[y * w + x] * b[y * w + x];
                ii[(y + 1) * (w + 1) + (x + 1)] = ii[y * (w + 1) + (x + 1)] + rowSum;
            }
        }
        return ii;
    }

    // Inclusive box sum [x0..x1]x[y0..y1] from an integral image of width (w+1).
    private static double Box(double[] ii, int w, int x0, int y0, int x1, int y1)
    {
        int W = w + 1;
        return ii[(y1 + 1) * W + (x1 + 1)] - ii[y0 * W + (x1 + 1)] - ii[(y1 + 1) * W + x0] + ii[y0 * W + x0];
    }
}
