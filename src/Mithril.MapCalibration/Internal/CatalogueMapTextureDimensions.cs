namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Default <see cref="IMapTextureDimensions"/> impl. Pre-builds a sha→(W,H)
/// index across all PG versions in the catalogue so the lookup is O(1) at
/// render path. Zero-dim entries (v1-wrapped catalogue records) are
/// excluded — they signal "uncatalogued; fail-soft."
/// </summary>
internal sealed class CatalogueMapTextureDimensions : IMapTextureDimensions
{
    private readonly IReadOnlyDictionary<string, (int W, int H)> _bySha;

    public CatalogueMapTextureDimensions(CanonicalAssetHashes catalogue)
    {
        var idx = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var byArtifact in catalogue.ByPgVersion.Values)
        {
            foreach (var entry in byArtifact.Values)
            {
                if (entry.Width > 0 && entry.Height > 0)
                {
                    idx[entry.Sha] = (entry.Width, entry.Height);
                }
            }
        }
        _bySha = idx;
    }

    public (int Width, int Height)? TryGetSizeBySha(string? pixelSha256)
    {
        if (string.IsNullOrWhiteSpace(pixelSha256)) return null;
        return _bySha.TryGetValue(pixelSha256!, out var dims) ? (dims.W, dims.H) : null;
    }
}
