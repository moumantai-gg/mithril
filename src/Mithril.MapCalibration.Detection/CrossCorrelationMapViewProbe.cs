using System;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// FFT-accelerated normalised cross-correlation probe. Searches over view-scale
/// candidates and picks the (pan, scale) maximising the normalised correlation
/// peak of screenshot against base texture. Rotation/mirror are held by the
/// caller's cal record — PG's world-map view doesn't independently rotate.
///
/// <para>The cross-correlation is normalised by <c>sqrt(E_patch × E_window)</c>
/// at each offset using an integral-image sliding window — this keeps NCC scores
/// in <c>[−1, 1]</c> regardless of patch size or scale, enabling fair comparison
/// across scale candidates.</para>
/// </summary>
public sealed class CrossCorrelationMapViewProbe : IMapViewProbe
{
    private const double MinScale = 0.25;
    private const double MaxScale = 4.0;
    private const int CoarseScaleCount = 8;
    private const double AbsoluteThreshold = 0.55;  // NCC in [0,1]; tuned against synthetic tests
    private const double RatioThreshold = 1.10;     // peak / 2nd-peak (outside guard zone)

    private readonly ILogger? _logger;

    public CrossCorrelationMapViewProbe(ILogger<CrossCorrelationMapViewProbe>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture)
    {
        if (screenshot is null || baseTexture is null)
        {
            _logger?.LogWarning("TryProbe: null input (screenshot={Screenshot}, baseTex={BaseTex}).",
                screenshot is null ? "null" : $"{screenshot.Width}x{screenshot.Height}",
                baseTexture is null ? "null" : $"{baseTexture.Width}x{baseTexture.Height}");
            return null;
        }
        if (screenshot.Width < 8 || screenshot.Height < 8)
        {
            _logger?.LogWarning("TryProbe: screenshot too small ({W}x{H} < 8x8).", screenshot.Width, screenshot.Height);
            return null;
        }
        if (baseTexture.Width < 8 || baseTexture.Height < 8)
        {
            _logger?.LogWarning("TryProbe: baseTexture too small ({W}x{H} < 8x8).", baseTexture.Width, baseTexture.Height);
            return null;
        }

        _logger?.LogTrace("TryProbe: screenshot {SW}x{SH}, baseTex {TW}x{TH}; starting coarse scale sweep.",
            screenshot.Width, screenshot.Height, baseTexture.Width, baseTexture.Height);

        var coarse = ScaleSweepCoarse(screenshot, baseTexture);
        if (coarse is null)
        {
            _logger?.LogWarning("TryProbe: coarse sweep produced no candidate (all scales out of range or degenerate).");
            return null;
        }

        var refined = GoldenSectionRefine(screenshot, baseTexture, coarse.Value);

        var fix = refined ?? coarse.Value;
        fix = ProbeExactFitScales(screenshot, baseTexture, fix);
        if (!PassesConfidenceGate(fix))
        {
            _logger?.LogWarning(
                "TryProbe: confidence gate REJECTED. peak={Peak:0.000} (abs threshold {AbsT:0.000}), " +
                "second={Second:0.000}, ratio={Ratio:0.00} (ratio threshold {RatioT:0.00}), " +
                "best scale={Scale:0.000}, pan=({PanX:0},{PanY:0}).",
                fix.PeakScore, AbsoluteThreshold, fix.SecondPeakScore,
                fix.SecondPeakScore > 0 ? fix.PeakScore / fix.SecondPeakScore : double.PositiveInfinity,
                RatioThreshold, fix.Scale, fix.PanX, fix.PanY);
            return null;
        }

        _logger?.LogTrace("TryProbe: ACCEPTED. peak={Peak:0.000}, second={Second:0.000}, scale={Scale:0.000}, pan=({PanX:0},{PanY:0}).",
            fix.PeakScore, fix.SecondPeakScore, fix.Scale, fix.PanX, fix.PanY);

        return new MapViewFix(
            PanTexPxX: fix.PanX,
            PanTexPxY: fix.PanY,
            ViewScale: fix.Scale,
            Confidence: fix.PeakScore,
            MeasuredAt: DateTimeOffset.UtcNow);
    }

    private readonly record struct ScaleCandidate(
        double Scale, double PanX, double PanY, double PeakScore, double SecondPeakScore);

    private ScaleCandidate? ScaleSweepCoarse(GrayImage screenshot, GrayImage baseTexture)
    {
        ScaleCandidate? best = null;
        for (int i = 0; i < CoarseScaleCount; i++)
        {
            double t = (double)i / (CoarseScaleCount - 1);
            double s = MinScale * Math.Pow(MaxScale / MinScale, t);
            var candidate = EvaluateAtScale(screenshot, baseTexture, s);
            if (candidate is { } c && (best is null || c.PeakScore > best.Value.PeakScore))
                best = c;
        }
        return best;
    }

    /// <summary>
    /// Refine by running golden-section search from every coarse candidate, then
    /// returning the overall best refined result. Running from all candidates (not
    /// just the coarse peak) is necessary because the NCC landscape over scale is
    /// often non-unimodal for periodic or self-similar textures: the true scale=1.0
    /// maximum may lie in a local bracket whose coarse score is lower than a
    /// harmonic peak at e.g. scale=4.0.
    /// </summary>
    private ScaleCandidate? GoldenSectionRefine(GrayImage screenshot, GrayImage baseTexture, ScaleCandidate seed)
    {
        double step = Math.Pow(MaxScale / MinScale, 1.0 / (CoarseScaleCount - 1));
        ScaleCandidate? best = null;

        // Build the full list of coarse candidates (including nulls we skip).
        for (int i = 0; i < CoarseScaleCount; i++)
        {
            double t = (double)i / (CoarseScaleCount - 1);
            double s = MinScale * Math.Pow(MaxScale / MinScale, t);
            var coarse = EvaluateAtScale(screenshot, baseTexture, s);
            if (coarse is null) continue;

            var refined = GoldenSectionFromSeed(screenshot, baseTexture, coarse.Value, step);
            var winner = refined ?? coarse.Value;
            if (best is null || winner.PeakScore > best.Value.PeakScore)
                best = winner;
        }

        return best ?? seed;
    }

    private ScaleCandidate? GoldenSectionFromSeed(
        GrayImage screenshot, GrayImage baseTexture, ScaleCandidate seed, double step)
    {
        double a = seed.Scale / step;
        double b = seed.Scale * step;
        const double phi = 0.61803398875;
        ScaleCandidate? best = seed;
        for (int i = 0; i < 6; i++)  // 6 iterations narrows the bracket ~12×
        {
            double c = b - phi * (b - a);
            double d = a + phi * (b - a);
            var fc = EvaluateAtScale(screenshot, baseTexture, c);
            var fd = EvaluateAtScale(screenshot, baseTexture, d);
            double scoreC = fc?.PeakScore ?? double.NegativeInfinity;
            double scoreD = fd?.PeakScore ?? double.NegativeInfinity;
            if (scoreC >= scoreD)
            {
                b = d;
                if (fc is { } x && x.PeakScore > (best?.PeakScore ?? double.NegativeInfinity)) best = x;
            }
            else
            {
                a = c;
                if (fd is { } x && x.PeakScore > (best?.PeakScore ?? double.NegativeInfinity)) best = x;
            }
        }
        return best;
    }

    /// <summary>
    /// Probe the exact-fit scale (screenshot.W / texture.W) and nearby integer
    /// ratios. The golden-section bracket can miss scale=1.0 when it falls exactly
    /// at the null boundary (resampled > texture). Compare each result to
    /// <paramref name="current"/> and return the better one.
    /// </summary>
    private static ScaleCandidate ProbeExactFitScales(
        GrayImage screenshot, GrayImage baseTexture, ScaleCandidate current)
    {
        // Candidate exact-fit scales: sw/tw (X-axis), sh/th (Y-axis), and
        // simple integer ratios 0.5× through 4× that round to exactly tw/th.
        var exactScales = new double[]
        {
            (double)screenshot.Width / baseTexture.Width,
            (double)screenshot.Height / baseTexture.Height,
            0.25, 0.5, 1.0, 2.0, 4.0,
        };

        var best = current;
        foreach (var s in exactScales)
        {
            if (s < MinScale || s > MaxScale) continue;
            var c = EvaluateAtScale(screenshot, baseTexture, s);
            if (c is { } x && x.PeakScore > best.PeakScore) best = x;
        }
        return best;
    }

    private static ScaleCandidate? EvaluateAtScale(GrayImage screenshot, GrayImage baseTexture, double scale)
    {
        // The screenshot was rendered at `scale` overlay-pixels per texture-pixel.
        // Resample into texture-pixel space: divide by scale.
        var resampled = Resample(screenshot, scale);
        if (resampled.Width > baseTexture.Width || resampled.Height > baseTexture.Height)
            return null;  // resampled patch must fit inside the base texture

        var corr = NormalisedFftCrossCorrelate(resampled, baseTexture);
        var (peakX, peakY, peakScore, secondPeak) = FindTopTwoPeaks(
            corr.Map, resampled.Width, resampled.Height,
            corrN: corr.N,
            textureW: baseTexture.Width, textureH: baseTexture.Height);

        return new ScaleCandidate(
            Scale: scale,
            PanX: peakX,
            PanY: peakY,
            PeakScore: peakScore,
            SecondPeakScore: secondPeak);
    }

    /// <summary>
    /// Resample the screenshot so that each output pixel represents one texture
    /// pixel: the screenshot was rendered at <paramref name="scale"/> overlay
    /// pixels per texture pixel, so we shrink by that factor.
    /// </summary>
    private static GrayImage Resample(GrayImage src, double scale)
    {
        int w = Math.Max(8, (int)Math.Round(src.Width / scale));
        int h = Math.Max(8, (int)Math.Round(src.Height / scale));
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(src.Width - 1, (int)(x * scale));
                int sy = Math.Min(src.Height - 1, (int)(y * scale));
                pixels[y * w + x] = src.Pixels[sy * src.Width + sx];
            }
        }
        return new GrayImage(w, h, pixels);
    }

    private readonly record struct CorrResult(double[] Map, int N, int M);

    /// <summary>
    /// Normalised cross-correlation via FFT + integral-image window normalisation.
    /// Each value in the returned map is NCC ∈ [−1, 1], comparable across
    /// different patch sizes and scales.
    /// </summary>
    private static CorrResult NormalisedFftCrossCorrelate(GrayImage patch, GrayImage texture)
    {
        int n = Fft2D.NextPow2(Math.Max(patch.Width, texture.Width));
        int m = Fft2D.NextPow2(Math.Max(patch.Height, texture.Height));

        // Zero-mean the patch and texture.
        double patchEnergy = ZeroMeanEnergy(patch.Pixels, out double patchMean);
        double texMean = ComputeMean(texture.Pixels);

        if (patchEnergy < 1e-12)
        {
            // Degenerate flat patch — return zero map.
            return new CorrResult(new double[n * m], n, m);
        }

        // FFT cross-correlation: IFFT(conj(FFT(patch)) * FFT(texture))
        var f = ToZeroMeanComplexGrid(patch, n, m, patchMean);
        var g = ToZeroMeanComplexGrid(texture, n, m, texMean);
        Fft2D.Forward(f, m, n);
        Fft2D.Forward(g, m, n);
        for (int i = 0; i < f.Length; i++) f[i] = Complex.Conjugate(f[i]) * g[i];
        Fft2D.Inverse(f, m, n);

        // Raw correlation map.
        var rawCorr = new double[n * m];
        for (int i = 0; i < rawCorr.Length; i++) rawCorr[i] = f[i].Real;

        // Build integral image of squared zero-mean texture values for sliding
        // window energy normalisation: E_window(dx,dy) computed in O(1) per offset.
        var integralSq = BuildIntegralSquare(texture, texMean);

        double sqrtPatchE = Math.Sqrt(patchEnergy);
        int maxX = texture.Width - patch.Width;
        int maxY = texture.Height - patch.Height;
        int pw = patch.Width, ph = patch.Height;

        var normCorr = new double[n * m];
        for (int dy = 0; dy <= maxY; dy++)
        {
            for (int dx = 0; dx <= maxX; dx++)
            {
                double windowEnergy = WindowEnergy(integralSq, texture.Width + 1, dx, dy, pw, ph);
                double denom = sqrtPatchE * Math.Sqrt(windowEnergy);
                if (denom < 1e-12)
                    normCorr[dy * n + dx] = 0;
                else
                    normCorr[dy * n + dx] = rawCorr[dy * n + dx] / denom;
            }
        }

        return new CorrResult(normCorr, n, m);
    }

    /// <summary>
    /// Build a summed-area table of (pixel − mean)² for the texture.
    /// The table has dimensions (H+1) × (W+1) for boundary convenience.
    /// </summary>
    private static double[] BuildIntegralSquare(GrayImage texture, double mean)
    {
        int w = texture.Width, h = texture.Height;
        var integral = new double[(h + 1) * (w + 1)];
        for (int y = 1; y <= h; y++)
        {
            for (int x = 1; x <= w; x++)
            {
                double d = texture.Pixels[(y - 1) * w + (x - 1)] - mean;
                integral[y * (w + 1) + x] = d * d
                    + integral[(y - 1) * (w + 1) + x]
                    + integral[y * (w + 1) + (x - 1)]
                    - integral[(y - 1) * (w + 1) + (x - 1)];
            }
        }
        return integral;
    }

    /// <summary>
    /// Retrieve the sum-of-squares energy inside the window [dx, dx+pw) × [dy, dy+ph)
    /// from the (H+1)×(W+1) integral image (stride = textureW+1).
    /// </summary>
    private static double WindowEnergy(double[] integral, int stride, int dx, int dy, int pw, int ph)
    {
        int x1 = dx, y1 = dy, x2 = dx + pw, y2 = dy + ph;
        return integral[y2 * stride + x2]
            - integral[y1 * stride + x2]
            - integral[y2 * stride + x1]
            + integral[y1 * stride + x1];
    }

    private static Complex[] ToZeroMeanComplexGrid(GrayImage img, int n, int m, double mean)
    {
        var g = new Complex[n * m];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                g[y * n + x] = new Complex(img.Pixels[y * img.Width + x] - mean, 0);
            }
        }
        return g;
    }

    private static double ComputeMean(byte[] pixels)
    {
        double sum = 0;
        for (int i = 0; i < pixels.Length; i++) sum += pixels[i];
        return sum / pixels.Length;
    }

    private static double ZeroMeanEnergy(byte[] pixels, out double mean)
    {
        mean = ComputeMean(pixels);
        double energy = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            double d = pixels[i] - mean;
            energy += d * d;
        }
        return energy;
    }

    private static (double X, double Y, double Peak, double SecondPeak) FindTopTwoPeaks(
        double[] corr, int patchW, int patchH, int corrN,
        int textureW, int textureH)
    {
        int maxX = textureW - patchW;
        int maxY = textureH - patchH;
        if (maxX < 0 || maxY < 0) return (0, 0, double.NegativeInfinity, double.NegativeInfinity);

        // First pass: find the peak location.
        double peak = double.NegativeInfinity;
        int peakX = 0, peakY = 0;
        for (int y = 0; y <= maxY; y++)
        {
            for (int x = 0; x <= maxX; x++)
            {
                double v = corr[y * corrN + x];
                if (v > peak) { peak = v; peakX = x; peakY = y; }
            }
        }

        // Second pass: find the highest value outside a guard zone around the peak.
        // The guard radius is half the patch size so that a periodic texture's
        // near-equal alias peaks don't deflate the ratio.
        int guardW = Math.Max(1, patchW / 2);
        int guardH = Math.Max(1, patchH / 2);
        double second = double.NegativeInfinity;
        for (int y = 0; y <= maxY; y++)
        {
            for (int x = 0; x <= maxX; x++)
            {
                if (Math.Abs(x - peakX) < guardW && Math.Abs(y - peakY) < guardH)
                    continue;
                double v = corr[y * corrN + x];
                if (v > second) second = v;
            }
        }

        return (peakX, peakY, peak, second);
    }

    private static bool PassesConfidenceGate(ScaleCandidate c)
    {
        if (c.PeakScore < AbsoluteThreshold) return false;
        if (c.SecondPeakScore <= 0 || double.IsNegativeInfinity(c.SecondPeakScore))
            return c.PeakScore > AbsoluteThreshold;
        return (c.PeakScore / c.SecondPeakScore) > RatioThreshold;
    }
}
