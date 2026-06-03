using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Diagnostics;

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
        // Cache per orientation: L_t fields + top-K + scored winner (when mode != Off).
        SynthesisOrientationWinner? bestSynthesis = null;
        CalibrationSolveResult? bestLegacyAccepted = null;
        CalibrationSolveResult? bestLegacyRejected = null;

        var mode = _options.SynthesisRerankMode;
        int topK = mode == SynthesisRerankMode.Off ? 1 : Math.Max(1, _options.RansacTopK);

        foreach (var rotate180 in new[] { false, true })
        {
            var texture = rotate180 ? ImageOps.Rotate180(request.BaseTexture) : request.BaseTexture;
            var req = request with { BaseTexture = texture };

            var detections = _detector.Detect(req);
            LogDetectSummary(rotate180, detections, references);
            var topKList = TypeAwareRansacSolver.SolveTopK(ToMutable(detections), references, request.MapRect, topK);
            var flatDetections = FlattenDetections(detections);

            // === Legacy track: pick the lowest-residual gate-accepted top-K[0] (preserves shadow-source-of-truth) ===
            if (topKList.Count == 0)
            {
                bestLegacyRejected ??= new CalibrationSolveResult(
                    null, 0, "no geometrically-consistent fit", []) { Detections = flatDetections };
            }
            else
            {
                var legacyHead = topKList[0];
                if (_gate.Accept(legacyHead.Calibration, legacyHead.Inliers.Count, out var legacyReason))
                {
                    var accepted = new CalibrationSolveResult(
                        legacyHead.Calibration, legacyHead.Inliers.Count, null, legacyHead.Inliers)
                        { Detections = flatDetections };
                    if (bestLegacyAccepted is null
                        || legacyHead.Calibration.ResidualPixels < bestLegacyAccepted.Calibration!.ResidualPixels)
                    {
                        bestLegacyAccepted = accepted;
                    }
                }
                else if (bestLegacyRejected is null
                    || legacyHead.Calibration.ResidualPixels
                        < (bestLegacyRejected.Calibration?.ResidualPixels ?? double.PositiveInfinity))
                {
                    bestLegacyRejected = new CalibrationSolveResult(
                        null, legacyHead.Inliers.Count, legacyReason, legacyHead.Inliers)
                        { Detections = flatDetections };
                }
            }

            // === Synthesis track (skipped when mode == Off) ===
            if (mode == SynthesisRerankMode.Off) continue;

            var fields = BuildLikelihoodFieldsFromDeviation(
                req.Screenshot, req.BaseTexture, req.Templates,
                req.TypeFloor, req.RenderSizePx);
            var winner = ScoreOrientationCandidates(rotate180, topKList, fields, references, req.MapRect);
            if (winner is null) continue;
            if (bestSynthesis is null || winner.J > bestSynthesis.J)
            {
                bestSynthesis = winner;
            }
        }

        // Decide the unified result.
        var legacyResult = bestLegacyAccepted ?? bestLegacyRejected ??
            new CalibrationSolveResult(null, 0, "no detections");

        if (mode != SynthesisRerankMode.Enabled)
        {
            // Off + Shadow: legacy is source of truth. Telemetry emission (Task 16)
            // wraps this whole block in the synthesis_rerank span; bestSynthesis
            // values are still available for tagging when mode == Shadow.
            EmitSynthesisRerankTelemetry(mode, bestSynthesis, legacyResult);
            if (legacyResult.Calibration is not null)
            {
                _logger?.LogInformation(
                    "Auto-calibration accepted: residual {Residual:0.00} px, {Inliers} inliers.",
                    legacyResult.Calibration.ResidualPixels, legacyResult.InlierCount);
                LogInlierCorrespondences(legacyResult.Calibration, legacyResult.Inliers);
            }
            else
            {
                _logger?.LogInformation("Auto-calibration rejected: {Reason}.", legacyResult.RejectReason);
            }
            return legacyResult;
        }

        // Enabled: synthesis-J IS the gate.
        if (bestSynthesis is null)
        {
            _logger?.LogInformation("Auto-calibration rejected (synthesis): no synthesis-J winner.");
            var noWinner = new CalibrationSolveResult(null, 0, "no synthesis-J winner",
                legacyResult.Inliers) { Detections = legacyResult.Detections };
            EmitSynthesisRerankTelemetry(mode, bestSynthesis, noWinner);
            return noWinner;
        }
        bool synthAccept = bestSynthesis.J >= _options.SynthesisJMin
                        && bestSynthesis.RefsAboveHalf >= _options.SynthesisNMin;
        CalibrationSolveResult finalResult;
        if (synthAccept)
        {
            finalResult = new CalibrationSolveResult(
                bestSynthesis.Calibration, bestSynthesis.Inliers.Count, null, bestSynthesis.Inliers)
                { Detections = legacyResult.Detections };
            _logger?.LogInformation(
                "Auto-calibration accepted (synthesis-J): J={J:0.00}, refs>=0.5 {Refs}/{Total}.",
                bestSynthesis.J, bestSynthesis.RefsAboveHalf, bestSynthesis.RefsTotal);
        }
        else
        {
            var reason = $"synthesis-J below threshold (J={bestSynthesis.J:0.00} < {_options.SynthesisJMin:0.00} "
                       + $"OR refs>=0.5 {bestSynthesis.RefsAboveHalf} < {_options.SynthesisNMin})";
            finalResult = new CalibrationSolveResult(null, bestSynthesis.Inliers.Count, reason, bestSynthesis.Inliers)
                { Detections = legacyResult.Detections };
            _logger?.LogInformation("Auto-calibration rejected (synthesis): {Reason}.", reason);
        }
        EmitSynthesisRerankTelemetry(mode, bestSynthesis, finalResult);
        return finalResult;
    }

    private void EmitSynthesisRerankTelemetry(
        SynthesisRerankMode mode, SynthesisOrientationWinner? winner, CalibrationSolveResult finalResult)
    {
        // Off mode: no L_t was built, no telemetry to emit. (StartActivity returns
        // null when no listener is attached, so the cost when listeners ARE
        // attached but mode is Off is one bool branch.)
        if (mode == SynthesisRerankMode.Off) return;

        using var span = MapCalibrationDiagnostics.ActivitySource.StartActivity("calibration.synthesis_rerank");
        if (span is null && !HasAnyMeterListener()) return;

        // The legacy gate's verdict, derived from finalResult when mode==Shadow
        // (legacy is source-of-truth, accept iff Calibration is not null) — or, when
        // mode==Enabled, computed against the *winner's* AreaCalibration + inlier
        // count so we can still report disagreement between the gates even though
        // synthesis-J is doing the final accept.
        bool legacyAccept;
        int legacyInlierCount;
        double? legacyResidualPx;
        if (mode == SynthesisRerankMode.Shadow)
        {
            legacyAccept = finalResult.Calibration is not null;
            legacyInlierCount = finalResult.InlierCount;
            legacyResidualPx = finalResult.Calibration?.ResidualPixels;
        }
        else
        {
            // Enabled: re-run the legacy gate on the synthesis winner's fit so the
            // disagreement counter remains meaningful.
            if (winner is not null
                && _gate.Accept(winner.Calibration, winner.Inliers.Count, out _))
            {
                legacyAccept = true;
                legacyInlierCount = winner.Inliers.Count;
                legacyResidualPx = winner.Calibration.ResidualPixels;
            }
            else if (winner is not null)
            {
                legacyAccept = false;
                legacyInlierCount = winner.Inliers.Count;
                legacyResidualPx = winner.Calibration.ResidualPixels;
            }
            else
            {
                legacyAccept = false;
                legacyInlierCount = 0;
                legacyResidualPx = null;
            }
        }

        bool synthesisAccept = mode == SynthesisRerankMode.Enabled
            ? finalResult.Calibration is not null
            : winner is not null
              && winner.J >= _options.SynthesisJMin
              && winner.RefsAboveHalf >= _options.SynthesisNMin;

        var synthVerdict = synthesisAccept ? "accept" : "reject";
        var gateVerdict = legacyAccept ? "accept" : "reject";
        var disagree = synthesisAccept != legacyAccept;
        var change = disagree
            ? (synthesisAccept ? "reject_to_accept" : "accept_to_reject")
            : "none";

        if (span is not null)
        {
            span.SetTag("synth.mode", mode.ToString().ToLowerInvariant());
            if (winner is not null)
            {
                span.SetTag("synth.j_best", winner.J);
                span.SetTag("synth.refs_above_0.5", winner.RefsAboveHalf);
                span.SetTag("synth.refs_total", winner.RefsTotal);
                span.SetTag("synth.refs_off_crop", winner.RefsOffCrop);
            }
            span.SetTag("synth.j_min", _options.SynthesisJMin);
            span.SetTag("synth.n_min", _options.SynthesisNMin);
            span.SetTag("synth.verdict", synthVerdict);
            span.SetTag("gate.verdict", gateVerdict);
            span.SetTag("gate.inliers", legacyInlierCount);
            if (legacyResidualPx is not null) span.SetTag("gate.residual_px", legacyResidualPx.Value);
            span.SetTag("disagree", disagree);
            span.SetTag("disagree.would_change", change);
        }

        if (winner is not null)
        {
            var verdictTag = new KeyValuePair<string, object?>("verdict", synthVerdict);
            MapCalibrationDiagnostics.Meters.SynthesisJ.Record(winner.J, verdictTag);
            MapCalibrationDiagnostics.Meters.SynthesisRefsAboveThreshold.Record(winner.RefsAboveHalf, verdictTag);
        }
        if (disagree)
        {
            MapCalibrationDiagnostics.Meters.SynthesisDisagree.Add(1,
                new KeyValuePair<string, object?>("change", change));
        }
    }

    /// <summary>
    /// True if any consumer is currently listening to the synthesis meters. Used
    /// to short-circuit the emit body when no span listener AND no meter listener
    /// — the unconditional-producer convention (CLAUDE.md) means producers emit
    /// without `if (active)`, but this helper avoids the per-emit prep work when
    /// the activity didn't start and nobody is listening to the meters either.
    /// </summary>
    private static bool HasAnyMeterListener() =>
        MapCalibrationDiagnostics.Meters.SynthesisJ.Enabled
        || MapCalibrationDiagnostics.Meters.SynthesisRefsAboveThreshold.Enabled
        || MapCalibrationDiagnostics.Meters.SynthesisDisagree.Enabled;

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

    /// <summary>
    /// Build per-type L_t fields for one orientation by computing the additive
    /// deviation D = max(0, screenshot − baseTexture), applying the rim-mask
    /// (mithril#992), and scoring each unique landmark-type template against the
    /// masked deviation. Cached by orientation: built once per orientation, reused
    /// across all top-K candidates the re-rank scores.
    /// </summary>
    internal static IReadOnlyDictionary<string, double[,]> BuildLikelihoodFieldsFromDeviation(
        GrayImage screenshot,
        GrayImage baseTexture,
        IconTemplateSet templates,
        double typeFloor,
        int? renderSizePx)
    {
        if (screenshot.Width != baseTexture.Width || screenshot.Height != baseTexture.Height)
            throw new ArgumentException("screenshot and base texture must have matching dimensions");

        int w = screenshot.Width, h = screenshot.Height;
        var deviation = new byte[w * h];
        for (int i = 0; i < deviation.Length; i++)
        {
            int d = screenshot.Pixels[i] - baseTexture.Pixels[i];
            deviation[i] = d > 0 ? (byte)Math.Min(255, d) : (byte)0;
        }
        var devImage = new GrayImage(w, h, deviation);

        // One template per landmark-type — the per-type L_t fields are keyed by
        // LandmarkType. If a type has multiple templates (e.g. variants), the
        // LAST in iteration order wins, matching the probe's path at
        // SynthesisProbePhase.cs (`fieldsByType[template.LandmarkType] = ...`
        // inside a foreach). Production must match this so Task 17's L_t equality
        // test holds in any future multi-template-per-type scenario.
        var perType = new Dictionary<string, IconTemplate>(StringComparer.Ordinal);
        foreach (var template in templates.Templates)
        {
            perType[template.LandmarkType] = template;
        }

        var fields = new Dictionary<string, double[,]>(perType.Count, StringComparer.Ordinal);
        foreach (var (type, template) in perType)
        {
            fields[type] = IconLikelihoodField.LoadDeviationAsField(
                devImage, template,
                applyRimMask: true,
                devThr: IconLikelihoodField.DefaultDevThr);
        }
        return fields;
    }

    /// <summary>
    /// For one orientation, score each of the RANSAC top-K candidates with
    /// <see cref="JEvaluator"/>, LM-refine the highest-J candidate with
    /// <see cref="LocalRefine"/>, and return the orientation winner. The winner's
    /// <c>Calibration</c> reflects the LM-refined fit; <c>Inliers</c> are the
    /// raw RANSAC inlier set of the pre-refine candidate (the LM step adjusts
    /// the geometry but the inlier set was the seed of that geometry).
    /// </summary>
    private SynthesisOrientationWinner? ScoreOrientationCandidates(
        bool rotate180,
        IReadOnlyList<TypeAwareRansacSolver.TopKCandidate> candidates,
        IReadOnlyDictionary<string, double[,]> fields,
        IReadOnlyList<LandmarkReference> references,
        MapRect alignedRect)
    {
        if (candidates.Count == 0) return null;

        SynthesisOrientationWinner? best = null;
        foreach (var cand in candidates)
        {
            var t = CandidateTransform.FromCalibration(cand.Calibration, alignedRect);
            var j = JEvaluator.Evaluate(t, fields, references);
            if (best is null || j.J > best.J)
            {
                best = new SynthesisOrientationWinner(
                    Rotate180: rotate180,
                    Calibration: cand.Calibration,
                    Inliers: cand.Inliers,
                    J: j.J,
                    RefsAboveHalf: j.RefsAboveHalf,
                    RefsOffCrop: j.RefsOffCrop,
                    RefsTotal: references.Count);
            }
        }

        if (best is null) return null;

        // LM-refine the highest-J candidate's transform. The refined transform
        // re-scores against the same L_t fields → we update J / RefsAboveHalf /
        // RefsOffCrop to reflect the refined geometry. We do NOT mutate
        // best.Calibration, because that's the texture-pixel-space AreaCalibration
        // the engine still persists; LM works in aligned-pair-pixel space and
        // wouldn't round-trip cleanly through the rect re-scale.
        var seed = CandidateTransform.FromCalibration(best.Calibration, alignedRect);
        var refined = LocalRefine.Run(seed, fields, references, maxIter: 24, stepInit: 1.0);
        var refinedJ = JEvaluator.Evaluate(refined, fields, references);
        return best with
        {
            J = refinedJ.J,
            RefsAboveHalf = refinedJ.RefsAboveHalf,
            RefsOffCrop = refinedJ.RefsOffCrop,
        };
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

/// <summary>
/// Per-orientation synthesis-J winner, used by
/// <see cref="MapCalibrationSolveEngine"/>'s cross-orientation selector.
/// Internal — the public consumer sees the unified <see cref="CalibrationSolveResult"/>.
/// </summary>
internal sealed record SynthesisOrientationWinner(
    bool Rotate180,
    AreaCalibration Calibration,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference> Inliers,
    double J,
    int RefsAboveHalf,
    int RefsOffCrop,
    int RefsTotal);
