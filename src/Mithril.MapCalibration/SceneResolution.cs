using Arda.World.Player;

namespace Mithril.MapCalibration;

/// <summary>Cold-start scene resolution helper consumed by every renderer +
/// autocal call site. Pure function, no side effects.</summary>
public static class SceneResolution
{
    /// <summary>Resolve the current <see cref="MapSceneRef"/> using the cascade
    /// (a) <see cref="IMapState.CurrentMapScene"/> (live truth, preferred),
    /// (b) <see cref="ISceneAssetCache.TryResolve"/> on
    /// <see cref="IMapState.CurrentArea"/> with <c>sceneFriendlyName: null</c>
    /// (seeded or learned), (c) <c>null</c> (strict gate — uncalibrated).</summary>
    /// <remarks>Branch (a) wins over (b) when both could fire — observation is
    /// authoritative, so a <c>Downloading Map</c> line that emits a fresh
    /// <see cref="MapSceneRef.MapAssetKey"/> for a <c>(parent, friendly)</c>
    /// the cache already knew under a stale <c>MapAssetKey</c> will overwrite
    /// the cache via <see cref="ISceneAssetCache.Record"/> write-through.
    /// Empty <see cref="IMapState.CurrentArea"/> is treated as strict-gate
    /// (cache lookup is skipped).</remarks>
    public static MapSceneRef? ResolveCurrentScene(IMapState state, ISceneAssetCache cache)
    {
        if (state.CurrentMapScene is { } live) return live;
        if (state.CurrentArea is { Length: > 0 } area &&
            cache.TryResolve(area, sceneFriendlyName: null) is { } cached)
            return cached;
        return null;
    }
}
