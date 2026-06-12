using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-screenshot fog-of-war detector (mithril#1116 Task 3, spec §D6). Combines
/// a local-variance ceiling and a luminance window to identify pixels that sit
/// inside PG's grey fog-of-war chrome.
///
/// <para><b>Role.</b> Used as residual coverage for fog-region edges that
/// <c>LocalNccDeviation</c>'s <c>addedOnly:true</c> filter doesn't fully
/// suppress: <c>LocalNccDeviation</c> already drops most fog by detecting "this
/// is missing relative to baseline", but the soft transition band along fog
/// edges can still leak through. This detector furnishes the second layer —
/// a positive identification of "this pixel IS fog" that the deviation
/// pipeline ANDs out of the mask before NCC.</para>
///
/// <para><b>Algorithm.</b> For each pixel:</para>
/// <list type="bullet">
///   <item>If luminance is outside
///     [<see cref="MapCalibrationDetectorOptions.FogColorMin"/>,
///     <see cref="MapCalibrationDetectorOptions.FogColorMax"/>], not fog.</item>
///   <item>Otherwise compute local variance in a 7×7 window (clamped to image
///     bounds) and mark the pixel as fog iff variance &lt;
///     <see cref="MapCalibrationDetectorOptions.FogVarianceThreshold"/>.</item>
/// </list>
///
/// <para>Direct double-pass variance (sum of x and sum of x²) per pixel; the
/// input ROI is ~800×800 and the hot path is once per detection attempt, so
/// integral-image acceleration isn't worth the complexity here. When
/// <see cref="MapCalibrationDetectorOptions.FogOfWarDetectionEnabled"/> is
/// <c>false</c>, returns an all-zeros mask so callers can AND it in
/// unconditionally without an enable check.</para>
/// </summary>
internal sealed class FogOfWarDetector
{
    private const int WindowRadius = 3; // 7×7 window.

    private readonly MapCalibrationDetectorOptions _options;
    private readonly ILogger<FogOfWarDetector>? _logger;

    public FogOfWarDetector(
        MapCalibrationDetectorOptions options,
        ILogger<FogOfWarDetector>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Returns a fog-of-war mask the same size as <paramref name="screenshotRoi"/>:
    /// 255 where the pixel is classified as fog, 0 elsewhere. When fog detection
    /// is disabled, returns an all-zeros mask so the caller's AND-based
    /// composition is a no-op.
    /// </summary>
    public GrayImage Detect(GrayImage screenshotRoi)
    {
        int w = screenshotRoi.Width;
        int h = screenshotRoi.Height;
        var mask = new byte[w * h];

        if (!_options.FogOfWarDetectionEnabled)
        {
            _logger?.LogTrace(
                "Fog-of-war detection disabled; returning empty mask ({W}x{H}).", w, h);
            return new GrayImage(w, h, mask);
        }

        var src = screenshotRoi.Pixels;
        byte colorMin = _options.FogColorMin;
        byte colorMax = _options.FogColorMax;
        double varianceThreshold = _options.FogVarianceThreshold;

        int marked = 0;
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int yStart = y - WindowRadius; if (yStart < 0) yStart = 0;
            int yEnd   = y + WindowRadius; if (yEnd > h - 1) yEnd = h - 1;

            for (int x = 0; x < w; x++)
            {
                int i = rowBase + x;
                byte v = src[i];
                if (v < colorMin || v > colorMax)
                {
                    // Out of fog luminance window; mask[i] already 0.
                    continue;
                }

                int xStart = x - WindowRadius; if (xStart < 0) xStart = 0;
                int xEnd   = x + WindowRadius; if (xEnd > w - 1) xEnd = w - 1;

                // Double-pass variance over the (clamped) 7×7 window:
                // var = E[x²] - (E[x])². Cheap for once-per-attempt at 800×800.
                long sum = 0;
                long sumSq = 0;
                int count = 0;
                for (int yi = yStart; yi <= yEnd; yi++)
                {
                    int yiBase = yi * w;
                    for (int xi = xStart; xi <= xEnd; xi++)
                    {
                        int p = src[yiBase + xi];
                        sum += p;
                        sumSq += (long)p * p;
                        count++;
                    }
                }

                double mean = (double)sum / count;
                double variance = ((double)sumSq / count) - (mean * mean);
                if (variance < varianceThreshold)
                {
                    mask[i] = 255;
                    marked++;
                }
            }
        }

        _logger?.LogTrace(
            "Fog-of-war mask computed ({W}x{H}): {Marked} pixels marked (varThreshold={VarThreshold}, colorWindow=[{ColorMin},{ColorMax}]).",
            w, h, marked, varianceThreshold, colorMin, colorMax);
        return new GrayImage(w, h, mask);
    }
}
