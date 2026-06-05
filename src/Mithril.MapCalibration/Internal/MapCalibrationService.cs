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
    private readonly ILogger? _logger;
    private readonly object _eventGate = new();

    public MapCalibrationService(
        IReadOnlyDictionary<string, AreaCalibration> baseline,
        UserRefinementStore userStore,
        ILogger? logger = null)
    {
        _baseline = baseline;
        _userStore = userStore;
        _logger = logger;
    }

    public event EventHandler<MapSceneRef>? Changed;

    public bool IsCalibrated(MapSceneRef scene) => GetCalibration(scene) is not null;

    public AreaCalibration? GetCalibration(MapSceneRef scene)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

        var candidates = new List<AreaCalibration>(capacity: 2);
        if (_userStore.TryGet(scene.MapAssetKey, out var user)) candidates.Add(user);
        if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) candidates.Add(baseline);
        // CommunitySync slot reserved.

        if (candidates.Count == 0) return null;

        var eligible = candidates.Where(c => c.ReferenceCount >= MinReferences).ToList();
        AreaCalibration picked;
        if (eligible.Count == 0)
        {
            picked = candidates.OrderByDescending(SourceRank).First();
            _logger?.LogInformation(
                "GetCalibration({MapAssetKey}): no candidate cleared MinReferences={Floor}; returning best-source-precedence fallback (source={Source}, residual={Residual:0.00}px, refs={Refs}).",
                scene.MapAssetKey, MinReferences, picked.Source, picked.ResidualPixels, picked.ReferenceCount);
        }
        else
        {
            picked = eligible.OrderBy(c => c.ResidualPixels).ThenByDescending(SourceRank).First();
            _logger?.LogTrace(
                "GetCalibration({MapAssetKey}): {Eligible}/{Total} eligible, picked source={Source} residual={Residual:0.00}px refs={Refs}.",
                scene.MapAssetKey, eligible.Count, candidates.Count, picked.Source, picked.ResidualPixels, picked.ReferenceCount);
        }
        return picked;
    }

    /// <summary>
    /// Minimum <see cref="AreaCalibration.ReferenceCount"/> required for a candidate to be
    /// considered in the residual-ordered pick. Fits below this floor are excluded from the
    /// eligible set because a closed-form similarity solve at N=2 has residual ≈ 0 by
    /// construction and is therefore not statistically meaningful.
    /// </summary>
    internal const int MinReferences = 4;

    private static int SourceRank(AreaCalibration c) => c.Source switch
    {
        CalibrationSource.UserRefinement  => 4,
        CalibrationSource.AutoCapture     => 3,
        CalibrationSource.CommunitySync   => 2,
        CalibrationSource.BundledBaseline => 1,
        _ => 0,
    };

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

    /// <summary>
    /// #1076 typed-frame view: every candidate AreaCalibration for the scene
    /// that infers to TEXTURE frame (see <see cref="InferFrameFromSource"/>),
    /// wrapped as <see cref="WorldToTextureCalibration"/>. Underlying storage
    /// is unchanged — this is a derived projection of <see cref="GetAllSources"/>
    /// for the texture-frame consumers (drift check, AutoCal).
    /// </summary>
    internal IReadOnlyList<WorldToTextureCalibration> GetTextureRecords(MapSceneRef scene)
    {
        var all = GetAllSources(scene);
        if (all.Count == 0) return Array.Empty<WorldToTextureCalibration>();
        var result = new List<WorldToTextureCalibration>(all.Count);
        foreach (var cal in all)
        {
            if (InferFrameFromSource(cal.Source) == CalibrationFrameKind.Texture)
                result.Add(ToTextureCalibration(cal));
        }
        return result;
    }

    /// <summary>
    /// #1076 typed-frame view: every candidate AreaCalibration for the scene
    /// that infers to OVERLAY frame, wrapped as <see cref="WorldToOverlayCalibration"/>.
    /// Used by Legolas overlay rendering.
    /// </summary>
    internal IReadOnlyList<WorldToOverlayCalibration> GetOverlayRecords(MapSceneRef scene)
    {
        var all = GetAllSources(scene);
        if (all.Count == 0) return Array.Empty<WorldToOverlayCalibration>();
        var result = new List<WorldToOverlayCalibration>(all.Count);
        foreach (var cal in all)
        {
            if (InferFrameFromSource(cal.Source) == CalibrationFrameKind.Overlay)
                result.Add(ToOverlayCalibration(cal));
        }
        return result;
    }

    /// <summary>
    /// #1076 source→frame inference. Spec §7.2 (revised after P.1/P.1b):
    /// AutoCapture + BundledBaseline + CommunitySync → Texture frame;
    /// UserRefinement → Overlay (the in-the-wild Schema-1 default since AutoCal
    /// has never shipped to users — every persisted UserRefinement record is
    /// Legolas-wizard-produced overlay-frame).
    /// </summary>
    private static CalibrationFrameKind InferFrameFromSource(CalibrationSource source) => source switch
    {
        CalibrationSource.AutoCapture     => CalibrationFrameKind.Texture,
        CalibrationSource.BundledBaseline => CalibrationFrameKind.Texture,
        CalibrationSource.CommunitySync   => CalibrationFrameKind.Texture,
        CalibrationSource.UserRefinement  => CalibrationFrameKind.Overlay,
        _ => CalibrationFrameKind.Overlay, // unknown → safest default; spec §13 P.1b
    };

    private static WorldToTextureCalibration ToTextureCalibration(AreaCalibration legacy) =>
        new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
            legacy.MirrorNorth, legacy.CalibrationZoom);

    private static WorldToOverlayCalibration ToOverlayCalibration(AreaCalibration legacy) =>
        new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
            legacy.MirrorNorth, legacy.CalibrationZoom);

    private enum CalibrationFrameKind { Texture, Overlay }

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
