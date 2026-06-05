namespace Mithril.MapCalibration;

/// <summary>
/// One catalogue entry: the canonical SHA-256 (lowercase hex) and native pixel
/// dimensions of a base texture, harvested from the asset-extractor sidecar's
/// <c>map-texture-&lt;X&gt;.json</c> manifest at Mithril release time. The hash
/// gate (<c>Mithril.MapCalibration.Detection.Internal.CanonicalAssetHashGate</c>)
/// reads <see cref="Sha"/>; the overlay's dim resolver
/// (<see cref="IMapTextureDimensions"/>) reads <see cref="Width"/> +
/// <see cref="Height"/>. One catalogue, two consumers. mithril#1081 Schema v2.
/// </summary>
public sealed record CanonicalAssetHashEntry(
    string Sha,
    int    Width,
    int    Height);
