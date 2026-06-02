using System.IO;
using System.Text.Json;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

/// <summary>
/// Auto-fills --area / --screenshot / --map-rect from a loaded bundle when the
/// caller has not supplied them explicitly.  The explicit flags always win.
/// </summary>
internal static class BundleArgsResolver
{
    /// <summary>
    /// Returns the resolved (area, screenshotPath, mapRect) triple.
    /// </summary>
    /// <param name="bundle">The already-loaded bundle.</param>
    /// <param name="bundleDir">The bundle directory (used to resolve relative paths).</param>
    /// <param name="mapRectJsonPath">
    ///   Path to the bundle's 04-maprect.json, or <see langword="null"/> if absent.
    /// </param>
    /// <param name="alignedDeviationPath">
    ///   Path to the bundle's 07-deviation.png, or <see langword="null"/> if absent.
    ///   When present, the deviation PNG's actual dimensions are used as the mapRect
    ///   W×H (the engine clamps mapRect.height at runtime but does not rewrite the JSON —
    ///   Bundle B 031130-122 records height=999 but the engine ran at height=986).
    /// </param>
    /// <param name="explicitArea">Caller-supplied --area value (empty string = not set).</param>
    /// <param name="explicitScreenshotPath">Caller-supplied --screenshot path, or <see langword="null"/>.</param>
    /// <param name="explicitMapRect">Caller-supplied --map-rect, or <see langword="null"/>.</param>
    public static (
        string Area,
        string? ScreenshotPath,
        (int X, int Y, int W, int H)? MapRect)
    Resolve(
        LoadedBundle bundle,
        string bundleDir,
        string? mapRectJsonPath,
        string? alignedDeviationPath,
        string explicitArea,
        string? explicitScreenshotPath,
        (int X, int Y, int W, int H)? explicitMapRect)
    {
        // Start with explicit values; fill gaps from the bundle.
        string area = string.IsNullOrEmpty(explicitArea) ? bundle.Attempt.Area : explicitArea;

        string? screenshotPath = explicitScreenshotPath;
        if (screenshotPath is null)
        {
            screenshotPath = bundle.Attempt.Files.GrayScreenshot is { } gs
                ? Path.Combine(bundleDir, gs)
                : bundle.Attempt.Files.RawScreenshot is { } rs
                    ? Path.Combine(bundleDir, rs)
                    : null;
        }

        (int X, int Y, int W, int H)? mapRect = explicitMapRect;
        if (mapRect is null && mapRectJsonPath is not null)
        {
            var mr = LoadMapRectJson(mapRectJsonPath);
            if (alignedDeviationPath is not null && File.Exists(alignedDeviationPath))
            {
                // The deviation PNG's actual W×H is the post-clamp truth (see param doc).
                var (devW, devH) = PngHeader.ReadDimensions(alignedDeviationPath);
                mapRect = (mr.OriginX, mr.OriginY, devW, devH);
            }
            else
            {
                mapRect = (mr.OriginX, mr.OriginY, mr.Width, mr.Height);
            }
        }

        return (area, screenshotPath, mapRect);
    }

    private static MapRectJson LoadMapRectJson(string path)
    {
        return JsonSerializer.Deserialize(
            File.ReadAllText(path),
            BundleJsonContext.Default.MapRectJson)!;
    }
}
