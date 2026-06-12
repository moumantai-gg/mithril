// Out-of-process asset-extraction sidecar for mithril#931. Decodes PG assets
// (AssetsTools.NET + System.Drawing) in a child process and writes the
// pre-decoded manifest+blob cache the decoder-free app graph reads BCL-only.
//
// CLI:
//   mithril-asset-extract --install <pgRoot> --out <cacheDir> (--icons | --asset <Map_<X>>) [--expect-pg-version <v>] [--tpk <path>]
//
// --asset takes the literal Unity Texture2D name (e.g. Map_AreaSerbule,
// Map_HogansKeepBasement) verbatim from the Player.log "Downloading Map" line
// — see mithril#1021. Renamed from --area in mithril#1021; the caller side
// (ProcessAssetExtractor in src/Mithril.MapCalibration.Detection) emits the
// new flag.
//
// Outputs: cache files on disk (existing manifest+blob format) + ONE JSON result
// line on stdout + an exit code. stderr = human diagnostics.
//
// Exit codes: 0 ok · 2 install-not-found · 3 bundle-missing-for-area
//            · 4 decode-failed · 5 output-unwritable.

using System.Reflection;
using System.Text.Json;
using Mithril.Tools.AssetExtractor;
using Mithril.Tools.MapCalibration.Common;

return SidecarProgram.Run(args);

namespace Mithril.Tools.AssetExtractor
{
    internal static class SidecarProgram
    {
        public const int ExitOk = 0;
        public const int ExitInstallNotFound = 2;
        public const int ExitBundleMissing = 3;
        public const int ExitDecodeFailed = 4;
        public const int ExitOutputUnwritable = 5;

        public static int Run(string[] args)
        {
            SidecarArgs parsed;
            try
            {
                parsed = SidecarArgs.Parse(args);
            }
            catch (UserFacingException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                SidecarArgs.PrintUsage();
                return ExitInstallNotFound; // arg error ≈ can't proceed; closest data-only code.
            }

            try
            {
                return Extract(parsed);
            }
            catch (UserFacingException ex)
            {
                // Map the user-facing message to the appropriate data-only exit code.
                Console.Error.WriteLine($"error: {ex.Message}");
                return ClassifyExit(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"error: output unwritable: {ex.Message}");
                return ExitOutputUnwritable;
            }
            catch (IOException ex) when (IsOutputPath(ex, parsed.OutDir))
            {
                Console.Error.WriteLine($"error: output unwritable: {ex.Message}");
                return ExitOutputUnwritable;
            }
            catch (Exception ex)
            {
                // Any decode failure (AssetsTools / System.Drawing) lands here.
                Console.Error.WriteLine($"error: decode failed: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return ExitDecodeFailed;
            }
        }

        private static int Extract(SidecarArgs args)
        {
            // Validate the install up front so a bad path reports exit 2 cleanly.
            if (string.IsNullOrWhiteSpace(args.InstallRoot) || !Directory.Exists(args.InstallRoot))
            {
                Console.Error.WriteLine($"error: PG install not found at '{args.InstallRoot}'");
                return ExitInstallNotFound;
            }

            try { Directory.CreateDirectory(args.OutDir); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: output dir not writable '{args.OutDir}': {ex.Message}");
                return ExitOutputUnwritable;
            }

            var pgVersion = PgVersionDetector.TryDetect(args.InstallRoot);
            var extractorVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

            if (!string.IsNullOrWhiteSpace(args.ExpectPgVersion)
                && !string.IsNullOrWhiteSpace(pgVersion)
                && !string.Equals(args.ExpectPgVersion, pgVersion, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"warning: detected PG version '{pgVersion}' != expected '{args.ExpectPgVersion}' (continuing).");
            }

            var artifacts = new List<ResultArtifact>();

            if (args.Kind == ExtractKind.Icons)
            {
                var iconsDir = Path.Combine(args.OutDir, "icons-src");
                IconTemplateExtractor.EnsureExtracted(args.InstallRoot, iconsDir, ResolveTpkPath(args.TpkPath));
                var sha = IconTemplateEmitter.EmitFromIcons(iconsDir, args.OutDir, pgVersion, extractorVersion);
                artifacts.Add(new ResultArtifact("icons", null, Path.Combine(args.OutDir, "icon-templates.json"), sha));
            }
            else
            {
                var mapDir = Path.Combine(args.OutDir, "maps-src");
                // --asset is the literal Unity Texture2D name (e.g. Map_AreaSerbule,
                // Map_HogansKeepBasement). The shared MapTextureExtractor.EnsureExtracted
                // is also called from the gate-study / WPF / synthesis-probe tools, all
                // of which pass the bare "<X>" form (e.g. "AreaSerbule"); to avoid
                // churning those callers, strip the Map_ prefix here at the sidecar
                // boundary before handing off. The consumer-facing cache (written by
                // MapTextureCacheEmitter and read by CachedBaseTextureProvider) is keyed
                // on the full Map_<X> form (#1021 — that's the per-scene cache key the
                // runtime now expects).
                var asset = args.MapAssetName!;
                var bareArea = asset.StartsWith("Map_", StringComparison.Ordinal)
                    ? asset["Map_".Length..]
                    : asset;
                string pngPath;
                try
                {
                    pngPath = MapTextureExtractor.EnsureExtracted(args.InstallRoot, mapDir, bareArea);
                }
                catch (UserFacingException ex) when (ex.Message.Contains("no map bundle for area", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                    return ExitBundleMissing;
                }
                var (manifestPath, sha) = MapTextureCacheEmitter.EmitFromPng(
                    pngPath, asset, args.OutDir, pgVersion, extractorVersion);
                artifacts.Add(new ResultArtifact("texture", asset, manifestPath, sha));

                // mithril#1140: emit the alpha companion when the source PNG
                // has a real alpha channel. RGB-only formats (RGB24 / BC1)
                // return null → no alpha artifact; the consumer's
                // TryGetTextureAlpha safe-degrades.
                var alphaResult = MapTextureCacheEmitter.EmitAlphaFromPng(
                    pngPath, asset, args.OutDir, pgVersion, extractorVersion);
                if (alphaResult is { } alpha)
                {
                    artifacts.Add(new ResultArtifact("texture-alpha", asset, alpha.ManifestPath, alpha.PixelSha256));
                }
            }

            EmitResult(pgVersion, extractorVersion, artifacts);
            return ExitOk;
        }

        private static void EmitResult(string? pgVersion, string? extractorVersion, List<ResultArtifact> artifacts)
        {
            var result = new SidecarResultDto("ok", pgVersion, extractorVersion, artifacts);
            // Single JSON line on stdout (the app parses this). camelCase to match
            // the app's source-gen contract.
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            });
            Console.Out.WriteLine(json);
        }

        private static string ResolveTpkPath(string? explicitPath)
        {
            // The icon extractor needs classdata.tpk. Prefer an explicit --tpk path
            // when the caller provided one and it exists (the app downloads the tpk
            // to its always-writable asset cache and threads that path in — #960),
            // then one next to the exe, then the Tools default. EnsureExtracted
            // errors with a download URL if it's genuinely missing (→ decode-failed
            // exit), so missing-tpk still fail-softs exactly as before.
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
                return explicitPath;
            var beside = Path.Combine(AppContext.BaseDirectory, "classdata.tpk");
            return File.Exists(beside) ? beside : RepoPaths.DefaultTpkPath();
        }

        private static int ClassifyExit(string message)
        {
            if (message.Contains("install", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return ExitInstallNotFound;
            if (message.Contains("no map bundle for area", StringComparison.OrdinalIgnoreCase))
                return ExitBundleMissing;
            if (message.Contains("not writable", StringComparison.OrdinalIgnoreCase) || message.Contains("unwritable", StringComparison.OrdinalIgnoreCase))
                return ExitOutputUnwritable;
            // sharedassets0 / bundle / tpk / texture decode problems.
            return ExitDecodeFailed;
        }

        private static bool IsOutputPath(IOException ex, string outDir)
            => ex.Message.Contains(outDir, StringComparison.OrdinalIgnoreCase);
    }

    internal enum ExtractKind { Icons, Texture }

    internal sealed record SidecarArgs(string InstallRoot, string OutDir, ExtractKind Kind, string? MapAssetName, string? ExpectPgVersion, string? TpkPath)
    {
        public static SidecarArgs Parse(string[] args)
        {
            string? install = null, outDir = null, asset = null, expect = null, tpk = null;
            bool icons = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--install": install = Next(args, ref i, "--install"); break;
                    case "--out": outDir = Next(args, ref i, "--out"); break;
                    case "--icons": icons = true; break;
                    case "--asset": asset = Next(args, ref i, "--asset"); break;
                    case "--expect-pg-version": expect = Next(args, ref i, "--expect-pg-version"); break;
                    case "--tpk": tpk = Next(args, ref i, "--tpk"); break;
                    default:
                        throw new UserFacingException($"unknown argument '{args[i]}'");
                }
            }
            if (string.IsNullOrWhiteSpace(install)) throw new UserFacingException("--install <pgRoot> is required");
            if (string.IsNullOrWhiteSpace(outDir)) throw new UserFacingException("--out <cacheDir> is required");
            if (icons && asset is not null) throw new UserFacingException("--icons and --asset are mutually exclusive");
            if (!icons && asset is null) throw new UserFacingException("one of --icons or --asset <Map_<X>> is required");
            return new SidecarArgs(install, outDir, icons ? ExtractKind.Icons : ExtractKind.Texture, asset, expect, tpk);
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new UserFacingException($"{flag} requires a value");
            return args[++i];
        }

        public static void PrintUsage()
        {
            Console.Error.WriteLine(
                "usage: mithril-asset-extract --install <pgRoot> --out <cacheDir> (--icons | --asset <Map_<X>>) [--expect-pg-version <v>] [--tpk <path>]");
        }
    }

    internal sealed record ResultArtifact(string Kind, string? Area, string Path, string PixelSha256);

    internal sealed record SidecarResultDto(string Status, string? PgVersion, string? ExtractorVersion, List<ResultArtifact> Artifacts);
}
