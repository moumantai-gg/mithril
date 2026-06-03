using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Human-diffable, schema-versioned metadata manifest for the bundled icon
/// templates. The pixel payload itself lives in the sibling DeflateStream-
/// compressed <c>icon-templates.bin</c>; <see cref="PixelSha256"/> is the
/// SHA-256 (lowercase hex) of the decompressed pixel stream — the integrity
/// gate against manifest↔blob skew / truncation.
/// </summary>
internal sealed record IconTemplateManifest(
    int SchemaVersion,
    string PixelSha256,
    List<IconTemplateManifestEntry> Icons)
{
    /// <summary>
    /// PG version the asset-extractor sidecar decoded these icons from (issue
    /// #931); the cache-invalidation / canonical-hash-gate lookup key. Null in
    /// older / synthetic manifests.
    /// </summary>
    public string? PgVersion { get; init; }

    /// <summary>Sidecar assembly version that wrote this manifest (issue #931).</summary>
    public string? ExtractorVersion { get; init; }
}

internal sealed record IconTemplateManifestEntry(
    string Name,
    string LandmarkType,
    double PivotX,
    double PivotY,
    int Width,
    int Height);
