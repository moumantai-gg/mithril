using System.IO;
using System.Text.Json;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;

internal static class BundleLoader
{
    public static LoadedBundle Open(string directory)
    {
        var attemptPath = Path.Combine(directory, "01-attempt.json");
        if (!File.Exists(attemptPath))
            throw new FileNotFoundException($"Bundle missing 01-attempt.json", attemptPath);

        var attempt = JsonSerializer.Deserialize(
            File.ReadAllText(attemptPath),
            BundleJsonContext.Default.AttemptJson)!;

        var mapRect = LoadOptionalJson(directory, attempt.Files.MapRect, BundleJsonContext.Default.MapRectJson);
        var recoveredCal = LoadOptionalJson(directory, attempt.Files.RecoveredCalibration, BundleJsonContext.Default.RecoveredCalibrationJson);
        var detections = LoadOptionalJson(directory, attempt.Files.Detections, BundleJsonContext.Default.DetectionsJson);

        string? deviationPath = attempt.Files.Deviation is { } name
            ? Path.Combine(directory, name)
            : null;
        if (deviationPath is not null && !File.Exists(deviationPath))
            deviationPath = null;

        return new LoadedBundle(directory, attempt, mapRect, recoveredCal, detections, deviationPath);
    }

    private static T? LoadOptionalJson<T>(
        string directory,
        string? fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (fileName is null) return null;
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
    }
}
