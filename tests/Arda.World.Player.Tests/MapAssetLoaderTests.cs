using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class MapAssetLoaderTests
{
    private readonly SpyEventBus _bus = new();
    private readonly MapAssetLoader _handler;

    public MapAssetLoaderTests()
    {
        _handler = new MapAssetLoader(_bus);
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
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

        _handler.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
        _handler.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");
        _handler.MapAssetMeasuredAt.Should().NotBeNull();

        var changed = _bus.Published<MapAssetChanged>().Should().ContainSingle().Subject;
        changed.PreviousMapAsset.Should().BeNull();
        changed.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
        changed.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");
    }

    [Theory]
    [InlineData("Hogan's Basement", "Map_HogansKeepBasement")]
    [InlineData("Caves Beneath Kur Mountains", "Map_AreaKurCaves")]
    [InlineData("Serbule", "Map_AreaSerbule")]
    [InlineData("Anagoge Island", "Map_AreaNewbieIsland")]
    public void Parses_VariousFriendlyAndAssetForms(string friendly, string asset)
    {
        Dispatch(friendly, asset);
        _handler.CurrentMapAsset.Should().Be(asset);
        _handler.CurrentSceneFriendlyName.Should().Be(friendly);
    }

    [Fact]
    public void Idempotent_ReParse_DoesNotRepublish()
    {
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
        _bus.Published<MapAssetChanged>().Should().ContainSingle();
    }

    [Fact]
    public void Transition_PopulatesPreviousMapAsset()
    {
        Dispatch("Serbule", "Map_AreaSerbule");
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

        var events = _bus.Published<MapAssetChanged>().ToList();
        events.Should().HaveCount(2);
        events[1].PreviousMapAsset.Should().Be("Map_AreaSerbule");
        events[1].CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
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
        _handler.CurrentMapAsset.Should().BeNull();
        _handler.CurrentSceneFriendlyName.Should().BeNull();
        _bus.Published<MapAssetChanged>().Should().BeEmpty();
    }

    [Fact]
    public void LastBracketWins_NotTheArgsHeadGuidBracket()
    {
        Dispatch("Test Area", "Map_TestScene");
        _handler.CurrentMapAsset.Should().Be("Map_TestScene");
        _handler.CurrentMapAsset.Should().NotStartWith("44d50fb"); // not the GUID
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
