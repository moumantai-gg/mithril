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
                RecoveredCalibration: TryWriteRecoveredCalibrationJson(subdir, context));

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

    private void WriteAttemptJson(string dir, CalibrationAttemptContext ctx, AttemptFilesJson files)
    {
        try
        {
            var finalized = DateTimeOffset.UtcNow;
            var dto = new AttemptJson(
                SchemaVersion: 2,
                Area: ctx.Area,
                AttemptStartedUtc: ctx.StartedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                AttemptFinalizedUtc: finalized.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                Outcome: ctx.Outcome,
                RejectReason: ctx.Result?.RejectReason ?? ctx.ExceptionInfo,
                EngineVersion: AssemblyVersion,
                Files: files,
                LocatorBest: ToLocatorBestJson(ctx));
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
        return new LocatorBestJson(
            SchemaVersion: 2,
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
            PadPx: padPx);
    }

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
