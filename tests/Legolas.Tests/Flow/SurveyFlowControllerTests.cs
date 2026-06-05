using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;

namespace Legolas.Tests.Flow;

/// <summary>
/// #454 collapsed the Survey FSM to <c>Listening → OptimizeRoute → Gathering
/// → Done → (auto)Reset → Listening</c>. No anchor/AwaitingPosition/Ready
/// bootstrap (pins are absolute). Pins are added straight to
/// <see cref="SessionState.Surveys"/>; the controller reacts to the
/// collection change.
/// </summary>
public class SurveyFlowControllerTests
{
    private static readonly DateTime FixedTime = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset when) => _now = when;
    }

    private static (SurveyFlowController flow, SessionState session, LegolasSettings settings, List<SurveyTransition> transitions, FakeClock clock) BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var clock = new FakeClock(new DateTimeOffset(FixedTime, TimeSpan.Zero));
        var flow = new SurveyFlowController(session, settings, clock);
        var transitions = new List<SurveyTransition>();
        flow.Transitioned += transitions.Add;
        return (flow, session, settings, transitions, clock);
    }

    private static SurveyItemViewModel Pin(string name = "Diamond", double px = 10, double py = 20) =>
        new(Survey.CreateAbsolute(name, new WorldCoord(px, 0, py), new OverlayPixel(px, py), 0));

    [Fact]
    public void InitialState_is_Listening()
    {
        var (flow, _, _, _, _) = BuildSut();
        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
    }

    [Fact]
    public void FirstPin_in_Listening_stamps_StartedAt_without_a_transition()
    {
        var (flow, session, _, transitions, clock) = BuildSut();
        var stamp = new DateTimeOffset(FixedTime.AddMinutes(3), TimeSpan.Zero);
        clock.Set(stamp);

        session.Surveys.Add(Pin("Good Metal Slab"));

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.StartedAt.Should().Be(stamp);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void SubsequentSurveys_do_not_re_stamp_StartedAt()
    {
        var (_, session, _, _, clock) = BuildSut();
        var first = new DateTimeOffset(FixedTime.AddMinutes(1), TimeSpan.Zero);
        clock.Set(first);
        session.Surveys.Add(Pin("Diamond"));

        clock.Set(new DateTimeOffset(FixedTime.AddMinutes(5), TimeSpan.Zero));
        session.Surveys.Add(Pin("Coal", 30, 40));

        session.StartedAt.Should().Be(first);
    }

    [Fact]
    public void SecondCycle_after_AutoReset_re_stamps_StartedAt()
    {
        // Regression (reframed for the collapsed FSM): a completed cycle
        // auto-resets to Listening and the next first pin must re-stamp a
        // fresh StartedAt — otherwise run #2 reports ≈0s elapsed.
        var (flow, session, settings, _, clock) = BuildSut();
        settings.AutoResetWhenAllCollected = true;

        var t1 = new DateTimeOffset(FixedTime.AddMinutes(1), TimeSpan.Zero);
        clock.Set(t1);
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        session.StartedAt.Should().Be(t1);
        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.StartedAt.Should().BeNull("ClearSurveys wiped the cycle-1 stamp");

        var t2 = new DateTimeOffset(FixedTime.AddMinutes(10), TimeSpan.Zero);
        clock.Set(t2);
        session.Surveys.Add(Pin("Coal", 30, 40));

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.StartedAt.Should().Be(t2, "second cycle re-stamped on its first pin");
    }

    [Fact]
    public void OptimizeRoute_Listening_to_Gathering_when_surveys_present()
    {
        var (flow, session, _, _, _) = BuildSut();
        session.Surveys.Add(Pin());
        flow.CanOptimize.Should().BeTrue();

        flow.OptimizeRoute();

        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
    }

    [Fact]
    public void OptimizeRoute_with_no_pins_is_a_noop()
    {
        var (flow, session, _, _, _) = BuildSut();
        flow.CanOptimize.Should().BeFalse("Listening no longer implies pins exist");

        flow.OptimizeRoute();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.LastLogEvent.Should().Contain("OptimizeRoute ignored");
    }

    [Fact]
    public void New_target_during_Gathering_is_accepted_no_drop()
    {
        var (flow, session, _, _, _) = BuildSut();
        session.Surveys.Add(Pin("First"));
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);

        session.Surveys.Add(Pin("Second", 50, 60));

        session.Surveys.Should().HaveCount(2, "a new target mid-route is kept, not dropped");
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
    }

    [Fact]
    public void Reset_clears_surveys_and_returns_to_Listening()
    {
        var (flow, session, _, _, _) = BuildSut();
        session.Surveys.Add(Pin());
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.Surveys.Should().BeEmpty();
        session.StartedAt.Should().BeNull("Reset wipes the stamp; next first pin re-stamps");
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_then_AutoReset_back_to_Listening()
    {
        var (flow, session, settings, transitions, _) = BuildSut();
        var s1 = Pin("Diamond");
        var s2 = Pin("Coal", 30, 40);
        session.Surveys.Add(s1);
        session.Surveys.Add(s2);
        flow.OptimizeRoute();
        transitions.Clear();
        settings.AutoResetWhenAllCollected = true;

        s1.UpdateModel(s1.Model with { Collected = true });
        s2.UpdateModel(s2.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Listening, "auto-reset returned to Listening");
        session.Surveys.Should().BeEmpty();
        transitions.Select(t => t.To).Should().ContainInOrder(
            SurveyFlowState.Done, SurveyFlowState.Listening);
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_no_AutoReset_stays_Done()
    {
        var (flow, session, settings, _, _) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        flow.OptimizeRoute();

        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Done);
        session.Surveys.Should().HaveCount(1, "no auto-reset, surveys retained for review");
    }

    [Fact]
    public void AllCollected_from_Listening_also_transitions_to_Done()
    {
        var (flow, session, settings, _, _) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);

        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Done);
    }

    // ─── #476 SettingPosition detour (Option C) ──────────────────────────

    [Fact]
    public void RequestSetPosition_from_Listening_parks_and_returns_on_confirm()
    {
        var (flow, _, _, transitions, _) = BuildSut();

        flow.RequestSetPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.SettingPosition);
        flow.IsSettingPosition.Should().BeTrue();
        flow.ReturnState.Should().Be(SurveyFlowState.Listening);

        flow.ConfirmPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        transitions.Select(t => (t.From, t.To)).Should().Equal(
            (SurveyFlowState.Listening, SurveyFlowState.SettingPosition),
            (SurveyFlowState.SettingPosition, SurveyFlowState.Listening));
    }

    [Fact]
    public void RequestSetPosition_from_Gathering_returns_to_Gathering()
    {
        var (flow, session, _, _, _) = BuildSut();
        session.Surveys.Add(Pin());
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);

        flow.RequestSetPosition();
        flow.ReturnState.Should().Be(SurveyFlowState.Gathering);

        flow.ConfirmPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering, "the detour returns to where it was launched");
    }

    [Fact]
    public void CancelSetPosition_returns_to_parked_state()
    {
        var (flow, _, _, _, _) = BuildSut();
        flow.RequestSetPosition();

        flow.CancelSetPosition();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
    }

    [Fact]
    public void RequestSetPosition_is_ignored_from_Done()
    {
        var (flow, session, settings, _, _) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });
        flow.CurrentState.Should().Be(SurveyFlowState.Done);

        flow.RequestSetPosition();

        flow.CurrentState.Should().Be(SurveyFlowState.Done, "no-op from a state with no anchor to correct");
    }

    [Fact]
    public void Confirm_and_Cancel_are_noops_outside_SettingPosition()
    {
        var (flow, _, _, transitions, _) = BuildSut();

        flow.ConfirmPosition();
        flow.CancelSetPosition();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void Reset_exits_SettingPosition()
    {
        var (flow, _, _, _, _) = BuildSut();
        flow.RequestSetPosition();

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        flow.IsSettingPosition.Should().BeFalse();
    }

    [Fact]
    public void Reset_from_Listening_fires_Transitioned_self_edge_with_Reset_trigger()
    {
        // Iter-1 review of PR #721 (issue #699): a manual Reset() invoked
        // while already in Listening must still surface as a Transitioned
        // event so session-bound listeners (ItemCollectionTracker's
        // pending-Add queue clear) observe the "start over" signal. Without
        // this, stale _pendingAdds from the prior cycle would leak into the
        // next survey.
        var (flow, _, _, transitions, _) = BuildSut();
        flow.CurrentState.Should().Be(SurveyFlowState.Listening);

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        transitions.Should().ContainSingle();
        transitions.Single().Should().BeEquivalentTo(new SurveyTransition(
            SurveyFlowState.Listening, SurveyFlowState.Listening, "Reset"));
    }
}
