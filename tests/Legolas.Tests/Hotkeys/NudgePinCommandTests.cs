using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Hotkeys;

namespace Legolas.Tests.Hotkeys;

public class NudgePinCommandTests
{
    private static (SessionState session, MapOverlayViewModel map, LegolasSettings settings)
        BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings);
        return (session, map, settings);
    }

    private static SurveyItemViewModel SeedSelectedSurveyAt(
        SessionState session, double x, double y)
    {
        var survey = Survey.Create("Test", new MetreOffset(0, 0), gridIndex: 0)
            with { ManualOverride = new OverlayPixel(x, y) };
        var vm = new SurveyItemViewModel(survey);
        session.Surveys.Add(vm);
        session.SelectedSurvey = vm;
        return vm;
    }

    [Fact]
    public async Task Up_default_moves_pin_minus_one_y()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(100);
        vm.Model.ManualOverride!.Value.Y.Should().Be(199);
    }

    [Fact]
    public async Task Down_fast_moves_pin_plus_five_y()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinDownFastCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(100);
        vm.Model.ManualOverride!.Value.Y.Should().BeApproximately(205, 1e-9);
    }

    [Fact]
    public async Task Right_fine_moves_pin_plus_quarter_x()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinRightFineCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().BeApproximately(100.25, 1e-9);
        vm.Model.ManualOverride!.Value.Y.Should().Be(200);
    }

    [Fact]
    public async Task Left_default_moves_pin_minus_one_x()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinLeftCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(99);
        vm.Model.ManualOverride!.Value.Y.Should().Be(200);
    }

    [Fact]
    public async Task Override_default_step_changes_resulting_pixel()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        settings.NudgeStepDefault = 3.0;
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.Y.Should().Be(197);
    }

    [Fact]
    public async Task Null_selected_survey_is_noop()
    {
        var (session, map, settings) = BuildSut();
        session.SelectedSurvey = null;
        var cmd = new NudgePinUpCommand(session, map, settings);

        var act = () => cmd.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Selected_survey_without_effective_pixel_is_noop()
    {
        var (session, map, settings) = BuildSut();
        var survey = Survey.Create("Test", new MetreOffset(0, 0), gridIndex: 0);
        // No ManualOverride and no PixelPos → EffectivePixel is null.
        var vm = new SurveyItemViewModel(survey);
        session.Surveys.Add(vm);
        session.SelectedSurvey = vm;

        var cmd = new NudgePinUpCommand(session, map, settings);
        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride.Should().BeNull("nudge cannot start from no pixel");
    }

    [Fact]
    public void Negative_step_setting_clamps_to_default()
    {
        var settings = new LegolasSettings();
        settings.NudgeStepDefault = -2.0;
        settings.NudgeStepDefault.Should().Be(1.0, "negative step would silently disable the nudge");
    }

    [Fact]
    public void Zero_step_setting_clamps_to_default()
    {
        var settings = new LegolasSettings();
        settings.NudgeStepFast = 0;
        settings.NudgeStepFast.Should().Be(5.0);
        settings.NudgeStepFine = 0;
        settings.NudgeStepFine.Should().Be(0.25);
    }

    [Fact]
    public void Hotkey_metadata_is_categorised_under_pin_nudge()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);
        cmd.Category.Should().Be("Legolas · Pin Nudge");
        cmd.Id.Should().Be("legolas.pin.nudge.up");
        cmd.DefaultBinding.Should().BeNull("arrow keys collide with in-game movement; user must opt in");
    }

    [Fact]
    public async Task No_pin_selected_is_a_noop()
    {
        // #454 retired the editable-anchor fallback — Pin Nudge only ever
        // targets a selected survey pin; with none, arrow keys do nothing.
        var (session, map, settings) = BuildSut();
        session.SelectedSurvey = null;
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        session.Surveys.Should().BeEmpty();
    }

    // ─── #139: registration gate (selected-pin only post-#454) ──────────────

    [Fact]
    public void IsRegistrable_false_at_idle()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);
        cmd.IsRegistrable.Should().BeFalse("no pin selected → nothing to nudge");
    }

    [Fact]
    public void IsRegistrable_true_when_survey_selected()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        SeedSelectedSurveyAt(session, 100, 200);

        cmd.IsRegistrable.Should().BeTrue();
    }

    [Fact]
    public void IsRegistrable_false_when_selection_cleared()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);
        SeedSelectedSurveyAt(session, 100, 200);
        cmd.IsRegistrable.Should().BeTrue();

        session.SelectedSurvey = null;

        cmd.IsRegistrable.Should().BeFalse();
    }

    [Fact]
    public void IsRegistrable_raises_PropertyChanged_on_selection_change()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        var fires = 0;
        ((IGatedHotkeyCommand)cmd).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IGatedHotkeyCommand.IsRegistrable)) fires++;
        };

        session.SelectedSurvey =
            new SurveyItemViewModel(Survey.Create("S", new MetreOffset(0, 0), gridIndex: 0));

        fires.Should().BeGreaterThanOrEqualTo(1,
            "HotkeyService relies on this signal to flip Win32 registration");
    }
}
