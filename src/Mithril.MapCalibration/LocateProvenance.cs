namespace Mithril.MapCalibration;

/// <summary>
/// Which locate-stage algorithm produced a <see cref="LocateMetrics"/>
/// record. Bundle JSON + status copy + telemetry tags route on this.
/// </summary>
public enum LocateProvenance
{
    /// <summary>ORB + Lowe + RANSAC partial-affine (#1009 primary).</summary>
    OrbRansac = 0,

    /// <summary>Sobel magnitude + 100 px padded matchTemplate + 3-level pyramid (#1061 fallback).</summary>
    SobelPaddedPyramid = 1,
}
