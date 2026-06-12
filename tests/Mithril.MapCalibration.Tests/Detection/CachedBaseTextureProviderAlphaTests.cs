using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1116 (sidecar-rgba-alpha-surface): <see cref="CachedBaseTextureProvider.TryGetTextureAlpha"/>
/// reads the parallel <c>map-texture-&lt;area&gt;-alpha.{json,bin}</c> cache the sidecar populates,
/// mirroring the gray base-texture loader exactly (DeflateStream blob + pixelSha256-verified manifest).
/// </summary>
public sealed class CachedBaseTextureProviderAlphaTests : IDisposable
{
    private readonly string _tmpDir;

    public CachedBaseTextureProviderAlphaTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "mithril1116-alphaprovider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TryGetTextureAlpha_round_trips_bytes()
    {
        // 2x2: half not-floor (alpha=0), half floor (alpha=255).
        var alphaBytes = new byte[] { 0, 0, 255, 255 };
        WriteAlphaCache("Map_TestArea", 2, 2, alphaBytes);

        var provider = new CachedBaseTextureProvider(_tmpDir, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_TestArea");

        alpha.Should().NotBeNull();
        alpha!.Width.Should().Be(2);
        alpha.Height.Should().Be(2);
        alpha.Pixels.Should().Equal(alphaBytes);
    }

    [Fact]
    public void TryGetTextureAlpha_returns_null_when_manifest_absent()
    {
        // No files written.
        var provider = new CachedBaseTextureProvider(_tmpDir, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_Missing");
        alpha.Should().BeNull();
    }

    [Fact]
    public void TryGetTextureAlpha_returns_null_when_blob_absent()
    {
        // Manifest only, no blob.
        var alphaBytes = new byte[] { 0, 255 };
        var sha = Convert.ToHexStringLower(SHA256.HashData(alphaBytes));
        var manifestPath = Path.Combine(_tmpDir, "map-texture-Map_BlobMissing-alpha.json");
        File.WriteAllText(manifestPath, $"{{\"schemaVersion\":1,\"width\":2,\"height\":1,\"pixelSha256\":\"{sha}\"}}");
        // No blob written.

        var provider = new CachedBaseTextureProvider(_tmpDir, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_BlobMissing");
        alpha.Should().BeNull();
    }

    [Fact]
    public void TryGetTextureAlpha_returns_null_on_hash_mismatch()
    {
        // Write a manifest declaring a sha that doesn't match the blob's actual bytes.
        var alphaBytes = new byte[] { 0, 0, 255, 255 };
        var wrongSha = "00000000000000000000000000000000000000000000000000000000000000ff";
        var manifestPath = Path.Combine(_tmpDir, "map-texture-Map_HashBad-alpha.json");
        var blobPath = Path.Combine(_tmpDir, "map-texture-Map_HashBad-alpha.bin");
        File.WriteAllText(manifestPath, $"{{\"schemaVersion\":1,\"width\":2,\"height\":2,\"pixelSha256\":\"{wrongSha}\"}}");
        using (var fs = File.Create(blobPath))
        using (var ds = new DeflateStream(fs, CompressionMode.Compress))
            ds.Write(alphaBytes);

        var provider = new CachedBaseTextureProvider(_tmpDir, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_HashBad");
        alpha.Should().BeNull();
    }

    [Fact]
    public void TryGetTextureAlpha_returns_null_on_size_mismatch()
    {
        // Manifest declares 100x100 but blob is 4 bytes.
        var alphaBytes = new byte[] { 0, 0, 255, 255 };
        var sha = Convert.ToHexStringLower(SHA256.HashData(alphaBytes));
        var manifestPath = Path.Combine(_tmpDir, "map-texture-Map_SizeBad-alpha.json");
        var blobPath = Path.Combine(_tmpDir, "map-texture-Map_SizeBad-alpha.bin");
        File.WriteAllText(manifestPath, $"{{\"schemaVersion\":1,\"width\":100,\"height\":100,\"pixelSha256\":\"{sha}\"}}");
        using (var fs = File.Create(blobPath))
        using (var ds = new DeflateStream(fs, CompressionMode.Compress))
            ds.Write(alphaBytes);

        var provider = new CachedBaseTextureProvider(_tmpDir, hashGate: null, pgVersion: null, logger: null);
        var alpha = provider.TryGetTextureAlpha("Map_SizeBad");
        alpha.Should().BeNull();
    }

    [Fact]
    public void TryGetTextureAlpha_uses_alpha_artifact_key_for_canonical_gate()
    {
        // Build a canonical-hash catalogue where the GRAY entry's sha matches the
        // alpha blob's actual sha. If the gate is called with the wrong artifact key
        // ("Map_GateTest" instead of "Map_GateTest-alpha"), this would spuriously
        // accept. The correct implementation queries the "Map_GateTest-alpha" entry,
        // which we deliberately omit -> accept-with-warn fallback -> returns the
        // GrayImage.
        //
        // The flip side: when an "-alpha" entry exists but with a different sha,
        // the gate rejects the alpha load.

        var alphaBytes = new byte[] { 0, 0, 255, 255 };
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(alphaBytes));
        WriteAlphaCache("Map_GateTest", 2, 2, alphaBytes);

        // Case A: catalogue has only gray entry. The gate should fall through the
        // "artifact absent" branch (accept-with-warn) when asked about "-alpha",
        // and the alpha load succeeds.
        var grayOnlyCatalogue = new CanonicalAssetHashes(
            SchemaVersion: 2,
            ByPgVersion: new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal)
            {
                ["1.0.0"] = new Dictionary<string, CanonicalAssetHashEntry>(StringComparer.Ordinal)
                {
                    ["Map_GateTest"] = new CanonicalAssetHashEntry(Sha: "doesnt-matter", Width: 2, Height: 2),
                },
            });
        var grayOnlyGate = CanonicalAssetHashGate.FromCatalogue(grayOnlyCatalogue);
        var grayOnlyProvider = new CachedBaseTextureProvider(_tmpDir, hashGate: grayOnlyGate, pgVersion: "1.0.0", logger: null);
        grayOnlyProvider.TryGetTextureAlpha("Map_GateTest").Should().NotBeNull(
            "no -alpha entry in catalogue -> accept-with-warn -> alpha load succeeds");

        // Case B: catalogue has an -alpha entry, but with a DIFFERENT sha than the
        // blob produces. The gate rejects -> alpha load returns null.
        var rejectingCatalogue = new CanonicalAssetHashes(
            SchemaVersion: 2,
            ByPgVersion: new Dictionary<string, Dictionary<string, CanonicalAssetHashEntry>>(StringComparer.Ordinal)
            {
                ["1.0.0"] = new Dictionary<string, CanonicalAssetHashEntry>(StringComparer.Ordinal)
                {
                    ["Map_GateTest"]       = new CanonicalAssetHashEntry(Sha: actualSha, Width: 2, Height: 2),
                    ["Map_GateTest-alpha"] = new CanonicalAssetHashEntry(Sha: "deadbeef", Width: 2, Height: 2),
                },
            });
        var rejectingGate = CanonicalAssetHashGate.FromCatalogue(rejectingCatalogue);
        var rejectingProvider = new CachedBaseTextureProvider(_tmpDir, hashGate: rejectingGate, pgVersion: "1.0.0", logger: null);
        rejectingProvider.TryGetTextureAlpha("Map_GateTest").Should().BeNull(
            "alpha entry sha mismatch -> canonical-hash gate rejects -> null");
    }

    private void WriteAlphaCache(string area, int w, int h, byte[] pixels)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));
        var manifestPath = Path.Combine(_tmpDir, $"map-texture-{area}-alpha.json");
        var blobPath = Path.Combine(_tmpDir, $"map-texture-{area}-alpha.bin");
        var manifestJson = $"{{\"schemaVersion\":1,\"width\":{w},\"height\":{h},\"pixelSha256\":\"{sha}\"}}";
        File.WriteAllText(manifestPath, manifestJson);
        using var fs = File.Create(blobPath);
        using var ds = new DeflateStream(fs, CompressionMode.Compress);
        ds.Write(pixels);
    }
}
