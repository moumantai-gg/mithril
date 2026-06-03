using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public sealed class SceneAssetCacheRecorderTests
{
    [Fact]
    public async Task StartAsync_SubscribesAndRecordsLiveEvents()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        bus.Fire(new MapAssetChanged(PreviousScene: null, CurrentScene: scene, Metadata: NewMetadata(isReplay: false)));

        cache.TryResolve("AreaSerbule", null)!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");

        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RecordsReplayEventsToo()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        var scene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule");
        bus.Fire(new MapAssetChanged(null, scene, NewMetadata(isReplay: true)));

        cache.TryResolve("AreaEltibule", null).Should().NotBeNull();
        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NullCurrentScene_DoesNotRecord()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        bus.Fire(new MapAssetChanged(null, CurrentScene: null, NewMetadata(isReplay: false)));

        cache.TryResolve("AnyArea", null).Should().BeNull();
        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromBus()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);

        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        bus.Fire(new MapAssetChanged(null, scene, NewMetadata(isReplay: false)));

        // After stop, events no longer reach the cache.
        cache.TryResolve("AreaSerbule", null).Should().BeNull();
    }

    private static (TestCache cache, TestDomainEventBus bus, SceneAssetCacheRecorder recorder) BuildHarness()
    {
        var cache = new TestCache();
        var bus = new TestDomainEventBus();
        var recorder = new SceneAssetCacheRecorder(bus, cache);
        return (cache, bus, recorder);
    }

    private static LogLineMetadata NewMetadata(bool isReplay) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    private sealed class TestCache : ISceneAssetCache
    {
        private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
        public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
            _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
        public void Record(MapSceneRef scene, DateTimeOffset observedAt) =>
            _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    }

    private sealed class TestDomainEventBus : IDomainEventSubscriber
    {
        private readonly List<Delegate> _handlers = new();
        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            _handlers.Add(handler);
            return new DummyDisposable(_handlers, handler);
        }
        public void Fire<T>(T evt) where T : struct
        {
            foreach (var h in _handlers.OfType<Action<T>>()) h(evt);
        }
        private sealed class DummyDisposable : IDisposable
        {
            private readonly List<Delegate> _list;
            private readonly Delegate _handler;
            public DummyDisposable(List<Delegate> list, Delegate handler) { _list = list; _handler = handler; }
            public void Dispose() => _list.Remove(_handler);
        }
    }
}
