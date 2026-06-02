using System.Globalization;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Parsed command-line arguments. All optional except <see cref="ScreenshotPath"/>
/// and <see cref="Area"/>. The tool needs either an explicit
/// <see cref="PlayerCoord"/> or a <see cref="PlayerLogPath"/> to triangulate the
/// player-pin's world coord; if neither is supplied the solver still runs but
/// loses the most reliable reference point.
/// </summary>
internal sealed record CliArgs(
    string ScreenshotPath,
    string Area,
    string? BaselinePath,
    string? LandmarksPath,
    string? NpcsPath,
    string? IconsDir,
    string? MapDir,
    string? TpkPath,
    string? PlayerLogPath,
    (double X, double Z)? PlayerCoord,
    (int X, int Y, int W, int H)? MapRect,
    double DetectionThreshold,
    int IconRenderSize,
    IReadOnlyDictionary<string, (int W, int H)> IconSizeOverrides,
    IReadOnlySet<string> ExcludedLandmarkTypes,
    string? DebugImagePath,
    string? ProjectionOverlayPath,
    string? MaskDebugPath,
    double Zoom,
    Phase Phase,
    bool DryRun,
    bool UseBorderMask,
    string? DetectionsCsvPath,
    bool IgnoreTypes,
    (double Rot, double Scale, double Ox, double Oy, bool Mirror)? Seed,
    (double Scale, double Rot, double Ox, double Oy, bool Mirror)? TruthCal,
    string? RansacSeedsCsvPath,
    bool TraceConsole,
    string? OtlpEndpoint,
    string? AlignedBasePath,
    string? BundleDir,
    string? MapRectJsonPath,
    string? RecoveredCalJsonPath,
    string? AlignedDeviationPath,
    string? DetectionsJsonPath,
    (double Scale, double Rot, double Ox, double Oy, bool Mirror)? HandTruthCal)
{
    public static CliArgs? Parse(string[] argv)
    {
        if (argv.Length == 0) return null;

        string? screenshot = null;
        string? area = null;
        string? baseline = null;
        string? landmarks = null;
        string? npcs = null;
        string? iconsDir = null;
        string? mapDir = null;
        string? tpk = null;
        string? playerLog = null;
        (double, double)? playerCoord = null;
        (int, int, int, int)? mapRect = null;
        double detectionThreshold = 0.5;
        int iconRenderSize = 0;  // 0 = auto-detect
        var iconSizeOverrides = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var excludedTypes = new HashSet<string>(StringComparer.Ordinal);
        string? debugImagePath = null;
        string? projectionOverlayPath = null;
        string? maskDebugPath = null;
        double zoom = 1.0;
        Phase phase = Phase.Full;
        bool dryRun = false;
        bool useBorderMask = false;
        bool debug = false;
        string? outDir = null;
        string? detectionsCsv = null;
        bool ignoreTypes = false;
        (double, double, double, double, bool)? seed = null;
        (double, double, double, double, bool)? truthCal = null;
        string? ransacSeedsCsv = null;
        bool traceConsole = false;
        string? otlpEndpoint = null;
        string? alignedBasePath = null;
        string? bundleDir = null;
        string? mapRectJsonPath = null;
        string? recoveredCalJsonPath = null;
        string? alignedDeviationPath = null;
        string? detectionsJsonPath = null;
        (double, double, double, double, bool)? handTruthCal = null;

        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i])
            {
                case "--screenshot": screenshot = Next(argv, ref i); break;
                case "--area": area = Next(argv, ref i); break;
                case "--baseline": baseline = Next(argv, ref i); break;
                case "--landmarks": landmarks = Next(argv, ref i); break;
                case "--npcs": npcs = Next(argv, ref i); break;
                case "--icons-dir": iconsDir = Next(argv, ref i); break;
                case "--map-dir": mapDir = Next(argv, ref i); break;
                case "--tpk": tpk = Next(argv, ref i); break;
                case "--player-log": playerLog = Next(argv, ref i); break;
                case "--player-coord":
                    playerCoord = ParseCoord(Next(argv, ref i));
                    break;
                case "--map-rect":
                    mapRect = ParseMapRect(Next(argv, ref i));
                    break;
                case "--detection-threshold":
                    detectionThreshold = double.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--icon-render-size":
                    iconRenderSize = int.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--icon-size":
                    var (iconName, iconWh) = ParseIconSize(Next(argv, ref i));
                    iconSizeOverrides[iconName] = iconWh;
                    break;
                case "--exclude-type":
                    excludedTypes.Add(Next(argv, ref i));
                    break;
                case "--debug-image":
                    debugImagePath = Next(argv, ref i);
                    break;
                case "--projection-overlay":
                    projectionOverlayPath = Next(argv, ref i);
                    break;
                case "--mask-debug":
                    maskDebugPath = Next(argv, ref i);
                    break;
                case "--zoom":
                    zoom = double.Parse(Next(argv, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--phase":
                    phase = ParsePhase(Next(argv, ref i));
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--border-mask":
                    useBorderMask = true;
                    break;
                case "--debug":
                    debug = true;
                    break;
                case "--outdir":
                    outDir = Next(argv, ref i);
                    break;
                case "--detections-csv":
                    detectionsCsv = Next(argv, ref i);
                    break;
                case "--ignore-types":
                    ignoreTypes = true;
                    break;
                case "--seed":
                    seed = ParseSeed(Next(argv, ref i));
                    break;
                case "--truth-cal":
                    truthCal = ParseTruthCal(Next(argv, ref i));
                    break;
                case "--ransac-seeds-csv":
                    ransacSeedsCsv = Next(argv, ref i);
                    break;
                case "--trace-console":
                    traceConsole = true;
                    break;
                case "--otlp":
                    otlpEndpoint = Next(argv, ref i);
                    break;
                case "--aligned-base":
                    alignedBasePath = Next(argv, ref i);
                    break;
                case "--bundle-dir":
                    bundleDir = Next(argv, ref i);
                    break;
                case "--maprect-json":
                    mapRectJsonPath = Next(argv, ref i);
                    break;
                case "--recovered-cal-json":
                    recoveredCalJsonPath = Next(argv, ref i);
                    break;
                case "--aligned-deviation":
                    alignedDeviationPath = Next(argv, ref i);
                    break;
                case "--detections-json":
                    detectionsJsonPath = Next(argv, ref i);
                    break;
                case "--hand-truth-cal":
                {
                    var raw = Next(argv, ref i);
                    try { handTruthCal = ParseTruthCal(raw); }
                    catch (UserFacingException)
                    {
                        throw new UserFacingException(
                            $"--hand-truth-cal wants 'scale,rot,ox,oy,mirror' (got '{raw}')");
                    }
                    break;
                }
                case "-h" or "--help":
                    return null;
                default:
                    Console.Error.WriteLine($"unknown arg '{argv[i]}'");
                    return null;
            }
        }

        // Only the full pipeline needs --screenshot. extract-icons and
        // extract-map are cache-population modes; self-test synthesises its
        // own inputs.
        // SynthesisProbe also requires --screenshot; guard added in Task 14 where
        // SynthesisProbePhase.Run first reads args.ScreenshotPath.
        if (screenshot is null && phase is Phase.Full)
        {
            Console.Error.WriteLine("--screenshot required for --phase full");
            return null;
        }
        // --area is needed whenever we touch area-specific data (the map
        // bundle in extract-map; landmarks/npcs in the full pipeline).
        // extract-icons (sharedassets0-wide) and self-test (synthetic area)
        // don't need it.
        if (area is null && phase is not (Phase.ExtractIcons or Phase.SelfTest or Phase.EmitTemplates))
        {
            Console.Error.WriteLine("--area required (e.g. --area AreaSerbule)");
            return null;
        }

        // --debug is a convenience switch: dump every intermediate-stage
        // visualization with stable <area>_<stage>.png names instead of wiring
        // each path by hand. --outdir picks the folder (default: beside the
        // screenshot). Explicit per-file flags still win if also given.
        if (debug)
        {
            var dir = outDir
                ?? (screenshot is not null ? Path.GetDirectoryName(Path.GetFullPath(screenshot)) : null)
                ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            var stem = string.IsNullOrEmpty(area) ? "calib" : area;
            debugImagePath ??= Path.Combine(dir, $"{stem}_detections.png");
            maskDebugPath ??= Path.Combine(dir, $"{stem}_mask.png");
            projectionOverlayPath ??= Path.Combine(dir, $"{stem}_projection.png");
            Console.WriteLine($"[debug] dumping stage visualizations to {dir}");
        }
        else if (outDir is not null)
        {
            Console.Error.WriteLine("--outdir has no effect without --debug");
        }

        return new CliArgs(
            ScreenshotPath: screenshot ?? "",
            Area: area ?? "",
            BaselinePath: baseline,
            LandmarksPath: landmarks,
            NpcsPath: npcs,
            IconsDir: iconsDir,
            MapDir: mapDir,
            TpkPath: tpk,
            PlayerLogPath: playerLog,
            PlayerCoord: playerCoord,
            MapRect: mapRect,
            DetectionThreshold: detectionThreshold,
            IconRenderSize: iconRenderSize,
            IconSizeOverrides: iconSizeOverrides,
            ExcludedLandmarkTypes: excludedTypes,
            DebugImagePath: debugImagePath,
            ProjectionOverlayPath: projectionOverlayPath,
            MaskDebugPath: maskDebugPath,
            Zoom: zoom,
            Phase: phase,
            DryRun: dryRun,
            UseBorderMask: useBorderMask,
            DetectionsCsvPath: detectionsCsv,
            IgnoreTypes: ignoreTypes,
            Seed: seed,
            TruthCal: truthCal,
            RansacSeedsCsvPath: ransacSeedsCsv,
            TraceConsole: traceConsole,
            OtlpEndpoint: otlpEndpoint,
            AlignedBasePath: alignedBasePath,
            BundleDir: bundleDir,
            MapRectJsonPath: mapRectJsonPath,
            RecoveredCalJsonPath: recoveredCalJsonPath,
            AlignedDeviationPath: alignedDeviationPath,
            DetectionsJsonPath: detectionsJsonPath,
            HandTruthCal: handTruthCal);
    }

    private static (double, double, double, double, bool) ParseSeed(string s)
    {
        // "rot,scale,ox,oy,mirror" — seeds the seed-guided ICP assignment with a
        // known-orientation calibration (e.g. a fragile cold solve or a rotation
        // lifted from the frame-invariant user-refinement store) so sparse areas
        // whose RANSAC correspondence is unstable can still converge.
        var parts = s.Split(',', 5);
        if (parts.Length != 5)
        {
            throw new UserFacingException($"--seed wants 'rot,scale,ox,oy,mirror' (got '{s}')");
        }
        return (
            double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture),
            bool.Parse(parts[4].Trim()));
    }

    private static (double, double, double, double, bool) ParseTruthCal(string s)
    {
        // "scale,rot,ox,oy,mirror" — the known-correct calibration used as a
        // reference truth for E1-E5 synthesis-probe diagnostics.
        // NOTE: ordering is scale-first, unlike ParseSeed which is rot-first.
        var parts = s.Split(',', 5);
        if (parts.Length != 5) throw new UserFacingException($"--truth-cal wants 'scale,rot,ox,oy,mirror' (got '{s}')");
        return (
            double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
            double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture),
            bool.Parse(parts[4].Trim()));
    }

    private static (string Name, (int W, int H) Wh) ParseIconSize(string s)
    {
        var eq = s.IndexOf('=');
        if (eq <= 0)
        {
            throw new UserFacingException($"--icon-size wants 'name=WxH' (got '{s}')");
        }
        var name = s[..eq];
        var dim = s[(eq + 1)..];
        var x = dim.IndexOf('x');
        if (x <= 0)
        {
            throw new UserFacingException($"--icon-size wants 'name=WxH' (got '{s}')");
        }
        var w = int.Parse(dim[..x], CultureInfo.InvariantCulture);
        var h = int.Parse(dim[(x + 1)..], CultureInfo.InvariantCulture);
        return (name, (w, h));
    }

    private static (int, int, int, int) ParseMapRect(string s)
    {
        var parts = s.Split(',', 4);
        if (parts.Length != 4)
        {
            throw new UserFacingException($"--map-rect wants 'x,y,w,h' (got '{s}')");
        }
        return (
            int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[2].Trim(), CultureInfo.InvariantCulture),
            int.Parse(parts[3].Trim(), CultureInfo.InvariantCulture));
    }

    private static string Next(string[] argv, ref int i)
    {
        if (i + 1 >= argv.Length)
        {
            throw new UserFacingException($"missing value after '{argv[i]}'");
        }
        return argv[++i];
    }

    private static (double, double) ParseCoord(string s)
    {
        var parts = s.Split(',', 2);
        if (parts.Length != 2)
        {
            throw new UserFacingException($"--player-coord wants 'x,z' (got '{s}')");
        }
        var x = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        var z = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
        return (x, z);
    }

    private static Phase ParsePhase(string s) => s.ToLowerInvariant() switch
    {
        "extract-icons" => Phase.ExtractIcons,
        "extract-map" => Phase.ExtractMap,
        "full" => Phase.Full,
        "self-test" => Phase.SelfTest,
        "emit-templates" => Phase.EmitTemplates,
        "synthesis-probe" => Phase.SynthesisProbe,
        _ => throw new UserFacingException($"unknown phase '{s}' (extract-icons | extract-map | full | self-test | emit-templates | synthesis-probe)"),
    };

    public static void PrintUsage()
    {
        Console.Error.WriteLine("""
            mithril#852 — screenshot-based map calibration

            usage:
              MapCalibrationFromScreenshot --screenshot <png> --area <AreaName> [opts]
              MapCalibrationFromScreenshot --phase extract-icons [--tpk <path>] [--icons-dir <dir>]

            required:
              --screenshot <path>           PNG of the in-game map screen (M key)
              --area <AreaName>             e.g. AreaSerbule, matches landmarks.json key

            recommended (improves the fit):
              --player-coord <x,z>          player's world coord at screenshot time (signed)
              --player-log  <Player.log>    alternative: extract from most recent [Status]
              --zoom <float>                in-game map zoom (default 1.0 = CalibrationZoom default)

            map-rect override (skip auto-detect):
              --map-rect <x,y,w,h>          visible map's bbox in the screenshot (px); use when
                                            auto-detect can't find the map or picks the wrong scale

            detection tuning:
              --detection-threshold <0..1>  min NCC score to accept a template match (default 0.5).
                                            Lower (e.g. 0.3) when real-screenshot recall is low;
                                            higher (e.g. 0.7) when there are too many false positives
              --icon-render-size <px>       on-screen pixel size PG renders icons at; bypasses the
                                            auto-detect sweep. Measure by opening the screenshot in
                                            an image viewer and reading an icon's bbox dimension
              --icon-size <name>=<W>x<H>    force a specific template to exact WxH dimensions (no
                                            aspect-preservation). Repeatable; use when PG renders
                                            a sprite with a different aspect than its source asset
                                            (verified for landmark_npc on Serbule — 17x16 vs 236x256
                                            source). Overrides --icon-render-size for that icon
              --exclude-type <Type>         drop a landmark Type from the RANSAC pool entirely.
                                            Repeatable. Use when a type's template doesn't match
                                            PG's actual sprite (verified for Npc on Serbule — the
                                            landmark_npc source asset is not what PG renders for
                                            NPC pins, and including noisy NPC matches in the pool
                                            misleads RANSAC into wrong-but-self-consistent fits)
              --border-mask                 drop detections sitting in the map's rocky border.
                                            Irregular-bordered outdoor zones have a stone rim
                                            that matches pin templates as noise; a rectangular
                                            map-rect can't exclude it, so the rim floods RANSAC
                                            and out-votes sparse interior landmarks (Eltibule,
                                            KurMountains). Masks the edge-connected
                                            non-vegetation/water region. Opt-in: flat tan/desert
                                            areas have no such border.
              --ignore-types                let any detection pair with any ref in RANSAC (ignore the
                                            per-type constraint). Diagnostic: tests whether anonymous
                                            blob centroids register without the template-typing label
              --detections-csv <path>       load the detection pool from a CSV
                                            (screenshotX,screenshotY,type,iconName,score) instead of
                                            running whole-image template NCC. Pairs with the deviation
                                            probe's blob-typed detections (type-aware template NCC within
                                            icon blobs) — well-spread, low-false-positive points that fix
                                            sparse-area correspondence. Combine with --seed for the solve
              --seed <rot,scale,ox,oy,mirror>  bypass RANSAC; seed a guided-ICP assignment with a
                                            known-orientation calibration (texture-frame: rotation
                                            in rad, scale px/unit, origin px, mirror true|false).
                                            Project refs through the seed, snap each to its nearest
                                            same-type detection, re-solve, iterate (shrinking radius).
                                            For sparse areas whose cold RANSAC is unstable but whose
                                            orientation is known (the {0,π} class — refinements.json
                                            rotation is frame-invariant). Verify with --projection-overlay
              --debug                       dump every intermediate-stage visualization (detections,
                                            border mask, projection overlay) as <area>_<stage>.png.
                                            One switch instead of wiring each path below by hand.
              --outdir <dir>                where --debug writes its PNGs (default: beside the screenshot)

            individual stage outputs (or let --debug name them for you):
              --mask-debug <path>           border-mask diagnostic PNG (needs --border-mask): masked
                                            rocky-rim region tinted red over the screenshot, every
                                            detection a cross — green = kept (interior), red = dropped
                                            (in border). Shows unmasked-vs-masked at a glance
              --debug-image <path>          write an annotated PNG: cyan rects mark every detection
                                            that cleared threshold, red crosses mark the pivot-
                                            corrected anchor, green rect outlines the map rect
              --projection-overlay <path>   project every landmark + NPC ref through the recovered
                                            calibration and mark on the screenshot. Yellow crosses
                                            for all refs, green rects around RANSAC inliers. Useful
                                            for seeing where the residual lives at a glance

            paths (sane defaults):
              --baseline    <baseline.json> default: src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json
              --landmarks   <landmarks.json> default: src/Mithril.Shared/Reference/BundledData/landmarks.json
              --npcs        <npcs.json>      default: src/Mithril.Shared/Reference/BundledData/npcs.json
              --icons-dir   <dir>           where to read/cache extracted icon PNGs
              --map-dir     <dir>           where to read/cache extracted area-map PNGs
              --tpk         <classdata.tpk> AssetsTools.NET class-data package (~290 KB)
                                            (download from github.com/nesrak1/UABEA/raw/master/ReleaseFiles/classdata.tpk)

            modes:
              --phase extract-icons         only extract the icon templates from sharedassets0.assets
              --phase extract-map           only extract the area's map PNG from its bundle
              --phase full                  (default) run the full pipeline
              --phase self-test             synthetic end-to-end test (no PG/tpk needed)
              --phase synthesis-probe       run E1-E5 icon-likelihood-field diagnostic; emits CSV + PNGs + OTel
              --dry-run                     don't write the baseline JSON, just print what would change

            synthesis-probe diagnostic (--phase synthesis-probe):
              --truth-cal <scale,rot,ox,oy,mirror>  known-correct calibration for E1-E5 (mirror = true|false)
              --ransac-seeds-csv <path>             CSV of candidate calibrations to score (E4):
                                                      label,scale,rot,ox,oy,mirror
              --trace-console                       emit OTel spans to stdout
              --otlp <endpoint>                     emit OTel spans to the named OTLP endpoint
              --aligned-base <path>                 pre-ECC-aligned base texture PNG, resampled to the --map-rect
                                                     crop dimensions. Overrides the auto-load+resize path; use when
                                                     a fixture pre-pair was prepared by the live capture pipeline
                                                     (e.g. study fixtures with matching foo-crop.png + foo-texture-resampled.png).
              --bundle-dir <dir>                    a per-attempt diagnostic bundle directory written by Mithril's
                                                     AutoCalibrationEngine. Auto-resolves --maprect-json /
                                                     --recovered-cal-json / --aligned-deviation / --detections-json
                                                     from the bundle's 01-attempt.json manifest if those flags aren't
                                                     given explicitly.
              --maprect-json <path>                 the bundle's 04-maprect.json (texture↔aligned-pair transform).
              --recovered-cal-json <path>           the bundle's 11-recovered-cal.json (production-recovered
                                                     AreaCalibration). When given with --maprect-json, the
                                                     synthesis-probe derives --truth-cal automatically.
              --aligned-deviation <path>            the bundle's 07-deviation.png (post-ECC, post-subtraction
                                                     deviation). Bypasses IconLikelihoodField.Build's subtraction.
              --detections-json <path>              the bundle's 10-detections.json (production's NCC detection set).
                                                     Optional; not consumed in v1.
              --hand-truth-cal <scale,rot,ox,oy,mirror>
                                                     user-supplied texture-pixel-space truth cal (mirror = true|false).
                                                     Converted to crop-pixel via --maprect-json before use. Takes
                                                     precedence over --recovered-cal-json (use when production's
                                                     recovered cal is known-wrong, e.g. the 2026-06-02 4-inlier
                                                     residual-4-px solves; supply the hand-verified entry from
                                                     src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json).
            """);
    }
}

internal enum Phase
{
    Full,
    ExtractIcons,
    ExtractMap,
    SelfTest,
    EmitTemplates,
    SynthesisProbe,
}
