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
    LocatorBestJson? LocatorBest = null);

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
    string? RecoveredCalibration);

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
    double CalibrationZoom,
    double ResidualPixels,
    int ReferenceCount,
    string Source,
    IReadOnlyList<InlierJson> Inliers);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(LocatorBestJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
