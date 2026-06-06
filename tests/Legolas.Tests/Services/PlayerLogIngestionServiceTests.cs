using FluentAssertions;
using Arda.Abstractions.Logs;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// Post-Arda migration: the service subscribes to structured domain events via
/// <see cref="Arda.Contracts.IDomainEventSubscriber"/> instead of the former L1
/// driver. mithril#1041 (per-scene calibration keying) replaces the
/// <c>AreaChanged</c> bridge with <see cref="MapAssetChanged"/> so the
/// calibration handoff is keyed on the Unity asset (one transform per scene),
/// not the parent areas.json key (which aggregates sub-scenes). The service no
/// longer takes an <c>IAreaState</c>; the cold-start seed flows through the
/// bus replay of the first <c>Downloading Map</c> line.
/// </summary>
public sealed class PlayerLogIngestionServiceTests : IDisposable
{
    private static AreaCalibration Identity() =>
        new(1.0, 0.0, 0.0, 0.0, 2, 0.0);

    private static readonly LogLineMetadata LiveMeta = new(
        Timestamp: new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: false);

    private static readonly LogLineMetadata ReplayMeta = new(
        Timestamp: new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
        ReadOn: DateTimeOffset.UtcNow,
        IsReplay: true);

    private static MapSceneRef SceneFor(string areaKey) =>
        new(ParentAreaKey: areaKey, SceneFriendlyName: null, MapAssetKey: "Map_" + areaKey);

    private sealed record Fixture(
        PlayerLogIngestionService Service,
        TestDomainEventBus Bus,
        SpyAreaCalibration Spy,
        SessionState Session,
        SurveyFlowController Flow);

    private static Fixture Build(AreaCalibration? calibration = null)
    {
        var bus = new TestDomainEventBus();
        var spy = new SpyAreaCalibration(calibration);
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(session), bus);
        // mithril#1041: IAreaState dropped from the ctor — calibration handoff
        // is now driven exclusively by MapAssetChanged on the bus.
        var svc = new PlayerLogIngestionService(
            bus, spy, flow, session, motherlode, settings);
        return new Fixture(svc, bus, spy, session, flow);
    }

    // ---- area→calibration bridge -----------------------------------------

    [Fact]
    public async Task Map_asset_change_applies_calibration()
    {
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapAssetChanged(SceneFor("AreaSerbule"), SceneFor("AreaEltibule"), LiveMeta));
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Same_asset_re_emit_does_not_duplicate()
    {
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapAssetChanged(null, SceneFor("AreaEltibule"), LiveMeta));
            f.Bus.Publish(new MapAssetChanged(null, SceneFor("AreaEltibule"), LiveMeta));
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Startup_replay_applies_current_scene()
    {
        // mithril#1041: there is no startup-time IAreaState seed; the cold-start
        // calibration handoff arrives as the first MapAssetChanged event Arda
        // replays through the bus on first boot. Publishing the event after
        // StartAsync mirrors that replay shape.
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapAssetChanged(null, SceneFor("AreaEltibule"), LiveMeta));
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(f, cts); }
    }

    // ---- MapFxObserved placement -----------------------------------------

    [Fact]
    public async Task MapFx_places_absolute_pin()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "The Good Metal Slab is 67m west and 1181m south.".AsMemory(),
                LiveMeta));

            f.Session.Surveys.Should().HaveCount(1);
            var pin = f.Session.Surveys[0];
            pin.Name.Should().Be("Good Metal Slab");
            pin.Model.World.Should().Be(new WorldCoord(1236.00, 38.17, 2528.00));
            f.Session.SelectedSurvey.Should().BeSameAs(pin);
            f.Session.IsInventoryVisible.Should().BeTrue();
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task MapFx_feeds_NoteSurvey_with_relative_offset()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "The Good Metal Slab is 67m west and 1181m south.".AsMemory(),
                LiveMeta));

            f.Spy.NotedSurveys.Should().ContainSingle();
            var (name, offset) = f.Spy.NotedSurveys[0];
            name.Should().Be("Good Metal Slab");
            offset.East.Should().Be(-67);
            offset.North.Should().Be(-1181);
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task NoteSurvey_fires_even_when_area_uncalibrated()
    {
        var f = Build(calibration: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "The Good Metal Slab is 67m west and 1181m south.".AsMemory(),
                LiveMeta));

            f.Spy.NotedSurveys.Should().ContainSingle();
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Replay_events_are_dropped()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                ReplayMeta));

            f.Session.Surveys.Should().BeEmpty("replay events are dropped");
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Uncalibrated_area_does_not_place()
    {
        var f = Build(calibration: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                LiveMeta));

            f.Session.Surveys.Should().BeEmpty();
            f.Session.LastLogEvent.Should().Contain("not calibrated");
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Motherlode_mode_ignores_targets()
    {
        var f = Build(calibration: Identity());
        f.Session.Mode = SessionMode.Motherlode;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                LiveMeta));

            f.Session.Surveys.Should().BeEmpty();
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Non_accepting_flow_state_does_not_place()
    {
        var f = Build(calibration: Identity());
        f.Flow.RequestSetPosition();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                LiveMeta));

            f.Session.Surveys.Should().BeEmpty();
            f.Session.LastLogEvent.Should().Contain("SettingPosition");
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Duplicate_world_coord_does_not_stack()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                LiveMeta));
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(),
                "msg".AsMemory(),
                LiveMeta));

            f.Session.Surveys.Should().HaveCount(1);
        }
        finally { await Stop(f, cts); }
    }

    [Fact]
    public async Task Distinct_targets_place_separate_pins()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Service.StartAsync(cts.Token);
        try
        {
            f.Bus.Publish(new MapFxObserved(
                1236.00, 38.17, 2528.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(), "msg".AsMemory(), LiveMeta));
            f.Bus.Publish(new MapFxObserved(
                1666.00, 36.95, 2620.00,
                "Good Metal Slab is here".AsMemory(),
                "ImportantInfo".AsMemory(), "msg".AsMemory(), LiveMeta));

            f.Session.Surveys.Should().HaveCount(2);
        }
        finally { await Stop(f, cts); }
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task Stop(Fixture f, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try { await f.Service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        f.Service.Dispose();
    }

    private sealed class SpyAreaCalibration : IAreaCalibrationService
    {
        private readonly AreaCalibration? _cal;
        public SpyAreaCalibration(AreaCalibration? cal) => _cal = cal;

        /// <summary>Record the <see cref="MapSceneRef.ParentAreaKey"/> of every
        /// <see cref="SelectScene"/> call so existing assertions on parent-area
        /// strings (the pre-#1041 surface) keep working. Tests that need the full
        /// composite read <see cref="SelectedScenes"/> directly.</summary>
        public List<string> SelectedAreas { get; } = new();
        public List<MapSceneRef> SelectedScenes { get; } = new();

        public void SelectScene(MapSceneRef scene)
        {
            SelectedScenes.Add(scene);
            SelectedAreas.Add(scene.ParentAreaKey);
        }

        public AreaCalibration? CurrentCalibration => _cal;
        public WorldToOverlayCalibration? CurrentOverlayCalibration => _cal is { } c
            ? new WorldToOverlayCalibration(c.OriginX, c.OriginY, c.Scale, c.RotationRadians, c.MirrorNorth)
            : null;
        public bool IsCurrentAreaCalibrated => _cal is not null;

        public MapSceneRef? CurrentScene => null;
        public string? CurrentAreaFriendlyName => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences =>
            Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
            double calibrationZoom = 1.0) => null;
        public void ClearCurrentAreaCalibration() { }
        public List<(string Name, MetreOffset Offset)> NotedSurveys { get; } = new();
        public void NoteSurvey(string name, MetreOffset offset) => NotedSurveys.Add((name, offset));
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    public void Dispose() { }
}
