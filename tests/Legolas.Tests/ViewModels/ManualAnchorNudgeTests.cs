using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #477 Part C: the #476 manual "Set my position" anchor is selectable +
/// nudgeable on Part A's shared marker-interaction layer. Nudging mutates only
/// <see cref="SessionState.SurveyPlayerPixel"/> (flag preserved), never the
/// Motherlode <c>PlayerPosition</c>; the auto/tracker anchor stays
/// non-interactive; a fresh tracker fix still supersedes (the #476 rule).
/// </summary>
public class ManualAnchorNudgeTests
{
    // Scale 1, no rotation, origin (0,0): ProjectWorld(x,_,z) → (x, -z).
    private static readonly AreaCalibration Calib = new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 3, ResidualPixels: 0.5);

    private static (SessionState session, MapOverlayViewModel map, TestDomainEventBus bus)
        BuildSut()
    {
        var session = new SessionState { Mode = SessionMode.Survey };
        // #524: legacy CalibrationZoom = 1.0 stamp ⇒ pin live zoom to 1.0 so
        // a fresh tracker fix re-projects with zoomFactor 1.0 and the
        // pre-#524 nudge assertions still hold.
        session.CurrentMapZoom = 1.0;
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var posState = new FakePositionState();
        var bus = new TestDomainEventBus();
        var areaCal = new FakeAreaCalibrationService();
        areaCal.SetCalibration(Calib);
        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: null, positionState: posState, bus: bus, areaCalibration: areaCal);
        return (session, map, bus);
    }

    [Fact]
    public void Manual_anchor_nudge_moves_only_SurveyPlayerPixel_and_keeps_the_flag()
    {
        var (session, map, _) = BuildSut();
        session.PlayerPosition = new OverlayPixel(7, 7); // Motherlode anchor
        session.SurveyPlayerPixel = new OverlayPixel(100, 100);
        session.SurveyPlayerIsManual = true;

        map.Nudge(1, -1, step: 5);

        session.SurveyPlayerPixel.Should().Be(new OverlayPixel(105, 95));
        session.SurveyPlayerIsManual.Should().BeTrue();
        session.PlayerPosition.Should().Be(new OverlayPixel(7, 7), "Motherlode anchor is untouched");
    }

    [Fact]
    public void Auto_tracker_anchor_is_not_nudgeable()
    {
        var (session, map, _) = BuildSut();
        session.SurveyPlayerPixel = new OverlayPixel(50, 50);
        session.SurveyPlayerIsManual = false; // projected fix, not manual

        map.Nudge(1, 0, step: 5);

        session.SurveyPlayerPixel.Should().Be(new OverlayPixel(50, 50), "a data-sourced fix is read-only");
    }

    [Fact]
    public void A_fresh_tracker_fix_after_a_nudge_still_supersedes()
    {
        var (session, map, bus) = BuildSut();
        session.SurveyPlayerPixel = new OverlayPixel(100, 100);
        session.SurveyPlayerIsManual = true;
        map.Nudge(2, 0, step: 5); // → (110, 100)

        bus.Publish(new PlayerPositionChanged(33, 0, 11, PositionSource.Movement, default));

        session.SurveyPlayerIsManual.Should().BeFalse("a zone-in/teleport wins");
        session.SurveyPlayerPixel.Should().Be(new OverlayPixel(33, -11), "reprojected from the fresh fix");
    }

    [Fact]
    public void A_selected_survey_still_wins_over_the_manual_anchor()
    {
        var (session, map, _) = BuildSut();
        session.SurveyPlayerPixel = new OverlayPixel(100, 100);
        session.SurveyPlayerIsManual = true;

        var survey = new SurveyItemViewModel(
            Survey.CreateAbsolute("Diamond", new WorldCoord(10, 0, 20), new OverlayPixel(10, 20), 0));
        session.Surveys.Add(survey);
        session.SelectedSurvey = survey;

        map.Nudge(1, 0, step: 4);

        survey.EffectivePixel.Should().Be(new OverlayPixel(14, 20), "the selected survey takes precedence");
        session.SurveyPlayerPixel.Should().Be(new OverlayPixel(100, 100), "the manual anchor is untouched");
    }
}
