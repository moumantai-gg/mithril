using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration;

/// <summary>
/// Supplies the aligned base map texture (a single-channel <see cref="GrayImage"/>)
/// for a given area, which the deviation/NCC detector
/// (<see cref="DeviationBlobCalibrationDetector"/> / <see cref="DetectionRequest.BaseTexture"/>)
/// diffs the screenshot against. Decoder-free at this seam: an implementation may
/// read a pre-decoded cache the out-of-process asset-extractor sidecar wrote, but
/// no image decoder ever enters the app graph.
///
/// <para><b>#931 defines this seam; #914 PR-2 consumes it</b> (the capture/trigger
/// orchestrator resolves the base texture for the area being calibrated). The
/// default implementation is <see cref="Internal.CachedBaseTextureProvider"/>
/// over the sidecar cache.</para>
///
/// <para><b>Fail-soft:</b> returns <c>null</c> on any miss (no cache, missing
/// file, hash mismatch, truncation). A null base texture → the detector produces
/// no detections → the confidence gate rejects → safe-degrade, never a silent
/// wrong calibration.</para>
/// </summary>
public interface IBaseTextureProvider
{
    /// <summary>
    /// The base texture for <paramref name="areaKey"/> (e.g. <c>"AreaSerbule"</c>),
    /// or <c>null</c> if it can't be loaded + verified.
    /// </summary>
    GrayImage? TryGetBaseTexture(string areaKey);
}
