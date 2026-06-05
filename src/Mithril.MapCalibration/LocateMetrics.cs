namespace Mithril.MapCalibration;

/// <summary>
/// Diagnostic + gate-feeding metrics from one <see cref="IMapRegionRefiner"/>
/// run. Populated whenever the refiner produced a fit (gate-pass-or-not);
/// <c>null</c> on <see cref="MapRegionRefineResult"/> means "no fit found at all".
///
/// <para><b>Provenance.</b>
/// <see cref="LocateProvenance.OrbRansac"/> populates Inlier* / RotationDegrees /
/// ResidualPixels; <see cref="Confidence"/> is null because the gate reads
/// InlierCount/InlierRatio.
/// <see cref="LocateProvenance.SobelPaddedPyramid"/> populates Scale + Tx + Ty +
/// <see cref="Confidence"/>; Inlier* / RotationDegrees / ResidualPixels are zero
/// — consumers route on <see cref="Provenance"/>.</para>
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
    double ResidualPixels,
    LocateProvenance Provenance = LocateProvenance.OrbRansac,
    double? Confidence = null)
{
    /// <summary>
    /// The located map rect's origin within the captured frame, as a typed
    /// <see cref="CapturedFramePixel"/>. Synonymous with
    /// (<see cref="Tx"/>, <see cref="Ty"/>) but compile-time-tagged so a caller
    /// can't accidentally feed it into texture- or crop-frame arithmetic
    /// (mithril#1076).
    /// </summary>
    public CapturedFramePixel LocatedRectOrigin => new(Tx, Ty);
}
