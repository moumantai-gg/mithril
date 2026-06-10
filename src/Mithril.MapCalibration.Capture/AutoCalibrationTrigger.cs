using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arda.Contracts;
using Arda.Hosting;
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
///
/// <para><b>Replay gate (mithril#1117).</b> Subscription to
/// <see cref="AreaChanged"/> + <see cref="MapAssetChanged"/> is deferred until
/// <see cref="IReplayProgress.ReplayComplete"/> resolves. During Player.log
/// replay the handler re-emits past scene transitions; firing a capture+solve
/// against those would screenshot the CURRENT scene and locate it against an
/// UNRELATED historical scene's bundled texture, contaminating the auto-cal
/// store with rejected attempts for scenes the user never visited this session.
/// The gate matches the documented module-activation pattern on
/// <see cref="IReplayProgress"/>; the live tail's first scene-change event
/// fires the right attempt.</para>
/// </summary>
public sealed class AutoCalibrationTrigger : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly IReplayProgress _replayProgress;
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
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _deferredSubscribeTask;
    private readonly object _gate = new();
    // Scenes (keyed on MapAssetKey) whose auto-attempt PERSISTED — never re-attempted (Fix C).
    private readonly HashSet<string> _persistedScenes = new(StringComparer.Ordinal);
    // Scenes (keyed on MapAssetKey) with an attempt currently running — skip duplicate concurrent launches
    // (the in-flight guard against a retry storm; Fix C).
    private readonly HashSet<string> _inFlightScenes = new(StringComparer.Ordinal);

    public AutoCalibrationTrigger(
        IDomainEventSubscriber bus,
        IReplayProgress replayProgress,
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
        _replayProgress = replayProgress;
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
        // Fast-path: replay already complete (headless tests, second-instance
        // takeover, or a tail-only restart). Subscribe synchronously so the
        // first live event fires immediately without a thread-pool hop.
        if (_replayProgress.ReplayComplete.IsCompleted)
        {
            SubscribeNow();
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Auto-calibration trigger deferred until Player/Chat replay completes (mithril#1117 gate).");
        _deferredSubscribeTask = Task.Run(async () =>
        {
            try
            {
                await _replayProgress.ReplayComplete.WaitAsync(_stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // StopAsync fired before replay finished — leave the bus
                // un-subscribed so the trigger stays inert.
                return;
            }

            if (_stopCts.IsCancellationRequested) return;
            SubscribeNow();
        });

        return Task.CompletedTask;
    }

    private void SubscribeNow()
    {
        _areaChangedSub = _bus.Subscribe<AreaChanged>(OnAreaChanged);
        _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
        _logger.LogInformation(
            "Auto-calibration trigger subscribed to AreaChanged + MapAssetChanged (replay complete).");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel the deferred-subscribe awaiter so a Stop before ReplayComplete
        // leaves the bus untouched (the field stays null, dispose is a no-op).
        _stopCts.Cancel();
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

            // Skip if the store has a converged texture-frame record (AutoCapture or
            // BundledBaseline) for this scene. Overlay-frame records (Legolas-wizard) do
            // not satisfy the trigger's goal of landing a texture-frame AutoCal record —
            // the trigger and the wizard write into different SceneRefinements slots,
            // and both can coexist on the same scene (mithril#1082).
            var sources = _calibrationService.GetAllSources(scene);
            var convergedTexture = sources.FirstOrDefault(s =>
                s.Frame == CalibrationFrame.Texture &&
                s.Source is CalibrationSource.AutoCapture or CalibrationSource.BundledBaseline);
            if (convergedTexture is not null)
            {
                _logger.LogInformation(
                    "Auto-trigger skipped for {MapAssetKey}: store has converged texture-frame {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected.",
                    key, convergedTexture.Source, convergedTexture.ResidualPixels, convergedTexture.ReferenceCount);

                // Picker/store-disagreement telemetry — informational, surfaces how
                // often the picker prefers a baseline over a stored auto.
                //
                // Second store lookup is intentional: GetAllSources returns the persisted
                // list (what the trigger gates on); GetCalibration runs the picker (what
                // the runtime renderer sees). Comparing the two surfaces the divergence.
                var picked = _calibrationService.GetCalibration(scene);
                if (picked is not null && picked.Source != convergedTexture.Source)
                {
                    _logger.LogInformation(
                        "Auto-trigger skipped for {MapAssetKey}: store has converged texture-frame solve (source={StoredSource}) but picker returned {PickedSource}. Picker may have crossed frames or chose a different-quality record; trigger respects texture-frame store record.",
                        key, convergedTexture.Source, picked.Source);
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

    private int _disposed;
    public void Dispose()
    {
        // Idempotent: the DI container can invoke Dispose more than once for an
        // instance registered as both a singleton and as IHostedService.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stopCts.Cancel(); } catch (ObjectDisposedException) { /* StopAsync raced */ }
        _areaChangedSub?.Dispose();
        _mapAssetChangedSub?.Dispose();
        _stopCts.Dispose();
    }
}
