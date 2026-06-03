using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Writes per-area ORB descriptors to disk as a sibling pair of
/// <c>map-texture-&lt;area&gt;.orb.json</c> (manifest) +
/// <c>map-texture-&lt;area&gt;.orb.bin</c> (DeflateStream-compressed blob)
/// so subsequent runs can skip the texture-side ORB compute. Reader is
/// <see cref="CachedOrbDescriptorProvider"/>; format owner is
/// <see cref="OrbDescriptorBundle.Encode"/>.
///
/// <para>Fail-soft: any <see cref="IOException"/> on write is logged at
/// Warning and swallowed — the locate path already has the descriptors in
/// memory for this attempt, so a failed cache write only costs a recompute
/// on the next run.</para>
/// </summary>
internal sealed class OrbDescriptorWriter
{
    private readonly string _cacheDir;
    private readonly string _orbParamsHash;
    private readonly ILogger? _logger;

    public OrbDescriptorWriter(string cacheDir, string orbParamsHash, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _orbParamsHash = orbParamsHash;
        _logger = logger;
    }

    public void Write(
        string areaKey, KeyPoint[] keypoints, Mat descriptors,
        string texturePixelSha256, string? pgVersion)
    {
        var blob = OrbDescriptorBundle.Encode(keypoints, descriptors);
        var blobSha = Convert.ToHexStringLower(SHA256.HashData(blob));
        var manifest = new OrbDescriptorManifest(
            SchemaVersion: 1,
            Area: areaKey,
            PgVersion: pgVersion,
            KeypointCount: keypoints.Length,
            DescriptorDim: 32,
            OrbParamsHash: _orbParamsHash,
            PixelSha256: texturePixelSha256,
            BlobSha256: blobSha);

        try
        {
            Directory.CreateDirectory(_cacheDir);
            var manifestPath = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.json");
            var blobPath     = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.bin");

            using (var s = File.Create(manifestPath))
            {
                JsonSerializer.Serialize(s, manifest, DetectionJsonContext.Default.OrbDescriptorManifest);
            }
            using (var s = File.Create(blobPath))
            using (var deflate = new DeflateStream(s, CompressionLevel.Optimal))
            {
                deflate.Write(blob, 0, blob.Length);
            }
            _logger?.LogInformation(
                "Wrote ORB descriptor cache for {Area}: {Count} keypoints, {BlobBytes} bytes deflate-compressed payload.",
                areaKey, keypoints.Length, blob.Length);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Failed to write ORB descriptor cache for {Area}; locate will recompute on next run.", areaKey);
        }
    }
}
