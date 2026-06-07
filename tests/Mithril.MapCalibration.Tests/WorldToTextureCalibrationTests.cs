using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class WorldToTextureCalibrationTests
{
    // Canonical fixture: scale 4, 30° rotation, mirror off.
    private static readonly WorldToTextureCalibration Canonical = new(
        OriginX: 100,
        OriginY: 200,
        Scale: 4.0,
        RotationRadians: Math.PI / 6,
        MirrorNorth: false);

    public static IEnumerable<object[]> Worlds() => new[]
    {
        new object[] { new WorldCoord(0, 0, 0) },
        new object[] { new WorldCoord(10, 0, 5) },
        new object[] { new WorldCoord(-15, 99, -3) }, // negative + non-zero Y
        new object[] { new WorldCoord(0, 0, 1000) },
    };

    [Theory, MemberData(nameof(Worlds))]
    public void ToTexture_RoundTripsThroughFromTexture(WorldCoord world)
    {
        // Round-trip is the contract: project then unproject must recover
        // the input world coord on the (X, Z) ground plane (Y elevation is
        // always dropped to 0 by the projection model — pixels are 2D).
        var pixel = Canonical.ToTexture(world);
        var recovered = Canonical.FromTexture(pixel);

        recovered.Should().NotBeNull();
        recovered!.Value.X.Should().BeApproximately(world.X, 1e-9);
        recovered.Value.Z.Should().BeApproximately(world.Z, 1e-9);
        recovered.Value.Y.Should().Be(0); // elevation cannot be recovered from a 2D pixel
        pixel.Z.Should().Be(0); // texture frame Z always 0
    }

    [Fact]
    public void ToTexture_ScaleProportionalToPixelOffset()
    {
        // Doubling Scale doubles the pixel offset from origin.
        var atUnitScale = Canonical.ToTexture(new WorldCoord(10, 0, 0));
        var atDoubleScale = (Canonical with { Scale = Canonical.Scale * 2 }).ToTexture(new WorldCoord(10, 0, 0));

        var unitOffsetX = atUnitScale.X - Canonical.OriginX;
        var doubleOffsetX = atDoubleScale.X - Canonical.OriginX;
        doubleOffsetX.Should().BeApproximately(2 * unitOffsetX, 1e-9);
    }

    [Fact]
    public void MirrorNorth_FlipsZAxis()
    {
        var unmirrored = Canonical with { MirrorNorth = false };
        var mirrored = Canonical with { MirrorNorth = true };

        var world = new WorldCoord(0, 0, 10);
        var u = unmirrored.ToTexture(world);
        var m = mirrored.ToTexture(world);

        // The mirror flips the north component → the Y offset from origin inverts.
        var uOffsetY = u.Y - Canonical.OriginY;
        var mOffsetY = m.Y - Canonical.OriginY;
        mOffsetY.Should().BeApproximately(-uOffsetY, 1e-9);
    }

    // mithril#1107 review fix: ProjectThroughOverlay_ComposesTextureFrameOntoOverlayRect
    // test deleted along with WorldToTextureCalibration.ProjectThroughOverlay. The
    // composer is now a rebrand-only operation (the texture cal's fields become the
    // overlay cal's fields verbatim); the rebrand semantics are exercised by
    // ComposedOverlayCalibrationResolverTests.AutoCalOnly_RebrandsTextureCalAsComposedFromTexture.

    [Fact]
    public void PixelSha256_CarryThroughTheStruct()
    {
        // mithril#1081 — the texture identity travels with the typed projection
        // struct, not just the AreaCalibration record. Post-#1107 the sha is
        // informational only (the composer no longer looks up catalogue dims),
        // but consumers like the drift-check still read it from the struct.
        var cal = new WorldToTextureCalibration(
            OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false)
        {
            PixelSha256 = "abc123",
        };

        cal.PixelSha256.Should().Be("abc123");
    }
}
