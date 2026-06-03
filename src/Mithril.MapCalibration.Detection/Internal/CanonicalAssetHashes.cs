using System.Collections.Generic;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// The committed catalogue of canonical (validated-once) SHA-256 hashes for the
/// pre-decoded assets the runtime asset-extractor sidecar produces, keyed by the
/// detected Project Gorgon version (issue #931). Publishing a SHA-256 is
/// non-expressive — it reconstructs no PG art — so this file is redistribution-
/// clean and ships embedded, unlike the icon/texture blobs themselves which #931
/// stops shipping.
///
/// <para>Shape: <c>byPgVersion["&lt;pg&gt;"]["icons"|"&lt;AreaKey&gt;"] =
/// "&lt;sha256-lowercase-hex&gt;"</c>. The inner key <c>"icons"</c> covers the
/// icon-template set; an area key (e.g. <c>"AreaSerbule"</c>) covers that area's
/// base texture. <see cref="CanonicalAssetHashGate"/> compares a runtime cache
/// artifact's <c>pixelSha256</c> against the entry for the detected PG version:
/// match → accept; mismatch → reject (decode-tool drift / corruption); PG version
/// absent from the catalogue → accept-with-warn (never hard-fail a newer patch).</para>
/// </summary>
internal sealed record CanonicalAssetHashes(
    int SchemaVersion,
    Dictionary<string, Dictionary<string, string>> ByPgVersion);
