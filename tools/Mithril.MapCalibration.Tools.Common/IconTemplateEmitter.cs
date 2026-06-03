using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Writes the bundled pre-decoded icon templates the runtime
/// <c>BundledIconTemplateLoader</c> consumes: a diffable schema-versioned
/// metadata manifest (<c>icon-templates.json</c>), a DeflateStream-compressed
/// gray+alpha pixel blob (<c>icon-templates.bin</c>), and PNG sidecars for
/// visual inspection. This is the <b>only</b> place AssetsTools.NET is touched
/// (via <see cref="IconTemplateExtractor"/>) — it stays in tools/, off the slnx
/// core, so the shipped runtime needs no image decoder and no asset-bundle
/// reader.
///
/// <para>Two sources: <see cref="EmitFromIcons"/> reads real extracted icon PNGs
/// (the canonical regen path, needs a PG install + classdata.tpk once);
/// <see cref="EmitSynthetic"/> generates deterministic teardrop templates with no
/// PG dependency (CI-friendly placeholder + the shape the synthetic end-to-end
/// test relies on).</para>
/// </summary>
public static class IconTemplateEmitter
{
    private const int SchemaVersion = 1;

    // The four landmark icon types the detector pairs against (mirrors
    // IconTemplateExtractor.LandmarkIcons). Name → (LandmarkType, synthetic dims).
    private static readonly (string Name, string LandmarkType, int W, int H)[] Landmarks =
    [
        ("landmark_telepad", CanonicalLandmarkTypes.TeleportationPlatform, 28, 22),
        ("landmark_medipillar", CanonicalLandmarkTypes.MeditationPillar, 18, 40),
        ("landmark_portal", CanonicalLandmarkTypes.Portal, 24, 32),
        ("landmark_npc", CanonicalLandmarkTypes.Npc, 17, 16),
    ];

    private sealed record Decoded(string Name, string LandmarkType, double PivotX, double PivotY, int W, int H, byte[] Gray, byte[] Alpha);

    private sealed record ManifestEntry(string Name, string LandmarkType, double PivotX, double PivotY, int Width, int Height);

    private sealed record Manifest(
        int SchemaVersion,
        string PixelSha256,
        List<ManifestEntry> Icons,
        string? PgVersion,
        string? ExtractorVersion);

    /// <summary>Real-PG path: load the extracted icon PNGs from <paramref name="iconsDir"/>.</summary>
    public static void EmitFromIcons(string iconsDir, string outDir)
        => EmitFromIcons(iconsDir, outDir, pgVersion: null, extractorVersion: null);

    /// <summary>
    /// Real-PG path with version stamps (issue #931): load the extracted icon PNGs
    /// and write the manifest+blob cache, stamping <paramref name="pgVersion"/> +
    /// <paramref name="extractorVersion"/> into the manifest (the cache-
    /// invalidation / canonical-hash keys). Returns the pixelSha256 over the
    /// decompressed gray+alpha stream — the value the sidecar reports on stdout.
    /// </summary>
    public static string EmitFromIcons(string iconsDir, string outDir, string? pgVersion, string? extractorVersion)
    {
        var index = IconTemplateExtractor.Load(iconsDir);
        var decoded = new List<Decoded>();
        foreach (var icon in index.Icons)
        {
            var path = Path.Combine(iconsDir, icon.File);
            if (!File.Exists(path)) continue;
            var (g, a) = ImageIo.LoadGrayAndAlpha(path);
            decoded.Add(new Decoded(icon.Name, icon.LandmarkType, icon.PivotX, icon.PivotY, g.Width, g.Height, g.Pixels, a.Pixels));
        }
        if (decoded.Count == 0)
            throw new UserFacingException($"no icons loaded from {iconsDir}; run --phase extract-icons first");
        return Write(decoded, outDir, pgVersion, extractorVersion);
    }

    /// <summary>
    /// PG-free path: deterministic teardrop templates for the synthetic
    /// end-to-end test (no PG install). The synthetic pin icons (telepad, portal)
    /// carry pivot (0.5, 0) — bottom-tip anchored — to exercise the pivot
    /// correction with a non-trivial offset; medipillar/npc use (0.5, 0.5).
    /// NOTE: this is a deliberate test fixture, NOT PG's real pivots. The real
    /// shipped sprites are centered (0.5, 0.5) for all four — see the real-PG
    /// path in <see cref="EmitFromIcons"/> and #916.
    /// </summary>
    public static void EmitSynthetic(string outDir)
    {
        var decoded = new List<Decoded>();
        foreach (var (name, type, w, h) in Landmarks)
        {
            var (gray, alpha) = Teardrop(w, h, luminance: 150);
            // Pin-shaped icons anchor at the bottom tip; the two pillar/npc
            // markers anchor centrally.
            bool pin = name is "landmark_telepad" or "landmark_portal";
            double pivotY = pin ? 0.0 : 0.5;
            decoded.Add(new Decoded(name, type, 0.5, pivotY, w, h, gray, alpha));
        }
        Write(decoded, outDir, pgVersion: null, extractorVersion: null);
    }

    private static string Write(List<Decoded> decoded, string outDir, string? pgVersion, string? extractorVersion)
    {
        Directory.CreateDirectory(outDir);

        // Assemble the pixel stream: per icon, in manifest order, w*h gray bytes
        // then w*h alpha bytes.
        using var pixelMs = new MemoryStream();
        foreach (var d in decoded)
        {
            pixelMs.Write(d.Gray, 0, d.Gray.Length);
            pixelMs.Write(d.Alpha, 0, d.Alpha.Length);
        }
        var pixels = pixelMs.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(pixels));

        var manifest = new Manifest(
            SchemaVersion,
            sha,
            decoded.Select(d => new ManifestEntry(d.Name, d.LandmarkType, d.PivotX, d.PivotY, d.W, d.H)).ToList(),
            pgVersion,
            extractorVersion);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(Path.Combine(outDir, "icon-templates.json"), json + "\n", new UTF8Encoding(false));

        // Deflate-compress the pixel stream. Default compression level; the
        // runtime loader inflates it via DeflateStream and re-hashes.
        var binPath = Path.Combine(outDir, "icon-templates.bin");
        using (var fs = File.Create(binPath))
        using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal))
        {
            deflate.Write(pixels, 0, pixels.Length);
        }

        // PNG sidecars for visual inspection.
        foreach (var d in decoded)
        {
            ImageIo.SaveGrayPng(new GrayImage(d.W, d.H, d.Gray), Path.Combine(outDir, d.Name + ".gray.png"));
            ImageIo.SaveGrayPng(new GrayImage(d.W, d.H, d.Alpha), Path.Combine(outDir, d.Name + ".alpha.png"));
        }

        Console.WriteLine($"[emit-templates] {decoded.Count} icons -> {outDir}");
        Console.WriteLine($"[emit-templates] pixelSha256 = {sha}");
        Console.WriteLine($"[emit-templates] icon-templates.bin = {new FileInfo(binPath).Length} bytes (deflated)");
        return sha;
    }

    // Two-tone teardrop: filled interior + darker 1-px outline, transparent
    // outside. The outline gives NCC the spatial signal a constant interior
    // can't (zero variance → undefined NCC).
    private static (byte[] Gray, byte[] Alpha) Teardrop(int width, int height, int luminance)
    {
        int outlineLum = Math.Max(0, luminance - 60);
        var gray = new byte[width * height];
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int kind = TeardropPixel(x, y, width, height);
                int idx = y * width + x;
                if (kind == 0)
                {
                    gray[idx] = 0;
                    alpha[idx] = 0;
                }
                else
                {
                    gray[idx] = (byte)(kind == 1 ? outlineLum : luminance);
                    alpha[idx] = 255;
                }
            }
        return (gray, alpha);
    }

    // 0 = outside, 1 = outline ring, 2 = fill interior.
    private static int TeardropPixel(int x, int y, int width, int height)
    {
        double cx = (width - 1) / 2.0;
        double radius = width / 2.5;
        double circleCy = radius + 1;
        bool inShape, inInterior;
        if (y <= circleCy + 1)
        {
            double dx = x - cx;
            double dy = y - circleCy;
            double r2 = dx * dx + dy * dy;
            inShape = r2 <= radius * radius;
            inInterior = r2 <= (radius - 1.2) * (radius - 1.2);
        }
        else
        {
            double t = (y - circleCy) / (height - 1 - circleCy);
            double halfW = radius * (1.0 - t);
            inShape = Math.Abs(x - cx) <= halfW;
            inInterior = Math.Abs(x - cx) <= halfW - 1.0;
        }
        return inShape ? (inInterior ? 2 : 1) : 0;
    }
}
