namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using System.Windows.Media;
using Mithril.MapCalibration;

/// <summary>
/// One dot on the projection overlay: every known landmark/NPC's world coord
/// projected through the current <see cref="AreaCalibration"/>. Colour-keyed
/// on per-ref residual when this marker corresponds to a committed ref, else
/// neutral grey.
/// </summary>
public sealed record ProjectionMarkerViewModel(string Name, TexturePixel TexturePixel, double? ResidualPx)
{
    public Brush MarkerBrush => ResidualPx switch
    {
        null => Brushes.Gray,
        <= SolverReadoutViewModel.GoodResidualPx => Brushes.LimeGreen,
        <= SolverReadoutViewModel.AcceptableResidualPx => Brushes.Goldenrod,
        _ => Brushes.IndianRed,
    };
}
