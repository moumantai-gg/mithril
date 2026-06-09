using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration;

/// <summary>
/// Turns a captured map frame (already cropped to its <see cref="MapRect"/>) plus
/// the aligned base texture into typed icon detections in screenshot-pixel space,
/// grouped by landmark type — the input the <see cref="TypeAwareRansacSolver"/>
/// consumes. Two implementations ship: the deviation-blob detector (the proven
/// sparse-area front-end) and a whole-image template-NCC fallback.
/// </summary>
public interface ICalibrationDetector
{
    /// <summary>
    /// Captured map (already cropped to MapRect) + base texture (aligned) →
    /// typed detections in screenshot-pixel space, grouped by landmark type.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<TypedDetection>> Detect(DetectionRequest request);
}

/// <summary>Everything a detector needs for one detect pass. BCL-only inputs (no decoder).</summary>
public sealed record DetectionRequest(
    GrayImage Screenshot,
    GrayImage BaseTexture,
    MapRect MapRect,
    IconTemplateSet Templates,
    RimMaskMode RimMask,
    double LowNcc,
    double TypeFloor,           // per-blob template-NCC acceptance gate (§8: ~0.65, not deviation-rim alone)
    BlobOptions BlobOptions)
{
    /// <summary>
    /// On-screen icon render size (px) to downscale native-resolution PG sprites
    /// (~256&#160;px) to before NCC. PG renders every map icon at one consistent
    /// size; the gate study pinned this at <b>16&#160;px</b> (the empirical
    /// sweet-spot — see <c>tools/MapCalibrationFromScreenshot/README.md</c>).
    /// <c>null</c> falls back to the <see cref="IconRenderScaler"/> aggregate-NCC
    /// sweep, which is less reliable on real assets (it can collapse to the
    /// smallest, blurriest size that spuriously correlates with everything;
    /// mithril#916). Ignored when templates are already small (synthetic fixtures).
    /// </summary>
    public int? RenderSizePx { get; init; } = 16;

    /// <summary>
    /// Optional diagnostic sink for per-blob × per-template NCC scores (mithril#1121).
    /// When non-null, the deviation-blob detector emits one
    /// <see cref="BlobTemplateScore"/> for every (blob, template) pair it considers
    /// — including the skip path (template too large for the padded crop) — using
    /// the absolute best NCC peak in the crop irrespective of <see cref="TypeFloor"/>.
    /// Used by the calibration attempt-bundle sink to write <c>10b-blob-template-scores.json</c>
    /// for offline triage of NPC pip recall failures (#1116). Null in tests +
    /// production paths that don't need the diagnostic; producer cost is zero
    /// when null.
    /// </summary>
    public Action<BlobTemplateScore>? BlobScoreSink { get; init; }
}

/// <summary>
/// Per-blob × per-template NCC observation (mithril#1121). Produced by the
/// deviation-blob detector when <see cref="DetectionRequest.BlobScoreSink"/> is
/// wired. Records the absolute best NCC peak the template achieves anywhere in
/// the blob's padded crop, regardless of whether it cleared the type floor — so
/// downstream triage can distinguish "scored 0.78 (one fix)" from "scored 0.30
/// (different fix)" instead of seeing both as "below floor → silent drop."
/// </summary>
/// <remarks>
/// <para><see cref="Score"/> is <see cref="double.NaN"/> when the template was
/// skipped (its dimensions exceeded the padded crop). <see cref="AboveFloor"/>
/// is the gate verdict — <c>true</c> iff the template participated in the
/// "best-icon-wins" competition for this blob. <see cref="Rotate180"/>
/// disambiguates the two passes the engine runs (0° and 180° base texture).</para>
/// </remarks>
public sealed record BlobTemplateScore(
    int BlobIndex,
    int BlobMinX,
    int BlobMinY,
    int BlobWidth,
    int BlobHeight,
    int BlobArea,
    string TemplateName,
    string TemplateLandmarkType,
    int TemplateWidth,
    int TemplateHeight,
    double Score,
    double TypeFloor,
    bool AboveFloor,
    bool Skipped,
    bool Rotate180);
