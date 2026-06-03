using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Fallback detector: whole-image template NCC per icon (no deviation map). Picks
/// a single global render size across the template set (PG renders all map icons
/// at one consistent on-screen pixel size), then runs mask-aware NCC over the
/// whole cropped map and pivot-corrects each hit to a screenshot-space anchor.
///
/// <para>Ported from the gate-study <c>ScreenshotCalibrator.DetectIconsByType</c>;
/// the render-size selection lives in the shared <see cref="IconRenderScaler"/>
/// (used by both detectors). The spec keeps both paths (§8); the deviation-blob
/// detector is preferred on sparse irregular-bordered areas, this one is the
/// simpler dense-area fallback. BCL-only.</para>
/// </summary>
public sealed class WholeImageTemplateDetector : ICalibrationDetector
{
    public IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request)
    {
        var screenshot = request.Screenshot;
        double threshold = request.TypeFloor;

        // Downscale native-resolution PG sprites to the single on-screen render
        // size before NCC (shared with the deviation-blob detector via
        // IconRenderScaler; mithril#916). No-op when templates are already small.
        var templates = IconRenderScaler.RenderSized(screenshot, request.Templates.Templates, threshold, request.RenderSizePx);

        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        foreach (var icon in templates)
        {
            int rw = icon.Gray.Width, rh = icon.Gray.Height;

            // 64-cap per template by score — a quality filter against the
            // mid-score false-positive flood (gate-study finding).
            var hits = NccTemplateMatch.FindAll(screenshot, icon.Gray, icon.Alpha, threshold, maxResults: 64);
            if (hits.Count == 0) continue;

            if (!byType.TryGetValue(icon.LandmarkType, out var list))
            {
                list = new List<TypedDetection>();
                byType[icon.LandmarkType] = list;
            }
            foreach (var hit in hits)
            {
                var (cx, cy) = hit.Centre(rw, rh);
                double anchorX = cx + rw * (icon.PivotX - 0.5);
                double anchorY = cy + rh * (0.5 - icon.PivotY);
                list.Add(new TypedDetection(icon.LandmarkType, icon.Name, anchorX, anchorY, hit.Score));
            }
        }

        var result = new Dictionary<string, IReadOnlyList<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType) result[kv.Key] = kv.Value;
        return result;
    }
}
