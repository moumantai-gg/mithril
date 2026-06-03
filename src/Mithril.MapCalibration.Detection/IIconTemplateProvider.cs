using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration;

/// <summary>
/// Supplies the decoded <see cref="IconTemplateSet"/> the detector matches against,
/// resolved <i>per attempt</i> rather than once eagerly. This is the icon analogue
/// of <see cref="IBaseTextureProvider"/>: an implementation re-reads the on-disk
/// cache the out-of-process asset-extractor sidecar (issue #931) populates, so a
/// same-session populate (e.g. the engine's <c>--icons</c> demand-trigger or the
/// startup warm-up) takes effect on the very next attempt — no app restart needed.
/// Decoder-free at this seam: an implementation reads a pre-decoded manifest+blob
/// cache BCL-only; no image decoder ever enters the app graph.
///
/// <para><b>#949 motivation.</b> The previous design resolved
/// <see cref="IconTemplateSet"/> as an eager singleton, captured before the icon
/// cache was populated — so the first session always saw
/// <see cref="IconTemplateSet.Empty"/> (zero typed detections → the confidence gate
/// rejected → the user had to restart). This provider closes that gap by deferring
/// the load to attempt time.</para>
///
/// <para><b>Fail-soft:</b> returns <see cref="IconTemplateSet.Empty"/> on any miss
/// (no cache, missing file, hash mismatch, truncation, exception). Empty templates
/// → no typed detections → the confidence gate rejects → safe-degrade, never a
/// silent wrong calibration and never a throw into the engine.</para>
/// </summary>
public interface IIconTemplateProvider
{
    /// <summary>
    /// The current icon-template set, re-read from the cache, or
    /// <see cref="IconTemplateSet.Empty"/> if it can't be loaded + verified.
    /// </summary>
    IconTemplateSet GetTemplates();
}
