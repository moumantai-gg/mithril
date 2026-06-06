using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #494 — "Validate calibration": projecting known area references as ghost
/// markers via the persisted calibration's <c>ProjectWorld</c>, gated on the
/// area being calibrated, refreshing on calibration change.
/// </summary>
public class MapOverlayCalibrationValidationTests
{
    private static AreaCalibration Cal(double scale) =>
        new(scale, 0.0, 100, 200, 3, 1.5);

    private static (MapOverlayViewModel map, FakeAreaCalibrationService cal, SessionState session) Build()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null, areaCalibration: cal);
        return (map, cal, session);
    }

    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    [Fact]
    public void Toggle_on_projects_every_reference_via_ProjectWorld()
    {
        var (map, cal, session) = Build();
        var calibration = Cal(2.0);
        cal.SetReferences(Ref("Statue", 10, 5), Ref("Well", -4, 12));
        cal.SetCalibration(calibration);

        map.IsCurrentAreaCalibrated.Should().BeTrue();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeTrue();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.ShowCalibrationGhosts.Should().BeTrue();
        session.IsMapVisible.Should().BeTrue("the overlay must be up for the ghosts to be visible");
        map.CalibrationGhosts.Should().HaveCount(2);
        map.CalibrationGhosts[0].Name.Should().Be("Statue");
        // #1076 Phase 6.5: assertions use the frame-typed overlay projection.
        var overlayCal = new WorldToOverlayCalibration(
            calibration.OriginX, calibration.OriginY, calibration.Scale,
            calibration.RotationRadians, calibration.MirrorNorth);
        map.CalibrationGhosts[0].Pixel.Should().Be(overlayCal.ToOverlay(new WorldCoord(10, 0, 5)));
        map.CalibrationGhosts[1].Pixel.Should().Be(overlayCal.ToOverlay(new WorldCoord(-4, 0, 12)));
        map.CalibrationValidationStatus.Should().Contain("2 known").And.Contain("not accuracy");
    }

    [Fact]
    public void Toggle_off_clears_the_ghosts_and_status()
    {
        var (map, cal, _) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));

        map.ToggleCalibrationValidationCommand.Execute(null);
        map.CalibrationGhosts.Should().NotBeEmpty();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.ShowCalibrationGhosts.Should().BeFalse();
        map.CalibrationGhosts.Should().BeEmpty();
        map.CalibrationValidationStatus.Should().BeEmpty();
    }

    [Fact]
    public void Toggle_off_restores_prior_map_visibility_when_it_was_hidden()
    {
        var (map, cal, session) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        session.IsMapVisible.Should().BeFalse();

        map.ToggleCalibrationValidationCommand.Execute(null);
        session.IsMapVisible.Should().BeTrue("validation forces the overlay up");

        map.ToggleCalibrationValidationCommand.Execute(null);
        session.IsMapVisible.Should().BeFalse("the overlay's prior visibility is restored, not left stuck open");
    }

    [Fact]
    public void Toggle_off_keeps_the_overlay_up_if_it_was_already_visible()
    {
        var (map, cal, session) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        session.IsMapVisible = true;

        map.ToggleCalibrationValidationCommand.Execute(null);
        map.ToggleCalibrationValidationCommand.Execute(null);

        session.IsMapVisible.Should().BeTrue("it was already open before validation, so leave it open");
    }

    [Fact]
    public void ForceHide_clears_the_ghosts_and_restores_visibility_then_is_a_noop()
    {
        var (map, cal, session) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));

        map.ToggleCalibrationValidationCommand.Execute(null);
        map.ShowCalibrationGhosts.Should().BeTrue();

        map.ForceHideCalibrationValidation();
        map.ShowCalibrationGhosts.Should().BeFalse();
        map.CalibrationGhosts.Should().BeEmpty();
        session.IsMapVisible.Should().BeFalse();

        // No-op when not showing (no exception, state unchanged).
        map.ForceHideCalibrationValidation();
        map.ShowCalibrationGhosts.Should().BeFalse();
    }

    [Fact]
    public void Uncalibrated_area_disables_the_command_and_yields_no_ghosts()
    {
        var (map, _, _) = Build();   // FakeAreaCalibrationService with no calibration set

        map.IsCurrentAreaCalibrated.Should().BeFalse();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeFalse();

        // Even if invoked directly (CanExecute is advisory), there is nothing
        // to project, so no ghosts appear.
        map.ToggleCalibrationValidationCommand.Execute(null);
        map.CalibrationGhosts.Should().BeEmpty();
    }

    [Fact]
    public void Recalibration_while_showing_reprojects_the_ghosts()
    {
        var (map, cal, _) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        map.ToggleCalibrationValidationCommand.Execute(null);
        var before = map.CalibrationGhosts[0].Pixel;

        cal.SetCalibration(Cal(4.0));   // re-solved at a different scale → Changed

        map.CalibrationGhosts.Should().HaveCount(1);
        map.CalibrationGhosts[0].Pixel.Should().NotBe(before);
        // #1076 Phase 6.5: assertion uses the frame-typed overlay projection.
        var c4 = Cal(4.0);
        var overlay4 = new WorldToOverlayCalibration(
            c4.OriginX, c4.OriginY, c4.Scale, c4.RotationRadians, c4.MirrorNorth);
        map.CalibrationGhosts[0].Pixel.Should().Be(overlay4.ToOverlay(new WorldCoord(10, 0, 5)));
    }

    [Fact]
    public void Losing_calibration_while_showing_drops_the_overlay()
    {
        var (map, cal, session) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        map.ToggleCalibrationValidationCommand.Execute(null);
        map.ShowCalibrationGhosts.Should().BeTrue();

        cal.SetCalibration(null);   // calibration cleared

        map.ShowCalibrationGhosts.Should().BeFalse();
        map.CalibrationGhosts.Should().BeEmpty();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeFalse();
        session.IsMapVisible.Should().BeFalse("losing calibration restores the overlay's prior visibility too");
    }
}
