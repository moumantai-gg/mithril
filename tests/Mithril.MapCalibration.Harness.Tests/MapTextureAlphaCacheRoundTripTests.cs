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
/// mithril#1140 byte-parity round-trip for the alpha companion: the REAL
/// <see cref="MapTextureCacheEmitter.EmitAlphaFromPng"/> → the REAL reader
/// (the core's cached base-texture provider, resolved through the public
/// <c>AddMithrilMapCalibrationDetection</c> seam). Sits next to
/// <see cref="MapTextureCacheRoundTripTests"/> — same harness, same wiring —
/// to keep the producer↔consumer contract symmetric across gray and alpha.
///
/// <para>The producer skips emit when the source PNG has α = 255 everywhere
/// (RGB-only Texture2D source). That branch is verified here too; the
/// downstream <c>TryGetTextureAlpha</c> safe-degrade is already covered by the
/// consumer's own unit tests in #1139.</para>
/// </summary>
public sealed class MapTextureAlphaCacheRoundTripTests
{
    private const string Area = "AreaAlphaRoundTrip";
    private const int Width = 12;
    private const int Height = 9;

    // Deterministic alpha pattern that is NEITHER all-0 nor all-255 — so the
    // emitter accepts it and the round-trip byte-equality assertion is sharp.
    private static byte ExpectedAlpha(int x, int y) => (byte)((x * 17 + y * 29) % 256);

    private static string WriteRgbaPng(string dir, Func<int, int, byte> alphaFn)
    {
        var path = Path.Combine(dir, "src.png");
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bmp.SetPixel(x, y, Color.FromArgb(alphaFn(x, y), 128, 128, 128));
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
    public void Emitter_then_reader_round_trips_alpha_dims_and_pixels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril1140-tex-alpha-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteRgbaPng(dir, ExpectedAlpha);
            var result = MapTextureCacheEmitter.EmitAlphaFromPng(
                png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            result.Should().NotBeNull("the source PNG has non-opaque alpha; emit should write the pair");

            var tex = Reader(dir).TryGetTextureAlpha(Area);
            tex.Should().NotBeNull("the real emitter output must load through the real reader");
            tex!.Width.Should().Be(Width);
            tex.Height.Should().Be(Height);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    tex.Pixels[y * Width + x].Should().Be(
                        ExpectedAlpha(x, y), $"alpha pixel ({x},{y}) must survive the deflate+SHA round-trip");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void All_zero_alpha_is_emitted_not_skipped()
    {
        // α=0 everywhere is a real (degenerate) alpha channel; the
        // consumer's TryGetTextureAlpha returns the GrayImage and the downstream
        // mask cache's degenerate-alpha branch (spec §7) handles it. The emitter
        // MUST emit — only α=255 everywhere is the "RGB-only source" sentinel
        // that skips. PR #1145 review raised this as a coverage gap.
        var dir = Path.Combine(Path.GetTempPath(), "mithril1140-tex-alpha-allzero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteRgbaPng(dir, (_, _) => 0);
            var result = MapTextureCacheEmitter.EmitAlphaFromPng(
                png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            result.Should().NotBeNull("α=0 everywhere is a real (degenerate) alpha channel, not the RGB-only sentinel");

            var tex = Reader(dir).TryGetTextureAlpha(Area);
            tex.Should().NotBeNull();
            tex!.Pixels.Should().AllSatisfy(b => b.Should().Be(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Map_../../../escape")]
    [InlineData("Map_with/slash")]
    [InlineData(@"Map_with\backslash")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_area_name_is_rejected(string badArea)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril1140-tex-alpha-badarea-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteRgbaPng(dir, ExpectedAlpha);
            var act = () => MapTextureCacheEmitter.EmitAlphaFromPng(
                png, badArea, dir, pgVersion: "test-1", extractorVersion: "test-1");
            act.Should().Throw<ArgumentException>("path-escaping area names must be rejected before reaching Path.Combine");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Source_with_no_alpha_channel_skips_emit_and_consumer_safe_degrades()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril1140-tex-alpha-rgb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Simulate an RGB-only Texture2D round-trip: the decoder synthesises
            // α=255 for every pixel. The emitter must detect this and skip emit.
            var png = WriteRgbaPng(dir, (_, _) => 255);
            var result = MapTextureCacheEmitter.EmitAlphaFromPng(
                png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            result.Should().BeNull("an all-255 alpha channel is the no-real-alpha sentinel");

            File.Exists(Path.Combine(dir, $"map-texture-{Area}-alpha.json")).Should().BeFalse();
            File.Exists(Path.Combine(dir, $"map-texture-{Area}-alpha.bin")).Should().BeFalse();

            // Consumer safe-degrade: TryGetTextureAlpha returns null when no
            // alpha files exist. (The #1139 consumer-side unit tests cover this
            // exhaustively; we just spot-check it here to anchor the contract.)
            Reader(dir).TryGetTextureAlpha(Area).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tampered_alpha_blob_is_rejected_by_the_sha_gate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril1140-tex-alpha-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = WriteRgbaPng(dir, ExpectedAlpha);
            MapTextureCacheEmitter.EmitAlphaFromPng(png, Area, dir, pgVersion: "test-1", extractorVersion: "test-1");

            Reader(dir).TryGetTextureAlpha(Area).Should().NotBeNull();

            var binPath = Path.Combine(dir, $"map-texture-{Area}-alpha.bin");
            var bytes = File.ReadAllBytes(binPath);
            bytes.Should().NotBeEmpty();
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(binPath, bytes);

            Reader(dir).TryGetTextureAlpha(Area).Should().BeNull(
                "a tampered alpha blob must fail the SHA gate (safe-degrade, never a silent wrong mask)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
