namespace Mithril.MapCalibration;

/// <summary>
/// A known landmark/NPC reference for the area being calibrated: its
/// <see cref="World"/> position (area-local engine units) tagged with the
/// landmarks.json / npcs.json <see cref="Type"/> discriminator the solver pairs
/// detections against. Lifted from the tool's <c>LandmarkRef</c>.
/// </summary>
public sealed record LandmarkReference(string Type, string Name, WorldCoord World);
