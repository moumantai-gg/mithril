using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Arda.World.Player.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;

namespace Legolas.ViewModels;

/// <summary>
/// Shared, session-scoped state referenced by both overlays and the control panel.
/// Everything here dies with the session (new Set Position wipes it).
/// </summary>
public sealed partial class SessionState : ObservableObject
{
    public ObservableCollection<SurveyItemViewModel> Surveys { get; } = new();

    /// <summary>
    /// Items the player has actually collected during this session, keyed by item
    /// display name (parser-canonical, case-insensitive). Surveys map 1:1 to a
    /// surveyed node, but a node can yield multiple items — the chat parser is the
    /// only place we see real quantities, so the ingestion service accumulates
    /// here. Cleared by <see cref="ClearSurveys"/> alongside the survey list.
    /// </summary>
    public Dictionary<string, int> CollectedItems { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Wall-clock instant the current session started — stamped when the first
    /// pin of a cycle lands while the FSM is <c>Listening</c> (#454: there is
    /// no anchor bootstrap any more). Null when no session has been started.
    /// Cleared by <see cref="ClearSurveys"/>.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    public SessionState()
    {
        Surveys.CollectionChanged += OnSurveysChanged;
    }

    private void OnSurveysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (SurveyItemViewModel s in e.NewItems)
                s.PropertyChanged += OnSurveyPropertyChanged;
        if (e.OldItems is not null)
            foreach (SurveyItemViewModel s in e.OldItems)
                s.PropertyChanged -= OnSurveyPropertyChanged;
        RecalculateActiveTarget();
    }

    private void OnSurveyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SurveyItemViewModel.Collected)
            or nameof(SurveyItemViewModel.Skipped)
            or nameof(SurveyItemViewModel.RouteOrder))
            RecalculateActiveTarget();
    }

    public event Action? AllCollected;

    public void RecalculateActiveTarget()
    {
        SurveyItemViewModel? next = Surveys
            .Where(s => !s.Collected && !s.Skipped)
            .OrderBy(s => s.RouteOrder ?? int.MaxValue)
            .FirstOrDefault();
        foreach (var s in Surveys)
            s.IsActiveTarget = ReferenceEquals(s, next);

        UpdateActiveTargetSummary(next);

        // Fire only on the transition: non-empty collection where nothing is
        // uncollected. After a reset (Surveys becomes empty) this doesn't re-fire.
        if (next is null && Surveys.Count > 0)
            AllCollected?.Invoke();
    }

    /// <summary>
    /// One-line "Next: #6 of 12 — Pin Name" surfaced in the wizard panel so
    /// players can see route progress without opening the map overlay.
    /// Empty when there's no active target (idle, all collected, or no route
    /// has been computed yet).
    /// </summary>
    [ObservableProperty]
    private string _activeTargetSummary = string.Empty;

    private void UpdateActiveTargetSummary(SurveyItemViewModel? active)
    {
        if (active is null || !active.RouteOrder.HasValue)
        {
            ActiveTargetSummary = string.Empty;
            return;
        }
        var total = Surveys.Count(s => s.RouteOrder.HasValue);
        var oneBased = active.RouteOrder!.Value + 1;
        ActiveTargetSummary = $"Next: #{oneBased} of {total} — {active.Name}";
    }

    // #454: Survey placement is absolute (no anchor). PlayerPosition /
    // HasPlayerPosition survive but are now Motherlode-only — its
    // triangulation records the player position from a map click. Survey mode
    // never reads them; IsAnchorEditable (the Survey "drag the anchor" gate)
    // is retired.
    [ObservableProperty] private OverlayPixel _playerPosition = new(400, 300);
    [ObservableProperty] private bool _hasPlayerPosition;

    // #476: Survey's player-position GPS. Distinct from the Motherlode-only
    // PlayerPosition above so the two modes can't cross-contaminate (Motherlode
    // writes a manual click; Survey writes a projected world coord). This is
    // the Arda IPositionState world coordinate projected through the current
    // area's calibration — set by MapOverlayViewModel, null when no tracker fix
    // or no calibrated area. Sparse by nature (zone-in / teleport only): goes
    // stale as the player walks the route, which is why MeasuredAt/Source ride
    // alongside it and are surfaced honestly rather than drawn as if live.
    //
    // SurveyPlayerIsManual (#476 Option C): true when the pixel came from the
    // user's "set my position" map click (the stale-anchor override), not the
    // tracker projection. A manual pixel is a raw screen coordinate (no
    // Source; MeasuredAt = when the user clicked), survives calibration
    // re-applies, and is superseded by the next *fresh* tracker fix
    // (zone-in / teleport) — a new fix is authoritative again.
    [ObservableProperty] private OverlayPixel? _surveyPlayerPixel;
    [ObservableProperty] private DateTimeOffset? _surveyPlayerMeasuredAt;
    [ObservableProperty] private PositionSource? _surveyPlayerSource;
    [ObservableProperty] private bool _surveyPlayerIsManual;

    // #497: the manual anchor is sourced from a character-named (or "@me")
    // map pin's exact world coord rather than a pixel click. Implies
    // SurveyPlayerIsManual=true. Distinguished so PlayerAnchorStatus can say
    // "pinned" and so the pin-sourced manual ends with the pin (a genuine
    // pixel-click manual — IsManual && !IsPinned — keeps its #476 stickiness).
    [ObservableProperty] private bool _surveyPlayerIsPinned;

    [ObservableProperty] private string _lastLogEvent = "(waiting)";
    [ObservableProperty] private bool _showBearingWedges = true;
    [ObservableProperty] private bool _showRouteLines = true;
    [ObservableProperty] private SessionMode _mode = SessionMode.Survey;
    [ObservableProperty] private double _mapOpacity = 1.0;
    [ObservableProperty] private double _inventoryOpacity = 1.0;
    [ObservableProperty] private bool _isMapVisible;
    [ObservableProperty] private bool _isInventoryVisible;
    [ObservableProperty] private bool _isCalibrationVisible;

    [ObservableProperty]
    private SurveyItemViewModel? _selectedSurvey;

    /// <summary>
    /// Mirror the SelectedSurvey identity onto each pin's IsSelected flag so
    /// the DataTemplate can drive its halo treatment without dotted-path
    /// MultiBindings against a sibling property. Mirrors the
    /// <see cref="RecalculateActiveTarget"/> pattern.
    /// </summary>
    partial void OnSelectedSurveyChanged(SurveyItemViewModel? value)
    {
        foreach (var s in Surveys)
            s.IsSelected = ReferenceEquals(s, value);
    }

    public void ClearSurveys()
    {
        Surveys.Clear();
        CollectedItems.Clear();
        StartedAt = null;
    }
}

public enum SessionMode
{
    Survey,
    Motherlode
}
