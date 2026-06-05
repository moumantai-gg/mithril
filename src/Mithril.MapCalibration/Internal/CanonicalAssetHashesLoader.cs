using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Schema v1 → v2 tolerant loader for canonical-asset-hashes.json
/// (mithril#1081). The on-disk shape's inner-dict value changed from
/// <c>string</c> (sha-only) to <see cref="CanonicalAssetHashEntry"/>
/// (sha + width + height). v1 values are wrapped on load with
/// <c>Width = Height = 0</c>; dim consumers see a catalogue miss for
/// v1-loaded entries and fail-soft to "no render", while hash-gate
/// consumers continue to read <see cref="CanonicalAssetHashEntry.Sha"/>
/// without changing behaviour.
/// </summary>
internal static class CanonicalAssetHashesLoader
{
    /// <summary>Load the catalogue from <paramref name="stream"/>. Returns
    /// null on read / parse failure so the caller can fail-soft to an
    /// empty catalogue (same posture as today).</summary>
    public static CanonicalAssetHashes? TryLoad(Stream stream, ILogger? logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Canonical-asset-hashes JSON failed to parse — gate accepts all (safe-degrade).");
            return null;
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Canonical-asset-hashes stream failed to read — gate accepts all (safe-degrade).");
            return null;
        }
    }

    /// <summary>Test-friendly overload. Parses a JSON string; throws on
    /// malformed JSON (tests rely on the throw to catch fixture bugs).</summary>
    public static CanonicalAssetHashes Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    private static CanonicalAssetHashes Parse(JsonElement root)
    {
        var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
            ? sv.GetInt32()
            : 1;
        var isV1 = schemaVersion < 2;

        var byPg = new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(System.StringComparer.Ordinal);
        if (root.TryGetProperty("byPgVersion", out var bpv) && bpv.ValueKind == JsonValueKind.Object)
        {
            foreach (var pgVersionProp in bpv.EnumerateObject())
            {
                var byArtifact = new Dictionary<string, CanonicalAssetHashEntry>(System.StringComparer.Ordinal);
                foreach (var artifactProp in pgVersionProp.Value.EnumerateObject())
                {
                    byArtifact[artifactProp.Name] = ReadEntry(artifactProp.Value, isV1);
                }
                byPg[pgVersionProp.Name] = byArtifact;
            }
        }
        return new CanonicalAssetHashes(schemaVersion, byPg);
    }

    private static CanonicalAssetHashEntry ReadEntry(JsonElement element, bool isV1)
    {
        if (isV1 || element.ValueKind == JsonValueKind.String)
        {
            // v1: bare-string sha. Wrap with zero dims (dim consumers see catalogue miss).
            return new CanonicalAssetHashEntry(element.GetString() ?? string.Empty, 0, 0);
        }

        // v2: { sha, width, height } object.
        var sha = element.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
        var width = element.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
        var height = element.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;
        return new CanonicalAssetHashEntry(sha, width, height);
    }
}
