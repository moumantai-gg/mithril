namespace Mithril.Tools.MapCalibrationWpf.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.MapCalibration;

/// <summary>
/// One committed reference: a landmark/NPC's world coordinate paired with the
/// texture-pixel the user clicked. Residual is filled in by the solver after
/// each Refs change (Task 9).
/// </summary>
public sealed partial class RefViewModel : ObservableObject
{
    public string Name { get; }
    public string Kind { get; }
    public WorldCoord World { get; }
    public TexturePixel TexturePixel { get; }

    [ObservableProperty]
    private double? _residualPx;

    public RefViewModel(string name, string kind, WorldCoord world, TexturePixel texturePixel)
    {
        Name = name;
        Kind = kind;
        World = world;
        TexturePixel = texturePixel;
    }
}
