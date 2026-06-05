using Microsoft.Extensions.Logging;
using System.Windows;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;
using Mithril.MapCalibration;

namespace Legolas.Services;

/// <summary>
/// Legolas-owned Arda domain event consumer. Subscribes to structured events
/// via <see cref="IDomainEventSubscriber"/> replacing the former L1 driver +
/// <see cref="PlayerLogParser"/> + <c>IPlayerAreaState</c> subscription.
///
/// <para><b>Responsibilities.</b>
/// <list type="bullet">
///   <item><b>Scene→calibration bridge.</b> Subscribes to
///   <see cref="MapAssetChanged"/> (mithril#1041 — was <see cref="AreaChanged"/>;
///   per-scene granularity is strictly-more-informative for aggregator areas
///   because the same log line carries the parent area name and the per-scene
///   asset key) and, whenever the player's scene changes, applies that scene's
///   persisted <see cref="Domain.AreaCalibration"/> via
///   <see cref="IAreaCalibrationService.SelectScene"/>.</item>
///   <item><b><see cref="MapFxObserved"/> placement.</b> Absolute
///   survey/treasure targets place a pin at the projected pixel; the
///   trailing relative-offset readout drives the calibration verify-mode
///   <see cref="IAreaCalibrationService.NoteSurvey"/> hook.</item>
///   <item><b><see cref="DelayLoopStarted"/> Motherlode-map use gesture.</b>
///   Forwarded to the <see cref="MotherlodeMeasurementCoordinator"/>.</item>
///   <item><b><see cref="ScreenTextObserved"/> motherlode distance
///   readout.</b> Same single-source coordinator.</item>
/// </list></para>
///
/// <para><b>Replay gating.</b> Arda events carry
/// <see cref="Arda.Abstractions.Logs.LogLineMetadata.IsReplay"/> which replaces
/// the former <c>LiveOnly</c> + high-water sequence mechanism. Events during
/// replay are dropped; only live events reach the handlers.</para>
///
/// <para><b>Threading.</b> The Arda bus fires synchronously on the driver
/// thread. All state mutations marshal to the UI thread via the WPF
/// dispatcher so overlay-bound <c>SessionState</c> mutations stay
/// single-threaded.</para>
/// </summary>
public sealed class PlayerLogIngestionService : BackgroundService
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly SurveyFlowController _flow;
    private readonly SessionState _session;
    private readonly MotherlodeMeasurementCoordinator _motherlode;
    private readonly LegolasSettings _settings;
    private readonly ILogger? _logger;

    private IDisposable? _mapFxSub;
    private IDisposable? _delayLoopSub;
    private IDisposable? _screenTextSub;
    private IDisposable? _mapAssetChangedSub;

    // The previous scene seen — used for dedup so we don't re-apply the
    // same scene calibration on repeat events. Compared by MapAssetKey
    // since that's the calibration-store key.
    private MapSceneRef? _lastScene;

    public PlayerLogIngestionService(
        IDomainEventSubscriber bus,
        IAreaCalibrationService areaCalibration,
        SurveyFlowController flow,
        SessionState session,
        MotherlodeMeasurementCoordinator motherlode,
        LegolasSettings settings,
        ILogger? logger = null)
    {
        _bus = bus;
        _areaCalibration = areaCalibration;
        _flow = flow;
        _session = session;
        _motherlode = motherlode;
        _settings = settings;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // No initial-state seed — IMapState.CurrentMapScene is null until the
        // first Downloading Map line, which Arda replays through the bus on
        // first boot. The SceneAssetCache's recorder populates the cache during
        // replay; the resolution helper handles cold-start downstream.

        _mapFxSub = _bus.Subscribe<MapFxObserved>(OnMapFxObserved);
        _delayLoopSub = _bus.Subscribe<DelayLoopStarted>(OnDelayLoopStarted);
        _screenTextSub = _bus.Subscribe<ScreenTextObserved>(OnScreenTextObserved);
        _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);

        _logger?.LogInformation("Subscribed to Arda domain events");

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    public override void Dispose()
    {
        _mapFxSub?.Dispose();
        _delayLoopSub?.Dispose();
        _screenTextSub?.Dispose();
        _mapAssetChangedSub?.Dispose();
        base.Dispose();
    }

    private void OnMapAssetChanged(MapAssetChanged evt)
    {
        if (evt.CurrentScene is not { } scene)
        {
            _lastScene = null;
            return;
        }
        // Dedup on MapAssetKey — the calibration-store key. Different parent
        // areas + same asset shouldn't happen in practice, but if they do, the
        // asset key is the load-bearing axis.
        if (_lastScene is { } prev &&
            string.Equals(prev.MapAssetKey, scene.MapAssetKey, StringComparison.Ordinal)) return;
        _lastScene = scene;
        _areaCalibration.SelectScene(scene);
    }

    private void OnMapFxObserved(MapFxObserved evt)
    {
        if (evt.Metadata.IsReplay) return;

        var shortName = evt.ShortName.ToString();
        var message = evt.Message.ToString();
        var world = new WorldCoord(evt.X, evt.Y, evt.Z);

        MarshalToUi(() =>
        {
            if (PlayerLogParser.TryParseMapFxRelativeOffset(message) is { } offset)
                _areaCalibration.NoteSurvey(CleanName(shortName), offset);

            HandleMapTarget(world, shortName, message);
        });
    }

    private void OnDelayLoopStarted(DelayLoopStarted evt)
    {
        if (evt.Metadata.IsReplay) return;
        if (_session.Mode != SessionMode.Motherlode) return;
        if (!PlayerLogParser.IsMotherlodeMapText(evt.Text.Span)) return;

        var mapName = PlayerLogParser.NormalizeMapName(evt.Text.ToString());
        var at = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;

        MarshalToUi(() => _motherlode.OnUse(at, mapName));
    }

    private void OnScreenTextObserved(ScreenTextObserved evt)
    {
        if (evt.Metadata.IsReplay) return;
        if (_session.Mode != SessionMode.Motherlode) return;
        if (!evt.Category.Span.SequenceEqual("ImportantInfo".AsSpan())) return;

        var text = evt.Text.ToString();
        if (PlayerLogParser.TryParseMotherlodeDistance(text) is not { } metres) return;

        var at = evt.Metadata.Timestamp ?? evt.Metadata.ReadOn;
        MarshalToUi(() => _motherlode.OnDistance(metres, at));
    }

    private void HandleMapTarget(WorldCoord world, string shortName, string message)
    {
        if (_session.Mode != SessionMode.Survey)
        {
            _session.LastLogEvent = $"Map target: {CleanName(shortName)} @ ({world.X:0},{world.Z:0}) → ignored (mode is Motherlode)";
            return;
        }

        if (_flow.CurrentState is not (SurveyFlowState.Listening or SurveyFlowState.Gathering))
        {
            _session.LastLogEvent =
                $"Map target: {CleanName(shortName)} @ ({world.X:0},{world.Z:0}) → ignored (survey flow is {_flow.CurrentState})";
            return;
        }

        if (_areaCalibration.CurrentOverlayCalibration is not { } cal)
        {
            _session.LastLogEvent =
                $"Map target: {CleanName(shortName)} @ ({world.X:0},{world.Z:0}) → area not calibrated; run pin calibration";
            return;
        }

        var name = CleanName(shortName);
        // #1076 Phase 6.5: frame-typed projection — OverlayPixel out, no re-tag.
        var pixel = cal.ToOverlay(world, _session.CurrentMapZoom);

        if (FindDuplicateAbsolute(world, _settings.MapTargetDedupRadiusMetres) is { } dup)
        {
            dup.UpdateModel(dup.Model with { PixelPos = pixel, World = world });
            _session.LastLogEvent = $"Map target: {name} → duplicate (X,Z), updated";
            return;
        }

        var index = _session.Surveys.Count;
        var pinVm = new SurveyItemViewModel(
            Survey.CreateAbsolute(name, world, pixel, index));
        _session.Surveys.Add(pinVm);
        _session.SelectedSurvey = pinVm;
        _session.IsInventoryVisible = true;
        _session.LastLogEvent = $"Map target: {name} → placed (absolute)";
    }

    private SurveyItemViewModel? FindDuplicateAbsolute(WorldCoord world, double radiusMetres)
    {
        var r2 = radiusMetres * radiusMetres;
        foreach (var s in _session.Surveys)
        {
            if (s.Collected) continue;
            if (s.Model.World is not { } w) continue;
            var dx = w.X - world.X;
            var dz = w.Z - world.Z;
            if (dx * dx + dz * dz <= r2) return s;
        }
        return null;
    }

    private static string CleanName(string shortText)
    {
        const string suffix = " is here";
        var t = shortText.Trim();
        return t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? t[..^suffix.Length].Trim()
            : t;
    }

    private static void MarshalToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(action);
        else
            action();
    }
}
