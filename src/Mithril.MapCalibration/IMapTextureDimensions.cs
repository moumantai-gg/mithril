namespace Mithril.MapCalibration;

/// <summary>
/// Content-addressed resolver for base-texture pixel dimensions, backed by the
/// canonical-asset-hash catalogue. The overlay's per-frame composer
/// (mithril#1081 / <see cref="WorldToTextureCalibration.ProjectThroughOverlay"/>)
/// queries this with the calibration record's stamped PixelSha256 (added by
/// mithril#1081 Task 5 to <see cref="AreaCalibration"/>) to build the
/// <see cref="MapRect"/> describing where the base texture renders on the
/// overlay surface.
///
/// <para>Catalogue maintenance: ships in
/// <c>BundledData/canonical-asset-hashes.json</c>, refreshed per Mithril
/// release alongside the existing canonical-hash gate. PG-version-agnostic
/// at the lookup layer (same sha = same pixel content = same dims by
/// definition).</para>
/// </summary>
public interface IMapTextureDimensions
{
    /// <summary>Look up the canonical (width, height) for a texture by its
    /// SHA-256 (lowercase hex). Returns null when:
    /// <list type="bullet">
    /// <item><paramref name="pixelSha256"/> is null/empty (e.g. a pre-#1081
    /// calibration record);</item>
    /// <item>the catalogue has no entry for the sha (newer PG patch than
    /// Mithril release, or an uncatalogued asset);</item>
    /// <item>the entry exists but carries zero dims (a v1-wrapped catalogue
    /// entry — same fail-soft as a real miss).</item>
    /// </list>
    /// Fail-soft by design — the overlay treats null as "skip this scene
    /// this frame," matching the existing hash-gate's accept-with-warn
    /// posture for uncatalogued assets.</summary>
    (int Width, int Height)? TryGetSizeBySha(string? pixelSha256);
}
