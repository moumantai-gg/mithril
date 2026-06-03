using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration;

/// <summary>
/// Default <see cref="ISceneAssetCache"/> implementation. Delegates persistence
/// to <see cref="SceneAssetCacheStore"/>; the cache itself is the thread-safe
/// in-memory dict + the write-through Record path.
/// </summary>
public sealed class SceneAssetCache : ISceneAssetCache
{
    private readonly SceneAssetCacheStore _store;
    private readonly ILogger? _logger;

    internal SceneAssetCache(SceneAssetCacheStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName)
    {
        if (string.IsNullOrEmpty(parentAreaKey)) return null;
        if (_store.TryGet(parentAreaKey, sceneFriendlyName, out var entry))
            return new MapSceneRef(parentAreaKey, sceneFriendlyName, entry.MapAssetKey);
        return null;
    }

    public void Record(MapSceneRef scene, DateTimeOffset observedAt)
    {
        if (string.IsNullOrEmpty(scene.ParentAreaKey) || string.IsNullOrEmpty(scene.MapAssetKey))
            return; // an under-defined composite — don't poison the cache
        _store.Record(scene.ParentAreaKey, scene.SceneFriendlyName, scene.MapAssetKey, observedAt);
    }
}
