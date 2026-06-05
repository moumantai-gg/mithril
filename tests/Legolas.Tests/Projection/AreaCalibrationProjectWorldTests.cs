using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Xunit;

namespace Legolas.Tests.Projection;

/// <summary>
/// #454: <see cref="AreaCalibration.WorldToWindow"/> must reproduce exactly the
/// transform <see cref="LandmarkCalibrationSolver"/> fits (it's the canonical
/// absolute world→pixel used by both ProcessMapFx placement and the landmark
/// "ghost" preview). Round-trips synthetic exact-fit references and pins the
/// <see cref="AreaCalibration.MirrorNorth"/> handedness.
/// </summary>
public class AreaCalibrationProjectWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectWorld_reproduces_the_solved_reference_pixels(bool mirrorNorth)
    {
        // A known similarity: pixel = origin + s·R(θ)·(east, north),
        // north = mirrorNorth ? -Z : Z — exactly the solver/Project model.
        const double s = 0.42;
        const double thetaDeg = 23.0;
        var theta = thetaDeg * Math.PI / 180.0;
        const double ox = 360.0, oy = 540.0;

        var world = new[]
        {
            new WorldCoord(-521.96, 0, 368.39),
            new WorldCoord(367.28, 0, 2798.03),
            new WorldCoord(1145.39, 0, 1323.40),
            new WorldCoord(1666.00, 0, -322.68),
        };

        OverlayPixel Forward(WorldCoord w)
        {
            var east = w.X;
            var north = mirrorNorth ? -w.Z : w.Z;
            var rotE = east * Math.Cos(theta) + north * Math.Sin(theta);
            var rotN = -east * Math.Sin(theta) + north * Math.Cos(theta);
            return new OverlayPixel(ox + s * rotE, oy - s * rotN);
        }

        var refs = world
            .Select(w =>
            {
                var px = Forward(w);
                return new LandmarkCalibrationSolver.Reference(w.X, w.Z, px.X, px.Y);
            })
            .ToList();

        var cal = LandmarkCalibrationSolver.Solve(refs);
        cal.Should().NotBeNull();
        cal!.ResidualPixels.Should().BeLessThan(1e-6, "the references are an exact similarity");
        cal.MirrorNorth.Should().Be(mirrorNorth, "the lower-residual handedness must win");

        foreach (var w in world)
        {
            var projected = cal.WorldToWindow(w);
            var expected = Forward(w);
            projected.X.Should().BeApproximately(expected.X, 1e-6);
            projected.Y.Should().BeApproximately(expected.Y, 1e-6);
        }
    }

    [Fact]
    public void ProjectWorld_honours_MirrorNorth_flag()
    {
        // Same scale/rotation/origin, opposite handedness → Z sign flips the
        // north component, so the two projections differ for any Z != 0.
        var plus = new AreaCalibration(1.0, 0.0, 0, 0, 2, 0) { MirrorNorth = false };
        var minus = new AreaCalibration(1.0, 0.0, 0, 0, 2, 0) { MirrorNorth = true };
        var w = new WorldCoord(10, 0, 7);

        // #1076 5a: AreaCalibration.WorldToWindow still returns PixelPoint
        // (the Mithril.MapCalibration core type), unchanged in 5a. Phase 6
        // adds frame-typed overloads to the core; for now compare by raw X/Y.
        var plusRes = plus.WorldToWindow(w);
        var minusRes = minus.WorldToWindow(w);
        (plusRes.X, plusRes.Y).Should().Be((10.0, -7.0));
        (minusRes.X, minusRes.Y).Should().Be((10.0, 7.0));
    }

    // ---- #524: zoom-aware overload --------------------------------------

    [Theory]
    [InlineData(0.13, 1.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.13, 2.0)]
    [InlineData(0.5, 2.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 2.0)]
    public void ProjectWorld_AppliesZoomFactor_AcrossRange(double currentZoom, double calibrationZoom)
    {
        // Scale 2 px/unit, no rotation, origin (100, 200). The effective scale
        // at currentZoom is 2 * (currentZoom / calibrationZoom).
        var cal = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = calibrationZoom };
        var w = new WorldCoord(10, 0, 5);
        var factor = currentZoom / calibrationZoom;

        var projected = cal.WorldToWindow(w, currentZoom);

        // East = X * eff, North = Z * eff. Mirror-north default false → Y = Oy - Scale*eff*Z.
        projected.X.Should().BeApproximately(100 + 2.0 * factor * 10, 1e-9);
        projected.Y.Should().BeApproximately(200 - 2.0 * factor * 5, 1e-9);
    }

    [Fact]
    public void ProjectWorld_NoOpWhenZoomMatchesCalibration()
    {
        // Back-compat: a current zoom equal to the calibration's stamp is the
        // byte-identical no-op the parameterless overload always produced.
        var cal = new AreaCalibration(1.7, 0.4, 50, -30, 3, 0) { CalibrationZoom = 1.5 };
        var w = new WorldCoord(12.5, 0, -7.25);

        var legacy = cal.WorldToWindow(w);
        var zoomAware = cal.WorldToWindow(w, 1.5);

        zoomAware.X.Should().Be(legacy.X);
        zoomAware.Y.Should().Be(legacy.Y);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-0.13)]
    public void ProjectWorld_GuardsInvalidZoom(double badZoom)
    {
        // Defensive: a non-positive zoom falls back to factor 1.0 — same pixel
        // as ProjectWorld(world) using Scale verbatim. Keeps a malformed value
        // from crashing the per-frame projector.
        var cal = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 0.5 };
        var w = new WorldCoord(10, 0, 5);

        var guarded = cal.WorldToWindow(w, badZoom);
        // factor = 1.0 ⇒ effScale = Scale verbatim.
        guarded.X.Should().BeApproximately(100 + 2.0 * 10, 1e-9);
        guarded.Y.Should().BeApproximately(200 - 2.0 * 5, 1e-9);
    }

    [Fact]
    public void ProjectWorld_LegacyOverload_DelegatesToZoomAware_WithCalibrationZoom()
    {
        // The parameterless overload must equal the zoom-aware overload at
        // currentZoom = CalibrationZoom. This is the back-compat seam — every
        // pre-#524 call site retains its pixels.
        var cal = new AreaCalibration(0.85, -0.2, 320, 480, 4, 0) { CalibrationZoom = 1.75, MirrorNorth = true };
        var w = new WorldCoord(123, 0, -456);

        cal.WorldToWindow(w).Should().Be(cal.WorldToWindow(w, cal.CalibrationZoom));
    }
}
