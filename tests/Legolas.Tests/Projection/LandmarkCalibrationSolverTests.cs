using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Projection;

public class LandmarkCalibrationSolverTests
{
    // Project the way CoordinateProjector does, given a ground-truth transform
    // and a world point treated as (East=X, North=Z).
    private static OverlayPixel Project(double scale, double rot, OverlayPixel origin, double x, double z)
    {
        var cos = Math.Cos(rot);
        var sin = Math.Sin(rot);
        var rotE = x * cos + z * sin;
        var rotN = -x * sin + z * cos;
        return new OverlayPixel(origin.X + scale * rotE, origin.Y - scale * rotN);
    }

    [Fact]
    public void Recovers_known_scale_rotation_and_origin_from_exact_references()
    {
        const double scale = 1.7;
        var rot = Math.PI / 5;
        var origin = new OverlayPixel(640, 360);

        // Signed world coords (negative X/Z are real — Myconian/SunVale/etc.).
        var world = new[]
        {
            (X: -677.0, Z: 803.0),
            (X: 1138.0, Z: 1367.0),
            (X: -1459.0, Z: -860.0),
            (X: 254.0, Z: -55.0),
        };

        var refs = world
            .Select(p =>
            {
                var px = Project(scale, rot, origin, p.X, p.Z);
                return new LandmarkCalibrationSolver.Reference(p.X, p.Z, px.X, px.Y);
            })
            .ToList();

        var cal = LandmarkCalibrationSolver.Solve(refs);

        cal.Should().NotBeNull();
        cal!.Scale.Should().BeApproximately(scale, 1e-6);
        cal.RotationRadians.Should().BeApproximately(rot, 1e-6);
        cal.OriginX.Should().BeApproximately(origin.X, 1e-4);
        cal.OriginY.Should().BeApproximately(origin.Y, 1e-4);
        cal.ResidualPixels.Should().BeApproximately(0, 1e-6);
        cal.ReferenceCount.Should().Be(4);
    }

    [Fact]
    public void Picks_the_handedness_that_fits_a_reflected_world_layout()
    {
        // Construct pixels from a model where North = -Z (the mirrored
        // handedness). The orientation-preserving fit cannot represent a
        // reflection, so the solver must select the mirrored candidate and
        // still drive residual to ~0.
        const double scale = 2.0;
        var rot = 0.4;
        var origin = new OverlayPixel(100, 200);

        var world = new[]
        {
            (X: 10.0, Z: 0.0),
            (X: 0.0, Z: 12.0),
            (X: -7.0, Z: 5.0),
            (X: 4.0, Z: -9.0),
        };

        OverlayPixel Mirrored(double x, double z)
        {
            var cos = Math.Cos(rot);
            var sin = Math.Sin(rot);
            var east = x;
            var north = -z; // reflection
            var rotE = east * cos + north * sin;
            var rotN = -east * sin + north * cos;
            return new OverlayPixel(origin.X + scale * rotE, origin.Y - scale * rotN);
        }

        var refs = world
            .Select(p =>
            {
                var px = Mirrored(p.X, p.Z);
                return new LandmarkCalibrationSolver.Reference(p.X, p.Z, px.X, px.Y);
            })
            .ToList();

        var cal = LandmarkCalibrationSolver.Solve(refs);

        cal.Should().NotBeNull();
        cal!.ResidualPixels.Should().BeApproximately(0, 1e-6);
        cal.Scale.Should().BeApproximately(scale, 1e-6);
    }

    [Fact]
    public void Returns_null_with_fewer_than_two_references()
    {
        LandmarkCalibrationSolver.Solve(new[]
        {
            new LandmarkCalibrationSolver.Reference(1, 2, 3, 4),
        }).Should().BeNull();

        LandmarkCalibrationSolver.Solve(Array.Empty<LandmarkCalibrationSolver.Reference>())
            .Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_all_world_points_are_coincident()
    {
        // Degenerate: identical world coords carry no scale/rotation info
        // (same threshold as CoordinateProjector.Refit).
        var refs = new[]
        {
            new LandmarkCalibrationSolver.Reference(50, 50, 10, 10),
            new LandmarkCalibrationSolver.Reference(50, 50, 99, 99),
        };

        LandmarkCalibrationSolver.Solve(refs).Should().BeNull();
    }

    [Fact]
    public void Residual_reflects_real_pixel_error_when_a_reference_is_misplaced()
    {
        const double scale = 1.0;
        const double rot = 0.0;
        var origin = new OverlayPixel(0, 0);
        var world = new[] { (1000.0, 0.0), (0.0, 1000.0), (1000.0, 1000.0) };

        var refs = world
            .Select(p =>
            {
                var px = Project(scale, rot, origin, p.Item1, p.Item2);
                return new LandmarkCalibrationSolver.Reference(p.Item1, p.Item2, px.X, px.Y);
            })
            .ToList();
        // Nudge one reference's clicked pixel well off true.
        refs[2] = refs[2] with { PixelX = refs[2].PixelX + 60, PixelY = refs[2].PixelY - 80 };

        var cal = LandmarkCalibrationSolver.Solve(refs);

        cal.Should().NotBeNull();
        cal!.ResidualPixels.Should().BeGreaterThan(5); // a real, surfaced error
    }
}
