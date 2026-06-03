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
        {
            // mithril#1053: an under-defined composite — don't poison the cache.
            // Surface the drop at Trace so a support investigation can spot the
            // MapAssetLoader "Downloading Map fired before Initializing area!"
            // race (the dominant cause) without spamming Information.
            _logger?.LogTrace(
                "SceneAssetCache: dropped under-defined composite (parentArea='{ParentArea}', assetKey='{AssetKey}').",
                scene.ParentAreaKey, scene.MapAssetKey);
            return;
        }
        _store.Record(scene.ParentAreaKey, scene.SceneFriendlyName, scene.MapAssetKey, observedAt);
    }
}
