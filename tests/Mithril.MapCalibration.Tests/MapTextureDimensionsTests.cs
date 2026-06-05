using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — IMapTextureDimensions is the overlay's content-addressed
/// resolver for "given a base texture's sha, what are its native pixel
/// dimensions?" Backed by the canonical-asset-hash catalogue; the
/// implementation pre-builds a sha→(W,H) index across all PG versions so
/// the lookup is O(1) at render time.
/// </summary>
public sealed class MapTextureDimensionsTests
{
    private static CanonicalAssetHashes Catalogue(params (string Pg, string Key, string Sha, int W, int H)[] entries)
    {
        var byPg = new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal);
        foreach (var (pg, key, sha, w, h) in entries)
        {
            if (!byPg.TryGetValue(pg, out var inner))
                inner = byPg[pg] = new Dictionary<string, CanonicalAssetHashEntry>(StringComparer.Ordinal);
            inner[key] = new CanonicalAssetHashEntry(sha, w, h);
        }
        return new CanonicalAssetHashes(2, byPg);
    }

    [Fact]
    public void KnownSha_ReturnsDims()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024),
            ("467", "Map_AreaEltibule", "def", 2048, 1024)));

        dims.TryGetSizeBySha("abc").Should().Be((1024, 1024));
        dims.TryGetSizeBySha("def").Should().Be((2048, 1024));
    }

    [Fact]
    public void UnknownSha_ReturnsNull()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha("notinhere").Should().BeNull();
    }

    [Fact]
    public void NullOrEmptySha_ReturnsNull()
    {
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha(null).Should().BeNull();
        dims.TryGetSizeBySha("").Should().BeNull();
        dims.TryGetSizeBySha("   ").Should().BeNull();
    }

    [Fact]
    public void ZeroDimEntries_SkippedFromIndex()
    {
        // v1-wrapped entries (loaded with 0/0 dims) must not poison the
        // sha→(W,H) index — they're catalogue misses by construction.
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 0, 0)));

        dims.TryGetSizeBySha("abc").Should().BeNull();
    }

    [Fact]
    public void SameShaAcrossPgVersions_LastWriterWins()
    {
        // Same sha (= same pixel content) under two PG versions must have
        // matching dims (the lint test guarantees this); if a future build
        // somehow ships conflicting dims, the index resolves to whichever
        // entry was enumerated last. Behaviour is observable but not
        // semantically meaningful — both values are equivalently "right"
        // for the sha, and the lint test fails the build before this matters.
        var dims = new CatalogueMapTextureDimensions(Catalogue(
            ("467", "Map_AreaSerbule", "abc", 1024, 1024),
            ("468", "Map_AreaSerbule", "abc", 1024, 1024)));

        dims.TryGetSizeBySha("abc").Should().Be((1024, 1024));
    }

    [Fact]
    public void EmptyCatalogue_AllLookupsNull()
    {
        var dims = new CatalogueMapTextureDimensions(new CanonicalAssetHashes(
            2, new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>()));

        dims.TryGetSizeBySha("abc").Should().BeNull();
    }
}
