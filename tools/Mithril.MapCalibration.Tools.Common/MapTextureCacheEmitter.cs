using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Writes the per-area base-texture cache the runtime
/// <c>CachedBaseTextureProvider</c> consumes (issue #931): a schema-versioned
/// metadata manifest (<c>map-texture-&lt;area&gt;.json</c>) + a DeflateStream-
/// compressed single-channel gray pixel blob (<c>map-texture-&lt;area&gt;.bin</c>).
/// Mirrors <see cref="IconTemplateEmitter"/>'s deflate+SHA pattern.
///
/// <para>Alpha companion (mithril#1140): when the source PNG carries a real
/// alpha channel (some byte != 255), <see cref="EmitAlphaFromPng"/> writes a
/// parallel <c>map-texture-&lt;area&gt;-alpha.{json,bin}</c> pair the consumer
/// (<c>CachedBaseTextureProvider.TryGetTextureAlpha</c> — added in #1139)
/// reads to compute floor-boundary masks for the #1116 deviation-mask fix. The
/// alpha channel is the floor signal for indoor scenes (transparent = not-floor,
/// opaque = floor; verified for all 65 indoor textures in mithril#1141).</para>
///
/// <para>Decoder-side: the input PNG is read via <see cref="ImageIo.LoadGray"/>
/// / <see cref="ImageIo.LoadAlphaMask"/> (System.Drawing), so this lives in
/// tools/ alongside the extractors, off the shipped src/** graph.
/// <c>pixelSha256</c> is over the decompressed channel stream — the same
/// integrity contract the loader re-verifies, and the value the canonical-hash
/// gate compares against.</para>
/// </summary>
public static class MapTextureCacheEmitter
{
    private const int SchemaVersion = 1;

    private sealed record Manifest(
        int SchemaVersion,
        string Area,
        int Width,
        int Height,
        string PixelSha256,
        string? PgVersion,
        string? ExtractorVersion);

    /// <summary>
    /// Converts the extracted base-texture <paramref name="texturePngPath"/> to
    /// the gray-only deflate cache format under <paramref name="outDir"/>. Returns
    /// the written manifest path + the pixelSha256.
    /// </summary>
    public static (string ManifestPath, string PixelSha256) EmitFromPng(
        string texturePngPath,
        string area,
        string outDir,
        string? pgVersion,
        string? extractorVersion)
    {
        Directory.CreateDirectory(outDir);

        var gray = ImageIo.LoadGray(texturePngPath);
        var pixels = gray.Pixels;
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));

        var manifest = new Manifest(SchemaVersion, area, gray.Width, gray.Height, sha, pgVersion, extractorVersion);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var manifestPath = Path.Combine(outDir, $"map-texture-{area}.json");
        File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(false));

        var binPath = Path.Combine(outDir, $"map-texture-{area}.bin");
        using (var fs = File.Create(binPath))
        using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal))
        {
            deflate.Write(pixels, 0, pixels.Length);
        }

        Console.WriteLine($"[emit-texture] {area} {gray.Width}x{gray.Height} -> {outDir}");
        Console.WriteLine($"[emit-texture] pixelSha256 = {sha}");
        Console.WriteLine($"[emit-texture] map-texture-{area}.bin = {new FileInfo(binPath).Length} bytes (deflated)");
        return (manifestPath, sha);
    }

    /// <summary>
    /// Companion to <see cref="EmitFromPng"/>: writes the alpha channel as a
    /// parallel <c>map-texture-&lt;area&gt;-alpha.{json,bin}</c> pair when the
    /// source PNG actually carries alpha (some pixel has α != 255). Returns
    /// <see langword="null"/> when the source has no alpha channel — the
    /// decoder synthesises α=255 for RGB-only Texture2D formats (RGB24 / BC1),
    /// detected here as "every pixel = 255". The consumer
    /// (<c>CachedBaseTextureProvider.TryGetTextureAlpha</c>) safe-degrades on
    /// the missing files; outdoor zones that go through ORB primary never reach
    /// the deviation detector anyway, so the absence is harmless.
    /// </summary>
    /// <returns>The written manifest path + the pixelSha256, or <see langword="null"/>
    /// when the source PNG has no real alpha channel (skip-and-warn path).</returns>
    public static (string ManifestPath, string PixelSha256)? EmitAlphaFromPng(
        string texturePngPath,
        string area,
        string outDir,
        string? pgVersion,
        string? extractorVersion)
    {
        Directory.CreateDirectory(outDir);

        var alpha = ImageIo.LoadAlphaMask(texturePngPath);
        var pixels = alpha.Pixels;
        if (IsAllOpaque(pixels))
        {
            // RGB-only Texture2D (RGB24 / DXT1): the decoder synthesised α=255
            // everywhere. Skip emit; consumer's TryGetTextureAlpha returns null
            // and safe-degrades. (mithril#1141 survey: 13 of 79 areas, all
            // outdoor zone overviews, hit this branch.)
            Console.WriteLine($"[emit-texture-alpha] {area} has no real alpha channel (α=255 everywhere) — skipping alpha emit.");
            return null;
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));

        var manifest = new Manifest(SchemaVersion, area, alpha.Width, alpha.Height, sha, pgVersion, extractorVersion);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var manifestPath = Path.Combine(outDir, $"map-texture-{area}-alpha.json");
        File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(false));

        var binPath = Path.Combine(outDir, $"map-texture-{area}-alpha.bin");
        using (var fs = File.Create(binPath))
        using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal))
        {
            deflate.Write(pixels, 0, pixels.Length);
        }

        Console.WriteLine($"[emit-texture-alpha] {area} {alpha.Width}x{alpha.Height} -> {outDir}");
        Console.WriteLine($"[emit-texture-alpha] pixelSha256 = {sha}");
        Console.WriteLine($"[emit-texture-alpha] map-texture-{area}-alpha.bin = {new FileInfo(binPath).Length} bytes (deflated)");
        return (manifestPath, sha);
    }

    private static bool IsAllOpaque(byte[] alpha)
    {
        for (int i = 0; i < alpha.Length; i++)
        {
            if (alpha[i] != 255) return false;
        }
        return true;
    }
}
