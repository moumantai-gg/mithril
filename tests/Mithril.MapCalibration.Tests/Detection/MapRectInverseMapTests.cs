using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class MapRectInverseMapTests
{
    [Theory]
    [InlineData(12, 18, 1192, 1020, 4096, 4096, 500.0, 500.0)]
    [InlineData(0, 0, 800, 600, 1024, 1024, 123.4, 567.8)]
    [InlineData(50, 50, 100, 100, 2048, 2048, 50.0, 50.0)]
    public void TextureToScreenshot_inverts_ScreenshotToTexture(
        int ox, int oy, int w, int h, int tw, int th, double sx, double sy)
    {
        var rect = new MapRect(ox, oy, w, h, tw, th);
        var (tx, ty) = rect.ScreenshotToTexture(sx, sy);
        var (rx, ry) = rect.TextureToScreenshot(tx, ty);
        rx.Should().BeApproximately(sx, 1e-9);
        ry.Should().BeApproximately(sy, 1e-9);
    }
}
