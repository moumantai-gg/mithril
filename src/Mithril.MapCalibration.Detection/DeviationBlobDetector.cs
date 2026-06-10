using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Shape/size filter that turns the "all added content" deviation map into a
/// clean icon-candidate set (mithril#897). Pipeline: threshold deviation →
/// [deviation-flood rim mask] → [morphological close] → connected components →
/// per-blob features → classify icon vs fog vs structure vs noise.
///
/// <para>Icons render ~16 px and, with the local-NCC window smearing the boundary
/// by ±(window/2), produce compact high-peak blobs roughly 12-30 px across.
/// Fog-of-war is large, soft, low-gradient. Structures (keeps, labels) are large
/// and elongated/high-contrast. Size is the primary separator; solidity + aspect
/// + peak-deviation reject labels, fragmented terrain noise, and soft fog.</para>
///
/// <para>Lifted from the gate-study probe's <c>BlobFilter.BlobStage</c> (classify
/// path only — the render / CSV / ground-truth / typing bits stay in the tool).
/// BCL-only.</para>
/// </summary>
public static class DeviationBlobDetector
{
    /// <summary>
    /// Detect icon-candidate blobs in a deviation map. <paramref name="rim"/>
    /// selects how the irregular map rim is excluded; <see cref="RimMaskMode.ColourFlood"/>
    /// is not available on this overload (it needs the BGRA screenshot) and
    /// throws — use <see cref="RimMaskMode.DeviationFlood"/> or
    /// <see cref="RimMaskMode.None"/>.
    /// </summary>
    public static IReadOnlyList<BlobFeat> DetectIconBlobs(
        float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius)
    {
        if (rim == RimMaskMode.ColourFlood)
        {
            throw new ArgumentException(
                "ColourFlood rim masking needs the BGRA screenshot; not available on the deviation-only overload. " +
                "Use DeviationFlood (preferred) or None.",
                nameof(rim));
        }

        int n = w * h;
        double devThr = 1.0 - lowNcc;  // dev >= devThr  <=>  ncc <= lowNcc

        var fg = new bool[n];
        for (int i = 0; i < n; i++) if (dev[i] >= devThr) fg[i] = true;

        if (rim == RimMaskMode.DeviationFlood)
        {
            // Edge-connected deviation flood: see DeviationFloodRimMask docs.
            var rimMask = DeviationFloodRimMask.Build(dev, w, h, devThr);
            for (int i = 0; i < n; i++) if (rimMask[i]) fg[i] = false;
        }

        if (closeRadius > 0)
        {
            fg = Morphology.Close(fg, w, h, closeRadius);
        }

        var comps = ConnectedComponents.Label(fg, w, h, dev);

        var icons = new List<BlobFeat>();
        foreach (var f in comps)
        {
            if (Classify(f, opts) == BlobClass.Icon) icons.Add(f);
        }
        return icons;
    }

    internal static BlobClass Classify(BlobFeat f, BlobOptions o)
    {
        if (f.Area < o.MinArea) return BlobClass.Noise;
        bool iconBand = f.Area <= o.MaxIconArea;
        if (iconBand && f.Solidity >= o.MinSolidity && f.Aspect <= o.MaxAspect && f.PeakDev >= o.MinPeak)
            return BlobClass.Icon;
        if (f.Area > o.MaxIconArea)
        {
            // Large blobs are all rejected; the split mirrors the tool's
            // visualization. Structures (keep, labels) are elongated or sharply
            // deviating; fog-of-war is a large, soft, low-gradient region.
            if (f.Aspect >= 2.2 || f.MeanDev >= 0.6) return BlobClass.Structure;
            return BlobClass.Fog;
        }
        return BlobClass.Noise;  // icon-sized but failed a shape gate
    }
}

/// <summary>Blob classification: terrain noise, an icon candidate, soft fog, or a large structure.</summary>
public enum BlobClass { Noise, Icon, Fog, Structure }

/// <summary>Shape/size thresholds for <see cref="DeviationBlobDetector.DetectIconBlobs"/>.</summary>
public readonly record struct BlobOptions(
    int MinArea, int MaxIconArea, double MinSolidity, double MaxAspect, double MinPeak);

/// <summary>Per-blob geometry + deviation stats. Pixel list retained for downstream typing/rendering.</summary>
public sealed class BlobFeat
{
    public List<int> Pixels { get; } = new();
    public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;
    public double SumX, SumY, SumDev, PeakDev;
    public int Area => Pixels.Count;
    public int W => MaxX - MinX + 1;
    public int H => MaxY - MinY + 1;
    public double Cx => SumX / Area;
    public double Cy => SumY / Area;
    public double MeanDev => SumDev / Area;
    public double Solidity => (double)Area / Math.Max(1, W * H);
    public double Aspect => (double)Math.Max(W, H) / Math.Max(1, Math.Min(W, H));

    /// <summary>
    /// Index over the 8-connected emission order produced by
    /// <see cref="ConnectedComponents.Label"/> — the same ordinal space carried
    /// by <c>BlobTemplateScore.BlobOrdinal</c> (#1121) and
    /// <c>BlobClassification.BlobOrdinal</c> (#1123). Set by
    /// <see cref="ConnectedComponents.Label"/> during blob emission so the
    /// detector's per-template scores and the pipeline-observability dump
    /// reference the same physical blob with the same int.
    /// </summary>
    public int Ordinal;
}

/// <summary>
/// 8-connected component labelling over a boolean foreground mask. Iterative
/// stack (no recursion — a single large fog/border component can span 100k+ px).
/// </summary>
internal static class ConnectedComponents
{
    public static List<BlobFeat> Label(bool[] fg, int w, int h, float[] dev)
    {
        var seen = new bool[fg.Length];
        var comps = new List<BlobFeat>();
        var stack = new Stack<int>();
        for (int start = 0; start < fg.Length; start++)
        {
            if (!fg[start] || seen[start]) continue;
            var f = new BlobFeat();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % w, py = p / w;
                f.Pixels.Add(p);
                f.SumX += px; f.SumY += py; f.SumDev += dev[p];
                if (dev[p] > f.PeakDev) f.PeakDev = dev[p];
                if (px < f.MinX) f.MinX = px;
                if (px > f.MaxX) f.MaxX = px;
                if (py < f.MinY) f.MinY = py;
                if (py > f.MaxY) f.MaxY = py;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int qi = ny * w + nx;
                        if (fg[qi] && !seen[qi]) { seen[qi] = true; stack.Push(qi); }
                    }
            }
            // mithril#1123 D3.a: assign the all-blobs ordinal in 8-connected
            // emission order — same int that BlobTemplateScore (#1121) and
            // BlobClassification (#1123) reference.
            f.Ordinal = comps.Count;
            comps.Add(f);
        }
        return comps;
    }
}

/// <summary>
/// Square-element morphological close (dilate then erode) to bridge fragmented
/// icon pixels into a single component without growing the overall footprint.
/// </summary>
internal static class Morphology
{
    public static bool[] Close(bool[] src, int w, int h, int r)
    {
        var dil = Dilate(src, w, h, r);
        return Erode(dil, w, h, r);
    }

    private static bool[] Dilate(bool[] s, int w, int h, int r)
    {
        var o = new bool[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!s[y * w + x]) continue;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h) o[ny * w + nx] = true;
                    }
            }
        return o;
    }

    private static bool[] Erode(bool[] s, int w, int h, int r)
    {
        var o = new bool[s.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool all = true;
                for (int dy = -r; dy <= r && all; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h || !s[ny * w + nx]) { all = false; break; }
                    }
                o[y * w + x] = all;
            }
        return o;
    }
}
