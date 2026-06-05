using System.Globalization;
using Mithril.MapCalibration;
using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Glue layer that wires the seven pipeline phases together. Each phase delegates
/// to a focused helper; <see cref="Pipeline"/> only sequences them and prints
/// per-step progress so a user running the tool can see where time is spent
/// (icon extraction + the NCC pass dominate end-to-end).
/// </summary>
internal static class Pipeline
{
    public static int Run(CliArgs args)
    {
        if (args.Phase == Phase.SelfTest)
        {
            return SelfTest.Run();
        }

        if (args.Phase == Phase.SynthesisProbe)
        {
            return SynthesisProbe.SynthesisProbePhase.Run(args);
        }

        var pgInstall = SteamInstall.FindPgInstall();
        Console.WriteLine($"[steam] PG install: {pgInstall}");

        var iconsDir = args.IconsDir ?? DefaultScratch("icons");
        var mapDir = args.MapDir ?? DefaultScratch("maps");
        var tpkPath = args.TpkPath ?? Path.Combine(AppContext.BaseDirectory, "classdata.tpk");

        // Phase 1: icon templates. Always ensure they exist (cheap to skip if cached).
        IconTemplateExtractor.EnsureExtracted(pgInstall, iconsDir, tpkPath);

        if (args.Phase == Phase.ExtractIcons)
        {
            Console.WriteLine($"[done] icon templates ready in {iconsDir}");
            return 0;
        }

        // Phase 2: area map texture.
        var mapPng = MapTextureExtractor.EnsureExtracted(pgInstall, mapDir, args.Area);

        if (args.Phase == Phase.ExtractMap)
        {
            Console.WriteLine($"[done] map texture at {mapPng}");
            return 0;
        }

        if (!File.Exists(args.ScreenshotPath))
        {
            throw new UserFacingException($"screenshot not found: {args.ScreenshotPath}");
        }

        // Phase 3-6: locate, detect, assign, solve. ScreenshotCalibrator owns the
        // image-processing math so Pipeline can stay flow-only.
        var inputs = new CalibrationInputs(
            ScreenshotPath: args.ScreenshotPath,
            AreaMapPath: mapPng,
            IconsDir: iconsDir,
            LandmarksJsonPath: ResolveLandmarksPath(args),
            NpcsJsonPath: ResolveNpcsPath(args),
            Area: args.Area,
            Zoom: args.Zoom,
            PlayerCoord: ResolvePlayerCoord(args),
            MapRectOverride: args.MapRect,
            DetectionThreshold: args.DetectionThreshold,
            IconRenderSizeOverride: args.IconRenderSize,
            IconSizeOverrides: args.IconSizeOverrides,
            ExcludedLandmarkTypes: args.ExcludedLandmarkTypes,
            DebugImagePath: args.DebugImagePath,
            ProjectionOverlayPath: args.ProjectionOverlayPath,
            MaskDebugPath: args.MaskDebugPath,
            UseBorderMask: args.UseBorderMask,
            DetectionsCsvPath: args.DetectionsCsvPath,
            IgnoreTypes: args.IgnoreTypes,
            Seed: args.Seed);

        var result = ScreenshotCalibrator.Calibrate(inputs);
        if (result.Calibration is null)
        {
            Console.Error.WriteLine($"[fail] could not solve: {result.FailureReason}");
            return 1;
        }

        Console.WriteLine();
        PrintCalibrationSummary(result.Calibration, result.AssignedReferences);

        if (args.DryRun)
        {
            Console.WriteLine("[dry-run] baseline JSON unchanged");
            return 0;
        }

        // Phase 7: persist.
        var baselinePath = ResolveBaselinePath(args);
        BaselineFile.UpsertAnchor(baselinePath, args.Area, result.Calibration);
        Console.WriteLine($"[ok] wrote anchors[{args.Area}] -> {baselinePath}");
        return 0;
    }

    private static (double X, double Z)? ResolvePlayerCoord(CliArgs args)
    {
        if (args.PlayerCoord is { } c) return c;
        if (args.PlayerLogPath is null) return null;
        var coord = PlayerLogScanner.MostRecentPositionInArea(args.PlayerLogPath, args.Area)
            ?? throw new UserFacingException(
                $"--player-log given but no recent [Status] line found in {args.PlayerLogPath} for area {args.Area}");
        Console.WriteLine($"[player-log] resolved {args.Area} position: ({coord.X:0.##}, {coord.Z:0.##})");
        return (coord.X, coord.Z);
    }

    private static string ResolveBaselinePath(CliArgs args)
    {
        if (args.BaselinePath is not null) return args.BaselinePath;
        var p = Path.Combine(RepoRoot(), "src", "Mithril.MapCalibration", "BundledData", "map-calibration-baseline.json");
        if (!File.Exists(p))
        {
            throw new UserFacingException($"default baseline path not found: {p} (pass --baseline)");
        }
        return p;
    }

    private static string ResolveLandmarksPath(CliArgs args)
    {
        if (args.LandmarksPath is not null) return args.LandmarksPath;
        var p = Path.Combine(RepoRoot(), "src", "Mithril.Shared", "Reference", "BundledData", "landmarks.json");
        if (!File.Exists(p))
        {
            throw new UserFacingException($"default landmarks.json path not found: {p} (pass --landmarks)");
        }
        return p;
    }

    private static string ResolveNpcsPath(CliArgs args)
    {
        if (args.NpcsPath is not null) return args.NpcsPath;
        var p = Path.Combine(RepoRoot(), "src", "Mithril.Shared", "Reference", "BundledData", "npcs.json");
        if (!File.Exists(p))
        {
            throw new UserFacingException($"default npcs.json path not found: {p} (pass --npcs)");
        }
        return p;
    }

    private static string RepoRoot()
    {
        // Climb from the tool's bin dir up to the repo root (it has Mithril.slnx).
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new UserFacingException(
            $"could not locate Mithril.slnx walking up from {AppContext.BaseDirectory}");
    }

    private static string DefaultScratch(string sub)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mithril-852", sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PrintCalibrationSummary(AreaCalibration cal, IReadOnlyList<AssignedReference> refs)
    {
        Console.WriteLine($"[solve] residualPixels={cal.ResidualPixels:0.00}  scale={cal.Scale:0.0000} px/unit  rotation={cal.RotationRadians:0.000} rad  origin=({cal.OriginX:0.0},{cal.OriginY:0.0})  mirrorNorth={cal.MirrorNorth}");
        Console.WriteLine($"[solve] {cal.ReferenceCount} references used (per-inlier residual = distance from projected to detected pixel):");
        // #1076 Phase 7.5: project through a texture-frame view of the solved
        // calibration; per-inlier residuals are texture-pixel.
        var calTex = new WorldToTextureCalibration(
            cal.OriginX, cal.OriginY, cal.Scale, cal.RotationRadians,
            cal.MirrorNorth, cal.CalibrationZoom);
        foreach (var r in refs)
        {
            // Per-inlier residual — reveals whether the RMS is dominated by a
            // single outlier (suggesting iterative RANSAC would help) or evenly
            // distributed across all inliers (suggesting PG's non-affine ceiling).
            var projected = calTex.ToTexture(new Mithril.MapCalibration.WorldCoord(r.WorldX, 0, r.WorldZ));
            var dx = projected.X - r.PixelX;
            var dy = projected.Y - r.PixelY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            Console.WriteLine($"        {r.Label,-50} world=({r.WorldX,7:0.0},{r.WorldZ,7:0.0})  pixel=({r.PixelX,7:0.0},{r.PixelY,7:0.0})  res={dist,5:0.00}px  score={r.MatchScore:0.000}");
        }
        if (cal.ResidualPixels >= 12.0)
        {
            Console.WriteLine($"[warn] residualPixels={cal.ResidualPixels:0.00} >= 12.0 (CalibrationGoodResidualPx). Result is below shipping bar.");
        }
    }
}

/// <summary>Bundle of everything <see cref="ScreenshotCalibrator"/> needs.</summary>
internal sealed record CalibrationInputs(
    string ScreenshotPath,
    string AreaMapPath,
    string IconsDir,
    string LandmarksJsonPath,
    string NpcsJsonPath,
    string Area,
    double Zoom,
    (double X, double Z)? PlayerCoord,
    (int X, int Y, int W, int H)? MapRectOverride,
    double DetectionThreshold,
    int IconRenderSizeOverride,
    IReadOnlyDictionary<string, (int W, int H)> IconSizeOverrides,
    IReadOnlySet<string> ExcludedLandmarkTypes,
    string? DebugImagePath,
    string? ProjectionOverlayPath,
    string? MaskDebugPath,
    bool UseBorderMask,
    string? DetectionsCsvPath,
    bool IgnoreTypes,
    (double Rot, double Scale, double Ox, double Oy, bool Mirror)? Seed);

internal sealed record AssignedReference(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

internal sealed record CalibrationResult(
    AreaCalibration? Calibration,
    IReadOnlyList<AssignedReference> AssignedReferences,
    string? FailureReason);
