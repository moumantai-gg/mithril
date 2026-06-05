using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests.Frames;

public class CanvasPixelTests
{
    [Fact]
    public void TwoArgCtor_DefaultsZToZero()
    {
        var p = new CanvasPixel(3, 4);
        p.X.Should().Be(3);
        p.Y.Should().Be(4);
        p.Z.Should().Be(0);
    }

    [Fact]
    public void ThreeArgCtor_KeepsAllComponents()
    {
        var p = new CanvasPixel(3, 4, 5);
        p.X.Should().Be(3);
        p.Y.Should().Be(4);
        p.Z.Should().Be(5);
    }

    [Fact]
    public void Zero_IsOrigin() =>
        CanvasPixel.Zero.Should().Be(new CanvasPixel(0, 0, 0));

    [Fact]
    public void DistanceTo_Uses2DMath_IgnoringZ()
    {
        var a = new CanvasPixel(0, 0, 100);
        var b = new CanvasPixel(3, 4, -100);
        a.DistanceTo(b).Should().Be(5);
    }

    [Fact]
    public void DistanceSquaredTo_Uses2DMath_IgnoringZ()
    {
        var a = new CanvasPixel(0, 0, 100);
        var b = new CanvasPixel(3, 4, -100);
        a.DistanceSquaredTo(b).Should().Be(25);
    }

    [Fact]
    public void EqualsByComponents()
    {
        var a = new CanvasPixel(1, 2, 3);
        var b = new CanvasPixel(1, 2, 3);
        var c = new CanvasPixel(1, 2, 99);
        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void ImplementsIPixelPoint()
    {
        IPixelPoint p = new CanvasPixel(1, 2, 3);
        p.X.Should().Be(1);
        p.Y.Should().Be(2);
        p.Z.Should().Be(3);
    }
}
