using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — invariants on the bundled-data files:
///
/// <list type="number">
/// <item>Every texture-frame baseline row carries a non-null PixelSha256
/// that resolves to a positive-dim entry in the catalogue. A row added
/// without a matching catalogue entry would render empty on the overlay
/// (catalogue miss); the build fails fast at #1081 commit time.</item>
/// <item>The catalogue carries no sha-collision-with-conflicting-dims. The
/// same sha (= same pixel content) must always carry the same dims; a
/// mismatch is a bundling bug (re-harvest went wrong).</item>
/// </list>
/// </summary>
public sealed class BundledCatalogueLintTests
{
    private static CanonicalAssetHashes LoadCatalogue()
    {
        var asm = typeof(CatalogueMapTextureDimensions).Assembly;
        using var stream = asm.GetManifestResourceStream("Mithril.MapCalibration.BundledData.canonical-asset-hashes.json")
            ?? throw new InvalidOperationException("catalogue resource missing");
        return CanonicalAssetHashesLoader.TryLoad(stream, NullLogger.Instance)
            ?? throw new InvalidOperationException("catalogue resource failed to parse");
    }

    [Fact]
    public void EveryTextureFrameBaseline_HasPixelSha256_ResolvingInCatalogue()
    {
        var baseline = BundledBaselineLoader.Load(NullLogger.Instance);
        var catalogue = LoadCatalogue();
        var dims = new CatalogueMapTextureDimensions(catalogue);

        var textureRows = baseline
            .Where(kv => kv.Value.Frame == CalibrationFrame.Texture)
            .ToList();

        textureRows.Should().NotBeEmpty(
            "BundledBaselineLoader stamps Frame=Texture on every row by construction.");

        foreach (var (key, cal) in textureRows)
        {
            cal.PixelSha256.Should().NotBeNullOrWhiteSpace(
                $"bundled row {key} must carry PixelSha256 — see " +
                $"docs/planning/calibration-1081-overlay-cross-frame-composition/spec.md §4.3");

            var resolved = dims.TryGetSizeBySha(cal.PixelSha256);
            resolved.Should().NotBeNull(
                $"bundled row {key}'s PixelSha256 ({cal.PixelSha256}) must resolve in the catalogue " +
                $"with positive dims. A miss means the catalogue is missing this scene's entry — " +
                $"add it under the current PG version's byPgVersion section.");
        }
    }

    [Fact]
    public void Catalogue_HasNoConflictingShaDims()
    {
        var catalogue = LoadCatalogue();

        // Collect every (sha, w, h) tuple; ensure same sha never carries conflicting dims.
        var seen = new Dictionary<string, (int W, int H, string Origin)>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (pg, byArtifact) in catalogue.ByPgVersion)
        {
            foreach (var (key, entry) in byArtifact)
            {
                if (entry.Width <= 0 || entry.Height <= 0) continue;  // v1-wrapped; skip
                var origin = $"byPgVersion[\"{pg}\"][\"{key}\"]";
                if (seen.TryGetValue(entry.Sha, out var prior))
                {
                    (entry.Width, entry.Height).Should().Be((prior.W, prior.H),
                        $"sha {entry.Sha} appears at {prior.Origin} with ({prior.W}x{prior.H}) " +
                        $"AND at {origin} with ({entry.Width}x{entry.Height}). Same pixel content " +
                        $"must yield the same dims; this is a bundling bug — re-harvest from the " +
                        $"sidecar.");
                }
                else
                {
                    seen[entry.Sha] = (entry.Width, entry.Height, origin);
                }
            }
        }
    }
}
