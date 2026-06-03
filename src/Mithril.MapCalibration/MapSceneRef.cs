namespace Mithril.MapCalibration;

/// <summary>
/// Composite identifier for a single Unity scene's calibration scope.
/// <see cref="ParentAreaKey"/> is the areas.json key (always non-null in
/// practice — Arda surfaces it from <c>!!! Initializing area! </c>).
/// <see cref="SceneFriendlyName"/> is the sub-zone-level npcs.json
/// <c>AreaFriendlyName</c>; <c>null</c> for directly-registered areas,
/// set for aggregator-area sub-zones (e.g. for the Hogan's Keep basement
/// scene under <c>AreaCave1</c>, <c>SceneFriendlyName</c> is
/// <c>"Hogan's Basement"</c>).
/// </summary>
/// <remarks>
/// Used by <c>Mithril.MapCalibration.Capture.IAreaReferenceProvider.ForArea</c> to scope
/// NPC lookups to the right sub-zone. Landmarks.json has no sub-zone field,
/// so the landmark filter uses <see cref="ParentAreaKey"/> alone — partial
/// coverage for aggregator scenes is documented in the spec (mithril#1021).
/// </remarks>
public readonly record struct MapSceneRef(
    string ParentAreaKey,
    string? SceneFriendlyName);
