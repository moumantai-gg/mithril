using System.Collections.Generic;
using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// One frame's worth of inputs for the D2D pin renderer. Built fresh per
/// tick from the live <c>MapOverlayViewModel</c> by the surface's
/// <c>Render</c> handler; consumed by <see cref="PinSceneRenderer"/>.
///
/// Step C: routes + wedges. Step D: survey pins (no active treatment).
/// Active treatments + player anchor land in steps E and F and add fields
/// here. <see cref="MotherlodePins"/> (#113 Layer 5) is the
/// calibration-projected solved-treasure markers — a separate list from
/// <see cref="SurveyPins"/> because the two modes are mutually exclusive and
/// the Motherlode marker takes none of the Survey active-pin treatment.
/// </summary>
public sealed record PinScene(
    IReadOnlyList<OverlayPixel> RoutePoints,
    IReadOnlyList<OverlayPixel> ActiveSegmentPoints,
    IReadOnlyList<WedgeArc> Wedges,
    IReadOnlyList<OverlayPixel> SurveyPins,
    IReadOnlyList<OverlayPixel> MotherlodePins,
    MotherlodeGuidanceCircle? MotherlodeGuidance,
    int? ActivePinIndex,
    ActivePinTreatmentSpec? ActiveTreatment,
    PinLayerStyle SurveyOuter,
    PinLayerStyle SurveyCenter,
    double SurveyOuterDiameter,
    OverlayPixel? PlayerPosition,
    PinLayerStyle PlayerOuter,
    PinLayerStyle PlayerCenter,
    Color RouteLineColor,
    Color WedgeFillColor,
    Color WedgeStrokeColor,
    double ActiveSegmentDashOffset);
