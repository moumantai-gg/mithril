using System;
using System.Collections.Generic;

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
/// </summary>
public sealed class DeviationBlobCalibrationDetector : ICalibrationDetector
{
    public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request)
    {
        int w = request.Screenshot.Width;
        int h = request.Screenshot.Height;

        var shotF = LocalNccDeviation.ToGrayFloat(request.Screenshot);
        var texF = LocalNccDeviation.ToGrayFloat(request.BaseTexture);

        // Window 11 mirrors the gate-study probe default.
        var dev = LocalNccDeviation.DeviationMap(shotF, texF, w, h, win: 11, out _, addedOnly: true);

        // The deviation-only overload can't run ColourFlood (needs the BGRA shot);
        // fall back to DeviationFlood if asked for ColourFlood here.
        var rim = request.RimMask == RimMaskMode.ColourFlood ? RimMaskMode.DeviationFlood : request.RimMask;
        var blobs = DeviationBlobDetector.DetectIconBlobs(dev, w, h, request.LowNcc, rim, request.BlobOptions, closeRadius: 1);

        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);

        // PG ships icon sprites at native resolution (~256 px) but renders map
        // icons at a single small on-screen size (~16 px). Single-scale NCC only
        // correlates at matching size, so the templates MUST be downscaled to the
        // render size before the per-blob match — otherwise every native-res
        // template is larger than the blob crop and skipped, yielding zero
        // detections (mithril#916). Returns the templates unchanged when they're
        // already small (the synthetic-fixture path).
        var templates = IconRenderScaler.RenderSized(request.Screenshot, request.Templates.Templates, request.TypeFloor, request.RenderSizePx);

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
                if (t.Gray.Width > cw || t.Gray.Height > ch) continue;
                var hit = NccTemplateMatch.FindBest(crop, t.Gray, t.Alpha, request.TypeFloor);
                if (hit is null) continue;
                if (hit.Value.Score > bestScore)
                {
                    bestScore = hit.Value.Score;
                    bestDet = hit.Value;
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
            list.Add(new TypedDetection(bestIcon.LandmarkType, bestIcon.Name, anchorX, anchorY, bestDet.Score));
        }

        var result = new Dictionary<string, IReadOnlyList<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType) result[kv.Key] = kv.Value;
        return result;
    }
}
