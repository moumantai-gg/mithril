using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Palantir.ViewModels;
using Xunit;

namespace Palantir.Tests;

public sealed class WorldStateViewModelTests
{
    private static readonly DateTimeOffset T =
        new(2026, 5, 18, 10, 45, 47, TimeSpan.Zero);

    private static LogLineMetadata Meta(DateTimeOffset? ts = null)
        => new(ts ?? T, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void No_area_no_position_shows_defaults()
    {
        using var vm = NewVm(out _);

        vm.AreaKey.Should().Be("(none)");
        vm.AreaResolved.Should().BeFalse();
        vm.HasPosition.Should().BeFalse();
        vm.PositionText.Should().Contain("no position");
    }

    [Fact]
    public void Refresh_resolves_area_through_areas_json()
    {
        var refData = new FakeRefData(("AreaSerbule", new AreaEntry("AreaSerbule", "Serbule", "Serb")));
        var area = new FakeAreaState();
        using var vm = NewVm(out _, area: area, refData: refData);

        area.CurrentArea = "AreaSerbule";
        vm.RefreshCommand.Execute(null);

        vm.AreaKey.Should().Be("AreaSerbule");
        vm.AreaFriendlyName.Should().Be("Serbule");
        vm.AreaShortName.Should().Be("Serb");
        vm.AreaResolved.Should().BeTrue();
    }

    [Fact]
    public void Short_name_suppressed_when_equal_to_friendly_name()
    {
        var refData = new FakeRefData(("AreaTomb1", new AreaEntry("AreaTomb1", "The Tomb", "The Tomb")));
        var area = new FakeAreaState();
        using var vm = NewVm(out _, area: area, refData: refData);

        area.CurrentArea = "AreaTomb1";
        vm.RefreshCommand.Execute(null);

        vm.AreaFriendlyName.Should().Be("The Tomb");
        vm.AreaShortName.Should().BeEmpty();
    }

    [Fact]
    public void Area_key_known_but_absent_from_areas_json_falls_back_to_key()
    {
        var area = new FakeAreaState();
        using var vm = NewVm(out _, area: area, refData: new FakeRefData());

        area.CurrentArea = "AreaMysteryZone";
        vm.RefreshCommand.Execute(null);

        vm.AreaKey.Should().Be("AreaMysteryZone");
        vm.AreaFriendlyName.Should().Be("AreaMysteryZone");
        vm.AreaResolved.Should().BeFalse();
    }

    [Fact]
    public void Position_event_populates_coords_instant_and_reresolves_area()
    {
        var refData = new FakeRefData(("AreaSerbule", new AreaEntry("AreaSerbule", "Serbule", "Serb")));
        var area = new FakeAreaState { CurrentArea = "AreaSerbule" };
        using var vm = NewVm(out var bus, area: area, refData: refData);

        bus.Fire(new PlayerPositionChanged(834.09, 290.24, 3480.81, PositionSource.Movement, Meta()));

        vm.HasPosition.Should().BeTrue();
        vm.PositionText.Should().Be("X 834.09   Y 290.24   Z 3480.81");
        vm.MeasuredAtText.Should().Be("2026-05-18 10:45:47Z");
        vm.PositionSourceText.Should().Contain("ProcessNewPosition");
        vm.AreaFriendlyName.Should().Be("Serbule");
    }

    [Fact]
    public void Seed_from_state_picks_up_existing_position()
    {
        var pos = new FakePositionState { X = 1, Y = 2, Z = 3 };
        using var vm = NewVm(out _, position: pos);

        vm.HasPosition.Should().BeTrue();
        vm.PositionText.Should().Be("X 1.00   Y 2.00   Z 3.00");
    }

    [Fact]
    public void Dispose_stops_observing_position()
    {
        using var vm = NewVm(out var bus);
        vm.Dispose();

        bus.Fire(new PlayerPositionChanged(9, 9, 9, PositionSource.Movement, Meta()));

        vm.HasPosition.Should().BeFalse("the disposed VM must unsubscribe");
    }

    // --- Pins ---------------------------------------------------------------

    [Fact]
    public void Seed_from_state_picks_up_existing_pins()
    {
        var pins = new FakeMapPinState([
            new MapPinEntry(10, -20, "Vendor", 1, 2),
            new MapPinEntry(-5.5, 99.25, "", 0, 0),
        ]);
        using var vm = NewVm(out _, pins: pins);

        vm.HasPins.Should().BeTrue();
        vm.PinCount.Should().Be(2);
        vm.Pins.Should().HaveCount(2);
    }

    [Fact]
    public void MapPinAdded_refreshes_pin_rows_and_stamps_observed_at()
    {
        var pins = new FakeMapPinState();
        using var vm = NewVm(out var bus, pins: pins);

        bus.Fire(new MapPinAdded(123.4, -567.8, "Camp", 2, 3, Meta()));

        vm.HasPins.Should().BeTrue();
        vm.PinCount.Should().Be(1);
        var row = vm.Pins.Single();
        row.Label.Should().Be("Camp");
        row.X.Should().BeApproximately(123.4, 0.001);
        row.Z.Should().BeApproximately(-567.8, 0.001);
        vm.PinsObservedAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void MapPinRemoved_drops_the_row()
    {
        var b = new MapPinEntry(2, 2, "B", 0, 0);
        var pins = new FakeMapPinState([new MapPinEntry(1, 1, "A", 0, 0), b]);
        using var vm = NewVm(out var bus, pins: pins);

        bus.Fire(new MapPinRemoved(1, 1, "A", Meta()));

        vm.Pins.Select(r => r.Label).Should().ContainSingle().Which.Should().Be("B");
    }

    [Fact]
    public void Unlabeled_pin_falls_back_to_placeholder_name()
    {
        var pins = new FakeMapPinState();
        using var vm = NewVm(out var bus, pins: pins);

        bus.Fire(new MapPinAdded(-3.14, 0, "", 0, 0, Meta()));

        var row = vm.Pins.Single();
        // The "Unnamed pin" display fallback lives in the XAML DataTemplate
        // (FallbackValue/TargetNullValue); the MapPinEntry stores the raw label.
        row.Label.Should().Be("");
        row.X.Should().BeApproximately(-3.14, 0.001);
        row.Z.Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Dispose_stops_observing_pins()
    {
        var pins = new FakeMapPinState();
        using var vm = NewVm(out var bus, pins: pins);
        vm.Dispose();

        bus.Fire(new MapPinAdded(9, 9, "Late", 0, 0, Meta()));

        // After dispose, the VM's own pin-timestamp subscription must be silent.
        // (The presenter is a separate singleton in production — not co-disposed
        // with the VM — so vm.Pins / HasPins reflect the presenter's state; the
        // VM's own observable, PinsObservedAtText, must stay at its default.)
        vm.PinsObservedAtText.Should().Be("—",
            "the disposed VM must unsubscribe its own OnPinAdded handler");
    }

    // --- Moon phase ---------------------------------------------------------

    [Fact]
    public void No_celestial_info_shows_default()
    {
        using var vm = NewVm(out _);

        vm.HasMoonPhase.Should().BeFalse();
        vm.MoonPhaseText.Should().Contain("no celestial");
        vm.MoonMeasuredAtText.Should().Be("—");
    }

    [Fact]
    public void Celestial_event_surfaces_phase_raw_token_and_instant()
    {
        using var vm = NewVm(out var bus);

        bus.Fire(new CelestialInfoChanged(null, "WaxingCrescentMoon", MoonPhase.WaxingCrescent, "Waxing Crescent", Meta()));

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Waxing Crescent");
        vm.MoonPhaseRawText.Should().Be("WaxingCrescentMoon");
        vm.MoonMeasuredAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void Unrecognised_phase_token_is_flagged_but_still_shown()
    {
        using var vm = NewVm(out var bus);

        bus.Fire(new CelestialInfoChanged(null, "BloodMoonEclipse", MoonPhase.Unknown, "Blood Moon Eclipse", Meta()));

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Blood Moon Eclipse");
        vm.MoonPhaseRawText.Should().Be("BloodMoonEclipse (unrecognised token)");
    }

    [Fact]
    public void Seed_from_state_picks_up_existing_phase()
    {
        var celestial = new FakeCelestialState
        {
            CurrentPhaseRaw = "FullMoon", Phase = MoonPhase.FullMoon,
            DisplayName = "Full Moon", MeasuredAt = T,
        };
        using var vm = NewVm(out _, celestial: celestial);

        vm.HasMoonPhase.Should().BeTrue();
        vm.MoonPhaseText.Should().Be("Full Moon");
    }

    [Fact]
    public void Dispose_stops_observing_celestial()
    {
        using var vm = NewVm(out var bus);
        vm.Dispose();

        bus.Fire(new CelestialInfoChanged(null, "FullMoon", MoonPhase.FullMoon, "Full Moon", Meta()));

        vm.HasMoonPhase.Should().BeFalse("the disposed VM must unsubscribe from celestial");
    }

    // --- Weather ------------------------------------------------------------

    [Fact]
    public void Seed_from_state_picks_up_existing_weather()
    {
        var weather = new FakeWeatherState { CurrentWeather = "Foggy" };
        using var vm = NewVm(out _, weather: weather);

        vm.HasWeather.Should().BeTrue();
        vm.WeatherConditionText.Should().Be("Foggy");
    }

    [Fact]
    public void Changed_weather_populates_condition_and_observed_at()
    {
        using var vm = NewVm(out var bus);

        bus.Fire(new Arda.World.Player.Events.WeatherChanged(null, "Rainy", Meta()));

        vm.HasWeather.Should().BeTrue();
        vm.WeatherConditionText.Should().Be("Rainy");
        vm.WeatherObservedAtText.Should().Be("2026-05-18 10:45:47Z");
    }

    [Fact]
    public void Dispose_stops_observing_weather()
    {
        using var vm = NewVm(out var bus);
        vm.Dispose();

        bus.Fire(new Arda.World.Player.Events.WeatherChanged(null, "Foggy", Meta()));

        vm.HasWeather.Should().BeFalse("the disposed VM must unsubscribe from weather");
    }

    // --- Helpers ------------------------------------------------------------

    private static WorldStateViewModel NewVm(
        out FakeBus bus,
        IPositionState? position = null,
        IAreaState? area = null,
        IMapPinState? pins = null,
        ICelestialState? celestial = null,
        IWeatherState? weather = null,
        IReferenceDataService? refData = null)
    {
        bus = new FakeBus();
        var pinState = pins ?? new FakeMapPinState();
        var subscriber = new Arda.Wpf.SyncUiEventSubscriber(bus);
        var presenter = new Arda.Wpf.WpfMapPinPresenter(pinState, subscriber);

        return new WorldStateViewModel(
            position ?? new FakePositionState(),
            area ?? new FakeAreaState(),
            pinState,
            celestial ?? new FakeCelestialState(),
            weather ?? new FakeWeatherState(),
            subscriber,
            presenter,
            refData);
    }

    // --- Fakes --------------------------------------------------------------

    internal sealed class FakeBus : IDomainEventPublisher, IDomainEventSubscriber
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Publish<T>(T evt) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            foreach (var d in list.ToArray()) ((Action<T>)d).Invoke(evt);
        }

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
            return new Sub(() => list.Remove(handler));
        }

        /// <summary>Fire an event synchronously — test helper alias for Publish.</summary>
        public void Fire<T>(T evt) where T : struct => Publish(evt);

        private sealed class Sub(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
        }
    }

    internal sealed class FakePositionState : IPositionState
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
    }

    internal sealed class FakeAreaState : IAreaState
    {
        public string? CurrentArea { get; set; }
    }

    internal sealed class FakeMapPinState : IMapPinState
    {
        public IReadOnlyList<MapPinEntry> Pins { get; private set; }

        public FakeMapPinState(IReadOnlyCollection<MapPinEntry>? pins = null)
            => Pins = (pins as IReadOnlyList<MapPinEntry>) ?? [];

        public void Set(IReadOnlyCollection<MapPinEntry> pins) => Pins = (pins as IReadOnlyList<MapPinEntry>) ?? pins.ToList();
    }

    internal sealed class FakeCelestialState : ICelestialState
    {
        public string? CurrentPhaseRaw { get; set; }
        public MoonPhase Phase { get; set; }
        public string? DisplayName { get; set; }
        public DateTimeOffset? MeasuredAt { get; set; }
    }

    internal sealed class FakeWeatherState : IWeatherState
    {
        public string? CurrentWeather { get; set; }
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, AreaEntry> _areas;

        public FakeRefData(params (string Key, AreaEntry Entry)[] areas)
        {
            _areas = areas.ToDictionary(a => a.Key, a => a.Entry, StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;

        public IReadOnlyList<string> Keys { get; } = ["areas"];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Mithril.Shared.Reference.SkillEntry> Skills { get; } = new Dictionary<string, Mithril.Shared.Reference.SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
