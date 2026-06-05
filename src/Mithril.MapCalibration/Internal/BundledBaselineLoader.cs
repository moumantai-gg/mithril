using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Loads the bundled per-area baseline calibrations from the embedded
/// <c>BundledData/map-calibration-baseline.json</c> resource shipped with this
/// assembly. The file is hand-authored once per area (#830 Tier 2a) and ships
/// as the fallback when no per-user or community-sync source exists.
///
/// <para>If the resource is missing or malformed the loader returns an empty
/// catalogue and logs a warning &#8212; the service still works, just without
/// any baseline coverage. This matches the spec's "no calibration =
/// IsCalibrated returns false" degradation path.</para>
/// </summary>
internal static class BundledBaselineLoader
{
    private const string ResourceName = "Mithril.MapCalibration.BundledData.map-calibration-baseline.json";

    public static IReadOnlyDictionary<string, AreaCalibration> Load(ILogger? logger)
    {
        var assembly = typeof(BundledBaselineLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            logger?.LogWarning("Bundled baseline resource {Resource} not found in assembly {Assembly} — no baseline calibrations available.",
                ResourceName, assembly.GetName().Name);
            return EmptyCatalogue();
        }

        try
        {
            var file = JsonSerializer.Deserialize(stream, MapCalibrationJsonContext.Default.BundledBaselineFile);
            if (file is null || file.Anchors is null)
            {
                logger?.LogWarning("Bundled baseline resource deserialised to null — no baseline calibrations available.");
                return EmptyCatalogue();
            }

            // Stamp Source AND Frame on every entry so consumers always see
            // BundledBaseline + Texture, even if the file omits either property.
            // (Source defaults to UserRefinement on the record; Frame defaults to
            // Texture and so is technically redundant — set explicitly anyway so
            // the post-deserialise stamping is the single source of truth for the
            // bundled catalogue. The bundled JSON is, by construction, all
            // BundledBaseline → Texture per spec §7.2.)
            var stamped = new Dictionary<string, AreaCalibration>(file.Anchors.Count, StringComparer.Ordinal);
            foreach (var (key, cal) in file.Anchors)
            {
                stamped[key] = cal with
                {
                    Source = CalibrationSource.BundledBaseline,
                    Frame = CalibrationFrame.Texture,
                };
            }
            return stamped;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Bundled baseline resource {Resource} failed to parse — no baseline calibrations available.", ResourceName);
            return EmptyCatalogue();
        }
    }

    private static IReadOnlyDictionary<string, AreaCalibration> EmptyCatalogue() =>
        new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
}
