using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
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
