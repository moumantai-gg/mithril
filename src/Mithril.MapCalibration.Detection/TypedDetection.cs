namespace Mithril.MapCalibration.Detection;

/// <summary>
/// A typed icon detection: the landmark <see cref="LandmarkType"/> the matched
/// <see cref="IconName"/> template implies, the pivot-corrected world-anchor in
/// <b>cropped-frame pixel</b> space (<see cref="Anchor"/>), and the NCC match
/// <see cref="Score"/>. The detector emits these grouped by landmark type;
/// the solver pairs each against same-type <see cref="LandmarkReference"/>s.
///
/// <para>The anchor's frame (<see cref="CroppedFramePixel"/>) reflects that the
/// detector consumes the cropped screenshot the locator carves out of the
/// captured frame — the origin is the crop's top-left, NOT the captured
/// frame's. Use <see cref="MapRect.CroppedToTexture"/> to map an anchor into
/// the canonical base-texture frame for cross-source comparison (mithril#1076).</para>
/// </summary>
public sealed record TypedDetection(
    string LandmarkType,
    string IconName,
    CroppedFramePixel Anchor,
    double Score);
