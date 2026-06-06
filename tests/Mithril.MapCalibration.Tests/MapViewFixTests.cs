using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class MapViewFixTests
{
    [Fact]
    public void Construction_RoundTripsAllFields()
    {
        var t = new DateTimeOffset(2026, 6, 6, 12, 4, 32, TimeSpan.Zero);
        var fix = new MapViewFix(
            PanTexPxX: 100.5, PanTexPxY: 200.25,
            ViewScale: 0.65,
            Confidence: 0.92,
            MeasuredAt: t);

        fix.PanTexPxX.Should().Be(100.5);
        fix.PanTexPxY.Should().Be(200.25);
        fix.ViewScale.Should().Be(0.65);
        fix.Confidence.Should().Be(0.92);
        fix.MeasuredAt.Should().Be(t);
    }

    [Fact]
    public void TextureToOverlay_AppliesPanAndScale()
    {
        var fix = new MapViewFix(
            PanTexPxX: 100, PanTexPxY: 50,
            ViewScale: 2.0,
            Confidence: 1.0,
            MeasuredAt: DateTimeOffset.UnixEpoch);

        // texture pixel (150, 75) is offset (50, 25) from pan, scaled 2× = overlay (100, 50)
        var (ox, oy) = fix.TextureToOverlay(150, 75);

        ox.Should().Be(100);
        oy.Should().Be(50);
    }
}
