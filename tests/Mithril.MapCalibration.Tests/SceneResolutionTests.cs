using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class SceneResolutionTests
{
    [Fact]
    public void LiveCurrentMapScene_IsPreferred()
    {
        var live = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        var state = new FakeMapState { CurrentMapScene = live, CurrentArea = "AreaSerbule" };
        var cache = new FakeCache(); // empty

        SceneResolution.ResolveCurrentScene(state, cache).Should().Be(live);
    }

    [Fact]
    public void CacheFallback_WhenLiveSceneIsNull()
    {
        var cached = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = "AreaSerbule" };
        var cache = new FakeCache();
        cache.Add(("AreaSerbule", null), cached);

        SceneResolution.ResolveCurrentScene(state, cache).Should().Be(cached);
    }

    [Fact]
    public void StrictGate_BothLiveAndCacheNull()
    {
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = "AreaUnknown" };
        var cache = new FakeCache();

        SceneResolution.ResolveCurrentScene(state, cache).Should().BeNull();
    }

    [Fact]
    public void StrictGate_CurrentAreaIsEmpty()
    {
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = string.Empty };
        var cache = new FakeCache();
        cache.Add((string.Empty, null), new MapSceneRef(string.Empty, null, "Map_X"));

        // Empty parent area key is treated as unknown — never resolve through it.
        SceneResolution.ResolveCurrentScene(state, cache).Should().BeNull();
    }

    private sealed class FakeMapState : IMapState
    {
        public string? CurrentArea { get; set; }
        public string? PreviousArea { get; set; }
        public DateTimeOffset? TransitionedAt { get; set; }
        public MapSceneRef? CurrentMapScene { get; set; }
        public DateTimeOffset? MapSceneMeasuredAt { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public DateTimeOffset? PositionMeasuredAt { get; set; }
        public PositionSource? PositionSource { get; set; }
        public string? CurrentWeather { get; set; }
        public DateTimeOffset? WeatherMeasuredAt { get; set; }
        public IReadOnlyList<MapPinEntry> Pins => Array.Empty<MapPinEntry>();
    }

    private sealed class FakeCache : ISceneAssetCache
    {
        private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
        public void Add((string ParentArea, string? Friendly) key, MapSceneRef scene) => _store[key] = scene;
        public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
            _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
        public void Record(MapSceneRef scene, DateTimeOffset observedAt) =>
            _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    }
}
