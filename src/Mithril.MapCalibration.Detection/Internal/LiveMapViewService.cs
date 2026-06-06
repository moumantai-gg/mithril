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
        // GetOrAdd may call the factory more than once under contention but
        // only one result wins; we therefore call a non-factory overload that
        // is guaranteed to call the factory exactly once via the lock-free
        // compare-and-swap that AddOrUpdate provides.
        // Using GetOrAdd: factory is called speculatively but the winner is
        // deterministic — if the key is already present the extra Task.Run
        // is wasted but the second caller still gets the right in-flight task.
        // For dedup correctness, the returned task must be the first-inserted
        // one. GetOrAdd does that.
        var task = _inflight.GetOrAdd(mapAssetKey, key => RunProbe(key, ct));
        return task.ContinueWith(
            _ => _inflight.TryRemove(new KeyValuePair<string, Task>(mapAssetKey, task)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task RunProbe(string mapAssetKey, CancellationToken ct)
    {
        _status[mapAssetKey] = LiveMapViewStatus.Detecting;

        var status = LiveMapViewStatus.FailedLowConfidence;
        MapViewFix? fix = null;
        await Task.Run(() =>
        {
            var screenshot = _capture.Capture();
            if (screenshot is null) { status = LiveMapViewStatus.FailedNoCapture; return; }

            var baseTex = _textures.TryGetBaseTexture(mapAssetKey);
            if (baseTex is null) { status = LiveMapViewStatus.FailedNoBaseTexture; return; }

            fix = _probe.TryProbe(screenshot, baseTex);
            status = fix.HasValue ? LiveMapViewStatus.Detected : LiveMapViewStatus.FailedLowConfidence;
        }, ct).ConfigureAwait(false);

        if (fix.HasValue) _fixes[mapAssetKey] = fix.Value;
        _status[mapAssetKey] = status;
        RaiseChanged(mapAssetKey);
    }

    private void RaiseChanged(string area)
        => _uiSynchronizer(() => Changed?.Invoke(area));
}
