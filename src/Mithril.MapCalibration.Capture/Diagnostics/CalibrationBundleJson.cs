using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Capture.Diagnostics;

/// <summary>
/// Top-level shape of <c>01-attempt.json</c> — the bundle header that ties
/// every per-attempt artifact path together with the locator/synthesis
/// diagnostics that produced the engine's outcome.
///
/// <para><b>Schema v2 (mithril#1061):</b> <see cref="LocatorBest"/> gained
/// <c>Algorithm</c> / <c>FallbackNcc</c> / <c>PadPx</c> fields. The
/// <see cref="SchemaVersion"/> bump tracks the nested change because
/// downstream tooling keys triage off the top-level number.</para>
///
/// <para><b>Schema v3 (mithril#1117):</b> adds optional <see cref="Synthesis"/>.
/// Readers should treat absence as "synthesis did not run / mode == Off"
/// rather than "missing data".</para>
///
/// <para><b>Schema v4 (mithril#1116):</b> adds optional
/// <see cref="AttemptFilesJson.DeviationMask"/> carrying the
/// <c>07a-deviation-mask.png</c> artifact path. Additive: v3 readers ignore
/// the new key, and v4 readers treat absence as "the engine did not write
/// a deviation mask for this attempt" (no fog/boundary mask was emitted).</para>
///
/// <para><b>Schema v4 additive (mithril#1163 Phase 1):</b> adds optional
/// <see cref="SceneClass"/>, <see cref="SceneClassOpaqueFraction"/>,
/// <see cref="Profile"/>. Additive — schema-version is NOT bumped to 5
/// because every existing v4 reader ignores unknown keys and v5 readers
/// would need a structural change before the bump pays off. Treat absence
/// as "the engine ran without scene-class resolution" (mask block skipped /
/// pre-#1163 engine), in which case the safe-degrade Outdoor profile was
/// in effect.</para>
/// </summary>
public sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files,
    // Coarse locator's raw fit rect + FM-style inlier/transform metrics + the gate
    // verdict that produced the engine's outcome. Populated on both accept and
    // rejected-map-not-located so the bundle is self-triaging for future
    // close-miss-vs-catastrophic-mismatch rejections. Null when the locator never
    // ran (early pre-locate rejects) or the captured frame had no viable fit.
    LocatorBestJson? LocatorBest = null,
    // Per-attempt synthesis-J snapshot (#1117). Null when SynthesisRerankMode == Off
    // or when this bundle was written by a pre-#1117 engine version (schema v1/v2).
    SynthesisJson? Synthesis = null,
    // mithril#1163 Phase 1: resolved scene class ("Outdoor" | "Indoor") + the
    // measured opaque-pixel fraction + the BlobOptions the profile dispatched.
    // Null when the engine ran without resolving the scene class (mask block
    // skipped — DeviationMaskingEnabled=false, boundary cache unwired, or a
    // pre-locate reject); readers treat absence as "Outdoor" by safe-degrade.
    string? SceneClass = null,
    double? SceneClassOpaqueFraction = null,
    SceneCalibrationProfileJson? Profile = null);

/// <summary>
/// Carries the locator's raw fit rect (gate-pass-or-not), the per-algorithm
/// metrics, and the gate verdict that drove the engine's outcome.
///
/// <para><b>Schema v2 (mithril#1061):</b> adds <see cref="Algorithm"/>,
/// <see cref="FallbackNcc"/>, <see cref="PadPx"/>. Readers should treat absence
/// of these as v1 ORB-only (default <c>Algorithm = "orb-lowe"</c>, others
/// null).</para>
///
/// <para><b>Schema v3 (mithril#1070):</b> adds <see cref="BlurAppliedSigma"/>
/// — the σ (px) of the Gaussian blur applied to the Sobel template at the
/// recovered scale in the fallback's full-resolution stage. Readers should
/// treat absence of this field as v2 (null).</para>
/// </summary>
public sealed record LocatorBestJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    double Tx,
    double Ty,
    double ResidualPixels,
    bool GateAccepted,
    string? GateRejectReason,
    string Algorithm = "orb-lowe",
    double? FallbackNcc = null,
    int? PadPx = null,
    // mithril#1070: σ (px) of the Gaussian blur applied to the Sobel template
    // at the recovered scale (the matchTemplate call that drove the recovered
    // Tx/Ty). Null on ORB primary or when RendererBlurEnabled=false. Zero when
    // the σ-curve clamped to 0 at the recovered scale.
    double? BlurAppliedSigma = null);

public sealed record AttemptFilesJson(
    string? RawScreenshot,
    string? GrayScreenshot,
    string? MapRect,
    string? BaseTextureResampled,
    string? AlignedScreenshot,
    string? Deviation,
    string? DetectionsImage,
    string? ProjectionOverlay,
    string? Detections,
    string? RecoveredCalibration,
    // mithril#1121: per-blob × per-template NCC observation dump
    // (10b-blob-template-scores.json). Default-null so pre-#1121 readers
    // round-trip unchanged; populated when the deviation-blob detector
    // emitted any blob scores (it always does in the production AutoCalibrationEngine
    // path).
    string? BlobTemplateScores = null,
    // mithril#1123: detector-pipeline observability dump (10c) + 10 PNGs for
    // the stage masks (foreground, rim, morph, classification × orientation).
    // All default-null so pre-#1123 readers round-trip unchanged. The 10c JSON
    // pairs with the BlobOrdinal cross-ref in 10b — same int identifies the
    // same physical blob in both files (D3.a).
    string? BlobPipeline = null,           // 10c-blob-pipeline.json
    string? Foreground = null,             // 07b-foreground.png
    string? ForegroundR180 = null,         // 07b-r180-foreground.png
    string? RimMask = null,                // 07c-rim-mask.png
    string? RimMaskR180 = null,            // 07c-r180-rim-mask.png
    string? SynthRimMask = null,           // 07c-synth-rim-mask.png
    string? SynthRimMaskR180 = null,       // 07c-r180-synth-rim-mask.png
    string? Morphed = null,                // 07d-morphed.png
    string? MorphedR180 = null,            // 07d-r180-morphed.png
    string? BlobClassification = null,     // 07e-blob-classification.png
    string? BlobClassificationR180 = null, // 07e-r180-blob-classification.png
    // mithril#1116 (AttemptJson v4): per-attempt deviation mask PNG —
    // OR-combination of the floor-boundary alpha-edge mask + the per-frame
    // fog-of-war edge mask, sampled at deviation-grid coordinates. White
    // pixels mark deviation-grid cells the blob detector excluded from
    // candidate generation (so spurious wall/fog-induced blobs do not feed
    // into NCC / synthesis). Null on pre-#1116 engine versions or when the
    // detector ran with the mask disabled (e.g. no base-texture alpha
    // available + no fog detected).
    string? DeviationMask = null           // 07a-deviation-mask.png
);

public sealed record MapRectJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight);

public sealed record DetectionJson(
    string LandmarkType,
    string IconName,
    double AnchorX,
    double AnchorY,
    double Score);

public sealed record DetectionsJson(
    int SchemaVersion,
    int RenderSizePx,
    IReadOnlyList<DetectionJson> Detections);

public sealed record InlierJson(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

public sealed record RecoveredCalibrationJson(
    int SchemaVersion,
    double Scale,
    double RotationRadians,
    double OriginX,
    double OriginY,
    bool MirrorNorth,
    double ResidualPixels,
    int ReferenceCount,
    string Source,
    IReadOnlyList<InlierJson> Inliers);

/// <summary>
/// Bundle wire-format mirror of <see cref="Mithril.MapCalibration.Detection.SynthesisDiagnostics"/>.
/// SchemaVersion 1 — first persisted version. Null on <see cref="AttemptJson.Synthesis"/>
/// when synthesis did not run (<c>SynthesisRerankMode == Off</c>) or when the bundle was
/// written by a pre-#1117 engine version (schema v1/v2 AttemptJson).
/// </summary>
public sealed record SynthesisJson(
    int SchemaVersion,
    string Mode,
    bool? Rotate180,
    double? J,
    double JMin,
    int? RefsAboveHalf,
    int? RefsTotal,
    int? RefsOffCrop,
    int NMin,
    string Verdict,
    string GateVerdict,
    bool Disagree,
    string? DisagreeChange);

/// <summary>
/// Per-blob × per-template NCC observation in the
/// <c>10b-blob-template-scores.json</c> bundle dump (mithril#1121).
/// Wire-format mirror of <see cref="Mithril.MapCalibration.BlobTemplateScore"/>;
/// see that type's doc for field semantics (<c>Score</c> is <c>NaN</c> on the
/// skip path; <c>aboveFloor</c> is the gate verdict; <c>rotate180</c>
/// disambiguates the two orientation passes the engine runs).
///
/// <para><b>Schema v2 (mithril#1123 D3.a):</b> <c>BlobIndex</c> renamed to
/// <c>BlobOrdinal</c> with all-blobs semantics — the same ordinal carried by
/// <c>BlobClassification.BlobOrdinal</c> in <c>10c-blob-pipeline.json</c>
/// (the per-comp classification dump). 10b's records are sparse over the
/// 10c ordinal space: only Icon-class blobs that ran per-template NCC
/// emit here.</para>
/// </summary>
public sealed record BlobTemplateScoreJson(
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

/// <summary>
/// Top-level shape of the <c>10b-blob-template-scores.json</c> bundle dump
/// (mithril#1121). The list is grouped client-side by <c>blobOrdinal</c> /
/// <c>rotate180</c>; the wire format is a flat array so downstream jq /
/// pandas pipelines stay straightforward.
/// </summary>
public sealed record BlobTemplateScoresJson(
    int SchemaVersion,
    IReadOnlyList<BlobTemplateScoreJson> Scores);

/// <summary>
/// Per-orientation deviation section in <c>10c-blob-pipeline.json</c>
/// (mithril#1123). Wire-format mirror of <c>DeviationSnapshot</c>; the
/// <c>ForegroundBuffer bool[]</c> is NOT serialised here — it renders to
/// <c>07b-foreground.png</c> instead.
/// </summary>
public sealed record DeviationSectionJson(
    bool Rotate180,
    int Width, int Height, int Win,
    double Threshold, double MeanNcc,
    double Min, double Max, double Mean,
    double P50, double P95, double P99,
    int AboveThresholdCount);

/// <summary>
/// Per-(orientation, pipeline) rim-mask section in <c>10c-blob-pipeline.json</c>
/// (mithril#1123). pipeline ∈ <c>{"blob_detection", "synthesis_j"}</c>;
/// <c>FgInputCount</c> / <c>FgSurvivorCount</c> carry <c>-1</c> sentinels on
/// the synthesis_j path (no fg-mask concept there). The bool[] mask renders
/// to <c>07c-rim-mask.png</c> / <c>07c-synth-rim-mask.png</c> instead of
/// serialising here.
/// </summary>
public sealed record RimMaskSectionJson(
    string Pipeline,
    bool Rotate180,
    int Width, int Height,
    double Threshold,
    int RimPixelCount,
    int FgInputCount,
    int FgSurvivorCount);

/// <summary>
/// Per-orientation morph-close section in <c>10c-blob-pipeline.json</c>
/// (mithril#1123). Wire-format mirror of <c>MorphSnapshot</c>; the post-morph
/// bool[] renders to <c>07d-morphed.png</c> instead of serialising here.
/// </summary>
public sealed record MorphSectionJson(
    bool Rotate180,
    int Width, int Height,
    int CloseRadius,
    int FgInputCount,
    int FgOutputCount);

/// <summary>
/// Per-comp classification record in <c>10c-blob-pipeline.json</c>
/// (mithril#1123). One per blob across ALL comps (Noise/Icon/Fog/Structure),
/// not just the Icon-class subset that emits to 10b. <see cref="BlobOrdinal"/>
/// cross-refs <see cref="BlobTemplateScoreJson.BlobOrdinal"/> in 10b — same
/// physical blob, same int (D3.a).
///
/// <para><b>Pixels NOT serialised:</b> the in-memory <c>BlobClassification</c>
/// record carries a <c>Pixels</c> list for the bundle sink's
/// <c>07e-blob-classification.png</c> render, but the JSON shape excludes it
/// — keeps 10c bounded in size on Hogan's-shaped inputs (~50 blobs × ~200 px
/// each would 10×-bloat the file).</para>
/// </summary>
public sealed record BlobJson(
    bool Rotate180,
    int BlobOrdinal,
    int MinX, int MinY,
    int W, int H, int Area,
    double Cx, double Cy,
    double MeanDev, double PeakDev,
    double Solidity, double Aspect,
    string BlobClass);

/// <summary>
/// Top-level shape of <c>10c-blob-pipeline.json</c> (mithril#1123). The four
/// sections (deviation, rim masks, morph, blobs) co-locate the per-stage
/// observations so downstream triage opens one file per attempt instead of
/// four. <see cref="RimMasks"/> is a flat array with the <c>pipeline</c>
/// discriminator (per D6.a) — both blob_detection and synthesis_j records
/// live in the same list so a triager can compare them side-by-side.
/// </summary>
public sealed record BlobPipelineJson(
    int SchemaVersion,
    IReadOnlyList<DeviationSectionJson> Deviation,
    IReadOnlyList<RimMaskSectionJson> RimMasks,
    IReadOnlyList<MorphSectionJson> Morph,
    IReadOnlyList<BlobJson> Blobs);

/// <summary>
/// mithril#1163 Phase 1: bundle wire-format for the resolved
/// <see cref="Mithril.MapCalibration.Detection.SceneCalibrationProfile"/>.
/// Carries the profile's BlobOptions verbatim so a triager can read the
/// exact gates the detector ran with for this attempt (Outdoor and Indoor
/// differ here in v1). Future phases (per spec §5.1) extend this DTO as
/// additional fields diverge.
/// </summary>
public sealed record SceneCalibrationProfileJson(
    int MinArea,
    int MaxIconArea,
    double MinSolidity,
    double MaxAspect,
    double MinPeak)
{
    /// <summary>
    /// mithril#1155 Phase 3 (review #1169-r2): raw-BGRA peak-luma gate.
    /// <see langword="null"/> in Outdoor / pre-#1155 attempts (peak-luma filter
    /// inactive); 0.7 in Indoor v1. Init-only so existing positional
    /// constructions stay source-compatible; the JSON projection emits null when
    /// the field is null per the DefaultIgnoreCondition / camelCase pipeline.
    /// </summary>
    public double? MinPeakLuma { get; init; }
}

// mithril#1121: AllowNamedFloatingPointLiterals lets BlobTemplateScore.Score
// round-trip its NaN sentinel (the skip-path marker) as the JSON token "NaN".
// Existing DTOs in this context don't carry NaN values, so this is additive.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(LocatorBestJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
[JsonSerializable(typeof(SynthesisJson))]
[JsonSerializable(typeof(BlobTemplateScoresJson))]
[JsonSerializable(typeof(BlobPipelineJson))]                        // mithril#1123
[JsonSerializable(typeof(SceneCalibrationProfileJson))]             // mithril#1163
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
