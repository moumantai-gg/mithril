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
/// / <see cref="ImageIo.LoadAlphaMask"/> / <see cref="ImageIo.LoadGrayAndAlpha"/>
/// (System.Drawing), so this lives in tools/ alongside the extractors, off the
/// shipped src/** graph. <c>pixelSha256</c> is over the decompressed channel
/// stream — the same integrity contract the loader re-verifies, and the value
/// the canonical-hash gate compares against. Callers wanting both channels
/// should decode once via <see cref="ImageIo.LoadGrayAndAlpha"/> then feed the
/// pair through <see cref="EmitFromGray"/> + <see cref="EmitAlphaFromGray"/>
/// to avoid re-decoding the PNG twice.</para>
/// </summary>
public static class MapTextureCacheEmitter
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        string texturePngPath, string area, string outDir, string? pgVersion, string? extractorVersion) =>
        EmitFromGray(ImageIo.LoadGray(texturePngPath), area, outDir, pgVersion, extractorVersion);

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
        string texturePngPath, string area, string outDir, string? pgVersion, string? extractorVersion) =>
        EmitAlphaFromGray(ImageIo.LoadAlphaMask(texturePngPath), area, outDir, pgVersion, extractorVersion);

    /// <summary>
    /// Same as <see cref="EmitFromPng"/> but takes a pre-decoded <see cref="GrayImage"/>
    /// so a caller that already has the BGRA buffer (e.g. from
    /// <see cref="ImageIo.LoadGrayAndAlpha"/>) doesn't re-decode the PNG.
    /// </summary>
    public static (string ManifestPath, string PixelSha256) EmitFromGray(
        GrayImage gray, string area, string outDir, string? pgVersion, string? extractorVersion)
    {
        ValidateAreaName(area);
        return WriteCacheFiles(
            gray.Pixels, gray.Width, gray.Height, area, suffix: "", logPrefix: "[emit-texture]",
            outDir, pgVersion, extractorVersion);
    }

    /// <summary>
    /// Same as <see cref="EmitAlphaFromPng"/> but takes a pre-decoded alpha
    /// <see cref="GrayImage"/> so a caller that already has the BGRA buffer
    /// doesn't re-decode the PNG.
    /// </summary>
    public static (string ManifestPath, string PixelSha256)? EmitAlphaFromGray(
        GrayImage alpha, string area, string outDir, string? pgVersion, string? extractorVersion)
    {
        ValidateAreaName(area);
        if (IsAllOpaque(alpha.Pixels))
        {
            // RGB-only Texture2D (RGB24 / DXT1): the decoder synthesised α=255
            // everywhere. Skip emit; consumer's TryGetTextureAlpha returns null
            // and safe-degrades. (mithril#1141 survey: 13 of 79 areas, all
            // outdoor zone overviews, hit this branch.)
            //
            // Pixel-pattern detection rather than source-format threading is the
            // current choice: AssetsTools.NET's DecodeTextureRaw is documented to
            // synthesise α=255 exactly for RGB-only inputs, and the #1141 survey
            // confirmed empirically that the only DXT5 outdoor texture with
            // degenerate alpha (Map_AreaSunVale) has α in [230, 255] — distinct
            // from the all-255 sentinel. If a future decoder upgrade ever
            // produces α=254 for RGB-only sources, the heuristic flips behavior
            // silently; the long-term fix is to thread `TextureFormat` from
            // MapTextureExtractor through to the emitter and decide on format
            // alone. Tracked as a follow-up to #1140.
            Console.WriteLine($"[emit-texture-alpha] {area} has no real alpha channel (α=255 everywhere) — skipping alpha emit.");
            return null;
        }
        return WriteCacheFiles(
            alpha.Pixels, alpha.Width, alpha.Height, area, suffix: "-alpha", logPrefix: "[emit-texture-alpha]",
            outDir, pgVersion, extractorVersion);
    }

    private static (string ManifestPath, string PixelSha256) WriteCacheFiles(
        byte[] pixels, int width, int height, string area, string suffix, string logPrefix,
        string outDir, string? pgVersion, string? extractorVersion)
    {
        Directory.CreateDirectory(outDir);

        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));
        var manifest = new Manifest(SchemaVersion, area, width, height, sha, pgVersion, extractorVersion);
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);

        var manifestPath = Path.Combine(outDir, $"map-texture-{area}{suffix}.json");
        File.WriteAllText(manifestPath, json + "\n", new UTF8Encoding(false));

        var binPath = Path.Combine(outDir, $"map-texture-{area}{suffix}.bin");
        using (var fs = File.Create(binPath))
        using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal))
        {
            deflate.Write(pixels, 0, pixels.Length);
        }

        Console.WriteLine($"{logPrefix} {area} {width}x{height} -> {outDir}");
        Console.WriteLine($"{logPrefix} pixelSha256 = {sha}");
        Console.WriteLine($"{logPrefix} map-texture-{area}{suffix}.bin = {new FileInfo(binPath).Length} bytes (deflated)");
        return (manifestPath, sha);
    }

    // Reject anything that could escape outDir via Path.Combine — directory
    // separators, parent traversal, or rooted paths. The current caller chain
    // (sidecar --asset arg → Player.log "Downloading Map [Map_<X>]" bracket)
    // never produces such names in practice, but the input crosses a process
    // boundary and a hostile Player.log line would otherwise let the emit write
    // outside the cache dir.
    private static void ValidateAreaName(string area)
    {
        if (string.IsNullOrWhiteSpace(area)
            || area != Path.GetFileName(area)
            || area.Contains('/') || area.Contains('\\')
            || area.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(area))
        {
            throw new ArgumentException(
                $"area name '{area}' is not a valid path-segment (must be a bare Texture2D name like 'Map_<X>').",
                nameof(area));
        }
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
