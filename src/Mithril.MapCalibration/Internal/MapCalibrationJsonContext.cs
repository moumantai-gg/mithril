using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Source-generated <c>JsonSerializerContext</c> for the bundled baseline file
/// and the per-user refinement store. Project convention requires reflection-free
/// serialization (CLAUDE.md, "Settings classes" bullet).
/// </summary>
// UseStringEnumConverter is load-bearing: the bundled baseline JSON (and the
// tool that writes it, tools/.../BaselineFile.SerializeAreaCalibration) stores
// AreaCalibration.Source as the enum *name* ("BundledBaseline"), per the
// project's "string enums in JSON" convention. Without this the source-gen
// deserializer expects a number, throws on the string, and BundledBaselineLoader
// silently returns an empty catalogue — i.e. the engine couldn't load its own
// committed baseline (caught by the #916 replay; the prior >=0 loader test
// didn't notice because empty is "valid"). See #916.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(BundledBaselineFile))]
[JsonSerializable(typeof(UserRefinementFile))]
[JsonSerializable(typeof(Dictionary<string, AreaCalibration>))]
[JsonSerializable(typeof(AreaCalibration))]
internal sealed partial class MapCalibrationJsonContext : JsonSerializerContext;

/// <summary>On-disk shape for the embedded baseline JSON resource.</summary>
internal sealed record BundledBaselineFile(
    int SchemaVersion,
    Dictionary<string, AreaCalibration> Anchors);

/// <summary>On-disk shape for the per-user refinement store JSON file.</summary>
internal sealed record UserRefinementFile(
    int SchemaVersion,
    Dictionary<string, AreaCalibration> Calibrations);
