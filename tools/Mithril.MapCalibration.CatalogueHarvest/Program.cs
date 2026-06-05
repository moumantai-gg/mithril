// Catalogue-harvest tool for mithril#1081.
//
// Enumerates every Map_<X> bundle in PG's Addressables directory, extracts each
// texture via the shared MapTextureExtractor + MapTextureCacheEmitter pipeline,
// and writes canonical-asset-hashes.json (schemaVersion 2) populated with all
// entries at the current PG version.
//
// The sha / width / height values come from the SAME code path CachedBaseTextureProvider
// re-verifies at runtime (MapTextureExtractor.EnsureExtracted writes the PNG;
// MapTextureCacheEmitter.EmitFromPng reads it with ImageIo.LoadGray and hashes
// the resulting gray pixels). Catalogue miss or sha mismatch at runtime = this
// tool was NOT used to populate the file.
//
// CLI:
//   mithril-catalogue-harvest [--install <pgRoot>] [--cache <cacheDir>] [--output <cataloguePath>]
//
// Defaults:
//   --install   auto-detect via SteamInstall.FindPgInstall()
//   --cache     %TEMP%\mithril-harvest-<pid>
//   --output    <repoRoot>/src/Mithril.MapCalibration/BundledData/canonical-asset-hashes.json
//               (resolved relative to the tool exe's location — two levels up from
//               tools/Mithril.MapCalibration.CatalogueHarvest/)

using System.Reflection;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mithril.Tools.MapCalibration.Common;

var pgInstall = ParseArg(args, "--install");
var cacheDir  = ParseArg(args, "--cache");
var outputArg = ParseArg(args, "--output");

// --- Resolve PG install ---
if (string.IsNullOrWhiteSpace(pgInstall))
{
    try
    {
        pgInstall = SteamInstall.FindPgInstall();
        Console.WriteLine($"[harvest] PG install: {pgInstall}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[harvest] error: could not find PG install: {ex.Message}");
        Console.Error.WriteLine("[harvest] pass --install <pgRoot> explicitly.");
        return 2;
    }
}

if (!Directory.Exists(pgInstall))
{
    Console.Error.WriteLine($"[harvest] error: PG install not found at '{pgInstall}'");
    return 2;
}

// --- Resolve bundle dir ---
var bundleDir = Path.Combine(pgInstall, "WindowsPlayer_Data", "StreamingAssets", "aa", "StandaloneWindows64");
if (!Directory.Exists(bundleDir))
{
    Console.Error.WriteLine($"[harvest] error: bundle dir not found: {bundleDir}");
    return 3;
}

// --- Detect PG version ---
var pgVersion = PgVersionDetector.TryDetect(pgInstall);
if (string.IsNullOrWhiteSpace(pgVersion))
{
    Console.Error.WriteLine("[harvest] warning: could not detect PG version; harvested entries will have no version key.");
    pgVersion = "unknown";
}
Console.WriteLine($"[harvest] PG version: {pgVersion}");

// --- Resolve cache dir ---
if (string.IsNullOrWhiteSpace(cacheDir))
{
    cacheDir = Path.Combine(Path.GetTempPath(), $"mithril-harvest-{Environment.ProcessId}");
}
Directory.CreateDirectory(cacheDir);
var mapsDir = Path.Combine(cacheDir, "maps-src");
Directory.CreateDirectory(mapsDir);
Console.WriteLine($"[harvest] cache dir: {cacheDir}");

// --- Resolve output path ---
string outputPath;
if (!string.IsNullOrWhiteSpace(outputArg))
{
    outputPath = outputArg;
}
else
{
    // Derive from exe location: tools/Mithril.MapCalibration.CatalogueHarvest/ → two levels up = repo root
    var exeDir = AppContext.BaseDirectory;
    var repoRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", ".."));
    // When running with `dotnet run`, the exe dir is deep inside bin/Debug/netX/. We need to
    // go up past net10.0-windows/, Debug/, bin/, Mithril.MapCalibration.CatalogueHarvest/, tools/ → repo root.
    // Use a more robust approach: look for the canonical file relative to the source tree.
    // Try: up from exe until we find Mithril.slnx or the specific bundled data directory.
    repoRoot = FindRepoRoot(exeDir)
        ?? throw new InvalidOperationException(
            $"Could not find repo root from exe dir '{exeDir}'. Pass --output explicitly.");
    outputPath = Path.Combine(repoRoot, "src", "Mithril.MapCalibration", "BundledData", "canonical-asset-hashes.json");
}
Console.WriteLine($"[harvest] output: {outputPath}");

var extractorVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

// --- Enumerate bundles ---
var bundleFiles = Directory.EnumerateFiles(bundleDir, "maps_assets_assets_art_maps_map_*.png_*.bundle")
    .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"[harvest] found {bundleFiles.Count} map bundle(s)");

// --- Build the catalogue ---
var byArtifact = new SortedDictionary<string, CatalogueEntry>(StringComparer.Ordinal);
var errors = new List<string>();

foreach (var bundlePath in bundleFiles)
{
    var bundleFile = Path.GetFileName(bundlePath);

    // Peek at the m_Name inside the bundle to get the canonical CamelCase area name.
    string? canonicalName;
    try
    {
        canonicalName = ReadTextureName(bundlePath);
    }
    catch (Exception ex)
    {
        var msg = $"[harvest] warning: could not read m_Name from '{bundleFile}': {ex.Message} — skipping";
        Console.Error.WriteLine(msg);
        errors.Add(msg);
        continue;
    }

    if (string.IsNullOrWhiteSpace(canonicalName))
    {
        var msg = $"[harvest] warning: no Texture2D m_Name in '{bundleFile}' — skipping";
        Console.Error.WriteLine(msg);
        errors.Add(msg);
        continue;
    }

    // canonicalName is e.g. "Map_AreaSerbule"; bare area for EnsureExtracted is "AreaSerbule"
    var bareArea = canonicalName.StartsWith("Map_", StringComparison.Ordinal)
        ? canonicalName["Map_".Length..]
        : canonicalName;

    Console.Write($"[harvest] {canonicalName} ... ");

    string pngPath;
    try
    {
        pngPath = MapTextureExtractor.EnsureExtracted(pgInstall, mapsDir, bareArea);
    }
    catch (Exception ex)
    {
        var msg = $"FAIL (extract): {ex.Message}";
        Console.WriteLine(msg);
        errors.Add($"{canonicalName}: {msg}");
        continue;
    }

    string manifestPath;
    string sha;
    try
    {
        (manifestPath, sha) = MapTextureCacheEmitter.EmitFromPng(
            pngPath, canonicalName, cacheDir, pgVersion, extractorVersion);
    }
    catch (Exception ex)
    {
        var msg = $"FAIL (emit): {ex.Message}";
        Console.WriteLine(msg);
        errors.Add($"{canonicalName}: {msg}");
        continue;
    }

    // Read width/height from the emitted manifest JSON
    int width, height;
    try
    {
        (width, height) = ReadManifestDims(manifestPath);
    }
    catch (Exception ex)
    {
        var msg = $"FAIL (read manifest): {ex.Message}";
        Console.WriteLine(msg);
        errors.Add($"{canonicalName}: {msg}");
        continue;
    }

    byArtifact[canonicalName] = new CatalogueEntry(sha, width, height);
    Console.WriteLine($"ok ({width}x{height}) sha={sha[..8]}…");
}

Console.WriteLine();
Console.WriteLine($"[harvest] harvested {byArtifact.Count} entries ({errors.Count} errors)");

if (errors.Count > 0)
{
    Console.Error.WriteLine("[harvest] errors encountered:");
    foreach (var e in errors)
        Console.Error.WriteLine($"  {e}");
}

// --- Write catalogue JSON ---
var catalogue = new
{
    schemaVersion = 2,
    byPgVersion = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        [pgVersion] = byArtifact.ToDictionary(
            kv => kv.Key,
            kv => (object)new { sha = kv.Value.Sha, width = kv.Value.Width, height = kv.Value.Height },
            StringComparer.Ordinal)
    }
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var json = JsonSerializer.Serialize(catalogue, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
});

File.WriteAllText(outputPath, json + "\n", new UTF8Encoding(false));
Console.WriteLine($"[harvest] wrote {outputPath}");

if (errors.Count > 0)
{
    Console.Error.WriteLine($"[harvest] {errors.Count} error(s) — catalogue is PARTIAL; re-run to retry failed entries.");
    return 1;
}

Console.WriteLine("[harvest] done.");
return 0;

// --- Helpers ---

static string? ReadTextureName(string bundlePath)
{
    var manager = new AssetsManager();
    var bunInst = manager.LoadBundleFile(bundlePath, true);
    var entries = bunInst.file.BlockAndDirInfo?.DirectoryInfos?.Count ?? 0;

    AssetsFileInstance? afileInst = null;
    for (int i = 0; i < entries; i++)
    {
        try { afileInst = manager.LoadAssetsFileFromBundle(bunInst, i); break; }
        catch { /* not an assets file; keep trying */ }
    }
    if (afileInst is null) return null;

    var tex2ds = afileInst.file.GetAssetsOfType(AssetClassID.Texture2D).ToList();
    foreach (var info in tex2ds)
    {
        var field = manager.GetBaseField(afileInst, info);
        if (field is null) continue;
        var name = field["m_Name"].AsString;
        if (!string.IsNullOrWhiteSpace(name)) return name;
    }
    return null;
}

static (int Width, int Height) ReadManifestDims(string manifestPath)
{
    var json = File.ReadAllText(manifestPath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var w = root.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
    var h = root.TryGetProperty("height", out var hp) ? hp.GetInt32() : 0;
    return (w, h);
}

static string? FindRepoRoot(string startDir)
{
    var dir = startDir;
    for (int i = 0; i < 10; i++)
    {
        if (dir is null) break;
        if (File.Exists(Path.Combine(dir, "Mithril.slnx")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static string? ParseArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

internal sealed record CatalogueEntry(string Sha, int Width, int Height);
