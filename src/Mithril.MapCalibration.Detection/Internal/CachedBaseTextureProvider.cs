using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Diagnostics;

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

    // mithril#1116: parallel alpha-surface cache reader. Mirrors
    // TryGetBaseTexture exactly but reads map-texture-<area>-alpha.{json,bin}
    // and asks the canonical-hash gate about "<area>-alpha" (same Check API).
    //
    // Telemetry: the body is wrapped in a `texture.alpha.load` span on
    // MapCalibrationDiagnostics.ActivitySource. Outcome tags distinguish
    // success vs each rejection branch so a Seq/OTLP triager can split
    // "missing manifest" / "missing blob" / "hash mismatch" / "size mismatch"
    // / "gate reject" without parsing logs. The sibling gray path
    // (TryGetBaseTexture) is not yet span-instrumented — symmetry is a
    // follow-up; this PR only adds observability to the new alpha path.
    public GrayImage? TryGetTextureAlpha(string mapAssetKey)
    {
        using var span = MapCalibrationDiagnostics.ActivitySource.StartActivity("texture.alpha.load");
        span?.SetTag("area", mapAssetKey);

        if (string.IsNullOrWhiteSpace(mapAssetKey))
        {
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "empty_key");
            return null;
        }
        if (string.IsNullOrWhiteSpace(_cacheDir) || !Directory.Exists(_cacheDir))
        {
            _logger?.LogInformation(
                "Base-texture cache dir {CacheDir} absent — no alpha for {MapAsset} (safe-degrade).",
                _cacheDir, mapAssetKey);
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "cache_dir_absent");
            return null;
        }

        var manifestPath = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}-alpha.json");
        var blobPath = Path.Combine(_cacheDir, $"map-texture-{mapAssetKey}-alpha.bin");

        var manifest = ReadManifest(manifestPath, mapAssetKey);
        if (manifest is null)
        {
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "manifest_missing");
            return null;
        }

        var pixels = ReadDecompressedPixels(blobPath, mapAssetKey);
        if (pixels is null)
        {
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "blob_missing");
            return null;
        }

        var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
        if (!string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "Alpha pixel hash mismatch for {MapAsset} (manifest {Expected}, blob {Actual}) — alpha rejected (safe-degrade).",
                mapAssetKey, manifest.PixelSha256, actualHash);
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "hash_mismatch");
            return null;
        }

        int count = manifest.Width * manifest.Height;
        if (count <= 0 || pixels.Length != count)
        {
            _logger?.LogWarning(
                "Alpha blob length {Len} != width*height={Expected} for {MapAsset} — alpha rejected (safe-degrade).",
                pixels.Length, count, mapAssetKey);
            span?.SetTag("texture.alpha.available", false);
            span?.SetTag("texture.alpha.rejected", "size_mismatch");
            return null;
        }

        // Canonical-hash gate (decode-tool drift / corruption). Use the existing
        // Check(...) API with the "<area>-alpha" artifact key convention — no
        // new gate method needed.
        if (_hashGate is not null)
        {
            var verdict = _hashGate.Check(_pgVersion, $"{mapAssetKey}-alpha", manifest.PixelSha256);
            if (!verdict.Accepted)
            {
                _logger?.LogWarning(
                    "Alpha for {MapAsset} rejected by canonical-hash gate: {Reason} — alpha rejected (safe-degrade).",
                    mapAssetKey, verdict.Reason);
                span?.SetTag("texture.alpha.available", false);
                span?.SetTag("texture.alpha.rejected", "canonical_gate_reject");
                return null;
            }
        }

        _logger?.LogInformation("Loaded alpha for {MapAsset} ({W}x{H}) from {CacheDir} (pixelSha256 verified).",
            mapAssetKey, manifest.Width, manifest.Height, _cacheDir);
        span?.SetTag("texture.alpha.available", true);
        return new GrayImage(manifest.Width, manifest.Height, pixels);
    }

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
