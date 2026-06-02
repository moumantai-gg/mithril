using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Stable strings + classifiers for the per-attempt bundle's outcome field
/// (used in subdir names and 01-attempt.json).
/// </summary>
public static class OutcomeVocabulary
{
    public const string Accepted = "accepted";
    public const string RejectedNoArea = "rejected-no-area";
    public const string RejectedPgNotForeground = "rejected-pg-not-foreground";
    public const string RejectedNoBbox = "rejected-no-bbox";
    public const string RejectedCaptureFailed = "rejected-capture-failed";
    public const string RejectedNoBaseTexture = "rejected-no-base-texture";
    public const string RejectedMapNotLocated = "rejected-map-not-located";
    public const string RejectedClampDegenerate = "rejected-clamp-degenerate";
    public const string RejectedSolve = "rejected-solve";
    public const string RejectedSolveNoDetections = "rejected-solve-no-detections";
    public const string RejectedSolveInsufficientInliers = "rejected-solve-insufficient-inliers";
    public const string RejectedSolveResidual = "rejected-solve-residual";
    public const string Error = "error";

    private static readonly FrozenSet<string> NoBundleOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        RejectedNoArea, RejectedPgNotForeground, RejectedNoBbox,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>True when the bundle should be written; false for the pre-capture rejects.</summary>
    public static bool ShouldWriteBundle(string outcome) => !NoBundleOutcomes.Contains(outcome);

    /// <summary>
    /// Map a free-form <see cref="CalibrationSolveResult.RejectReason"/> to a fixed
    /// subdir-name suffix. Unmappable reasons → <see cref="RejectedSolve"/>.
    /// </summary>
    public static string RejectSolveSubcategory(string? rejectReason)
    {
        if (string.IsNullOrWhiteSpace(rejectReason)) return RejectedSolve;
        var s = rejectReason!.AsSpan();
        if (s.Contains("no detections", StringComparison.OrdinalIgnoreCase)) return RejectedSolveNoDetections;
        if (s.Contains("insufficient inliers", StringComparison.OrdinalIgnoreCase)) return RejectedSolveInsufficientInliers;
        if (s.Contains("residual", StringComparison.OrdinalIgnoreCase)) return RejectedSolveResidual;
        return RejectedSolve;
    }
}
