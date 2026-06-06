using System.Text.Json;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbePhase
{
    public static int Run(CliArgs args)
    {
        // Resolve --bundle-dir into the four file paths if not explicitly overridden.
        LoadedBundle? loadedBundle = null;
        string? mapRectJsonPath = args.MapRectJsonPath;
        string? recoveredCalJsonPath = args.RecoveredCalJsonPath;
        string? alignedDeviationPath = args.AlignedDeviationPath;
        string? detectionsJsonPath = args.DetectionsJsonPath;

        if (!string.IsNullOrEmpty(args.BundleDir))
        {
            loadedBundle = BundleLoader.Open(args.BundleDir);
            mapRectJsonPath ??= loadedBundle.Attempt.Files.MapRect is { } mr ? Path.Combine(args.BundleDir, mr) : null;
            recoveredCalJsonPath ??= loadedBundle.Attempt.Files.RecoveredCalibration is { } rc ? Path.Combine(args.BundleDir, rc) : null;
            alignedDeviationPath ??= loadedBundle.DeviationPath;
            detectionsJsonPath ??= loadedBundle.Attempt.Files.Detections is { } d ? Path.Combine(args.BundleDir, d) : null;
        }

        // Local helper: deserialise + materialise a MapRect from the bundle's
        // 04-maprect.json. Used by both texture-pixel-space truth-cal branches.
        MapRect LoadMapRectJson(string path)
        {
            var j = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                BundleJsonContext.Default.MapRectJson)!;
            return new MapRect(
                j.OriginX, j.OriginY,
                j.Width, j.Height,
                j.TextureWidth, j.TextureHeight);
        }

        // Derive truth-cal per precedence:
        //   1. --truth-cal           (crop-pixel space, wins outright)
        //   2. --hand-truth-cal      (texture-pixel space + MapRect → conversion)
        //   3. --recovered-cal-json  (texture-pixel space + MapRect → conversion)
        CandidateTransform? truth = null;

        if (args.TruthCal is { } tc)
        {
            truth = new CandidateTransform(tc.Scale, tc.Rot, tc.Mirror, tc.Ox, tc.Oy);
        }
        else if (args.HandTruthCal is { } htc)
        {
            if (mapRectJsonPath is null)
            {
                Console.Error.WriteLine("[err] --hand-truth-cal requires --maprect-json (directly or via --bundle-dir).");
                return 2;
            }
            var mapRectForHandCal = LoadMapRectJson(mapRectJsonPath);
            var handCalJson = new RecoveredCalibrationJson(
                SchemaVersion: 1,
                Scale: htc.Scale, RotationRadians: htc.Rot,
                OriginX: htc.Ox, OriginY: htc.Oy,
                MirrorNorth: htc.Mirror,
                ResidualPixels: 0.0,
                ReferenceCount: 0,
                Source: "HandSupplied",
                Inliers: System.Array.Empty<InlierJson>());
            truth = MapRectConversion.FromRecoveredCalibration(handCalJson, mapRectForHandCal, out var handAnisoPct);
            if (handAnisoPct > 1.0)
                Console.Error.WriteLine($"[warn] MapRect resize is anisotropic by {handAnisoPct:0.00}%; using geometric mean.");
            Console.Error.WriteLine("[truth] using --hand-truth-cal (texture-pixel space → crop-pixel via MapRect).");
        }
        else if (mapRectJsonPath is not null && recoveredCalJsonPath is not null)
        {
            var mapRectForRecoveredCal = LoadMapRectJson(mapRectJsonPath);
            var recoveredCalJson = JsonSerializer.Deserialize(
                File.ReadAllText(recoveredCalJsonPath),
                BundleJsonContext.Default.RecoveredCalibrationJson)!;
            truth = MapRectConversion.FromRecoveredCalibration(recoveredCalJson, mapRectForRecoveredCal, out var anisoPct);
            if (anisoPct > 1.0)
                Console.Error.WriteLine($"[warn] MapRect resize is anisotropic by {anisoPct:0.00}%; using geometric mean.");
            Console.Error.WriteLine(
                $"[truth] using --recovered-cal-json (production's recovered cal, residual {recoveredCalJson.ResidualPixels:0.00} px). " +
                "If production's solve is suspect, override with --hand-truth-cal.");
        }

        if (truth is null)
        {
            Console.Error.WriteLine("[err] No truth-cal: pass --truth-cal, or --hand-truth-cal + --maprect-json, or --bundle-dir/--recovered-cal-json + --maprect-json.");
            return 2;
        }

        // Non-nullable local for use throughout the rest of Run.
        var truthNn = truth.Value;

        // Auto-fill --area / --screenshot / --map-rect from the bundle when the
        // caller has not supplied them explicitly.  Explicit flags always win.
        string area = args.Area;
        string? screenshotPath = string.IsNullOrEmpty(args.ScreenshotPath) ? null : args.ScreenshotPath;
        (int X, int Y, int W, int H)? mapRect = args.MapRect;

        if (loadedBundle is not null)
        {
            (area, screenshotPath, mapRect) = BundleArgsResolver.Resolve(
                loadedBundle, args.BundleDir!,
                mapRectJsonPath,
                alignedDeviationPath,
                explicitArea: area,
                explicitScreenshotPath: screenshotPath,
                explicitMapRect: mapRect);
        }

        // Prereq guards (relaxed: --screenshot is not required when --aligned-deviation
        // or --bundle-dir already supplies the field source directly).
        if (string.IsNullOrEmpty(screenshotPath) && alignedDeviationPath is null)
        {
            Console.Error.WriteLine("--screenshot required for --phase synthesis-probe (unless --aligned-deviation or --bundle-dir is given)");
            return 2;
        }
        if (mapRect is null)
        {
            Console.Error.WriteLine("--map-rect required for --phase synthesis-probe (auto-detect is not reliable enough for the diagnostic)");
            return 2;
        }
        if (string.IsNullOrEmpty(area))
        {
            Console.Error.WriteLine("--area required for --phase synthesis-probe (unless --bundle-dir is given)");
            return 2;
        }

        using var tracer = SynthesisProbeTracer.Configure(args.TraceConsole, args.OtlpEndpoint);
        using var rootSpan = SynthesisProbeTracer.Source.StartActivity("probe.attempt");
        rootSpan?.SetTag("area", area);
        if (screenshotPath is not null) rootSpan?.SetTag("screenshot", screenshotPath);
        rootSpan?.SetTag("truth.scale", truthNn.Scale);
        rootSpan?.SetTag("truth.rot", truthNn.RotRadians);
        rootSpan?.SetTag("truth.mirror", truthNn.Mirror);

        // Hoist mapRect destructure before the conditional screenshot+base loads
        // since mw/mh are needed by both branches (field build and E5 grid search).
        var (mx, my, mw, mh) = mapRect.Value;
        rootSpan?.SetTag("crop.w", mw);
        rootSpan?.SetTag("crop.h", mh);

        // pgInstall and tpkPath are needed for both the screenshot+base branch
        // (when alignedDeviationPath is null) and for icon template extraction.
        var pgInstall = SteamInstall.FindPgInstall();
        var tpkPath = args.TpkPath ?? RepoPaths.DefaultTpkPath();

        // Load screenshot + crop to map rect (only needed when building the field
        // from scratch; skipped when --aligned-deviation already provides the layer).
        GrayImage? screenshotCrop = null;
        GrayImage? alignedBase = null;
        if (alignedDeviationPath is null)
        {
            var screenshot = ImageIo.LoadGray(screenshotPath!);
            screenshotCrop = ImageOps.Crop(screenshot, mx, my, mw, mh);

            // Locate + load the aligned base texture, resampled to the crop dimensions.
            if (!string.IsNullOrEmpty(args.AlignedBasePath))
            {
                if (!File.Exists(args.AlignedBasePath))
                {
                    Console.Error.WriteLine($"--aligned-base file not found: {args.AlignedBasePath}");
                    return 2;
                }
                alignedBase = ImageIo.LoadGray(args.AlignedBasePath);
                if (alignedBase.Width != mw || alignedBase.Height != mh)
                {
                    Console.Error.WriteLine(
                        $"--aligned-base dimensions {alignedBase.Width}x{alignedBase.Height} " +
                        $"don't match --map-rect crop dims {mw}x{mh}");
                    return 2;
                }
            }
            else
            {
                var mapDir = args.MapDir ?? RepoPaths.DefaultMapsCacheDir();
                var mapPng = MapTextureExtractor.EnsureExtracted(pgInstall, mapDir, area);
                var baseTexture = ImageIo.LoadGray(mapPng);
                alignedBase = ImageOps.Resize(baseTexture, mw, mh);
            }
        }

        // Load icon templates from the extracted icons cache, scaled to the
        // requested render size.  IconTemplateExtractor has no LoadAll(dir, size)
        // method — build IconTemplate objects manually from the IconIndex.
        var iconsDir = args.IconsDir ?? RepoPaths.DefaultIconsCacheDir();
        IconTemplateExtractor.EnsureExtracted(pgInstall, iconsDir, tpkPath);
        var iconIndex = IconTemplateExtractor.Load(iconsDir);

        int renderSizePx = args.IconRenderSize > 0 ? args.IconRenderSize : 16;
        var templates = new List<IconTemplate>();
        foreach (var meta in iconIndex.Icons)
        {
            var iconPath = Path.Combine(iconsDir, meta.File);
            if (!File.Exists(iconPath))
            {
                Console.WriteLine($"  ! template file missing: {iconPath} (skipping)");
                continue;
            }
            var (gray, alpha) = ImageIo.LoadGrayAndAlpha(iconPath);
            // Scale so the largest source dimension lands at renderSizePx — same
            // logic ScreenshotCalibrator uses when icon templates are large artwork.
            int maxDim = Math.Max(gray.Width, gray.Height);
            int rw = Math.Max(1, gray.Width * renderSizePx / maxDim);
            int rh = Math.Max(1, gray.Height * renderSizePx / maxDim);
            var grayR = (rw == gray.Width && rh == gray.Height) ? gray : ImageOps.Resize(gray, rw, rh);
            var alphaR = (rw == alpha.Width && rh == alpha.Height) ? alpha : ImageOps.Resize(alpha, rw, rh);
            templates.Add(new IconTemplate(
                Name: meta.Name,
                LandmarkType: meta.LandmarkType,
                PivotX: meta.PivotX,
                PivotY: meta.PivotY,
                Gray: grayR,
                Alpha: alphaR));
        }

        if (templates.Count == 0)
        {
            Console.Error.WriteLine("no icon templates loaded — run --phase extract-icons first");
            return 1;
        }

        // Build per-type likelihood fields by sliding each template over the
        // positive-deviation layer. When --aligned-deviation is provided, load
        // the pre-computed post-ECC deviation directly (skips screenshot-minus-base
        // subtraction). Otherwise fall back to Build(screenshotCrop, alignedBase).
        var fieldsByType = new Dictionary<string, double[,]>(StringComparer.Ordinal);
        if (alignedDeviationPath is not null)
        {
            var deviation = ImageIo.LoadGray(alignedDeviationPath);
            bool applyRimMask = !args.SkipRimMask;
            foreach (var template in templates)
            {
                using var fieldSpan = SynthesisProbeTracer.Source.StartActivity("field.build");
                fieldSpan?.SetTag("template.type", template.LandmarkType);
                fieldSpan?.SetTag("template.size_px", Math.Max(template.Gray.Width, template.Gray.Height));
                fieldSpan?.SetTag("source", "aligned-deviation");
                fieldSpan?.SetTag("rim_masked", applyRimMask);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                fieldsByType[template.LandmarkType] = IconLikelihoodField.LoadDeviationAsField(
                    deviation, template, applyRimMask, IconLikelihoodField.DefaultDevThr);
                fieldSpan?.SetTag("duration_ms", sw.ElapsedMilliseconds);
            }
        }
        else
        {
            foreach (var template in templates)
            {
                using var fieldSpan = SynthesisProbeTracer.Source.StartActivity("field.build");
                fieldSpan?.SetTag("template.type", template.LandmarkType);
                fieldSpan?.SetTag("template.size_px", Math.Max(template.Gray.Width, template.Gray.Height));
                fieldSpan?.SetTag("source", "screenshot-minus-base");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                fieldsByType[template.LandmarkType] = IconLikelihoodField.Build(screenshotCrop!, alignedBase!, template);
                fieldSpan?.SetTag("duration_ms", sw.ElapsedMilliseconds);
            }
        }

        // Load reference points (landmarks + NPCs) for the area.
        var refs = ProbeReferences.Load(
            args.LandmarksPath ?? ProbeReferences.DefaultLandmarksPath(),
            args.NpcsPath ?? ProbeReferences.DefaultNpcsPath(),
            area);

        // Output directory + writer.
        var outDir = Path.Combine(RepoPaths.RepoRoot(), "study", "synthesis-probe", area);
        using var writer = new SynthesisProbeWriter(outDir);

        // Dump field PNGs — one per landmark type.
        foreach (var (type, field) in fieldsByType)
            writer.WriteFieldPng(type, field);

        // E1 — truth score (always).
        E1_TruthScore.Run(fieldsByType, refs, truthNn, writer);

        // E2 — translation sweep ±2×templateSizePx (always).
        E2_TranslationSweep.Run(fieldsByType, refs, truthNn, templateSizePx: renderSizePx, writer);

        // E3 — scale sweep ±25 % in 1 % steps (always).
        E3_ScaleSweep.Run(fieldsByType, refs, truthNn, writer);

        // E4 — RANSAC seed scores (only when a seeds CSV is supplied).
        if (!string.IsNullOrEmpty(args.RansacSeedsCsvPath))
            E4_RansacSeedScore.Run(fieldsByType, refs, truthNn, args.RansacSeedsCsvPath, writer);

        // E5 — cold-grid global search + local refine (always).
        // Narrow the scale bracket to ±20% of the expected scale derived from
        // truth, excluding the tiny-scale degeneracy.
        var scaleBracket = E5_ColdGrid.BracketAroundExpected(truthNn.Scale, fractionAbove: 0.2);
        E5_ColdGrid.Run(
            fieldsByType, refs, truthNn,
            scaleBracket: scaleBracket,
            scaleSamples: 16,
            cropWidth: mw,
            cropHeight: mh,
            gridStepPx: renderSizePx,
            templateSizePx: renderSizePx,
            writer);

        Console.WriteLine($"[synthesis-probe] artifacts written to {outDir}");
        return 0;
    }
}
