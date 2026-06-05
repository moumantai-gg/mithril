using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class WorldToTextureCalibrationTests
{
    // Canonical fixture: scale 4, 30° rotation, mirror off, calibration zoom 1.
    private static readonly WorldToTextureCalibration Canonical = new(
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
        new object[] { new WorldCoord(-15, 99, -3) }, // negative + non-zero Y
        new object[] { new WorldCoord(0, 0, 1000) },
    };

    [Theory, MemberData(nameof(Worlds))]
    public void ToTexture_MatchesLegacyWorldToWindow_BitIdentical(WorldCoord world)
    {
        var newResult = Canonical.ToTexture(world, currentZoom: 1.0);
        var oldResult = LegacyEquivalent.WorldToWindow(world, currentZoom: 1.0);

        newResult.X.Should().Be(oldResult.X);
        newResult.Y.Should().Be(oldResult.Y);
        newResult.Z.Should().Be(0); // texture frame Z always 0
    }

    [Theory, MemberData(nameof(Worlds))]
    public void FromTexture_MatchesLegacyWindowToWorld_BitIdentical(WorldCoord world)
    {
        // Round-trip through the new struct.
        var pixel = Canonical.ToTexture(world, 1.0);
        var newRoundTrip = Canonical.FromTexture(pixel, 1.0);

        // Round-trip through the old struct.
        var oldPixel = LegacyEquivalent.WorldToWindow(world, 1.0);
        var oldRoundTrip = LegacyEquivalent.WindowToWorld(oldPixel, 1.0);

        newRoundTrip.Should().NotBeNull();
        oldRoundTrip.Should().NotBeNull();
        newRoundTrip!.Value.X.Should().Be(oldRoundTrip!.Value.X);
        newRoundTrip.Value.Z.Should().Be(oldRoundTrip.Value.Z);
        // Y (elevation) is dropped by both; both return 0.
    }

    [Fact]
    public void ToTexture_HonoursZoomFactor()
    {
        var atUnitZoom = Canonical.ToTexture(new WorldCoord(10, 0, 0), 1.0);
        var atDoubleZoom = Canonical.ToTexture(new WorldCoord(10, 0, 0), 2.0);

        // Doubling currentZoom doubles the effective scale → pixel offset from origin doubles.
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
        var u = unmirrored.ToTexture(world, 1.0);
        var m = mirrored.ToTexture(world, 1.0);

        // The mirror flips the north component → the Y offset from origin inverts.
        var uOffsetY = u.Y - Canonical.OriginY;
        var mOffsetY = m.Y - Canonical.OriginY;
        mOffsetY.Should().BeApproximately(-uOffsetY, 1e-9);
    }

    [Fact]
    public void ProjectThroughOverlay_ComposesTextureFrameOntoOverlayRect()
    {
        // A texture-frame calibration with known parameters.
        var texCal = new WorldToTextureCalibration(
            OriginX: 100, OriginY: 200, Scale: 4.0,
            RotationRadians: 0, MirrorNorth: false, CalibrationZoom: 1.0);

        // The texture renders onto the overlay at a known placement:
        // the overlay shows the 1000×500 texture at half-size starting at overlay (30, 40).
        var overlayRect = new MapRect(
            OriginX: 30, OriginY: 40,
            Width: 500, Height: 250,
            TextureWidth: 1000, TextureHeight: 500);

        var overlayCal = texCal.ProjectThroughOverlay(overlayRect);

        // A world point projected through texCal then composed onto the overlay
        // should equal projecting through the resulting overlayCal directly.
        var world = new WorldCoord(7, 0, 3);
        var viaCompose = texCal.ToTexture(world);
        var expectedOverlay = new OverlayPixel(
            overlayRect.OriginX + (viaCompose.X * overlayRect.Width / overlayRect.TextureWidth),
            overlayRect.OriginY + (viaCompose.Y * overlayRect.Height / overlayRect.TextureHeight));

        var viaBridge = overlayCal.ToOverlay(world);

        viaBridge.X.Should().BeApproximately(expectedOverlay.X, 1e-9);
        viaBridge.Y.Should().BeApproximately(expectedOverlay.Y, 1e-9);
    }
}
