using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Loads pre-decoded icon templates from an on-disk cache directory written by
/// the out-of-process asset-extractor sidecar (issue #931): an
/// <c>icon-templates.json</c> schema-versioned metadata manifest +
/// <c>icon-templates.bin</c> DeflateStream-compressed gray+alpha pixel payload.
/// BCL-only — no System.Drawing, no WPF, no AssetsTools.NET — so the in-process
/// detection core consumes assets without any image decoder. The decoders live
/// only in the sidecar (<c>tools/Mithril.AssetExtractor</c>); the app↔sidecar
/// link is a process boundary, never an assembly reference.
///
/// <para><b>History:</b> before #931 these two files shipped as
/// <c>EmbeddedResource</c>s baked into this assembly (committed PG art). #931
/// removed them; the loader now reads the same manifest+blob format from a cache
/// directory the sidecar populates at runtime, and a canonical-hash gate
/// (<see cref="CanonicalAssetHashGate"/>) re-establishes the "validated input is
/// frozen" property the committed blobs used to give.</para>
///
/// <para><b>Fail-soft:</b> a missing cache dir/file OR a <c>pixelSha256</c>
/// mismatch warns and returns <see cref="IconTemplateSet.Empty"/>. Empty
/// templates → no detections → the confidence gate rejects → no calibration
/// persisted → the system safe-degrades to manual/baseline, never a silent wrong
/// match (the "verify your inputs" guard from the gate study, applied to the
/// runtime-extracted artifact).</para>
/// </summary>
internal static class BundledIconTemplateLoader
{
    private const string ManifestFileName = "icon-templates.json";
    private const string BlobFileName = "icon-templates.bin";

    /// <summary>
    /// Loads + verifies the icon-template set from <paramref name="cacheDir"/>.
    /// Returns <see cref="IconTemplateSet.Empty"/> on any failure (missing dir,
    /// missing file, parse error, hash mismatch, truncation).
    /// </summary>
    public static IconTemplateSet LoadFromDirectory(string cacheDir, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(cacheDir) || !Directory.Exists(cacheDir))
        {
            logger?.LogInformation(
                "Icon-template cache dir {CacheDir} absent — icon templates disabled (safe-degrade until the asset-extractor populates it).",
                cacheDir);
            return IconTemplateSet.Empty;
        }

        var manifestPath = Path.Combine(cacheDir, ManifestFileName);
        var blobPath = Path.Combine(cacheDir, BlobFileName);

        var manifest = ReadManifest(manifestPath, logger);
        if (manifest is null) return IconTemplateSet.Empty;

        var pixels = ReadDecompressedPixels(blobPath, logger);
        if (pixels is null) return IconTemplateSet.Empty;

        // Integrity gate: SHA-256 of the decompressed pixel stream must match the
        // manifest's recorded hash. Guards against manifest↔blob skew / truncation.
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(pixels));
        if (!string.Equals(actualHash, manifest.PixelSha256, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Icon-template pixel hash mismatch (manifest {Expected}, blob {Actual}) — icon templates disabled (safe-degrade).",
                manifest.PixelSha256, actualHash);
            return IconTemplateSet.Empty;
        }

        var templates = new List<IconTemplate>(manifest.Icons.Count);
        int offset = 0;
        foreach (var entry in manifest.Icons)
        {
            int count = entry.Width * entry.Height;
            if (count <= 0 || offset + 2 * count > pixels.Length)
            {
                logger?.LogWarning(
                    "Icon-template blob too short for entry {Name} ({W}x{H}) — icon templates disabled (safe-degrade).",
                    entry.Name, entry.Width, entry.Height);
                return IconTemplateSet.Empty;
            }

            var gray = new byte[count];
            Array.Copy(pixels, offset, gray, 0, count);
            offset += count;
            var alpha = new byte[count];
            Array.Copy(pixels, offset, alpha, 0, count);
            offset += count;

            templates.Add(new IconTemplate(
                entry.Name,
                entry.LandmarkType,
                entry.PivotX,
                entry.PivotY,
                new GrayImage(entry.Width, entry.Height, gray),
                new GrayImage(entry.Width, entry.Height, alpha)));
        }

        logger?.LogInformation("Loaded {Count} icon templates from {CacheDir} (pixelSha256 verified).", templates.Count, cacheDir);
        return new IconTemplateSet(templates);
    }

    /// <summary>
    /// The <c>pixelSha256</c> recorded in the cache manifest under
    /// <paramref name="cacheDir"/>, or <c>null</c> if absent/unverifiable. Used by
    /// the canonical-hash gate to compare the runtime artifact against the
    /// committed catalogue without re-decompressing the blob.
    /// </summary>
    public static string? ManifestPixelSha256(string cacheDir, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(cacheDir)) return null;
        var manifest = ReadManifest(Path.Combine(cacheDir, ManifestFileName), logger);
        return manifest?.PixelSha256;
    }

    private static IconTemplateManifest? ReadManifest(string manifestPath, ILogger? logger)
    {
        if (!File.Exists(manifestPath))
        {
            logger?.LogInformation("Icon-template manifest {Path} not found — icon templates disabled (safe-degrade).", manifestPath);
            return null;
        }
        try
        {
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize(stream, DetectionJsonContext.Default.IconTemplateManifest);
            if (manifest is null || manifest.Icons is null || manifest.Icons.Count == 0 || string.IsNullOrEmpty(manifest.PixelSha256))
            {
                logger?.LogWarning("Icon-template manifest {Path} empty/malformed — icon templates disabled (safe-degrade).", manifestPath);
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Icon-template manifest {Path} failed to parse — icon templates disabled (safe-degrade).", manifestPath);
            return null;
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Icon-template manifest {Path} failed to read — icon templates disabled (safe-degrade).", manifestPath);
            return null;
        }
    }

    private static byte[]? ReadDecompressedPixels(string blobPath, ILogger? logger)
    {
        if (!File.Exists(blobPath))
        {
            logger?.LogInformation("Icon-template blob {Path} not found — icon templates disabled (safe-degrade).", blobPath);
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
            logger?.LogWarning(ex, "Icon-template blob {Path} failed to decompress — icon templates disabled (safe-degrade).", blobPath);
            return null;
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "Icon-template blob {Path} failed to read — icon templates disabled (safe-degrade).", blobPath);
            return null;
        }
    }
}
