using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture.Internal;

/// <summary>
/// Reads cached ORB descriptors for a per-area base texture. Returns null
/// on any miss, mismatch, or corruption — caller is responsible for
/// computing+writing them via <c>OrbDescriptorWriter</c>.
/// </summary>
internal sealed class CachedOrbDescriptorProvider
{
    private readonly string _cacheDir;
    private readonly string _orbParamsHash;
    private readonly ILogger? _logger;

    public CachedOrbDescriptorProvider(string cacheDir, string orbParamsHash, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _orbParamsHash = orbParamsHash;
        _logger = logger;
    }

    public OrbDescriptorBundle? TryRead(string areaKey, string expectedTexturePixelSha256)
    {
        if (string.IsNullOrWhiteSpace(_cacheDir) || !Directory.Exists(_cacheDir)) return null;

        var manifestPath = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.json");
        var blobPath     = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.bin");
        if (!File.Exists(manifestPath) || !File.Exists(blobPath)) return null;

        OrbDescriptorManifest? manifest;
        try
        {
            using var s = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(s, CaptureJsonContext.Default.OrbDescriptorManifest);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "ORB descriptor manifest {Path} unparseable — rebuild.", manifestPath);
            return null;
        }
        if (manifest is null) return null;

        if (manifest.OrbParamsHash != _orbParamsHash
            || manifest.PixelSha256 != expectedTexturePixelSha256
            || manifest.SchemaVersion != 1
            || manifest.DescriptorDim != 32)
        {
            _logger?.LogInformation("ORB descriptor cache for {Area} stale — rebuild.", areaKey);
            return null;
        }

        byte[] blob;
        try
        {
            using var stream = File.OpenRead(blobPath);
            using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            deflate.CopyTo(ms);
            blob = ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning(ex, "ORB descriptor blob {Path} corrupt — rebuild.", blobPath);
            return null;
        }

        var actualBlobHash = Convert.ToHexStringLower(SHA256.HashData(blob));
        if (actualBlobHash != manifest.BlobSha256)
        {
            _logger?.LogWarning(
                "ORB descriptor blob hash mismatch for {Area} (manifest {Expected}, blob {Actual}) — rebuild.",
                areaKey, manifest.BlobSha256, actualBlobHash);
            return null;
        }

        return OrbDescriptorBundle.Decode(blob, manifest);
    }
}

/// <summary>
/// Wire format of the .orb.bin blob: per-keypoint header + 32-byte
/// descriptor row. See <see cref="Encode"/> / <see cref="Decode"/> for the
/// concrete layout — the format is private to PR-2's reader + writer.
/// </summary>
internal sealed class OrbDescriptorBundle : IDisposable
{
    public KeyPoint[] Keypoints { get; }
    public Mat Descriptors { get; }   // CV_8UC1, rows = KeypointCount, cols = 32

    private OrbDescriptorBundle(KeyPoint[] keypoints, Mat descriptors)
    {
        Keypoints = keypoints;
        Descriptors = descriptors;
    }

    public void Dispose()
    {
        Descriptors.Dispose();
    }

    public static OrbDescriptorBundle Decode(byte[] blob, OrbDescriptorManifest manifest)
    {
        // Format:
        //   uint32  keypointCount
        //   per keypoint (24 bytes):
        //     float32 x, float32 y, float32 size, float32 angle,
        //     float32 response, int32 octave
        //   then keypointCount × 32 bytes of descriptor data
        if (blob.Length < 4) throw new InvalidDataException("blob too small");
        int n = BitConverter.ToInt32(blob, 0);
        if (n != manifest.KeypointCount)
            throw new InvalidDataException($"blob keypointCount {n} != manifest {manifest.KeypointCount}");

        var keypoints = new KeyPoint[n];
        int offset = 4;
        for (int i = 0; i < n; i++)
        {
            float x        = BitConverter.ToSingle(blob, offset + 0);
            float y        = BitConverter.ToSingle(blob, offset + 4);
            float size     = BitConverter.ToSingle(blob, offset + 8);
            float angle    = BitConverter.ToSingle(blob, offset + 12);
            float response = BitConverter.ToSingle(blob, offset + 16);
            int   octave   = BitConverter.ToInt32 (blob, offset + 20);
            keypoints[i] = new KeyPoint(new Point2f(x, y), size, angle, response, octave, ClassId: -1);
            offset += 24;
        }

        var descriptors = new Mat(n, 32, MatType.CV_8UC1);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                descriptors.Set(i, j, blob[offset + i * 32 + j]);
            }
        }
        return new OrbDescriptorBundle(keypoints, descriptors);
    }

    public static byte[] Encode(KeyPoint[] keypoints, Mat descriptors)
    {
        if (descriptors.Cols != 32 || descriptors.Type() != MatType.CV_8UC1)
            throw new ArgumentException("expected 32-col CV_8UC1 ORB descriptors", nameof(descriptors));

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(keypoints.Length);
        foreach (var kp in keypoints)
        {
            bw.Write(kp.Pt.X);
            bw.Write(kp.Pt.Y);
            bw.Write(kp.Size);
            bw.Write(kp.Angle);
            bw.Write(kp.Response);
            bw.Write(kp.Octave);
        }
        for (int i = 0; i < keypoints.Length; i++)
            for (int j = 0; j < 32; j++)
                bw.Write(descriptors.At<byte>(i, j));
        return ms.ToArray();
    }
}
