using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Arda.World.Player.Tests;

public class MapAssetLoaderTests
{
    private readonly SpyEventBus _bus = new();
    private readonly FakeAreaState _areaState = new();
    private readonly MapAssetLoader _handler;

    public MapAssetLoaderTests()
    {
        _handler = new MapAssetLoader(_bus, _areaState);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    /// <summary>Simulates what the DispatchTable does for a DownloadingMap verb:
    /// args is everything after "Downloading Map " in the source line.</summary>
    private void Dispatch(string friendlyArea, string mapAsset, LogLineMetadata? meta = null)
    {
        var source =
            $"Downloading Map [44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 "
            + $"for area {friendlyArea} runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[{mapAsset}]";
        var args = source["Downloading Map ".Length..].AsSpan();
        _handler.Handle(args, default, source, meta ?? Meta());
    }

    [Fact]
    public void Parses_HogansBasement_HappyPath()
    {
        _areaState.CurrentArea = "AreaCave1";
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

        _handler.CurrentMapScene.Should().NotBeNull();
        _handler.CurrentMapScene!.Value.MapAssetKey.Should().Be("Map_HogansKeepBasement");
        _handler.CurrentMapScene!.Value.SceneFriendlyName.Should().Be("Hogan's Basement");
        _handler.CurrentMapScene!.Value.ParentAreaKey.Should().Be("AreaCave1");
        _handler.MapSceneMeasuredAt.Should().NotBeNull();

        var changed = _bus.Published<MapAssetChanged>().Should().ContainSingle().Subject;
        changed.PreviousScene.Should().BeNull();
        changed.CurrentScene.Should().NotBeNull();
        changed.CurrentScene!.Value.MapAssetKey.Should().Be("Map_HogansKeepBasement");
        changed.CurrentScene!.Value.SceneFriendlyName.Should().Be("Hogan's Basement");
        changed.CurrentScene!.Value.ParentAreaKey.Should().Be("AreaCave1");
    }

    [Theory]
    [InlineData("Hogan's Basement", "Map_HogansKeepBasement", "AreaCave1")]
    [InlineData("Caves Beneath Kur Mountains", "Map_AreaKurCaves", "AreaKurCaves")]
    [InlineData("Serbule", "Map_AreaSerbule", "AreaSerbule")]
    [InlineData("Anagoge Island", "Map_AreaNewbieIsland", "AreaNewbieIsland")]
    public void Parses_VariousFriendlyAndAssetForms(string friendly, string asset, string parent)
    {
        _areaState.CurrentArea = parent;
        Dispatch(friendly, asset);
        _handler.CurrentMapScene!.Value.MapAssetKey.Should().Be(asset);
        _handler.CurrentMapScene!.Value.SceneFriendlyName.Should().Be(friendly);
        _handler.CurrentMapScene!.Value.ParentAreaKey.Should().Be(parent);
    }

    [Fact]
    public void Idempotent_ReParse_DoesNotRepublish()
    {
        _areaState.CurrentArea = "AreaCave1";
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
        _bus.Published<MapAssetChanged>().Should().ContainSingle();
    }

    [Fact]
    public void Replay_LastDownloadingMapLineWins()
    {
        _areaState.CurrentArea = "AreaSerbule";
        Dispatch("Serbule", "Map_AreaSerbule", Meta(isReplay: true));
        _areaState.CurrentArea = "AreaEltibule";
        Dispatch("Eltibule", "Map_AreaEltibule", Meta(isReplay: true));
        _areaState.CurrentArea = "AreaCave1";
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement", Meta(isReplay: true));

        _handler.CurrentMapScene!.Value.MapAssetKey.Should().Be("Map_HogansKeepBasement");
        _handler.CurrentMapScene!.Value.SceneFriendlyName.Should().Be("Hogan's Basement");
        _handler.CurrentMapScene!.Value.ParentAreaKey.Should().Be("AreaCave1");
        _bus.Published<MapAssetChanged>().Should().HaveCount(3);
    }

    [Fact]
    public void Transition_PopulatesPreviousScene()
    {
        _areaState.CurrentArea = "AreaSerbule";
        Dispatch("Serbule", "Map_AreaSerbule");
        _areaState.CurrentArea = "AreaCave1";
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

        var events = _bus.Published<MapAssetChanged>().ToList();
        events.Should().HaveCount(2);
        events[1].PreviousScene.Should().NotBeNull();
        events[1].PreviousScene!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");
        events[1].CurrentScene!.Value.MapAssetKey.Should().Be("Map_HogansKeepBasement");
    }

    [Fact]
    public void SubZoneTransition_WithinSameParentArea_PreservesParentAreaKey()
    {
        // Two sub-zones in the same aggregator area: parent key carries through
        // via the with-expression branch.
        _areaState.CurrentArea = "AreaCave1";
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
        var first = _handler.CurrentMapScene!.Value;

        Dispatch("Goblin Dungeon", "Map_GoblinDungeon");
        var second = _handler.CurrentMapScene!.Value;

        first.ParentAreaKey.Should().Be("AreaCave1");
        second.ParentAreaKey.Should().Be("AreaCave1");
        second.MapAssetKey.Should().Be("Map_GoblinDungeon");
        second.SceneFriendlyName.Should().Be("Goblin Dungeon");
        second.MapAssetKey.Should().NotBe(first.MapAssetKey);
        _bus.Published<MapAssetChanged>().Should().HaveCount(2);
    }

    [Fact]
    public void Parses_BeforeAreaIsKnown_ParentAreaKeyIsEmpty()
    {
        // Cold-start: Downloading Map line fires before any Initializing area! —
        // unusual but valid. ParentAreaKey is empty; consumers treat as strict-gate.
        Dispatch("Serbule", "Map_AreaSerbule");

        _handler.CurrentMapScene!.Value.ParentAreaKey.Should().BeEmpty();
        _handler.CurrentMapScene!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");
    }

    [Theory]
    [InlineData("")]
    [InlineData("[GUID] no for-area no runtime-key")]
    [InlineData("[GUID] for area X but no runtime key delimiter")]
    [InlineData("[GUID] for area X runtime key GUID no_close_bracket")]
    [InlineData("[GUID] for area X runtime key GUID[]")]
    public void Malformed_SilentSkip_NoStateMutation_NoEvent(string args)
    {
        _handler.Handle(args.AsSpan(), default, "Downloading Map " + args, Meta());
        _handler.CurrentMapScene.Should().BeNull();
        _bus.Published<MapAssetChanged>().Should().BeEmpty();
    }

    [Fact]
    public void LastBracketWins_NotTheArgsHeadGuidBracket()
    {
        _areaState.CurrentArea = "AreaTest";
        Dispatch("Test Area", "Map_TestScene");
        _handler.CurrentMapScene!.Value.MapAssetKey.Should().Be("Map_TestScene");
        _handler.CurrentMapScene!.Value.MapAssetKey.Should().NotStartWith("44d50fb"); // not the GUID
    }

    private sealed class FakeAreaState : IAreaState
    {
        public string? CurrentArea { get; set; }
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
