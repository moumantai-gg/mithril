using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

// Note: LiveMapViewService lives in Mithril.MapCalibration.Detection (not
// Mithril.MapCalibration) because it depends on IOverlayCaptureSource which
// also lives here. Placing it in Mithril.MapCalibration would create a
// circular reference (Mithril.Overlay → Detection → MapCalibration).
// The namespace Mithril.MapCalibration.Internal is preserved so the contract
// surface matches the P1.7 spec. (#1095)

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Per-area <see cref="MapViewFix"/> holder + refresh orchestrator. See
/// <see cref="ILiveMapViewService"/> for the contract.
/// </summary>
public sealed class LiveMapViewService : ILiveMapViewService
{
    private readonly IMapViewProbe _probe;
    private readonly IOverlayCaptureSource _capture;
    private readonly IBaseTextureProvider _textures;
    private readonly Action<Action> _uiSynchronizer;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, MapViewFix> _fixes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveMapViewStatus> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);

    public event Action<string>? Changed;

    public LiveMapViewService(
        IMapViewProbe probe,
        IOverlayCaptureSource capture,
        IBaseTextureProvider textures,
        Action<Action> uiSynchronizer,
        ILogger<LiveMapViewService>? logger = null)
    {
        _probe = probe;
        _capture = capture;
        _textures = textures;
        _uiSynchronizer = uiSynchronizer;
        _logger = logger;
    }

    public MapViewFix? GetCurrent(string mapAssetKey)
        => _fixes.TryGetValue(mapAssetKey, out var f) ? f : null;

    public LiveMapViewStatus GetStatus(string mapAssetKey)
        => _status.TryGetValue(mapAssetKey, out var s) ? s : LiveMapViewStatus.NeverMeasured;

    public Task RefreshAsync(string mapAssetKey, CancellationToken ct = default)
    {
        var task = _inflight.GetOrAdd(mapAssetKey, key =>
        {
            _logger?.LogTrace("RefreshAsync({Area}): kicking off probe.", key);
            return RunProbe(key, ct);
        });
        return task.ContinueWith(
            _ => _inflight.TryRemove(new KeyValuePair<string, Task>(mapAssetKey, task)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task RunProbe(string mapAssetKey, CancellationToken ct)
    {
        _status[mapAssetKey] = LiveMapViewStatus.Detecting;
        RaiseChanged(mapAssetKey);

        var status = LiveMapViewStatus.FailedLowConfidence;
        MapViewFix? fix = null;
        await Task.Run(() =>
        {
            var screenshot = _capture.Capture();
            if (screenshot is null)
            {
                status = LiveMapViewStatus.FailedNoCapture;
                _logger?.LogWarning("Probe({Area}): IOverlayCaptureSource.Capture returned null — overlay window not realised, or capture exception (see Mithril.Overlay.Capture warnings).", mapAssetKey);
                return;
            }

            var baseTex = _textures.TryGetBaseTexture(mapAssetKey);
            if (baseTex is null)
            {
                status = LiveMapViewStatus.FailedNoBaseTexture;
                _logger?.LogWarning("Probe({Area}): IBaseTextureProvider.TryGetBaseTexture returned null — area not in bundled CanonicalAssetHashes / texture catalogue. Live-view detection cannot run; ghosts + survey-anchor will fall back to canonical projection (wrong scale on overlay surface — mithril#1107).", mapAssetKey);
                return;
            }

            fix = _probe.TryProbe(screenshot, baseTex);
            status = fix.HasValue ? LiveMapViewStatus.Detected : LiveMapViewStatus.FailedLowConfidence;
            if (fix is { } f)
                _logger?.LogInformation("Probe({Area}): detected — pan=({PanX:0},{PanY:0})tex viewScale={Scale:0.000} conf={Conf:0.00}.",
                    mapAssetKey, f.PanTexPxX, f.PanTexPxY, f.ViewScale, f.Confidence);
            else
                _logger?.LogWarning("Probe({Area}): IMapViewProbe.TryProbe returned null — cross-correlation didn't meet confidence threshold.", mapAssetKey);
        }, ct).ConfigureAwait(false);

        if (fix.HasValue) _fixes[mapAssetKey] = fix.Value;
        _status[mapAssetKey] = status;
        RaiseChanged(mapAssetKey);
    }

    private void RaiseChanged(string area)
        => _uiSynchronizer(() => Changed?.Invoke(area));
}
