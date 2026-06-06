using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Windows;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;
using Microsoft.Extensions.Hosting;
using Mithril.MapCalibration;
using Mithril.Shared.Diagnostics.Telemetry;

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
    private readonly ILiveMapViewService? _liveView;
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
        ILoggerFactory? loggerFactory = null,
        ILiveMapViewService? liveView = null)
    {
        _bus = bus;
        _areaCalibration = areaCalibration;
        _liveView = liveView;
        _flow = flow;
        _session = session;
        _motherlode = motherlode;
        _settings = settings;
        // #1093 D10: DI never registered the non-generic ILogger directly, so the
        // former optional `ILogger? logger = null` resolved to null in production
        // and the "Subscribed to Arda domain events" line below was silently dead.
        // ILoggerFactory IS registered, so resolving it and creating the named
        // category here lights the dead line up (verified by
        // PlayerLogIngestionServiceLoggingTests).
        _logger = loggerFactory?.CreateLogger("Legolas.Ingestion");
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
        var cleanName = CleanName(shortName);

        if (_session.Mode != SessionMode.Survey)
        {
            _session.LastLogEvent = $"Map target: {cleanName} @ ({world.X:0},{world.Z:0}) → ignored (mode is Motherlode)";
            _logger?.LogTrace(
                "HandleMapTarget {Name}@({X:0},{Z:0}): ignored, mode is {Mode}.",
                cleanName, world.X, world.Z, _session.Mode);
            return;
        }

        if (_flow.CurrentState is not (SurveyFlowState.Listening or SurveyFlowState.Gathering))
        {
            _session.LastLogEvent =
                $"Map target: {cleanName} @ ({world.X:0},{world.Z:0}) → ignored (survey flow is {_flow.CurrentState})";
            _logger?.LogTrace(
                "HandleMapTarget {Name}@({X:0},{Z:0}): ignored, flow is {Flow}.",
                cleanName, world.X, world.Z, _flow.CurrentState);
            return;
        }

        if (_areaCalibration.CurrentOverlayCalibration is not { } cal)
        {
            _session.LastLogEvent =
                $"Map target: {cleanName} @ ({world.X:0},{world.Z:0}) → area not calibrated; run pin calibration";
            var skippedArea = _areaCalibration?.CurrentScene?.MapAssetKey ?? "<unknown>";
            _logger?.LogInformation(
                "HandleMapTarget {Name}@({X:0},{Z:0}) area={Area}: dropped — area not calibrated.",
                cleanName, world.X, world.Z, skippedArea);
            MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                new KeyValuePair<string, object?>("consumer", "survey_pin"),
                new KeyValuePair<string, object?>("area", skippedArea));
            return;
        }

        // mithril#1095: layer-2 composition — resolve the live MapViewFix for this
        // area and apply it to produce live overlay pixels. If no fix is available
        // (ILiveMapViewService not injected, or no probe has completed yet), fall
        // back to canonical projection so tests and first-start paths still work.
        var area = _areaCalibration.CurrentScene?.MapAssetKey;
        var fix = !string.IsNullOrEmpty(area) ? _liveView?.GetCurrent(area) : null;
        OverlayPixel pixel;
        if (fix is { } f)
        {
            pixel = cal.ToLiveOverlay(world, f);
        }
        else
        {
            // No live-view fix yet (or no ILiveMapViewService injected) — use
            // canonical projection. This is the expected path in tests and on
            // first placement before any probe has completed.
            if (!string.IsNullOrEmpty(area))
            {
                _logger?.LogTrace(
                    "HandleMapTarget {Name}@({X:0},{Z:0}) area={Area}: no live-view fix, using canonical projection.",
                    cleanName, world.X, world.Z, area);
            }
            pixel = cal.ToOverlay(world);
        }

        if (FindDuplicateAbsolute(world, _settings.MapTargetDedupRadiusMetres) is { } dup)
        {
            dup.UpdateModel(dup.Model with { PixelPos = pixel, World = world });
            _session.LastLogEvent = $"Map target: {cleanName} → duplicate (X,Z), updated";
            _logger?.LogTrace(
                "HandleMapTarget {Name}: duplicate within {Radius}m, updating existing pin.",
                cleanName, _settings.MapTargetDedupRadiusMetres);
            return;
        }

        var index = _session.Surveys.Count;
        var pinVm = new SurveyItemViewModel(
            Survey.CreateAbsolute(cleanName, world, pixel, index));
        _session.Surveys.Add(pinVm);
        _session.SelectedSurvey = pinVm;
        _session.IsInventoryVisible = true;
        _session.LastLogEvent = $"Map target: {cleanName} → placed (absolute)";

        // WorldToOverlayCalibration doesn't carry Source / ResidualPixels — they
        // live on the full AreaCalibration record (Task 3 finding). Fall back to
        // sentinels when CurrentCalibration is null (shouldn't happen on the
        // success branch but cheap to guard).
        var source = _areaCalibration.CurrentCalibration?.Source.ToString() ?? "<unknown>";
        var residual = _areaCalibration.CurrentCalibration?.ResidualPixels ?? double.NaN;
        _logger?.LogInformation(
            "HandleMapTarget {Name}@({X:0},{Z:0}): placed at overlay ({Px:0},{Py:0}) (cal source={Source}, residual={Residual:0.00}px).",
            cleanName, world.X, world.Z, pixel.X, pixel.Y, source, residual);
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
