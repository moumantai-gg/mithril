using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Reads a per-area base map texture from the on-disk cache the asset-extractor
/// sidecar populates (issue #931): a <c>map-texture-&lt;area&gt;.json</c>
/// schema-versioned manifest + <c>map-texture-&lt;area&gt;.bin</c> DeflateStream-
/// compressed gray pixel payload. BCL-only — mirrors the icon-template loader's
/// parse → decompress → SHA-256-verify → <see cref="GrayImage"/> path, gray-only
/// (no alpha channel).
///
/// <para>The cache directory is supplied by the caller (the consumer resolves
/// <c>%LocalAppData%/Mithril/assets/</c> or wherever it points the sidecar);
/// this type never hardcodes a location. An optional
/// <see cref="CanonicalAssetHashGate"/> rejects an artifact whose
/// <c>pixelSha256</c> doesn't match the committed catalogue for the detected PG
/// version (decode-tool drift / corruption).</para>
///
/// <para><b>Fail-soft:</b> any miss → <c>null</c> (no detections → gate rejects →
/// safe-degrade), never a silent wrong texture.</para>
/// </summary>
internal sealed class CachedBaseTextureProvider : IBaseTextureProvider
{
    private readonly string _cacheDir;
    private readonly CanonicalAssetHashGate? _hashGate;
    private readonly string? _pgVersion;
    private readonly ILogger? _logger;

    /// <param name="cacheDir">Directory holding <c>map-texture-&lt;area&gt;.{json,bin}</c>.</param>
    /// <param name="hashGate">Optional canonical-hash gate; when supplied, an artifact
    /// failing the gate is rejected (returns <c>null</c>).</param>
    /// <param name="pgVersion">The detected PG version, used as the hash-gate lookup key.</param>
    public CachedBaseTextureProvider(
        string cacheDir,
        CanonicalAssetHashGate? hashGate = null,
        string? pgVersion = null,
        ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _hashGate = hashGate;
        _pgVersion = pgVersion;
        _logger = logger;
    }

    // mithril#1116 Task 1: alpha-surface stub. Real implementation (parallel
    // map-texture-<area>-alpha.{json,bin} cache reader) lands in Task 2.
    public GrayImage? TryGetTextureAlpha(string mapAssetKey) => null;

    public GrayImage? TryGetBaseTexture(string mapAssetKey)
    {
        if (string.IsNullOrWhiteSpace(mapAssetKey))
            return null;
        if (string.IsNullOrWhiteSpace(_cacheDir) || !Directory.Exists(_cacheDir))
        {
            _logger?.LogInformation(
                "Base-texture cache dir {CacheDir} absent — no base texture for {MapAsset} (safe-degrade).",
                _cacheDir, mapAssetKey);
            return null;
        }

        var manifestPath = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}.json");
        var blobPath = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}.bin");

        var manifest = ReadManifest(manifestPath, mapAssetKey);
        if (manifest is null) return null;

        var pixels = ReadDecompressedPixels(blobPath, mapAssetKey);
        if (pixels is null) return null;

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
        if (!string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "Base-texture pixel hash mismatch for {MapAsset} (manifest {Expected}, blob {Actual}) — base texture rejected (safe-degrade).",
                mapAssetKey, manifest.PixelSha256, actualHash);
            return null;
        }

        int count = manifest.Width * manifest.Height;
        if (count <= 0 || pixels.Length != count)
        {
            _logger?.LogWarning(
                "Base-texture blob length {Len} != width*height={Expected} for {MapAsset} — base texture rejected (safe-degrade).",
                pixels.Length, count, mapAssetKey);
            return null;
        }

        // Canonical-hash gate (decode-tool drift / corruption). Absent gate → trust
        // the within-cache integrity check above + lean on the confidence gate.
        if (_hashGate is not null)
        {
            var verdict = _hashGate.Check(_pgVersion, mapAssetKey, manifest.PixelSha256);
            if (!verdict.Accepted)
            {
                _logger?.LogWarning(
                    "Base-texture for {MapAsset} rejected by canonical-hash gate: {Reason} — base texture rejected (safe-degrade).",
                    mapAssetKey, verdict.Reason);
                return null;
            }
        }

        _logger?.LogInformation("Loaded base texture for {MapAsset} ({W}x{H}) from {CacheDir} (pixelSha256 verified).",
            mapAssetKey, manifest.Width, manifest.Height, _cacheDir);
        return new GrayImage(manifest.Width, manifest.Height, pixels);
    }

    private MapTextureManifest? ReadManifest(string manifestPath, string mapAssetKey)
    {
        if (!File.Exists(manifestPath))
        {
            _logger?.LogInformation("Base-texture manifest {Path} not found — no base texture for {MapAsset} (safe-degrade).", manifestPath, mapAssetKey);
            return null;
        }
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize(stream, DetectionJsonContext.Default.MapTextureManifest);
            if (manifest is null || string.IsNullOrEmpty(manifest.PixelSha256) || manifest.Width <= 0 || manifest.Height <= 0)
            {
                _logger?.LogWarning("Base-texture manifest {Path} empty/malformed — no base texture for {MapAsset} (safe-degrade).", manifestPath, mapAssetKey);
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Base-texture manifest {Path} failed to parse — no base texture for {MapAsset} (safe-degrade).", manifestPath, mapAssetKey);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Base-texture manifest {Path} failed to read — no base texture for {MapAsset} (safe-degrade).", manifestPath, mapAssetKey);
            return null;
        }
    }

    private byte[]? ReadDecompressedPixels(string blobPath, string mapAssetKey)
    {
        if (!File.Exists(blobPath))
        {
            _logger?.LogInformation("Base-texture blob {Path} not found — no base texture for {MapAsset} (safe-degrade).", blobPath, mapAssetKey);
            return null;
        }
        try
        {
            using var stream = File.OpenRead(blobPath);
            using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            deflate.CopyTo(ms);
            return ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning(ex, "Base-texture blob {Path} failed to decompress — no base texture for {MapAsset} (safe-degrade).", blobPath, mapAssetKey);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Base-texture blob {Path} failed to read — no base texture for {MapAsset} (safe-degrade).", blobPath, mapAssetKey);
            return null;
        }
    }
}
