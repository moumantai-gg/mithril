namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Outcome of one <see cref="IAutoCalibrationRunner.CheckDriftAsync"/> attempt
/// (mithril#1046 §6.1). The manual hotkey coordinator branches on the concrete
/// case to decide whether to arm, surface a chip, or fall through to a cold
/// solve.
/// </summary>
public abstract record DriftCheckOutcome
{
    /// <summary>No stored calibration exists for the current scene — caller
    /// should fall through to the cold solve path.</summary>
    public sealed record NoStoredCalibration : DriftCheckOutcome;

    /// <summary>
    /// A calibration is stored for the scene but it lives in OVERLAY frame (a
    /// Legolas-wizard fit, spec §2.4), not TEXTURE frame. The drift check
    /// compares predictions against detections in TEXTURE-then-CROP space; a
    /// non-texture-frame record can't legitimately enter that arithmetic.
    /// Refuse honestly instead of silently producing 0/N matches (mithril#1076).
    /// </summary>
    public sealed record NoTextureFrameRecord : DriftCheckOutcome;

    /// <summary>Map capture failed (black frame, wrong size, PG not foreground).
    /// Surface <paramref name="Reason"/> via the chip; do not arm.</summary>
    public sealed record CaptureFailed(string Reason) : DriftCheckOutcome;

    /// <summary>The locator couldn't find the map sub-rect in the captured
    /// frame. Surface <paramref name="Reason"/> via the chip; do not arm.</summary>
    public sealed record MapNotLocated(string Reason) : DriftCheckOutcome;

    /// <summary>The typed icon detector found nothing in the captured frame —
    /// can't compare predictions to detections. Do not arm.</summary>
    public sealed record NoIconDetections : DriftCheckOutcome;

    /// <summary>Fewer than the minimum matched references survived the
    /// 20-px gate — drift is not measurable. Do not arm.</summary>
    public sealed record Inconclusive(string Reason, int MatchedReferences) : DriftCheckOutcome;

    /// <summary>Predictions land on detections within the drift tolerance —
    /// the stored calibration is fine; no recalibration needed.</summary>
    public sealed record Ok(double MaxResidualPx, int MatchedReferences) : DriftCheckOutcome;

    /// <summary>At least one matched reference exceeds the drift tolerance.
    /// Coordinator should arm the hotkey for a confirmation re-press.</summary>
    public sealed record Drift(double MaxResidualPx, int MatchedReferences, double ThresholdPx) : DriftCheckOutcome;
}
