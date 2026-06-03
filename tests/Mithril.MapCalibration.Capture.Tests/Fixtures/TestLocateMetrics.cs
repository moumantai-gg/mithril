using Mithril.MapCalibration.Capture;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Test-helper factory for building a representative <see cref="LocateMetrics"/>
/// block from a healthy locate. Tests that don't care about specific metric
/// values use this so the harness stays succinct and the engine's gate logic
/// sees a populated <c>Metrics</c>.
/// </summary>
internal static class TestLocateMetrics
{
    /// <summary>
    /// Build a metrics block at the given <paramref name="scale"/> with an
    /// ordinary inlier count. Other fields are filled with healthy defaults
    /// (rotation 0, no mirror, residual ~1 px).
    ///
    /// <para><b>Placeholder fields.</b> <see cref="LocateMetrics.InlierRatio"/>
    /// is hard-coded to 0.5 (not derived from <paramref name="inlierCount"/> /
    /// <c>CandidateCount</c>) because no current consumer reads it for gating.
    /// If a future test asserts on <see cref="LocateMetrics.InlierRatio"/>,
    /// <see cref="LocateMetrics.ResidualPixels"/>, <see cref="LocateMetrics.Tx"/>,
    /// or any other field beyond <see cref="LocateMetrics.Scale"/> /
    /// <see cref="LocateMetrics.InlierCount"/>, construct the
    /// <see cref="LocateMetrics"/> explicitly rather than relying on these
    /// defaults &#8212; the helper is only safe when "default placeholder" is
    /// what the test needs.</para>
    /// </summary>
    public static LocateMetrics ForScale(double scale, int inlierCount = 50) =>
        new(InlierCount: inlierCount, CandidateCount: inlierCount * 2,
            InlierRatio: 0.5, Scale: scale, RotationDegrees: 0.0,
            Mirror: false, Tx: 0.0, Ty: 0.0, ResidualPixels: 1.0);
}
