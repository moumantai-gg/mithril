// Type lives in Arda.Contracts (NOT Mithril.MapCalibration) so IMapState can
// expose it without creating a circular project reference between Arda.Contracts
// (consumer of MapSceneRef on IMapState) and Mithril.MapCalibration (which needs
// Arda.Contracts for IMapState, MapAssetChanged, IDomainEventSubscriber).
// Namespace is preserved as Mithril.MapCalibration per the mithril#1041 spec —
// every consumer continues to `using Mithril.MapCalibration;` regardless of
// physical assembly location.
namespace Mithril.MapCalibration;

/// <summary>
/// Composite identifier for a single Unity scene's calibration scope — the universal
/// calibration identity south of <see cref="Arda.World.Player.IMapState"/>.
///
/// <para><see cref="ParentAreaKey"/> is the areas.json key (always non-null in
/// practice — Arda surfaces it from <c>!!! Initializing area! </c>).
/// <see cref="SceneFriendlyName"/> is the sub-zone-level npcs.json
/// <c>AreaFriendlyName</c>; <c>null</c> for directly-registered areas, set for
/// aggregator-area sub-zones (e.g. for the Hogan's Keep basement scene under
/// <c>AreaCave1</c>, <c>SceneFriendlyName</c> is <c>"Hogan's Basement"</c>).
/// <see cref="MapAssetKey"/> is the literal Unity Texture2D name (e.g.
/// <c>"Map_HogansKeepBasement"</c>) — verbatim from the runtime-key bracket in
/// the Player.log <c>Downloading Map</c> line. This is the calibration store
/// key everywhere south of <see cref="Arda.World.Player.IMapState"/>:
/// <see cref="IMapCalibrationService"/>'s persistence is keyed on it.</para>
/// </summary>
/// <remarks>
/// Used by <c>Mithril.MapCalibration.Capture.IAreaReferenceProvider.ForArea</c>
/// to scope NPC lookups to the right sub-zone (consumer uses
/// <see cref="ParentAreaKey"/> + <see cref="SceneFriendlyName"/>; ignores
/// <see cref="MapAssetKey"/>). And by <see cref="IMapCalibrationService"/>'s
/// every public method as the typed lookup parameter
/// (mithril#1041 — promotes the type from projection identifier to universal
/// calibration identity).
/// </remarks>
public readonly record struct MapSceneRef(
    string ParentAreaKey,
    string? SceneFriendlyName,
    string MapAssetKey);
