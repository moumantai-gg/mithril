using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CaptureServiceTests
{
    [Fact]
    public async Task Returns_null_gray_when_capture_fails()
    {
        var svc = new CaptureService(new FailingCapture(), new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().BeNull();
        result.Color.Should().BeNull();
    }

    [Fact]
    public async Task Returns_gray_for_a_valid_capture()
    {
        var px = new byte[8 * 8 * 4]; Array.Fill(px, (byte)180);
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, px)),
            new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().NotBeNull();
        result.Gray!.Width.Should().Be(8);
        result.Color.Should().NotBeNull("color frame should be returned alongside gray");
    }

    [Fact]
    public async Task Rejects_a_black_capture() // spec §11 "captured our own overlay / occlusion"
    {
        var svc = new CaptureService(new FakeCapture(new CapturedFrame(8, 8, new byte[8 * 8 * 4])),
            new CaptureValidation(), null);
        var result = await svc.CaptureMapAsync(new CaptureRect(0, 0, 8, 8), default);
        result.Gray.Should().BeNull();
        result.Color.Should().BeNull();
    }

    private sealed class FakeCapture(CapturedFrame frame) : IScreenCapture
    {
        public CapturedFrame? Capture(CaptureRect rect) => frame;
    }

    private sealed class FailingCapture : IScreenCapture
    {
        public CapturedFrame? Capture(CaptureRect rect) => null;
    }
}
