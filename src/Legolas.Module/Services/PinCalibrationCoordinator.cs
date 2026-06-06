using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Arda.Contracts;
using Arda.World.Player;
using Arda.World.Player.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using Legolas.Domain;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging;

namespace Legolas.Services;

/// <summary>The two explicit phases of the guided in-flow (#460) calibration
/// walkthrough. They are explicit because a transparent overlay's click-through
/// is all-or-nothing: <see cref="Drop"/> passes right-clicks to the game (drop
/// pins), <see cref="Pair"/> captures left-clicks (pair them) — never both at
/// once. The phase is toggled by the user (wizard-panel button / hotkey), never
/// an automatic FSM edge.</summary>
public enum CalibrationPhase
{
    /// <summary>Overlay click-through ON. The user right-clicks the in-game map
    /// to place well-spread pins (or relies on ones already there). Legolas
    /// only observes the live count — it captures nothing.</summary>
    Drop,

    /// <summary>Overlay captures clicks. The wizard names one pin at a time;
    /// the user left-clicks that pin's game-rendered dot through the
    /// transparent overlay to pair it, and may select / drag / nudge a placed
    /// marker to correct it.</summary>
    Pair,
}

/// <summary>
/// A placed calibration marker — the overlay-pixel half of one
/// <c>(WorldCoord ↔ OverlayPixel)</c> solve pair, promoted from a bare
/// <c>OverlayPixel</c> to an observable VM so it can be selected, dragged and
/// nudged after placement (the dominant accuracy bottleneck is click
/// precision — see #443/#449). The marker's <see cref="Pixel"/> and its
/// <c>_pairs[<see cref="PairIndex"/>]</c> entry move in lockstep; the world
/// coordinate is tracker-supplied and never mutated.
///
/// <para>Kept appearance-free on purpose: this is the single mutable marker
/// model #478 reshapes to drive per-marker <c>LegolasPinStyle</c> styling, so
/// the interaction state (pixel / selection / pair back-ref) lives here and
/// styling drops in there without touching this contract.</para>
/// </summary>
public sealed partial class CalibrationMarker : ObservableObject
{
    public CalibrationMarker(OverlayPixel pixel, int pairIndex)
    {
        _pixel = pixel;
        PairIndex = pairIndex;
    }

    [ObservableProperty] private OverlayPixel _pixel;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Index into the coordinator's pair list. Stable: pairs only ever
    /// grow (until a full Clear/Arm), so no re-indexing is needed.</summary>
    public int PairIndex { get; }

    partial void OnPixelChanged(OverlayPixel value)
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
    }

    public double X => Pixel.X;
    public double Y => Pixel.Y;
}

/// <summary>
/// View-agnostic driver for the <b>guided two-phase correctable</b> cold-start
/// pin calibration done through the survey map overlay (#460 → #477 Part A).
/// Consumes Arda's <see cref="IMapPinState"/> — the authoritative, area-scoped
/// player pin set — and feeds click-paired <c>(WorldCoord ↔ OverlayPixel)</c>
/// placements to <see cref="IAreaCalibrationService.CalibrateCurrentArea"/>.
///
/// <para><b>The flow.</b> One model, two phases (see
/// <see cref="CalibrationPhase"/>). Entry starts in <see cref="CalibrationPhase.Pair"/>
/// when the area already has ≥3 usable pins (the common case — PG players
/// accumulate them), else <see cref="CalibrationPhase.Drop"/>. In Pair the
/// coordinator names <em>one pin at a time</em> by its in-game identity
/// (<see cref="SuggestedPin"/>, chosen for spread via a farthest-point
/// heuristic), the user clicks that pin's game-rendered dot, and advance is
/// <em>implicit</em> — pairing the next named pin is the advance. A pin can be
/// skipped (deferred) or overridden (pick any pin).</para>
///
/// <para><b>#454 label-agnostic preserved.</b> Identity (label/colour/shape) is
/// UX-only — it only helps the human decide which service-supplied world coord
/// they are clicking. The solve is purely <c>(WorldCoord ↔ OverlayPixel)</c>;
/// colour/shape/label never reach <c>LandmarkCalibrationSolver</c>. Correction
/// edits only the <em>pixel</em> half.</para>
///
/// <para><b>Non-persisting residual.</b> Once ≥3 pairs exist, each add / nudge
/// / drag re-runs the pure <c>LandmarkCalibrationSolver</c> in-process (no
/// persist, no <see cref="IAreaCalibrationService.Changed"/>) to surface a live
/// <see cref="PreviewResidual"/>. Only <see cref="Confirm"/> /
/// <see cref="ConfirmAnyway"/> call the persisting
/// <see cref="IAreaCalibrationService.CalibrateCurrentArea"/>.</para>
/// </summary>
public sealed partial class PinCalibrationCoordinator : ObservableObject, IDisposable
{
    private readonly IAreaCalibrationService _service;
    private readonly IMapPinState _pinState;
    private readonly LegolasSettings _settings;
    // #919: the "good residual" threshold moved from LegolasSettings to the
    // shared GameConfig so the map-calibration capture engine reads the same
    // value the manual walkthrough uses. Optional in the ctor (defaults to a
    // fresh GameConfig with the 12 px default) so existing test ctors compile
    // unchanged.
    private readonly Mithril.Shared.Game.GameConfig _gameConfig;
    private readonly SessionState? _session;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly IDisposable _addedSub;
    private readonly IDisposable _removedSub;

    // The accumulated solve pairs. Keyed by the MapPinEntry (for spread + identity);
    // only (WorldCoord, Pixel) ever reaches the solver.
    private readonly List<(MapPinEntry Pin, OverlayPixel Pixel)> _pairs = new();
    // Pins the user deferred ("skip") — excluded from the next suggestion until
    // everything else is paired (then recycled, so the user is never stuck).
    private readonly List<MapPinEntry> _skipped = new();

    // #524: SessionState carries the live in-game map zoom the user types into
    // the wizard / overlay slider. Optional so the unit-test ctor stays
    // unchanged (legacy callers stamp the calibration at the default 1.0,
    // matching the pre-#524 hardcoded value).
    //
    // loggerFactory is optional so existing test ctors compile unchanged; the
    // catch in Persist log-warns through it when present (#836 round-4 review).
    public PinCalibrationCoordinator(
        IAreaCalibrationService service, IMapPinState pinState, IDomainEventSubscriber bus,
        LegolasSettings settings, SessionState? session = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        Mithril.Shared.Game.GameConfig? gameConfig = null)
    {
        _service = service;
        _pinState = pinState;
        _settings = settings;
        _gameConfig = gameConfig ?? new Mithril.Shared.Game.GameConfig();
        _session = session;
        _logger = loggerFactory?.CreateLogger("Legolas.PinCalibrationCoordinator");
        // Seed ExistingPins from the current pin state; live add/remove
        // notifications arrive via the domain event bus.
        SyncExistingPins(_pinState.Pins);
        _addedSub = bus.Subscribe<MapPinAdded>(OnPinAdded);
        _removedSub = bus.Subscribe<MapPinRemoved>(OnPinRemoved);
        // #919: react to live edits of the shared threshold (now on GameConfig).
        _gameConfig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Mithril.Shared.Game.GameConfig.CalibrationGoodResidualPx))
            {
                OnPropertyChanged(nameof(IsResidualGood));
                OnPropertyChanged(nameof(ResidualText));
            }
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isArmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairing))]
    [NotifyPropertyChangedFor(nameof(IsDropping))]
    [NotifyPropertyChangedFor(nameof(PhaseToggleLabel))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private CalibrationPhase _phase;

    /// <summary>Overlay capture is desired (Pair phase + armed). The view
    /// routes left-clicks to <see cref="PairClick"/> / marker selection only
    /// while this holds; Drop phase wants click-through so right-clicks reach
    /// the game.</summary>
    public bool IsPairing => IsArmed && Phase == CalibrationPhase.Pair;

    /// <summary>Drop phase + armed — click-through ON, no pixel capture.</summary>
    public bool IsDropping => IsArmed && Phase == CalibrationPhase.Drop;

    partial void OnIsArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPairing));
        OnPropertyChanged(nameof(IsDropping));
    }

    /// <summary>The pin the user explicitly chose to pair next (override the
    /// spread suggestion). Cleared once paired or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestedPin))]
    [NotifyPropertyChangedFor(nameof(PromptText))]
    private MapPinEntry? _overridePin;

    /// <summary>Current-area pins by in-game identity — the override picker's
    /// source. Kept in sync with the Arda pin state.</summary>
    public ObservableCollection<MapPinEntry> ExistingPins { get; } = new();

    /// <summary>Markers the overlay renders, one per accumulated pair (parallel
    /// to <c>_pairs</c> by <see cref="CalibrationMarker.PairIndex"/>).</summary>
    public ObservableCollection<CalibrationMarker> PlacedMarkers { get; } = new();

    /// <summary>The currently-selected marker (for drag / nudge correction), or
    /// null. Defaults to the just-placed marker after a pair.</summary>
    [ObservableProperty] private CalibrationMarker? _selectedMarker;

    /// <summary>Live count of current-area pins available to pair against.</summary>
    public int PinsAvailable => _pinState.Pins.Count;

    /// <summary>≥3 pins exist ⇒ Pair phase is workable without dropping more.</summary>
    public bool HasUsablePins => PinsAvailable >= 3;

    /// <summary>Click-paired points accumulated so far.</summary>
    public int PairedCount => _pairs.Count;

    /// <summary>Hard floor: ≥3 pairs before any finalize.</summary>
    public bool CanConfirm => _pairs.Count >= 3;

    /// <summary>Non-persisting RMS residual of the current pairs (≥3), or null.
    /// Recomputed on every pair add / nudge / drag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResidualText))]
    [NotifyPropertyChangedFor(nameof(IsResidualGood))]
    private double? _previewResidual;

    /// <summary>Confirm is ungated once the preview residual is at or below the
    /// configured "good" threshold; otherwise the user must "finish anyway".</summary>
    public bool IsResidualGood =>
        PreviewResidual is { } r && r <= _gameConfig.CalibrationGoodResidualPx;

    public string ResidualText => PreviewResidual is { } r
        ? $"Fit residual: {r:0.0} px (target ≤ {_gameConfig.CalibrationGoodResidualPx:0} px)."
        : "Pair ≥3 pins to see the fit quality.";

    /// <summary>The next pin the user should pair: the explicit override if
    /// set, else the spread suggestion (farthest-point from already-paired,
    /// excluding skipped). Null when nothing is left to pair.</summary>
    public MapPinEntry? SuggestedPin
    {
        get
        {
            if (OverridePin is { } o && !IsPaired(o)) return o;
            return ComputeSuggestion();
        }
    }

    /// <summary>Label for the phase-toggle button.</summary>
    public string PhaseToggleLabel =>
        Phase == CalibrationPhase.Drop ? "Done dropping — pair them" : "Drop more pins";

    public string StatusText
    {
        get
        {
            if (!IsArmed) return "Pin calibration is off.";
            if (Phase == CalibrationPhase.Drop)
                return $"Right-click the in-game map to place ≥3 well-spread pins. " +
                       $"You have {PinsAvailable}. " +
                       (HasUsablePins ? "Enough — pair them when ready." : "Drop a few more.");
            return $"Paired {PairedCount}. " + PromptText;
        }
    }

    /// <summary>Per-phase guidance. In Pair, names the suggested pin by its
    /// in-game identity (UX-only — never reaches the solver).</summary>
    public string PromptText
    {
        get
        {
            if (Phase == CalibrationPhase.Drop)
                return "Drop ≥3 well-spread map pins in-game (works on a fogged/blank map).";
            if (SuggestedPin is { } p)
                return $"Click where this pin is: {p.Appearance()} — “{p.DisplayName()}”.";
            return PairedCount >= 3
                ? "All pins paired. Check the fit, then Confirm."
                : "No more pins to pair — drop more, or Confirm if you have ≥3.";
        }
    }

    /// <summary>Arm capture and clear stale state. Seeds
    /// <see cref="ExistingPins"/> and picks the entry phase: straight to
    /// <see cref="CalibrationPhase.Pair"/> when ≥3 usable pins already exist
    /// (the common case), else <see cref="CalibrationPhase.Drop"/>.
    ///
    /// <para>No seed-calibration gate. The #835 issue body's dissolved-#868
    /// decision is explicit: <em>"calibration placement pins are drawn by
    /// the scene hook ... no seed-calibration requirement."</em> Placement
    /// pins render pixel-native via
    /// <c>LegolasOverlaySceneDrawer.DrawCalibrationPlacementPins</c>, so
    /// the walkthrough works in both calibrated and uncalibrated areas.
    /// Earlier iter-1 work added a gate here that contradicted the
    /// dissolution; reverted in the iter-1 touch-up.</para></summary>
    public void Arm()
    {
        _pairs.Clear();
        _skipped.Clear();
        PlacedMarkers.Clear();
        SelectedMarker = null;
        OverridePin = null;
        PreviewResidual = null;
        PersistError = null;
        SyncExistingPins(_pinState.Pins);
        Phase = HasUsablePins ? CalibrationPhase.Pair : CalibrationPhase.Drop;
        IsArmed = true;
        RaiseAll();
    }

    /// <summary>Disarm and flush — leaving the calibration step, or after a
    /// confirm.</summary>
    public void Disarm()
    {
        _pairs.Clear();
        _skipped.Clear();
        PlacedMarkers.Clear();
        SelectedMarker = null;
        OverridePin = null;
        PreviewResidual = null;
        // Clear PersistError on Disarm so a failed Confirm's message doesn't
        // linger after the user backs out / area-changes without re-Arming
        // (round-4 review #3). Arm() also clears it, but Disarm is an
        // independently-reachable exit so it needs the same hygiene.
        PersistError = null;
        IsArmed = false;
        RaiseAll();
    }

    /// <summary>Flip Drop ⇄ Pair. The wizard-panel button (and optional
    /// hotkey) drives this — an explicit, user-driven toggle, never an
    /// automatic transition. Idempotent intent: re-reading <see cref="Phase"/>
    /// is the source of truth for the overlay's capture mode.</summary>
    public void TogglePhase()
    {
        if (!IsArmed) return;
        Phase = Phase == CalibrationPhase.Drop ? CalibrationPhase.Pair : CalibrationPhase.Drop;
        RaiseAll();
    }

    /// <summary>
    /// Pair an overlay click with the currently-named pin (override or spread
    /// suggestion) — the implicit advance: the next call pairs the next named
    /// pin. No-op when disarmed, not pairing, or nothing is left to pair.
    /// Degenerate duplicate world points are rejected so the solve stays
    /// well-conditioned. The just-placed marker becomes the selection so an
    /// immediate nudge corrects it.
    /// </summary>
    public void PairClick(OverlayPixel pixel)
    {
        if (!IsArmed || Phase != CalibrationPhase.Pair) return;
        if (SuggestedPin is not { } pin) return;

        // Reject a second click on the same world point — duplicates degrade
        // the least-squares solve.
        if (_pairs.Any(p => SameWorld(p.Pin, pin))) { OverridePin = null; RaiseAll(); return; }

        var index = _pairs.Count;
        _pairs.Add((pin, pixel));
        var marker = new CalibrationMarker(pixel, index);
        PlacedMarkers.Add(marker);
        SelectMarker(marker);
        OverridePin = null;
        // The pin is paired now; drop it from the skip list if it was deferred.
        _skipped.RemoveAll(s => SameWorld(s, pin));
        RecomputeResidual();
        RaiseAll();
    }

    /// <summary>Defer the suggested pin: it is excluded from the next
    /// suggestion (a different, still-spread pin is offered) until everything
    /// else is paired. No pair is recorded.</summary>
    public void SkipSuggestion()
    {
        if (!IsArmed || Phase != CalibrationPhase.Pair) return;
        if (SuggestedPin is not { } pin) return;
        OverridePin = null;
        if (!_skipped.Any(s => SameWorld(s, pin))) _skipped.Add(pin);
        RaiseAll();
    }

    /// <summary>Mouse-down hit-test: select the nearest marker within
    /// <paramref name="radius"/> px (so the view starts a drag) and return
    /// true; else false → the click falls through to <see cref="PairClick"/>.</summary>
    public bool TrySelectMarkerAt(OverlayPixel at, double radius)
    {
        CalibrationMarker? best = null;
        var bestD = radius;
        foreach (var m in PlacedMarkers)
        {
            var d = m.Pixel.DistanceTo(at);
            if (d <= bestD) { bestD = d; best = m; }
        }
        if (best is null) return false;
        SelectMarker(best);
        return true;
    }

    /// <summary>Drag the selected marker to an absolute pixel. Mutates only the
    /// pixel half of its pair; the world coord is untouched.</summary>
    public void DragSelectedTo(OverlayPixel at)
    {
        if (SelectedMarker is not { } m) return;
        m.Pixel = at;
        _pairs[m.PairIndex] = (_pairs[m.PairIndex].Pin, at);
        RecomputeResidual();
    }

    /// <summary>Arrow-key nudge of the selected (default: just-placed) marker
    /// by a pixel delta. Magnitude is resolved by the caller from
    /// <c>LegolasSettings.NudgeStep*</c>.</summary>
    public void NudgeSelected(double dx, double dy)
    {
        if (SelectedMarker is not { } m) return;
        var moved = new OverlayPixel(m.Pixel.X + dx, m.Pixel.Y + dy);
        m.Pixel = moved;
        _pairs[m.PairIndex] = (_pairs[m.PairIndex].Pin, moved);
        RecomputeResidual();
    }

    public void ClearSelection() => SelectMarker(null);

    /// <summary>Terminal confirm — solve + persist + apply, gated on ≥3 pairs
    /// AND a good residual. Returns the calibration or null if the gate fails /
    /// the solve failed. On success the area becomes calibrated
    /// (<see cref="IAreaCalibrationService.Changed"/> fires) and the
    /// coordinator disarms.</summary>
    public AreaCalibration? Confirm()
    {
        if (!CanConfirm || !IsResidualGood) return null;
        return Persist();
    }

    /// <summary>"Finish anyway" — persist with a high residual (still ≥3
    /// pairs). The non-affine ±10% map ceiling means a high residual is
    /// sometimes unavoidable; the user is never trapped.</summary>
    public AreaCalibration? ConfirmAnyway()
    {
        if (!CanConfirm) return null;
        return Persist();
    }

    /// <summary>
    /// User-visible error from the most recent <see cref="Confirm"/> /
    /// <see cref="ConfirmAnyway"/> attempt that failed to persist (typically
    /// a transient IOException &#8212; AV scan locking <c>refinements.json.tmp</c>,
    /// OneDrive placeholder hiccup, full disk). Null when the last attempt
    /// succeeded or no attempt has been made. Cleared on every new attempt
    /// and on <see cref="Arm"/>. The wizard surfaces this so the user can
    /// retry without the WPF command crashing into the unhandled-exception
    /// path (round-3 review #1).
    /// </summary>
    [ObservableProperty]
    private string? _persistError;

    private AreaCalibration? Persist()
    {
        var pairs = _pairs
            .Select(p => (new WorldCoord(p.Pin.X, 0, p.Pin.Z), p.Pixel))
            .ToList();
        // mithril#1095: CalibrationZoom removed from AreaCalibration — the live view
        // state is tracked by ILiveMapViewService. The calibrationZoom param is retained
        // on IAreaCalibrationService for API stability but no longer stamped on the record.
        PersistError = null;
        try
        {
            var result = _service.CalibrateCurrentArea(pairs);
            if (result is not null) Disarm();
            return result;
        }
        catch (System.IO.IOException ex)
        {
            // UserRefinementStore.Save propagates IOException with full rollback
            // (in-memory state restored). Surface the error to the wizard
            // without crashing the WPF command — the user can retry once the
            // lock clears. We deliberately stay armed so the placed pairs
            // aren't lost: the user fixes whatever held the file open (AV /
            // OneDrive) and hits Confirm again.
            //
            // Log at warning level so DiagnosticsLoggerProvider captures it
            // even before the XAML chip binding for PersistError lands (UI
            // surfacing tracked in #842). Without this log, a real disk
            // failure would be a fully silent no-op for the user
            // (round-4 review #1).
            _logger?.LogWarning(ex,
                "Calibration persist failed for scene {AssetKey}; PersistError set, coordinator stays armed.",
                _service.CurrentScene?.MapAssetKey ?? "(unknown)");
            PersistError = $"Couldn't save calibration: {ex.Message}. Retry once the file lock clears.";
            return null;
        }
    }

    private void SelectMarker(CalibrationMarker? m)
    {
        if (ReferenceEquals(SelectedMarker, m)) return;
        if (SelectedMarker is { } prev) prev.IsSelected = false;
        SelectedMarker = m;
        if (m is not null) m.IsSelected = true;
    }

    private MapPinEntry? ComputeSuggestion()
    {
        var candidates = _pinState.Pins
            .Where(p => !IsPaired(p) && !_skipped.Any(s => SameWorld(s, p)))
            .ToList();
        if (candidates.Count == 0)
        {
            // Everything left is skipped — recycle so the user is never stuck.
            candidates = _pinState.Pins.Where(p => !IsPaired(p)).ToList();
        }
        if (candidates.Count == 0) return null;
        if (_pairs.Count == 0) return candidates[0];

        // Farthest-point: maximise the minimum world-distance to already-paired
        // pins, so each new pair widens the geometric spread (a well-spread set
        // conditions the similarity solve far better than a clustered one).
        MapPinEntry? best = null;
        var bestMin = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var min = _pairs.Min(p => WorldDist(p.Pin, c));
            if (min > bestMin) { bestMin = min; best = c; }
        }
        return best;
    }

    private bool IsPaired(MapPinEntry p) => _pairs.Any(q => SameWorld(q.Pin, p));

    private static bool SameWorld(MapPinEntry a, MapPinEntry b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Z - b.Z) < 0.01;

    private static double WorldDist(MapPinEntry a, MapPinEntry b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private void RecomputeResidual()
    {
        if (_pairs.Count < 3) { PreviewResidual = null; return; }
        var refs = _pairs
            .Select(p => new LandmarkCalibrationSolver.Reference(p.Pin.X, p.Pin.Z, p.Pixel.X, p.Pixel.Y))
            .ToList();
        PreviewResidual = LandmarkCalibrationSolver.Solve(refs)?.ResidualPixels;
    }

    public void Dispose()
    {
        _addedSub.Dispose();
        _removedSub.Dispose();
    }

    private void OnPinAdded(MapPinAdded evt)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
            disp.Invoke(() => SyncExistingPins(_pinState.Pins));
        else
            SyncExistingPins(_pinState.Pins);
    }

    private void OnPinRemoved(MapPinRemoved evt)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is not null && !disp.CheckAccess())
            disp.Invoke(() => SyncExistingPins(_pinState.Pins));
        else
            SyncExistingPins(_pinState.Pins);
    }

    /// <summary>Rebuild <see cref="ExistingPins"/> from a snapshot, preserving
    /// the override selection by value when the same pin survives.</summary>
    private void SyncExistingPins(IReadOnlyCollection<MapPinEntry> pins)
    {
        var prevOverride = OverridePin;
        ExistingPins.Clear();
        foreach (var p in pins) ExistingPins.Add(p);
        OverridePin = prevOverride is null
            ? null
            : ExistingPins.FirstOrDefault(p => p == prevOverride);
        OnPropertyChanged(nameof(PinsAvailable));
        OnPropertyChanged(nameof(HasUsablePins));
        OnPropertyChanged(nameof(SuggestedPin));
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(PinsAvailable));
        OnPropertyChanged(nameof(HasUsablePins));
        OnPropertyChanged(nameof(PairedCount));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(SuggestedPin));
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResidualText));
        OnPropertyChanged(nameof(IsResidualGood));
        OnPropertyChanged(nameof(PhaseToggleLabel));
    }
}
