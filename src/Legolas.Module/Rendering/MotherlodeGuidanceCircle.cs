using System.Windows.Media;
using Legolas.Domain;

namespace Legolas.Rendering;

/// <summary>
/// #506: advisory tolerance ring + centre marker on the map overlay. World
/// geometry is approximate on the non-affine map — draw soft/dashed only.
/// </summary>
public sealed record MotherlodeGuidanceCircle(
    OverlayPixel Center,
    double RadiusPixels,
    Color StrokeColor);
