using System.Collections.Generic;

namespace Mithril.MapCalibration;

/// <summary>
/// The single source of truth for the landmark-type vocabulary the calibration
/// detect→solve pipeline pairs on. These are the <b>raw PG type</b> strings —
/// the same values that appear in landmarks.json (<c>Landmark.Type</c>), in the
/// bundled icon-template manifest's <c>landmarkType</c> field (which the loader
/// surfaces as <see cref="IconTemplate.LandmarkType"/>), and that the solver
/// matches with an <see cref="System.StringComparison.Ordinal"/> equality.
///
/// <para>Both sides of the correspondence — detection type-keys
/// (<see cref="IconTemplate.LandmarkType"/>) and reference type-keys
/// (<see cref="LandmarkReference.Type"/>) — MUST speak this vocabulary, or the
/// type-constrained RANSAC pool is empty and every solve rejects with 0 inliers
/// (mithril#974). The provider's allowlist consumes
/// <see cref="LandmarkTypes"/> so the vocabulary lives in exactly one place.</para>
/// </summary>
public static class CanonicalLandmarkTypes
{
    /// <summary>Portal landmark type (landmarks.json).</summary>
    public const string Portal = "Portal";

    /// <summary>Meditation-pillar landmark type (landmarks.json).</summary>
    public const string MeditationPillar = "MeditationPillar";

    /// <summary>Teleportation-platform landmark type (landmarks.json).</summary>
    public const string TeleportationPlatform = "TeleportationPlatform";

    /// <summary>NPC type — NPCs come from npcs.json, not landmarks.json, but pair like a landmark.</summary>
    public const string Npc = "Npc";

    /// <summary>
    /// The three types sourced from <c>landmarks.json</c> (excludes <see cref="Npc"/>),
    /// used as the provider's landmark allowlist.
    /// </summary>
    public static readonly IReadOnlySet<string> LandmarkTypes =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            Portal,
            MeditationPillar,
            TeleportationPlatform,
        };

    /// <summary>Every canonical type the solver pairs on (the three landmark types + <see cref="Npc"/>).</summary>
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            Portal,
            MeditationPillar,
            TeleportationPlatform,
            Npc,
        };
}
