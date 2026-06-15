using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1155 Phase 3 — pixel-level unit tests for the post-classification
/// peak-luma pre-filter. Tests three synthetic blob/pixel scenarios that mirror
/// the production failure modes the spike + audit identified:
///
/// <list type="bullet">
///   <item><b>Bright pip</b> — saturated-white pixels at the blob's bbox; the
///         real-icon case. Peak luma should round to 1.0.</item>
///   <item><b>Gray cobble</b> — mid-gray pixels at the blob's bbox; the
///         floor-noise case. Peak luma should sit at ~0.30.</item>
///   <item><b>Alpha-zero hole</b> — pixels at BGRA(0,0,0,0); the texture-hole
///         case the §6.g sibling work tracks. Peak luma should be 0.0.</item>
/// </list>
///
/// <para>The threshold-check is owned by the call site
/// (<c>DeviationBlobDetector.DetectIconBlobs</c>) — <see cref="PeakLumaFilter"/>
/// returns the raw [0, 1] value and the caller compares against
/// <c>BlobOptions.MinPeakLuma</c>. These tests pin the per-pixel arithmetic, not
/// the gate decision.</para>
/// </summary>
public sealed class PeakLumaFilterTests
{
    /// <summary>BT.601 luma weights mirror CapturedFrame.ToGray / PeakLumaFilter.</summary>
    private const double LumaR = 0.299;
    private const double LumaG = 0.587;
    private const double LumaB = 0.114;

    /// <summary>
    /// Build a single-pixel blob at a known offset whose Pixels list points at
    /// the BGRA buffer's row-major index for that offset. The bbox is degenerate
    /// (1×1) but the Ordinal is required-init so we wire it explicitly.
    /// </summary>
    private static BlobFeat SinglePixelBlob(int x, int y, int width)
    {
        int idx = y * width + x;
        var blob = new BlobFeat { Ordinal = 0 };
        blob.Pixels.Add(idx);
        blob.MinX = blob.MaxX = x;
        blob.MinY = blob.MaxY = y;
        return blob;
    }

    /// <summary>
    /// Build a many-pixel blob from a list of (x, y) coordinates. Tests the
    /// "peak over many pixels" behaviour — the production-shape blobs have
    /// 20-400 pixels each.
    /// </summary>
    private static BlobFeat MultiPixelBlob(int width, (int X, int Y)[] coords)
    {
        var blob = new BlobFeat { Ordinal = 0 };
        foreach (var (x, y) in coords)
        {
            blob.Pixels.Add(y * width + x);
            if (x < blob.MinX) blob.MinX = x;
            if (x > blob.MaxX) blob.MaxX = x;
            if (y < blob.MinY) blob.MinY = y;
            if (y > blob.MaxY) blob.MaxY = y;
        }
        return blob;
    }

    [Fact]
    public void Bright_white_pip_pixel_yields_peak_luma_at_one()
    {
        // 16x16 frame, single bright-white pixel at (5,5). The peak should be
        // exactly 1.0 — BGRA(255,255,255,255) → (0.114+0.587+0.299) * 255 / 255 = 1.0.
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4];
        int o = (5 * w + 5) * 4;
        bgra[o + 0] = 255; bgra[o + 1] = 255; bgra[o + 2] = 255; bgra[o + 3] = 255;

        var blob = SinglePixelBlob(5, 5, w);
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().BeApproximately(1.0, 1e-9,
            "BGRA(255,255,255) is the bright-pip case the spike's blob 176 PeakLuma 0.91 corresponds to (after sub-pixel rendering — the synthetic max is the upper bound).");
    }

    [Fact]
    public void Multi_pixel_real_icon_peak_uses_the_brightest_pixel()
    {
        // 16x16 frame; blob covers 4 pixels — 3 mid-gray and 1 bright. The peak
        // must pick the brightest, not the mean. Mirrors the real-icon shape
        // where the central glyph stroke is bright but the surrounding pixels
        // in the blob bbox are mid-gray (the bright pip is a small fraction of
        // the connected component).
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4];
        void Paint(int x, int y, byte b, byte g, byte r)
        {
            int o2 = (y * w + x) * 4;
            bgra[o2] = b; bgra[o2 + 1] = g; bgra[o2 + 2] = r; bgra[o2 + 3] = 255;
        }
        Paint(2, 2, 80, 80, 80);    // mid-gray
        Paint(2, 3, 80, 80, 80);    // mid-gray
        Paint(3, 2, 80, 80, 80);    // mid-gray
        Paint(3, 3, 240, 240, 240); // bright glyph centre

        var blob = MultiPixelBlob(w, [(2, 2), (2, 3), (3, 2), (3, 3)]);
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().BeApproximately(240.0 / 255.0, 1e-9,
            "the peak is the brightest pixel in the blob, not the mean — that's why PeakLuma > 0.78 cleanly separates real-icon blobs even when most of the blob bbox is floor noise (audit §E).");
    }

    [Fact]
    public void Gray_cobble_noise_pixel_yields_peak_luma_around_zero_point_three()
    {
        // 16x16 frame; single mid-gray pixel at (3,3). BGRA(80,80,80) ≈ 80/255 = 0.314.
        // Mirrors the floor-noise case the audit's 0.22-0.40 range covers.
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4];
        int o = (3 * w + 3) * 4;
        bgra[o + 0] = 80; bgra[o + 1] = 80; bgra[o + 2] = 80; bgra[o + 3] = 255;

        var blob = SinglePixelBlob(3, 3, w);
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().BeApproximately(80.0 / 255.0, 1e-9,
            "mid-gray BGRA(80) → luma 80/255 ≈ 0.314 — well below MinPeakLuma=0.7 so the filter drops this blob (audit §E floor-noise range 0.22-0.40).");
    }

    [Fact]
    public void Alpha_zero_hole_yields_peak_luma_at_zero()
    {
        // 16x16 frame; single all-zero-channel pixel (BGRA(0,0,0,0) — the
        // alpha-zero texture-hole sibling case spec §6.g flagged). Alpha is
        // ignored by PeakLumaFilter — what matters is RGB = (0,0,0) → luma 0.
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4]; // all zeros — implicit BGRA(0,0,0,0).

        var blob = SinglePixelBlob(7, 7, w);
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().Be(0.0,
            "BGRA(0,0,0,0) → luma 0; the alpha-zero hole always fails the peak-luma gate, defending the §6.g residual gap below the noise floor.");
    }

    [Fact]
    public void Bgra_dimension_mismatch_returns_zero_and_does_not_crash()
    {
        // Producer-side defensive: a caller that hands a mis-sized buffer must
        // not crash the detector. PeakLumaFilter logs a LogWarning and returns
        // 0.0 (which fails the gate). Mirrors DeviationBlobDetector's existing
        // "DeviationMask length != expected — skipping subtract" idiom.
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4 - 8]; // 8 bytes short

        var blob = SinglePixelBlob(0, 0, w);
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().Be(0.0, "dim mismatch is fail-soft — a misaligned producer can't crash the pipeline.");
    }

    [Fact]
    public void Empty_blob_yields_peak_luma_at_zero()
    {
        // Empty pixel list is a degenerate input; the filter returns 0.0 rather
        // than throwing on max-of-empty. With MinPeakLuma > 0, an empty blob
        // gets dropped — which is correct: no pixels means no icon evidence.
        int w = 16, h = 16;
        var bgra = new byte[w * h * 4];

        var blob = new BlobFeat { Ordinal = 0 };
        var peak = PeakLumaFilter.PeakLuma(blob, bgra, w, h, NullLogger.Instance);

        peak.Should().Be(0.0, "empty-pixel-list blobs return 0 — caller's MinPeakLuma > 0 gate drops them.");
    }

    [Fact]
    public void DetectIconBlobs_warns_and_skips_when_MinPeakLuma_is_NaN()
    {
        // Review #1169-r2 finding #1: `is { } minPeakLuma` matches NaN, then
        // `peakLuma >= NaN` is always false → every blob would be silently
        // dropped. The guard short-circuits with a LogWarning when MinPeakLuma
        // is non-finite so the malformed-config case is visible.
        int w = 20, h = 20;
        var dev = SyntheticDevWithOneBrightBlob(w, h);
        var bgra = SyntheticBrightBgra(w, h);

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 1000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0)
        {
            MinPeakLuma = double.NaN,
        };
        var logger = new RecordingLogger();
        var icons = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, rim: RimMaskMode.None, opts, closeRadius: 0,
            logger: logger, rawBgra: bgra);

        icons.Should().NotBeEmpty(
            "NaN MinPeakLuma must short-circuit the filter — every blob would otherwise be silently dropped because `peakLuma >= NaN` is always false.");
        logger.WarningCount.Should().BeGreaterThan(0,
            "the NaN short-circuit must LogWarning so a malformed config surfaces immediately.");
    }

    [Fact]
    public void DetectIconBlobs_warns_when_MinPeakLuma_is_set_but_rawBgra_is_null()
    {
        // Review #1169-r2 finding #2: the "looks wired but isn't" gap mithril#1107
        // explicitly cited. When Indoor profile carries MinPeakLuma=0.7 but the
        // engine fails to thread RawBgra, the filter silently no-ops. Warn so
        // the wiring bug is visible from logs alone.
        int w = 20, h = 20;
        var dev = SyntheticDevWithOneBrightBlob(w, h);

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 1000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0)
        {
            MinPeakLuma = 0.7,
        };
        var logger = new RecordingLogger();
        var icons = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, rim: RimMaskMode.None, opts, closeRadius: 0,
            logger: logger, rawBgra: null);

        icons.Should().NotBeEmpty(
            "MinPeakLuma set + rawBgra null must short-circuit the filter so the caller's Outdoor-equivalent recall isn't masquerading as Indoor.");
        logger.WarningCount.Should().BeGreaterThan(0,
            "the silent-disable case is exactly the 'looks wired but isn't' gap CLAUDE.md's instrumentation contract forbids — must LogWarning.");
    }

    [Fact]
    public void DetectIconBlobs_warns_when_100_percent_of_Icon_class_blobs_drop()
    {
        // Review #1169-r2 finding #4: when every Icon-class blob fails the
        // peak-luma gate (dim capture, dark icons, misaligned crop, or BGRA-dim
        // drift), Indoor calibration silently falls back to zero detections.
        // Promote the "kept 0/N when N > 0" case to LogWarning per CLAUDE.md.
        int w = 20, h = 20;
        var dev = SyntheticDevWithOneBrightBlob(w, h);
        // BGRA all-dark (luma ~0) → every blob fails MinPeakLuma=0.7.
        var bgraDark = new byte[w * h * 4];

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 1000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0)
        {
            MinPeakLuma = 0.7,
        };
        var logger = new RecordingLogger();
        var icons = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, rim: RimMaskMode.None, opts, closeRadius: 0,
            logger: logger, rawBgra: bgraDark);

        icons.Should().BeEmpty("every blob is below the 0.7 threshold against a dark BGRA buffer.");
        logger.WarningCount.Should().BeGreaterThan(0,
            "the 100%-drop case is a safe-degrade signal — must LogWarning so the silent-zero-icon failure mode is visible at production log levels.");
    }

    [Fact]
    public void DetectIconBlobs_demotes_100_percent_drop_Warning_to_Trace_when_rotate180_true()
    {
        // Phase 3 follow-up — the 180° base-texture pass on non-mirrored Indoor
        // scenes (PG's common case) legitimately drops every blob because the
        // rotated texture doesn't correlate with the screenshot. The 0° pass
        // owns the signal-bearing failure-mode Warning; the 180° pass demotes
        // to LogTrace to avoid Warning-noise on the live diagnostics surface.
        // See phase-3-live-verification.md for the in-game evidence.
        int w = 20, h = 20;
        var dev = SyntheticDevWithOneBrightBlob(w, h);
        var bgraDark = new byte[w * h * 4];  // all-dark → 100% drop expected.

        var opts = new BlobOptions(MinArea: 4, MaxIconArea: 1000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0)
        {
            MinPeakLuma = 0.7,
        };

        // Sanity-pin the 0° pass — it MUST still LogWarning (sibling test pins
        // the same case at default rotate180=false, but capturing here side-by-
        // side keeps the asymmetry explicit and resistant to a future refactor
        // that accidentally flips the default).
        var logger0 = new RecordingLogger();
        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, rim: RimMaskMode.None, opts, closeRadius: 0,
            logger: logger0, rawBgra: bgraDark, rotate180: false);
        logger0.WarningCount.Should().BeGreaterThan(0,
            "0° pass at 100% drop must still LogWarning — that's the signal-bearing branch.");

        // 180° pass — same inputs, must NOT increment WarningCount.
        var logger180 = new RecordingLogger();
        var icons180 = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, rim: RimMaskMode.None, opts, closeRadius: 0,
            logger: logger180, rawBgra: bgraDark, rotate180: true);

        icons180.Should().BeEmpty("180° pass still drops every blob — only the log severity changed.");
        logger180.WarningCount.Should().Be(0,
            "180° pass at 100% drop is expected on non-mirrored Indoor scenes; the Warning belongs to the 0° pass, not this one. Demoted to LogTrace.");
    }

    /// <summary>Synthesises a deviation map with one bright (high-dev) blob at (5..9, 5..9).</summary>
    private static float[] SyntheticDevWithOneBrightBlob(int w, int h)
    {
        var dev = new float[w * h];
        for (int y = 5; y < 10; y++)
            for (int x = 5; x < 10; x++)
                dev[y * w + x] = 0.95f;
        return dev;
    }

    /// <summary>Synthesises a BGRA buffer where the (5..9, 5..9) region is bright white.</summary>
    private static byte[] SyntheticBrightBgra(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 5; y < 10; y++)
            for (int x = 5; x < 10; x++)
            {
                int o = (y * w + x) * 4;
                bgra[o] = 255; bgra[o + 1] = 255; bgra[o + 2] = 255; bgra[o + 3] = 255;
            }
        return bgra;
    }

    /// <summary>Captures LogWarning count across the DetectIconBlobs path so the guards are testable.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) WarningCount++;
        }
    }

    [Fact]
    public void Bt_601_weights_match_the_canonical_constants()
    {
        // Sanity-pin the BT.601 weights so a future refactor that swaps to a
        // different luma formula (BT.709, simple average) gets caught here.
        // Pure-red pixel: peak = LumaR (~0.299). Pure-green: LumaG (~0.587).
        // Pure-blue: LumaB (~0.114).
        int w = 4, h = 4;
        var bgra = new byte[w * h * 4];
        void Paint(int x, int y, byte b, byte g, byte r)
        {
            int o = (y * w + x) * 4;
            bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255;
        }
        Paint(0, 0, 0, 0, 255); // pure red
        Paint(1, 0, 0, 255, 0); // pure green
        Paint(2, 0, 255, 0, 0); // pure blue

        var redBlob = SinglePixelBlob(0, 0, w);
        var greenBlob = SinglePixelBlob(1, 0, w);
        var blueBlob = SinglePixelBlob(2, 0, w);

        PeakLumaFilter.PeakLuma(redBlob, bgra, w, h).Should().BeApproximately(LumaR, 1e-9,
            "BT.601 R weight = 0.299; matches CapturedFrame.ToGray.");
        PeakLumaFilter.PeakLuma(greenBlob, bgra, w, h).Should().BeApproximately(LumaG, 1e-9,
            "BT.601 G weight = 0.587.");
        PeakLumaFilter.PeakLuma(blueBlob, bgra, w, h).Should().BeApproximately(LumaB, 1e-9,
            "BT.601 B weight = 0.114.");
    }
}
