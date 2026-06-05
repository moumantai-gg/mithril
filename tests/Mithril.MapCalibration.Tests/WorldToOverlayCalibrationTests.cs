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

    // Same parameters expressed as the legacy AreaCalibration shape.
    private static readonly AreaCalibration LegacyEquivalent = new(
        Scale: 4.0,
        RotationRadians: Math.PI / 6,
        OriginX: 100,
        OriginY: 200,
        ReferenceCount: 0,
        ResidualPixels: 0)
    {
        MirrorNorth = false,
        CalibrationZoom = 1.0,
    };

    public static IEnumerable<object[]> Worlds() => new[]
    {
        new object[] { new WorldCoord(0, 0, 0) },
        new object[] { new WorldCoord(10, 0, 5) },
        new object[] { new WorldCoord(-15, 99, -3) },
        new object[] { new WorldCoord(0, 0, 1000) },
    };

    [Theory, MemberData(nameof(Worlds))]
    public void ToOverlay_MatchesLegacyWorldToWindow_BitIdentical(WorldCoord world)
    {
        var newResult = Canonical.ToOverlay(world, currentZoom: 1.0);
        var oldResult = LegacyEquivalent.WorldToWindow(world, currentZoom: 1.0);

        newResult.X.Should().Be(oldResult.X);
        newResult.Y.Should().Be(oldResult.Y);
        newResult.Z.Should().Be(0);
    }

    [Theory, MemberData(nameof(Worlds))]
    public void FromOverlay_MatchesLegacyWindowToWorld_BitIdentical(WorldCoord world)
    {
        var pixel = Canonical.ToOverlay(world, 1.0);
        var newRoundTrip = Canonical.FromOverlay(pixel, 1.0);

        var oldPixel = LegacyEquivalent.WorldToWindow(world, 1.0);
        var oldRoundTrip = LegacyEquivalent.WindowToWorld(oldPixel, 1.0);

        newRoundTrip.Should().NotBeNull();
        oldRoundTrip.Should().NotBeNull();
        newRoundTrip!.Value.X.Should().Be(oldRoundTrip!.Value.X);
        newRoundTrip.Value.Z.Should().Be(oldRoundTrip.Value.Z);
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
