using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class WorldToOverlayCalibrationTests
{
    // Canonical fixture: scale 4, 30° rotation, mirror off, calibration zoom 1.
    private static readonly WorldToOverlayCalibration Canonical = new(
        OriginX: 100,
        OriginY: 200,
        Scale: 4.0,
        RotationRadians: Math.PI / 6,
        MirrorNorth: false,
        CalibrationZoom: 1.0);

    public static IEnumerable<object[]> Worlds() => new[]
    {
        new object[] { new WorldCoord(0, 0, 0) },
        new object[] { new WorldCoord(10, 0, 5) },
        new object[] { new WorldCoord(-15, 99, -3) },
        new object[] { new WorldCoord(0, 0, 1000) },
    };

    [Theory, MemberData(nameof(Worlds))]
    public void ToOverlay_RoundTripsThroughFromOverlay(WorldCoord world)
    {
        // Round-trip is the contract: project then unproject must recover
        // the input world coord on the (X, Z) ground plane (Y elevation is
        // always dropped to 0 by the projection model — pixels are 2D).
        var pixel = Canonical.ToOverlay(world, currentZoom: 1.0);
        var recovered = Canonical.FromOverlay(pixel, currentZoom: 1.0);

        recovered.Should().NotBeNull();
        recovered!.Value.X.Should().BeApproximately(world.X, 1e-9);
        recovered.Value.Z.Should().BeApproximately(world.Z, 1e-9);
        recovered.Value.Y.Should().Be(0);
        pixel.Z.Should().Be(0);
    }

    [Fact]
    public void ToOverlay_HonoursZoomFactor()
    {
        var atUnitZoom = Canonical.ToOverlay(new WorldCoord(10, 0, 0), 1.0);
        var atDoubleZoom = Canonical.ToOverlay(new WorldCoord(10, 0, 0), 2.0);

        var unitOffsetX = atUnitZoom.X - Canonical.OriginX;
        var doubleOffsetX = atDoubleZoom.X - Canonical.OriginX;
        doubleOffsetX.Should().BeApproximately(2 * unitOffsetX, 1e-9);
    }

    [Fact]
    public void MirrorNorth_FlipsZAxis()
    {
        var unmirrored = Canonical with { MirrorNorth = false };
        var mirrored = Canonical with { MirrorNorth = true };

        var world = new WorldCoord(0, 0, 10);
        var u = unmirrored.ToOverlay(world, 1.0);
        var m = mirrored.ToOverlay(world, 1.0);

        var uOffsetY = u.Y - Canonical.OriginY;
        var mOffsetY = m.Y - Canonical.OriginY;
        mOffsetY.Should().BeApproximately(-uOffsetY, 1e-9);
    }
}
