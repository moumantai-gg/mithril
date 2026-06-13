using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class TypeAwareRansacSolverTests
{
    // #1076 Phase 6.5: ground-truth transform: world (X,Z) -> texture pixels.
    private static readonly WorldToTextureCalibration Truth = new(
        OriginX: 400.0, OriginY: 300.0, Scale: 1.2, RotationRadians: 0.35,
        MirrorNorth: false);

    // Texture 800x600 rendered at native scale (factor 1.0) into a screenshot
    // padded by (50, 80) — so texture-pixel == screenshot-pixel - origin.
    private static readonly MapRect Rect = new(
        OriginX: 50, OriginY: 80, Width: 800, Height: 600, TextureWidth: 800, TextureHeight: 600);

    private static readonly (string Type, string Icon, double X, double Z)[] Landmarks =
    [
        ("Portal", "landmark_portal", -50.0, 80.0),
        ("Portal", "landmark_portal", 75.0, -40.0),
        ("TeleportationPlatform", "landmark_telepad", 100.0, 20.0),
        ("MeditationPillar", "landmark_medipillar", 0.0, -10.0),
        ("Npc", "landmark_npc", 40.0, 60.0),
        ("Npc", "landmark_npc", -30.0, -55.0),
    ];

    private static List<LandmarkReference> BuildRefs() =>
        Landmarks.Select(l => new LandmarkReference(l.Type, l.Icon, new WorldCoord(l.X, 0, l.Z))).ToList();

    // Project each landmark to texture pixels via Truth, then to screenshot
    // pixels via the rect origin, and emit a TypedDetection grouped by type.
    private static Dictionary<string, List<TypedDetection>> BuildDetections(bool collapseTypes = false)
    {
        var byType = new Dictionary<string, List<TypedDetection>>(StringComparer.Ordinal);
        foreach (var l in Landmarks)
        {
            var tex = Truth.ToTexture(new WorldCoord(l.X, 0, l.Z));
            double sx = tex.X + Rect.OriginX;
            double sy = tex.Y + Rect.OriginY;
            var key = collapseTypes ? "All" : l.Type;
            var det = new TypedDetection(key, l.Icon, new CroppedFramePixel(sx, sy), Score: 0.9);
            if (!byType.TryGetValue(key, out var list)) { list = new(); byType[key] = list; }
            list.Add(det);
        }
        return byType;
    }

    [Fact]
    public void Recovers_truth_from_typed_detections()
    {
        var (cal, inliers) = TypeAwareRansacSolver.Solve(BuildDetections(), BuildRefs(), Rect);

        cal.Should().NotBeNull();
        Math.Abs(cal!.Scale - 1.2).Should().BeLessThan(0.05);
        Math.Abs(NormaliseAngle(cal.RotationRadians - 0.35)).Should().BeLessThan(0.02);
        cal.ResidualPixels.Should().BeLessThan(12.0);
        inliers.Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Type_constraint_keeps_pairs_honest()
    {
        // With correct type labels, an npc detection can only pair to npc refs:
        // truth recovers cleanly.
        var (typed, _) = TypeAwareRansacSolver.Solve(BuildDetections(collapseTypes: false), BuildRefs(), Rect);
        typed.Should().NotBeNull();
        typed!.ResidualPixels.Should().BeLessThan(12.0);

        // Collapsing every detection under ONE type lets the solver pair any
        // detection with any ref — recovery degrades (higher residual) or fails.
        var (collapsed, _) = TypeAwareRansacSolver.Solve(BuildDetections(collapseTypes: true), BuildRefs(), Rect);
        bool degraded = collapsed is null || collapsed.ResidualPixels > typed.ResidualPixels + 1.0;
        degraded.Should().BeTrue("collapsing types removes the per-type pairing constraint the solver relies on");
    }

    [Fact]
    public void Deterministic_across_runs()
    {
        var (a, _) = TypeAwareRansacSolver.Solve(BuildDetections(), BuildRefs(), Rect);
        var (b, _) = TypeAwareRansacSolver.Solve(BuildDetections(), BuildRefs(), Rect);
        a!.Scale.Should().Be(b!.Scale);
        a.OriginX.Should().Be(b.OriginX);
        a.RotationRadians.Should().Be(b.RotationRadians);
    }

    [Fact]
    public void SolveTopK_returns_candidates_ordered_by_inliers_then_residual()
    {
        var detections = BuildDetections();
        var refs = BuildRefs();

        var topK = TypeAwareRansacSolver.SolveTopK(detections, refs, Rect, k: 4);

        topK.Should().NotBeEmpty();
        topK.Count.Should().BeLessOrEqualTo(4);
        topK[0].Calibration.Should().NotBeNull();

        // Non-increasing inlier count, ties broken by non-decreasing residual.
        for (int i = 1; i < topK.Count; i++)
        {
            var prev = topK[i - 1];
            var cur = topK[i];
            var prevBetter =
                prev.Inliers.Count > cur.Inliers.Count
                || (prev.Inliers.Count == cur.Inliers.Count
                    && prev.Calibration!.ResidualPixels <= cur.Calibration!.ResidualPixels);
            prevBetter.Should().BeTrue(
                $"candidate {i - 1} ({prev.Inliers.Count} inliers, "
                + $"{prev.Calibration!.ResidualPixels:0.00} px) must rank >= candidate {i} "
                + $"({cur.Inliers.Count} inliers, {cur.Calibration!.ResidualPixels:0.00} px)");
        }
    }

    [Fact]
    public void SolveTopK_with_k1_is_equivalent_to_Solve()
    {
        var detections = BuildDetections();
        var refs = BuildRefs();

        var (legacyCal, legacyInliers) = TypeAwareRansacSolver.Solve(detections, refs, Rect);
        var topK = TypeAwareRansacSolver.SolveTopK(detections, refs, Rect, k: 1);

        if (legacyCal is null)
        {
            topK.Should().BeEmpty();
            return;
        }
        topK.Should().HaveCount(1);
        topK[0].Calibration!.Should().BeEquivalentTo(legacyCal);
        topK[0].Inliers.Should().HaveCount(legacyInliers.Count);
    }

    // mithril#1156: defense-in-depth. The detector should not emit byte-identical
    // anchors (mithril#1154 fixes that at source), but the solver still dedupes
    // its input at a conservative ε=1 px. The dedup SEMANTICS are covered by
    // DetectionSpatialDedupTests; here we only prove the WIRING — the solver
    // actually invokes the helper before pool construction. We assert that via
    // a LogTrace mirror the helper emits ("Spatial-dedup: …") with ε=1.0.
    //
    // Why not assert an inlier-count delta? Because RansacAssignAll has a
    // downstream `bestPerRef` dictionary keyed on (Ref.World.X, Ref.World.Z)
    // that ALREADY de-duplicates inliers per ref — so an inlier-count assertion
    // against a hostile duplicate is tautological: it would pass without the
    // solver-side dedup too. The wiring-via-LogTrace test is the load-bearing
    // proof that the helper is actually called.
    [Fact]
    public void Solver_calls_spatial_dedup_with_one_pixel_epsilon()
    {
        var logger = new CapturingLogger();
        var detections = BuildDetections();
        var refs = BuildRefs();

        TypeAwareRansacSolver.Solve(detections, refs, Rect, logger: logger);

        var dedupLines = logger.Entries.Where(e => e.StartsWith("Spatial-dedup:", StringComparison.Ordinal)).ToList();
        dedupLines.Should().NotBeEmpty(
            "TypeAwareRansacSolver must invoke DetectionSpatialDedup.Dedupe before pool construction "
            + "(mithril#1156 defense-in-depth) — the helper emits one LogTrace per call");
        dedupLines.Should().AllSatisfy(line => line.Should().Contain("ε=1.00px",
            "solver dedup epsilon is the conservative 1.0 px constant"));
    }

    /// <summary>
    /// Minimal in-test logger that captures formatted log messages so a test can
    /// assert on the helper's LogTrace ("Spatial-dedup: …"). xunit-friendly,
    /// allocation-light, intentionally inline (matches the repo's "fake-in-test"
    /// style — no shared utility file).
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<string> Entries = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;
        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }

    private static double NormaliseAngle(double radians)
    {
        var twoPi = 2 * Math.PI;
        var r = radians % twoPi;
        if (r > Math.PI) r -= twoPi;
        if (r < -Math.PI) r += twoPi;
        return r;
    }
}
