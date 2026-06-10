using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

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
    ///
    /// <para><paramref name="hooks"/> threads the mithril#1123 per-stage
    /// observability sinks. When non-null, the orchestrator retains the
    /// intermediate buffers (fg-initial, rim mask, fg-after-morph) and emits
    /// one record per stage + one record per classified comp (ALL comps, not
    /// just Icon). Every bool[] (and float[]) handed to a snapshot is a
    /// <c>.Clone()</c> of the working buffer — the orchestrator continues to
    /// mutate <c>fg</c> after emission (rim subtract, morph close), so
    /// without cloning the snapshot's buffer would silently change to the
    /// post-mutation state. Producer cost is zero when <paramref name="hooks"/>
    /// is null.</para>
    ///
    /// <para><paramref name="meanNcc"/> is the mean NCC over the deviation
    /// window — computed by <c>LocalNccDeviation.DeviationMap</c>'s
    /// <c>out</c> param and threaded through here for <see cref="DeviationSnapshot.MeanNcc"/>.
    /// Defaults to <see cref="double.NaN"/> for callers that don't compute it.</para>
    /// </summary>
    public static IReadOnlyList<BlobFeat> DetectIconBlobs(
        float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
        DetectionDiagnosticHooks? hooks = null,
        double meanNcc = double.NaN,
        ILogger? logger = null)
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

        // Spec D9: zero producer cost when Diagnostics == null. On the null-hook
        // path we allocate ONE bool[] (the working fg buffer that subsequent
        // stages mutate). When OnDeviation IS wired we also allocate a separate
        // fgInitial bool[] + clone, because the snapshot must capture the
        // pre-rim-subtract / pre-morph state — the orchestrator continues to
        // mutate fg afterwards.
        bool[] fg;
        int aboveThresholdCount = 0;
        if (hooks?.OnDeviation is not null)
        {
            // fg-initial: the threshold output retained for the snapshot.
            var fgInitial = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (dev[i] >= devThr)
                {
                    fgInitial[i] = true;
                    aboveThresholdCount++;
                }
            }

            // Emit DeviationSnapshot — fires after the threshold step, with the
            // dev float[] still in scope for the stats sweep. The LogTrace mirror
            // matches the sink cadence (Task 3) so a triager can watch the pipeline
            // in real-time without waiting for the bundle to write.
            var (min, max, mean, p50, p95, p99) = ComputeDeviationStats(dev);
            hooks.OnDeviation(new DeviationSnapshot(
                Rotate180: false,
                Width: w, Height: h, Win: 11,
                Threshold: devThr, MeanNcc: meanNcc,
                Min: min, Max: max, Mean: mean,
                P50: p50, P95: p95, P99: p99,
                AboveThresholdCount: aboveThresholdCount,
                ForegroundBuffer: (bool[])fgInitial.Clone()));
            logger?.LogTrace(
                "Deviation (rotate180=False): mean ncc={MeanNcc:0.000} above-threshold={AboveCount} of {Total} px (threshold={Threshold:0.000}, p50={P50:0.000} p95={P95:0.000} p99={P99:0.000}).",
                meanNcc, aboveThresholdCount, n, devThr, p50, p95, p99);

            // fg is the working buffer that subsequent stages mutate; fgInitial
            // is retained by the snapshot.
            fg = (bool[])fgInitial.Clone();
        }
        else
        {
            // Null-hook fast path: skip the fg-initial bool[] + clone — write the
            // threshold output directly into the working buffer. aboveThresholdCount
            // is still tallied because the OnRimMask sink (independently null-able)
            // reports it as FgInputCount when wired.
            fg = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (dev[i] >= devThr)
                {
                    fg[i] = true;
                    aboveThresholdCount++;
                }
            }
        }

        if (rim == RimMaskMode.DeviationFlood)
        {
            var rimMask = DeviationFloodRimMask.Build(dev, w, h, devThr);

            // Hot inner loop kept at minimum cost on the null-hook path: rim
            // subtract is unconditional (it's a real decision branch), but the
            // diagnostic counters only tally when the sink is wired.
            if (hooks?.OnRimMask is not null)
            {
                int rimCount = 0, survivorCount = 0;
                for (int i = 0; i < n; i++)
                {
                    if (rimMask[i])
                    {
                        rimCount++;
                        fg[i] = false;
                    }
                    if (fg[i]) survivorCount++;
                }
                hooks.OnRimMask(new RimMaskSnapshot(
                    Pipeline: "blob_detection",
                    Rotate180: false,
                    Width: w, Height: h,
                    Threshold: devThr,
                    RimPixelCount: rimCount,
                    FgInputCount: aboveThresholdCount,
                    FgSurvivorCount: survivorCount,
                    RimMaskBuffer: (bool[])rimMask.Clone()));
                logger?.LogTrace(
                    "RimMask (rotate180=False, pipeline=blob_detection): rim={Rim} of {Total} px, fg pre={Pre} post={Post}.",
                    rimCount, n, aboveThresholdCount, survivorCount);
            }
            else
            {
                for (int i = 0; i < n; i++) if (rimMask[i]) fg[i] = false;
            }
        }

        if (closeRadius > 0)
        {
            int fgInputCount = 0;
            if (hooks?.OnMorph is not null)
            {
                for (int i = 0; i < n; i++) if (fg[i]) fgInputCount++;
            }

            fg = Morphology.Close(fg, w, h, closeRadius);

            if (hooks?.OnMorph is not null)
            {
                int fgOutputCount = 0;
                for (int i = 0; i < n; i++) if (fg[i]) fgOutputCount++;
                hooks.OnMorph(new MorphSnapshot(
                    Rotate180: false,
                    Width: w, Height: h,
                    CloseRadius: closeRadius,
                    FgInputCount: fgInputCount,
                    FgOutputCount: fgOutputCount,
                    FgAfterMorphBuffer: (bool[])fg.Clone()));
                logger?.LogTrace(
                    "Morph (rotate180=False): closeRadius={R} fg pre={Pre} post={Post}.",
                    closeRadius, fgInputCount, fgOutputCount);
            }
        }

        var comps = ConnectedComponents.Label(fg, w, h, dev);

        var icons = new List<BlobFeat>();
        foreach (var f in comps)
        {
            var cls = Classify(f, opts);

            // mithril#1123: emit per-comp classification BEFORE the Icon gate —
            // all comps including Noise/Fog/Structure, since "why did this blob
            // get classified as Noise?" is exactly the triage question.
            if (hooks?.OnBlobClassified is not null)
            {
                hooks.OnBlobClassified(new BlobClassification(
                    Rotate180: false,
                    BlobOrdinal: f.Ordinal,
                    MinX: f.MinX, MinY: f.MinY,
                    W: f.W, H: f.H, Area: f.Area,
                    Cx: f.Cx, Cy: f.Cy,
                    MeanDev: f.MeanDev, PeakDev: f.PeakDev,
                    Solidity: f.Solidity, Aspect: f.Aspect,
                    BlobClass: cls.ToString(),
                    // Pixels list is passed through — render-only payload (07e PNG
                    // colourmap); not serialised to 10c JSON.
                    Pixels: f.Pixels.ToArray()));
                logger?.LogTrace(
                    "Blob #{Ord} ({Mx},{My},{W},{H}) area={A} meanDev={MeanDev:0.000} peakDev={PeakDev:0.000} solidity={Sol:0.00} aspect={Asp:0.00} -> {Class}.",
                    f.Ordinal, f.MinX, f.MinY, f.W, f.H, f.Area,
                    f.MeanDev, f.PeakDev, f.Solidity, f.Aspect, cls);
            }

            if (cls == BlobClass.Icon) icons.Add(f);
        }
        return icons;
    }

    /// <summary>
    /// Sweep the deviation float[] for distribution stats: min, max, mean, and
    /// 50/95/99 percentiles. Used at <see cref="DeviationSnapshot"/> emission
    /// time only (zero cost when no hook is wired). Cost: O(n log n) for the
    /// percentile sort on a copy — Hogan's 458k pixels sorts in &lt;50 ms; fires
    /// once per orientation pass.
    /// </summary>
    private static (double Min, double Max, double Mean, double P50, double P95, double P99)
        ComputeDeviationStats(float[] dev)
    {
        if (dev.Length == 0) return (0, 0, 0, 0, 0, 0);

        double sum = 0;
        float min = dev[0], max = dev[0];
        for (int i = 0; i < dev.Length; i++)
        {
            var v = dev[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        double mean = sum / dev.Length;

        var sorted = (float[])dev.Clone();
        Array.Sort(sorted);
        // Guard upper-bound — *0.99 can equal Length on tiny inputs.
        int last = sorted.Length - 1;
        int p50Idx = Math.Min(last, (int)(sorted.Length * 0.50));
        int p95Idx = Math.Min(last, (int)(sorted.Length * 0.95));
        int p99Idx = Math.Min(last, (int)(sorted.Length * 0.99));
        return (min, max, mean, sorted[p50Idx], sorted[p95Idx], sorted[p99Idx]);
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
