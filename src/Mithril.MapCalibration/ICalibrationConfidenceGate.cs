namespace Mithril.MapCalibration;

/// <summary>
/// Accept/reject gate for a candidate auto-solved calibration. An auto solve is
/// only persisted when it is at least as trustworthy as a manual one: low RMS
/// residual AND enough geometrically-consistent inliers.
/// </summary>
public interface ICalibrationConfidenceGate
{
    /// <summary>
    /// Returns true when <paramref name="solve"/> clears the residual + inlier
    /// gates. On rejection, <paramref name="rejectReason"/> is a short
    /// human-readable explanation (null on accept).
    /// </summary>
    bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason);
}
