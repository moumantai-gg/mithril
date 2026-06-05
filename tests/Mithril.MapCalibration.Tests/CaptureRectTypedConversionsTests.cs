using FluentAssertions;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class CaptureRectTypedConversionsTests
{
    [Fact]
    public void GameWindowToCaptured_TranslatesByRectOrigin()
    {
        var rect = new CaptureRect(X: 100, Y: 50, Width: 800, Height: 600);

        var captured = rect.GameWindowToCaptured(new GameWindowPixel(110, 60));
        captured.X.Should().Be(10);
        captured.Y.Should().Be(10);
    }

    [Fact]
    public void CapturedToGameWindow_AddsRectOrigin()
    {
        var rect = new CaptureRect(X: 100, Y: 50, Width: 800, Height: 600);
        var gw = rect.CapturedToGameWindow(new CapturedFramePixel(10, 10));
        gw.X.Should().Be(110);
        gw.Y.Should().Be(60);
    }

    [Fact]
    public void RoundTrip_GameWindowThroughCapturedAndBack()
    {
        var rect = new CaptureRect(X: 100, Y: 50, Width: 800, Height: 600);
        var original = new GameWindowPixel(135, 87);
        var roundTrip = rect.CapturedToGameWindow(rect.GameWindowToCaptured(original));

        roundTrip.X.Should().BeApproximately(original.X, 1e-9);
        roundTrip.Y.Should().BeApproximately(original.Y, 1e-9);
    }
}
