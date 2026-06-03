namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Human-diffable, schema-versioned metadata manifest for a single cached map
/// base texture (one area). The gray pixel payload lives in the sibling
/// DeflateStream-compressed <c>map-texture-&lt;area&gt;.bin</c>;
/// <see cref="PixelSha256"/> is the SHA-256 (lowercase hex) of the decompressed
/// gray byte stream — the integrity gate against manifest↔blob skew / truncation,
/// and the value the canonical-hash gate compares against.
///
/// <para>Gray-only (no alpha channel): the base texture is consumed by the
/// deviation/NCC detector as a single-channel <see cref="GrayImage"/>. The
/// <see cref="PgVersion"/> / <see cref="ExtractorVersion"/> stamps are the
/// cache-invalidation + provenance keys written by the out-of-process
/// asset-extractor sidecar (issue #931).</para>
/// </summary>
internal sealed record MapTextureManifest(
    int SchemaVersion,
    string Area,
    int Width,
    int Height,
    string PixelSha256,
    string? PgVersion,
    string? ExtractorVersion);
