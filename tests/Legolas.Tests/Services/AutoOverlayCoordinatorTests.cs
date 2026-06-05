using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Modules;

namespace Legolas.Tests.Services;

public class AutoOverlayCoordinatorTests
{
    private static (AutoOverlayCoordinator coordinator, SessionState session, LegolasSettings settings, SurveyFlowController flow) BuildSut()
    {
        var settings = new LegolasSettings();
        var session = new SessionState();
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var coordinator = new AutoOverlayCoordinator(gates, settings, session, flow);
        return (coordinator, session, settings, flow);
    }

    // #454: the FSM starts in Listening (an "active" state — no
    // AwaitingPosition bootstrap). A pin via Survey.CreateAbsolute.
    private static SurveyItemViewModel Pin(double px = 10, double py = 20) =>
        new(Survey.CreateAbsolute("Diamond", new WorldCoord(px, 0, py), new OverlayPixel(px, py), 0));

    [Fact]
    public void Initialize_no_change_when_inventory_hidden()
    {
        var (coordinator, _, settings, _) = BuildSut();
        coordinator.Initialize();
        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Inventory_visible_during_listening_flips_click_through_on()
    {
        var (coordinator, session, settings, _) = BuildSut();
        session.IsInventoryVisible = true;
        coordinator.Initialize();

        settings.ClickThroughInventory.Should().BeTrue();
    }

    [Fact]
    public void Done_state_with_inventory_visible_does_not_flip()
    {
        // Done is not an active session state — between cycles the user may
        // want the inventory normally interactive.
        var (coordinator, session, settings, flow) = BuildSut();
        var settings2 = settings;
        settings2.AutoResetWhenAllCollected = false;
        var pin = Pin();
        session.Surveys.Add(pin);
        pin.UpdateModel(pin.Model with { Collected = true }); // → Done
        flow.CurrentState.Should().Be(SurveyFlowState.Done);

        coordinator.Initialize();
        session.IsInventoryVisible = true;

        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Auto_setting_disabled_skips_flip()
    {
        var (coordinator, session, settings, _) = BuildSut();
        settings.AutoClickThroughInventoryDuringSession = false;
        coordinator.Initialize();
        session.IsInventoryVisible = true;

        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_does_not_force_off_when_user_disables_mid_session()
    {
        var (coordinator, session, settings, _) = BuildSut();
        session.IsInventoryVisible = true;
        coordinator.Initialize();
        settings.ClickThroughInventory.Should().BeTrue();

        settings.ClickThroughInventory = false;
        settings.ClickThroughInventory.Should().BeFalse();
    }

    [Fact]
    public void Coordinator_re_enables_on_next_state_transition()
    {
        var (coordinator, session, settings, flow) = BuildSut();
        session.Surveys.Add(Pin());
        session.IsInventoryVisible = true;
        coordinator.Initialize();

        settings.ClickThroughInventory = false; // user opt-out

        // Listening → Gathering should re-flip.
        flow.OptimizeRoute();

        settings.ClickThroughInventory.Should().BeTrue();
    }

    [Fact]
    public void Teardown_unsubscribes_and_stops_reacting()
    {
        var (coordinator, session, settings, _) = BuildSut();
        coordinator.Initialize();
        coordinator.Teardown();

        session.IsInventoryVisible = true; // would normally trigger
        settings.ClickThroughInventory.Should().BeFalse();
    }
}
