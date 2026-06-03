using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Detection.Internal;
using OpenCvSharp;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CachedOrbDescriptorProviderTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "mithril-orb-cache-" + Guid.NewGuid());
    private const string ParamsHash = "deadbeef";
    private const string PixelHash = "facefeed";

    public CachedOrbDescriptorProviderTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }

    [Fact]
    public void Round_trips_write_then_read()
    {
        var (kp, desc) = SampleDescriptors(count: 8);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: "test");

        using var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", PixelHash);

        bundle.Should().NotBeNull();
        bundle!.Keypoints.Length.Should().Be(8);
        bundle.Descriptors.Rows.Should().Be(8);
        bundle.Descriptors.Cols.Should().Be(32);
    }

    [Fact]
    public void Returns_null_on_blob_corruption()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        // Flip a byte in the deflate-compressed blob (almost certainly breaks decompression OR hash).
        var blobPath = Path.Combine(_tmpDir, "map-texture-X.orb.bin");
        var bytes = File.ReadAllBytes(blobPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(blobPath, bytes);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", PixelHash);
        bundle.Should().BeNull();
    }

    [Fact]
    public void Returns_null_on_orb_params_hash_mismatch()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, "different-params-hash").TryRead("X", PixelHash);
        bundle.Should().BeNull();
    }

    [Fact]
    public void Returns_null_on_pixel_sha_mismatch()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", "different-pixel-sha");
        bundle.Should().BeNull();
    }

    private static (KeyPoint[] kp, Mat desc) SampleDescriptors(int count)
    {
        var kp = new KeyPoint[count];
        for (int i = 0; i < count; i++)
            kp[i] = new KeyPoint(new Point2f(i * 10, i * 10), 7f, 0f, 1f, 0, -1);

        var desc = new Mat(count, 32, MatType.CV_8UC1);
        for (int i = 0; i < count; i++)
            for (int j = 0; j < 32; j++)
                desc.Set(i, j, (byte)((i * 31 + j * 17) & 0xFF));
        return (kp, desc);
    }
}
