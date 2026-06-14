using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Pure-BCL post-classification filter (mithril#1155 Phase 3): computes the
/// maximum BT.601 luma over a blob's connected-component pixels in the raw BGRA
/// screenshot, then admits or rejects the blob against
/// <see cref="BlobOptions.MinPeakLuma"/>.
///
/// <para><b>Why this exists.</b> The spike measurement
/// (<c>indoor-chroma-threshold.md</c>) showed that PG Indoor icons render as
/// bright-white glyphs (PeakLuma 0.91) while floor noise sits at mid-gray
/// (PeakLuma 0.22–0.40). The follow-up stage-attribution audit
/// (<c>indoor-recall-stage-attribution.md</c> §E) generalised the finding:
/// real-icon blobs across the Indoor corpus all carry PeakLuma &gt; 0.78, and
/// floor-noise Icon-class blobs sit ≤ 0.40. A 0.7 threshold cleanly separates
/// the populations and suppresses the residual noise blobs that survive T1+T2's
/// relaxed classifier gates on the Indoor profile.</para>
///
/// <para><b>BT.601 luma.</b> Mirrors <c>CapturedFrame.ToGray</c>'s weights
/// (<c>0.114·B + 0.587·G + 0.299·R</c>) so the threshold's intuition matches
/// what the rest of the detector sees — the gray buffer the deviation map runs
/// on uses the same weights. Normalised to <c>[0, 1]</c> for symmetry with
/// <c>BlobFeat.PeakDev</c>.</para>
///
/// <para><b>BCL only.</b> Pure pixel arithmetic over the BGRA byte array; no
/// decoder, no allocations beyond local doubles. Producer cost is paid only
/// when <see cref="BlobOptions.MinPeakLuma"/> is non-null AND the caller threads
/// a raw BGRA buffer — both gates are checked by the call site
/// (<c>DeviationBlobDetector.DetectIconBlobs</c>).</para>
/// </summary>
internal static class PeakLumaFilter
{
    /// <summary>BT.601 luma weights matching <c>CapturedFrame.ToGray</c>.</summary>
    private const double LumaR = 0.299;
    private const double LumaG = 0.587;
    private const double LumaB = 0.114;

    /// <summary>
    /// Returns the peak BT.601 luma over <paramref name="blob"/>'s connected
    /// component pixels in <paramref name="bgra"/>, normalised to <c>[0, 1]</c>.
    ///
    /// <para>Each pixel index in <see cref="BlobFeat.Pixels"/> is a row-major
    /// offset into a <paramref name="width"/>×<paramref name="height"/> image
    /// (the same convention <c>ConnectedComponents.Label</c> uses). The BGRA
    /// byte at pixel <c>i</c> is <c>bgra[i*4]</c> (B), <c>bgra[i*4+1]</c> (G),
    /// <c>bgra[i*4+2]</c> (R); alpha is ignored.</para>
    ///
    /// <para>Returns <c>0.0</c> on a dimension mismatch (<paramref name="bgra"/>
    /// length doesn't match <paramref name="width"/>×<paramref name="height"/>×4)
    /// — the same fail-soft convention as the rest of the detector (a misaligned
    /// producer can't crash the pipeline). When <paramref name="logger"/> is
    /// non-null, also emits a single <c>LogWarning</c>; the production caller
    /// (<c>DeviationBlobDetector.DetectIconBlobs</c>) always supplies one, so
    /// the misalignment surfaces in the diagnostic stream. Test/measurement
    /// callers may omit the logger for an arithmetic-only invocation.</para>
    ///
    /// <para>Returns <c>0.0</c> for an empty blob; the caller treats that as a
    /// drop when <see cref="BlobOptions.MinPeakLuma"/> &gt; 0.</para>
    /// </summary>
    public static double PeakLuma(
        BlobFeat blob, byte[] bgra, int width, int height, ILogger? logger = null)
    {
        // Width/height are validated against bgra.Length in long arithmetic so a
        // pathological producer (negative dims, overflow at width*height*4) hits
        // the documented fail-soft path (LogWarning + return 0.0) rather than
        // an OverflowException — see review #1169-r2 finding #6.
        long expectedLen = (long)width * (long)height * 4L;
        if (width < 0 || height < 0 || bgra.LongLength != expectedLen)
        {
            logger?.LogWarning(
                "PeakLumaFilter: bgra length {Len} != expected {Expected} ({W}x{H}x4) — returning 0.0.",
                bgra.LongLength, expectedLen, width, height);
            return 0.0;
        }

        if (blob.Pixels.Count == 0) return 0.0;

        double peak = 0.0;
        foreach (int i in blob.Pixels)
        {
            int o = i * 4;
            double l = LumaB * bgra[o] + LumaG * bgra[o + 1] + LumaR * bgra[o + 2];
            if (l > peak) peak = l;
        }
        return peak / 255.0;
    }
}
