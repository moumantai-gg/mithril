using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapRectTypedConversionsTests
{
    // A MapRect describing a 200×100 crop carved from a 1000×500 base texture,
    // with the crop-aligned (origin (0,0) in its own screenshot frame) case.
    private static readonly MapRect CropAligned = new(
        OriginX: 0, OriginY: 0,
        Width: 200, Height: 100,
        TextureWidth: 1000, TextureHeight: 500);

    [Fact]
    public void CroppedToTexture_ScalesUpByAspectRatio()
    {
        var cropPixel = new CroppedFramePixel(100, 50); // center of the crop
        var texPixel = CropAligned.CroppedToTexture(cropPixel);

        // Crop is 200×100 mapped onto 1000×500 base — 5× scale on both axes.
        texPixel.X.Should().BeApproximately(500, 1e-9);
        texPixel.Y.Should().BeApproximately(250, 1e-9);
    }

    [Fact]
    public void TextureToCropped_RoundTripsCroppedToTexture()
    {
        var original = new CroppedFramePixel(37, 13);
        var roundTrip = CropAligned.TextureToCropped(CropAligned.CroppedToTexture(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }

    [Fact]
    public void CroppedToTexture_DoesNotRequireZ()
    {
        // CroppedFramePixel(X, Y) defaults Z to 0; texture frame also Z=0.
        var texPixel = CropAligned.CroppedToTexture(new CroppedFramePixel(0, 0));
        texPixel.Z.Should().Be(0);
    }
}
