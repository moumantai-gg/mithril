using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Services;
using Arda.Contracts;
using Arda.World.Player.Events;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Shared.Diagnostics.Telemetry;
using Mithril.Shared.Reference;

namespace Legolas.ViewModels;

/// <summary>
/// Drives the standalone calibration overlay. Two pin families share one
/// select → nudge → Done state machine:
/// <list type="bullet">
/// <item><b>Reference placements</b> — landmark/NPC clicks that feed the solve.</item>
/// <item><b>Verify</b> — a single <see cref="PlayerPin"/> ("you") plus
/// <see cref="SurveyPins"/>: each survey/treasure vector becomes a pin that
/// stores BOTH its calibration-<see cref="SurveyPin.ProjectedPixel"/> and the
/// user-corrected <see cref="SurveyPin.OverlayPixel"/> (dragged onto the real
/// ping), so the projected-vs-actual delta is fully recoverable for the math.</item>
/// </list>
/// All decoupled from the survey FSM — nothing here touches SessionState.
/// </summary>
public sealed partial class CalibrationSessionViewModel : ObservableObject, IDisposable
{
    private readonly IAreaCalibrationService _service;
    private readonly IDisposable? _pinSub;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly IComposedOverlayCalibrationResolver? _composedResolver;   // mithril#1096

    public CalibrationSessionViewModel(
        IAreaCalibrationService service,
        IDomainEventSubscriber? bus = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        IComposedOverlayCalibrationResolver? composedResolver = null)   // mithril#1096
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        _service.SurveyObserved += OnSurveyObserved;
        _pinSub = bus?.Subscribe<MapPinAdded>(OnPinAdded);
        _logger = loggerFactory?.CreateLogger("Legolas.CalibrationSession");
        _composedResolver = composedResolver;
        Refresh();
    }

    public void Dispose()
    {
        _service.Changed -= OnServiceChanged;
        _service.SurveyObserved -= OnSurveyObserved;
        _pinSub?.Dispose();
    }

    public ObservableCollection<CalibrationReference> References { get; } = new();
    public ObservableCollection<PlacedReference> Placements { get; } = new();

    /// <summary>Projected positions of the *unplaced* landmarks per the solved
    /// calibration — a pure world→pixel sanity check (never feeds the solve).</summary>
    public ObservableCollection<GhostPin> GhostPins { get; } = new();

    /// <summary>Survey/treasure vectors as correctable pins (own list, behaves
    /// like a landmark pin). Each keeps projected + overlay coords + the player
    /// pixel it was projected from, for the math.</summary>
    public ObservableCollection<SurveyPin> SurveyPins { get; } = new();

    /// <summary>"You are here" — a first-class selectable/nudgeable pin (not a
    /// click-mode). Set via the panel button; moving it re-projects every
    /// survey pin's <see cref="SurveyPin.ProjectedPixel"/>.</summary>
    [ObservableProperty] private PlayerPin? _playerPin;

    [ObservableProperty] private SurveyPin? _selectedSurveyPin;

    // Next viewport click drops/moves the player pin (armed by the button).
    private bool _armPlayerClick;

    // #454 freehand pin calibration. ProcessMapPinAdd world coords queue up
    // ONLY while armed (the area-entry bulk replay arrives disarmed → dropped);
    // each subsequent overlay click pairs the OLDEST unpaired world point —
    // turn-order, never by the pin's label.
    private readonly Queue<WorldCoord> _pendingPins = new();
    private int _pinSeq;

    [ObservableProperty] private bool _pinCalibrationArmed;

    public int PendingPinCount => _pendingPins.Count;

    public string PinCalibrationStatus => PinCalibrationArmed
        ? $"Pin calibration ARMED — drop ≥3 well-spread map pins in-game, then " +
          $"click each on the overlay in the same order. {PendingPinCount} pending."
        : "Pin calibration off. Cold-start path: works on a fogged map (no " +
          "landmarks needed).";

    partial void OnPinCalibrationArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(PinCalibrationStatus));
        RaiseDebug();
    }

    public bool HasPlayerPin => PlayerPin is not null;
    public double PlayerPinX => PlayerPin?.X ?? 0;
    public double PlayerPinY => PlayerPin?.Y ?? 0;
    public bool IsPlayerSelected => PlayerPin?.IsSelected == true;

    partial void OnPlayerPinChanged(PlayerPin? oldValue, PlayerPin? newValue)
    {
        OnPropertyChanged(nameof(HasPlayerPin));
        OnPropertyChanged(nameof(PlayerPinX));
        OnPropertyChanged(nameof(PlayerPinY));
        OnPropertyChanged(nameof(IsPlayerSelected));
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    partial void OnSelectedSurveyPinChanged(SurveyPin? oldValue, SurveyPin? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            // Mutually exclusive across the families.
            SelectedPlacement = null;
            SelectedReference = null;
            ClearPlayerSelection();
        }
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    /// <summary>Every known area, for the manual picker (no live banner needed).</summary>
    public IReadOnlyList<AreaEntry> AvailableAreas => _service.AllAreas;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SolveCommand))]
    private CalibrationReference? _selectedReference;

    /// <summary>Manually-chosen area. Switching it drives the service.</summary>
    [ObservableProperty] private AreaEntry? _selectedArea;

    /// <summary>The reference placement the nudge acts on.</summary>
    [ObservableProperty] private PlacedReference? _selectedPlacement;

    public string? NudgeTargetText =>
        IsPlayerSelected
            ? "Nudging your position — arrow keys move it; survey pins re-project (Shift = 5px)"
            : SelectedSurveyPin is { } s
                ? $"Nudging survey “{s.Name}” onto the real ping — arrow keys (Shift = 5px)"
                : SelectedPlacement is { } p
                    ? $"Nudging “{p.Name}” — arrow keys move it (Shift = 5px)"
                    : null;

    public bool CanNudge =>
        IsPlayerSelected || SelectedSurveyPin is not null || SelectedPlacement is not null;

    /// <summary>Blunt always-visible state readout.</summary>
    public string DebugState =>
        $"area={_service.CurrentScene?.ParentAreaKey ?? "-"}  refs={References.Count}  " +
        $"sel={SelectedReference?.Name ?? "-"}  pins={Placements.Count}  " +
        $"last={(Placements.Count > 0 ? $"({Placements[^1].X:0},{Placements[^1].Y:0})" : "-")}  " +
        $"you={(PlayerPin is { } pp ? $"({pp.X:0},{pp.Y:0}){(pp.IsSelected ? "*" : "")}" : "-")}  " +
        $"surveys={SurveyPins.Count}  ghosts={GhostPins.Count}  " +
        $"mapZoom={MapZoom:0.###}";

    private void RaiseDebug()
    {
        OnPropertyChanged(nameof(DebugState));
        OnPropertyChanged(nameof(DiagnosticSnapshot));
    }

    /// <summary>Full multi-line diagnostic for the clipboard — dumps EVERY
    /// survey pin's projected-vs-corrected math + raw coords so the scale factor
    /// is readable without selecting/eyeballing each one.</summary>
    public string DiagnosticSnapshot
    {
        get
        {
            var c = _service.CurrentCalibration;
            var cal = c is null
                ? "calibration: none"
                : $"calibration: scale={c.Scale:0.0000} rotDeg={c.RotationRadians * 180.0 / Math.PI:0.00} " +
                  $"origin=({c.OriginX:0.0},{c.OriginY:0.0}) mirrorNorth={c.MirrorNorth} " +
                  $"refs={c.ReferenceCount} residual={c.ResidualPixels:0.0}px";

            var lines = new List<string>
            {
                "Legolas calibration diagnostic",
                DebugState,
                cal,
                $"player: {(PlayerPin is { } pp ? $"({pp.X:0.0},{pp.Y:0.0})" : "-")}",
                $"status: {StatusText}",
                $"lastSurvey: {LastSurveyText ?? "-"}",
                $"result: {ResultText ?? "-"}",
                $"nudgeTarget: {NudgeTargetText ?? "-"}",
                $"clickWarning: {ClickWarning ?? "-"}",
                $"surveyPins ({SurveyPins.Count}):",
            };
            if (SurveyPins.Count == 0)
                lines.Add("  (none)");
            else
                foreach (var s in SurveyPins)
                    lines.Add(
                        $"  {(ReferenceEquals(s, SelectedSurveyPin) ? "*" : " ")} " +
                        $"off=({s.Offset.East:0},{s.Offset.North:0})m  " +
                        $"origin=({s.OriginPixel.X:0.0},{s.OriginPixel.Y:0.0})  " +
                        $"proj=({s.ProjX:0.0},{s.ProjY:0.0})  " +
                        $"overlay=({s.OverlayX:0.0},{s.OverlayY:0.0}){(s.Corrected ? "" : " uncorrected")}  | {s.MathText}");
            return string.Join('\n', lines);
        }
    }

    [RelayCommand]
    private void CopyDebug()
    {
        try { System.Windows.Clipboard.SetText(DiagnosticSnapshot); }
        catch { /* clipboard can be transiently locked — best-effort */ }
    }

    partial void OnSelectedPlacementChanged(PlacedReference? oldValue, PlacedReference? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (!ReferenceEquals(SelectedReference, newValue.Reference))
                SelectedReference = newValue.Reference;
            SelectedSurveyPin = null;
            ClearPlayerSelection();
        }
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    partial void OnSelectedReferenceChanged(CalibrationReference? value)
    {
        var pin = value is null
            ? null
            : Placements.FirstOrDefault(p => ReferenceEquals(p.Reference, value));
        if (!ReferenceEquals(SelectedPlacement, pin))
            SelectedPlacement = pin;
        RaiseDebug();
    }

    [ObservableProperty] private string? _clickWarning;

    /// <summary>Always set whenever a survey/treasure reaches the window.</summary>
    [ObservableProperty] private string? _lastSurveyText;

    [ObservableProperty] private string _statusText = "No area detected yet.";
    [ObservableProperty] private string _instruction =
        "Pick an area, place ≥2 landmark/NPC references, Solve. Then Set player position and fire a survey to verify.";
    [ObservableProperty] private string? _resultText;

    /// <summary>The in-game map zoom the user reads off the game UI (Mithril
    /// can't see it). Stamped onto the calibration at Solve; survey projections
    /// scale by currentMapZoom / calibration.CalibrationZoom so a calibration
    /// stays correct when you change zoom. 1.0 default = no-op until set.</summary>
    [ObservableProperty] private double _mapZoom = 1.0;

    partial void OnMapZoomChanged(double value)
    {
        ReprojectSurveyPins(); // existing pins re-scale to the new zoom live
        RaiseDebug();
    }

    partial void OnSelectedAreaChanged(AreaEntry? value)
    {
        if (value is null || value.Key == _service.CurrentScene?.ParentAreaKey) return;
        _service.SelectScene(AreaCalibrationService.MapSceneRefForDirectlyRegisteredArea(value.Key));
    }

    /// <summary>Arrow-key fine-tune of whatever is selected: the player pin
    /// (re-projects surveys), a survey pin's overlay (drag onto the real ping),
    /// or a reference placement.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (PlayerPin is { IsSelected: true } pp)
        {
            pp.Pixel = new OverlayPixel(pp.Pixel.X + dx, pp.Pixel.Y + dy);
            OnPropertyChanged(nameof(PlayerPinX));
            OnPropertyChanged(nameof(PlayerPinY));
            ReprojectSurveyPins();
            RaiseDebug();
            return;
        }
        if (SelectedSurveyPin is { } s)
        {
            s.OverlayPixel = new OverlayPixel(s.OverlayPixel.X + dx, s.OverlayPixel.Y + dy);
            s.Corrected = true; // it's now the real-ping position, not the projection
            ClampSurvey(s);
            RaiseDebug();
            return;
        }
        if (SelectedPlacement is { } p)
            p.Pixel = new OverlayPixel(p.Pixel.X + dx, p.Pixel.Y + dy);
    }

    private static double Dist(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Mouse-down hit-test: if a pin (player / survey overlay /
    /// placement) is within <paramref name="radius"/> px of the click, select
    /// it (mutually exclusive) and return true so the view starts a drag.
    /// Otherwise false → the click falls through to place/arm. Priority:
    /// player, then nearest on-screen survey overlay, then nearest placement.</summary>
    public bool TrySelectPinAt(OverlayPixel at, double radius)
    {
        if (PlayerPin is { } pp && Dist(at.X, at.Y, pp.X, pp.Y) <= radius)
        {
            SelectPlayerInternal();
            return true;
        }

        SurveyPin? bestS = null;
        var bestSd = radius;
        foreach (var s in SurveyPins)
        {
            if (s.IsOffscreen) continue; // can't grab a clamped (not-true) pin
            var d = Dist(at.X, at.Y, s.OverlayX, s.OverlayY);
            if (d <= bestSd) { bestSd = d; bestS = s; }
        }
        if (bestS is not null) { SelectedSurveyPin = bestS; return true; }

        PlacedReference? bestP = null;
        var bestPd = radius;
        foreach (var p in Placements)
        {
            var d = Dist(at.X, at.Y, p.X, p.Y);
            if (d <= bestPd) { bestPd = d; bestP = p; }
        }
        if (bestP is not null) { SelectedPlacement = bestP; return true; }

        return false;
    }

    /// <summary>Drag: move the selected pin to the absolute pixel (not a delta).
    /// Same target rules as <see cref="NudgeSelected"/>.</summary>
    public void DragSelectedTo(OverlayPixel at)
    {
        if (PlayerPin is { IsSelected: true } pp)
        {
            pp.Pixel = at;
            OnPropertyChanged(nameof(PlayerPinX));
            OnPropertyChanged(nameof(PlayerPinY));
            ReprojectSurveyPins();
            RaiseDebug();
            return;
        }
        if (SelectedSurveyPin is { } s)
        {
            s.OverlayPixel = at;
            s.Corrected = true;
            ClampSurvey(s);
            RaiseDebug();
            return;
        }
        if (SelectedPlacement is { } p)
            p.Pixel = at;
    }

    public bool CanSolve => Placements.Count >= 2;

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(Refresh);
        else Refresh();
    }

    private void Refresh()
    {
        RefreshCore();
        RaiseDebug();
    }

    private void RefreshCore()
    {
        References.Clear();
        foreach (var r in _service.CurrentAreaReferences) References.Add(r);

        if (_service.CurrentScene is { } scene)
            SelectedArea = AvailableAreas.FirstOrDefault(a => a.Key == scene.ParentAreaKey);

        if (_service.CurrentAreaFriendlyName is not { } area)
        {
            StatusText = "No area detected — pick one from the dropdown, or walk into one in-game.";
            return;
        }
        if (_service.CurrentScene is null)
        {
            StatusText = $"{area} — unrecognised area (no reference data); calibration unavailable.";
            return;
        }
        StatusText = _service.CurrentCalibration is { } c
            ? $"{area} — calibrated ({c.ReferenceCount} refs, residual {c.ResidualPixels:0.0} px). " +
              "Set player position + fire a survey to verify; Recalibrate if off."
            : $"{area} — not calibrated. Place ≥2 references and Solve.";
    }

    [RelayCommand]
    private void PlaceSelectedAt(OverlayPixel pixel)
    {
        if (SelectedReference is not { } reference)
        {
            ClickWarning = References.Count == 0
                ? "No area selected — choose an area above first."
                : "Pick a landmark/NPC from the list, then click where it is on the map.";
            RaiseDebug();
            return;
        }

        for (var i = Placements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Placements[i].Reference, reference))
                Placements.RemoveAt(i);

        var placed = new PlacedReference(reference, pixel);
        Placements.Add(placed);
        SelectedPlacement = placed;
        SelectedReference = reference;
        ClickWarning = null;
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
        RaiseDebug();
    }

    /// <summary>Single map-click entry point. If the player-pin click is armed,
    /// the click drops/moves "you"; otherwise it places the selected reference.</summary>
    [RelayCommand]
    private void ViewportClicked(OverlayPixel pixel)
    {
        if (_armPlayerClick)
        {
            _armPlayerClick = false;
            if (PlayerPin is { } pp) pp.Pixel = pixel;
            else PlayerPin = new PlayerPin(pixel);
            SelectPlayerInternal();
            ReprojectSurveyPins();
            ClickWarning = _service.CurrentCalibration is null
                ? "Player set. Solve a calibration, then fire a survey to verify."
                : null;
            RaiseDebug();
            return;
        }
        if (PinCalibrationArmed && _pendingPins.Count > 0)
        {
            // Pair the OLDEST unpaired world point with this click (turn
            // order). A synthetic CalibrationReference flows through the exact
            // same Placements/Solve path as landmark calibration — the ONLY
            // delta is the world-coord source. The "Map pin N" name is the
            // turn index, display-only; the ProcessMapPinAdd label is never
            // consulted.
            var world = _pendingPins.Dequeue();
            var placed = new PlacedReference(
                new CalibrationReference($"Map pin {++_pinSeq}", "MapPin", world), pixel);
            Placements.Add(placed);
            SelectedPlacement = placed;
            ClickWarning = null;
            OnPropertyChanged(nameof(CanSolve));
            OnPropertyChanged(nameof(PendingPinCount));
            OnPropertyChanged(nameof(PinCalibrationStatus));
            SolveCommand.NotifyCanExecuteChanged();
            RaiseDebug();
            return;
        }
        PlaceSelectedAt(pixel);
    }

    /// <summary>Panel button: if no player pin yet, arm the next click to drop
    /// it; if it exists, select it as the nudge target (same as picking a
    /// landmark pin from a list).</summary>
    [RelayCommand]
    private void SetPlayerPosition()
    {
        if (PlayerPin is null)
        {
            _armPlayerClick = true;
            ClickWarning = "Click on the map where you are.";
        }
        else
        {
            SelectPlayerInternal();
        }
        RaiseDebug();
    }

    private void SelectPlayerInternal()
    {
        SelectedPlacement = null;
        SelectedReference = null;
        SelectedSurveyPin = null;
        if (PlayerPin is { } pp) pp.IsSelected = true;
        OnPropertyChanged(nameof(IsPlayerSelected));
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
    }

    private void ClearPlayerSelection()
    {
        if (PlayerPin is { IsSelected: true } pp) pp.IsSelected = false;
        OnPropertyChanged(nameof(IsPlayerSelected));
    }

    /// <summary>Commit &amp; stop: deselect everything so a stray click/arrow
    /// does nothing until something is picked again.</summary>
    [RelayCommand]
    private void Deselect()
    {
        SelectedReference = null;
        SelectedPlacement = null;
        SelectedSurveyPin = null;
        ClearPlayerSelection();
        ClickWarning = null;
        OnPropertyChanged(nameof(NudgeTargetText));
        OnPropertyChanged(nameof(CanNudge));
        RaiseDebug();
    }

    [RelayCommand]
    private void ClearSurveyPins()
    {
        SurveyPins.Clear();
        SelectedSurveyPin = null;
        RaiseDebug();
    }

    /// <summary>Ghost every *unplaced* landmark at its calibrated position
    /// (pure world→pixel; never feeds the solve).</summary>
    [RelayCommand]
    private void ProjectLandmarks()
    {
        GhostPins.Clear();

        // mithril#1096: resolve via the composer when wired (passes the wizard
        // canvas dims from _viewportW/_viewportH, populated by Viewport_SizeChanged
        // in CalibrationOverlayView). Fall back to direct-overlay-only on legacy
        // ctor paths so existing tests stay green.
        WorldToOverlayCalibration? c;
        string? missReason;
        if (_composedResolver is not null)
        {
            var r = _composedResolver.Resolve(_service.CurrentScene, _viewportW, _viewportH);
            c = r.Calibration;
            missReason = r.MissReason;
        }
        else
        {
            c = _service.CurrentOverlayCalibration;
            missReason = c is null ? "no_overlay_cal" : null;
        }

        if (c is null)
        {
            ClickWarning = "Solve a calibration first — nothing to project.";
            var skippedArea = _service.CurrentScene?.MapAssetKey ?? "<unknown>";
            _logger?.LogInformation(
                "ProjectLandmarks: refused — no overlay calibration (reason={Reason}); UI surfaced 'Solve a calibration first'.",
                missReason ?? "no_overlay_cal");
            MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
                new KeyValuePair<string, object?>("consumer", "wizard_landmarks"),
                new KeyValuePair<string, object?>("area", skippedArea));
            RaiseDebug();
            return;
        }
        var skipped = 0;
        foreach (var r in References)
        {
            if (Placements.Any(p => ReferenceEquals(p.Reference, r))) { skipped++; continue; }
            // Canonical absolute world→pixel — shared with the #454
            // ProcessMapFx placement path so the two can't drift.
            // #1076 Phase 6.5: frame-typed projection — OverlayPixel out, no re-tag.
            GhostPins.Add(new GhostPin(r.Name, c.Value.ToOverlay(r.World)));
        }
        _logger?.LogInformation(
            "ProjectLandmarks: projected {Count} ghost pins from {Refs} refs ({Skipped} already placed).",
            GhostPins.Count, References.Count, skipped);
        ClickWarning = null;
        RaiseDebug();
    }

    [RelayCommand]
    private void ClearGhosts()
    {
        GhostPins.Clear();
        RaiseDebug();
    }

    private void OnSurveyObserved(object? sender, CalibrationSurveyObservation obs)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(() => AddSurvey(obs));
        else AddSurvey(obs);
    }

    private void AddSurvey(CalibrationSurveyObservation obs)
    {
        var seen = $"Last survey: {obs.Name} ({obs.Offset.East:0}E, {obs.Offset.North:0}N)";
        if (_service.CurrentCalibration is not { } c)
        {
            LastSurveyText = seen + " — solve a calibration first.";
            RaiseDebug();
            return;
        }
        if (PlayerPin is not { } pp)
        {
            LastSurveyText = seen + " — Set player position first.";
            RaiseDebug();
            return;
        }

        // Dedup: the same node surveyed again from the same spot (or a
        // double-parsed line) repeats the exact vector. Treat same name +
        // offset within ~2 m as the same pin and skip — don't pile duplicates
        // (and don't clobber a correction already made on it).
        const double dedupMetres = 2.0;
        if (SurveyPins.Any(s =>
                string.Equals(s.Name, obs.Name, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(s.Offset.East - obs.Offset.East) <= dedupMetres &&
                Math.Abs(s.Offset.North - obs.Offset.North) <= dedupMetres))
        {
            LastSurveyText = seen + " → duplicate vector, ignored (already in Surveys).";
            RaiseDebug();
            return;
        }

        var projected = ProjectSurvey(c, pp.Pixel, obs.Offset);
        var sp = new SurveyPin(obs.Name, obs.Offset, pp.Pixel, projected);
        ClampSurvey(sp);
        SurveyPins.Add(sp);
        LastSurveyText = seen + " → added to Surveys; select it and drag it onto the real ping.";
        ClickWarning = null;
        RaiseDebug();
    }

    /// <summary>Arm/disarm freehand pin calibration. Arming clears any stale
    /// pending pins so the area-entry replay can't leak in; disarming clears
    /// the queue too.</summary>
    [RelayCommand]
    private void TogglePinCalibration()
    {
        PinCalibrationArmed = !PinCalibrationArmed;
        _pendingPins.Clear();
        if (PinCalibrationArmed)
        {
            _pinSeq = 0;
            Instruction =
                "Pin calibration: drop ≥3 (ideally 4) well-spread map pins in-game " +
                "(works on a fogged/blank map — no landmarks needed), then click each " +
                "on the overlay in the SAME order. Solve when ≥3 are placed.";
            ClickWarning = null;
        }
        OnPropertyChanged(nameof(PendingPinCount));
        OnPropertyChanged(nameof(PinCalibrationStatus));
        RaiseDebug();
    }

    private void OnPinAdded(MapPinAdded evt)
    {
        var world = new WorldCoord(evt.X, 0, evt.Z);
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess()) disp.Invoke(() => EnqueuePin(world));
        else EnqueuePin(world);
    }

    private void EnqueuePin(WorldCoord world)
    {
        // Disarmed → ignore (the bulk area-entry replay is already
        // service-deduped; this gate scopes to the active calibration).
        // Label is never read — pairing is the user's next overlay click.
        if (!PinCalibrationArmed) return;
        _pendingPins.Enqueue(world);
        OnPropertyChanged(nameof(PendingPinCount));
        OnPropertyChanged(nameof(PinCalibrationStatus));
        RaiseDebug();
    }

    // Viewport size, pushed from the view, so a far overlay pin clamps to the
    // edge instead of vanishing.
    private double _viewportW, _viewportH;

    public void SetViewport(double width, double height)
    {
        _viewportW = width;
        _viewportH = height;
        foreach (var s in SurveyPins) ClampSurvey(s);
    }

    private void ClampSurvey(SurveyPin s)
    {
        const double inset = 14;
        var px = s.OverlayPixel;
        if (_viewportW <= 0 || _viewportH <= 0)
        {
            s.DisplayX = px.X;
            s.DisplayY = px.Y;
            s.IsOffscreen = false;
            return;
        }
        s.IsOffscreen = px.X < 0 || px.X > _viewportW || px.Y < 0 || px.Y > _viewportH;
        s.DisplayX = Math.Clamp(px.X, inset, _viewportW - inset);
        s.DisplayY = Math.Clamp(px.Y, inset, _viewportH - inset);
    }

    /// <summary>Survey projection from the (current-zoom) player pixel: only
    /// <see cref="AreaCalibration.Scale"/> needs the zoom factor because the
    /// origin is the player pin the user placed at the CURRENT zoom (no stored
    /// origin to also rescale) — so this is exact across zooms.</summary>
    private OverlayPixel ProjectSurvey(AreaCalibration c, OverlayPixel origin, MetreOffset off)
        => ProjectAtScale(c, origin, off, c.Scale * ZoomFactor(c));

    /// <summary>mithril#1095: CalibrationZoom removed from AreaCalibration;
    /// the wizard now operates in overlay-frame at a constant scale (1.0 factor).
    /// Retained as a method stub so ProjectSurvey compiles without changes.</summary>
    public static double ZoomFactor(AreaCalibration c) => 1.0;

    private static OverlayPixel ProjectAtScale(AreaCalibration c, OverlayPixel origin, MetreOffset off, double scale)
    {
        var cos = Math.Cos(c.RotationRadians);
        var sin = Math.Sin(c.RotationRadians);
        var rotE = off.East * cos + off.North * sin;
        var rotN = -off.East * sin + off.North * cos;
        return new OverlayPixel(origin.X + scale * rotE, origin.Y - scale * rotN);
    }

    /// <summary>Re-project every survey pin from the (moved) player pin. The
    /// overlay follows the projection until the user corrects it; once dragged
    /// (<see cref="SurveyPin.Corrected"/>) the overlay sticks — it's the real
    /// ping, fixed on the map regardless of where the player marker is.</summary>
    private void ReprojectSurveyPins()
    {
        if (_service.CurrentCalibration is not { } c || PlayerPin is not { } pp) return;
        foreach (var s in SurveyPins)
        {
            s.OriginPixel = pp.Pixel;
            s.ProjectedPixel = ProjectSurvey(c, pp.Pixel, s.Offset);
            if (!s.Corrected) s.OverlayPixel = s.ProjectedPixel;
            ClampSurvey(s);
        }
    }

    [RelayCommand]
    private void RemoveLastPlacement()
    {
        if (Placements.Count == 0) return;
        var removed = Placements[^1];
        Placements.RemoveAt(Placements.Count - 1);
        if (ReferenceEquals(SelectedPlacement, removed)) SelectedPlacement = null;
        OnPropertyChanged(nameof(CanSolve));
        SolveCommand.NotifyCanExecuteChanged();
        RaiseDebug();
    }

    [RelayCommand]
    private void ClearPlacements()
    {
        Placements.Clear();
        SelectedPlacement = null;
        // Pin calibration shares Placements — clear its queue + disarm so a
        // half-done pin session can't bleed into the next (also covers
        // Recalibrate, which routes through here).
        _pendingPins.Clear();
        PinCalibrationArmed = false;
        OnPropertyChanged(nameof(CanSolve));
        OnPropertyChanged(nameof(PendingPinCount));
        OnPropertyChanged(nameof(PinCalibrationStatus));
        SolveCommand.NotifyCanExecuteChanged();
        ResultText = null;
        RaiseDebug();
    }

    [RelayCommand(CanExecute = nameof(CanSolve))]
    private void Solve()
    {
        var pairs = Placements.Select(p => (p.Reference.World, p.Pixel)).ToList();
        AreaCalibration? calibration;
        try
        {
            calibration = _service.CalibrateCurrentArea(pairs, MapZoom);
        }
        catch (System.IO.IOException ex)
        {
            // UserRefinementStore.Persist propagates IOException so the user
            // sees disk failures instead of the in-memory state silently
            // diverging from the file. The store rolls back its in-memory
            // dictionary on throw, so retrying after the user clears whatever
            // held the lock (AV scan, OneDrive sync) works. Logging at warning
            // so DiagnosticsLoggerProvider captures it for later diagnosis
            // (round-4 review #1).
            _logger?.LogWarning(ex,
                "Calibration solve persist failed for area {AreaKey}; ResultText surfaces the error.",
                _service.CurrentScene?.ParentAreaKey ?? "(unknown)");
            ResultText = $"Couldn't save calibration: {ex.Message}. Check your disk / sync tooling and Solve again.";
            return;
        }

        if (calibration is null)
        {
            ResultText = "Couldn't solve — need ≥2 references at meaningfully different spots.";
            return;
        }

        ResultText = calibration.ResidualPixels <= 12
            ? $"Calibrated: {calibration.ResidualPixels:0.0} px residual, scale {calibration.Scale:0.000} px/m. " +
              "Now Set player position and fire a survey to verify."
            : $"Solved but residual is high ({calibration.ResidualPixels:0.0} px) — references were likely " +
              "placed imprecisely or the map was at a different zoom. Clear and redo for a tighter fit.";
        Instruction = "Set player position (button), then fire a survey/treasure — it joins the Surveys " +
            "list; select it and drag it onto the real ping. Projected vs corrected = the scale check.";
        ReprojectSurveyPins();
        Refresh();
    }

    [RelayCommand]
    private void Recalibrate()
    {
        try
        {
            _service.ClearCurrentAreaCalibration();
        }
        catch (System.IO.IOException ex)
        {
            // UserRefinementStore.Remove propagates IOException with full
            // rollback. Surface the failure (same contract as Solve) so the
            // WPF command doesn't crash. The in-memory state is restored, so
            // the prior calibration still applies until the user retries.
            // Log at warning so DiagnosticsLoggerProvider captures it
            // (round-4 review #1).
            _logger?.LogWarning(ex,
                "Calibration clear persist failed for area {AreaKey}; ResultText surfaces the error.",
                _service.CurrentScene?.ParentAreaKey ?? "(unknown)");
            ResultText = $"Couldn't clear calibration: {ex.Message}. Retry once the file lock clears.";
            return;
        }
        ClearPlacements();
        Refresh();
    }
}

/// <summary>A projected unplaced-landmark "ghost" — pure world→pixel sanity
/// marker (minimal, static).</summary>
public sealed class GhostPin
{
    public GhostPin(string name, OverlayPixel pixel)
    {
        Name = name;
        Pixel = pixel;
    }

    public string Name { get; }
    public OverlayPixel Pixel { get; }
    public double X => Pixel.X;
    public double Y => Pixel.Y;
}

/// <summary>The "you are here" pin — selectable/nudgeable like a placement.</summary>
public sealed partial class PlayerPin : ObservableObject
{
    public PlayerPin(OverlayPixel pixel) => _pixel = pixel;

    [ObservableProperty] private OverlayPixel _pixel;
    [ObservableProperty] private bool _isSelected;

    partial void OnPixelChanged(OverlayPixel value)
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    public double X => Pixel.X;
    public double Y => Pixel.Y;
}

/// <summary>A survey/treasure vector as a correctable pin. Stores the player
/// pixel it was projected from (<see cref="OriginPixel"/>), the calibration's
/// <see cref="ProjectedPixel"/>, and the user-corrected <see cref="OverlayPixel"/>
/// (dragged onto the real ping). The projected↔overlay delta vs. the metre
/// offset is the empirical scale check.</summary>
public sealed partial class SurveyPin : ObservableObject
{
    public SurveyPin(string name, MetreOffset offset, OverlayPixel origin, OverlayPixel projected)
    {
        Name = name;
        Offset = offset;
        _originPixel = origin;
        _projectedPixel = projected;
        _overlayPixel = projected; // starts on the projection; user drags to truth
    }

    public string Name { get; }
    public MetreOffset Offset { get; }

    [ObservableProperty] private OverlayPixel _originPixel;
    [ObservableProperty] private OverlayPixel _projectedPixel;
    [ObservableProperty] private OverlayPixel _overlayPixel;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True once the user has dragged the overlay — it then sticks
    /// (the real ping is fixed on the map) instead of following re-projection.</summary>
    [ObservableProperty] private bool _corrected;

    // Clamped render position for the overlay marker (edge-visible).
    [ObservableProperty] private double _displayX;
    [ObservableProperty] private double _displayY;
    [ObservableProperty] private bool _isOffscreen;

    partial void OnProjectedPixelChanged(OverlayPixel value)
    {
        OnPropertyChanged(nameof(ProjX));
        OnPropertyChanged(nameof(ProjY));
        OnPropertyChanged(nameof(MathText));
    }

    partial void OnOverlayPixelChanged(OverlayPixel value)
    {
        OnPropertyChanged(nameof(OverlayX));
        OnPropertyChanged(nameof(OverlayY));
        OnPropertyChanged(nameof(MathText));
    }

    public double ProjX => ProjectedPixel.X;
    public double ProjY => ProjectedPixel.Y;
    public double OverlayX => OverlayPixel.X;
    public double OverlayY => OverlayPixel.Y;

    /// <summary>The math: metre distance, projected px distance, corrected px
    /// distance (all from the player pixel), and the implied scale factor —
    /// i.e. what px/m the corrected position actually needs vs. what the
    /// calibration used.</summary>
    public string MathText
    {
        get
        {
            var metres = Math.Sqrt(Offset.East * Offset.East + Offset.North * Offset.North);
            var projDist = Dist(OriginPixel, ProjectedPixel);
            var overDist = Dist(OriginPixel, OverlayPixel);
            var impliedScale = metres > 1e-6 ? overDist / metres : 0;
            var ratio = projDist > 1e-6 ? overDist / projDist : 0;
            return $"{Name}: {metres:0}m  proj={projDist:0}px  corrected={overDist:0}px  " +
                   $"ratio={ratio:0.000}  impliedScale={impliedScale:0.0000}px/m" +
                   (Corrected ? "" : "  (not yet corrected)");
        }
    }

    private static double Dist(OverlayPixel a, OverlayPixel b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed partial class PlacedReference : ObservableObject
{
    public PlacedReference(CalibrationReference reference, OverlayPixel pixel)
    {
        Reference = reference;
        _pixel = pixel;
    }

    public CalibrationReference Reference { get; }

    [ObservableProperty] private OverlayPixel _pixel;
    [ObservableProperty] private bool _isSelected;

    partial void OnPixelChanged(OverlayPixel value)
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    public string Name => Reference.Name;
    public string Kind => Reference.Kind;
    public double X => Pixel.X;
    public double Y => Pixel.Y;
}
