using Mithril.Tools.MapCalibration.Common;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe;

/// <summary>
/// Loads <see cref="ReferencePoint"/> entries for a given area by combining
/// landmarks (Portal / MeditationPillar / TeleportationPlatform) from
/// <c>landmarks.json</c> and NPC positions from <c>npcs.json</c>.
/// </summary>
internal static class ProbeReferences
{
    public static IReadOnlyList<ReferencePoint> Load(string landmarksJson, string npcsJson, string area)
    {
        var result = new List<ReferencePoint>();

        foreach (var l in LandmarksReader.LoadForArea(landmarksJson, area))
        {
            result.Add(new ReferencePoint(l.Name, l.Type, l.World.X, l.World.Z));
        }

        foreach (var n in NpcsReader.LoadForArea(npcsJson, area))
        {
            result.Add(new ReferencePoint(n.Name, n.Type, n.World.X, n.World.Z));
        }

        return result;
    }

    /// <summary>Absolute path to the bundled <c>landmarks.json</c> in the repo.</summary>
    public static string DefaultLandmarksPath() => RepoPaths.LandmarksJsonPath();

    /// <summary>Absolute path to the bundled <c>npcs.json</c> in the repo.</summary>
    public static string DefaultNpcsPath() => RepoPaths.NpcsJsonPath();
}
