using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Capture.Internal;

/// <summary>
/// Source-generated JSON context for internal capture-side records (ORB
/// descriptor cache manifest, etc). Kept separate from
/// <see cref="Mithril.MapCalibration.Capture.Diagnostics.CalibrationBundleJsonContext"/>
/// (which is for user-facing diagnostic bundle output) so the two concerns
/// don't bleed.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OrbDescriptorManifest))]
internal partial class CaptureJsonContext : JsonSerializerContext;
