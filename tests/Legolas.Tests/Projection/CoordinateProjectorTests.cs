using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;

namespace Legolas.Tests.Projection;

public class CoordinateProjectorTests
{
    [Fact]
    public void Zero_rotation_projects_east_to_positive_x_and_north_to_negative_y()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new OverlayPixel(100, 100));
        projector.CalibrateFromClick(
            playerPixel: new OverlayPixel(100, 100),
            click: new OverlayPixel(110, 100),
            offset: new MetreOffset(East: 5, North: 0));

        // scale should be 10/5 = 2 px/m, rotation ~ 0
        projector.Scale.Should().BeApproximately(2.0, 1e-6);
        projector.RotationRadians.Should().BeApproximately(0.0, 1e-6);

        var northProj = projector.Project(new MetreOffset(0, 5));
        northProj.X.Should().BeApproximately(100, 1e-6);
        northProj.Y.Should().BeApproximately(100 - 10, 1e-6); // north projects up (smaller y)
    }

    [Fact]
    public void ApplyCalibration_adopts_scale_and_rotation_but_keeps_the_player_anchor_origin()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new OverlayPixel(640, 360)); // the player's anchor click

        projector.ApplyCalibration(new AreaCalibration(
            Scale: 2.5, RotationRadians: 0.7,
            OriginX: 99999, OriginY: -99999, // world-(0,0) pixel — must be ignored
            ReferenceCount: 3, ResidualPixels: 0.5));

        projector.Scale.Should().BeApproximately(2.5, 1e-9);
        projector.RotationRadians.Should().BeApproximately(0.7, 1e-9);
        // Origin stays the anchor, NOT the calibration's world-origin pixel.
        projector.Origin.X.Should().Be(640);
        projector.Origin.Y.Should().Be(360);

        // A pure-north offset still projects straight "up" from the anchor
        // (origin-relative), not from some absolute world-0 pixel.
        var p = projector.Project(new MetreOffset(0, 10));
        var cos = Math.Cos(0.7);
        p.X.Should().BeApproximately(640 + 2.5 * (10 * Math.Sin(0.7)), 1e-6);
        p.Y.Should().BeApproximately(360 - 2.5 * (10 * cos), 1e-6);
    }

    [Fact]
    public void Refit_recovers_known_scale_and_rotation()
    {
        // Ground truth: scale=3 px/m, rotation=30 deg (clockwise)
        const double expectedScale = 3.0;
        var expectedRotation = Math.PI / 6;

        var offsets = new[]
        {
            new MetreOffset(10, 0),
            new MetreOffset(0, 15),
            new MetreOffset(-8, 12),
            new MetreOffset(5, -7),
            new MetreOffset(20, 25),
        };

        var origin = new OverlayPixel(500, 400);
        var truth = new CoordinateProjector();
        truth.SetOrigin(origin);
        // Seed scale and rotation via a synthetic calibration click at a known offset
        var syntheticCalibOffset = new MetreOffset(1, 0);
        truth.CalibrateFromClick(origin, new OverlayPixel(500 + 3 * Math.Cos(expectedRotation),
                                                        400 + 3 * Math.Sin(expectedRotation)), syntheticCalibOffset);
        // That calibration sets rotation/scale based on the synthetic click; we verify later.

        // Build synthetic corrections from GROUND-TRUTH parameters directly.
        var corrections = new List<(MetreOffset, OverlayPixel)>();
        foreach (var offset in offsets)
        {
            var cos = Math.Cos(expectedRotation);
            var sin = Math.Sin(expectedRotation);
            var rotE = offset.East * cos + offset.North * sin;
            var rotN = -offset.East * sin + offset.North * cos;
            var pixel = new OverlayPixel(origin.X + expectedScale * rotE,
                                        origin.Y - expectedScale * rotN);
            corrections.Add((offset, pixel));
        }

        var fitted = new CoordinateProjector();
        fitted.SetOrigin(origin);
        fitted.Refit(corrections);

        fitted.Scale.Should().BeApproximately(expectedScale, 1e-6);
        fitted.RotationRadians.Should().BeApproximately(expectedRotation, 1e-6);
        fitted.Origin.X.Should().BeApproximately(origin.X, 1e-6);
        fitted.Origin.Y.Should().BeApproximately(origin.Y, 1e-6);
    }

    [Fact]
    public void Refit_recovers_true_origin_even_when_setorigin_was_biased()
    {
        // The fix: the original 2-DOF Refit kept origin pinned to wherever SetOrigin
        // last put it, absorbing any anchor bias into scale/rotation as a permanent
        // residual — worst near the anchor, vanishingly small far away. The new
        // 4-DOF Refit recovers the true origin alongside scale/rotation.

        const double expectedScale = 3.0;
        var expectedRotation = Math.PI / 6;
        var trueOrigin = new OverlayPixel(500, 400);
        var biasedOrigin = new OverlayPixel(508, 397); // user clicked 8px east, 3px north of where they actually are

        var offsets = new[]
        {
            new MetreOffset(10, 0),
            new MetreOffset(0, 15),
            new MetreOffset(-8, 12),
            new MetreOffset(5, -7),
            new MetreOffset(20, 25),
        };

        var corrections = new List<(MetreOffset, OverlayPixel)>();
        foreach (var offset in offsets)
        {
            var cos = Math.Cos(expectedRotation);
            var sin = Math.Sin(expectedRotation);
            var rotE = offset.East * cos + offset.North * sin;
            var rotN = -offset.East * sin + offset.North * cos;
            var pixel = new OverlayPixel(trueOrigin.X + expectedScale * rotE,
                                        trueOrigin.Y - expectedScale * rotN);
            corrections.Add((offset, pixel));
        }

        var fitted = new CoordinateProjector();
        fitted.SetOrigin(biasedOrigin);
        fitted.Refit(corrections);

        fitted.Scale.Should().BeApproximately(expectedScale, 1e-6);
        fitted.RotationRadians.Should().BeApproximately(expectedRotation, 1e-6);
        fitted.Origin.X.Should().BeApproximately(trueOrigin.X, 1e-6);
        fitted.Origin.Y.Should().BeApproximately(trueOrigin.Y, 1e-6);
    }

    [Fact]
    public void Refit_is_noop_when_all_corrections_are_coincident()
    {
        // Two corrections at the same offset/pixel carry no rotation/scale info —
        // their centred metre vectors are zero, so Σ |z'|² = 0. Should silently
        // no-op rather than divide by zero.
        var projector = new CoordinateProjector();
        projector.SetOrigin(new OverlayPixel(100, 100));
        var originalScale = projector.Scale;
        var originalRotation = projector.RotationRadians;

        var pt = (new MetreOffset(5, 5), new OverlayPixel(120, 80));
        projector.Refit(new[] { pt, pt });

        projector.Scale.Should().Be(originalScale);
        projector.RotationRadians.Should().Be(originalRotation);
    }

    [Fact]
    public void Refit_is_noop_with_fewer_than_two_corrections()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new OverlayPixel(0, 0));
        var originalScale = projector.Scale;

        projector.Refit(new[] { (new MetreOffset(5, 0), new OverlayPixel(10, 0)) });

        projector.Scale.Should().Be(originalScale);
    }

    [Fact]
    public void Round_trip_projection_preserves_magnitude_with_unit_scale()
    {
        var projector = new CoordinateProjector();
        projector.SetOrigin(new OverlayPixel(0, 0));
        projector.CalibrateFromClick(
            OverlayPixel.Zero,
            new OverlayPixel(0, -10),
            new MetreOffset(0, 10));

        projector.Scale.Should().BeApproximately(1.0, 1e-6);
        projector.RotationRadians.Should().BeApproximately(0.0, 1e-6);

        var projected = projector.Project(new MetreOffset(3, 4));
        projected.DistanceTo(OverlayPixel.Zero).Should().BeApproximately(5.0, 1e-6);
    }
}
