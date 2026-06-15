using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// The proven sparse-area detection front-end (mithril#897 verdict): texture-
/// deviation local-NCC → shape/size filter (with the deviation-flood rim mask) →
/// per-blob type-aware template NCC. Replaces the gate-study probe→CSV→calibrator
/// hand-off with a single in-process path.
///
/// <list type="number">
///   <item>Local-NCC deviation map of screenshot vs aligned base texture
///         (<c>addedOnly: true</c> — only flag content added on the screenshot
///         side, the icons).</item>
///   <item>Shape/size filter with the deviation-flood rim mask → icon-candidate
///         blobs.</item>
///   <item>Type each blob via template NCC within its padded bbox; the best
///         template ≥ <see cref="DetectionRequest.TypeFloor"/> assigns the blob's
///         landmark type + pivot-corrected anchor (the §8 "type the blobs +
///         per-blob TypeFloor" pairing — deviation-rim alone is not enough).</item>
/// </list>
/// BCL-only.
///
/// <para><b>Diagnostic observability (mithril#1121).</b> When the request carries
/// a <see cref="DetectionRequest.BlobScoreSink"/>, every (blob, template) pair
/// considered emits a <see cref="BlobTemplateScore"/> — both the skip path
/// (template too large for the padded crop) and the scored path (absolute best
/// NCC peak in the crop, irrespective of <see cref="DetectionRequest.TypeFloor"/>).
/// The same data is mirrored to <see cref="LogLevel.Trace"/> via the optional
/// logger. Used to pick between three different NPC-pip-recall fixes (threshold
/// vs template-quality vs pipeline-bug) instead of inferring from end-state.
/// Producer cost is zero when the sink is null and no Trace listener is
/// attached.</para>
/// </summary>
public sealed class DeviationBlobCalibrationDetector : ICalibrationDetector
{
    private readonly ILogger? _logger;

    public DeviationBlobCalibrationDetector(ILogger? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request)
    {
        int w = request.Screenshot.Width;
        int h = request.Screenshot.Height;

        var shotF = LocalNccDeviation.ToGrayFloat(request.Screenshot);
        var texF = LocalNccDeviation.ToGrayFloat(request.BaseTexture);

        // Window 11 mirrors the gate-study probe default.
        // mithril#1123: capture meanNcc — DeviationSnapshot.MeanNcc surfaces it
        // alongside the per-pixel deviation stats.
        var dev = LocalNccDeviation.DeviationMap(shotF, texF, w, h, win: 11, out var meanNcc, addedOnly: true);

        // The deviation-only overload can't run ColourFlood (needs the BGRA shot);
        // fall back to DeviationFlood if asked for ColourFlood here.
        var rim = request.RimMask == RimMaskMode.ColourFlood ? RimMaskMode.DeviationFlood : request.RimMask;
        // mithril#1123: thread the per-stage diagnostic hooks (and meanNcc) into
        // the deepest orchestrator layer — DetectIconBlobs owns the dev[], fg[],
        // rim, morph, classify intermediate buffers and is where stage records
        // are emitted. null hooks → zero producer cost. The logger is passed
        // through too so the static helper's LogTrace mirror fires under the
        // same "Mithril.MapCalibration.Detection" category as the per-(blob,
        // template) lines from EmitDiagnostic.
        var blobs = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, request.LowNcc, rim, request.BlobOptions, closeRadius: 1,
            hooks: request.Diagnostics,
            meanNcc: meanNcc,
            logger: _logger,
            // mithril#1116: combined boundary+fog mask threaded from the request.
            // Null when no mask was built (pre-#1116 paths / disabled by settings)
            // — DetectIconBlobs treats null as no-op so behavior is unchanged.
            deviationMask: request.DeviationMask,
            // mithril#1155 Phase 3: raw BGRA backing the Indoor peak-luma pre-
            // filter. Null on Outdoor profile / pre-#1155 callers — DetectIconBlobs
            // short-circuits the filter when either rawBgra is null or
            // BlobOptions.MinPeakLuma is null, so legacy paths are byte-identical.
            rawBgra: request.RawBgra,
            // mithril#1155 Phase 3 follow-up: forward orientation so the
            // 100%-drop LogWarning demotes to LogTrace on the 180° pass
            // (non-mirrored Indoor scenes legitimately drop everything there;
            // the 0° pass is the signal-bearing branch).
            rotate180: request.Rotate180,
            // mithril#1155 Phase 2.5: morph-open BEFORE morph-close. Sourced
            // from SceneCalibrationProfile.MorphOpenRadiusPx via the engine.
            // 0 (the default on every profile in v1 per the negative-result
            // Phase 2.5 measurement) preserves the byte-identical pre-#1155
            // pipeline. Carrier ships so future Indoor / new-class profiles
            // can flip the value without re-plumbing the detector contract.
            openRadius: request.MorphOpenRadiusPx);

        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);

        // PG ships icon sprites at native resolution (~256 px) but renders map
        // icons at a single small on-screen size (~16 px). Single-scale NCC only
        // correlates at matching size, so the templates MUST be downscaled to the
        // render size before the per-blob match — otherwise every native-res
        // template is larger than the blob crop and skipped, yielding zero
        // detections (mithril#916). Returns the templates unchanged when they're
        // already small (the synthetic-fixture path).
        var templates = IconRenderScaler.RenderSized(request.Screenshot, request.Templates.Templates, request.TypeFloor, request.RenderSizePx);

        // mithril#1121: diagnostic sink fires when wired. We use the unfiltered
        // best NCC peak (minScore = -1) for the diagnostic so a 0.78-just-below-floor
        // blob is distinguishable from a 0.30-way-below-floor blob — see
        // BlobTemplateScore. The detection-decision NCC stays gated by TypeFloor.
        //
        // mithril#1123 D3.a: BlobTemplateScore.BlobOrdinal carries the same int that
        // BlobClassification.BlobOrdinal does (across all comps, not just the
        // Icon-class subset this method returns) — set on BlobFeat by
        // ConnectedComponents.Label and read here. Cross-file ordinal-space coherence
        // is what lets 10b ↔ 10c be joined by ordinal without a bbox guess.
        var sink = request.BlobScoreSink;

        foreach (var blob in blobs)
        {
            // Search region: blob bbox padded so a template centred near a blob
            // edge still fits inside the crop. Pad by the largest template dim.
            int pad = 0;
            foreach (var t in templates) pad = Math.Max(pad, Math.Max(t.Gray.Width, t.Gray.Height));
            int x0 = Math.Max(0, blob.MinX - pad), y0 = Math.Max(0, blob.MinY - pad);
            int x1 = Math.Min(w - 1, blob.MaxX + pad), y1 = Math.Min(h - 1, blob.MaxY + pad);
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            var crop = ImageOps.Crop(request.Screenshot, x0, y0, cw, ch);

            IconTemplate? bestIcon = null;
            Detection bestDet = default;
            double bestScore = double.NegativeInfinity;
            foreach (var t in templates)
            {
                if (t.Gray.Width > cw || t.Gray.Height > ch)
                {
                    EmitDiagnostic(sink, blob, blob.Ordinal, t,
                        score: double.NaN, typeFloor: request.TypeFloor,
                        aboveFloor: false, skipped: true);
                    continue;
                }
                // mithril#1121: probe the unfiltered best NCC peak so the diagnostic
                // surfaces the actual score, not just "below floor → null." The
                // detection decision still uses TypeFloor via the explicit compare
                // on line below.
                var diagHit = NccTemplateMatch.FindBest(crop, t.Gray, t.Alpha, minScore: -1.0);
                var hitScore = diagHit?.Score ?? double.NaN;
                var clearedFloor = diagHit is not null && hitScore >= request.TypeFloor;

                EmitDiagnostic(sink, blob, blob.Ordinal, t,
                    score: hitScore, typeFloor: request.TypeFloor,
                    aboveFloor: clearedFloor, skipped: false);

                if (!clearedFloor) continue;
                if (hitScore > bestScore)
                {
                    bestScore = hitScore;
                    bestDet = diagHit!.Value;
                    bestIcon = t;
                }
            }

            if (bestIcon is null) continue;

            var (cx, cy) = bestDet.Centre(bestIcon.Gray.Width, bestIcon.Gray.Height);
            double anchorX = x0 + cx + bestIcon.Gray.Width * (bestIcon.PivotX - 0.5);
            double anchorY = y0 + cy + bestIcon.Gray.Height * (0.5 - bestIcon.PivotY);

            if (!byType.TryGetValue(bestIcon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[bestIcon.LandmarkType] = list;
            }
            list.Add(new TypedDetection(bestIcon.LandmarkType, bestIcon.Name, new CroppedFramePixel(anchorX, anchorY), bestDet.Score));
        }

        // mithril#1154: overlapping template-match crops between adjacent blobs
        // can pivot-correct distinct blobs to byte-identical anchors; collapse
        // anchors within RenderSizePx (the on-screen icon size, default 16) of
        // each other to one survivor per cluster (highest-score wins). Without
        // this, the solver double-counts the duplicate as two inliers and the
        // "only N inliers (need >=4)" reject reason becomes deceptive. Per-type
        // — anchors of different landmark types are already in separate lists.
        //
        // `?? 16` fallback covers the documented null path: callers that pass
        // RenderSizePx=null opt into IconRenderScaler's aggregate-NCC sweep (a
        // less-reliable mode that can collapse to a tiny scale). The dedup
        // still needs a numeric ε; 16 mirrors the property's default and is
        // the empirically-validated PG icon render size (gate study).
        double epsilon = request.RenderSizePx ?? 16;
        var result = new Dictionary<string, IReadOnlyList<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType)
            result[kv.Key] = DetectionSpatialDedup.Dedupe(kv.Value, epsilon, _logger);
        return result;
    }

    // mithril#1121: emit the per-(blob, template) observation to the sink + Trace
    // log. Rotate180 is left default-false; the SolveEngine's two-orientation
    // wrapper rewrites it via `with { Rotate180 = ... }` before appending — the
    // detector itself has no knowledge of which orientation pass it's running in.
    private void EmitDiagnostic(
        Action<BlobTemplateScore>? sink,
        BlobFeat blob, int blobOrdinal, IconTemplate template,
        double score, double typeFloor, bool aboveFloor, bool skipped)
    {
        if (skipped)
        {
            _logger?.LogTrace(
                "Blob #{Ord} ({Mx},{My},{W},{H}) area={A}: skipped template {T} ({Tt}) — too large ({Tw}x{Th}).",
                blobOrdinal, blob.MinX, blob.MinY, blob.W, blob.H, blob.Area,
                template.Name, template.LandmarkType, template.Gray.Width, template.Gray.Height);
        }
        else
        {
            _logger?.LogTrace(
                "Blob #{Ord} ({Mx},{My},{W},{H}) area={A}: template {T} ({Tt}) score={S:0.000} floor={F:0.00} {Outcome}.",
                blobOrdinal, blob.MinX, blob.MinY, blob.W, blob.H, blob.Area,
                template.Name, template.LandmarkType, score, typeFloor,
                aboveFloor ? "above" : "below");
        }

        if (sink is null) return;
        sink(new BlobTemplateScore(
            BlobOrdinal: blobOrdinal,
            BlobMinX: blob.MinX,
            BlobMinY: blob.MinY,
            BlobWidth: blob.W,
            BlobHeight: blob.H,
            BlobArea: blob.Area,
            TemplateName: template.Name,
            TemplateLandmarkType: template.LandmarkType,
            TemplateWidth: template.Gray.Width,
            TemplateHeight: template.Gray.Height,
            Score: score,
            TypeFloor: typeFloor,
            AboveFloor: aboveFloor,
            Skipped: skipped,
            Rotate180: false));
    }
}
