using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Background auto-attempt trigger (spec §10). Subscribes to Arda's
/// <see cref="AreaChanged"/> and <see cref="MapAssetChanged"/> and, on a
/// zone-in or per-scene transition, fires one auto-calibration attempt
/// <b>iff</b>:
/// <list type="bullet">
/// <item>a map capture bbox has been framed (<see cref="IMapCaptureRegionProvider.Current"/> != null), AND</item>
/// <item>the game is the foreground window (<see cref="IGameWindowLocator.Locate"/> != null), AND</item>
/// <item>the scene is uncalibrated OR its active calibration is only a
/// <see cref="CalibrationSource.BundledBaseline"/> (an upgradeable fallback).</item>
/// </list>
///
/// <para><b>Never overwrites an existing <see cref="CalibrationSource.UserRefinement"/>
/// or <see cref="CalibrationSource.AutoCapture"/> on the auto path</b> — a
/// converged transform isn't re-attempted on every zone-in. (The manual
/// capture-&amp;-calibrate hotkey always attempts, by design.)</para>
///
/// <para><b>Per-scene keying (mithril#1041).</b> The skip-already-persisted set
/// is keyed on <see cref="MapSceneRef.MapAssetKey"/>, not on the parent area
/// key. This means sub-zone transitions within an aggregator area
/// (e.g. Hogan's Basement → Goblin Dungeon under <c>AreaCave1</c>) each get
/// their own attempt — the prior approach keyed on <see cref="AreaChanged"/>
/// fired only on cross-area changes and missed sub-zone transitions entirely.</para>
///
/// <para><b>Retry-on-re-entry (GATE-2 Fix C).</b> A scene is marked "done"
/// (suppressing re-attempt) ONLY when the attempt persisted a transform. A
/// non-persisted outcome (e.g. "no bbox", "not zoomed out") leaves the scene
/// un-marked, so a genuine later re-entry — the user zones out, zooms the map
/// properly, zones back — gets a fresh attempt. An in-flight guard prevents a
/// burst of scene-changed events from launching concurrent/looping
/// attempts; there is no timer/polling loop, retries happen only on fresh
/// scene-change events.</para>
///
/// <para>On a non-persisted, <i>actionable</i> reject the trigger surfaces the
/// reason on the overlay status chip (spec §10/§11) so the user learns why
/// auto-cal isn't engaging; a persisted success clears the chip silently.</para>
/// </summary>
public sealed class AutoCalibrationTrigger : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IAutoCalibrationRunner _runner;
    private readonly IMapCaptureRegionProvider _region;
    private readonly IGameWindowLocator _windowLocator;
    private readonly IMapCalibrationService _calibrationService;
    private readonly IMapState _mapState;
    private readonly ISceneAssetCache _sceneCache;
    private readonly IOverlayWindow _overlay;
    private readonly ILogger _logger;

    private IDisposable? _areaChangedSub;
    private IDisposable? _mapAssetChangedSub;
    private readonly object _gate = new();
    // Scenes (keyed on MapAssetKey) whose auto-attempt PERSISTED — never re-attempted (Fix C).
    private readonly HashSet<string> _persistedScenes = new(StringComparer.Ordinal);
    // Scenes (keyed on MapAssetKey) with an attempt currently running — skip duplicate concurrent launches
    // (the in-flight guard against a retry storm; Fix C).
    private readonly HashSet<string> _inFlightScenes = new(StringComparer.Ordinal);

    public AutoCalibrationTrigger(
        IDomainEventSubscriber bus,
        IAutoCalibrationRunner runner,
        IMapCaptureRegionProvider region,
        IGameWindowLocator windowLocator,
        IMapCalibrationService calibrationService,
        IMapState mapState,
        ISceneAssetCache sceneCache,
        IOverlayWindow overlay,
        ILogger logger)
    {
        _bus = bus;
        _runner = runner;
        _region = region;
        _windowLocator = windowLocator;
        _calibrationService = calibrationService;
        _mapState = mapState;
        _sceneCache = sceneCache;
        _overlay = overlay;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _areaChangedSub = _bus.Subscribe<AreaChanged>(OnAreaChanged);
        _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _areaChangedSub?.Dispose();
        _areaChangedSub = null;
        _mapAssetChangedSub?.Dispose();
        _mapAssetChangedSub = null;
        return Task.CompletedTask;
    }

    private void OnAreaChanged(AreaChanged e)
    {
        // Resolve the scene via the cascade — when the user zones into a directly-
        // registered area, the cache supplies the seeded MapSceneRef immediately
        // (no need to wait for the Downloading Map event). For aggregator areas
        // (AreaCave1 etc.), the cache miss falls through and OnMapAssetChanged
        // handles the per-scene attempt.
        var resolved = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        if (resolved is not { } scene) return;
        // Fire-and-forget on the thread pool — the bus delivers synchronously on
        // the ingest thread, which must not block on a capture+solve.
        _ = Task.Run(() => OnSceneChangedAsync(scene));
    }

    private void OnMapAssetChanged(MapAssetChanged e)
    {
        if (e.CurrentScene is not { } scene) return;
        _ = Task.Run(() => OnSceneChangedAsync(scene));
    }

    /// <summary>
    /// The gating decision, extracted for unit testing. Returns when the attempt
    /// completes (or is skipped). Awaited by the fire-and-forget path.
    /// </summary>
    internal async Task OnSceneChangedAsync(MapSceneRef scene)
    {
        var key = scene.MapAssetKey;
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (_gate)
        {
            // Already persisted for this scene → never re-attempt (Fix C).
            if (_persistedScenes.Contains(key)) return;
            // An attempt is already running for this scene → skip the duplicate so
            // a burst of scene-changed events can't launch a concurrent/looping
            // attempt (in-flight guard, Fix C).
            if (!_inFlightScenes.Add(key)) return;
        }

        try
        {
            if (_region.Current is null) return;                 // no bbox → can't capture
            if (_windowLocator.Locate() is null) return;         // PG not foreground

            // Skip if the store has any UserRefinement or AutoCapture record for this
            // scene. Decoupled from GetCalibration's picker: the picker may return a
            // BundledBaseline when its residual+ref-count beats a stored AutoCapture,
            // but the trigger's promise is "one cold solve per scene per install"
            // (mithril#1046 §7).
            var sources = _calibrationService.GetAllSources(scene);
            var converged = sources.FirstOrDefault(s => s.Source is CalibrationSource.UserRefinement or CalibrationSource.AutoCapture);
            if (converged is not null)
            {
                _logger.LogInformation(
                    "Auto-trigger skipped for {MapAssetKey}: store has {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected.",
                    key, converged.Source, converged.ResidualPixels, converged.ReferenceCount);

                // Picker/store-disagreement telemetry — informational, surfaces how
                // often the picker prefers a baseline over a stored auto.
                //
                // Second store lookup is intentional: GetAllSources returns the persisted
                // list (what the trigger gates on); GetCalibration runs the picker (what
                // the runtime renderer sees). Comparing the two surfaces the divergence.
                var picked = _calibrationService.GetCalibration(scene);
                if (picked is not null && picked.Source != converged.Source)
                {
                    _logger.LogInformation(
                        "Auto-trigger skipped for {MapAssetKey}: store has converged solve (source={StoredSource}) but picker returned {PickedSource}. Picker chose better-quality record; trigger respects store.",
                        key, converged.Source, picked.Source);
                }
                return;
            }

            _logger.LogInformation(
                "Auto-trigger firing for {MapAssetKey}: no converged solve in store; attempting cold solve (existing source: {Source}).",
                key, sources.FirstOrDefault()?.Source.ToString() ?? "<none>");
            AutoCalibrationOutcome? outcome = null;
            try
            {
                outcome = await _runner.TryCalibrateCurrentAreaAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-calibration attempt for {AssetKey} threw; the scene stays as-is.", key);
            }

            if (outcome is null) return; // threw → leave un-marked so a later re-entry retries

            if (outcome.Persisted)
            {
                lock (_gate) { _persistedScenes.Add(key); }
                // Silent upgrade (spec §10): a successful auto-persist clears any
                // prior status chip. Idempotent on the concrete overlay.
                _overlay.SetStatusMessage(null);
            }
            else
            {
                // Non-persisted → leave the scene un-marked so a genuine later
                // re-entry retries (Fix C). Surface the actionable reason so the
                // user learns why auto-cal isn't engaging (spec §10/§11). Setting
                // the same string is idempotent (the concrete overlay no-ops it).
                _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            }
        }
        finally
        {
            lock (_gate) { _inFlightScenes.Remove(key); }
        }
    }

    public void Dispose()
    {
        _areaChangedSub?.Dispose();
        _mapAssetChangedSub?.Dispose();
    }
}
