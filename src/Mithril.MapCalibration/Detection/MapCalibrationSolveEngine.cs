using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Headless detect → solve → gate engine. Ties an <see cref="ICalibrationDetector"/>
/// to the <see cref="TypeAwareRansacSolver"/>, enumerating the discrete {0, π}
/// map orientation (run with the base texture aligned at 0° and at 180°, keep the
/// better gated solve — spec §3/§4 step 6), and applies the
/// <see cref="ICalibrationConfidenceGate"/>. <b>No capture, no I/O</b> — fully
/// unit-testable. BCL-only (logging-abstractions optional).
/// </summary>
public sealed class MapCalibrationSolveEngine
{
    private readonly ICalibrationDetector _detector;
    private readonly ICalibrationConfidenceGate _gate;
    private readonly ILogger? _logger;
    private readonly MapCalibrationSolverOptions _options;

    public MapCalibrationSolveEngine(
        ICalibrationDetector detector,
        ICalibrationConfidenceGate gate,
        ILogger? logger = null,
        MapCalibrationSolverOptions? options = null)
    {
        _detector = detector;
        _gate = gate;
        _logger = logger;
        _options = options ?? new MapCalibrationSolverOptions();
    }

    /// <summary>
    /// Solve a calibration from a detection request + the area's landmark/NPC
    /// references. Tries both orientations; returns the gate-accepted result, or
    /// (null calibration + reject reason) when neither clears the gate.
    /// </summary>
    public CalibrationSolveResult Solve(DetectionRequest request, IReadOnlyList<LandmarkReference> references)
    {
        CalibrationSolveResult? bestAccepted = null;
        CalibrationSolveResult? bestRejected = null;

        foreach (var rotate180 in new[] { false, true })
        {
            var texture = rotate180 ? ImageOps.Rotate180(request.BaseTexture) : request.BaseTexture;
            var req = request with { BaseTexture = texture };

            var detections = _detector.Detect(req);
            LogDetectSummary(rotate180, detections, references);
            var (cal, inliers) = TypeAwareRansacSolver.Solve(ToMutable(detections), references, request.MapRect);
            var flatDetections = FlattenDetections(detections);

            if (cal is null)
            {
                bestRejected ??= new CalibrationSolveResult(null, inliers.Count, "no geometrically-consistent fit", inliers) { Detections = flatDetections };
                continue;
            }

            if (_gate.Accept(cal, inliers.Count, out var reason))
            {
                var accepted = new CalibrationSolveResult(cal, inliers.Count, null, inliers) { Detections = flatDetections };
                // Prefer the lower-residual accepted orientation.
                if (bestAccepted is null || cal.ResidualPixels < bestAccepted.Calibration!.ResidualPixels)
                {
                    bestAccepted = accepted;
                }
            }
            else
            {
                // Track the closest rejection for a useful reason if nothing passes.
                if (bestRejected is null || cal.ResidualPixels < (bestRejected.Calibration?.ResidualPixels ?? double.PositiveInfinity))
                {
                    bestRejected = new CalibrationSolveResult(null, inliers.Count, reason, inliers) { Detections = flatDetections };
                }
            }
        }

        if (bestAccepted is not null)
        {
            _logger?.LogInformation(
                "Auto-calibration accepted: residual {Residual:0.00} px, {Inliers} inliers.",
                bestAccepted.Calibration!.ResidualPixels, bestAccepted.InlierCount);
            LogInlierCorrespondences(bestAccepted.Calibration!, bestAccepted.Inliers);
            return bestAccepted;
        }

        var rejected = bestRejected ?? new CalibrationSolveResult(null, 0, "no detections");
        _logger?.LogInformation("Auto-calibration rejected: {Reason}.", rejected.RejectReason);
        return rejected;
    }

    /// <summary>
    /// Per-orientation detect summary: typed detection total + per-type breakdown
    /// and the reference per-type breakdown, plus a targeted Warning when the two
    /// type-key sets are disjoint (the mithril#974 failure mode: a detection-side
    /// IconTemplate.LandmarkType vocabulary that doesn't overlap the reference-side
    /// LandmarkReference.Type vocabulary → 0 correspondences possible). Cheap: at
    /// most one Information line + (rarely) one Warning per orientation, ≤ 2 each
    /// per solve attempt.
    /// </summary>
    private void LogDetectSummary(
        bool rotate180,
        IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> detections,
        IReadOnlyList<LandmarkReference> references)
    {
        if (_logger is null) return;

        var detTotal = detections.Sum(kv => kv.Value.Count);
        var detBreakdown = string.Join(" ", detections
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value.Count}"));
        var refBreakdown = string.Join(" ", references
            .GroupBy(r => r.Type, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}"));

        _logger.LogInformation(
            "Detect (rotate180={Rotate180}): {DetTotal} typed detections [{DetBreakdown}]; references [{RefBreakdown}].",
            rotate180, detTotal, detBreakdown, refBreakdown);

        var detKeys = new HashSet<string>(detections.Keys, StringComparer.Ordinal);
        var refKeys = new HashSet<string>(references.Select(r => r.Type), StringComparer.Ordinal);
        if (detKeys.Count > 0 && refKeys.Count > 0 && !detKeys.Overlaps(refKeys))
        {
            _logger.LogWarning(
                "Detection type-keys [{DetKeys}] and reference type-keys [{RefKeys}] are disjoint — "
                + "0 correspondences possible; likely an icon-template ↔ reference type-vocabulary mismatch.",
                string.Join(",", detKeys.OrderBy(k => k, StringComparer.Ordinal)),
                string.Join(",", refKeys.OrderBy(k => k, StringComparer.Ordinal)));
        }
    }

    /// <summary>
    /// Log the accepted solve's inlier correspondences — which detection paired with
    /// which reference, and the per-inlier residual (how far the solved calibration
    /// projects the ref's world coord from the detected texture pixel). Also logs the
    /// inlier pixel span: a small span means the fit is anchored by a clustered set
    /// and extrapolates poorly across the map (a "bad solve" signature even when the
    /// local residual looks acceptable). One Information line per accepted solve.
    /// </summary>
    private void LogInlierCorrespondences(
        AreaCalibration calibration,
        IReadOnlyList<TypeAwareRansacSolver.AssignedReference>? inliers)
    {
        if (_logger is null || inliers is null || inliers.Count == 0) return;

        var parts = new List<string>(inliers.Count);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var a in inliers)
        {
            var p = calibration.WorldToWindow(new WorldCoord(a.WorldX, 0, a.WorldZ));
            var dx = p.X - a.PixelX;
            var dy = p.Y - a.PixelY;
            var residual = Math.Sqrt(dx * dx + dy * dy);
            parts.Add($"{a.Label}@({a.PixelX:0},{a.PixelY:0})r={residual:0.0}");
            if (a.PixelX < minX) minX = a.PixelX;
            if (a.PixelX > maxX) maxX = a.PixelX;
            if (a.PixelY < minY) minY = a.PixelY;
            if (a.PixelY > maxY) maxY = a.PixelY;
        }

        _logger.LogInformation(
            "Inlier correspondences ({Count}), texture-px span {SpanW:0}x{SpanH:0}: {Correspondences}.",
            inliers.Count, maxX - minX, maxY - minY, string.Join("  ", parts));
    }

    private static IReadOnlyDictionary<string, List<TypedDetection>> ToMutable(
        IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> byType)
    {
        var result = new Dictionary<string, List<TypedDetection>>(byType.Count, StringComparer.Ordinal);
        foreach (var kv in byType) result[kv.Key] = new List<TypedDetection>(kv.Value);
        return result;
    }

    private static IReadOnlyList<TypedDetection> FlattenDetections(
        IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> byType)
    {
        var flat = new List<TypedDetection>();
        foreach (var kv in byType) flat.AddRange(kv.Value);
        return flat;
    }
}

/// <summary>
/// Outcome of a headless solve: the gated calibration (or null), the inlier count,
/// a reject reason when null, and the inlier correspondences that produced the fit
/// (empty when none). The correspondence list lets a caller log <i>which</i> refs
/// matched and at what per-inlier residual — the diagnostic that turns a bare
/// "4 inliers, 7.61 px" into a self-explaining solve.
/// </summary>
public sealed record CalibrationSolveResult(
    AreaCalibration? Calibration,
    int InlierCount,
    string? RejectReason,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference>? Inliers = null)
{
    public IReadOnlyList<TypedDetection>? Detections { get; init; }
}
