using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Sharing;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;

namespace Legolas.ViewModels;

/// <summary>
/// Step the wizard is currently rendering. Combines a synthetic <see cref="PickMode"/>
/// gate (step 0) with the active <see cref="SurveyFlowState"/>, and four
/// derived Motherlode sub-steps (#113 Layer 4). The Motherlode steps are not
/// FSM states — <see cref="Flow.MotherlodeFlowController"/> stays coarse; they
/// are projected from <see cref="MotherlodeViewModel.Stage"/>, itself derived
/// from the log-driven coordinator snapshot.
/// </summary>
public enum WizardStep
{
    PickMode,
    Calibrating,
    Listening,
    Gathering,
    Done,
    /// <summary>Motherlode: no readings yet — prompt to click maps at ≥3 spots.</summary>
    MotherlodeMeasuring,
    /// <summary>Motherlode: readings in, nothing solved yet — keep going / spread out.</summary>
    MotherlodeLocating,
    /// <summary>Motherlode: ≥1 treasure located — the relative-guidance route card.</summary>
    MotherlodeWalk,
    /// <summary>Motherlode: every located treasure collected.</summary>
    MotherlodeDone,
}

/// <summary>
/// View-model for the Survey/Motherlode wizard. Owns the synthetic mode-pick
/// gate, then projects active flow controllers' state onto a single
/// <see cref="CurrentStep"/> property the view templates against.
/// </summary>
public sealed partial class LegolasWizardViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly SurveyFlowController _surveyFlow;
    private readonly MotherlodeFlowController _motherlodeFlow;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly LegolasSettings _settings;
    private readonly LegolasReportService? _reportService;
    private readonly LegolasShareCardRenderer? _renderer;
    private readonly IActiveCharacterService? _activeChar;
    private readonly IReferenceDataService? _refData;
    private readonly IDialogService? _dialogs;

    public LegolasWizardViewModel(
        SessionState session,
        SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow,
        ControlPanelViewModel controlPanel,
        MotherlodeViewModel motherlode,
        MapOverlayViewModel mapOverlay,
        NudgePadViewModel nudgePad,
        IAreaCalibrationService areaCalibration,
        PinCalibrationCoordinator pinCalibration,
        LegolasSettings settings,
        LegolasReportService? reportService = null,
        LegolasShareCardRenderer? renderer = null,
        IActiveCharacterService? activeChar = null,
        IReferenceDataService? refData = null,
        IDialogService? dialogs = null)
    {
        _session = session;
        _surveyFlow = surveyFlow;
        _motherlodeFlow = motherlodeFlow;
        _settings = settings;
        _reportService = reportService;
        _renderer = renderer;
        _activeChar = activeChar;
        _refData = refData;
        _dialogs = dialogs;
        _areaCalibration = areaCalibration;
        PinCalibration = pinCalibration;
        ControlPanel = controlPanel;
        Motherlode = motherlode;
        MapOverlay = mapOverlay;
        NudgePad = nudgePad;

        _surveyFlow.PropertyChanged += OnSurveyFlowChanged;
        _surveyFlow.Transitioned += OnSurveyFlowTransitioned;
        _session.Surveys.CollectionChanged += OnSurveysChangedForOverlays;
        _motherlodeFlow.PropertyChanged += OnMotherlodeFlowChanged;
        Motherlode.PropertyChanged += OnMotherlodeViewModelChanged;
        _session.PropertyChanged += OnSessionChanged;
        // #460: once the area becomes calibrated (Confirm persisted it), leave
        // the Calibrating gate. #477B: a clear/(re)calibrate also flips
        // CanRecalibrate and must reset the confirm guard so a stale "are you
        // sure?" can't carry across areas.
        _areaCalibration.Changed += (_, _) =>
        {
            IsConfirmingRecalibrate = false;
            OnPropertyChanged(nameof(CanRecalibrate));
            OnPropertyChanged(nameof(IsAreaCalibrated));
            // #113 header chip: area and/or calibration state just changed.
            OnPropertyChanged(nameof(CurrentAreaName));
            OnPropertyChanged(nameof(IsAreaKnown));
            OnPropertyChanged(nameof(CalibrationChipText));
            OnPropertyChanged(nameof(CanCalibrateThisArea));
            // #495: losing/gaining a calibration flips the header validate
            // button's enablement (it needs something to validate).
            OnPropertyChanged(nameof(CanValidateCalibration));
            // #113: once this area is calibrated the Motherlode dot can place;
            // drop the one-shot request so RecomputeStep returns to the
            // log-driven Motherlode stage instead of re-entering Calibrating.
            if (_areaCalibration.IsCurrentAreaCalibrated)
            {
                _motherlodeCalibrationRequested = false;
                _calibrationRequested = false;   // #502: one-shot consumed
                _recalibrating = false;          // #501: new fit persisted
            }
            RecomputeStep();
        };
        if (_reportService is not null)
            _reportService.ReportGenerated += OnReportGenerated;
        RecomputeStep();
    }

    /// <summary>
    /// FSM-edge-driven overlay management (#454 collapsed FSM). A completed
    /// run is <c>… → Done</c>: hide both overlays so the game window is
    /// uncluttered between cycles. Re-showing happens on the next cycle's
    /// first pin (see <see cref="OnSurveysChangedForOverlays"/>) rather than
    /// on a state edge — the old Ready→Listening "next survey" edge is gone,
    /// and the auto-reset Done→Listening would otherwise re-show during the
    /// empty post-reset window. Gated on
    /// <see cref="LegolasSettings.HideOverlaysBetweenSessions"/>; a manual
    /// mid-session reset (which doesn't enter Done) doesn't hide — the test
    /// "Reset preserves overlay visibility" pins that.
    /// </summary>
    private void OnSurveyFlowTransitioned(SurveyTransition t)
    {
        if (!_settings.HideOverlaysBetweenSessions) return;

        if (t.To == SurveyFlowState.Done)
        {
            _session.IsMapVisible = false;
            _session.IsInventoryVisible = false;
        }
    }

    /// <summary>
    /// Re-show both overlays when the next cycle's first pin lands (count
    /// 0→1) — the collapsed-FSM replacement for the old Ready→Listening
    /// "next survey" re-show edge. Gated on the same opt-out so users who
    /// keep overlays always-visible are unaffected.
    /// </summary>
    private void OnSurveysChangedForOverlays(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_settings.HideOverlaysBetweenSessions) return;
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && _session.Surveys.Count == 1)
        {
            _session.IsMapVisible = true;
            _session.IsInventoryVisible = true;
        }
    }

    /// <summary>
    /// True when at least one overlay is currently visible. Drives the wizard
    /// hero row's overlay-toggle button: a click flips both to the opposite
    /// state, mirroring the <c>ToggleAllOverlaysCommand</c> hotkey shape.
    /// </summary>
    public bool AreOverlaysVisible => _session.IsMapVisible || _session.IsInventoryVisible;

    /// <summary>
    /// Wizard-hero overlay toggle. Sets both <see cref="SessionState.IsMapVisible"/>
    /// and <see cref="SessionState.IsInventoryVisible"/> to the same target value
    /// (the opposite of <see cref="AreOverlaysVisible"/>) so the two overlays move
    /// in lockstep, matching the hotkey-driven <c>ToggleAllOverlaysCommand</c>.
    /// </summary>
    [RelayCommand]
    private void ToggleOverlays()
    {
        var target = !AreOverlaysVisible;
        _session.IsMapVisible = target;
        _session.IsInventoryVisible = target;
    }

    /// <summary>
    /// Opens/closes the standalone map-calibration overlay (same flag the
    /// unbound <c>ToggleCalibrationOverlayCommand</c> hotkey flips). This is the
    /// discoverable entry point — the feature is otherwise reachable only via a
    /// user-assigned hotkey.
    /// </summary>
    [RelayCommand]
    private void ToggleCalibration() =>
        _session.IsCalibrationVisible = !_session.IsCalibrationVisible;

    /// <summary>True once the user has clicked Survey or Motherlode in step 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(CanCalibrateThisArea))]
    private bool _hasPickedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    [NotifyPropertyChangedFor(nameof(CanValidateCalibration))]
    private WizardStep _currentStep = WizardStep.PickMode;

    /// <summary>#495: "Validate calibration" is a between-runs diagnostic — it
    /// scatters reference markers across the live map, which would clutter the
    /// working overlay mid-flow. Available only when the area is calibrated
    /// (something to validate) and the user is not actively surveying or
    /// plotting motherlodes. <see cref="CurrentStep"/> is the right signal: it
    /// is <see cref="WizardStep.PickMode"/> before a mode is chosen, unlike the
    /// raw survey FSM which defaults to <c>Listening</c>.</summary>
    public bool CanValidateCalibration =>
        IsAreaCalibrated
        && CurrentStep is not (WizardStep.Listening
                            or WizardStep.Gathering
                            or WizardStep.MotherlodeMeasuring
                            or WizardStep.MotherlodeLocating
                            or WizardStep.MotherlodeWalk);

    partial void OnCurrentStepChanged(WizardStep value)
    {
        // #460: the Calibrating step arms pin-capture on the map overlay
        // (which it opens); any other step disarms (flushes pending/pairs).
        // #501: a recalibration shows the inline confirm gate first — don't
        // pop the overlay / arm until the user acknowledges (cold-start has no
        // gate, so it begins immediately). ConfirmRecalibrate then calls
        // BeginGuidedCalibration directly (no step change to re-trigger this).
        if (value == WizardStep.Calibrating)
        {
            if (!IsConfirmingRecalibrate)
                BeginGuidedCalibration();
        }
        else if (PinCalibration.IsArmed)
        {
            PinCalibration.Disarm();
        }

        // #454: no AwaitingPosition step. Entering Listening auto-opens the
        // map (so absolute pins are visible by default) and the inventory
        // (the user is picking which survey to use). Gathering keeps the
        // inventory open as a walk-the-route checklist.
        if (value is WizardStep.Listening or WizardStep.Gathering)
        {
            _session.IsInventoryVisible = true;
            if (value == WizardStep.Listening)
                _session.IsMapVisible = true;
        }

        // #113 Layer 5: once a treasure is located, the map overlay carries
        // the calibration-gated marker — open it so the dot is visible.
        if (value == WizardStep.MotherlodeWalk)
            _session.IsMapVisible = true;

        // #495: validation is a between-runs diagnostic. Entering a
        // surveying / motherlode-plotting step makes it unavailable — pull the
        // markers and restore the overlay's prior visibility so it doesn't
        // clutter the working flow.
        if (!CanValidateCalibration)
            MapOverlay.ForceHideCalibrationValidation();
    }

    /// <summary>Headline displayed inline with the wizard's per-step nav row.</summary>
    public string CurrentStepTitle => CurrentStep switch
    {
        WizardStep.PickMode => "What are you doing?",
        WizardStep.Calibrating => "Calibrate this area",
        WizardStep.Listening => "Use a survey",
        WizardStep.Gathering => "Walk your route",
        WizardStep.Done => "All collected",
        WizardStep.MotherlodeMeasuring => "Measure the treasure",
        WizardStep.MotherlodeLocating => "Locating…",
        WizardStep.MotherlodeWalk => "Walk to the treasure",
        WizardStep.MotherlodeDone => "All collected",
        _ => "",
    };

    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
    public MapOverlayViewModel MapOverlay { get; }
    public NudgePadViewModel NudgePad { get; }

    /// <summary>#460 cold-start pin-calibration driver — the Calibrating step
    /// binds its status/Solve to this.</summary>
    public PinCalibrationCoordinator PinCalibration { get; }

    public SessionState Session => _session;
    public SurveyFlowController SurveyFlow => _surveyFlow;
    public MotherlodeFlowController MotherlodeFlow => _motherlodeFlow;

    /// <summary>
    /// Mode-aware reset dispatched from the header's Reset icon. Wizard-level
    /// reset is "start this flow from scratch" — clears the player anchor,
    /// surveys, and any pending pin so the user lands back at step 2 (set
    /// position) for Survey or step 1 (record positions) for Motherlode.
    /// Overlays are preserved (per "Reset = do this flow again" rule).
    /// </summary>
    [RelayCommand]
    private void WizardReset()
    {
        if (_session.Mode == SessionMode.Motherlode)
        {
            Motherlode.ResetCommand.Execute(null);
            return;
        }
        // Survey (#454): no anchor — Reset clears surveys and lands on
        // Listening (the FSM's only resting state).
        _surveyFlow.Reset();
    }

    /// <summary>#477A: flip the guided walkthrough between the Drop and Pair
    /// phases. Lives on the wizard panel (a normal, always-clickable window) —
    /// the transparent overlay can't host the trigger while click-through.</summary>
    [RelayCommand]
    private void ToggleCalibrationPhase() => PinCalibration.TogglePhase();

    /// <summary>#477A: defer the currently-named pin and get the next
    /// spread suggestion (no pair recorded).</summary>
    [RelayCommand]
    private void SkipCalibrationPin() => PinCalibration.SkipSuggestion();

    /// <summary>#477A terminal Confirm: solve + persist, gated on ≥3 pairs and
    /// a good residual. On success the area is calibrated, <c>Changed</c>
    /// fires, and the wizard advances out of Calibrating (RecomputeStep).</summary>
    [RelayCommand]
    private void ConfirmCalibration()
    {
        PinCalibration.Confirm();
        RecomputeStep();
    }

    /// <summary>#477A "finish anyway": persist despite a high residual (still
    /// ≥3 pairs) — the non-affine ±10% ceiling means the user is never
    /// trapped.</summary>
    [RelayCommand]
    private void ConfirmCalibrationAnyway()
    {
        PinCalibration.ConfirmAnyway();
        RecomputeStep();
    }

    /// <summary>#477A: discard placed pairs and re-arm for a fresh attempt.</summary>
    [RelayCommand]
    private void ClearCalibrationPins() => PinCalibration.Arm();

    /// <summary>#477B: true once the user has clicked "Recalibrate this area"
    /// and we are waiting on the confirm guard (a misclick would wipe a good,
    /// persisted calibration).</summary>
    [ObservableProperty]
    private bool _isConfirmingRecalibrate;

    /// <summary>#501 (reworked): a recalibration is in progress — forces the
    /// <see cref="WizardStep.Calibrating"/> step even though the area is still
    /// calibrated (the old fit stays live, so a bailed redo loses nothing).
    /// Set by <see cref="Recalibrate"/>, cleared on cancel, on the new fit
    /// persisting, and on mode change.</summary>
    private bool _recalibrating;

    /// <summary>#477B/#501: recalibration entry — only meaningful when the
    /// area is already calibrated. Routes into the guided
    /// <see cref="WizardStep.Calibrating"/> flow with an inline confirm gate
    /// shown first (<see cref="IsConfirmingRecalibrate"/>); it does NOT delete
    /// anything. The old calibration stays active until a new fit is solved &amp;
    /// saved (which overwrites it), so cancelling loses nothing.</summary>
    [RelayCommand]
    private void Recalibrate()
    {
        _recalibrating = true;
        IsConfirmingRecalibrate = true;
        RecomputeStep();   // → Calibrating, inline gate showing
    }

    /// <summary>#501: acknowledge the inline gate — proceed to drop/pair. The
    /// persisted calibration is intentionally NOT cleared here; the existing
    /// <see cref="ConfirmCalibrationCommand"/> at the end of the guided flow
    /// solves and overwrites it. Still in <see cref="WizardStep.Calibrating"/>
    /// (no step change), so begin the guided flow directly — this is the point
    /// the overlay should pop, not the earlier chip click.</summary>
    [RelayCommand]
    private void ConfirmRecalibrate()
    {
        IsConfirmingRecalibrate = false;
        BeginGuidedCalibration();
    }

    /// <summary>#460/#501: arm pin-capture and open the map overlay for the
    /// guided drop/pair. The single place that "starts" calibration work —
    /// reached on entering <see cref="WizardStep.Calibrating"/> with no gate
    /// (cold-start) or on acknowledging the recalibrate gate.</summary>
    private void BeginGuidedCalibration()
    {
        PinCalibration.Arm();
        _session.IsMapVisible = true;
    }

    /// <summary>#501: back out of the recalibrate gate with the existing
    /// calibration untouched. Clears the in-progress flag and re-derives the
    /// step (→ back to Listening / the Motherlode stage / PickMode). Mirrors
    /// the pre-pick chip-escape overlay cleanup when no mode is picked.</summary>
    [RelayCommand]
    private void CancelRecalibrate()
    {
        IsConfirmingRecalibrate = false;
        _recalibrating = false;
        if (!HasPickedMode)
        {
            _session.IsMapVisible = false;
            _session.IsInventoryVisible = false;
        }
        RecomputeStep();
    }

    /// <summary>#477B: a "Recalibrate this area" affordance is offered only
    /// when there is a persisted calibration to redo (Listening step).</summary>
    public bool CanRecalibrate => _areaCalibration.IsCurrentAreaCalibrated;

    /// <summary>#113 Layer 5: true once the current area has an applied
    /// calibration — the only gate on the Motherlode on-map dot (the relative
    /// text is calibration-free). Drives the Walk panel's calibrate affordance
    /// vs. the honest "dot is approximate" caveat. Notified on
    /// <see cref="IAreaCalibrationService.Changed"/>.</summary>
    public bool IsAreaCalibrated => _areaCalibration.IsCurrentAreaCalibrated;

    /// <summary>#113: friendly name of the area Legolas thinks you're in, or
    /// null if none was detected (Mithril started mid-session with no
    /// "Entering Area" banner).</summary>
    public string? CurrentAreaName => _areaCalibration.CurrentAreaFriendlyName;

    /// <summary>True when the area is identified in reference data (so a
    /// calibration is even possible). False ⇒ the chip shows "area not
    /// detected" rather than a calibrate prompt.</summary>
    public bool IsAreaKnown => _areaCalibration.CurrentScene is not null;

    /// <summary>#113: the always-visible header chip text — area + calibration
    /// state at a glance, so the user never has to open the (experimental)
    /// calibration overlay to find out.</summary>
    public string CalibrationChipText =>
        !IsAreaKnown ? "Area not detected"
        : IsAreaCalibrated ? $"{CurrentAreaName} · calibrated"
        : $"{CurrentAreaName} · not calibrated";

    /// <summary>#501/#502: the chip is the single calibrate/recalibrate entry
    /// point — actionable whenever the area is known (calibrated or not, mode
    /// picked or not, since per-area calibration is orthogonal to the mode);
    /// otherwise a passive status label. The calibrated / uncalibrated /
    /// pre-pick-escape branches live in <see cref="CalibrateThisArea"/>.</summary>
    public bool CanCalibrateThisArea => IsAreaKnown;

    /// <summary>#113/#501: chip click — the single calibration entry point
    /// (never the experimental overlay). Uncalibrated → start the guided
    /// Drop/Pair flow (Survey already gates an uncalibrated area into
    /// <see cref="WizardStep.Calibrating"/>; Motherlode needs the explicit
    /// opt-in, it's calibration-free by default). Already calibrated → arm the
    /// #477B confirm guard (a single click must never wipe a good persisted
    /// calibration); the header popup's Confirm clears it, and
    /// <see cref="IAreaCalibrationService.Changed"/> routes
    /// <see cref="RecomputeStep"/> back into the cold-start pin route.</summary>
    [RelayCommand]
    private void CalibrateThisArea()
    {
        if (!CanCalibrateThisArea) return;   // chip is a passive label here
        if (IsAreaCalibrated)
        {
            Recalibrate();   // arm the confirm guard, don't destroy on one click
            return;
        }

        // #502: the chip is the escape from a chip-initiated pre-pick
        // calibration (no mode ⇒ hero Back + breadcrumb are hidden, so the
        // always-visible chip is the toggle). Mirror ChangeMode's
        // return-to-PickMode overlay cleanup.
        if (!HasPickedMode && CurrentStep == WizardStep.Calibrating)
        {
            _calibrationRequested = false;
            _session.IsMapVisible = false;
            _session.IsInventoryVisible = false;
            RecomputeStep();                 // → PickMode
            return;
        }

        // #502: per-area calibration is orthogonal to the mode, so the chip
        // may start it before one is picked. The one-shot drives the pre-pick
        // RecomputeStep branch; post-pick the Survey/Motherlode gates already
        // route to Calibrating, so it's a harmless re-arm there.
        _calibrationRequested = true;
        if (_session.Mode == SessionMode.Motherlode)
            _motherlodeCalibrationRequested = true;
        RecomputeStep();
    }

    /// <summary>#502: mode-independent one-shot — the header chip asked to
    /// calibrate (cold-start), possibly before a mode is picked. Drives the
    /// pre-pick <see cref="RecomputeStep"/> branch; the chip is also the
    /// escape (clears it). Cleared once the area calibrates or on mode change,
    /// alongside <see cref="_motherlodeCalibrationRequested"/>.</summary>
    private bool _calibrationRequested;

    /// <summary>#113: one-shot request to detour into the guided
    /// <see cref="WizardStep.Calibrating"/> walkthrough from Motherlode.
    /// Optional and non-blocking — measuring/locating/walking by relative text
    /// never needs it; it only unlocks the on-map dot. Cleared automatically
    /// once the area calibrates (or on mode change).</summary>
    private bool _motherlodeCalibrationRequested;

    /// <summary>#113: enter the same guided Drop/Pair calibration Survey uses,
    /// then fall back to the Motherlode stage once it persists. Reuses the
    /// existing <see cref="WizardStep.Calibrating"/> machinery via
    /// <see cref="RecomputeStep"/>.</summary>
    [RelayCommand]
    private void CalibrateForMotherlode()
    {
        _motherlodeCalibrationRequested = true;
        RecomputeStep();
    }

    /// <summary>
    /// Step-wise back. From the first post-pick step, returns to mode pick
    /// (delegates to <see cref="ChangeMode"/>). From mid-flow steps, undoes
    /// the most recent transition. From terminal/locked-in steps (Gathering,
    /// Done), full-resets to AwaitingPosition since the route's position
    /// anchor is invalidated once walking begins.
    /// </summary>
    [RelayCommand]
    private void Back()
    {
        switch (CurrentStep)
        {
            case WizardStep.Calibrating:
            case WizardStep.Listening:
            case WizardStep.MotherlodeMeasuring:
                // First post-pick step → back to mode pick.
                ChangeModeCommand.Execute(null);
                break;
            case WizardStep.Gathering:
            case WizardStep.Done:
                // Route in progress or done → reset the flow (clears surveys,
                // lands back on Listening).
                WizardResetCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private void PickSurveyMode()
    {
        _session.Mode = SessionMode.Survey;
        HasPickedMode = true;
        RecomputeStep();
    }

    [RelayCommand]
    private void PickMotherlodeMode()
    {
        _session.Mode = SessionMode.Motherlode;
        HasPickedMode = true;
        RecomputeStep();
    }

    /// <summary>
    /// Returns to step 0. Resets the active flow controller so its state
    /// doesn't leak into the next mode pick, and hides both overlays — the
    /// user is starting fresh, not continuing the same session. (Reset alone
    /// preserves overlays since the user is doing the same flow again.)
    /// </summary>
    [RelayCommand]
    private void ChangeMode()
    {
        if (_session.Mode == SessionMode.Survey)
            _surveyFlow.Reset();
        else
            _motherlodeFlow.Reset();
        _motherlodeCalibrationRequested = false;
        _calibrationRequested = false;   // #502: don't re-enter Calibrating from PickMode
        _recalibrating = false;          // #501: abandon any pending recalibrate
        _session.IsMapVisible = false;
        _session.IsInventoryVisible = false;
        HasPickedMode = false;
        RecomputeStep();
    }

    private void OnSurveyFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            RecomputeStep();
    }

    private void OnMotherlodeFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotherlodeFlowController.CurrentState))
            RecomputeStep();
    }

    // #113 Layer 4: the derived Motherlode stage moved (a reading landed, a
    // treasure solved, the last one was collected) — re-evaluate the step.
    private void OnMotherlodeViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotherlodeViewModel.Stage))
            RecomputeStep();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Hotkey-driven mode flip should re-project the wizard's step.
        if (e.PropertyName == nameof(SessionState.Mode))
            RecomputeStep();
        // Bubble overlay-visibility changes up to AreOverlaysVisible so the
        // hero-row toggle button's icon/tooltip stay in sync with the actual
        // session state (toggled by the button itself, the hotkey, or the
        // FSM-edge auto-hide/show in OnSurveyFlowTransitioned).
        else if (e.PropertyName is nameof(SessionState.IsMapVisible)
                              or nameof(SessionState.IsInventoryVisible))
            OnPropertyChanged(nameof(AreOverlaysVisible));
    }

    /// <summary>
    /// True once a survey run has completed at least once this app session, so the
    /// wizard can show a "View last report" button. The snapshot lives on the
    /// report service across FSM resets, so this stays true even after AutoReset
    /// has cleared <see cref="SessionState"/>.
    /// </summary>
    public bool HasLatestReport => _reportService?.LatestReport is not null;

    private void OnReportGenerated(LegolasSharePayload payload)
    {
        OnPropertyChanged(nameof(HasLatestReport));
        if (_settings.ShowReportOnDone)
            ShowReportDialog(payload);
    }

    [RelayCommand]
    private void ViewLastReport()
    {
        var payload = _reportService?.LatestReport;
        if (payload is null) return;
        ShowReportDialog(payload);
    }

    private void ShowReportDialog(LegolasSharePayload payload)
    {
        if (_dialogs is null || _reportService is null) return;
        // Capture the just-built payload so the dialog can rebuild on character-name
        // toggle without the FSM having to be in Done at click time.
        var captured = payload;
        var hasName = !string.IsNullOrWhiteSpace(_activeChar?.ActiveCharacterName);
        var vm = new LegolasShareDialogViewModel(
            buildPayload: includeName =>
            {
                if (includeName == (captured.CharacterName != null)) return captured;
                // Toggle the character name on the captured snapshot rather than
                // re-snapshotting from a now-reset SessionState.
                return new LegolasSharePayload
                {
                    SchemaVersion = captured.SchemaVersion,
                    CharacterName = includeName ? _activeChar?.ActiveCharacterName : null,
                    StartedAt = captured.StartedAt,
                    CompletedAt = captured.CompletedAt,
                    Mode = captured.Mode,
                    SurveyCount = captured.SurveyCount,
                    CollectedItemsByInternalName = new Dictionary<string, int>(captured.CollectedItemsByInternalName, StringComparer.Ordinal),
                    UnknownByName = captured.UnknownByName is null
                        ? null
                        : new Dictionary<string, int>(captured.UnknownByName, StringComparer.Ordinal),
                };
            },
            renderer: _renderer,
            settings: _settings,
            hasCharacterName: hasName,
            refData: _refData);
        _dialogs.ShowDialog(vm, new LegolasShareDialog());
    }

    private void RecomputeStep()
    {
        if (!HasPickedMode)
        {
            // #502: per-area calibration is mode-independent — the chip can
            // start it before a mode is picked. Honour the one-shot before the
            // PickMode fallthrough; clicking the chip again clears it (the
            // chip is the escape, since Back/breadcrumb are mode-gated).
            if (IsAreaKnown
                && ((_calibrationRequested && !_areaCalibration.IsCurrentAreaCalibrated)
                    || _recalibrating))
                CurrentStep = WizardStep.Calibrating;
            else
                CurrentStep = WizardStep.PickMode;
            return;
        }

        if (_session.Mode == SessionMode.Motherlode)
        {
            // #113 Layer 5: optional, non-blocking calibration detour. The
            // log-driven flow never needs calibration (relative text is
            // frame-internal); this fires only when the user asked for the
            // on-map dot in an uncalibrated area. Reuses the Survey guided
            // walkthrough; areaCalibration.Changed clears the request and
            // re-runs this, dropping back to the stage below.
            if ((_motherlodeCalibrationRequested && !_areaCalibration.IsCurrentAreaCalibrated)
                || _recalibrating)
            {
                CurrentStep = WizardStep.Calibrating;
                return;
            }

            // #113 Layer 4: derived sub-steps from the log-driven coordinator
            // snapshot (via MotherlodeViewModel.Stage). The FSM stays coarse.
            CurrentStep = Motherlode.Stage switch
            {
                MotherlodeStage.Locating => WizardStep.MotherlodeLocating,
                MotherlodeStage.Walk => WizardStep.MotherlodeWalk,
                MotherlodeStage.Done => WizardStep.MotherlodeDone,
                _ => WizardStep.MotherlodeMeasuring,
            };
            return;
        }

        // #460: cold-start gate. An uncalibrated area places nothing
        // (placement is absolute) — route the user through Calibrating until
        // the area has a calibration; IAreaCalibrationService.Changed
        // re-runs this once Solve persists one. #454 collapsed the rest of
        // the Survey FSM (no AwaitingPosition/Ready); Listening is the
        // resting/default step and its UI adapts to an empty Surveys list.
        if (!_areaCalibration.IsCurrentAreaCalibrated || _recalibrating)
        {
            CurrentStep = WizardStep.Calibrating;
            return;
        }

        CurrentStep = _surveyFlow.CurrentState switch
        {
            SurveyFlowState.Listening => WizardStep.Listening,
            SurveyFlowState.Gathering => WizardStep.Gathering,
            SurveyFlowState.Done => WizardStep.Done,
            // #476: the manual-override detour is transient — keep the panel
            // anchored to the step it was launched from (Listening vs
            // Gathering) rather than flashing a different step. The Set/Cancel
            // affordance toggles on MapOverlay.IsSettingPosition within those
            // same panels.
            SurveyFlowState.SettingPosition =>
                _surveyFlow.ReturnState == SurveyFlowState.Gathering
                    ? WizardStep.Gathering
                    : WizardStep.Listening,
            _ => WizardStep.Listening,
        };
    }
}
