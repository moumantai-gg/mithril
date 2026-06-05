namespace Legolas.Domain;

/// <summary>
/// Bearing-uncertainty arc rendered in Motherlode mode (gated by
/// <c>ShowBearingWedges</c> + <c>SessionMode.Motherlode</c> in
/// <c>MapOverlayViewModel.RebuildWedgeFor</c>). Drawn as a wedge from the
/// player anchor, sweeping a small arc around the projected bearing to
/// communicate "the node is somewhere in this direction at roughly this
/// distance". Survey mode does not draw it (#454 made survey placement
/// absolute — the retired projector-refit narrative this comment used to
/// reference no longer applies).
///
/// Stored on each <see cref="Legolas.ViewModels.SurveyItemViewModel"/>
/// as the raw inputs the renderer needs, not as a pre-built WPF
/// <c>PathGeometry</c> — the D2D pin layer constructs the arc each frame
/// (or cached, see <c>PinSceneRenderer</c>) so the VM stays
/// rendering-library-agnostic.
/// </summary>
/// <param name="Origin">Pixel position of the player anchor — the wedge's apex.</param>
/// <param name="BearingRadians">Centre bearing in screen coordinates, measured
/// clockwise from screen-up (matches the rest of the projection convention).</param>
/// <param name="HalfAngleRadians">Half the wedge's sweep. The full arc is
/// <c>BearingRadians ± HalfAngleRadians</c>.</param>
/// <param name="DistancePx">Radius of the arc, in pixels. Equal to the
/// projected distance from anchor to the survey's offset.</param>
public readonly record struct WedgeArc(
    OverlayPixel Origin,
    double BearingRadians,
    double HalfAngleRadians,
    double DistancePx);
