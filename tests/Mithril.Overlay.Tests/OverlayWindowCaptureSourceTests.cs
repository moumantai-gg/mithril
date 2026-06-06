using FluentAssertions;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

public sealed class OverlayWindowCaptureSourceTests
{
    [Fact]
    public void Capture_WithoutRegisteredWindow_ReturnsNull()
    {
        var source = new OverlayWindowCaptureSource(windowAccessor: () => null);

        source.Capture().Should().BeNull();
    }
}
