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
    double? Confidence = null,
    // mithril#1070: σ (px) of the Gaussian blur applied to the Sobel template
    // at the recovered scale at the final matchTemplate site. Null on ORB
    // primary (blur is a sparse-locate-fallback concept) and on
    // RendererBlurEnabled=false. Zero when blur was disabled or the σ-curve
    // clamped to 0 at the recovered scale. Diagnostic surface only — no
    // engine-side gate reads this.
    double? BlurAppliedSigma = null)
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
