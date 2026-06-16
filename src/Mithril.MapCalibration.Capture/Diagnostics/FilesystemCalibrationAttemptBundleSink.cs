using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Writes a per-attempt diagnostic bundle (the 11-file subdir described in the
/// design spec) to the configured root. Fail-soft: every error is caught,
/// logged, and dropped — never propagated into the engine.
/// </summary>
public sealed class FilesystemCalibrationAttemptBundleSink : ICalibrationAttemptBundleSink
{
    private static readonly string AssemblyVersion =
        typeof(FilesystemCalibrationAttemptBundleSink).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(FilesystemCalibrationAttemptBundleSink).Assembly.GetName().Version?.ToString()
            ?? "unknown";

    private readonly string _root;
    private readonly ILogger? _logger;
    private readonly IAttemptBundleVisualizer _visualizer;
    // mithril#1061: the sink reads FallbackPadPx live so the bundle's LocatorBest.padPx
    // reflects the actual pad the refiner used, not the option default. Optional so test
    // graphs that don't supply MapCalibrationLocateOptions still construct the sink (the
    // pad value falls back to the static default in that path — same behaviour as before
    // the wiring landed).
    private readonly MapCalibrationLocateOptions? _options;

    public FilesystemCalibrationAttemptBundleSink(string root, ILogger? logger, IAttemptBundleVisualizer visualizer)
        : this(root, logger, visualizer, options: null)
    {
    }

    public FilesystemCalibrationAttemptBundleSink(
        string root,
        ILogger? logger,
        IAttemptBundleVisualizer visualizer,
        MapCalibrationLocateOptions? options)
    {
        _root = root;
        _logger = logger;
        _visualizer = visualizer;
        _options = options;
    }

    public void Write(CalibrationAttemptContext context)
    {
        try
        {
            if (!OutcomeVocabulary.ShouldWriteBundle(context.Outcome)) return;

            Directory.CreateDirectory(_root);
            var subdir = Path.Combine(_root, MakeSubdirName(context));
            Directory.CreateDirectory(subdir);

            var files = new AttemptFilesJson(
                RawScreenshot: TryWriteRawScreenshot(subdir, context),
                GrayScreenshot: TryWriteGrayScreenshot(subdir, context),
                MapRect: TryWriteMapRectJson(subdir, context),
                BaseTextureResampled: TryWriteBaseTextureResampled(subdir, context),
                AlignedScreenshot: TryWriteAlignedScreenshot(subdir, context),
                Deviation: TryWriteDeviation(subdir, context),
                DetectionsImage: TryWriteDetectionsImage(subdir, context),
                ProjectionOverlay: TryWriteProjectionOverlay(subdir, context),
                Detections: TryWriteDetectionsJson(subdir, context),
                RecoveredCalibration: TryWriteRecoveredCalibrationJson(subdir, context),
                BlobTemplateScores: TryWriteBlobTemplateScoresJson(subdir, context),
                // mithril#1123: detector-pipeline observability — 10c JSON +
                // 10 PNGs for the stage masks × orientations.
                BlobPipeline: TryWriteBlobPipelineJson(subdir, context),
                Foreground: TryWriteForegroundPng(subdir, context, rotate180: false),
                ForegroundR180: TryWriteForegroundPng(subdir, context, rotate180: true),
                RimMask: TryWriteRimMaskPng(subdir, context, pipeline: RimMaskPipeline.BlobDetection, rotate180: false),
                RimMaskR180: TryWriteRimMaskPng(subdir, context, pipeline: RimMaskPipeline.BlobDetection, rotate180: true),
                SynthRimMask: TryWriteRimMaskPng(subdir, context, pipeline: RimMaskPipeline.SynthesisJ, rotate180: false),
                SynthRimMaskR180: TryWriteRimMaskPng(subdir, context, pipeline: RimMaskPipeline.SynthesisJ, rotate180: true),
                Morphed: TryWriteMorphedPng(subdir, context, rotate180: false),
                MorphedR180: TryWriteMorphedPng(subdir, context, rotate180: true),
                BlobClassification: TryWriteBlobClassificationPng(subdir, context, rotate180: false),
                BlobClassificationR180: TryWriteBlobClassificationPng(subdir, context, rotate180: true),
                // mithril#1116: OR-combined boundary + fog mask the engine fed into
                // DetectionRequest.DeviationMask. Omitted when DeviationMaskingEnabled
                // was false or both upstream sources contributed nothing (so the
                // engine left context.DeviationMaskImage null).
                DeviationMask: TryWriteDeviationMaskPng(subdir, context));

            WriteAttemptJson(subdir, context, files);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Calibration attempt bundle write failed for {Area}. Attempt continues.", context.Area);
        }
    }

    private static string MakeSubdirName(CalibrationAttemptContext ctx)
    {
        var stamp = ctx.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var area = Sanitize(ctx.Area);
        return $"{area}-{stamp}-{ctx.Outcome}";
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private string? TryWriteRawScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.RawCapture is null) return null;
            var src = BitmapSource.Create(
                ctx.RawCapture.Width, ctx.RawCapture.Height, 96, 96,
                PixelFormats.Bgra32, null,
                ctx.RawCapture.Bgra, ctx.RawCapture.Width * 4);
            return WritePng(dir, "02-screenshot-raw.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "02-screenshot-raw write failed"); return null; }
    }

    private string? TryWriteGrayScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.GrayCapture is null) return null;
            var src = BitmapSource.Create(
                ctx.GrayCapture.Width, ctx.GrayCapture.Height, 96, 96,
                PixelFormats.Gray8, null,
                ctx.GrayCapture.Pixels, ctx.GrayCapture.Width);
            return WritePng(dir, "03-screenshot-gray.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "03-screenshot-gray write failed"); return null; }
    }

    private string? TryWriteMapRectJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.MapRect is null) return null;
            var dto = new MapRectJson(1,
                ctx.MapRect.OriginX, ctx.MapRect.OriginY,
                ctx.MapRect.Width, ctx.MapRect.Height,
                ctx.MapRect.TextureWidth, ctx.MapRect.TextureHeight);
            return WriteJson(dir, "04-maprect.json", dto, CalibrationBundleJsonContext.Default.MapRectJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "04-maprect write failed"); return null; }
    }

    private string? TryWriteBaseTextureResampled(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.BaseTextureResampled is null) return null;
            var src = BitmapSource.Create(
                ctx.BaseTextureResampled.Width, ctx.BaseTextureResampled.Height, 96, 96,
                PixelFormats.Gray8, null,
                ctx.BaseTextureResampled.Pixels, ctx.BaseTextureResampled.Width);
            return WritePng(dir, "05-base-texture-resampled.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "05-base-texture-resampled write failed"); return null; }
    }

    private string? TryWriteAlignedScreenshot(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.AlignedCrop is null) return null;
            var src = BitmapSource.Create(
                ctx.AlignedCrop.Width, ctx.AlignedCrop.Height, 96, 96,
                PixelFormats.Gray8, null,
                ctx.AlignedCrop.Pixels, ctx.AlignedCrop.Width);
            return WritePng(dir, "06-aligned-screenshot.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "06-aligned-screenshot write failed"); return null; }
    }

    // mithril#1116: write the OR-combined boundary + fog mask the engine fed into
    // DetectionRequest.DeviationMask. Same Gray8 PNG shape as 03-screenshot-gray;
    // null when the engine left context.DeviationMaskImage null (masking disabled
    // or both upstream sources empty).
    private string? TryWriteDeviationMaskPng(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.DeviationMaskImage is null) return null;
            var img = ctx.DeviationMaskImage;
            var src = BitmapSource.Create(
                img.Width, img.Height, 96, 96,
                PixelFormats.Gray8, null,
                img.Pixels, img.Width);
            return WritePng(dir, "07a-deviation-mask.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "07a-deviation-mask write failed"); return null; }
    }

    private string? TryWriteDeviation(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.AlignedCrop is null || ctx.AlignedTexture is null) return null;
            var src = _visualizer.RenderDeviation(ctx.AlignedCrop, ctx.AlignedTexture);
            return WritePng(dir, "07-deviation.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "07-deviation write failed"); return null; }
    }

    private string? TryWriteDetectionsImage(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Detections is null || ctx.GrayCapture is null) return null;
            var src = _visualizer.RenderDetectionsOverlay(ctx.GrayCapture, ctx.Result.Detections, renderSizePx: 16);
            return WritePng(dir, "08-detections.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "08-detections write failed"); return null; }
    }

    private string? TryWriteProjectionOverlay(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Calibration is null
                || ctx.RawCapture is null
                || ctx.MapRect is null
                || ctx.References is null
                || ctx.Result.Inliers is null) return null;
            var src = _visualizer.RenderProjectionOverlay(
                ctx.RawCapture, ctx.MapRect, ctx.Result.Calibration,
                ctx.References, ctx.Result.Inliers, renderSizePx: 16);
            return WritePng(dir, "09-projection-overlay.png", src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "09-projection-overlay write failed"); return null; }
    }

    private string? TryWriteDetectionsJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Detections is null) return null;
            var detections = ctx.Result.Detections
                .Select(d => new DetectionJson(d.LandmarkType, d.IconName, d.Anchor.X, d.Anchor.Y, d.Score))
                .ToArray();
            var dto = new DetectionsJson(1, 16, detections);
            return WriteJson(dir, "10-detections.json", dto, CalibrationBundleJsonContext.Default.DetectionsJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "10-detections write failed"); return null; }
    }

    private string? TryWriteRecoveredCalibrationJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.Result?.Calibration is null) return null;
            var cal = ctx.Result.Calibration;
            var inliers = (ctx.Result.Inliers ?? Array.Empty<TypeAwareRansacSolver.AssignedReference>())
                .Select(i => new InlierJson(i.Label, i.WorldX, i.WorldZ, i.PixelX, i.PixelY, i.MatchScore))
                .ToArray();
            var dto = new RecoveredCalibrationJson(1,
                cal.Scale, cal.RotationRadians, cal.OriginX, cal.OriginY,
                cal.MirrorNorth, cal.ResidualPixels,
                cal.ReferenceCount, cal.Source.ToString(), inliers);
            return WriteJson(dir, "11-recovered-cal.json", dto, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "11-recovered-cal write failed"); return null; }
    }

    // mithril#1121: per-blob × per-template NCC observation dump. Omitted in two
    // cases:
    //   - null: synthetic test paths only — AutoCalibrationEngine always assigns
    //     the list (even empty) before calling the sink. Production bundles
    //     never see null here.
    //   - empty: legitimate runtime state when the deviation map produced zero
    //     blobs. Matches the existing per-file convention (RecoveredCalibration
    //     and ProjectionOverlay also omit on "no data produced") rather than
    //     emitting an empty array.
    // Use 07-deviation.png alongside this file's absence to distinguish the two.
    private string? TryWriteBlobTemplateScoresJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            if (ctx.BlobTemplateScores is not { Count: > 0 } scores) return null;
            var dtos = scores.Select(s => new BlobTemplateScoreJson(
                BlobOrdinal: s.BlobOrdinal,
                BlobMinX: s.BlobMinX,
                BlobMinY: s.BlobMinY,
                BlobWidth: s.BlobWidth,
                BlobHeight: s.BlobHeight,
                BlobArea: s.BlobArea,
                TemplateName: s.TemplateName,
                TemplateLandmarkType: s.TemplateLandmarkType,
                TemplateWidth: s.TemplateWidth,
                TemplateHeight: s.TemplateHeight,
                Score: s.Score,
                TypeFloor: s.TypeFloor,
                AboveFloor: s.AboveFloor,
                Skipped: s.Skipped,
                Rotate180: s.Rotate180)).ToArray();
            // mithril#1123 D3.a: schema v1→v2, BlobIndex→BlobOrdinal with all-blobs
            // semantics (the same int identifies the same physical blob in 10c).
            var dto = new BlobTemplateScoresJson(SchemaVersion: 2, Scores: dtos);
            return WriteJson(dir, "10b-blob-template-scores.json", dto,
                CalibrationBundleJsonContext.Default.BlobTemplateScoresJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "10b-blob-template-scores write failed"); return null; }
    }

    // mithril#1123: detector-pipeline observability dump (10c-blob-pipeline.json).
    // Omitted when ALL four context lists are null OR empty — same convention as
    // 10b. The four sections (deviation / rim masks / morph / blobs) are
    // co-located so triage workflows open one file per attempt instead of four.
    private string? TryWriteBlobPipelineJson(string dir, CalibrationAttemptContext ctx)
    {
        try
        {
            var devSnaps = ctx.DeviationSnapshots;
            var rimSnaps = ctx.RimMaskSnapshots;
            var morphSnaps = ctx.MorphSnapshots;
            var blobClasses = ctx.BlobClassifications;

            bool hasAnyData =
                (devSnaps is { Count: > 0 }) ||
                (rimSnaps is { Count: > 0 }) ||
                (morphSnaps is { Count: > 0 }) ||
                (blobClasses is { Count: > 0 });
            if (!hasAnyData) return null;

            var devDtos = (devSnaps ?? Array.Empty<DeviationSnapshot>())
                .Select(d => new DeviationSectionJson(
                    Rotate180: d.Rotate180,
                    Width: d.Width, Height: d.Height, Win: d.Win,
                    Threshold: d.Threshold, MeanNcc: d.MeanNcc,
                    Min: d.Min, Max: d.Max, Mean: d.Mean,
                    P50: d.P50, P95: d.P95, P99: d.P99,
                    AboveThresholdCount: d.AboveThresholdCount))
                .ToArray();

            // mithril#1125: the in-memory record carries the RimMaskPipeline enum
            // and nullable Fg counts. The DTO is the wire format and preserves
            // the lowercase / snake_case discriminator + -1 sentinel — pre-#1125
            // bundle readers (jq, downstream tools) round-trip unchanged.
            var rimDtos = (rimSnaps ?? Array.Empty<RimMaskSnapshot>())
                .Select(r => new RimMaskSectionJson(
                    Pipeline: ToWire(r.Pipeline),
                    Rotate180: r.Rotate180,
                    Width: r.Width, Height: r.Height,
                    Threshold: r.Threshold,
                    RimPixelCount: r.RimPixelCount,
                    FgInputCount: r.FgInputCount ?? -1,
                    FgSurvivorCount: r.FgSurvivorCount ?? -1))
                .ToArray();

            var morphDtos = (morphSnaps ?? Array.Empty<MorphSnapshot>())
                .Select(m => new MorphSectionJson(
                    Rotate180: m.Rotate180,
                    Width: m.Width, Height: m.Height,
                    CloseRadius: m.CloseRadius,
                    FgInputCount: m.FgInputCount,
                    FgOutputCount: m.FgOutputCount))
                .ToArray();

            // Pixels payload is render-only — not serialised here. mithril#1125:
            // BlobClass is an enum in memory; the DTO emits the wire-format
            // string via ToWire (symmetric with RimMaskPipeline). Decoupling the
            // wire from Enum.ToString() means a future rename like
            // BlobClass.Icon -> BlobClass.IconCandidate keeps "Icon" on the wire
            // (the switch arm calls it out explicitly) rather than silently
            // breaking downstream JSON readers + bundle replay tools.
            var blobDtos = (blobClasses ?? Array.Empty<BlobClassification>())
                .Select(b => new BlobJson(
                    Rotate180: b.Rotate180,
                    BlobOrdinal: b.BlobOrdinal,
                    MinX: b.MinX, MinY: b.MinY,
                    W: b.W, H: b.H, Area: b.Area,
                    Cx: b.Cx, Cy: b.Cy,
                    MeanDev: b.MeanDev, PeakDev: b.PeakDev,
                    Solidity: b.Solidity, Aspect: b.Aspect,
                    BlobClass: ToWire(b.BlobClass)))
                .ToArray();

            var dto = new BlobPipelineJson(
                SchemaVersion: 1,
                Deviation: devDtos,
                RimMasks: rimDtos,
                Morph: morphDtos,
                Blobs: blobDtos);
            return WriteJson(dir, "10c-blob-pipeline.json", dto,
                CalibrationBundleJsonContext.Default.BlobPipelineJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "10c-blob-pipeline write failed"); return null; }
    }

    // mithril#1123: bool[w*h] → Gray8 PNG (true=255, false=0). Used for the
    // 07b foreground, 07c rim, 07d morph masks. Mirrors the existing
    // TryWriteGrayScreenshot in-line BitmapSource.Create pattern (no
    // IAttemptBundleVisualizer extension needed for direct mask visualisation).
    // mithril#1126: mask is ReadOnlyMemory<bool> so the snapshot's read-only
    // contract carries through to the consumer — we iterate the span without
    // mutating.
    private string? TryWriteBoolMaskPng(string dir, string name, int w, int h, ReadOnlyMemory<bool> mask)
    {
        try
        {
            var span = mask.Span;
            var bytes = new byte[w * h];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = span[i] ? (byte)255 : (byte)0;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, bytes, w);
            return WritePng(dir, name, src);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "{Name} write failed", name); return null; }
    }

    private string? TryWriteForegroundPng(string dir, CalibrationAttemptContext ctx, bool rotate180)
    {
        var snap = ctx.DeviationSnapshots?.FirstOrDefault(s => s.Rotate180 == rotate180);
        if (snap is null) return null;
        var name = rotate180 ? "07b-r180-foreground.png" : "07b-foreground.png";
        return TryWriteBoolMaskPng(dir, name, snap.Width, snap.Height, snap.ForegroundBuffer);
    }

    private string? TryWriteRimMaskPng(
        string dir, CalibrationAttemptContext ctx, RimMaskPipeline pipeline, bool rotate180)
    {
        var snap = ctx.RimMaskSnapshots?
            .FirstOrDefault(s => s.Pipeline == pipeline && s.Rotate180 == rotate180);
        if (snap is null) return null;
        // Filename convention matches the spec §6.1 table.
        string name = (pipeline, rotate180) switch
        {
            (RimMaskPipeline.BlobDetection, false) => "07c-rim-mask.png",
            (RimMaskPipeline.BlobDetection, true) => "07c-r180-rim-mask.png",
            (RimMaskPipeline.SynthesisJ, false) => "07c-synth-rim-mask.png",
            (RimMaskPipeline.SynthesisJ, true) => "07c-r180-synth-rim-mask.png",
            _ => throw new ArgumentOutOfRangeException(nameof(pipeline)),
        };
        return TryWriteBoolMaskPng(dir, name, snap.Width, snap.Height, snap.RimMaskBuffer);
    }

    private string? TryWriteMorphedPng(string dir, CalibrationAttemptContext ctx, bool rotate180)
    {
        var snap = ctx.MorphSnapshots?.FirstOrDefault(s => s.Rotate180 == rotate180);
        if (snap is null) return null;
        var name = rotate180 ? "07d-r180-morphed.png" : "07d-morphed.png";
        return TryWriteBoolMaskPng(dir, name, snap.Width, snap.Height, snap.FgAfterMorphBuffer);
    }

    // mithril#1123: per-pixel labelled PNG. Walks each BlobClassification's
    // Pixels list (render-only payload, retained on the in-memory record but
    // NOT serialised to 10c JSON) and paints the colour of BlobClass into a
    // Bgra32 buffer. Background pixels stay black; the spatial layout of the
    // pipeline outcome surfaces in one image (the 07e companion to 10c).
    private string? TryWriteBlobClassificationPng(
        string dir, CalibrationAttemptContext ctx, bool rotate180)
    {
        try
        {
            var classifications = ctx.BlobClassifications?
                .Where(c => c.Rotate180 == rotate180)
                .ToArray();
            if (classifications is null || classifications.Length == 0) return null;

            // Use the first record's dims as canvas size (all records from the
            // same orientation come from the same Detect invocation, so dims
            // are consistent — but we don't carry them on BlobClassification
            // itself; pull from a co-orientation DeviationSnapshot if present).
            var devSnap = ctx.DeviationSnapshots?.FirstOrDefault(d => d.Rotate180 == rotate180);
            if (devSnap is null) return null;
            int w = devSnap.Width, h = devSnap.Height;

            var bgra = new byte[w * h * 4];  // default = black-fill (0,0,0,0)
            foreach (var c in classifications)
            {
                // Colour map: Icon=green, Fog=blue-ish, Structure=red, Noise=dim-grey.
                // Triager can spot the NPC-pip cluster region by colour at a glance.
                // mithril#1125 (clarified post-review): BlobClass is the typed
                // enum, but the `_` arm here is defensive against rogue
                // (BlobClass)N casts — NOT exhaustive at compile time. A future
                // BlobClass member would silently fall through to the default
                // colour instead of producing a compile error; update this
                // switch explicitly when adding values.
                var (b, g, r) = c.BlobClass switch
                {
                    BlobClass.Icon      => ((byte)0,   (byte)200, (byte)0),
                    BlobClass.Fog       => ((byte)200, (byte)100, (byte)40),
                    BlobClass.Structure => ((byte)0,   (byte)0,   (byte)200),
                    BlobClass.Noise     => ((byte)80,  (byte)80,  (byte)80),
                    _                   => ((byte)0,   (byte)0,   (byte)0),
                };
                foreach (var pixIdx in c.Pixels)
                {
                    int ofs = pixIdx * 4;
                    bgra[ofs]     = b;
                    bgra[ofs + 1] = g;
                    bgra[ofs + 2] = r;
                    bgra[ofs + 3] = 255;
                }
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            var name = rotate180 ? "07e-r180-blob-classification.png" : "07e-blob-classification.png";
            return WritePng(dir, name, src);
        }
        catch (Exception ex)
        {
            var name = rotate180 ? "07e-r180-blob-classification" : "07e-blob-classification";
            _logger?.LogWarning(ex, "{Name} write failed", name);
            return null;
        }
    }

    private void WriteAttemptJson(string dir, CalibrationAttemptContext ctx, AttemptFilesJson files)
    {
        try
        {
            var finalized = DateTimeOffset.UtcNow;
            // mithril#1163 Phase 1: emit the resolved scene-class fields
            // ONLY when the engine populated them (mask block ran). Absence
            // for pre-locate-reject / mask-disabled attempts means "Outdoor by
            // safe-degrade" — readers treat null as that. Emitting concrete
            // Outdoor numbers for an unresolved attempt would be the bundle
            // actively misreporting positive evidence it never had
            // (mithril#1168 review feedback).
            var profileJson = ctx.Profile is { } resolved
                ? new SceneCalibrationProfileJson(
                    MinArea: resolved.BlobOptions.MinArea,
                    MaxIconArea: resolved.BlobOptions.MaxIconArea,
                    MinSolidity: resolved.BlobOptions.MinSolidity,
                    MaxAspect: resolved.BlobOptions.MaxAspect,
                    MinPeak: resolved.BlobOptions.MinPeak)
                {
                    // mithril#1155 Phase 3 (review #1169-r2): MinPeakLuma surfaces
                    // in 01-attempt.json so a triager can tell whether the peak-
                    // luma filter was active for the attempt (null = Outdoor /
                    // pre-Phase-3, 0.7 = Indoor). Null is the JSON default per
                    // record-init semantics, so Outdoor bundles stay byte-identical
                    // by construction.
                    MinPeakLuma = resolved.BlobOptions.MinPeakLuma,
                    // mithril#1155 Phase 2.5: surface the resolved morph-open
                    // radius so a triager opening a future bundle knows whether
                    // open ran for this attempt without diffing engine commits.
                    // Both Outdoor and Indoor v1 emit 0; the field is always
                    // present (no nullable) so jq filters / downstream tooling
                    // can read it unconditionally.
                    MorphOpenRadiusPx = resolved.MorphOpenRadiusPx,
                    // mithril#1172 Phase 2.6: surface the resolved pre-
                    // deviation luma byte threshold. Outdoor emits 0,
                    // Indoor emits 200 in v1. Always-emitted by JSON-shape
                    // convention so a triager doesn't have to disambiguate
                    // "0 = absent" from "0 = explicit Outdoor".
                    MinLumaForDeviation = resolved.MinLumaForDeviation,
                }
                : null;
            var dto = new AttemptJson(
                SchemaVersion: 4,
                Area: ctx.Area,
                AttemptStartedUtc: ctx.StartedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                AttemptFinalizedUtc: finalized.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                Outcome: ctx.Outcome,
                RejectReason: ctx.Result?.RejectReason ?? ctx.ExceptionInfo,
                EngineVersion: AssemblyVersion,
                Files: files,
                LocatorBest: ToLocatorBestJson(ctx),
                Synthesis: ToSynthesisJson(ctx.Result?.Synthesis),
                SceneClass: ctx.SceneClass?.ToString(),
                SceneClassOpaqueFraction: ctx.SceneClassOpaqueFraction,
                Profile: profileJson);
            WriteJson(dir, "01-attempt.json", dto, CalibrationBundleJsonContext.Default.AttemptJson);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "01-attempt.json header write failed for {Outcome}", ctx.Outcome); }
    }

    // LocatorBest is emitted only when BOTH the raw fit rect AND the FM metrics are
    // present on the context. The production FeatureMatchingRefiner populates both
    // on accept; legacy/test contexts may omit Metrics, in which case LocatorBest
    // stays null in the bundle.
    // Non-static so the v2 PadPx field can read from the injected
    // MapCalibrationLocateOptions singleton (mithril#1061). Pre-PR it was static
    // because every input came from ctx; the option-reading change requires an
    // instance member.
    private LocatorBestJson? ToLocatorBestJson(CalibrationAttemptContext ctx)
    {
        if (ctx.LocatorRawFit is not { } rect || ctx.LocatorMetrics is not { } metrics)
            return null;
        // mithril#1061: schema v2 surfaces which algorithm produced this fit
        // (orb-lowe primary vs sobel-padded-pyramid fallback) plus the fallback-only
        // diagnostic fields (NCC peak + pad). PadPx reads MapCalibrationLocateOptions.
        // FallbackPadPx so a user who customises the pad sees that value in the
        // bundle — not the option-default literal. Test graphs that don't inject the
        // options resolve to the static default (100), preserving pre-injection
        // behaviour.
        var isFallback = metrics.Provenance == LocateProvenance.SobelPaddedPyramid;
        var padPx = isFallback ? (_options?.FallbackPadPx ?? 100) : (int?)null;
        // mithril#1070 schema v3: BlurAppliedSigma is the σ stamped at the
        // refiner's final matchTemplate call. Plumbed only on the fallback path
        // — the ORB primary doesn't blur, and surfacing a zero on v2 readers
        // would be a misleading "blur ran but with σ=0" semantic.
        return new LocatorBestJson(
            SchemaVersion: 3,
            OriginX: rect.OriginX,
            OriginY: rect.OriginY,
            Width: rect.Width,
            Height: rect.Height,
            TextureWidth: rect.TextureWidth,
            TextureHeight: rect.TextureHeight,
            InlierCount: metrics.InlierCount,
            CandidateCount: metrics.CandidateCount,
            InlierRatio: metrics.InlierRatio,
            Scale: metrics.Scale,
            RotationDegrees: metrics.RotationDegrees,
            Tx: metrics.Tx,
            Ty: metrics.Ty,
            ResidualPixels: metrics.ResidualPixels,
            GateAccepted: ctx.Outcome == OutcomeVocabulary.Accepted,
            GateRejectReason: ctx.Result?.RejectReason ?? ctx.ExceptionInfo,
            Algorithm: isFallback ? "sobel-padded-pyramid" : "orb-lowe",
            FallbackNcc: isFallback ? metrics.Confidence : null,
            PadPx: padPx,
            BlurAppliedSigma: isFallback ? metrics.BlurAppliedSigma : null);
    }

    // #1117: field-by-field translation from the engine-layer SynthesisDiagnostics
    // to the bundle wire-format SynthesisJson. Null in → null out so pre-#1117
    // solve results (or mode == Off) produce v3 bundles with synthesis: null.
    private static SynthesisJson? ToSynthesisJson(SynthesisDiagnostics? d)
    {
        if (d is null) return null;
        return new SynthesisJson(
            SchemaVersion: 1,
            Mode: d.Mode,
            Rotate180: d.Rotate180,
            J: d.J,
            JMin: d.JMin,
            RefsAboveHalf: d.RefsAboveHalf,
            RefsTotal: d.RefsTotal,
            RefsOffCrop: d.RefsOffCrop,
            NMin: d.NMin,
            Verdict: d.Verdict,
            GateVerdict: d.GateVerdict,
            Disagree: d.Disagree,
            DisagreeChange: d.DisagreeChange);
    }

    // mithril#1125: project the typed RimMaskPipeline enum to the snake_case
    // wire-format string the JSON shape expects (and pre-#1125 bundles already
    // shipped). Keeping the projection here means the in-memory record uses the
    // typed value and the producer can't silently typo a discriminator.
    private static string ToWire(RimMaskPipeline pipeline) => pipeline switch
    {
        RimMaskPipeline.BlobDetection => "blob_detection",
        RimMaskPipeline.SynthesisJ => "synthesis_j",
        _ => throw new ArgumentOutOfRangeException(nameof(pipeline)),
    };

    // mithril#1125: project BlobClass to its wire-format string. Symmetric with
    // ToWire(RimMaskPipeline). The wire strings happen to match the enum-member
    // names today, but stating them as literals decouples the contract: a
    // future BlobClass member rename keeps the wire stable until the switch arm
    // is updated, and the unreachable default arm throws on a rogue cast
    // instead of silently emitting Enum.ToString()'s numeric fallback.
    private static string ToWire(BlobClass blobClass) => blobClass switch
    {
        BlobClass.Icon => "Icon",
        BlobClass.Noise => "Noise",
        BlobClass.Fog => "Fog",
        BlobClass.Structure => "Structure",
        _ => throw new ArgumentOutOfRangeException(nameof(blobClass)),
    };

    private static string WritePng(string dir, string name, BitmapSource src)
    {
        var path = Path.Combine(dir, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return name;
    }

    private static string WriteJson<T>(string dir, string name, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var path = Path.Combine(dir, name);
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, value, typeInfo);
        return name;
    }
}
