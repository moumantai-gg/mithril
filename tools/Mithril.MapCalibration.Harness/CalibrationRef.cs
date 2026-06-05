using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;

namespace Mithril.Tools.MapCalibration.Harness;

/// <summary>
/// An accepted candidate in the live ref set, now editable. <see cref="World"/>
/// and <see cref="TexturePixel"/> are observable (re-assign / nudge);
/// <see cref="Enabled"/> toggles a ref in/out of the solve without deleting it
/// (disable a suspect ref, watch the residual). <see cref="ResidualPx"/> is
/// filled by the session after each solve.
/// </summary>
public sealed partial class CalibrationRef : ObservableObject
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public CalibrationRefSource Source { get; init; }

    public double Confidence { get; init; }

    [ObservableProperty]
    private WorldCoord _world;

    [ObservableProperty]
    private TexturePixel _texturePixel;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private double? _residualPx;
}
