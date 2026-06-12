using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class LiveMapViewServiceTests
{
    [Fact]
    public async Task GetCurrent_NeverMeasured_ReturnsNull()
    {
        var svc = NewServiceWith(probe: _ => null);

        svc.GetCurrent("Map_AreaSerbule").Should().BeNull();
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.NeverMeasured);
    }

    [Fact]
    public async Task RefreshAsync_SuccessfulProbe_StoresFixAndRaisesChanged()
    {
        var fix = new MapViewFix(10, 20, 1.0, 0.9, DateTimeOffset.UnixEpoch);
        var raised = new List<string>();
        var svc = NewServiceWith(probe: _ => fix);
        svc.Changed += area => raised.Add(area);

        await svc.RefreshAsync("Map_AreaSerbule");

        svc.GetCurrent("Map_AreaSerbule").Should().Be(fix);
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.Detected);
        raised.Should().HaveCount(2);
        raised.Should().AllBeEquivalentTo("Map_AreaSerbule");
    }

    [Fact]
    public async Task RefreshAsync_FailedProbe_PreservesPriorFixAndSetsFailureStatus()
    {
        var fix = new MapViewFix(10, 20, 1.0, 0.9, DateTimeOffset.UnixEpoch);
        int callCount = 0;
        var svc = NewServiceWith(probe: _ =>
        {
            callCount++;
            return callCount == 1 ? fix : null;  // first OK, second fails
        });

        await svc.RefreshAsync("Map_AreaSerbule");
        await svc.RefreshAsync("Map_AreaSerbule");

        svc.GetCurrent("Map_AreaSerbule").Should().Be(fix);  // prior preserved
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.FailedLowConfidence);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallsForSameArea_DedupeToOneProbe()
    {
        int callCount = 0;
        var gate = new TaskCompletionSource();
        var svc = NewServiceWith(probe: _ =>
        {
            Interlocked.Increment(ref callCount);
            gate.Task.Wait();
            return new MapViewFix(0, 0, 1, 1, DateTimeOffset.UnixEpoch);
        });

        var t1 = svc.RefreshAsync("Map_AreaSerbule");
        var t2 = svc.RefreshAsync("Map_AreaSerbule");
        gate.SetResult();
        await Task.WhenAll(t1, t2);

        callCount.Should().Be(1);
    }

    private static LiveMapViewService NewServiceWith(Func<string, MapViewFix?> probe)
    {
        var probeAdapter = new TestProbe(probe);
        var capture = new TestCapture();
        var textures = new TestBaseTextureProvider();
        return new LiveMapViewService(probeAdapter, capture, textures, uiSynchronizer: a => a());
    }

    private sealed class TestProbe : IMapViewProbe
    {
        private readonly Func<string, MapViewFix?> _impl;
        public TestProbe(Func<string, MapViewFix?> impl) { _impl = impl; }
        public MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture) => _impl("ignored");
    }

    private sealed class TestCapture : IOverlayCaptureSource
    {
        public GrayImage? Capture() => new GrayImage(8, 8, new byte[64]);
    }

    private sealed class TestBaseTextureProvider : IBaseTextureProvider
    {
        public GrayImage? TryGetBaseTexture(string mapAssetKey) => new GrayImage(16, 16, new byte[256]);
        public GrayImage? TryGetTextureAlpha(string mapAssetKey) => null;
    }
}
