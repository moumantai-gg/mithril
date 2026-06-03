using System;
using System.Threading;
using System.Threading.Tasks;
using Arda.World.Player;
using Microsoft.Extensions.Logging;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Owns the manual calibrate hotkey's state machine (mithril#1046 §6.4). On
/// each press: either re-press-armed → run the full solve, or run a drift
/// check; route the outcome through <see cref="CalibrationStatusFormatter"/>
/// to the overlay status chip. Arming is in-process only — a restart disarms.
/// </summary>
public sealed class ManualCalibrationCoordinator
{
    public const int ArmingSeconds = 10;
    private static readonly TimeSpan ArmingWindow = TimeSpan.FromSeconds(ArmingSeconds);

    private readonly IAutoCalibrationRunner _runner;
    private readonly IMapCalibrationService _calibrationService;
    private readonly IMapState _mapState;
    private readonly ISceneAssetCache _sceneCache;
    private readonly IOverlayWindow _overlay;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private DateTimeOffset? _armedUntil;

    public ManualCalibrationCoordinator(
        IAutoCalibrationRunner runner,
        IMapCalibrationService calibrationService,
        IMapState mapState,
        ISceneAssetCache sceneCache,
        IOverlayWindow overlay,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        _runner = runner;
        _calibrationService = calibrationService;
        _mapState = mapState;
        _sceneCache = sceneCache;
        _overlay = overlay;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// True while the arming timer is set and has not yet expired — the next
    /// hotkey press will run the full solve rather than a drift check.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
                return _armedUntil is { } until && _time.GetUtcNow() < until;
        }
    }

    public async Task HandleHotkeyAsync(CancellationToken ct)
    {
        var scene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
        var (armed, expiredArmed) = ConsumeArmingState();

        var storedSourceForLog = scene is { } s
            ? _calibrationService.GetCalibration(s)?.Source
            : null;
        _logger?.LogInformation(
            "Manual calibrate hotkey: scene={MapAssetKey}, armed={IsArmed}, storedSource={Source}.",
            scene?.MapAssetKey ?? "<none>", armed, storedSourceForLog?.ToString() ?? "<none>");

        if (expiredArmed)
        {
            _logger?.LogInformation(
                "Manual calibrate hotkey: drift arming window expired ({Arm}s).",
                ArmingSeconds);
        }

        if (armed)
        {
            _logger?.LogInformation("Manual calibrate hotkey: armed re-press confirmed; running full solve.");
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(outcome.Persisted
                ? CalibrationStatusFormatter.RecalibratedSuccessfully()
                : CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        if (scene is null)
        {
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        var stored = _calibrationService.GetCalibration(scene.Value);
        if (stored is null)
        {
            var outcome = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
            _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
            return;
        }

        var drift = await _runner.CheckDriftAsync(ct).ConfigureAwait(false);
        switch (drift)
        {
            case DriftCheckOutcome.Ok:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckOk());
                break;
            case DriftCheckOutcome.Inconclusive inc:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckInconclusive(inc.Reason));
                break;
            case DriftCheckOutcome.Drift d:
                lock (_gate) _armedUntil = _time.GetUtcNow() + ArmingWindow;
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftDetected(d.MaxResidualPx, ArmingSeconds));
                break;
            case DriftCheckOutcome.CaptureFailed cf:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckCaptureFailed(cf.Reason));
                break;
            case DriftCheckOutcome.MapNotLocated mnl:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckCaptureFailed(mnl.Reason));
                break;
            case DriftCheckOutcome.NoIconDetections:
                _overlay.SetStatusMessage(CalibrationStatusFormatter.DriftCheckInconclusive("no icons detected in captured frame"));
                break;
            case DriftCheckOutcome.NoStoredCalibration:
                // Race: stored existed at our pre-check but engine saw null. Fall through to solve.
                var fallback = await _runner.TryCalibrateCurrentAreaAsync(ct).ConfigureAwait(false);
                _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(fallback));
                break;
        }
    }

    /// <summary>
    /// Atomically check arming state and consume it: returns (armed=true) when
    /// the timer was set and not expired (clearing the timer); returns
    /// (expiredArmed=true) when the timer was set but expired (also clearing);
    /// (false, false) otherwise.
    /// </summary>
    private (bool armed, bool expiredArmed) ConsumeArmingState()
    {
        lock (_gate)
        {
            if (_armedUntil is not { } until) return (false, false);
            var now = _time.GetUtcNow();
            _armedUntil = null;
            return now < until ? (true, false) : (false, true);
        }
    }
}
