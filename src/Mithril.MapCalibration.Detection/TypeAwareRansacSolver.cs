using System;
using System.Collections.Generic;
using System.Linq;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Type-constrained RANSAC correspondence + iterative refinement, lifted from
/// the gate-study <c>ScreenshotCalibrator</c>. Picks 2 random same-type
/// (detection, ref) pairs, solves a candidate similarity transform, counts
/// geometrically-consistent inliers (per-detection-best then per-ref-best
/// dedup), and keeps the best inlier set (tie-broken by refit residual). Then
/// LO-RANSAC iterative refinement drops the worst-residual inlier when it is
/// meaningfully worse than the median and re-solves.
///
/// <para>Works in texture-pixel space (via <see cref="MapRect.CroppedToTexture"/>)
/// so the inlier predicate is independent of the screenshot's pan/zoom. The
/// deterministic <c>Random(852)</c> seed makes runs reproducible. BCL-only.</para>
/// </summary>
public static class TypeAwareRansacSolver
{
    /// <summary>An accepted (detection, reference) correspondence in texture-pixel space.</summary>
    public sealed record AssignedReference(
        string Label,
        double WorldX,
        double WorldZ,
        double PixelX,
        double PixelY,
        double MatchScore);

    /// <summary>One top-K candidate: solved calibration + the inlier set used.</summary>
    public sealed record TopKCandidate(
        AreaCalibration Calibration,
        IReadOnlyList<AssignedReference> Inliers);

    // Inlier threshold for RANSAC: a detection is an inlier of a candidate
    // calibration if its pivot-corrected pixel is within this many texture
    // pixels of where the calibration projects a same-type ref.
    private const double RansacInlierPx = 15.0;

    // Random-sample iterations. 800 handles ~80% outliers at 95% confidence
    // for a 2-point seed; cheap because each iteration is a 2-point solver
    // invocation + a linear inlier scan over the pool.
    private const int RansacIterations = 800;

    /// <summary>
    /// Top-K variant of <see cref="Solve"/>. Returns up to <paramref name="k"/>
    /// geometrically-consistent fits ordered by inlier count desc, refit-residual
    /// asc, after the same iterative refinement step. Synthesis-J re-rank consumes
    /// this so the re-ranker has alternatives to score. With <paramref name="k"/>=1
    /// the result is equivalent to <see cref="Solve"/>.
    /// </summary>
    public static IReadOnlyList<TopKCandidate> SolveTopK(
        IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
        IReadOnlyList<LandmarkReference> allRefs,
        MapRect mapRect,
        int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), k, "k must be >= 1");

        var rawCandidates = RansacAssignAll(detectionsByType, allRefs, mapRect);
        if (rawCandidates.Count == 0) return [];

        // Order by inlier count desc, then refit residual asc.
        rawCandidates.Sort((a, b) =>
        {
            int ic = b.Inliers.Count.CompareTo(a.Inliers.Count);
            return ic != 0 ? ic : a.Residual.CompareTo(b.Residual);
        });

        var refined = new List<TopKCandidate>(Math.Min(k, rawCandidates.Count));
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawCandidates)
        {
            if (refined.Count >= k) break;
            var (cal, refinedInliers) = IterativeRefine(raw.Inliers);
            if (cal is null) continue;

            // De-dup: two raw candidates can refine into the same fit. Key by a
            // coarse round of (scale, rot, originX, originY) — same key → drop.
            var key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{cal.Scale:F3}|{cal.RotationRadians:F3}|{cal.OriginX:F1}|{cal.OriginY:F1}|{cal.MirrorNorth}");
            if (!seenKeys.Add(key)) continue;

            refined.Add(new TopKCandidate(cal, refinedInliers));
        }
        return refined;
    }

    /// <summary>
    /// Existing single-best entry point — preserved for callers that don't need
    /// alternatives. Now a thin wrapper over <see cref="SolveTopK"/>.
    /// </summary>
    public static (AreaCalibration? Calibration, IReadOnlyList<AssignedReference> Inliers) Solve(
        IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
        IReadOnlyList<LandmarkReference> allRefs,
        MapRect mapRect)
    {
        var top = SolveTopK(detectionsByType, allRefs, mapRect, k: 1);
        if (top.Count == 0) return (null, []);
        return (top[0].Calibration, top[0].Inliers);
    }

    private static List<(IReadOnlyList<AssignedReference> Inliers, double Residual)> RansacAssignAll(
        IReadOnlyDictionary<string, List<TypedDetection>> detectionsByType,
        IReadOnlyList<LandmarkReference> allRefs,
        MapRect mapRect)
    {
        // Build pool: (texture-pixel detection, candidate refs of same type).
        // Work in texture-pixel space so the inlier predicate is in a stable
        // coord system independent of the screenshot's pan/zoom.
        var pool = new List<(TypedDetection Det, double Tx, double Ty, IReadOnlyList<LandmarkReference> Candidates)>();
        foreach (var kv in detectionsByType)
        {
            var typeRefs = allRefs.Where(r => string.Equals(r.Type, kv.Key, StringComparison.Ordinal)).ToList();
            if (typeRefs.Count == 0) continue;
            foreach (var det in kv.Value)
            {
                var tex = mapRect.CroppedToTexture(det.Anchor);
                pool.Add((det, tex.X, tex.Y, typeRefs));
            }
        }
        if (pool.Count < 2) return [];

        var rng = new Random(852);  // deterministic seed for reproducible runs
        var all = new List<(IReadOnlyList<AssignedReference> Inliers, double Residual)>();

        for (int iter = 0; iter < RansacIterations; iter++)
        {
            int i1 = rng.Next(pool.Count);
            int i2 = rng.Next(pool.Count);
            if (i1 == i2) continue;
            var e1 = pool[i1];
            var e2 = pool[i2];
            if (Math.Abs(e1.Tx - e2.Tx) < 5 && Math.Abs(e1.Ty - e2.Ty) < 5) continue;

            var r1 = e1.Candidates[rng.Next(e1.Candidates.Count)];
            var r2 = e2.Candidates[rng.Next(e2.Candidates.Count)];
            if (r1.World.X == r2.World.X && r1.World.Z == r2.World.Z) continue;

            var seedLegacy = LandmarkCalibrationSolver.Solve([
                new LandmarkCalibrationSolver.Reference(r1.World.X, r1.World.Z, e1.Tx, e1.Ty),
                new LandmarkCalibrationSolver.Reference(r2.World.X, r2.World.Z, e2.Tx, e2.Ty),
            ]);
            if (seedLegacy is null) continue;
            // #1076 Phase 6.5: RANSAC operates in texture-pixel space; project
            // candidate refs back through a texture-frame view of the seed so the
            // pixel comparison stays frame-explicit. AsTexture is a field-identity
            // re-tag of the same similarity transform.
            var seed = AsTexture(seedLegacy);

            // For each pool entry, project each of its candidate refs through the
            // seed; the closest projection wins. If within RansacInlierPx of the
            // detected pixel, it's a candidate inlier.
            //
            // Two-stage de-dup: (a) per detection, keep the single best ref. (b)
            // per ref, keep the single best detection — otherwise multiple noisy
            // detections all "claim" the same real landmark, inflating the inlier
            // count and dragging the final solve's residual upward.
            var perDetCandidates = new List<(int PoolIdx, LandmarkReference Ref, double Dist)>();
            for (int pi = 0; pi < pool.Count; pi++)
            {
                var e = pool[pi];
                LandmarkReference? best = null;
                double bestDist = double.PositiveInfinity;
                foreach (var cand in e.Candidates)
                {
                    var pred = seed.ToTexture(cand.World);
                    var dx = pred.X - e.Tx;
                    var dy = pred.Y - e.Ty;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < bestDist) { bestDist = d; best = cand; }
                }
                if (best is not null && bestDist <= RansacInlierPx)
                {
                    perDetCandidates.Add((pi, best, bestDist));
                }
            }

            // Per ref, keep the detection with the smallest distance.
            var bestPerRef = new Dictionary<(double, double), (int PoolIdx, LandmarkReference Ref, double Dist)>();
            foreach (var cand in perDetCandidates)
            {
                var key = (cand.Ref.World.X, cand.Ref.World.Z);
                if (!bestPerRef.TryGetValue(key, out var existing) || cand.Dist < existing.Dist)
                {
                    bestPerRef[key] = cand;
                }
            }

            var inliers = new List<AssignedReference>(bestPerRef.Count);
            foreach (var cand in bestPerRef.Values)
            {
                var e = pool[cand.PoolIdx];
                inliers.Add(new AssignedReference(
                    Label: $"{e.Det.IconName}:{cand.Ref.Name}",
                    WorldX: cand.Ref.World.X,
                    WorldZ: cand.Ref.World.Z,
                    PixelX: e.Tx,
                    PixelY: e.Ty,
                    MatchScore: e.Det.Score));
            }
            // Reject geometrically-degenerate inlier sets: require the bounding
            // box of detected pixels to span at least 100 px in the larger dim —
            // a real area calibration must cover meaningful ground (an
            // edge-artifact cluster collapses world coords to a point otherwise).
            if (inliers.Count < 2) continue;
            double minX = inliers.Min(a => a.PixelX), maxX = inliers.Max(a => a.PixelX);
            double minY = inliers.Min(a => a.PixelY), maxY = inliers.Max(a => a.PixelY);
            if (Math.Max(maxX - minX, maxY - minY) < 100) continue;

            // Score the candidate: prefer more inliers, but tie-break by the refit
            // residual over those inliers — a "wrong" seed can collect inliers by
            // chance, but the refit over mis-paired points yields a high residual.
            var refitRefs = inliers
                .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, a.PixelX, a.PixelY))
                .ToList();
            var refit = LandmarkCalibrationSolver.Solve(refitRefs);
            if (refit is null) continue;

            all.Add((inliers, refit.ResidualPixels));
        }

        return all;
    }

    private static (AreaCalibration? Cal, List<AssignedReference> Refined) IterativeRefine(
        IReadOnlyList<AssignedReference> initial)
    {
        // Solve, identify worst inlier by per-inlier residual, drop it if its
        // residual is significantly worse than the median, re-solve. Stop when
        // dropping the worst doesn't improve overall residual or we're down to 3
        // inliers (similarity solver needs >= 2; 3 gives a real residual).
        var current = initial.ToList();
        AreaCalibration? bestCal = SolveOver(current);
        if (bestCal is null) return (null, current);

        const int MinInliers = 3;
        for (int iter = 0; iter < 10 && current.Count > MinInliers; iter++)
        {
            // #1076 Phase 6.5: project through a texture-frame view of the
            // current best fit (RANSAC operates in texture-pixel space).
            var bestCalTex = AsTexture(bestCal);
            var perInlier = current.Select(a =>
            {
                var p = bestCalTex.ToTexture(new WorldCoord(a.WorldX, 0, a.WorldZ));
                var dx = p.X - a.PixelX;
                var dy = p.Y - a.PixelY;
                return (Ref: a, Dist: Math.Sqrt(dx * dx + dy * dy));
            }).ToList();
            perInlier.Sort((x, y) => x.Dist.CompareTo(y.Dist));

            var median = perInlier[perInlier.Count / 2].Dist;
            var worst = perInlier[^1];
            // Drop only if the worst is meaningfully worse than the median —
            // otherwise we're carving into legitimate non-affine ceiling residual.
            if (worst.Dist < Math.Max(median * 2.0, 3.0))
            {
                break;
            }

            var candidate = current.Where(r => !ReferenceEquals(r, worst.Ref)).ToList();
            var candidateCal = SolveOver(candidate);
            if (candidateCal is null) break;
            if (candidateCal.ResidualPixels >= bestCal.ResidualPixels)
            {
                break;
            }
            bestCal = candidateCal;
            current = candidate;
        }
        return (bestCal, current);
    }

    private static AreaCalibration? SolveOver(IEnumerable<AssignedReference> refs)
    {
        var input = refs
            .Select(a => new LandmarkCalibrationSolver.Reference(a.WorldX, a.WorldZ, a.PixelX, a.PixelY))
            .ToList();
        return LandmarkCalibrationSolver.Solve(input);
    }

    // #1076 Phase 6.5: field-identity re-tag of an AreaCalibration to its
    // texture-frame view. RANSAC operates in texture-pixel space (see class
    // remarks), so projecting candidate refs through a frame-typed struct
    // keeps the pixel comparison explicit — no frame-erased pixel leaks.
    private static WorldToTextureCalibration AsTexture(AreaCalibration cal) =>
        new(cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians,
            cal.MirrorNorth);
}
