using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class LocatedMapRectTests
{
    private static readonly MapRect Inner = new(
        OriginX: 0, OriginY: 0,
        Width: 200, Height: 100,
        TextureWidth: 1000, TextureHeight: 500);

    private static readonly CapturedFramePixel CapturedOrigin = new(320, 58);

    private static readonly LocatedMapRect Located = new(Inner, CapturedOrigin);

    [Fact]
    public void CroppedToCaptured_AddsOrigin()
    {
        var crop = new CroppedFramePixel(10, 20);
        var captured = Located.CroppedToCaptured(crop);

        captured.X.Should().Be(330);
        captured.Y.Should().Be(78);
    }

    [Fact]
    public void CapturedToCropped_SubtractsOrigin()
    {
        var captured = new CapturedFramePixel(330, 78);
        var crop = Located.CapturedToCropped(captured);

        crop.X.Should().Be(10);
        crop.Y.Should().Be(20);
    }

    [Fact]
    public void RoundTrip_CroppedThroughCapturedAndBack()
    {
        var original = new CroppedFramePixel(37, 13);
        var roundTrip = Located.CapturedToCropped(Located.CroppedToCaptured(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }

    [Fact]
    public void MapRect_ExposesInnerRectForTextureSideConversions()
    {
        Located.MapRect.Should().Be(Inner);
    }
}
