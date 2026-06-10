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

    /// <summary>
    /// Optional diagnostic hooks for the deviation-blob detector pipeline
    /// (mithril#1123). Sibling to <see cref="BlobScoreSink"/>; null in tests
    /// and production paths that don't need the per-stage observability.
    /// Producer cost is zero when null — the orchestrator skips both buffer
    /// retention and LogTrace formatting for the null fields.
    /// </summary>
    public DetectionDiagnosticHooks? Diagnostics { get; init; }
}

/// <summary>
/// Aggregate of opt-in observability sinks for the deviation-blob detector
/// pipeline (mithril#1123). Threaded via <see cref="DetectionRequest.Diagnostics"/>.
/// Each callback is independently nullable; the orchestrator skips both retention
/// and LogTrace emission for the null sinks (producer-cost = zero per CLAUDE.md's
/// instrumentation contract). Mirrors the #1121 BlobScoreSink pattern, scaled to
/// four upstream stages of the detector pipeline:
///
/// <list type="bullet">
///   <item><see cref="OnDeviation"/> — fires once after the threshold step;
///         emits the fg-initial bool[] + dev stats.</item>
///   <item><see cref="OnRimMask"/> — fires from BOTH callers of
///         <c>DeviationFloodRimMask.Build</c> (blob-detection AND the
///         synthesis-J L_t builder); records carry a <c>Pipeline</c>
///         discriminator.</item>
///   <item><see cref="OnMorph"/> — fires once after the morph-close stage.</item>
///   <item><see cref="OnBlobClassified"/> — fires for ALL comps (not just Icon),
///         so triage sees Noise/Fog/Structure verdicts too.</item>
/// </list>
/// </summary>
public sealed record DetectionDiagnosticHooks(
    Action<DeviationSnapshot>? OnDeviation,
    Action<RimMaskSnapshot>? OnRimMask,
    Action<MorphSnapshot>? OnMorph,
    Action<BlobClassification>? OnBlobClassified);

/// <summary>
/// One observation per orientation pass (mithril#1123). Emitted from inside
/// <c>DeviationBlobDetector.DetectIconBlobs</c> AFTER the threshold step —
/// that's where the fg-initial bool[] is produced and where the dev float[]
/// is still in scope for stats computation. Stats are computed at emission
/// time + serialised to JSON; the dev float[] itself is NOT retained on this
/// record. <see cref="ForegroundBuffer"/> IS retained — it backs
/// <c>07b-foreground.png</c> in the diagnostic bundle.
/// </summary>
public sealed record DeviationSnapshot(
    bool Rotate180,
    int Width,
    int Height,
    int Win,
    double Threshold,
    double MeanNcc,
    double Min,
    double Max,
    double Mean,
    double P50,
    double P95,
    double P99,
    int AboveThresholdCount,
    bool[] ForegroundBuffer);

/// <summary>
/// One observation per (orientation, pipeline) pair (mithril#1123). Pipeline
/// discriminates the two callers of <c>DeviationFloodRimMask.Build</c>:
/// <c>"blob_detection"</c> (from <c>DeviationBlobDetector.DetectIconBlobs</c>)
/// and <c>"synthesis_j"</c> (from
/// <c>MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation</c>).
/// <see cref="FgInputCount"/> / <see cref="FgSurvivorCount"/> are populated on
/// the blob_detection path; synthesis_j supplies <c>-1</c> sentinels (that
/// pipeline applies the rim mask to a likelihood field, not an fg mask).
/// </summary>
public sealed record RimMaskSnapshot(
    string Pipeline,
    bool Rotate180,
    int Width,
    int Height,
    double Threshold,
    int RimPixelCount,
    int FgInputCount,
    int FgSurvivorCount,
    bool[] RimMaskBuffer);

/// <summary>
/// One observation per orientation pass (mithril#1123). <see cref="CloseRadius"/>
/// is the configured morph-close radius (1 in production today; 0 disables the
/// stage and this snapshot is not emitted in that case).
/// </summary>
public sealed record MorphSnapshot(
    bool Rotate180,
    int Width,
    int Height,
    int CloseRadius,
    int FgInputCount,
    int FgOutputCount,
    bool[] FgAfterMorphBuffer);

/// <summary>
/// One observation per connected component — ALL components, not just
/// Icon-class (mithril#1123). <see cref="BlobOrdinal"/> is the position in the
/// 8-connected emission order from <c>ConnectedComponents.Label</c> — the same
/// ordinal carried by <see cref="BlobTemplateScore.BlobOrdinal"/>. Cross-ref
/// between <c>10c-blob-pipeline.json</c>'s <c>blobs[]</c> and
/// <c>10b-blob-template-scores.json</c>'s <c>scores[]</c> is by ordinal.
///
/// <para><see cref="Pixels"/> is render-only payload — retained on this
/// in-memory record so the bundle sink can colour <c>07e-blob-classification.png</c>
/// per blob, but NOT serialised to the 10c JSON shape.</para>
/// </summary>
public sealed record BlobClassification(
    bool Rotate180,
    int BlobOrdinal,
    int MinX,
    int MinY,
    int W,
    int H,
    int Area,
    double Cx,
    double Cy,
    double MeanDev,
    double PeakDev,
    double Solidity,
    double Aspect,
    string BlobClass,
    IReadOnlyList<int> Pixels);

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
///
/// <para><see cref="BlobOrdinal"/> is the blob's position in the 8-connected
/// emission order from <c>ConnectedComponents.Label</c> — the same ordinal
/// space carried by <c>BlobClassification.BlobOrdinal</c> (mithril#1123).
/// Cross-ref between the <c>10b-blob-template-scores.json</c> bundle dump
/// (per-template scores for Icon-class blobs only) and
/// <c>10c-blob-pipeline.json</c> (classification for all comps) is by
/// matching ordinal — same value identifies the same physical blob in both
/// files. Renamed from <c>BlobIndex</c> in schema v2.</para>
/// </remarks>
public sealed record BlobTemplateScore(
    int BlobOrdinal,
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
