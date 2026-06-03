// Offline tool for mithril#852: derive per-area map calibration from a single
// in-game screenshot of the open map. Reads on-disk PG assets only — never
// touches the running game process. Output: a populated
// `anchors["Area<Name>"] = AreaCalibration{…}` entry written back into
// `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`.
//
// Pipeline (all phases run unless --phase <name> selects one):
//   1. extract-icons  — pull landmark + player-pin Texture2Ds from sharedassets0.assets
//   2. extract-map    — pull the Map_Area<Name> Texture2D from the area's bundle
//   3. locate-map     — find the map's rect within the screenshot
//   4. detect         — NCC-match each icon template against the screenshot
//   5. assign         — match detections to landmarks.json entries by Type
//   6. solve          — feed (world, pixel) pairs into LandmarkCalibrationSolver
//   7. persist        — write the AreaCalibration into the baseline JSON
//
// See README.md next to this file for usage and the synthetic test harness.

using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            // emit-templates / emit-refs are standalone regen modes with their own
            // minimal args; handled before the full CliArgs parser so their flags
            // don't need threading through CliArgs.
            if (PhaseIs(args, "emit-templates"))
            {
                return RunEmitTemplates(args);
            }
            if (PhaseIs(args, "emit-refs"))
            {
                return RunEmitRefs(args);
            }
            if (PhaseIs(args, "sparse-locate-spike"))
            {
                return SparseLocateSpike.Run();
            }

            var parsed = CliArgs.Parse(args);
            if (parsed is null)
            {
                CliArgs.PrintUsage();
                return 2;
            }
            return Pipeline.Run(parsed);
        }
        catch (UserFacingException ex)
        {
            // Expected failure mode (missing PG install, missing screenshot,
            // unreadable log, etc.). Surface message only — stack traces here
            // are noise.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static bool PhaseIs(string[] args, string phase)
    {
        int pi = Array.IndexOf(args, "--phase");
        return pi >= 0 && pi + 1 < args.Length && args[pi + 1] == phase;
    }

    // emit-refs: regenerate the local-only replay reference fixtures
    // (study/refs/<area>.json) from the bundled landmarks.json + npcs.json. These
    // files are gitignored (they live under study/) — this is just the
    // reproducible regen path so the ReplayFixtureTests refs can be rebuilt
    // deterministically. --area <name> required (repeatable not supported; run
    // once per area). --refs-out defaults to study/refs beside Mithril.slnx.
    private static int RunEmitRefs(string[] args)
    {
        string GetArg(string key, string def)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return def;
        }
        var area = GetArg("--area", "");
        if (area.Length == 0)
            throw new UserFacingException("emit-refs needs --area <AreaName> (e.g. --area AreaSerbule)");

        var outDir = GetArg("--refs-out", DefaultRefsDir());
        var landmarks = GetArg("--landmarks", RepoPaths.LandmarksJsonPath());
        var npcs = GetArg("--npcs", RepoPaths.NpcsJsonPath());

        RefsEmitter.Emit(area, landmarks, npcs, outDir);
        return 0;
    }

    private static string DefaultRefsDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx")))
                return Path.Combine(dir, "study", "refs");
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new UserFacingException($"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }

    // emit-templates: regenerate the bundled pre-decoded icon templates
    // (icon-templates.json + .bin) consumed by the runtime
    // BundledIconTemplateLoader. --synthetic uses deterministic teardrop
    // templates (no PG install); otherwise reads extracted icon PNGs from
    // --icons-dir. --emit-out defaults to src/Mithril.MapCalibration/BundledData.
    private static int RunEmitTemplates(string[] args)
    {
        string GetArg(string key, string def)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == key) return args[i + 1];
            return def;
        }
        bool synthetic = Array.IndexOf(args, "--synthetic") >= 0;
        var outDir = GetArg("--emit-out", DefaultBundledDataDir());

        if (synthetic)
        {
            IconTemplateEmitter.EmitSynthetic(outDir);
        }
        else
        {
            var iconsDir = GetArg("--icons-dir", "");
            if (iconsDir.Length == 0)
                throw new UserFacingException("emit-templates needs --icons-dir <dir> (extracted icon PNGs) or --synthetic");
            IconTemplateEmitter.EmitFromIcons(iconsDir, outDir);
        }
        return 0;
    }

    private static string DefaultBundledDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx")))
                return Path.Combine(dir, "src", "Mithril.MapCalibration", "BundledData");
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new UserFacingException($"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }
}
