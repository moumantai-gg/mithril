namespace Mithril.MapCalibration.Detection;

public static class IconLikelihoodField
{
    /// <summary>
    /// Builds the per-type likelihood field L_t for <paramref name="template"/> by:
    /// 1. Computing the additive deviation D = max(0, screenshot - alignedBase) — the
    ///    positive residual layer where icons are drawn over terrain.
    /// 2. Sliding <paramref name="template"/>'s gray+alpha mask across D with
    ///    alpha-masked NCC (no threshold, no NMS), producing a dense score in [-1,1]
    ///    at every pixel.
    /// </summary>
    /// <returns>NCC score array. Same row-major [H, W] layout as <see cref="ScoreAll"/>
    /// (indexed <c>field[y, x]</c>, OPPOSITE of production
    /// <c>Mithril.MapCalibration.Detection.NccTemplateMatch.ScoreAll</c>). Unscored
    /// border pixels = 0.</returns>
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
    /// Default deviation threshold used by <see cref="LoadDeviationAsField(GrayImage, IconTemplate, bool, double)"/>'s
    /// rim-mask path. Matches the CLI's <c>--detection-threshold</c> default; production's
    /// <c>DeviationBlobDetector</c> derives <c>devThr = 1 - lowNcc</c> with the same 0.5 baseline.
    /// </summary>
    public const double DefaultDevThr = 0.5;

    /// <summary>
    /// Build a field from a pre-computed deviation map. Skips the screenshot-minus-
    /// aligned-base subtraction step that <see cref="Build"/> performs; equivalent
    /// to calling <see cref="ScoreAll"/> directly with a deviation supplied by the
    /// caller. Used by the bundle-consumption path where the live engine has
    /// already produced a post-ECC deviation via #978's ECC refiner.
    ///
    /// <para>This default overload applies <see cref="DeviationFloodRimMask"/> so the
    /// probe's J numbers reflect what production's RANSAC pool sees (rim-masked
    /// deviation, mithril#897 gate-study balance). Pass the 4-arg overload with
    /// <c>applyRimMask: false</c> to disable for diagnostic comparisons.</para>
    /// </summary>
    /// <returns>Same row-major [H, W] layout as <see cref="ScoreAll"/>.</returns>
    public static double[,] LoadDeviationAsField(GrayImage deviation, IconTemplate template)
        => LoadDeviationAsField(deviation, template, applyRimMask: true, devThr: DefaultDevThr);

    /// <summary>
    /// Overload with explicit rim-mask control. Set <paramref name="applyRimMask"/>
    /// to false to score the raw deviation (used by tests and diagnostic CLI runs).
    /// </summary>
    public static double[,] LoadDeviationAsField(
        GrayImage deviation, IconTemplate template, bool applyRimMask, double devThr)
    {
        if (!applyRimMask) return ScoreAll(deviation, template);

        int n = deviation.Width * deviation.Height;
        var dev = new float[n];
        for (int i = 0; i < n; i++) dev[i] = deviation.Pixels[i] / 255f;

        var rim = DeviationFloodRimMask.Build(dev, deviation.Width, deviation.Height, devThr);
        // mithril#1123: delegate to the 3-arg overload so the rim-applied
        // ScoreAll tail is byte-identical to the synthesis-J orchestrator's
        // lifted-rim path. Existing callers (probe tooling, equivalence tests)
        // continue to call this 4-arg overload unchanged.
        return LoadDeviationAsField(deviation, template, rim);
    }

    /// <summary>
    /// Overload with a caller-supplied pre-built rim mask. Used by the
    /// synthesis-J orchestrator (mithril#1123) which lifts rim-mask computation
    /// out of the per-template loop into
    /// <c>MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation</c>'s
    /// body — once per orientation rather than once per template. The other
    /// overloads delegate to this one internally (after building their own mask)
    /// so behaviour is identical across the surface.
    /// </summary>
    /// <param name="rim">Row-major rim mask: <c>true</c> at pixels to zero out
    /// before scoring; length must equal <c>deviation.Width * deviation.Height</c>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rim"/>'s
    /// length does not match the deviation's pixel count.</exception>
    public static double[,] LoadDeviationAsField(
        GrayImage deviation, IconTemplate template, bool[] rim)
    {
        int n = deviation.Width * deviation.Height;
        if (rim.Length != n)
        {
            throw new ArgumentException(
                $"rim.Length ({rim.Length}) must equal deviation.Width*Height ({n}).",
                nameof(rim));
        }

        var maskedPixels = new byte[n];
        for (int i = 0; i < n; i++) maskedPixels[i] = rim[i] ? (byte)0 : deviation.Pixels[i];

        var masked = new GrayImage(deviation.Width, deviation.Height, maskedPixels);
        return ScoreAll(masked, template);
    }

    /// <summary>
    /// Bicubic interpolation of <paramref name="field"/> at sub-pixel position
    /// (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    /// <remarks>
    /// Honors the [H, W] / <c>field[y, x]</c> row-major convention established by
    /// <see cref="Build"/> and <see cref="ScoreAll"/>. Returns 0.0 for any position
    /// that falls outside the field bounds.
    /// </remarks>
    public static double Sample(double[,] field, double x, double y)
    {
        int h = field.GetLength(0), w = field.GetLength(1);
        if (x < 0 || y < 0 || x > w - 1 || y > h - 1) return 0.0;

        int ix = (int)Math.Floor(x);
        int iy = (int)Math.Floor(y);
        double fx = x - ix;
        double fy = y - iy;

        // Cubic Hermite (Catmull-Rom-ish) over 4 samples per row, then over 4 row results.
        // Both buffers hoisted above the loop to satisfy CA2014 (no stackalloc in a loop);
        // `row` is overwritten every iteration so reuse is semantically identical.
        Span<double> col = stackalloc double[4];
        Span<double> row = stackalloc double[4];
        for (int j = -1; j <= 2; j++)
        {
            int yy = Math.Clamp(iy + j, 0, h - 1);
            for (int i = -1; i <= 2; i++)
            {
                int xx = Math.Clamp(ix + i, 0, w - 1);
                row[i + 1] = field[yy, xx];
            }
            col[j + 1] = CubicHermite(row[0], row[1], row[2], row[3], fx);
        }
        return CubicHermite(col[0], col[1], col[2], col[3], fy);
    }

    private static double CubicHermite(double a, double b, double c, double d, double t)
    {
        double a0 = -0.5 * a + 1.5 * b - 1.5 * c + 0.5 * d;
        double a1 = a - 2.5 * b + 2.0 * c - 0.5 * d;
        double a2 = -0.5 * a + 0.5 * c;
        double a3 = b;
        return ((a0 * t + a1) * t + a2) * t + a3;
    }

    /// <summary>
    /// Alpha-masked NCC of <paramref name="template"/> slid over <paramref name="image"/>.
    /// Public so downstream tasks (e.g. probe scoring) can call it directly without
    /// going through the deviation step.
    /// </summary>
    /// <returns>
    /// Row-major [H, W] dense score array indexed <c>field[y, x]</c> — note this is
    /// the OPPOSITE of <c>Mithril.MapCalibration.Detection.NccTemplateMatch.ScoreAll</c>,
    /// which returns [W, H] indexed <c>[x, y]</c>. Downstream synthesis-probe code
    /// (J evaluator, refine, experiments) consistently uses [H, W] / <c>field[y, x]</c>;
    /// don't transpose. Border pixels that can't fit the template are left at 0.
    /// </returns>
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
