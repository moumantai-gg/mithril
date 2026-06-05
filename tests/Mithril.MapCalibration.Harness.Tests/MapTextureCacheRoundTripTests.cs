using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.DependencyInjection;
using Mithril.Tools.MapCalibration.Common;
using Xunit;

namespace Mithril.Tools.MapCalibration.Harness.Tests;

/// <summary>
/// #931 byte-parity round-trip: the REAL texture-cache writer
/// (<see cref="MapTextureCacheEmitter"/> in Tools.Common) → the REAL reader
/// (the core's cached base-texture provider, resolved through the public
/// <c>AddMithrilMapCalibrationDetection</c> seam). Lives in the harness test project
/// because <see cref="MapTextureCacheEmitter"/> is in Tools.Common, which must
/// stay out of the decoder-free core test suite (issue #921); this project
/// already references Tools.Common (via the Harness lib) and runs in the isolated
/// CI step.
///
/// <para>The reader is driven through the public <see cref="IBaseTextureProvider"/>
/// seam (not the internal <c>CachedBaseTextureProvider</c>), so no
/// <c>InternalsVisibleTo</c> widening is needed. This is the only test that
/// exercises the texture writer↔reader against the real emitted on-disk format —
/// a future divergence between the two now fails red instead of degrading
/// silently.</para>
/// </summary>
public sealed class MapTextureCacheRoundTripTests
{
    private const string Area = "AreaRoundTrip";
    private const int Width = 12;
    private const int Height = 9;

    // Deterministic gray ramp; R=G=B so BT.601 luma == the channel value (equal
    // channels make the round-trip byte-exact regardless of luma rounding).
    private static byte ExpectedLuma(int x, int y) => (byte)((x * 7 + y * 13) % 256);

    private static string WriteGrayPng(string dir)
    {
        var path = Path.Combine(dir, "src.png");
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = ExpectedLuma(x, y);
                bmp.SetPixel(x, y, Color.FromArgb(255, v, v, v));
            }
        }
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    private static IBaseTextureProvider Reader(string cacheDir) =>
        new ServiceCollection()
            .AddMithrilMapCalibrationDetection(cacheDir)
            .BuildServiceProvider()
            .GetRequiredService<IBaseTextureProvider>();

    [Fact]
    public void Emitter_then_reader_round_trips_dims_and_pixels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril931-tex-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteGrayPng(dir);
            MapTextureCacheEmitter.EmitFromPng(png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            var tex = Reader(dir).TryGetBaseTexture(Area);

            tex.Should().NotBeNull("the real emitter output must load through the real reader");
            tex!.Width.Should().Be(Width);
            tex.Height.Should().Be(Height);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    tex.Pixels[y * Width + x].Should().Be(
                        ExpectedLuma(x, y), $"pixel ({x},{y}) must survive the deflate+SHA round-trip");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tampered_blob_is_rejected_by_the_sha_gate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril931-tex-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteGrayPng(dir);
            MapTextureCacheEmitter.EmitFromPng(png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            // Sanity: loads clean before tampering.
            Reader(dir).TryGetBaseTexture(Area).Should().NotBeNull();

            // Flip one byte in the REAL emitted .bin → the manifest pixelSha256 (over
            // the decompressed stream) no longer matches → the reader must reject it.
            var binPath = Path.Combine(dir, $"map-texture-{Area}.bin");
            var bytes = File.ReadAllBytes(binPath);
            bytes.Should().NotBeEmpty();
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(binPath, bytes);

            var tex = Reader(dir).TryGetBaseTexture(Area);

            tex.Should().BeNull("a tampered blob must fail the SHA gate (safe-degrade, never a silent wrong texture)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_area_returns_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril931-tex-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteGrayPng(dir);
            MapTextureCacheEmitter.EmitFromPng(png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            // A different area key has no cache files → null.
            Reader(dir).TryGetBaseTexture("AreaNotEmitted").Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
