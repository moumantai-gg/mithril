using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Reference.Models.Misc;
using Mithril.Shared.Reference;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Builds <see cref="LandmarkReference"/>s for an area from
/// <see cref="IReferenceDataService"/>: landmarks (<c>landmarks.json</c>) keyed by
/// area + NPCs (<c>npcs.json</c>, the full POCO that carries <see cref="Npc.Pos"/>)
/// whose <see cref="Npc.AreaName"/> matches the area.
///
/// <para>Each source is mapped to a <see cref="LandmarkReference"/> whose
/// <c>Type</c> is the <b>raw PG landmark type</b> — the canonical vocabulary
/// (<c>Portal</c> / <c>MeditationPillar</c> / <c>TeleportationPlatform</c> /
/// <c>Npc</c>, see <see cref="CanonicalLandmarkTypes"/>) the detector also keys
/// its <see cref="IconTemplate.LandmarkType"/> detections by, so the
/// type-constrained RANSAC solver can pair them with an Ordinal type match
/// (mithril#974). The <c>landmark_*</c> sprite strings are template <i>names</i>
/// (<see cref="LandmarkReference.Name"/>), never types. World coords are parsed
/// from the <c>"x:N y:N z:N"</c> position string (the live <c>landmarks.json</c> /
/// <c>npcs.json</c> shape — memory <c>pg_reference_coords_are_world_frame</c>).</para>
/// </summary>
public sealed class ReferenceDataAreaReferenceProvider : IAreaReferenceProvider
{
    // The canonical raw-PG landmark types we accept from landmarks.json. Confirmed
    // against the v470 corpus on 2026-05-31: Type ∈ { Portal, MeditationPillar,
    // TeleportationPlatform } — every landmark carries exactly one of these. NPCs
    // are emitted with the CanonicalLandmarkTypes.Npc type. The vocabulary lives
    // in CanonicalLandmarkTypes so detection-side (IconTemplate.LandmarkType) and
    // reference-side keys never diverge again (mithril#974). Any future/unmapped
    // Type is WARNED and dropped (no silent coercion to a wrong type, which would
    // mispair a detection and corrupt the solve).
    private static readonly IReadOnlySet<string> KnownLandmarkTypes = CanonicalLandmarkTypes.LandmarkTypes;

    private readonly IReferenceDataService _refData;
    private readonly ILogger? _logger;

    public ReferenceDataAreaReferenceProvider(IReferenceDataService refData, ILogger? logger = null)
    {
        _refData = refData;
        _logger = logger;
    }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef)
    {
        var areaKey = sceneRef.ParentAreaKey;
        if (string.IsNullOrWhiteSpace(areaKey)) return Array.Empty<LandmarkReference>();

        var result = new List<LandmarkReference>();
        // Count references dropped because a position string was PRESENT but didn't
        // parse — that, and only that, signals a possible landmarks.json / npcs.json
        // coord-shape change worth a warning (Fix E). A null/empty position is a
        // legitimately positionless table entry (e.g. the "Work Orders" sign, the
        // "Sacrificial Bowl" pedestal in npcs.json) and is skipped silently — it was
        // never a calibration reference and is not a shape regression.
        var malformedCoords = 0;

        // Landmarks: area-only filter (no sub-zone field on landmarks.json — partial
        // coverage for aggregator scenes is documented in spec mithril#1021;
        // the RANSAC solver tolerates the cross-scene noise via type-constrained
        // pairing + the inlier ratio gate).
        if (_refData.Landmarks.TryGetValue(areaKey, out var landmarks))
        {
            foreach (var lm in landmarks)
            {
                if (lm is null || string.IsNullOrEmpty(lm.Type)) continue;
                if (!KnownLandmarkTypes.Contains(lm.Type))
                {
                    // No silent drop: surface the unmapped type so an uncovered
                    // landmark category is visible (verification-owed follow-up).
                    _logger?.LogWarning(
                        "Unmapped landmark Type {Type} in area {Area} (landmark {Name}); dropped — not a canonical landmark type. " +
                        "Verification owed (#914): confirm Landmark.Type vocabulary vs live landmarks.json + the icon-template manifest.",
                        lm.Type, areaKey, lm.Name);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(lm.Loc)) continue; // positionless entry — skip, not a shape change
                if (TryParseWorld(lm.Loc, out var world))
                {
                    // Emit the raw PG type verbatim — the same vocabulary the
                    // detector keys IconTemplate.LandmarkType by (mithril#974).
                    result.Add(new LandmarkReference(lm.Type, lm.Name ?? lm.Type, world));
                }
                else
                {
                    malformedCoords++;
                }
            }
        }

        // NPCs: (AreaName, AreaFriendlyName) pair when SceneFriendlyName is set
        // (aggregator scene narrowed to one sub-zone — mithril#1021), else
        // area-only (back-compat for directly-registered areas).
        foreach (var npc in _refData.NpcsByInternalName.Values)
        {
            if (npc is null) continue;
            if (!string.Equals(npc.AreaName, areaKey, StringComparison.Ordinal)) continue;
            if (sceneRef.SceneFriendlyName is { } sub
                && !string.Equals(npc.AreaFriendlyName, sub, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(npc.Pos)) continue; // positionless entry — skip, not a shape change
            if (TryParseWorld(npc.Pos, out var world))
            {
                result.Add(new LandmarkReference(CanonicalLandmarkTypes.Npc, npc.Name ?? "NPC", world));
            }
            else
            {
                malformedCoords++;
            }
        }

        if (malformedCoords > 0)
        {
            _logger?.LogWarning(
                "Dropped {Count} reference(s) in area {Area} with malformed coords — possible landmarks.json / npcs.json "
                + "coord-shape change. Verification owed (#914): confirm the \"x:N y:N z:N\" position shape vs live data.",
                malformedCoords, areaKey);
        }

        return result;
    }

    /// <summary>
    /// Parses a PG position string. The live shape is <c>"x:N y:N z:N"</c>
    /// (landmarks.json / npcs.json, v470); also tolerates a bare <c>"x y z"</c>
    /// or <c>"x,y,z"</c> form so a future shape variant degrades to a skip, not a
    /// crash. Returns false (skip this reference) on any malformed input.
    /// </summary>
    internal static bool TryParseWorld(string? loc, out WorldCoord world)
    {
        world = default;
        if (string.IsNullOrWhiteSpace(loc)) return false;

        // Strip "x:" / "y:" / "z:" labels if present, then split on whitespace/comma.
        Span<double> coords = stackalloc double[3];
        var count = 0;
        foreach (var rawToken in loc.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken;
            var colon = token.IndexOf(':');
            if (colon >= 0) token = token[(colon + 1)..];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return false; // a non-numeric token → malformed → skip
            if (count >= 3) return false; // more than 3 numbers → unexpected shape → skip
            coords[count++] = value;
        }

        if (count != 3) return false;
        world = new WorldCoord(coords[0], coords[1], coords[2]);
        return true;
    }
}
