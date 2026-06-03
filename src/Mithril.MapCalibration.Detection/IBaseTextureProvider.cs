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
    /// The base texture for <paramref name="mapAssetKey"/> (e.g. <c>"Map_AreaSerbule"</c>),
    /// or <c>null</c> if it can't be loaded + verified.
    ///
    /// <para>The key is the <b>literal Unity Texture2D name</b> (with the
    /// <c>Map_</c> prefix), as observed in the Player.log
    /// <c>Downloading Map ... runtime key ...[Map_&lt;X&gt;]</c> line. See
    /// <see href="https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures">
    /// Player-Log-Signals § Map asset loads</see>.</para>
    /// </summary>
    GrayImage? TryGetBaseTexture(string mapAssetKey);
}
