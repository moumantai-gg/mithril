using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

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
        ILogger? logger = null,
        bool[]? deviationMask = null,
        byte[]? rawBgra = null,
        bool rotate180 = false,
        int openRadius = 0)
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
            // mithril#1126: clone the working array, then wrap the clone in
            // ReadOnlyMemory<bool> so the snapshot's read-only contract is on
            // the type. The orchestrator continues to mutate fg below — the
            // clone keeps the snapshot's view stable.
            hooks.OnDeviation(new DeviationSnapshot(
                Rotate180: false,
                Width: w, Height: h, Win: 11,
                Threshold: devThr, MeanNcc: meanNcc,
                Min: min, Max: max, Mean: mean,
                P50: p50, P95: p95, P99: p99,
                AboveThresholdCount: aboveThresholdCount,
                ForegroundBuffer: ((bool[])fgInitial.Clone()).AsMemory()));
            // Review #1170-r2 finding #4: thread the real rotate180 into the
            // LogTrace text. The snapshot record's Rotate180 field is still
            // hardcoded `false` here and rewritten by the engine's hook-wrap
            // for sink consumers (MapCalibrationSolveEngine.cs:65-101) — that's
            // the existing dual-mechanism pattern. The Trace TEXT is the
            // triager's grep target and was the immediate fix: don't lie on
            // the 180° pass.
            logger?.LogTrace(
                "Deviation (rotate180={Rotate180}): mean ncc={MeanNcc:0.000} above-threshold={AboveCount} of {Total} px (threshold={Threshold:0.000}, p50={P50:0.000} p95={P95:0.000} p99={P99:0.000}).",
                rotate180, meanNcc, aboveThresholdCount, n, devThr, p50, p95, p99);

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
                    Pipeline: RimMaskPipeline.BlobDetection,
                    Rotate180: false,
                    Width: w, Height: h,
                    Threshold: devThr,
                    RimPixelCount: rimCount,
                    FgInputCount: aboveThresholdCount,
                    FgSurvivorCount: survivorCount,
                    RimMaskBuffer: ((bool[])rimMask.Clone()).AsMemory()));
                logger?.LogTrace(
                    "RimMask (rotate180={Rotate180}, pipeline=blob_detection): rim={Rim} of {Total} px, fg pre={Pre} post={Post}.",
                    rotate180, rimCount, n, aboveThresholdCount, survivorCount);
            }
            else
            {
                for (int i = 0; i < n; i++) if (rimMask[i]) fg[i] = false;
            }
        }

        // mithril#1116: deviation-mask subtract — applied AFTER the existing rim
        // subtract, BEFORE the morph-close. The mask combines the texture-alpha-
        // derived floor-boundary band and the screenshot-derived fog-of-war mask
        // (built by DeviationMaskCombiner upstream). Null = pre-#1116 behaviour
        // (byte-identical fg buffer). Mirrors the rim-subtract idiom above —
        // diagnostic counters tally only when the OnDeviationMask sink is wired;
        // the null-hook path skips the tally and is a single subtract loop.
        if (deviationMask is not null)
        {
            if (deviationMask.Length != n)
            {
                // Defensive: dimension mismatch is a silent no-op + LogWarning.
                // The fg buffer is left unchanged so downstream stages can't
                // crash on an out-of-range index from a misaligned producer.
                logger?.LogWarning(
                    "DeviationMask length {Len} != expected {Expected} ({W}x{H}) — skipping subtract.",
                    deviationMask.Length, n, w, h);
            }
            else if (hooks?.OnDeviationMask is not null)
            {
                int maskedCount = 0, fgInputCount = 0, fgSurvivorCount = 0;
                for (int i = 0; i < n; i++) if (fg[i]) fgInputCount++;
                for (int i = 0; i < n; i++)
                {
                    if (deviationMask[i])
                    {
                        maskedCount++;
                        fg[i] = false;
                    }
                }
                for (int i = 0; i < n; i++) if (fg[i]) fgSurvivorCount++;

                hooks.OnDeviationMask(new DeviationMaskSnapshot(
                    Rotate180: false,
                    Width: w, Height: h,
                    MaskPixelCount: maskedCount,
                    FgInputCount: fgInputCount,
                    FgSurvivorCount: fgSurvivorCount,
                    MaskBuffer: ((bool[])deviationMask.Clone()).AsMemory()));
                logger?.LogTrace(
                    "DeviationMask (rotate180={Rotate180}): masked={Masked} of {Total} px, fg pre={Pre} post={Post}.",
                    rotate180, maskedCount, n, fgInputCount, fgSurvivorCount);
            }
            else
            {
                // Null-hook fast path — single subtract loop, no diagnostic tally.
                for (int i = 0; i < n; i++) if (deviationMask[i]) fg[i] = false;
            }
        }

        // mithril#1155 Phase 2.5 — morph-open BEFORE morph-close.
        // OPEN (erode-then-dilate) separates blobs joined by a thin foreground
        // bridge — the IconB+C merge pattern the
        // `indoor-recall-merge-fix-candidates.md` measurement showed isn't
        // reachable via (win, closeRadius) tuning. Sequencing open BEFORE close
        // is structurally important: open first severs the connecting bridge,
        // then close re-stitches each separated icon's own halo. Reversing the
        // order re-merges what open just split.
        //
        // Tallying fgInputCount once at this point covers both stages — the
        // MorphSnapshot's FgInputCount records the pre-OPEN count regardless of
        // which stages fire, so the snapshot's pre/post numbers bracket the
        // ENTIRE morph block. Outdoor with openRadius=0 + closeRadius=1 stays
        // byte-identical to pre-#1155 behaviour: zero allocations from the open
        // branch, snapshot's OpenRadius=0.
        bool morphActive = openRadius > 0 || closeRadius > 0;
        int morphFgInputCount = 0;
        if (morphActive && hooks?.OnMorph is not null)
        {
            for (int i = 0; i < n; i++) if (fg[i]) morphFgInputCount++;
        }

        if (openRadius > 0)
        {
            fg = Morphology.Open(fg, w, h, openRadius);
            if (hooks?.OnMorph is not null)
            {
                int fgAfterOpenCount = 0;
                for (int i = 0; i < n; i++) if (fg[i]) fgAfterOpenCount++;
                logger?.LogTrace(
                    "Morph-open (rotate180={Rotate180}): openRadius={R} fg pre={Pre} post={Post}.",
                    rotate180, openRadius, morphFgInputCount, fgAfterOpenCount);
            }
        }

        if (closeRadius > 0)
        {
            fg = Morphology.Close(fg, w, h, closeRadius);
        }

        if (morphActive && hooks?.OnMorph is not null)
        {
            int fgOutputCount = 0;
            for (int i = 0; i < n; i++) if (fg[i]) fgOutputCount++;
            hooks.OnMorph(new MorphSnapshot(
                Rotate180: false,
                Width: w, Height: h,
                CloseRadius: closeRadius,
                FgInputCount: morphFgInputCount,
                FgOutputCount: fgOutputCount,
                FgAfterMorphBuffer: ((bool[])fg.Clone()).AsMemory())
            {
                OpenRadius = openRadius,
            });
            logger?.LogTrace(
                "Morph (rotate180={Rotate180}): openRadius={Open} closeRadius={Close} fg pre={Pre} post={Post}.",
                rotate180, openRadius, closeRadius, morphFgInputCount, fgOutputCount);
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
                    BlobClass: cls,
                    // Pixels list is passed through — render-only payload (07e PNG
                    // colourmap); not serialised to 10c JSON.
                    Pixels: f.Pixels.ToArray()));
                logger?.LogTrace(
                    "Blob #{Ord} (rotate180={Rotate180}) ({Mx},{My},{W},{H}) area={A} meanDev={MeanDev:0.000} peakDev={PeakDev:0.000} solidity={Sol:0.00} aspect={Asp:0.00} -> {Class}.",
                    f.Ordinal, rotate180, f.MinX, f.MinY, f.W, f.H, f.Area,
                    f.MeanDev, f.PeakDev, f.Solidity, f.Aspect, cls);
            }

            if (cls == BlobClass.Icon) icons.Add(f);
        }

        // mithril#1155 Phase 3 — Indoor peak-luma pre-filter. Both gates must be
        // open for the filter to fire: opts.MinPeakLuma carries the threshold
        // (null in Outdoor profile / pre-#1155 callers) and rawBgra carries the
        // raw screenshot bytes (null when the engine hasn't threaded them).
        // Either null short-circuits to byte-identical pre-#1155 behaviour for
        // the legacy paths. Filter runs AFTER classification so the diagnostic
        // hook above sees the pre-luma BlobClassification for ALL Icon-class
        // blobs — the post-filter drop is logged at Trace per blob and surfaced
        // as a Warning when 100% of Icon-class blobs drop (the silent-zero-icon
        // case per CLAUDE.md's instrumentation contract) so the bundle record
        // + the log line together tell "classified Icon but rejected by peak-
        // luma at value X."
        //
        // Three observability guards (review #1169-r2):
        //   1. NaN MinPeakLuma — `is { }` matches NaN, but `>= NaN` is always
        //      false, so the loop would silently drop every blob. Guard with
        //      IsFinite + Warning so a malformed config surfaces immediately.
        //   2. MinPeakLuma set but rawBgra null — Indoor profile expected to
        //      filter but the engine didn't thread the buffer. Warn loudly so
        //      the "looks wired but isn't" failure (mithril#1107) is visible.
        //   3. 100% drop with icons.Count > 0 — every Icon-class blob rejected;
        //      Indoor calibration about to fail with "no detections." Promote
        //      to Warning so a triager can correlate against the gate decision.
        if (opts.MinPeakLuma is { } minPeakLuma)
        {
            if (!double.IsFinite(minPeakLuma))
            {
                logger?.LogWarning(
                    "PeakLumaFilter: MinPeakLuma is {Value} (not finite) — skipping the filter to avoid silently dropping every blob. Fix the profile config.",
                    minPeakLuma);
                return icons;
            }
            if (rawBgra is null)
            {
                logger?.LogWarning(
                    "PeakLumaFilter: MinPeakLuma is set to {Threshold:0.00} but rawBgra is null — filter no-ops. Indoor profile expected the engine to thread raw BGRA via DetectionRequest.RawBgra. Caller-side wiring bug.",
                    minPeakLuma);
                return icons;
            }
            if (icons.Count == 0)
            {
                // Review #1170-r2 finding #12: when the upstream classifier
                // already rejected every blob, the peak-luma filter has nothing
                // to do — but a triager scanning the log on a "no detections"
                // attempt needs to distinguish "classifier rejected all" from
                // "filter rejected all" per orientation. Emit a Trace so the
                // gap doesn't silently swallow the orientation-tagged signal.
                logger?.LogTrace(
                    "PeakLumaFilter (rotate180={Rotate180}): skipped — 0 Icon-class candidates from upstream classifier (threshold {Threshold:0.00}).",
                    rotate180, minPeakLuma);
            }
            if (icons.Count > 0)
            {
                var survivors = new List<BlobFeat>(icons.Count);
                int dropped = 0;
                foreach (var blob in icons)
                {
                    double peakLuma = PeakLumaFilter.PeakLuma(blob, rawBgra, w, h, logger);
                    if (peakLuma >= minPeakLuma)
                    {
                        survivors.Add(blob);
                    }
                    else
                    {
                        dropped++;
                        // Review #1170-r2 finding #6: thread rotate180 into the
                        // per-blob drop Trace so triagers can correlate by
                        // orientation without chaining trace_id / span_id.
                        logger?.LogTrace(
                            "Blob #{Ord} (rotate180={Rotate180}) ({Mx},{My},{W},{H}) area={A}: dropped by peak-luma filter (peakLuma={PeakLuma:0.000} < threshold {Threshold:0.00}).",
                            blob.Ordinal, rotate180, blob.MinX, blob.MinY, blob.W, blob.H, blob.Area,
                            peakLuma, minPeakLuma);
                    }
                }
                if (survivors.Count == 0 && icons.Count > 0)
                {
                    // mithril#1155 Phase 3 follow-up — scope the Warning to the
                    // 0° pass. The 180° pass on non-mirrored Indoor scenes (PG's
                    // common case) legitimately drops every blob because the
                    // rotated texture doesn't correlate with the screenshot; the
                    // 0° pass is the signal-bearing branch where 100%-drop IS a
                    // real "calibration will fail" signal. Per-orientation phase-3-
                    // live-verification.md confirmed the 0° pass produces 3 valid
                    // detections while the 180° pass legitimately rejects all 40.
                    //
                    // Review #1170-r2 finding #1 (altitude): the right home for
                    // this Warning is the engine layer (which knows BOTH
                    // orientations' results — "no detections after both passes"
                    // is the actual user-visible failure). Documenting the
                    // architectural debt explicitly; deferred because moving the
                    // Warning to the engine is a larger refactor (DetectionRequest
                    // contract change, engine-layer Warning emission, possibly a
                    // structured detector-event sink) that's out of scope for the
                    // immediate "stop false-positive 180° Warning" fix.
                    //
                    // Review #1170-r2 finding #3: unify `(rotate180={Rotate180})`
                    // template across all three log lines so the structured
                    // property is consistently queryable by OTLP / jq / Seq.
                    if (rotate180)
                    {
                        logger?.LogTrace(
                            "PeakLumaFilter (rotate180={Rotate180}): rejected ALL {Total} Icon-class blobs (threshold {Threshold:0.00}). Expected on non-mirrored Indoor scenes — the 0° pass owns the failure-mode Warning.",
                            rotate180, icons.Count, minPeakLuma);
                    }
                    else
                    {
                        logger?.LogWarning(
                            "PeakLumaFilter (rotate180={Rotate180}): rejected ALL {Total} Icon-class blobs (threshold {Threshold:0.00}). Indoor calibration will fail downstream with 'no detections'; check for upstream BGRA-dim drift, an unexpectedly dim capture, or a misaligned crop.",
                            rotate180, icons.Count, minPeakLuma);
                    }
                }
                else
                {
                    logger?.LogTrace(
                        "PeakLumaFilter (rotate180={Rotate180}): kept {Kept}/{Total} Icon-class blobs (threshold {Threshold:0.00}, dropped {Dropped}).",
                        rotate180, survivors.Count, icons.Count, minPeakLuma, dropped);
                }
                return survivors;
            }
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
/// <remarks>
/// <para>The positional fields gate blobs at the shape/size + deviation-peak layer:
/// <c>MinPeak</c> is the maximum value the blob reaches in the NCC deviation map
/// (a derived signal, not raw BGRA).</para>
///
/// <para>mithril#1155 Phase 3: <see cref="MinPeakLuma"/> is the post-classification
/// raw-BGRA-luma gate added as an init-only property so the ~25 existing positional
/// call sites compile unchanged. Per the
/// <c>indoor-recall-stage-attribution.md</c> §E finding ("real-icon blobs all
/// have PeakLuma &gt; 0.78 in their raw-BGRA bbox; floor-noise Icon-class blobs
/// are at 0.22–0.40"), a single threshold cleanly separates real Indoor icons
/// from the residual floor-noise blobs that survive T1+T2's relaxed classifier
/// gates. <c>null</c> = pre-filter disabled (byte-identical pre-#1155 behaviour);
/// Outdoor profile leaves it <c>null</c> so the filter is a no-op outdoors.</para>
/// </remarks>
public readonly record struct BlobOptions(
    int MinArea, int MaxIconArea, double MinSolidity, double MaxAspect, double MinPeak)
{
    /// <summary>
    /// Indoor peak-luma pre-filter threshold (mithril#1155 Phase 3). When non-null,
    /// blobs whose raw-BGRA bbox peak luma is below this value are dropped AFTER
    /// classification but BEFORE the per-blob NCC typing step. Luma is BT.601:
    /// <c>0.114·B + 0.587·G + 0.299·R</c>, normalized to <c>[0, 1]</c>; the peak
    /// is the maximum luma over the blob's connected-component pixels in the
    /// raw BGRA screenshot.
    ///
    /// <para>Disabled when null — the legacy pre-#1155 path. The filter is also
    /// a no-op when the caller doesn't supply a raw BGRA buffer (engine-layer
    /// gating: a missing buffer falls back to "filter skipped" rather than
    /// throwing).</para>
    /// </summary>
    public double? MinPeakLuma { get; init; }
}

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
    ///
    /// <para>mithril#1126: marked <c>required</c> so a direct
    /// <c>new BlobFeat()</c> at a future call site is a compile error rather
    /// than a silent <c>ordinal = 0</c> that breaks the unified ordinal space.
    /// All blobs go through <c>ConnectedComponents.Label</c> today; this guards
    /// any future alternate construction path.</para>
    /// </summary>
    public required int Ordinal { get; init; }
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
            // mithril#1123 D3.a / #1126: the all-blobs ordinal is the 8-connected
            // emission order — same int that BlobTemplateScore (#1121) and
            // BlobClassification (#1123) reference. Captured BEFORE the flood
            // fills the comp so it can be set via required-init at construction.
            var f = new BlobFeat { Ordinal = comps.Count };
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
            comps.Add(f);
        }
        return comps;
    }
}

/// <summary>
/// Square-element binary morphology helpers.
///
/// <para><see cref="Close"/> (dilate then erode) bridges fragmented icon
/// pixels into a single component without growing the overall footprint.</para>
///
/// <para><see cref="Open"/> (erode then dilate; mithril#1155 Phase 2.5) is the
/// dual — strips foreground pixels off the boundary of every component and
/// then regrows each survivor by the same radius. The operation severs
/// components joined by a connecting bridge thinner than 2r+1 pixels while
/// preserving the bulk of components whose minimum thickness exceeds the
/// kernel.</para>
///
/// <para><b>Why this exists despite shipping at <c>openRadius=0</c>.</b> Open
/// was investigated as a candidate to split the Indoor IconB+C merge that
/// blocks calibration acceptance, then measured to NOT help. The 2026-06-13
/// + 2026-06-15 Hogan's bundles confirmed the merged-blob "bridge" between
/// adjacent NPC pips is NOT a thin filament — it's the overlapping deviation
/// halos of the two pips themselves (LocalNcc <c>win=11</c> extends each
/// ~16-px pip's footprint by ~5 px on every side, so pips ~27-29 px apart
/// overlap by geometric necessity). Open cannot distinguish "halo edge" from
/// "bridge edge" because they're the same pixels; aggressive erosion collapses
/// real-icon recall instead of splitting the merge. The carrier ships
/// disabled so a future investigator can flip the value once a structurally
/// different mechanism (e.g. pre-deviation luma threshold, watershed split)
/// addresses the merge upstream. Full measurement: see
/// <c>indoor-recall-phase-2.5-morph-open.md</c> Findings 1, 4, and 5.</para>
///
/// <para>Outdoor callers and any caller that passes <c>openRadius=0</c> get
/// byte-identical pre-#1155 behaviour — the open branch short-circuits before
/// any allocation.</para>
/// </summary>
internal static class Morphology
{
    public static bool[] Close(bool[] src, int w, int h, int r)
    {
        var dil = Dilate(src, w, h, r);
        return Erode(dil, w, h, r);
    }

    /// <summary>
    /// Square-element morphological OPEN: erode by <paramref name="r"/> then
    /// dilate by <paramref name="r"/>. Drops foreground "spikes" thinner than
    /// 2r+1 along the connecting axis and disconnects components joined by a
    /// thin bridge, while preserving the bulk of components whose minimum
    /// thickness exceeds the kernel.
    /// </summary>
    public static bool[] Open(bool[] src, int w, int h, int r)
    {
        var er = Erode(src, w, h, r);
        return Dilate(er, w, h, r);
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

    /// <summary>
    /// Square-element binary erosion (mithril#1183 review C21: promoted from
    /// private to internal so dilation-sweep tests can share the canonical
    /// implementation instead of cloning the pixel-walking nested loop).
    /// </summary>
    internal static bool[] Erode(bool[] s, int w, int h, int r)
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
