using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.MapCalibration;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #454 collapsed the Survey wizard: there is no <c>AwaitingPosition</c> step
/// (placement is absolute, no anchor). PickMode → Listening directly;
/// Listening auto-opens map + inventory. The cold-start <c>Calibrating</c>
/// step is added in the follow-up PR (#460) and is not exercised here.
/// </summary>
public class LegolasWizardViewModelTests
{
    // #460: a minimal IAreaCalibrationService. Defaults to *calibrated* so
    // the existing flow tests bypass the Calibrating gate; gate tests pass
    // one with Calibrated=false and flip it + RaiseChanged().
    private sealed class FakeAreaCalib : IAreaCalibrationService
    {
        public bool Calibrated { get; set; } = true;
        public bool IsCurrentAreaCalibrated => Calibrated;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public MapSceneRef? CurrentScene { get; set; } = new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public string? CurrentAreaFriendlyName => "Test";
        public AreaCalibration? CurrentCalibration => null;
        public WorldToOverlayCalibration? CurrentOverlayCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<Mithril.Shared.Reference.AreaEntry> AllAreas => Array.Empty<Mithril.Shared.Reference.AreaEntry>();
        public event EventHandler? Changed;
        public void SelectScene(MapSceneRef scene) { }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements, double calibrationZoom = 1.0)
        {
            Calibrated = true;
            var c = new AreaCalibration(1, 0, 0, 0, placements.Count, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            return c;
        }
        public void ClearCurrentAreaCalibration()
        {
            if (!Calibrated) return;
            Calibrated = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    private static (LegolasWizardViewModel wizard, SessionState session, SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow, LegolasSettings settings) BuildSut(
        FakeAreaCalib? calib = null, FakeMapPinState? pins = null)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var motherlodeFlow = new MotherlodeFlowController(session);
        var controlPanel = new ControlPanelViewModel(settings, session, surveyFlow);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaCalib = calib ?? new FakeAreaCalib();
        var pinState = pins ?? new FakeMapPinState();
        var bus = new TestDomainEventBus();
        var pinCal = new PinCalibrationCoordinator(areaCalib, pinState, bus, settings);
        var coordinator = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), motherlodeFlow, bus);
        var motherlode = new MotherlodeViewModel(coordinator, optimizer, motherlodeFlow);
        var mapOverlay = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings, pinCal);
        var nudgePad = new NudgePadViewModel(session, mapOverlay, settings);
        var wizard = new LegolasWizardViewModel(session, surveyFlow, motherlodeFlow,
            controlPanel, motherlode, mapOverlay, nudgePad, areaCalib, pinCal, settings);
        return (wizard, session, surveyFlow, motherlodeFlow, settings);
    }

    private static SurveyItemViewModel Pin(string name = "Diamond", double px = 10, double py = 20) =>
        new(Survey.CreateAbsolute(name, new WorldCoord(px, 0, py), new OverlayPixel(px, py), 0));

    [Fact]
    public void Initial_state_is_PickMode_with_HasPickedMode_false()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void PickSurveyMode_advances_straight_to_Listening()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.HasPickedMode.Should().BeTrue();
        session.Mode.Should().Be(SessionMode.Survey);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
    }

    [Fact]
    public void PickMotherlodeMode_advances_to_MotherlodeMeasuring()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickMotherlodeModeCommand.Execute(null);
        wizard.HasPickedMode.Should().BeTrue();
        session.Mode.Should().Be(SessionMode.Motherlode);
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Entering_Listening_auto_opens_map_and_inventory()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();

        wizard.PickSurveyModeCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Survey_happy_path_steps_through_each_transition()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        session.Surveys.Add(Pin("Diamond"));
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);

        var item = session.Surveys[0];
        item.UpdateModel(item.Model with { Collected = true });
        wizard.CurrentStep.Should().Be(WizardStep.Done);
    }

    [Fact]
    public void ChangeMode_returns_to_PickMode_resets_flow_and_hides_overlays()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
        session.Surveys.Add(Pin());

        wizard.ChangeModeCommand.Execute(null);

        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();
        // #454: Reset always lands on Listening (no anchor precondition).
        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void External_mode_flip_after_pick_re_projects_step()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        session.Mode = SessionMode.Motherlode;

        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Reset_preserves_overlay_visibility()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        // Reset from Listening (no Done edge) — "do this flow again".
        surveyFlow.Reset();

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Entering_Gathering_reopens_inventory_overlay()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        session.IsInventoryVisible = false; // user closed it mid-Listening

        surveyFlow.OptimizeRoute();

        wizard.CurrentStep.Should().Be(WizardStep.Gathering);
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void WizardReset_from_Listening_clears_surveys_and_stays_Listening()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.WizardResetCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Back_from_Listening_returns_to_PickMode()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void Back_from_Gathering_resets_to_Listening()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Overlays_auto_hide_on_Done_and_re_show_on_next_survey()
    {
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = true;

        // Cycle 1: first pin (re-show), collect → Done (auto-reset → Listening).
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening,
            "auto-reset returned to Listening after Done");
        session.IsMapVisible.Should().BeFalse("entering Done hides the map");
        session.IsInventoryVisible.Should().BeFalse("entering Done hides the inventory");

        // Cycle 2: next pin (Surveys 0→1) re-shows both.
        session.Surveys.Add(Pin("Coal", 30, 40));

        session.IsMapVisible.Should().BeTrue("next survey re-shows the map");
        session.IsInventoryVisible.Should().BeTrue("next survey re-shows the inventory");
    }

    [Fact]
    public void Overlay_auto_hide_does_nothing_when_setting_off()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = false;

        wizard.PickSurveyModeCommand.Execute(null); // map + inventory open
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();

        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.IsMapVisible.Should().BeTrue("setting off → Done doesn't hide");
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Overlay_auto_hide_does_not_fire_on_manual_reset_mid_session()
    {
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.HideOverlaysBetweenSessions = true;
        session.Surveys.Add(Pin());
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        surveyFlow.Reset(); // Listening→Listening (no Done edge)

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void ToggleOverlays_when_either_visible_hides_both()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = true;
        session.IsInventoryVisible = false;
        wizard.AreOverlaysVisible.Should().BeTrue();

        wizard.ToggleOverlaysCommand.Execute(null);

        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();
        wizard.AreOverlaysVisible.Should().BeFalse();
    }

    [Fact]
    public void ToggleOverlays_when_both_hidden_shows_both()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = false;
        session.IsInventoryVisible = false;
        wizard.AreOverlaysVisible.Should().BeFalse();

        wizard.ToggleOverlaysCommand.Execute(null);

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
        wizard.AreOverlaysVisible.Should().BeTrue();
    }

    [Fact]
    public void AreOverlaysVisible_change_notifies_when_session_visibility_flips()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = false;
        session.IsInventoryVisible = false;
        var notifications = 0;
        wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasWizardViewModel.AreOverlaysVisible))
                notifications++;
        };

        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        notifications.Should().BeGreaterOrEqualTo(2);
    }

    // ─── #460 cold-start Calibrating gate ────────────────────────────────

    [Fact]
    public void PickSurveyMode_on_uncalibrated_area_goes_to_Calibrating()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);

        wizard.PickSurveyModeCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Calibrating,
            "the step machine routes to Calibrating on an uncalibrated area " +
            "so the user can drop placement pins to bootstrap a baseline.");
        // #835 step 6 iter-1 touch-up: the gate that previously refused Arm
        // on uncalibrated areas was reverted (it contradicted dissolved-#868,
        // which mandates pixel-native placement-pin rendering — no seed
        // calibration required). The walkthrough now Arms successfully in
        // both calibrated and uncalibrated areas.
        wizard.PinCalibration.IsArmed.Should().BeTrue(
            "Arm has no seed-calibration gate (dissolved-#868); placement " +
            "pins draw pixel-native via LegolasOverlaySceneDrawer.");
    }

    [Fact]
    public void Calibrating_advances_to_Listening_when_area_becomes_calibrated()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        calib.Calibrated = true;
        calib.RaiseChanged(); // IAreaCalibrationService.Changed → RecomputeStep

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.PinCalibration.IsArmed.Should().BeFalse("leaving Calibrating disarms capture");
    }

    // ─── #113 Motherlode optional, non-blocking calibration ──────────────

    [Fact]
    public void PickMotherlode_on_uncalibrated_area_is_NOT_gated()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);

        wizard.PickMotherlodeModeCommand.Execute(null);

        // Calibration-free measuring is the #113 tenet — no Calibrating gate.
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
        wizard.IsAreaCalibrated.Should().BeFalse();
    }

    [Fact]
    public void CalibrateForMotherlode_detours_to_Calibrating_then_returns()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickMotherlodeModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);

        wizard.CalibrateForMotherlodeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        // #835 step 6 iter-1 touch-up: gate reverted per dissolved-#868
        // (no seed-calibration requirement); Arm() unconditionally
        // succeeds on the Calibrating step entry. The test's intent is
        // the detour-then-return state machine; Arm succeeding is the
        // current (and intended) behavior.
        wizard.PinCalibration.IsArmed.Should().BeTrue(
            "Arm has no seed-calibration gate (dissolved-#868).");

        calib.Calibrated = true;
        calib.RaiseChanged(); // IAreaCalibrationService.Changed → RecomputeStep

        // Request cleared, area calibrated → back to the log-driven stage.
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
        wizard.IsAreaCalibrated.Should().BeTrue();
    }

    [Fact]
    public void Calibration_chip_reflects_area_and_state()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickMotherlodeModeCommand.Execute(null);

        wizard.CalibrationChipText.Should().Be("Test · not calibrated");
        wizard.CanCalibrateThisArea.Should().BeTrue();

        wizard.CalibrateThisAreaCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        calib.Calibrated = true;
        calib.RaiseChanged();

        wizard.CalibrationChipText.Should().Be("Test · calibrated");
        wizard.CanCalibrateThisArea.Should().BeTrue(
            "#501: the chip is now the single calibrate/recalibrate entry point");
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Chip_click_on_a_calibrated_area_opens_the_inline_recalibrate_gate()
    {
        var calib = new FakeAreaCalib { Calibrated = true };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.CanCalibrateThisArea.Should().BeTrue();

        // #501 reworked: the chip routes into Calibrating with the inline
        // confirm gate showing — and the existing calibration is NOT touched.
        wizard.CalibrateThisAreaCommand.Execute(null);
        wizard.IsConfirmingRecalibrate.Should().BeTrue("the inline gate is showing");
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        calib.Calibrated.Should().BeTrue("the old calibration stays live until a new one is saved");

        // Acknowledging the gate reveals the drop/pair body — still no delete.
        wizard.ConfirmRecalibrateCommand.Execute(null);
        wizard.IsConfirmingRecalibrate.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        calib.Calibrated.Should().BeTrue("solving the new fit is what overwrites it, later");
        wizard.PinCalibration.IsArmed.Should().BeTrue();
    }

    [Fact]
    public void Chip_click_when_area_unknown_is_a_passive_label_noop()
    {
        // #502: the only remaining gate is IsAreaKnown. Area not detected ⇒
        // the chip is a status label, not a button.
        var calib = new FakeAreaCalib { CurrentScene = null, Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.CanCalibrateThisArea.Should().BeFalse();

        wizard.CalibrateThisAreaCommand.Execute(null);

        wizard.IsConfirmingRecalibrate.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    // ─── #502 pre-mode-pick calibration (chip is the escape) ─────────────

    [Fact]
    public void Chip_starts_calibration_before_a_mode_is_picked()
    {
        var calib = new FakeAreaCalib { Calibrated = false };   // known, uncalibrated
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.CanCalibrateThisArea.Should().BeTrue("area is known — no mode required");

        wizard.CalibrateThisAreaCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        wizard.HasPickedMode.Should().BeFalse("calibration is mode-independent");
        // #835 step 6 iter-1 touch-up: gate reverted per dissolved-#868
        // (no seed-calibration requirement). The chip-to-Calibrating wizard
        // transition Arms the coordinator unconditionally; placement pins
        // draw pixel-native via LegolasOverlaySceneDrawer.
        wizard.PinCalibration.IsArmed.Should().BeTrue(
            "Arm has no seed-calibration gate (dissolved-#868).");
    }

    [Fact]
    public void Chip_is_the_escape_from_a_pre_pick_calibration()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, session, _, _, _) = BuildSut(calib);
        wizard.CalibrateThisAreaCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        // Clicking the chip again backs out (no Back button pre-pick).
        wizard.CalibrateThisAreaCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.PinCalibration.IsArmed.Should().BeFalse("escape disarms the guided flow");
        calib.Calibrated.Should().BeFalse("escaping never destroys/forces anything");
        session.IsMapVisible.Should().BeFalse("escape mirrors ChangeMode's overlay cleanup");
    }

    [Fact]
    public void Confirming_a_pre_pick_calibration_lands_on_PickMode_calibrated()
    {
        // #835 step 6 iter-1 touch-up: the gate that required a baseline
        // calibration before Arm() was reverted per dissolved-#868
        // ("no seed-calibration requirement"). Placement pins draw
        // pixel-native via LegolasOverlaySceneDrawer, so the walkthrough
        // works in both calibrated and uncalibrated areas. Setup pre-seeds
        // Calibrated=false so the wizard routes to Calibrating on
        // CalibrateThisAreaCommand (PickMode skips Calibrating when an
        // area is already calibrated).
        var calib = new FakeAreaCalib { Calibrated = false };
        var pins = new FakeMapPinState();
        var (wizard, _, _, _, _) = BuildSut(calib, pins);

        wizard.CalibrateThisAreaCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        pins.Add(1, 2);
        pins.Add(3, 4);
        pins.Add(5, 6);
        wizard.ToggleCalibrationPhaseCommand.Execute(null);
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(10, 10));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(20, 20));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(30, 30));
        wizard.ConfirmCalibrationCommand.Execute(null);

        calib.Calibrated.Should().BeTrue();
        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode, "calibrated, still no mode → back to mode pick");
    }

    [Fact]
    public void ConfirmCalibration_persists_and_leaves_the_gate()
    {
        // #835 step 6 iter-1 touch-up: gate reverted per dissolved-#868
        // ("no seed-calibration requirement"). The walkthrough works in
        // both calibrated and uncalibrated areas; placement pins draw
        // pixel-native via LegolasOverlaySceneDrawer. Setup pre-seeds
        // Calibrated=false so PickSurveyMode routes to Calibrating
        // (PickMode skips Calibrating when already calibrated).
        var calib = new FakeAreaCalib { Calibrated = false };
        var pins = new FakeMapPinState();
        var (wizard, _, _, _, _) = BuildSut(calib, pins);
        wizard.PickSurveyModeCommand.Execute(null);

        // Drop three pins in-game, switch to the Pair phase, click each named
        // pin's spot, then Confirm. FakeAreaCalib returns residual 0 ⇒ good.
        pins.Add(1, 2);
        pins.Add(3, 4);
        pins.Add(5, 6);
        wizard.ToggleCalibrationPhaseCommand.Execute(null); // Drop → Pair
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(10, 10));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(20, 20));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(30, 30));
        wizard.PinCalibration.CanConfirm.Should().BeTrue();

        wizard.ConfirmCalibrationCommand.Execute(null);

        calib.Calibrated.Should().BeTrue();
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
    }

    [Fact]
    public void Back_from_Calibrating_returns_to_PickMode()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.PinCalibration.IsArmed.Should().BeFalse();
    }

    // ─── #477B in-flow recalibration re-entry ────────────────────────────

    [Fact]
    public void Recalibrate_is_offered_only_when_the_area_is_calibrated()
    {
        var calibrated = new FakeAreaCalib { Calibrated = true };
        var (wizard, _, _, _, _) = BuildSut(calibrated);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.CanRecalibrate.Should().BeTrue();

        var uncal = new FakeAreaCalib { Calibrated = false };
        var (w2, _, _, _, _) = BuildSut(uncal);
        w2.PickSurveyModeCommand.Execute(null);
        w2.CanRecalibrate.Should().BeFalse("nothing to redo on an uncalibrated area");
    }

    [Fact]
    public void Recalibrate_defers_the_delete_until_a_new_fit_is_saved()
    {
        var calib = new FakeAreaCalib { Calibrated = true };
        var pins = new FakeMapPinState();
        var (wizard, _, _, _, _) = BuildSut(calib, pins);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        // Entry routes into Calibrating with the inline gate showing — the
        // old calibration is NOT touched.
        wizard.RecalibrateCommand.Execute(null);
        wizard.IsConfirmingRecalibrate.Should().BeTrue();
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        calib.Calibrated.Should().BeTrue("the existing fit stays live behind the gate");
        wizard.PinCalibration.IsArmed.Should().BeFalse(
            "#501: don't arm pin-capture until the gate is acknowledged");

        // Cancel exits with the calibration entirely intact.
        wizard.CancelRecalibrateCommand.Execute(null);
        wizard.IsConfirmingRecalibrate.Should().BeFalse();
        calib.Calibrated.Should().BeTrue("cancelling a recalibration loses nothing");
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.PinCalibration.IsArmed.Should().BeFalse("leaving Calibrating disarms");

        // Re-enter, acknowledge the gate → still no delete; drop/pair body up.
        wizard.RecalibrateCommand.Execute(null);
        wizard.ConfirmRecalibrateCommand.Execute(null);
        wizard.IsConfirmingRecalibrate.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        calib.Calibrated.Should().BeTrue("acknowledging the gate still doesn't delete");
        wizard.PinCalibration.IsArmed.Should().BeTrue();

        // Completing the guided flow is what overwrites it.
        pins.Add(1, 2);
        pins.Add(3, 4);
        pins.Add(5, 6);
        wizard.ToggleCalibrationPhaseCommand.Execute(null);
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(10, 10));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(20, 20));
        wizard.MapOverlay.PairCalibrationClick(new OverlayPixel(30, 30));
        wizard.ConfirmCalibrationCommand.Execute(null);

        calib.Calibrated.Should().BeTrue("a fresh fit was saved (overwrite, not delete-then-maybe)");
        wizard.CurrentStep.Should().Be(WizardStep.Listening, "recalibration finished");
    }

    [Fact]
    public void Recalibrate_gate_does_not_pop_the_overlay_until_acknowledged()
    {
        // Pre-pick (map hidden at PickMode) gives a clean "was it forced
        // open?" assertion. Calibrated chip → gate, no overlay/arm yet.
        var calib = new FakeAreaCalib { Calibrated = true };
        var (wizard, session, _, _, _) = BuildSut(calib);
        session.IsMapVisible.Should().BeFalse();

        wizard.CalibrateThisAreaCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        wizard.IsConfirmingRecalibrate.Should().BeTrue();
        session.IsMapVisible.Should().BeFalse("the overlay must not pop under the confirm gate");
        wizard.PinCalibration.IsArmed.Should().BeFalse();

        wizard.ConfirmRecalibrateCommand.Execute(null);
        session.IsMapVisible.Should().BeTrue("acknowledging is when the guided flow begins");
        wizard.PinCalibration.IsArmed.Should().BeTrue();
    }

    [Fact]
    public void Cold_start_calibration_opens_the_overlay_immediately_no_gate()
    {
        // No calibration ⇒ no gate ⇒ the overlay/arm begin at once (you need
        // the map to drop pins).
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, session, _, _, _) = BuildSut(calib);

        wizard.CalibrateThisAreaCommand.Execute(null);   // pre-pick cold-start

        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        wizard.IsConfirmingRecalibrate.Should().BeFalse("cold-start has nothing to confirm");
        session.IsMapVisible.Should().BeTrue();
        wizard.PinCalibration.IsArmed.Should().BeTrue();
    }

    // ---- #495 Validate-calibration availability gate -------------------

    [Fact]
    public void CanValidateCalibration_is_true_at_PickMode_when_calibrated()
    {
        var (wizard, _, _, _, _) = BuildSut();   // FakeAreaCalib defaults calibrated
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.CanValidateCalibration.Should().BeTrue("a between-runs diagnostic — available when not in a flow");
    }

    [Fact]
    public void CanValidateCalibration_is_false_while_surveying()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();

        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.CanValidateCalibration.Should().BeFalse("validation would clutter the working survey overlay");

        session.Surveys.Add(Pin());
        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);
        wizard.CanValidateCalibration.Should().BeFalse();
    }

    [Fact]
    public void CanValidateCalibration_is_false_while_plotting_motherlodes()
    {
        var (wizard, _, _, _, _) = BuildSut();

        wizard.PickMotherlodeModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
        wizard.CanValidateCalibration.Should().BeFalse();
    }

    [Fact]
    public void CanValidateCalibration_is_false_when_the_area_is_uncalibrated()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.CanValidateCalibration.Should().BeFalse("nothing to validate without a calibration");
    }

    [Fact]
    public void Entering_a_flow_step_notifies_CanValidateCalibration()
    {
        var (wizard, _, _, _, _) = BuildSut();
        var changes = new List<string?>();
        wizard.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        wizard.PickSurveyModeCommand.Execute(null);

        changes.Should().Contain(nameof(LegolasWizardViewModel.CanValidateCalibration));
    }
}
