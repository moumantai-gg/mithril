namespace Mithril.MapCalibration;

/// <summary>
/// The committed catalogue of canonical (validated-once) per-asset truth keyed
/// by Project Gorgon version: <c>byPgVersion["&lt;pg&gt;"]["&lt;artifactKey&gt;"]
/// = { sha, width, height }</c>. Artifact keys mirror the existing hash gate's
/// format — for map textures, the literal Unity Texture2D name with the
/// <c>Map_</c> prefix (e.g. <c>Map_AreaSerbule</c>); for icons, the sentinel
/// <c>"icons"</c>.
///
/// <para>Schema v1 (pre-#1081) carried bare-string values (sha only); v2 widens
/// to the <see cref="CanonicalAssetHashEntry"/> record. v1 files load via the
/// loader's wrapping fallback (zero dims) — hash-gate consumers continue to read
/// <see cref="CanonicalAssetHashEntry.Sha"/>; dim consumers see 0/0 → catalogue
/// miss → fail-soft. mithril#1081 lifts this type from <c>.Detection.Internal</c>
/// to core so <c>Mithril.Overlay</c> can consume the dim slice without crossing
/// the Detection assembly boundary.</para>
/// </summary>
public sealed record CanonicalAssetHashes(
    int SchemaVersion,
    Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>> ByPgVersion);
