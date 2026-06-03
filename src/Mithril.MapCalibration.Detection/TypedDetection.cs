namespace Mithril.MapCalibration.Detection;

/// <summary>
/// A typed icon detection: the landmark <see cref="LandmarkType"/> the matched
/// <see cref="IconName"/> template implies, the pivot-corrected world-anchor in
/// <b>screenshot-pixel</b> space (<see cref="AnchorX"/>/<see cref="AnchorY"/>),
/// and the NCC match <see cref="Score"/>. The detector emits these grouped by
/// landmark type; the solver pairs each against same-type
/// <see cref="LandmarkReference"/>s.
/// </summary>
public sealed record TypedDetection(
    string LandmarkType,
    string IconName,
    double AnchorX,
    double AnchorY,
    double Score);
