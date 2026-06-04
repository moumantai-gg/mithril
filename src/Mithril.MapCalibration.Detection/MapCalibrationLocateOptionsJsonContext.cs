using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// System.Text.Json source-gen context for the persisted
/// <see cref="MapCalibrationLocateOptions"/>. Used by
/// <c>JsonSettingsStore&lt;T&gt;</c> +
/// <c>AddMithrilVersionedSettings&lt;T&gt;</c> to load/save
/// <c>map-calibration-locate.json</c> with no reflection at runtime
/// (mithril#1061).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MapCalibrationLocateOptions))]
public partial class MapCalibrationLocateOptionsJsonContext : JsonSerializerContext;
