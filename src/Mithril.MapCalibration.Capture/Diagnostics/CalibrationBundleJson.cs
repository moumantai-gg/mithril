using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Capture.Diagnostics;

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
    SynthesisJson? Synthesis = null);

/// <summary>
/// Carries the locator's raw fit rect (gate-pass-or-not), the per-algorithm
/// metrics, and the gate verdict that drove the engine's outcome.
///
/// <para><b>Schema v2 (mithril#1061):</b> adds <see cref="Algorithm"/>,
/// <see cref="FallbackNcc"/>, <see cref="PadPx"/>. Readers should treat absence
/// of these as v1 ORB-only (default <c>Algorithm = "orb-lowe"</c>, others
/// null).</para>
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
    int? PadPx = null);

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
    string? BlobTemplateScores = null);

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
/// </summary>
public sealed record BlobTemplateScoreJson(
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

/// <summary>
/// Top-level shape of the <c>10b-blob-template-scores.json</c> bundle dump
/// (mithril#1121). The list is grouped client-side by <c>blobIndex</c> /
/// <c>rotate180</c>; the wire format is a flat array so downstream jq /
/// pandas pipelines stay straightforward.
/// </summary>
public sealed record BlobTemplateScoresJson(
    int SchemaVersion,
    IReadOnlyList<BlobTemplateScoreJson> Scores);

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
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
