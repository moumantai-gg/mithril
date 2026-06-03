namespace Mithril.MapCalibration;

/// <summary>
/// Per-install cache of <c>(ParentAreaKey, SceneFriendlyName?) → MapAssetKey</c>
/// pairings learned from observed <c>MapAssetChanged</c> events and pre-seeded
/// at startup from the bundled-baseline ∩ areas.json intersection. Provides
/// the cold-start fallback for the resolution cascade
/// (see <see cref="SceneResolution.ResolveCurrentScene"/>): when
/// <c>IMapState.CurrentMapScene</c> is null but <c>CurrentArea</c> is known,
/// the cache supplies a synthesized <see cref="MapSceneRef"/> for the renderer
/// / autocal-trigger / Legolas.
/// </summary>
public interface ISceneAssetCache
{
    /// <summary>Look up the cached <see cref="MapSceneRef"/> for a
    /// <c>(parentAreaKey, sceneFriendlyName)</c> pair. Composite-key strict —
    /// null friendly name does NOT match a stored entry with a non-null
    /// friendly name and vice versa. Returns null on miss.</summary>
    MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName);

    /// <summary>Write-through record of an observation. Overwrites any prior
    /// entry for the same composite key (live observation is authoritative;
    /// seed entries lose). Persists transactionally; on <see cref="System.IO.IOException"/>
    /// the in-memory state is rolled back and the exception is re-thrown.</summary>
    void Record(MapSceneRef scene, DateTimeOffset observedAt);
}
