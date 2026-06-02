using System;
using System.Collections.Generic;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Per-attempt mutable data carrier. Populated by AutoCalibrationEngine as the
/// pipeline progresses; consumed by ICalibrationAttemptBundleSink.Write at the
/// end of the attempt (success, gate-reject, exception, or cancellation).
/// </summary>
public sealed class CalibrationAttemptContext
{
    public CalibrationAttemptContext(string area, DateTimeOffset startedUtc)
    {
        Area = area;
        StartedUtc = startedUtc;
    }

    public string Area { get; }
    public DateTimeOffset StartedUtc { get; }

    // Filled by the engine as it goes. All nullable — sink writes what it has.
    public CapturedFrame? RawCapture { get; set; }
    public GrayImage? GrayCapture { get; set; }
    public GrayImage? BaseTextureResampled { get; set; }
    public MapRect? MapRect { get; set; }
    /// <summary>
    /// The locator's coarse best-rung rect regardless of whether it cleared the
    /// engine's accept threshold. Set on both accept and
    /// <c>rejected-map-not-located</c> outcomes so the bundle/log makes future
    /// close-miss vs catastrophic-mismatch self-triaging. (The score/factor metadata
    /// that used to ride on this rect is being re-surfaced through
    /// <c>LocatorMetrics</c> under the FM-based locate work — Task 15.)
    /// </summary>
    public MapRect? LocatorBestRect { get; set; }
    public GrayImage? AlignedCrop { get; set; }
    public GrayImage? AlignedTexture { get; set; }
    public IReadOnlyList<LandmarkReference>? References { get; set; }
    public IReadOnlyList<TypedDetection>? Detections { get; set; }
    public CalibrationSolveResult? Result { get; set; }

    // Outcome is set explicitly by the engine — either at each Fail() site, at
    // the end of the success path, or in the catch (exception → "error").
    public string Outcome { get; set; } = "unknown";
    public string? ExceptionInfo { get; set; }
}
