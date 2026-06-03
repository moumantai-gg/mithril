using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Extracts the named landmark + player-pin icon Texture2Ds from
/// <c>sharedassets0.assets</c> as PNGs, alongside an <c>index.json</c> sidecar
/// recording each icon's <see cref="IconMeta.PivotX"/> / <see cref="IconMeta.PivotY"/>
/// from the matching <c>Sprite</c> asset.
///
/// <para>The pivot is recorded per-sprite so the consumer can recover the
/// world-anchor pixel as centre + (w*(pivot.x-0.5), h*(0.5-pivot.y)). PG's real
/// landmark sprites turn out to be pivot ≈ (0.5, 0.5) — centered — re-confirmed
/// fresh from the live sharedassets0.assets with classdata.tpk (#916: all four
/// icons pivot=(0.50,0.50)); the earlier "teardrop bottom-tip, pivot ≈ (0.5, 0)"
/// reading was wrong about the authored data. The correction stays general (it
/// reads whatever m_Pivot the sprite carries) so an off-centre future sprite is
/// handled without a code change.</para>
///
/// <para>sharedassets0.assets is type-tree-stripped, so AssetsTools.NET can't
/// decode <c>m_Pivot</c> without a Unity 6000.3 <c>classdata.tpk</c> loaded via
/// <see cref="AssetsManager.LoadClassPackage"/>. The user supplies that — the
/// tool errors with the download URL if missing.</para>
/// </summary>
public static class IconTemplateExtractor
{
    // Bump when the extracted PNG bytes or sidecar shape change in a way that
    // requires re-extracting from sharedassets0.assets (e.g., the vertical-flip
    // fix changed icon orientation). Stored in index.json; mismatch with the
    // cached value forces a full re-extract.
    private const int CacheFormatVersion = 3;


    // Listed in spike doc §"Tangential positive". Maps to landmarks.json Type
    // discriminator (or "Player"/"Pet" for the player-self pins).
    private static readonly (string TextureName, string LandmarkType)[] LandmarkIcons =
    [
        ("landmark_telepad", CanonicalLandmarkTypes.TeleportationPlatform),
        ("landmark_medipillar", CanonicalLandmarkTypes.MeditationPillar),
        ("landmark_portal", CanonicalLandmarkTypes.Portal),
        ("landmark_npc", CanonicalLandmarkTypes.Npc),
        // landmark_star is generic waypoint; skip for v1 — no Type to match on.
    ];

    // Player-pin variants are intentionally NOT extracted in v1.
    //
    // PG's player pin rotates 0..360° to indicate the player's facing
    // direction (user observation 2026-05-29). Single-scale NCC against a
    // fixed-orientation template can't reliably match a rotated render;
    // residual scores collapse for any non-trivial rotation. Sweeping 36
    // angles per variant × ~18 variants would 600× the detection cost for
    // a reference point whose value is only an anchor (the solver already
    // converges on landmarks alone for any area with >= 2 of them).
    //
    // A future revision can re-enable a single user-chosen variant via
    // --player-pin + multi-angle sweep when the residual without the player
    // anchor proves insufficient. For now the cleanest path is: solve from
    // landmarks only; --player-coord becomes documentation.
    private static readonly string[] PlayerPinPrefixes = [];

    public static void EnsureExtracted(string pgInstall, string iconsDir, string tpkPath)
    {
        Directory.CreateDirectory(iconsDir);
        var indexPath = Path.Combine(iconsDir, "index.json");
        if (File.Exists(indexPath))
        {
            // Freshness: re-extract when (a) sharedassets0 has been touched
            // (PG patch) or (b) the cached format version is behind the current
            // extractor (e.g., the orientation-flip fix changed PNG bytes).
            var sharedAssetsPath = SteamInstall.ResolveSharedAssets0(pgInstall);
            var srcMtime = File.GetLastWriteTimeUtc(sharedAssetsPath);
            var cacheMtime = File.GetLastWriteTimeUtc(indexPath);
            int cachedVersion = TryReadCachedFormatVersion(indexPath);
            if (cacheMtime >= srcMtime && cachedVersion == CacheFormatVersion)
            {
                Console.WriteLine($"[icons] cache fresh in {iconsDir} (v{cachedVersion}, skipping extract)");
                return;
            }
            if (cachedVersion != CacheFormatVersion)
            {
                Console.WriteLine($"[icons] cache format v{cachedVersion} != current v{CacheFormatVersion}; re-extracting");
            }
            else
            {
                Console.WriteLine($"[icons] cache stale (PG patched since {cacheMtime:s}); re-extracting");
            }
        }

        if (!File.Exists(tpkPath))
        {
            throw new UserFacingException($"""
                classdata.tpk not found at: {tpkPath}

                sharedassets0.assets is type-tree-stripped so AssetsTools.NET needs a
                class-data package to decode the Texture2D + Sprite assets. The canonical
                build ships in the UABEA repo (covers all Unity versions including 6000.x);
                the AssetsTools.NET releases page does NOT attach it directly.

                Download (~290 KB):
                  https://github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk

                Place it at the path above, or pass --tpk <path>.
                """);
        }

        var sharedAssetsPath2 = SteamInstall.ResolveSharedAssets0(pgInstall);
        Console.WriteLine($"[icons] decoding {sharedAssetsPath2} via {Path.GetFileName(tpkPath)}");

        var manager = new AssetsManager();
        manager.LoadClassPackage(tpkPath);
        var inst = manager.LoadAssetsFile(sharedAssetsPath2, false);
        manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

        // 1) Build (textureName → pivotXY) by walking Sprite assets. We use the
        //    Sprite's m_Name as the join key — in PG's pack Sprites share the
        //    name of their wrapped Texture2D (verified for Map_<area> in the
        //    substrate spike).
        var spritePivots = ReadSpritePivots(manager, inst);
        Console.WriteLine($"[icons] indexed {spritePivots.Count} sprite pivots");

        // 2) Walk Texture2Ds, extract the named ones to PNG + record metadata.
        var icons = new List<IconMeta>();
        var allTextures = inst.file.GetAssetsOfType(AssetClassID.Texture2D).ToList();
        Console.WriteLine($"[icons] scanning {allTextures.Count} Texture2D assets");
        var wanted = new HashSet<string>(LandmarkIcons.Select(p => p.TextureName), StringComparer.Ordinal);
        var typeByName = LandmarkIcons.ToDictionary(p => p.TextureName, p => p.LandmarkType, StringComparer.Ordinal);

        foreach (var info in allTextures)
        {
            var baseField = manager.GetBaseField(inst, info);
            if (baseField is null) continue;
            var nameField = baseField["m_Name"];
            if (nameField is null || nameField.IsDummy) continue;
            var name = nameField.AsString;
            if (string.IsNullOrEmpty(name)) continue;

            string? landmarkType = null;
            if (wanted.Contains(name))
            {
                landmarkType = typeByName[name];
            }
            else if (PlayerPinPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                // "Player" is intentionally NOT a CanonicalLandmarkTypes member.
                // Player-self pins have no reference source (they appear in neither
                // landmarks.json nor npcs.json), so a "Player" detection simply
                // contributes nothing to the type-constrained pairing pool — its
                // absence from CanonicalLandmarkTypes.All is by-design, not the
                // detect↔reference vocabulary divergence #974 guards against.
                // (PlayerPinPrefixes is currently empty, so this branch is dead.)
                landmarkType = "Player";
            }
            else
            {
                continue;
            }

            try
            {
                var (width, height) = DecodeAndSavePng(manager, inst, baseField, iconsDir, name);
                var (pivotX, pivotY) = spritePivots.TryGetValue(name, out var p) ? p : (0.5, 0.5);
                icons.Add(new IconMeta(
                    Name: name,
                    File: name + ".png",
                    Width: width,
                    Height: height,
                    PivotX: pivotX,
                    PivotY: pivotY,
                    LandmarkType: landmarkType));
                Console.WriteLine($"  + {name} ({width}x{height}) pivot=({pivotX:0.00},{pivotY:0.00}) type={landmarkType}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! {name}: {ex.Message} (skipping)");
            }
        }

        if (icons.Count == 0)
        {
            throw new UserFacingException(
                "no icon textures matched in sharedassets0.assets — has PG renamed the assets in a patch?");
        }

        WriteIndex(indexPath, icons);
        Console.WriteLine($"[icons] {icons.Count} icons extracted to {iconsDir}");
    }

    private static Dictionary<string, (double X, double Y)> ReadSpritePivots(
        AssetsManager manager, AssetsFileInstance inst)
    {
        var map = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Sprite))
        {
            AssetTypeValueField? baseField;
            try { baseField = manager.GetBaseField(inst, info); }
            catch { continue; }
            if (baseField is null) continue;

            var nameField = baseField["m_Name"];
            if (nameField is null || nameField.IsDummy) continue;
            var name = nameField.AsString;
            if (string.IsNullOrEmpty(name)) continue;

            var pivot = baseField["m_Pivot"];
            if (pivot is null || pivot.IsDummy) continue;
            try
            {
                var px = pivot["x"].AsFloat;
                var py = pivot["y"].AsFloat;
                // Last write wins — if PG ships multiple Sprites with the same name
                // for a texture, we don't try to disambiguate; the pivot should be
                // identical for icon sprites anyway.
                map[name] = (px, py);
            }
            catch
            {
                // Some Sprites may have a non-standard m_Pivot layout; skip rather
                // than fail the whole extraction.
            }
        }
        return map;
    }

    private static (int Width, int Height) DecodeAndSavePng(
        AssetsManager manager, AssetsFileInstance inst, AssetTypeValueField baseField,
        string iconsDir, string name)
    {
        int width = baseField["m_Width"].AsInt;
        int height = baseField["m_Height"].AsInt;

        var texFile = TextureFile.ReadTextureFile(baseField);
        var encoded = texFile.FillPictureData(inst);
        if (encoded is null || encoded.Length == 0)
        {
            throw new InvalidOperationException("FillPictureData returned empty bytes");
        }
        var bgra = texFile.DecodeTextureRaw(encoded, useBgra: true);
        if (bgra is null || bgra.Length == 0)
        {
            throw new InvalidOperationException("DecodeTextureRaw returned empty bytes");
        }

        var outPath = Path.Combine(iconsDir, name + ".png");
        WritePng(bgra, width, height, outPath);
        return (width, height);
    }

    private static void WritePng(byte[] bgra32, int width, int height, string outPath)
    {
        // Unity stores Texture2Ds bottom-up. AssetsTools.NET's TextureFile flips
        // automatically for COMPRESSED formats (DXT/ETC/etc., what PG's area
        // bundles use) but NOT for raw RGBA — which is the storage these
        // sharedassets0.assets icons use. The MapAssetSpike comment claimed
        // DecodeTextureRaw always flips; that's a half-truth that bites here.
        //
        // Always flip the row order so the PNG comes out the way PG renders the
        // icon. NCC against a screenshot is shape-matching — if templates are
        // upside-down vs rendered icons, scores collapse and detection fails.
        // The Sprite.m_Pivot we cache alongside is in Unity Y-up coords; the
        // downstream pivot-correction formula (`h * (0.5 - pivotY)`) is written
        // against right-side-up rendered icons + Unity-Y-up pivot, so flipping
        // here keeps the pivot interpretation correct.
        int rowBytes = width * 4;
        var flipped = new byte[bgra32.Length];
        for (int srcRow = 0; srcRow < height; srcRow++)
        {
            int dstRow = height - 1 - srcRow;
            Buffer.BlockCopy(bgra32, srcRow * rowBytes, flipped, dstRow * rowBytes, rowBytes);
        }

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(flipped, row * rowBytes, data.Scan0 + row * data.Stride, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(outPath, ImageFormat.Png);
    }

    private static void WriteIndex(string indexPath, List<IconMeta> icons)
    {
        var doc = new IconIndex(CacheFormatVersion, icons);
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(indexPath, json);
    }

    private static int TryReadCachedFormatVersion(string indexPath)
    {
        try
        {
            using var stream = File.OpenRead(indexPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number)
            {
                return v.GetInt32();
            }
        }
        catch { /* malformed or missing — caller treats as version mismatch */ }
        return -1;
    }

    public static IconIndex Load(string iconsDir)
    {
        var indexPath = Path.Combine(iconsDir, "index.json");
        if (!File.Exists(indexPath))
        {
            throw new UserFacingException(
                $"icon index not found at {indexPath}; run --phase extract-icons first");
        }
        var json = File.ReadAllText(indexPath);
        var doc = JsonSerializer.Deserialize<IconIndex>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        if (doc is null || doc.Icons.Count == 0)
        {
            throw new UserFacingException($"icon index at {indexPath} is empty or malformed");
        }
        return doc;
    }
}

public sealed record IconIndex(int Version, List<IconMeta> Icons);

public sealed record IconMeta(
    string Name,
    string File,
    int Width,
    int Height,
    double PivotX,
    double PivotY,
    string LandmarkType);
