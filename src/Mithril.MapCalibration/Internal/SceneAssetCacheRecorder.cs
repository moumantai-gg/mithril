using System.IO;
using Arda.Contracts;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// <see cref="IHostedService"/> that subscribes to <see cref="MapAssetChanged"/> and
/// writes every observation to <see cref="ISceneAssetCache"/>. Replay metadata is
/// honoured the same as live — the file replay is the cheapest learning signal
/// the cache has; recording during replay populates the cache for cold-start
/// resolution on first boot.
/// </summary>
internal sealed class SceneAssetCacheRecorder : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly ISceneAssetCache _cache;
    private readonly ILogger? _logger;
    private IDisposable? _subscription;

    public SceneAssetCacheRecorder(IDomainEventSubscriber bus, ISceneAssetCache cache, ILogger? logger = null)
    {
        _bus = bus;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnMapAssetChanged(MapAssetChanged evt)
    {
        if (evt.CurrentScene is not { } scene) return;
        try
        {
            _cache.Record(scene, evt.Metadata.Timestamp ?? evt.Metadata.ReadOn);
        }
        catch (IOException ex)
        {
            // Lossy: in-memory state was rolled back by Record's transactional
            // wrapper. Log + drop; the next observation will retry. Surface as
            // Warning so a persistently-failing disk shows in diagnostics
            // without spamming Error on every event.
            _logger?.LogWarning(ex,
                "Failed to persist scene-asset-cache entry for {ParentArea}/{Friendly}; will retry on next observation.",
                scene.ParentAreaKey, scene.SceneFriendlyName);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
