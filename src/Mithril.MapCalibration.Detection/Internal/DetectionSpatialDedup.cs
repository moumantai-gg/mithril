using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Collapse typed detections whose anchors are within <c>epsilon</c> pixels of
/// each other into a single survivor — the highest-score detection in each
/// cluster wins. Insertion order of survivors is preserved so downstream
/// consumers (RANSAC pool construction, telemetry) see a deterministic order.
///
/// <para>mithril#1154 — overlapping template-match crops between adjacent
/// blobs can pivot-correct distinct blobs to byte-identical (or sub-pixel
/// equivalent) anchors. The solver then double-counts the duplicate as two
/// inliers, making "only N inliers (need ≥4)" reject reasons deceptive.
/// mithril#1156 — solver-side defense-in-depth uses this same helper at a
/// conservative epsilon so inlier counts stay honest even if a future
/// detector violates the contract.</para>
///
/// <para>Algorithm: sort indices by score descending (ties broken by original
/// index ascending — deterministic); walk the sorted indices, accepting each
/// as a survivor iff no already-accepted survivor's anchor is within
/// <c>epsilon</c> px (Euclidean) of this one's anchor; output the survivors
/// in their <b>original insertion order</b>. O(N²) — fine for N ≤ ~100 in
/// practice.</para>
///
/// <para><b>Instrumentation (PR #1157 follow-up):</b> static-utility decision
/// owners must be instrumentable from day one (CLAUDE.md — mithril#1093/#1121/
/// #1123 background). The helper takes an optional <see cref="ILogger"/> and
/// emits one <see cref="LogLevel.Trace"/> entry per call with input count,
/// survivor count, drop count, and epsilon — so a future "why was this blob's
/// anchor missing from the result?" investigation has something to read.
/// Logging is opt-in (null logger = zero producer cost); message template is
/// stable (asserted by integration tests).</para>
/// </summary>
internal static class DetectionSpatialDedup
{
    /// <summary>
    /// Returns the deduped list. Empty input → empty output; single-item input
    /// → unchanged; <c>epsilon</c> ≤ 0 → no clustering (input returned as a
    /// defensive copy).
    /// </summary>
    public static IReadOnlyList<TypedDetection> Dedupe(
        IReadOnlyList<TypedDetection> detections,
        double epsilon,
        ILogger? logger = null)
    {
        int n = detections.Count;
        if (n == 0) return [];
        if (n == 1)
        {
            logger?.LogTrace(
                "Spatial-dedup: {Input} → {Survivors} typed detections (dropped {Dropped}, ε={Epsilon:F2}px).",
                n, 1, 0, epsilon);
            return [detections[0]];
        }
        if (epsilon <= 0)
        {
            // Defensive copy so callers can't mutate the source list through
            // the returned reference (and to keep the return-shape consistent
            // — an IReadOnlyList<T> over a freshly-constructed array).
            var copy = new TypedDetection[n];
            for (int i = 0; i < n; i++) copy[i] = detections[i];
            logger?.LogTrace(
                "Spatial-dedup: {Input} → {Survivors} typed detections (dropped {Dropped}, ε={Epsilon:F2}px).",
                n, n, 0, epsilon);
            return copy;
        }

        // Sort indices by score desc, tie-break by original index asc.
        // Deterministic — equal scores never reorder.
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        System.Array.Sort(order, (a, b) =>
        {
            int c = detections[b].Score.CompareTo(detections[a].Score);
            return c != 0 ? c : a.CompareTo(b);
        });

        double epsSquared = epsilon * epsilon;
        var survives = new bool[n];
        var survivorIndices = new List<int>(n);

        foreach (int idx in order)
        {
            var anchor = detections[idx].Anchor;
            bool clusteredIntoExisting = false;
            for (int k = 0; k < survivorIndices.Count; k++)
            {
                var other = detections[survivorIndices[k]].Anchor;
                double dx = anchor.X - other.X;
                double dy = anchor.Y - other.Y;
                if (dx * dx + dy * dy <= epsSquared)
                {
                    clusteredIntoExisting = true;
                    break;
                }
            }
            if (clusteredIntoExisting) continue;
            survivorIndices.Add(idx);
            survives[idx] = true;
        }

        // Emit survivors in ORIGINAL insertion order — not score order — so
        // downstream iteration is deterministic w.r.t. detector emission order.
        var result = new List<TypedDetection>(survivorIndices.Count);
        for (int i = 0; i < n; i++)
            if (survives[i]) result.Add(detections[i]);

        // One LogTrace per call. Message template is stable — integration tests
        // assert the "Spatial-dedup:" prefix and the formatted ε.
        logger?.LogTrace(
            "Spatial-dedup: {Input} → {Survivors} typed detections (dropped {Dropped}, ε={Epsilon:F2}px).",
            n, survivorIndices.Count, n - survivorIndices.Count, epsilon);
        return result;
    }
}
