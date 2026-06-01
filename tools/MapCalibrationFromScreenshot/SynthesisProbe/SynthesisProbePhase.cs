using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.Tools.MapCalibration.Common;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Experiments;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

internal static class SynthesisProbePhase
{
    public static int Run(CliArgs args)
    {
        // Prereq guards.
        if (args.TruthCal is null)
        {
            Console.Error.WriteLine("--truth-cal required for --phase synthesis-probe");
            return 2;
        }
        if (string.IsNullOrEmpty(args.ScreenshotPath))
        {
            Console.Error.WriteLine("--screenshot required for --phase synthesis-probe");
            return 2;
        }
        if (args.MapRect is null)
        {
            Console.Error.WriteLine("--map-rect required for --phase synthesis-probe (auto-detect is not reliable enough for the diagnostic)");
            return 2;
        }

        using var tracer = SynthesisProbeTracer.Configure(args.TraceConsole, args.OtlpEndpoint);
        using var rootSpan = SynthesisProbeTracer.Source.StartActivity("probe.attempt");
        rootSpan?.SetTag("area", args.Area);
        rootSpan?.SetTag("screenshot", args.ScreenshotPath);

        var truth = new CandidateTransform(
            Scale: args.TruthCal.Value.Scale,
            RotRadians: args.TruthCal.Value.Rot,
            Mirror: args.TruthCal.Value.Mirror,
            Tx: args.TruthCal.Value.Ox,
            Ty: args.TruthCal.Value.Oy);
        rootSpan?.SetTag("truth.scale", truth.Scale);
        rootSpan?.SetTag("truth.rot", truth.RotRadians);
        rootSpan?.SetTag("truth.mirror", truth.Mirror);

        // Load screenshot + crop to map rect.
        var screenshot = ImageIo.LoadGray(args.ScreenshotPath);
        var (mx, my, mw, mh) = args.MapRect.Value;
        var screenshotCrop = ImageOps.Crop(screenshot, mx, my, mw, mh);
        rootSpan?.SetTag("crop.w", mw);
        rootSpan?.SetTag("crop.h", mh);

        // Locate + load the aligned base texture, resampled to the crop dimensions.
        var pgInstall = SteamInstall.FindPgInstall();
        var mapDir = args.MapDir ?? RepoPaths.DefaultMapsCacheDir();
        var tpkPath = args.TpkPath ?? RepoPaths.DefaultTpkPath();
        var mapPng = MapTextureExtractor.EnsureExtracted(pgInstall, mapDir, args.Area);
        var baseTexture = ImageIo.LoadGray(mapPng);
        var alignedBase = ImageOps.Resize(baseTexture, mw, mh);

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
        // positive-deviation layer (screenshot minus aligned base).
        var fieldsByType = new Dictionary<string, double[,]>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            using var fieldSpan = SynthesisProbeTracer.Source.StartActivity("field.build");
            fieldSpan?.SetTag("template.type", template.LandmarkType);
            fieldSpan?.SetTag("template.size_px", Math.Max(template.Gray.Width, template.Gray.Height));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            fieldsByType[template.LandmarkType] = IconLikelihoodField.Build(screenshotCrop, alignedBase, template);
            fieldSpan?.SetTag("duration_ms", sw.ElapsedMilliseconds);
        }

        // Load reference points (landmarks + NPCs) for the area.
        var refs = ProbeReferences.Load(
            args.LandmarksPath ?? ProbeReferences.DefaultLandmarksPath(),
            args.NpcsPath ?? ProbeReferences.DefaultNpcsPath(),
            args.Area);

        // Output directory + writer.
        var outDir = Path.Combine(RepoPaths.RepoRoot(), "study", "synthesis-probe", args.Area);
        using var writer = new SynthesisProbeWriter(outDir);

        // Dump field PNGs — one per landmark type.
        foreach (var (type, field) in fieldsByType)
            writer.WriteFieldPng(type, field);

        // E1 — truth score (always).
        E1_TruthScore.Run(fieldsByType, refs, truth, writer);

        // E2 — translation sweep ±2×templateSizePx (always).
        E2_TranslationSweep.Run(fieldsByType, refs, truth, templateSizePx: renderSizePx, writer);

        // E3 — scale sweep ±25 % in 1 % steps (always).
        E3_ScaleSweep.Run(fieldsByType, refs, truth, writer);

        // E4 — RANSAC seed scores (only when a seeds CSV is supplied).
        if (!string.IsNullOrEmpty(args.RansacSeedsCsvPath))
            E4_RansacSeedScore.Run(fieldsByType, refs, truth, args.RansacSeedsCsvPath, writer);

        // E5 — cold-grid global search + local refine (always).
        E5_ColdGrid.Run(
            fieldsByType, refs, truth,
            scaleBracket: (0.1, 2.0),
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
