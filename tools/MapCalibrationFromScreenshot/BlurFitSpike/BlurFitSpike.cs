// mithril#1070 measurement spike — fit σ(scale) for the blur-aware Sobel
// template in SobelPaddedPyramidRefiner's full-resolution stage.
//
// Walks every Map_HogansKeepBasement-* bundle under
// %LocalAppData%/Mithril/diagnostics/calibration/, loads each bundle's
// 06-aligned-screenshot.png (the recovered map region from the capture) and
// 05-base-texture-resampled.png (the texture resized to the recovered scale),
// computes the Sobel-magnitude image of each, measures the 2D autocorrelation
// half-max width in pixels, and derives the σ a Gaussian convolution must
// apply to the template to make its autocorrelation width match the
// screenshot's. Then linear-fits σ_needed vs (1/scale) and reports the
// coefficients (RendererBlurIntercept + Slope) plus the fit residual quality.
//
// Output goes to stdout as a single block — the four production defaults plus
// per-bundle measurements. The spike is invoked once on the user's machine to
// produce the σ-curve constants, then the constants land in
// MapCalibrationLocateOptions and this file gets deleted before PR.
//
// Invocation: `dotnet run --project tools/MapCalibrationFromScreenshot --
//   --phase blur-fit-spike`
//
// No CLI args: bundle root is %LocalAppData%/Mithril/diagnostics/calibration/.

using System.Globalization;
using System.Text.Json;
using OpenCvSharp;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

internal static class BlurFitSpike
{
    private const string BundlePrefix = "Map_HogansKeepBasement-";
    private const double Ln2 = 0.6931471805599453;
    // Half-max search box around the autocorrelation peak. The autocorrelation
    // is symmetric and peaks at the origin (shift=(0,0)); we measure the
    // radius at which the central value drops to half its peak. Cap at 32 px
    // so noisy tails don't drag the measurement.
    private const int HalfMaxSearchRadius = 32;

    public static int Run()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var calibRoot = Path.Combine(localApp, "Mithril", "diagnostics", "calibration");
        if (!Directory.Exists(calibRoot))
        {
            Console.Error.WriteLine($"!! calibration root not found: {calibRoot}");
            return 1;
        }

        var bundles = Directory.GetDirectories(calibRoot)
            .Where(d => Path.GetFileName(d).StartsWith(BundlePrefix, StringComparison.Ordinal))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        if (bundles.Count == 0)
        {
            Console.Error.WriteLine($"!! no {BundlePrefix}* bundles found under {calibRoot}");
            return 1;
        }

        Console.WriteLine($"=== BlurFitSpike (mithril#1070) — {DateTime.UtcNow:O} ===");
        Console.WriteLine($"corpus root: {calibRoot}");
        Console.WriteLine($"bundles    : {bundles.Count}");
        Console.WriteLine();

        var measurements = new List<Measurement>();
        foreach (var bundle in bundles)
        {
            var m = MeasureBundle(bundle);
            if (m is null) continue;
            measurements.Add(m.Value);
        }

        if (measurements.Count < 2)
        {
            Console.Error.WriteLine($"!! need >= 2 measurable bundles, got {measurements.Count}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== Per-bundle measurements ===");
        Console.WriteLine("bundle                                                          scale  1/scale   screenshot_w   template_w   sigma_needed");
        foreach (var m in measurements)
        {
            Console.WriteLine(
                $"{m.BundleName,-62}  {m.Scale,5:F3}  {1.0 / m.Scale,5:F3}    {m.ScreenshotWidth,12:F3}  {m.TemplateWidth,11:F3}    {m.SigmaNeeded,12:F4}");
        }

        // Linear regression: σ_needed = intercept + slope * (1/scale)
        // (least squares).
        var xs = measurements.Select(m => 1.0 / m.Scale).ToArray();
        var ys = measurements.Select(m => m.SigmaNeeded).ToArray();
        FitLinear(xs, ys, out double intercept, out double slope);

        // Residuals.
        var residuals = new double[measurements.Count];
        for (int i = 0; i < measurements.Count; i++)
        {
            double predicted = intercept + slope * xs[i];
            residuals[i] = ys[i] - predicted;
        }
        double meanY = ys.Average();
        double maxResidual = residuals.Select(Math.Abs).Max();
        double relativeMaxResidual = maxResidual / Math.Max(1e-9, Math.Abs(meanY));

        Console.WriteLine();
        Console.WriteLine("=== Linear fit: sigma_needed = intercept + slope * (1/scale) ===");
        Console.WriteLine($"intercept       = {intercept.ToString("F6", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"slope           = {slope.ToString("F6", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"mean(sigma)     = {meanY.ToString("F4", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"max |residual|  = {maxResidual.ToString("F4", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"relative max    = {relativeMaxResidual.ToString("F4", CultureInfo.InvariantCulture)} (gate: < 0.10 keeps linear; >= 0.10 → piecewise fallback)");
        Console.WriteLine();
        Console.WriteLine("=== Production defaults to plumb into MapCalibrationLocateOptions ===");
        Console.WriteLine($"RendererBlurIntercept = {Math.Round(intercept, 4).ToString(CultureInfo.InvariantCulture)};");
        Console.WriteLine($"RendererBlurSlope     = {Math.Round(slope, 4).ToString(CultureInfo.InvariantCulture)};");
        Console.WriteLine($"RendererBlurMinSigma  = 0.0;");
        Console.WriteLine($"RendererBlurMaxSigma  = 3.0;");
        Console.WriteLine();
        Console.WriteLine($"Corpus = {string.Join(", ", measurements.Select(m => m.BundleName))}");
        return 0;
    }

    private static Measurement? MeasureBundle(string bundleDir)
    {
        var name = Path.GetFileName(bundleDir);
        var attemptPath = Path.Combine(bundleDir, "01-attempt.json");
        var screenshotPath = Path.Combine(bundleDir, "06-aligned-screenshot.png");
        var texturePath = Path.Combine(bundleDir, "05-base-texture-resampled.png");

        if (!File.Exists(attemptPath) || !File.Exists(screenshotPath) || !File.Exists(texturePath))
        {
            Console.Error.WriteLine($"-- skip {name}: missing files");
            return null;
        }

        // Read the recovered scale from the bundle's attempt.json so we can
        // place this bundle at the right (1/scale) point on the fit curve.
        double scale;
        string algo;
        try
        {
            using var stream = File.OpenRead(attemptPath);
            using var doc = JsonDocument.Parse(stream);
            var locatorBest = doc.RootElement.GetProperty("locatorBest");
            scale = locatorBest.GetProperty("scale").GetDouble();
            algo = locatorBest.GetProperty("algorithm").GetString() ?? "?";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"-- skip {name}: attempt.json read failed: {ex.Message}");
            return null;
        }
        if (algo != "sobel-padded-pyramid")
        {
            Console.Error.WriteLine($"-- skip {name}: algo={algo} (not sobel-padded-pyramid)");
            return null;
        }

        using var screenshot = Cv2.ImRead(screenshotPath, ImreadModes.Grayscale);
        using var texture = Cv2.ImRead(texturePath, ImreadModes.Grayscale);
        if (screenshot.Empty() || texture.Empty())
        {
            Console.Error.WriteLine($"-- skip {name}: PNG decode failed");
            return null;
        }

        // Sobel magnitude on both — same operator the production refiner uses
        // (mirrors SobelMagnitudeHelpers.SobelMagnitude8U with cv-managed dtype).
        using var screenshotSobel = SobelMag(screenshot);
        using var textureSobel = SobelMag(texture);

        double screenshotWidth = AutocorrelationHalfMaxWidth(screenshotSobel);
        double templateWidth = AutocorrelationHalfMaxWidth(textureSobel);

        // σ that, convolved with the template, makes its autocorrelation match
        // the screenshot's. Convolution-of-Gaussian arithmetic: w_after² =
        // w_before² + (σ × 2 × sqrt(2 ln 2))², so σ = sqrt(w_after² - w_before²)
        // / (2 × sqrt(2 ln 2)). Equivalent to sqrt((w_a² - w_b²) / 8 ln 2).
        double diff = screenshotWidth * screenshotWidth - templateWidth * templateWidth;
        double sigmaNeeded = diff <= 0 ? 0.0 : Math.Sqrt(diff / (8.0 * Ln2));

        return new Measurement(name, scale, screenshotWidth, templateWidth, sigmaNeeded);
    }

    private static Mat SobelMag(Mat src)
    {
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        var u8 = new Mat();
        Cv2.Normalize(mag, u8, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        return u8;
    }

    // Half-max width of the central autocorrelation peak, in pixels. Computed
    // via the Wiener-Khinchin theorem: autocorrelation = IFFT(|FFT(image)|²),
    // shifted so the peak lands at the centre. We then measure the radius at
    // which the value drops to half the peak — averaged over the four cardinal
    // axes for symmetry.
    private static double AutocorrelationHalfMaxWidth(Mat image)
    {
        // Float-typed input, zero-meaned so DC bias doesn't dominate the FFT.
        using var f32 = new Mat();
        image.ConvertTo(f32, MatType.CV_32F);
        var mean = Cv2.Mean(f32).Val0;
        using var zm = new Mat();
        Cv2.Subtract(f32, Scalar.All(mean), zm);

        // DFT-friendly padding.
        int optW = Cv2.GetOptimalDFTSize(zm.Width);
        int optH = Cv2.GetOptimalDFTSize(zm.Height);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(zm, padded, 0, optH - zm.Height, 0, optW - zm.Width,
            BorderTypes.Constant, Scalar.All(0));

        // FFT.
        using var planes = new Mat();
        using var imaginary = Mat.Zeros(padded.Size(), MatType.CV_32F);
        var planeArr = new Mat[] { padded, imaginary };
        using var complex = new Mat();
        Cv2.Merge(planeArr, complex);
        using var spectrum = new Mat();
        Cv2.Dft(complex, spectrum, DftFlags.None);

        // |FFT|².
        var split = new Mat[2];
        Cv2.Split(spectrum, out split);
        using var re = split[0];
        using var im = split[1];
        using var magSq = new Mat();
        Cv2.Pow(re, 2, magSq);
        using var imSq = new Mat();
        Cv2.Pow(im, 2, imSq);
        Cv2.Add(magSq, imSq, magSq);

        // Zero out the imaginary part for IFFT — autocorrelation of a real
        // signal is real-valued.
        using var zero = Mat.Zeros(magSq.Size(), MatType.CV_32F);
        var ifftPlanes = new Mat[] { magSq, zero };
        using var combined = new Mat();
        Cv2.Merge(ifftPlanes, combined);
        using var autocorr = new Mat();
        Cv2.Dft(combined, autocorr, DftFlags.Inverse | DftFlags.Scale | DftFlags.RealOutput);

        // The peak lives at (0,0) in unshifted DFT output; shift so it's at
        // the centre for radius measurement.
        FftShift(autocorr, out var shifted);
        using var _ = shifted;

        int cx = shifted.Width / 2;
        int cy = shifted.Height / 2;
        var idx = shifted.GetGenericIndexer<float>();
        double peak = idx[cy, cx];
        if (peak <= 0) return 1.0;   // pathological — flat field
        double halfPeak = 0.5 * peak;

        // Walk outward along ±x and ±y from the peak; find the first radius
        // at which the value drops below half-peak. Average the four.
        double rPlusX = FindHalfMaxRadius(shifted, cx, cy, +1, 0, peak, halfPeak);
        double rMinusX = FindHalfMaxRadius(shifted, cx, cy, -1, 0, peak, halfPeak);
        double rPlusY = FindHalfMaxRadius(shifted, cx, cy, 0, +1, peak, halfPeak);
        double rMinusY = FindHalfMaxRadius(shifted, cx, cy, 0, -1, peak, halfPeak);

        // Full width = 2 × mean half-radius.
        double meanHalf = (rPlusX + rMinusX + rPlusY + rMinusY) / 4.0;
        return 2.0 * meanHalf;
    }

    private static double FindHalfMaxRadius(Mat shifted, int cx, int cy, int dx, int dy,
        double peak, double halfPeak)
    {
        var idx = shifted.GetGenericIndexer<float>();
        double prevValue = peak;
        for (int r = 1; r <= HalfMaxSearchRadius; r++)
        {
            int x = cx + dx * r;
            int y = cy + dy * r;
            if (x < 0 || y < 0 || x >= shifted.Width || y >= shifted.Height)
                return r;
            double v = idx[y, x];
            if (v <= halfPeak)
            {
                // Linear interp between (r-1, prevValue) and (r, v).
                double t = (prevValue - halfPeak) / Math.Max(1e-9, prevValue - v);
                return (r - 1) + t;
            }
            prevValue = v;
        }
        return HalfMaxSearchRadius;
    }

    private static void FftShift(Mat src, out Mat dst)
    {
        // Quadrant swap so the DC component (currently at (0,0)) lands at the
        // image centre. Handles odd dimensions by splitting at the floor of W/2
        // and H/2.
        int cx = src.Width / 2;
        int cy = src.Height / 2;
        dst = new Mat(src.Size(), src.Type());

        var q0 = new Mat(src, new Rect(0, 0, cx, cy));
        var q1 = new Mat(src, new Rect(cx, 0, src.Width - cx, cy));
        var q2 = new Mat(src, new Rect(0, cy, cx, src.Height - cy));
        var q3 = new Mat(src, new Rect(cx, cy, src.Width - cx, src.Height - cy));

        // Destination quadrants.
        var d0 = new Mat(dst, new Rect(0, 0, src.Width - cx, src.Height - cy));
        var d1 = new Mat(dst, new Rect(src.Width - cx, 0, cx, src.Height - cy));
        var d2 = new Mat(dst, new Rect(0, src.Height - cy, src.Width - cx, cy));
        var d3 = new Mat(dst, new Rect(src.Width - cx, src.Height - cy, cx, cy));

        // q3 (bottom-right) → d0 (top-left), q0 → d3, q1 → d2, q2 → d1.
        q3.CopyTo(d0);
        q2.CopyTo(d1);
        q1.CopyTo(d2);
        q0.CopyTo(d3);
    }

    private static void FitLinear(double[] xs, double[] ys, out double intercept, out double slope)
    {
        double meanX = xs.Average();
        double meanY = ys.Average();
        double num = 0, den = 0;
        for (int i = 0; i < xs.Length; i++)
        {
            num += (xs[i] - meanX) * (ys[i] - meanY);
            den += (xs[i] - meanX) * (xs[i] - meanX);
        }
        slope = den == 0 ? 0 : num / den;
        intercept = meanY - slope * meanX;
    }

    private readonly record struct Measurement(
        string BundleName,
        double Scale,
        double ScreenshotWidth,
        double TemplateWidth,
        double SigmaNeeded);
}
