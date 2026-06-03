using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Default <see cref="IMapCalibrationService"/> implementation. Composes the
/// bundled baseline catalogue with the per-user refinement store and resolves
/// the active transform per the precedence rules documented on
/// <see cref="IMapCalibrationService"/>.
///
/// <para>The service is thread-safe: reads take a short lock against the
/// refinement-store dictionary; writes go through <see cref="UserRefinementStore"/>
/// (which serialises persistence under its own lock).</para>
///
/// <para>The store backing (<c>_userStore</c>, <c>_baseline</c>) is keyed on the
/// raw <c>MapAssetKey</c> string (the persistence horizon, unchanged from
/// mithril#1021). Each public method extracts <see cref="MapSceneRef.MapAssetKey"/>
/// for the inner lookup; the typed <see cref="MapSceneRef"/> parameter prevents
/// the bare-string footgun at every call site.</para>
/// </summary>
internal sealed class MapCalibrationService : IMapCalibrationService
{
    private readonly IReadOnlyDictionary<string, AreaCalibration> _baseline;
    private readonly UserRefinementStore _userStore;
    private readonly double _goodResidualThresholdPx;
    private readonly ILogger? _logger;
    private readonly object _eventGate = new();

    public MapCalibrationService(
        IReadOnlyDictionary<string, AreaCalibration> baseline,
        UserRefinementStore userStore,
        double goodResidualThresholdPx,
        ILogger? logger = null)
    {
        _baseline = baseline;
        _userStore = userStore;
        _goodResidualThresholdPx = goodResidualThresholdPx;
        _logger = logger;
    }

    public event EventHandler<MapSceneRef>? Changed;

    public bool IsCalibrated(MapSceneRef scene) => GetCalibration(scene) is not null;

    public AreaCalibration? GetCalibration(MapSceneRef scene)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

        if (_userStore.TryGet(scene.MapAssetKey, out var user)
            && user.ResidualPixels <= _goodResidualThresholdPx)
        {
            return user;
        }

        // CommunitySync slot reserved here; not yet wired.

        if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) return baseline;

        // A user refinement above the threshold loses to a usable baseline.
        // When no baseline exists, fall back to the user refinement anyway —
        // a high-residual transform is better than nothing for the consumer's
        // degradation UX (chip + render).
        if (_userStore.TryGet(scene.MapAssetKey, out var fallbackUser)) return fallbackUser;

        return null;
    }

    public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom) =>
        GetCalibration(scene)?.WorldToWindow(world, currentZoom);

    public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) =>
        GetCalibration(scene)?.WindowToWorld(pixel, currentZoom);

    public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations
    {
        get
        {
            // Union of asset keys across both stores; the active record is whichever
            // source GetCalibration would pick for each. The dict iteration is the
            // only place where we synthesize a MapSceneRef from a raw asset key —
            // parent area + scene friendly name are unknown to the store, so we pass
            // them as ("", null). GetCalibration only reads MapAssetKey.
            var keys = new HashSet<string>(_baseline.Keys, StringComparer.Ordinal);
            foreach (var key in _userStore.All.Keys) keys.Add(key);
            var result = new Dictionary<string, AreaCalibration>(keys.Count, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var synthetic = new MapSceneRef(ParentAreaKey: string.Empty, SceneFriendlyName: null, MapAssetKey: key);
                if (GetCalibration(synthetic) is { } cal) result[key] = cal;
            }
            return result;
        }
    }

    public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return Array.Empty<AreaCalibration>();

        var sources = new List<AreaCalibration>(capacity: 2);
        if (_userStore.TryGet(scene.MapAssetKey, out var user)) sources.Add(user);
        if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) sources.Add(baseline);
        // CommunitySync slot reserved here; not yet wired.
        return sources;
    }

    public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey))
            throw new ArgumentException("scene.MapAssetKey required", nameof(scene));
        ArgumentNullException.ThrowIfNull(calibration);

        _userStore.Save(scene.MapAssetKey, calibration);
        _logger?.LogInformation("Saved user refinement for {MapAssetKey} (residual {Residual:F2}px, references {Count}).",
            scene.MapAssetKey, calibration.ResidualPixels, calibration.ReferenceCount);
        RaiseChanged(scene);
    }

    public void ClearUserRefinement(MapSceneRef scene)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return;
        if (_userStore.Remove(scene.MapAssetKey))
        {
            _logger?.LogInformation("Cleared user refinement for {MapAssetKey}.", scene.MapAssetKey);
            RaiseChanged(scene);
        }
    }

    private void RaiseChanged(MapSceneRef scene)
    {
        EventHandler<MapSceneRef>? handler;
        lock (_eventGate) handler = Changed;
        handler?.Invoke(this, scene);
    }
}
