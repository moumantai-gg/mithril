using System;
using Mithril.MapCalibration.Capture.Diagnostics;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Maps an <see cref="AutoCalibrationOutcome"/> / raw reject reason to the
/// user-facing status-chip string (spec §11). Pure + CI-tested; the push to the
/// overlay chip (<c>IOverlayWindow.SetStatusMessage</c>) is shell wiring (Task 28).
/// The engine's reject reasons are diagnostic ("residual 25.00 px exceeds
/// threshold…"); this turns them into an actionable instruction.
///
/// <para><b>Routing model (#1005).</b> <see cref="ForOutcome"/> routes
/// structurally on <see cref="AutoCalibrationOutcome.OutcomeCategory"/> first:
/// when set, the outcome category maps deterministically to its user message.
/// When <see langword="null"/> (legacy callers that pre-date #1005),
/// <see cref="ForReject"/> falls back to substring-matching the
/// <see cref="AutoCalibrationOutcome.RejectReason"/> &#8212; preserving the
/// pre-#1005 behaviour for any path that hasn't been updated yet.</para>
/// </summary>
public static class CalibrationStatusFormatter
{
    /// <summary>
    /// The status string for an outcome, or <see langword="null"/> when it
    /// succeeded (a persisted calibration clears the chip — happy state).
    /// </summary>
    public static string? ForOutcome(AutoCalibrationOutcome outcome)
    {
        if (outcome.Persisted) return null;
        return ForCategory(outcome.OutcomeCategory)
               ?? ForReject(outcome.RejectReason ?? "couldn't auto-calibrate the map");
    }

    /// <summary>
    /// Structural route — known <see cref="OutcomeVocabulary"/> categories to
    /// their user-facing messages. Returns <see langword="null"/> for unknown
    /// or null categories so the caller falls back to <see cref="ForReject"/>.
    /// </summary>
    private static string? ForCategory(string? category) => category switch
    {
        OutcomeVocabulary.RejectedNotMonotonic =>
            "Calibration unchanged: the new fit was worse than the saved one. "
            + "To force-replace, clear the saved calibration for this area.",
        // #1021: per-scene calibration keying — autocal fired before any
        // Downloading Map line was observed in this session, so the per-scene
        // Map_<X> asset name is unknown. Tell the user how to recover.
        OutcomeVocabulary.MapAssetNotYetKnown =>
            "Map asset not yet known — change zones once or restart while in this scene.",
        // Other categories deliberately not routed here yet — they fall through
        // to the substring path so today's wording is preserved by default.
        _ => null,
    };

    /// <summary>Map a raw reject reason to an actionable user instruction.</summary>
    public static string ForReject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Couldn't auto-calibrate the map.";

        // No bbox framed yet → tell them to draw it.
        if (Contains(reason, "bbox"))
            return "No map region set — use the draw-map-bbox hotkey to frame the map.";

        // PG not focused / not in-world → name the game.
        if (Contains(reason, "foreground") || Contains(reason, "in-world") || Contains(reason, "not detected"))
            return "Open Project Gorgon (focused, in an area) to calibrate the map.";

        // Assets still extracting.
        if (Contains(reason, "map assets") || Contains(reason, "preparing") || Contains(reason, "base texture"))
            return "Preparing map assets… try the capture again in a moment.";

        // Low-confidence solve (residual / inliers) → the actionable fix is to
        // zoom the in-game map all the way out and redraw the bbox.
        if (Contains(reason, "residual") || Contains(reason, "inlier")
            || Contains(reason, "fit") || Contains(reason, "locate the map") || Contains(reason, "capture"))
            return "Couldn't auto-calibrate — zoom the in-game map all the way out, then redraw the map bbox and retry.";

        return "Couldn't auto-calibrate the map.";
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ── Drift-check + recalibrate chip messages (spec §6.5) ──────────────────

    /// <summary>Chip shown when CheckDriftAsync returns Ok — predictions matched detections within tolerance.</summary>
    public static string DriftCheckOk() =>
        "Calibration check OK — no drift detected.";

    /// <summary>Chip shown when CheckDriftAsync returns Inconclusive (e.g., too few visible landmarks).</summary>
    public static string DriftCheckInconclusive(string reason) =>
        $"Drift check inconclusive — {reason}.";

    /// <summary>Chip shown when CheckDriftAsync returns Drift, arming the hotkey for a confirmation re-press.</summary>
    public static string DriftDetected(double maxResidualPx, int armingSeconds) =>
        $"Drift detected (~{maxResidualPx:0.0}px). Press calibrate hotkey again within {armingSeconds}s to recalibrate.";

    /// <summary>Chip shown when CheckDriftAsync returns CaptureFailed/MapNotLocated — actionable reject reason.</summary>
    public static string DriftCheckCaptureFailed(string reason) =>
        $"Drift check: {reason}.";

    /// <summary>Chip shown when an armed re-press successfully ran the full solve and persisted.</summary>
    public static string RecalibratedSuccessfully() =>
        "Recalibrated successfully.";
}
