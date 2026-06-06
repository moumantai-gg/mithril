using System.Text.Json;
using System.Text.Json.Nodes;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Reads and writes <c>src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json</c>.
/// Uses <see cref="JsonObject"/> rather than the internal
/// <c>MapCalibrationJsonContext</c> so the tool can preserve the <c>$schema</c>
/// pointer and any unknown future fields without depending on internals of the
/// referenced project.
///
/// <para>Writes JSON in the exact shape <c>BundledBaselineLoader</c> reads:
/// camelCase property names, indented, default values omitted.</para>
/// </summary>
public static class BaselineFile
{
    /// <summary>
    /// Reads the currently-stored anchor for <paramref name="area"/> from
    /// <paramref name="baselinePath"/>, or null if the file/anchor isn't
    /// present. Used by the WPF workspace's dirty-state check to decide
    /// whether the Commit button should enable.
    ///
    /// <para>Mirrors <see cref="UpsertAnchor"/>'s JsonNode-based reader so the
    /// tool doesn't need to take a dependency on
    /// <c>Mithril.MapCalibration.Internal.BundledBaselineLoader</c> (internal).
    /// Field defaults match <c>AreaCalibration</c>: MirrorNorth=false,
    /// Source=UserRefinement, SchemaVersion=1.</para>
    /// </summary>
    public static AreaCalibration? TryReadAnchor(string baselinePath, string area)
    {
        if (!File.Exists(baselinePath)) return null;
        var text = File.ReadAllText(baselinePath);
        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            // Wrap parse failures as UserFacingException so the caller's
            // existing catch surface (and the WPF tool's error dialogs) handle
            // a corrupt baseline the same way they handle a missing one.
            throw new UserFacingException($"baseline JSON at {baselinePath} is malformed: {ex.Message}");
        }
        if (rootNode is not JsonObject root) return null;
        if (root["anchors"] is not JsonObject anchors) return null;
        if (anchors[area] is not JsonObject obj) return null;
        return DeserializeAreaCalibration(obj, baselinePath, area);
    }

    public static void UpsertAnchor(string baselinePath, string area, AreaCalibration cal)
    {
        if (!File.Exists(baselinePath))
        {
            throw new UserFacingException($"baseline JSON not found: {baselinePath}");
        }

        var text = File.ReadAllText(baselinePath);
        var root = (JsonNode.Parse(text) as JsonObject)
            ?? throw new UserFacingException($"baseline JSON at {baselinePath} is not an object");

        if (root["anchors"] is not JsonObject anchors)
        {
            anchors = new JsonObject();
            root["anchors"] = anchors;
        }

        anchors[area] = SerializeAreaCalibration(cal);

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            // ToJsonString already preserves property order so we don't need a
            // custom comparer; the bundled-loader doesn't care about order.
        });
        File.WriteAllText(baselinePath, json + Environment.NewLine);
    }

    private static AreaCalibration DeserializeAreaCalibration(JsonObject obj, string baselinePath, string area)
    {
        // Required fields — UpsertAnchor always writes all six, so a missing
        // one means the JSON was hand-edited badly or truncated. Throw rather
        // than coercing to 0.0 (which would silently yield an all-zeros
        // "calibration" the dirty-check compares against — round-2 review nit).
        var scale = RequireDouble(obj, "scale", baselinePath, area);
        var rotation = RequireDouble(obj, "rotationRadians", baselinePath, area);
        var originX = RequireDouble(obj, "originX", baselinePath, area);
        var originY = RequireDouble(obj, "originY", baselinePath, area);
        var refCount = RequireInt(obj, "referenceCount", baselinePath, area);
        var residual = RequireDouble(obj, "residualPixels", baselinePath, area);

        var cal = new AreaCalibration(scale, rotation, originX, originY, refCount, residual);

        if (obj["mirrorNorth"]?.GetValue<bool>() is bool mirror) cal = cal with { MirrorNorth = mirror };
        if (obj["source"]?.GetValue<string>() is string srcStr
            && Enum.TryParse<CalibrationSource>(srcStr, out var src))
        {
            cal = cal with { Source = src };
        }
        if (obj["schemaVersion"]?.GetValue<int>() is int sv) cal = cal with { SchemaVersion = sv };
        return cal;
    }

    private static double RequireDouble(JsonObject obj, string key, string baselinePath, string area)
    {
        if (obj[key] is not JsonNode node)
        {
            throw new UserFacingException(
                $"baseline JSON at {baselinePath} is missing required field anchors[{area}].{key}");
        }
        try { return node.GetValue<double>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new UserFacingException(
                $"baseline JSON at {baselinePath} field anchors[{area}].{key} is not a number");
        }
    }

    private static int RequireInt(JsonObject obj, string key, string baselinePath, string area)
    {
        if (obj[key] is not JsonNode node)
        {
            throw new UserFacingException(
                $"baseline JSON at {baselinePath} is missing required field anchors[{area}].{key}");
        }
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new UserFacingException(
                $"baseline JSON at {baselinePath} field anchors[{area}].{key} is not an integer");
        }
    }

    private static JsonObject SerializeAreaCalibration(AreaCalibration cal)
    {
        // Mirror MapCalibrationJsonContext's DefaultIgnoreCondition = WhenWritingDefault.
        // Defaults per AreaCalibration.cs: MirrorNorth=false, Source=UserRefinement, SchemaVersion=1.
        var obj = new JsonObject
        {
            ["scale"] = cal.Scale,
            ["rotationRadians"] = cal.RotationRadians,
            ["originX"] = cal.OriginX,
            ["originY"] = cal.OriginY,
            ["referenceCount"] = cal.ReferenceCount,
            ["residualPixels"] = cal.ResidualPixels,
        };
        if (cal.MirrorNorth) obj["mirrorNorth"] = true;
        if (cal.Source != CalibrationSource.UserRefinement) obj["source"] = cal.Source.ToString();
        if (cal.SchemaVersion != 1) obj["schemaVersion"] = cal.SchemaVersion;
        return obj;
    }
}
