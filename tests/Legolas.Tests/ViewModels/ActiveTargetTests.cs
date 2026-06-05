using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

public class ActiveTargetTests
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

    private static SurveyItemViewModel SeedSurveyAt(SessionState session, string name, double x, double y, int routeOrder)
    {
        var survey = Survey.Create(name, new MetreOffset(x, y), gridIndex: routeOrder)
            with { ManualOverride = new OverlayPixel(x, y), RouteOrder = routeOrder };
        var vm = new SurveyItemViewModel(survey);
        session.Surveys.Add(vm);
        return vm;
    }

    // ─── ActiveSegmentPoints ─────────────────────────────────────────────

    [Fact]
    public void ActiveSegment_is_empty_when_no_active_target()
    {
        var (_, map, _) = BuildSut();
        map.ActiveSegmentPoints.Count.Should().Be(0);
    }

    [Fact]
    public void ActiveSegment_is_empty_before_first_collection()
    {
        // #454: no player anchor. Before anything is collected there is no
        // "where the player is now" start point — just the highlighted target
        // (IsActiveTarget / the static route), no live segment.
        var (session, map, _) = BuildSut();
        SeedSurveyAt(session, "First", 200, 200, 0);
        SeedSurveyAt(session, "Second", 300, 300, 1);

        map.ActiveSegmentPoints.Count.Should().Be(0);
    }

    [Fact]
    public void ActiveSegment_starts_from_last_collected_pin_after_one_collection()
    {
        var (session, map, _) = BuildSut();
        session.PlayerPosition = new OverlayPixel(100, 100);
        var first = SeedSurveyAt(session, "First", 200, 200, 0);
        SeedSurveyAt(session, "Second", 300, 300, 1);

        first.UpdateModel(first.Model with { Collected = true });

        // Active target rotates to "Second"; segment now starts from the
        // most-recently-collected pin (First @ 200,200), not the anchor.
        map.ActiveSegmentPoints.Count.Should().Be(2);
        map.ActiveSegmentPoints[0].X.Should().Be(200);
        map.ActiveSegmentPoints[0].Y.Should().Be(200);
        map.ActiveSegmentPoints[1].X.Should().Be(300);
        map.ActiveSegmentPoints[1].Y.Should().Be(300);
    }

    [Fact]
    public void ActiveSegment_uses_highest_RouteOrder_collected_as_start()
    {
        var (session, map, _) = BuildSut();
        session.PlayerPosition = new OverlayPixel(0, 0);
        var p0 = SeedSurveyAt(session, "P0", 100, 100, 0);
        var p1 = SeedSurveyAt(session, "P1", 200, 200, 1);
        SeedSurveyAt(session, "P2", 300, 300, 2);

        p0.UpdateModel(p0.Model with { Collected = true });
        p1.UpdateModel(p1.Model with { Collected = true });

        // Highest-ordered collected is P1 @ (200,200); active target is P2.
        map.ActiveSegmentPoints[0].X.Should().Be(200);
        map.ActiveSegmentPoints[0].Y.Should().Be(200);
        map.ActiveSegmentPoints[1].X.Should().Be(300);
        map.ActiveSegmentPoints[1].Y.Should().Be(300);
    }

    [Fact]
    public void ActiveSegment_clears_when_route_lines_disabled()
    {
        var (session, map, _) = BuildSut();
        // #454: a segment exists once one pin is collected (start = last
        // collected) and another remains active.
        var p0 = SeedSurveyAt(session, "P0", 100, 100, 0);
        SeedSurveyAt(session, "P1", 200, 200, 1);
        p0.UpdateModel(p0.Model with { Collected = true });
        map.ActiveSegmentPoints.Count.Should().Be(2, "sanity check");

        session.ShowRouteLines = false;

        map.ActiveSegmentPoints.Count.Should().Be(0);
    }

    [Fact]
    public void ActiveSegment_clears_when_all_collected()
    {
        var (session, map, _) = BuildSut();
        session.PlayerPosition = new OverlayPixel(0, 0);
        var only = SeedSurveyAt(session, "Only", 100, 100, 0);

        only.UpdateModel(only.Model with { Collected = true });

        map.ActiveSegmentPoints.Count.Should().Be(0, "no remaining active target → no segment");
    }

    // ─── ActiveTargetSummary ─────────────────────────────────────────────

    [Fact]
    public void Summary_is_empty_initially()
    {
        var session = new SessionState();
        session.ActiveTargetSummary.Should().Be(string.Empty);
    }

    [Fact]
    public void Summary_formats_as_one_based_count_and_name()
    {
        var (session, _, _) = BuildSut();
        SeedSurveyAt(session, "Hawthorn Tree", 100, 100, 0);
        SeedSurveyAt(session, "Pine", 200, 200, 1);
        SeedSurveyAt(session, "Oak", 300, 300, 2);

        // First active target: P0 (RouteOrder=0 → "#1 of 3 — Hawthorn Tree")
        session.ActiveTargetSummary.Should().Be("Next: #1 of 3 — Hawthorn Tree");
    }

    [Fact]
    public void Summary_advances_as_pins_are_collected()
    {
        var (session, _, _) = BuildSut();
        var p0 = SeedSurveyAt(session, "Pin0", 100, 100, 0);
        SeedSurveyAt(session, "Pin1", 200, 200, 1);

        p0.UpdateModel(p0.Model with { Collected = true });

        session.ActiveTargetSummary.Should().Be("Next: #2 of 2 — Pin1");
    }

    [Fact]
    public void Summary_clears_when_all_collected()
    {
        var (session, _, _) = BuildSut();
        var only = SeedSurveyAt(session, "Only", 100, 100, 0);

        only.UpdateModel(only.Model with { Collected = true });

        session.ActiveTargetSummary.Should().Be(string.Empty);
    }
}
