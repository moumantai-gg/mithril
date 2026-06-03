using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>One-shot startup helper that pre-populates <see cref="SceneAssetCacheStore"/>
/// from the bundled-baseline ∩ areas.json intersection. For each baseline key
/// <c>"Map_&lt;X&gt;"</c> where <c>X</c> exists in <paramref name="areaKeys"/>,
/// records <c>(X, null) → "Map_X"</c> with <see cref="DateTimeOffset.MinValue"/> so any
/// real observation wins on first write.
///
/// <para>Decouples from <c>Mithril.Shared.Reference.AreaEntry</c> by taking the
/// set of area keys directly — <see cref="Mithril.MapCalibration"/> stays at the
/// <c>net10.0</c> TFM, and the caller (composition root) is the natural place
/// to project <c>IReferenceDataService.Areas.Keys</c> into the set.</para>
/// </summary>
internal static class SceneAssetCacheSeeder
{
    private const string MapAssetPrefix = "Map_";

    public static void Seed(
        SceneAssetCacheStore store,
        IReadOnlyDictionary<string, AreaCalibration> baseline,
        IReadOnlySet<string> areaKeys,
        ILogger? logger = null)
    {
        var seeded = 0;
        foreach (var baselineKey in baseline.Keys)
        {
            if (!baselineKey.StartsWith(MapAssetPrefix, StringComparison.Ordinal)) continue;
            var areaCandidate = baselineKey.Substring(MapAssetPrefix.Length);
            if (!areaKeys.Contains(areaCandidate)) continue;

            // Skip if a prior observation already populated this cell — Record
            // would overwrite the observed entry, but TryGet lets us compare
            // timestamps before deciding to overwrite. Observation (LastObservedAt
            // > MinValue) always wins over seed (LastObservedAt = MinValue).
            if (store.TryGet(areaCandidate, null, out var existing) &&
                existing.LastObservedAt > DateTimeOffset.MinValue)
            {
                continue;
            }

            store.Record(areaCandidate, sceneFriendlyName: null, mapAssetKey: baselineKey, DateTimeOffset.MinValue);
            seeded++;
        }

        if (seeded > 0)
            logger?.LogInformation(
                "Seeded {Count} directly-registered scene-asset-cache entries from baseline ∩ areas.json.",
                seeded);
    }
}
