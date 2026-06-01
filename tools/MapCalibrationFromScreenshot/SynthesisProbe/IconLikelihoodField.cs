using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class IconLikelihoodField
{
    /// <summary>
    /// Builds the per-type likelihood field L_t for <paramref name="template"/> by:
    /// 1. Computing the additive deviation D = max(0, screenshot - alignedBase) — the
    ///    positive residual layer where icons are drawn over terrain.
    /// 2. Sliding <paramref name="template"/>'s gray+alpha mask across D with
    ///    alpha-masked NCC (no threshold, no NMS), producing a dense score in [-1,1]
    ///    at every pixel.
    /// </summary>
    /// <returns>Row-major [H, W] array of NCC scores; unscored border pixels = 0.</returns>
    public static double[,] Build(GrayImage screenshot, GrayImage alignedBase, IconTemplate template)
    {
        if (screenshot.Width != alignedBase.Width || screenshot.Height != alignedBase.Height)
            throw new ArgumentException("screenshot and aligned base must have matching dimensions");

        int w = screenshot.Width, h = screenshot.Height;
        var deviation = new byte[w * h];
        for (int i = 0; i < deviation.Length; i++)
        {
            int d = screenshot.Pixels[i] - alignedBase.Pixels[i];
            deviation[i] = d > 0 ? (byte)Math.Min(255, d) : (byte)0;
        }
        var devImage = new GrayImage(w, h, deviation);

        return ScoreAll(devImage, template);
    }

    /// <summary>
    /// Alpha-masked NCC of <paramref name="template"/> slid over <paramref name="image"/>.
    /// Public so downstream tasks (e.g. probe scoring) can call it directly without
    /// going through the deviation step.
    /// </summary>
    /// <returns>Row-major [H, W] dense score array; border pixels that can't fit the
    /// template are left at 0.</returns>
    public static double[,] ScoreAll(GrayImage image, IconTemplate template)
    {
        int W = image.Width, H = image.Height;
        int tw = template.Gray.Width, th = template.Gray.Height;
        int ax = (int)Math.Round(template.PivotX * tw);
        int ay = (int)Math.Round(template.PivotY * th);

        // Pre-compute template statistics over opaque pixels (alpha >= 128).
        double tSum = 0;
        int opaqueCount = 0;
        for (int i = 0; i < tw * th; i++)
        {
            if (template.Alpha.Pixels[i] < 128) continue;
            tSum += template.Gray.Pixels[i];
            opaqueCount++;
        }
        if (opaqueCount == 0) return new double[H, W];

        double tMean = tSum / opaqueCount;
        double tVar = 0;
        for (int i = 0; i < tw * th; i++)
        {
            if (template.Alpha.Pixels[i] < 128) continue;
            double d = template.Gray.Pixels[i] - tMean;
            tVar += d * d;
        }
        double tStd = Math.Sqrt(tVar);
        if (tStd < 1e-9) return new double[H, W];

        var field = new double[H, W];

        // Parallelise outer rows — each (cx, cy) write is independent.
        Parallel.For(0, H, cy =>
        {
            int y0 = cy - ay;
            if (y0 < 0 || y0 + th > H) return;
            for (int cx = 0; cx < W; cx++)
            {
                int x0 = cx - ax;
                if (x0 < 0 || x0 + tw > W) continue;

                // Window mean over opaque pixels.
                double iSum = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    int srcRow = (y0 + ty) * W + x0;
                    int alphaRow = ty * tw;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        if (template.Alpha.Pixels[alphaRow + tx] < 128) continue;
                        iSum += image.Pixels[srcRow + tx];
                    }
                }
                double iMean = iSum / opaqueCount;

                // Covariance and window variance.
                double iVar = 0, cov = 0;
                for (int ty = 0; ty < th; ty++)
                {
                    int srcRow = (y0 + ty) * W + x0;
                    int tplRow = ty * tw;
                    for (int tx = 0; tx < tw; tx++)
                    {
                        if (template.Alpha.Pixels[tplRow + tx] < 128) continue;
                        double di = image.Pixels[srcRow + tx] - iMean;
                        double dt = template.Gray.Pixels[tplRow + tx] - tMean;
                        iVar += di * di;
                        cov += di * dt;
                    }
                }
                double iStd = Math.Sqrt(iVar);
                field[cy, cx] = (iStd < 1e-9) ? 0.0 : cov / (iStd * tStd);
            }
        });
        return field;
    }
}
