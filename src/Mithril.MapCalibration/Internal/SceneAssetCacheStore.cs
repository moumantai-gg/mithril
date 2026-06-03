using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>Per-install persistence for <see cref="SceneAssetCache"/>.
/// Mirrors <see cref="UserRefinementStore"/>'s transactional shape: atomic
/// temp+rename writes, IOException-rollback, per-entry resilient parse.
/// File: <c>%LocalAppData%/Mithril/MapCalibration/scene-asset-cache.json</c>.</summary>
internal sealed class SceneAssetCacheStore
{
    /// <summary>Highest <c>schemaVersion</c> this build knows how to read.
    /// On-disk shape currently matches v1. Bump alongside any breaking
    /// shape change to <see cref="SceneAssetCacheFile"/>.</summary>
    private const int KnownSchemaVersion = 1;

    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry> _entries
        = new(SceneAssetCacheKeyComparer.Ordinal);

    public SceneAssetCacheStore(string directory, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "scene-asset-cache.json");
        _logger = logger;
        Load();
    }

    public bool TryGet(string parentAreaKey, string? sceneFriendlyName, out SceneAssetCacheEntry entry)
    {
        lock (_gate)
        {
            var key = new SceneAssetCacheKey(parentAreaKey, sceneFriendlyName);
            return _entries.TryGetValue(key, out entry);
        }
    }

    public void Record(string parentAreaKey, string? sceneFriendlyName, string mapAssetKey, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            var key = new SceneAssetCacheKey(parentAreaKey, sceneFriendlyName);
            var hadPrior = _entries.TryGetValue(key, out var prior);
            _entries[key] = new SceneAssetCacheEntry(mapAssetKey, observedAt);
            try { Persist(); }
            catch
            {
                if (hadPrior) _entries[key] = prior;
                else _entries.Remove(key);
                throw;
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            using var stream = File.OpenRead(_filePath);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            // schemaVersion read-back (mithril#1054): Persist stamps v1, but
            // until this gate Load ignored the field. A future v>1 file read by
            // this build would be silently half-parsed — every v2-shaped entry
            // falls through the per-entry resilient parse as "missing fields"
            // and the user's learned cache vanishes. Fail-closed instead: start
            // empty so the newer build (which CAN read v>1) sees the file
            // intact on its next run. Mirrors UserRefinementStore.Load's
            // schemaVersion handling.
            var schemaVersion = 1; // absent → v1 (pre-stamp shape)
            if (doc.RootElement.TryGetProperty("schemaVersion", out var verProp) &&
                verProp.ValueKind == JsonValueKind.Number &&
                verProp.TryGetInt32(out var v))
            {
                schemaVersion = v;
            }
            if (schemaVersion > KnownSchemaVersion)
            {
                _logger?.LogWarning(
                    "scene-asset-cache schema v{Found} at {Path} is newer than supported v{Known}; " +
                    "starting empty this session to avoid corrupting data this build doesn't recognise.",
                    schemaVersion, _filePath, KnownSchemaVersion);
                return;
            }

            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return;

            var loaded = new Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry>(SceneAssetCacheKeyComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                try
                {
                    if (!entry.TryGetProperty("parentArea", out var pa) ||
                        pa.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("mapAssetKey", out var ak) ||
                        ak.ValueKind != JsonValueKind.String) continue;

                    var parentArea = pa.GetString()!;
                    var mapAssetKey = ak.GetString()!;
                    string? sceneFriendlyName = null;
                    if (entry.TryGetProperty("sceneFriendlyName", out var sfn) &&
                        sfn.ValueKind == JsonValueKind.String) sceneFriendlyName = sfn.GetString();

                    DateTimeOffset observedAt = DateTimeOffset.MinValue;
                    if (entry.TryGetProperty("lastObservedAt", out var ts) &&
                        ts.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(ts.GetString(), out var parsedTs))
                        observedAt = parsedTs;

                    var key = new SceneAssetCacheKey(parentArea, sceneFriendlyName);
                    loaded[key] = new SceneAssetCacheEntry(mapAssetKey, observedAt);
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    _logger?.LogWarning(ex, "Skipping unparseable scene-asset-cache entry — {Reason}.", ex.Message);
                }
            }
            _entries = loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger?.LogWarning(ex, "Failed to load scene-asset-cache at {Path} — starting empty.", _filePath);
            _entries = new Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry>(SceneAssetCacheKeyComparer.Ordinal);
        }
    }

    private void Persist()
    {
        // Sort entries for deterministic file output (ordinal by parent then friendly).
        var sortedEntries = _entries
            .OrderBy(kv => kv.Key.ParentAreaKey, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.SceneFriendlyName ?? string.Empty, StringComparer.Ordinal)
            .Select(kv => new SceneAssetCacheFileEntry(
                ParentArea: kv.Key.ParentAreaKey,
                SceneFriendlyName: kv.Key.SceneFriendlyName,
                MapAssetKey: kv.Value.MapAssetKey,
                LastObservedAt: kv.Value.LastObservedAt))
            .ToArray();
        var file = new SceneAssetCacheFile(SchemaVersion: 1, Entries: sortedEntries);
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.SceneAssetCacheFile);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else File.Move(tmp, _filePath);
    }
}

internal readonly record struct SceneAssetCacheKey(string ParentAreaKey, string? SceneFriendlyName);
internal readonly record struct SceneAssetCacheEntry(string MapAssetKey, DateTimeOffset LastObservedAt);

internal sealed class SceneAssetCacheKeyComparer : IEqualityComparer<SceneAssetCacheKey>
{
    public static readonly SceneAssetCacheKeyComparer Ordinal = new();
    public bool Equals(SceneAssetCacheKey x, SceneAssetCacheKey y) =>
        string.Equals(x.ParentAreaKey, y.ParentAreaKey, StringComparison.Ordinal) &&
        string.Equals(x.SceneFriendlyName, y.SceneFriendlyName, StringComparison.Ordinal);
    public int GetHashCode(SceneAssetCacheKey k) =>
        HashCode.Combine(k.ParentAreaKey, k.SceneFriendlyName ?? string.Empty);
}

internal sealed record SceneAssetCacheFile(int SchemaVersion, SceneAssetCacheFileEntry[] Entries);
internal sealed record SceneAssetCacheFileEntry(
    string ParentArea,
    string? SceneFriendlyName,
    string MapAssetKey,
    DateTimeOffset LastObservedAt);
