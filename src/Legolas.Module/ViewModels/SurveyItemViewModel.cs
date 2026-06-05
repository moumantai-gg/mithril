using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

public sealed partial class SurveyItemViewModel : ObservableObject
{
    [ObservableProperty]
    private Survey _model;

    /// <summary>
    /// Bearing-uncertainty wedge, rendered in Motherlode mode only. Raw inputs
    /// the D2D renderer constructs the arc from each frame; null when no wedge
    /// should render (Survey mode, collected, etc.). Replaces the WPF
    /// <c>Geometry?</c> the legacy ItemsControl bound to.
    /// </summary>
    [ObservableProperty]
    private WedgeArc? _wedgeArc;

    [ObservableProperty]
    private bool _isActiveTarget;

    /// <summary>
    /// True iff this pin is the <c>SessionState.SelectedSurvey</c> — the
    /// pin a keyboard nudge will move. Drives the active-pin halo treatment
    /// while the FSM is in <c>Listening</c>. Distinct from
    /// <see cref="IsActiveTarget"/>, which marks the next pin to visit
    /// during <c>Gathering</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public SurveyItemViewModel(Survey model)
    {
        _model = model;
    }

    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public MetreOffset Offset => Model.Offset;
    public int GridIndex => Model.GridIndex;
    public bool Collected => Model.Collected;
    public bool Skipped => Model.Skipped;
    public int? RouteOrder => Model.RouteOrder;
    public OverlayPixel? EffectivePixel => Model.EffectivePixel;
    public bool IsCorrected => Model.IsCorrected;

    public double X => EffectivePixel?.X ?? 0;
    public double Y => EffectivePixel?.Y ?? 0;
    public bool HasPixel => EffectivePixel.HasValue;
    public bool IsVisible => HasPixel && !Collected;

    public void UpdateModel(Survey model) => Model = model;

    partial void OnModelChanged(Survey value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Offset));
        OnPropertyChanged(nameof(GridIndex));
        OnPropertyChanged(nameof(Collected));
        OnPropertyChanged(nameof(Skipped));
        OnPropertyChanged(nameof(RouteOrder));
        OnPropertyChanged(nameof(EffectivePixel));
        OnPropertyChanged(nameof(IsCorrected));
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(HasPixel));
        OnPropertyChanged(nameof(IsVisible));
    }
}
