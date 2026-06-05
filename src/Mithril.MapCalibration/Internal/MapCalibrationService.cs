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

        var candidates = new List<AreaCalibration>(capacity: 3);
        if (_userStore.TryGetAny(scene.MapAssetKey, out var slots))
        {
            if (slots.Texture is not null) candidates.Add(slots.Texture);
            if (slots.Overlay is not null) candidates.Add(slots.Overlay);
        }
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

    public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom)
    {
        var pick = PickTexture(scene);
        return pick is null ? null : pick.Value.ToTexture(world, currentZoom);
    }

    public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom)
    {
        var pick = PickTexture(scene);
        return pick?.FromTexture(pixel, currentZoom);
    }

    public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom)
    {
        var pick = PickOverlay(scene);
        return pick is null ? null : pick.Value.ToOverlay(world, currentZoom);
    }

    public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom)
    {
        var pick = PickOverlay(scene);
        return pick?.FromOverlay(pixel, currentZoom);
    }

    public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => PickTexture(scene);

    public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => PickOverlay(scene);

    /// <summary>
    /// #1076 picker for the texture-frame slice. Same tie-break semantics as
    /// <see cref="GetCalibration"/> (residual asc + MinReferences floor + source
    /// precedence) but restricted to candidates whose source maps to texture.
    /// Returns null when no texture-frame candidate exists.
    /// </summary>
    private WorldToTextureCalibration? PickTexture(MapSceneRef scene)
    {
        var legacy = PickByFrame(scene, CalibrationFrame.Texture);
        return legacy is null ? null : ToTextureCalibration(legacy);
    }

    /// <summary>#1076 picker for the overlay-frame slice; see <see cref="PickTexture"/>.</summary>
    private WorldToOverlayCalibration? PickOverlay(MapSceneRef scene)
    {
        var legacy = PickByFrame(scene, CalibrationFrame.Overlay);
        return legacy is null ? null : ToOverlayCalibration(legacy);
    }

    private AreaCalibration? PickByFrame(MapSceneRef scene, CalibrationFrame frame)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

        // mithril#1078: read AreaCalibration.Frame directly (single source of truth).
        // The Schema-1 → Schema-2 inference has already been done by UserRefinementStore
        // and BundledBaselineLoader at load time; the picker must not re-infer from
        // Source. Stamping Source AND Frame at save sites (AutoCalibrationEngine and
        // AreaCalibrationService) keeps disk records self-describing.
        var candidates = new List<AreaCalibration>(capacity: 2);
        if (_userStore.TryGet(scene.MapAssetKey, frame, out var user)) candidates.Add(user);
        if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline) && baseline.Frame == frame) candidates.Add(baseline);

        if (candidates.Count == 0) return null;

        var eligible = candidates.Where(c => c.ReferenceCount >= MinReferences).ToList();
        return eligible.Count == 0
            ? candidates.OrderByDescending(SourceRank).First()
            : eligible.OrderBy(c => c.ResidualPixels).ThenByDescending(SourceRank).First();
    }

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

        // mithril#1082: GetAllSources can now return up to 3 entries — both
        // user-store slots (texture + overlay) plus the bundled baseline. All
        // call sites iterate (FirstOrDefault / OrderBy) without count assumptions.
        var sources = new List<AreaCalibration>(capacity: 3);
        if (_userStore.TryGetAny(scene.MapAssetKey, out var slots))
        {
            if (slots.Texture is not null) sources.Add(slots.Texture);
            if (slots.Overlay is not null) sources.Add(slots.Overlay);
        }
        if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) sources.Add(baseline);
        // CommunitySync slot reserved here; not yet wired.
        return sources;
    }

    /// <summary>
    /// #1076 typed-frame view: every candidate AreaCalibration for the scene
    /// whose <see cref="AreaCalibration.Frame"/> is <see cref="CalibrationFrame.Texture"/>,
    /// wrapped as <see cref="WorldToTextureCalibration"/>. Underlying storage is
    /// unchanged — this is a derived projection of <see cref="GetAllSources"/>
    /// for the texture-frame consumers (drift check, AutoCal).
    ///
    /// <para>mithril#1078: reads <c>cal.Frame</c> directly. The Schema-1 → Schema-2
    /// inference is owned by <see cref="UserRefinementStore"/> and
    /// <see cref="BundledBaselineLoader"/> at load time; this method is a pure
    /// filter, no re-inference.</para>
    /// </summary>
    internal IReadOnlyList<WorldToTextureCalibration> GetTextureRecords(MapSceneRef scene)
    {
        var all = GetAllSources(scene);
        if (all.Count == 0) return Array.Empty<WorldToTextureCalibration>();
        var result = new List<WorldToTextureCalibration>(all.Count);
        foreach (var cal in all)
        {
            if (cal.Frame == CalibrationFrame.Texture)
                result.Add(ToTextureCalibration(cal));
        }
        return result;
    }

    /// <summary>
    /// #1076 typed-frame view: every candidate AreaCalibration for the scene
    /// whose <see cref="AreaCalibration.Frame"/> is <see cref="CalibrationFrame.Overlay"/>,
    /// wrapped as <see cref="WorldToOverlayCalibration"/>. Used by Legolas overlay
    /// rendering.
    ///
    /// <para>mithril#1078: reads <c>cal.Frame</c> directly; see
    /// <see cref="GetTextureRecords"/>.</para>
    /// </summary>
    internal IReadOnlyList<WorldToOverlayCalibration> GetOverlayRecords(MapSceneRef scene)
    {
        var all = GetAllSources(scene);
        if (all.Count == 0) return Array.Empty<WorldToOverlayCalibration>();
        var result = new List<WorldToOverlayCalibration>(all.Count);
        foreach (var cal in all)
        {
            if (cal.Frame == CalibrationFrame.Overlay)
                result.Add(ToOverlayCalibration(cal));
        }
        return result;
    }

    private static WorldToTextureCalibration ToTextureCalibration(AreaCalibration legacy) =>
        new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
            legacy.MirrorNorth, legacy.CalibrationZoom)
        {
            PixelSha256 = legacy.PixelSha256,
        };

    private static WorldToOverlayCalibration ToOverlayCalibration(AreaCalibration legacy) =>
        new(legacy.OriginX, legacy.OriginY, legacy.Scale, legacy.RotationRadians,
            legacy.MirrorNorth, legacy.CalibrationZoom);

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

    public void DeleteUserRefinement(MapSceneRef scene, CalibrationFrame frame)
    {
        if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return;
        if (_userStore.Remove(scene.MapAssetKey, frame))
        {
            _logger?.LogInformation(
                "Deleted user refinement for {MapAssetKey} frame {Frame}.",
                scene.MapAssetKey, frame);
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
