namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Diagnostic + gate-feeding metrics from one
/// <see cref="FeatureMatchingRefiner"/> run. Populated whenever RANSAC
/// converged on a fit; null on the result type means "no fit found at all".
/// <list type="bullet">
/// <item><c>InlierCount</c> + <c>InlierRatio</c> are the gate floors
/// (spec §"Gate criteria").</item>
/// <item><c>RotationDegrees</c> is the small-rotation gate — PG's UI is
/// axis-aligned, so anything &gt; ~0.5° indicates a wrong fit, not a real
/// rotated map.</item>
/// <item><c>ResidualPixels</c> is the median per-inlier reprojection error
/// in screenshot pixels — diagnostic only, not gated (the inlier mask is
/// already the answer the gate cares about).</item>
/// </list>
/// </summary>
public sealed record LocateMetrics(
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    bool Mirror,
    double Tx,
    double Ty,
    double ResidualPixels);
