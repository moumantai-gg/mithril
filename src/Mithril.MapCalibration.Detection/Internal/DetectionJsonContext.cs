using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Source-generated JSON context for internal detection-side records: ORB
/// descriptor cache manifest, icon template manifest, map texture manifest,
/// canonical asset hashes, and sidecar results. Kept separate from
/// <see cref="Mithril.MapCalibration.Capture.Diagnostics.CalibrationBundleJsonContext"/>
/// (which is for user-facing diagnostic bundle output) so the two concerns
/// don't bleed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OrbDescriptorManifest))]
[JsonSerializable(typeof(IconTemplateManifest))]
[JsonSerializable(typeof(MapTextureManifest))]
[JsonSerializable(typeof(CanonicalAssetHashes))]
[JsonSerializable(typeof(CanonicalAssetHashEntry))]
[JsonSerializable(typeof(SidecarResult))]
internal partial class DetectionJsonContext : JsonSerializerContext;
