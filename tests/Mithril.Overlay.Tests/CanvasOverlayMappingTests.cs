using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.Overlay.Tests;

public class CanvasOverlayMappingTests
{
    [Fact]
    public void IdentityDpi_IsIdentity()
    {
        var m = new CanvasOverlayMapping(DpiScale: 1.0);

        var canvas = new CanvasPixel(100, 200);
        var overlay = m.CanvasToOverlay(canvas);

        overlay.X.Should().Be(100);
        overlay.Y.Should().Be(200);
    }

    [Fact]
    public void NonIdentityDpi_ScalesBothAxes()
    {
        var m = new CanvasOverlayMapping(DpiScale: 1.5);

        var canvas = new CanvasPixel(100, 200);
        var overlay = m.CanvasToOverlay(canvas);

        overlay.X.Should().Be(150);
        overlay.Y.Should().Be(300);
    }

    [Fact]
    public void OverlayToCanvas_InvertsCanvasToOverlay()
    {
        var m = new CanvasOverlayMapping(DpiScale: 1.5);

        var original = new CanvasPixel(37, 13);
        var roundTrip = m.OverlayToCanvas(m.CanvasToOverlay(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }
}
