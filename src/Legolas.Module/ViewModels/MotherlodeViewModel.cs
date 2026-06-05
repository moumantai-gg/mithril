using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Arda.World.Player;

namespace Legolas.ViewModels;

/// <summary>
/// Motherlode mode (#488): a thin, read-only projection of
/// <see cref="MotherlodeMeasurementCoordinator"/>. All input is log-driven —
/// the ChatLog distance line, the Player.log use gesture, and position feeders
/// — so this VM no longer takes manual distance/position entry. It rebuilds
/// its slot list on each coordinator change and owns only route optimization
/// (calibration-free world-space ordering) and reset.
///
/// <para>#113 Layer 1: each solved slot is phrased relative to the nearest
/// recognizable reference — the player's own measured spots
/// (<see cref="MotherlodeStatus.Locations"/>), their current-area map pins
/// (<see cref="IMapPinState"/>), and the area landmark/NPC gazetteer
/// (<see cref="IAreaCalibrationService.CurrentAreaReferences"/>) — because raw
/// engine-unit coordinates are unactionable in a game with no coordinate
/// readout. Both reference feeders are optional: absent (or area-mismatched)
/// they simply drop out of the ranking and the phrase degrades gracefully.</para>
/// </summary>
public sealed partial class MotherlodeViewModel : ObservableObject, IDisposable
{
    private readonly MotherlodeMeasurementCoordinator _coordinator;
    private readonly IRouteOptimizer _optimizer;
    private readonly MotherlodeFlowController _flow;
    private readonly IMapPinState? _pinState;
    private readonly IAreaCalibrationService? _areaCalibration;
    private readonly LegolasSettings? _settings;

    private static readonly IReadOnlyCollection<MapPinEntry> NoPins = Array.Empty<MapPinEntry>();
    private static readonly IReadOnlyList<CalibrationReference> NoReferences = Array.Empty<CalibrationReference>();

    public MotherlodeViewModel(
        MotherlodeMeasurementCoordinator coordinator,
        IRouteOptimizer optimizer,
        MotherlodeFlowController flow,
        IMapPinState? pinState = null,
        IAreaCalibrationService? areaCalibration = null,
        LegolasSettings? settings = null)
    {
        _coordinator = coordinator;
        _optimizer = optimizer;
        _flow = flow;
        _pinState = pinState;
        _areaCalibration = areaCalibration;
        _settings = settings;
        _coordinator.Changed += OnCoordinatorChanged;
        if (_settings is not null)
            _settings.PropertyChanged += OnSettingsChanged;
        Rebuild();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LegolasSettings.MotherlodeMultiMapMode))
        {
            OnPropertyChanged(nameof(MultiMapMode));
            RecomputeContractText();
        }
    }

    /// <summary>
    /// #488 Multi-map mode. Two-way, setting-backed (not a snapshot
    /// projection) — this is an operational control the player flips mid-run,
    /// deliberately surfaced on the Motherlode panel rather than the Legolas
    /// settings tab (it belongs where the action is; the value still persists
    /// via <see cref="LegolasSettings.MotherlodeMultiMapMode"/>). Default true
    /// when no settings instance is available (tests).
    /// </summary>
    public bool MultiMapMode
    {
        get => _settings?.MotherlodeMultiMapMode ?? true;
        set
        {
            if (_settings is null || _settings.MotherlodeMultiMapMode == value) return;
            _settings.MotherlodeMultiMapMode = value;   // OnSettingsChanged raises the notification
        }
    }

    public MotherlodeFlowController Flow => _flow;

    public ObservableCollection<MotherlodeSlotViewModel> Slots { get; } = new();

    [ObservableProperty] private int _locationCount;
    [ObservableProperty] private int _locationsWithFix;
    [ObservableProperty] private string? _guidance;

    /// <summary>#506: committed measurement spots for the active treasure (not
    /// per-map distance bindings). Guided overlay gates on this (≥1), not on
    /// the three-spot solve minimum.</summary>
    [ObservableProperty] private int _measurementSpotCount;

    /// <summary>#506: relative phrase for the guided next spot when the area is
    /// uncalibrated (or as wizard copy when calibrated).</summary>
    [ObservableProperty] private string? _guidedSpotPhrase;

    /// <summary>Per-spot bound-reading tally, parallel to the measured spots —
    /// the passive multi-map shape surface ("Spot 1: 5 · Spot 2: 4").</summary>
    [ObservableProperty] private IReadOnlyList<int> _readsPerLocation = Array.Empty<int>();

    /// <summary>Session summary: motherlode maps dug (treasures found). Loot is
    /// a documented data ceiling (dig spawns a node — yield decoupled), so the
    /// summary is this + elapsed time only.</summary>
    [ObservableProperty] private int _mapsDug;

    /// <summary>Preformatted per-spot tally ("Spot 1: 5 · Spot 2: 4"), or null
    /// when nothing has been read yet.</summary>
    [ObservableProperty] private string? _readsSummary;

    /// <summary>Multi-map contract hint ("expecting N maps per spot"), or null
    /// when single-map / serial / inventory unknown.</summary>
    [ObservableProperty] private string? _contractHint;

    private void RecomputeContractText()
    {
        var reads = ReadsPerLocation;
        ReadsSummary = reads.Count == 0 || reads.All(r => r == 0)
            ? null
            : string.Join("  ·  ", reads.Select((r, i) => $"Spot {i + 1}: {r}"));
        // Working-set based (never inventory-held): hint only once a multi-map
        // batch is actually in progress (≥2 active slots).
        ContractHint = MultiMapMode && Slots.Count > 1
            ? "Multi-map mode — read your maps in the same order at every spot"
            : null;
    }

    /// <summary>#113 Layer 4: the derived progress stage the wizard projects
    /// onto its Motherlode sub-steps. Recomputed every <see cref="Rebuild"/>;
    /// the FSM stays coarse — this is purely a snapshot projection.</summary>
    [ObservableProperty] private MotherlodeStage _stage;

    /// <summary>True when there is a bound reading the player can undo (the
    /// "oops" button is enabled). Multi-level.</summary>
    [ObservableProperty] private bool _canUndo;

    /// <summary>Treasures with a confident fix so far — the headline number.</summary>
    public int SolvedCount => Slots.Count(s => s.HasFix);

    private void OnCoordinatorChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Rebuild();
        else d.InvokeAsync(Rebuild);
    }

    private void Rebuild()
    {
        var snap = _coordinator.Snapshot();
        var pins = _pinState?.Pins ?? NoPins;
        var gazetteer = _areaCalibration?.CurrentAreaReferences ?? NoReferences;

        // "Next up" = the lowest route-ordered uncollected slot (fallback: list
        // order). Surfacing it lets the panel highlight where to walk next.
        var nextUpId = snap.Surveys
            .Where(s => !s.Collected && s.SolvedWorld is not null)
            .OrderBy(s => s.RouteOrder ?? int.MaxValue)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefault();

        // Active-only projection: a retired (dug) slot drops out immediately —
        // the list is the working set, never a completed graveyard, so cost is
        // O(active) regardless of how many were farmed this session.
        Slots.Clear();
        foreach (var s in snap.Surveys)
        {
            if (s.Collected) continue;
            var bearing = s.SolvedWorld is { } w
                ? MotherlodeReferenceLocator.Nearest(w, snap.Locations, pins, gazetteer)
                : null;
            Slots.Add(new MotherlodeSlotViewModel(s, bearing, isNextUp: s.Id == nextUpId));
        }

        LocationCount = snap.LocationCount;
        LocationsWithFix = snap.LocationsWithFix;
        Guidance = snap.Guidance;
        MeasurementSpotCount = snap.MeasurementSpotCount;
        GuidedSpotPhrase = snap.NextSpot?.RelativePhrase is { } phrase
            ? $"Next spot: {phrase}"
            : snap.NextSpot is not null
                ? "Next spot: stand in the dashed ring on the map"
                : null;
        ReadsPerLocation = snap.ReadsPerLocation;
        MapsDug = snap.MapsDug;
        CanUndo = snap.CanUndo;
        RecomputeContractText();
        OnPropertyChanged(nameof(SolvedCount));

        Stage =
            MapsDug > 0 && Slots.Count == 0 ? MotherlodeStage.Done
            : Slots.Any(s => s.HasFix) ? MotherlodeStage.Walk
            : LocationCount > 0 || Slots.Count > 0 ? MotherlodeStage.Locating
            : MotherlodeStage.Measuring;
    }

    [RelayCommand]
    private void OptimizeRoute()
    {
        var snap = _coordinator.Snapshot();
        var ids = new List<Guid>();
        var points = new List<OverlayPixel>();   // world (X,Z); routing is similarity-invariant
        foreach (var s in snap.Surveys)
        {
            if (s.Collected || s.SolvedWorld is not { } w) continue;
            ids.Add(s.Id);
            points.Add(new OverlayPixel(w.X, w.Z));
        }
        if (points.Count == 0) return;

        var start = snap.LastPlayerWorld is { } p
            ? new OverlayPixel(p.X, p.Z)
            : points[0];
        var order = _optimizer.Optimize(start, points);
        _coordinator.ApplyRouteOrder(order.Select(i => ids[i]).ToList());
    }

    /// <summary>"Oops, I accidentally checked a map" — reverse the last bound
    /// reading (multi-level). Cheap correction vs. nuking the batch with
    /// Reset.</summary>
    [RelayCommand]
    private void UndoLastReading() => _coordinator.UndoLastReading();

    [RelayCommand]
    private void Reset() => _coordinator.Reset();

    public void Dispose()
    {
        _coordinator.Changed -= OnCoordinatorChanged;
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsChanged;
    }
}

/// <summary>Derived Motherlode progress stage (#113 Layer 4). Projected onto
/// the wizard's Motherlode sub-steps; not an FSM state.</summary>
public enum MotherlodeStage { Measuring, Locating, Walk, Done }

/// <summary>Plain-language confidence tier for a solved slot (#113 Layer 2).
/// Mapped 1:1 from the solver's own <see cref="MultilaterationQuality"/> so it
/// can never drift from the GDOP gate; drives both the badge text and the
/// XAML colour trigger.</summary>
public enum MotherlodeFixQuality { None, Strong, Usable, Rough }

/// <summary>Read-only per-treasure row for the wizard panel.</summary>
public sealed class MotherlodeSlotViewModel
{
    public MotherlodeSlotViewModel(MotherlodeSurvey m, MotherlodeBearing? bearing, bool isNextUp)
    {
        Id = m.Id;
        Collected = m.Collected;
        IsNextUp = isNextUp && !m.Collected;
        RouteOrder = m.RouteOrder;
        RouteText = m.RouteOrder is { } r ? $"{r + 1}." : "•";
        DistanceCount = m.DistancesByLocation.Count(d => d > 0);
        HasFix = m.SolvedWorld is not null;
        MapName = m.MapName;

        // Headline: the actionable relative phrase when solved; otherwise the
        // progress toward the 3-reading minimum. Prefixed with the inventory
        // map name when known (#488) so juggled multi-map slots are legible.
        var core = HasFix
            ? bearing?.ToDisplayString() ?? "located — no nearby reference"
            : DistanceCount < 3
                ? $"locating… ({DistanceCount}/3 spots)"
                : "locating…";
        HeadlineText = string.IsNullOrWhiteSpace(MapName) ? core : $"{MapName} — {core}";

        Quality = m.Quality switch
        {
            MultilaterationQuality.Solved => MotherlodeFixQuality.Strong,
            MultilaterationQuality.LowConfidenceGeometry => MotherlodeFixQuality.Usable,
            MultilaterationQuality.Insufficient or MultilaterationQuality.NoSolution
                => HasFix ? MotherlodeFixQuality.Rough : MotherlodeFixQuality.None,
            _ => MotherlodeFixQuality.None,
        };
        QualityText = Quality switch
        {
            MotherlodeFixQuality.Strong => "Strong fix",
            MotherlodeFixQuality.Usable => "Usable — spread out more",
            MotherlodeFixQuality.Rough => "Rough — add a reading",
            _ => null,
        };

        // Raw numbers demoted to a tooltip (kept for power users / debugging).
        DetailText = m.SolvedWorld is { } w
            ? $"({w.X:0}, {w.Z:0})"
                + (m.Gdop is { } g ? $"  ·  GDOP {g:0.0}" : "")
                + (m.ResidualRms is { } rr ? $"  ·  ±{rr:0.0} m fit" : "")
            : null;
    }

    public Guid Id { get; }
    public bool Collected { get; }
    public bool IsNextUp { get; }
    public int? RouteOrder { get; }
    public string RouteText { get; }
    public int DistanceCount { get; }
    public bool HasFix { get; }
    public string? MapName { get; }
    public string HeadlineText { get; }
    public MotherlodeFixQuality Quality { get; }
    public string? QualityText { get; }
    public string? DetailText { get; }
}
