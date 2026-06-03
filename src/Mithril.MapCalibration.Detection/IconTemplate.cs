namespace Mithril.MapCalibration.Detection;

/// <summary>
/// A single pre-decoded landmark/NPC icon template: the grayscale luma + alpha
/// mask the NCC matcher consumes, plus the Unity pivot (so the world-anchor
/// pixel can be recovered from a centre match). PG's real landmark sprites are
/// pivot (0.5, 0.5) — centered — so for the shipped four the anchor IS the
/// centre; the pivot correction is kept general (it's data-driven from the
/// manifest) in case a future sprite is authored off-centre. <see cref="LandmarkType"/>
/// is the landmarks.json / npcs.json Type discriminator the solver pairs
/// detections against.
/// </summary>
public sealed record IconTemplate(
    string Name,
    string LandmarkType,
    double PivotX,
    double PivotY,
    GrayImage Gray,
    GrayImage Alpha);
