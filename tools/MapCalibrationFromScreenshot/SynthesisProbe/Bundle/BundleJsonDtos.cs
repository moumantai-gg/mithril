using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

// Mirrors src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs.
// Replicated here (rather than ProjectReference'd) to keep the tool decoupled
// from the WPF-flavored Capture project. Shape parity is the contract — if
// CalibrationBundleJsonContext gains a field, mirror it here too.
internal sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files);

internal sealed record AttemptFilesJson(
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

internal sealed record MapRectJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight);

internal sealed record DetectionJson(
    string LandmarkType,
    string IconName,
    double AnchorX,
    double AnchorY,
    double Score);

internal sealed record DetectionsJson(
    int SchemaVersion,
    int RenderSizePx,
    IReadOnlyList<DetectionJson> Detections);

internal sealed record InlierJson(
    string Label,
    double WorldX,
    double WorldZ,
    double PixelX,
    double PixelY,
    double MatchScore);

internal sealed record RecoveredCalibrationJson(
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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
internal partial class BundleJsonContext : JsonSerializerContext;
