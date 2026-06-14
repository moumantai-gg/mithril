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

    /// <summary>
    /// Optional binary mask (mithril#1116) for the deviation-mask subtract step:
    /// pixels with <c>true</c> are excluded from the working <c>fg</c> buffer
    /// AFTER the existing rim subtract and BEFORE morph-close, inside
    /// <see cref="DeviationBlobDetector.DetectIconBlobs"/>. Combines the
    /// texture-alpha-derived floor-boundary band and the screenshot-derived
    /// fog-of-war mask (built upstream by <c>DeviationMaskCombiner</c>).
    /// Length MUST equal <c>Width*Height</c> of the deviation buffer; a
    /// mismatch is a silent no-op + <c>LogWarning</c>, never a crash.
    /// <c>null</c> = no mask applied (byte-identical to pre-#1116 behaviour).
    /// </summary>
    public bool[]? DeviationMask { get; init; }

    /// <summary>
    /// Optional raw BGRA buffer (mithril#1155 Phase 3) backing the post-
    /// classification peak-luma pre-filter inside
    /// <see cref="DeviationBlobDetector.DetectIconBlobs"/>. Layout matches
    /// <c>CapturedFrame.Bgra</c> — 4 bytes/pixel, B then G then R then A,
    /// row-major; length MUST equal <c>Screenshot.Width*Screenshot.Height*4</c>.
    /// A mismatch is a silent no-op + <c>LogWarning</c> inside the filter, never
    /// a crash. The filter only fires when this buffer is non-null AND
    /// <see cref="BlobOptions.MinPeakLuma"/> is non-null; either gate alone
    /// short-circuits to byte-identical pre-#1155 behaviour.
    /// </summary>
    public byte[]? RawBgra { get; init; }
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
    Action<BlobClassification>? OnBlobClassified)
{
    /// <summary>
    /// Fires after the mithril#1116 deviation-mask subtract step
    /// (boundary + fog), before morph-close. Null = no observer attached;
    /// producer cost is zero. Added as an init-only property (not a
    /// positional parameter) so existing four-arg constructor call sites
    /// remain source-compatible.
    /// </summary>
    public Action<DeviationMaskSnapshot>? OnDeviationMask { get; init; }
}

/// <summary>
/// One observation per orientation pass (mithril#1123). Emitted from inside
/// <c>DeviationBlobDetector.DetectIconBlobs</c> AFTER the threshold step —
/// that's where the fg-initial bool[] is produced and where the dev float[]
/// is still in scope for stats computation. Stats are computed at emission
/// time + serialised to JSON; the dev float[] itself is NOT retained on this
/// record. <see cref="ForegroundBuffer"/> IS retained — it backs
/// <c>07b-foreground.png</c> in the diagnostic bundle.
///
/// <para>mithril#1126: <see cref="ForegroundBuffer"/> is a
/// <see cref="ReadOnlyMemory{T}"/> rather than a bare <c>bool[]</c> so the
/// type itself surfaces the read-only contract — consumers can't mutate the
/// snapshot's buffer and corrupt other consumers reading the same record.
/// The orchestrator still clones into the snapshot (so subsequent stage
/// mutations to its working <c>fg</c> buffer don't bleed in); wrapping with
/// <see cref="ReadOnlyMemory{T}"/> makes that invariant load-bearing on the
/// type.</para>
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
    ReadOnlyMemory<bool> ForegroundBuffer)
{
    /// <summary>
    /// mithril#1126: Debug-only length invariant — fires at construction (named,
    /// positional, or <c>with</c>) so a buffer/dim mismatch surfaces at the
    /// producer site instead of a downstream array-out-of-bounds. No RELEASE cost.
    /// </summary>
    public ReadOnlyMemory<bool> ForegroundBuffer { get; init; } =
        AssertBufferLengthMatches(ForegroundBuffer, Width, Height, nameof(ForegroundBuffer));

    private static ReadOnlyMemory<bool> AssertBufferLengthMatches(
        ReadOnlyMemory<bool> buffer, int width, int height, string name)
    {
        System.Diagnostics.Debug.Assert(
            buffer.Length == width * height,
            $"DeviationSnapshot.{name}.Length ({buffer.Length}) must equal Width*Height ({width}*{height}={width * height}).");
        return buffer;
    }
}

/// <summary>
/// Identifies which caller of <c>DeviationFloodRimMask.Build</c> emitted a
/// <see cref="RimMaskSnapshot"/>. Closed two-value domain; promoting from
/// string to enum (mithril#1125) closes the silent-typo gap at the producer
/// site without changing the wire format — the
/// <c>FilesystemCalibrationAttemptBundleSink</c> projects to the lowercase /
/// snake_case strings (<c>"blob_detection"</c>, <c>"synthesis_j"</c>) when
/// serialising to <c>10c-blob-pipeline.json</c>.
/// </summary>
public enum RimMaskPipeline
{
    /// <summary>The detector pipeline (<c>DeviationBlobDetector.DetectIconBlobs</c>).</summary>
    BlobDetection,

    /// <summary>The synthesis-J L_t builder (<c>MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation</c>).</summary>
    SynthesisJ,
}

/// <summary>
/// One observation per (orientation, pipeline) pair (mithril#1123). Pipeline
/// discriminates the two callers of <c>DeviationFloodRimMask.Build</c>:
/// <see cref="RimMaskPipeline.BlobDetection"/> (from <c>DeviationBlobDetector.DetectIconBlobs</c>)
/// and <see cref="RimMaskPipeline.SynthesisJ"/> (from
/// <c>MapCalibrationSolveEngine.BuildLikelihoodFieldsFromDeviation</c>).
/// <see cref="FgInputCount"/> / <see cref="FgSurvivorCount"/> are populated on
/// the blob-detection path; synthesis-J supplies <c>null</c> (mithril#1125 —
/// the in-memory record uses nullable to express "absent" semantically; the
/// JSON wire format still emits <c>-1</c> on those fields, projected at the
/// DTO boundary).
/// </summary>
public sealed record RimMaskSnapshot(
    RimMaskPipeline Pipeline,
    bool Rotate180,
    int Width,
    int Height,
    double Threshold,
    int RimPixelCount,
    int? FgInputCount,
    int? FgSurvivorCount,
    ReadOnlyMemory<bool> RimMaskBuffer)
{
    /// <summary>mithril#1126: see <see cref="DeviationSnapshot.ForegroundBuffer"/>.</summary>
    public ReadOnlyMemory<bool> RimMaskBuffer { get; init; } =
        AssertBufferLengthMatches(RimMaskBuffer, Width, Height, nameof(RimMaskBuffer));

    private static ReadOnlyMemory<bool> AssertBufferLengthMatches(
        ReadOnlyMemory<bool> buffer, int width, int height, string name)
    {
        System.Diagnostics.Debug.Assert(
            buffer.Length == width * height,
            $"RimMaskSnapshot.{name}.Length ({buffer.Length}) must equal Width*Height ({width}*{height}={width * height}).");
        return buffer;
    }
}

/// <summary>
/// Snapshot emitted by <see cref="DeviationBlobDetector.DetectIconBlobs"/>
/// immediately after the mithril#1116 deviation-mask subtract step (alpha-
/// boundary + fog-of-war mask), before morph-close. Parallels
/// <see cref="RimMaskSnapshot"/>; same shape, different masking source.
/// </summary>
/// <remarks>
/// <see cref="MaskPixelCount"/> is the count of <c>true</c> pixels in the
/// combined deviation mask (boundary OR fog) at emission time —
/// the set of pixels the subtract step removed from the foreground.
/// <see cref="FgInputCount"/> / <see cref="FgSurvivorCount"/> bracket the
/// subtract: input = fg-true count BEFORE the subtract; survivor = fg-true
/// count AFTER. <see cref="MaskBuffer"/> is retained for the diagnostic bundle
/// (mirrors <see cref="RimMaskSnapshot.RimMaskBuffer"/>).
/// </remarks>
public sealed record DeviationMaskSnapshot(
    bool Rotate180,
    int Width,
    int Height,
    int MaskPixelCount,
    int FgInputCount,
    int FgSurvivorCount,
    ReadOnlyMemory<bool> MaskBuffer)
{
    /// <summary>mithril#1126: see <see cref="DeviationSnapshot.ForegroundBuffer"/>.</summary>
    public ReadOnlyMemory<bool> MaskBuffer { get; init; } =
        AssertBufferLengthMatches(MaskBuffer, Width, Height, nameof(MaskBuffer));

    private static ReadOnlyMemory<bool> AssertBufferLengthMatches(
        ReadOnlyMemory<bool> buffer, int width, int height, string name)
    {
        System.Diagnostics.Debug.Assert(
            buffer.Length == width * height,
            $"DeviationMaskSnapshot.{name}.Length ({buffer.Length}) must equal Width*Height ({width}*{height}={width * height}).");
        return buffer;
    }
}

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
    ReadOnlyMemory<bool> FgAfterMorphBuffer)
{
    /// <summary>mithril#1126: see <see cref="DeviationSnapshot.ForegroundBuffer"/>.</summary>
    public ReadOnlyMemory<bool> FgAfterMorphBuffer { get; init; } =
        AssertBufferLengthMatches(FgAfterMorphBuffer, Width, Height, nameof(FgAfterMorphBuffer));

    private static ReadOnlyMemory<bool> AssertBufferLengthMatches(
        ReadOnlyMemory<bool> buffer, int width, int height, string name)
    {
        System.Diagnostics.Debug.Assert(
            buffer.Length == width * height,
            $"MorphSnapshot.{name}.Length ({buffer.Length}) must equal Width*Height ({width}*{height}={width * height}).");
        return buffer;
    }
}

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
    BlobClass BlobClass,
    IReadOnlyList<int> Pixels)
{
    /// <summary>
    /// mithril#1126: BlobClassification's invariant is per-comp (not per-image):
    /// <see cref="Pixels"/>.Count must equal <see cref="Area"/>. The producer
    /// computes Area from the pixel list (BlobFeat.Area => Pixels.Count); this
    /// assertion guards against a producer that copies one without the other.
    /// </summary>
    public IReadOnlyList<int> Pixels { get; init; } =
        AssertPixelsMatchArea(Pixels, Area);

    private static IReadOnlyList<int> AssertPixelsMatchArea(IReadOnlyList<int> pixels, int area)
    {
        System.Diagnostics.Debug.Assert(
            pixels.Count == area,
            $"BlobClassification.Pixels.Count ({pixels.Count}) must equal Area ({area}).");
        return pixels;
    }
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
