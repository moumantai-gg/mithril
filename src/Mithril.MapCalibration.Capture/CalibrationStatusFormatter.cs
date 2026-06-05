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
/// <para>Routing model: callers populate <see cref="AutoCalibrationOutcome.OutcomeCategory"/>
/// (one of <see cref="OutcomeVocabulary"/>'s constants); <see cref="ForOutcome"/>
/// switches on the constant for crisp messages. When the field is null, the
/// formatter falls back to substring-matching the <see cref="AutoCalibrationOutcome.RejectReason"/>.</para>
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
        // #1021: per-scene calibration keying — autocal fired before any
        // Downloading Map line was observed in this session, so the per-scene
        // Map_<X> asset name is unknown. Tell the user how to recover.
        OutcomeVocabulary.MapAssetNotYetKnown =>
            "Map asset not yet known — change zones once or restart while in this scene.",
        // mithril#1061: the Sobel-padded-pyramid fallback found a fit but the
        // NCC peak was below the floor — input pathology rather than framing
        // problem. Different actionable advice than the ORB primary's
        // RejectedMapNotLocated ("redraw the bbox" — which won't help here).
        OutcomeVocabulary.RejectedMapLowConfidence =>
            "Couldn't locate the map confidently — try a different zoom or explore more of the area first.",
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

    /// <summary>
    /// Chip shown when CheckDriftAsync returns <see cref="DriftCheckOutcome.NoTextureFrameRecord"/>:
    /// the scene IS calibrated, but only with an overlay-frame Legolas wizard
    /// fit. Drift-check is bound to texture frame so it can't measure against
    /// the stored record. Tell the user how to land a texture-frame record
    /// (mithril#1076 spec §2.4).
    /// </summary>
    public static string DriftCheckNoTextureFrameRecord() =>
        "No AutoCalibration record for this scene — press AutoCalibrate to land one.";

    /// <summary>Chip shown when an armed re-press successfully ran the full solve and persisted.</summary>
    public static string RecalibratedSuccessfully() =>
        "Recalibrated successfully.";
}
