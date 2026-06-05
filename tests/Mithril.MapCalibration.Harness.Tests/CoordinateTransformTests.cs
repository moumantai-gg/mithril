using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibration.Harness;
using Xunit;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

public class CoordinateTransformTests
{
    private static CalibrationContext Context(MapRect rect, int texW, int texH) =>
        new(
            area: "TestArea",
            landmarks: new List<LandmarkRef>(),
            mapImage: null,
            mapRect: rect,
            textureSize: (texW, texH),
            currentCalibration: null);

    public static TheoryData<MapRect, int, int> RectsAndSizes() => new()
    {
        { new MapRect(0, 0, 100, 100), 100, 100 },        // identity
        { new MapRect(0, 0, 200, 200), 100, 100 },        // 2x scale
        { new MapRect(37.5, 12.25, 640, 480), 1024, 768 },// offset + non-square
        { new MapRect(-50, -25, 800, 600), 2048, 1536 },  // negative origin
        { new MapRect(10, 10, 333, 777), 512, 256 },      // anisotropic
    };

    [Theory]
    [MemberData(nameof(RectsAndSizes))]
    public void Screenshot_to_texture_round_trips(MapRect rect, int texW, int texH)
    {
        var ctx = Context(rect, texW, texH);

        foreach (var (sx, sy) in new[] { (0.0, 0.0), (12.3, 45.6), (rect.Left + rect.Width, rect.Top + rect.Height), (-5.0, 999.0) })
        {
            var texture = ctx.ScreenshotToTexture(sx, sy);
            var back = ctx.TextureToScreenshot(texture);
            back.Sx.Should().BeApproximately(sx, 1e-9);
            back.Sy.Should().BeApproximately(sy, 1e-9);
        }
    }

    [Fact]
    public void Screenshot_to_texture_uses_the_documented_formula()
    {
        // rect (left=10, top=20, w=200, h=100), texture 100x50.
        // A click at the rect's far corner maps to the full texture extent.
        var ctx = Context(new MapRect(10, 20, 200, 100), 100, 50);

        ctx.ScreenshotToTexture(10, 20).Should().Be(new TexturePixel(0, 0));
        ctx.ScreenshotToTexture(210, 120).Should().Be(new TexturePixel(100, 50));
        // Midpoint of the rect → midpoint of the texture.
        ctx.ScreenshotToTexture(110, 70).Should().Be(new TexturePixel(50, 25));
    }

    [Fact]
    public void Transforms_throw_without_a_map_rect()
    {
        var ctx = new CalibrationContext("A", new List<LandmarkRef>(), null, mapRect: null, (100, 100), null);

        ctx.Invoking(c => c.ScreenshotToTexture(1, 2))
            .Should().Throw<InvalidOperationException>().WithMessage("*none was set*");
        ctx.Invoking(c => c.TextureToScreenshot(new TexturePixel(1, 2)))
            .Should().Throw<InvalidOperationException>().WithMessage("*none was set*");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-50, 100)]
    [InlineData(100, -50)]
    public void Transforms_throw_on_a_degenerate_map_rect(double width, double height)
    {
        // A zero/negative dimension would silently divide to Infinity/NaN; the
        // context must throw with a message distinct from the missing-rect case.
        var ctx = Context(new MapRect(0, 0, width, height), 100, 100);

        ctx.Invoking(c => c.ScreenshotToTexture(1, 2))
            .Should().Throw<InvalidOperationException>().WithMessage("*non-positive dimension*");
        ctx.Invoking(c => c.TextureToScreenshot(new TexturePixel(1, 2)))
            .Should().Throw<InvalidOperationException>().WithMessage("*non-positive dimension*");
    }
}
