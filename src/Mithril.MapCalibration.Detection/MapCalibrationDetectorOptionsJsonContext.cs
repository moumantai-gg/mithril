using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// System.Text.Json source-gen context for the persisted
/// <see cref="MapCalibrationDetectorOptions"/>. Used by
/// <c>JsonSettingsStore&lt;T&gt;</c> +
/// <c>AddMithrilVersionedSettings&lt;T&gt;</c> to load/save
/// <c>map-calibration-detector.json</c> with no reflection at runtime
/// (mithril#1116).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MapCalibrationDetectorOptions))]
public partial class MapCalibrationDetectorOptionsJsonContext : JsonSerializerContext;
