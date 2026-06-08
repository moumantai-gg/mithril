using FluentAssertions;
using Legolas.Services;
using Mithril.Shared.Game;
using Mithril.Shared.Modules;

namespace Legolas.Tests.Services;

/// <summary>
/// mithril#1114 — Regression coverage for the shutdown-time crash where a
/// Win32 foreground-changed event delivered during WPF Application teardown
/// cascaded through <see cref="ForegroundFocusGate.IsInApp"/> ->
/// <c>OverlayController.SyncMap</c> -> lazy <c>OverlayWindow</c> construction
/// -> <c>Application.LoadComponent</c>, which throws once
/// <c>Application.IsShuttingDown</c> is true.
///
/// <para>The guard lives at the source (OnWinEvent) so any future
/// PropertyChanged consumer is protected. These tests drive
/// <see cref="ForegroundFocusGate.TestSimulateForegroundChanged"/> with the
/// shutdown probe set to true/false and assert that
/// <c>EvaluateForeground</c> runs only when the probe says we're alive.</para>
/// </summary>
public sealed class ForegroundFocusGateTests
{
    [Fact]
    public void OnWinEvent_skips_EvaluateForeground_when_shutdown_probe_true()
    {
        var gate = MakeGate();
        gate.SetShutdownProbeForTest(() => true);

        gate.TestSimulateForegroundChanged(new IntPtr(0xABCD));

        gate.EvaluateForegroundCallsForTest.Should().Be(0,
            "OnWinEvent must return BEFORE EvaluateForeground while Dispatcher.HasShutdownStarted is true — "
            + "otherwise IsInApp can flip during WPF teardown and cascade into Application.LoadComponent (mithril#1114).");
    }

    [Fact]
    public void OnWinEvent_runs_EvaluateForeground_when_shutdown_probe_false()
    {
        var gate = MakeGate();
        gate.SetShutdownProbeForTest(() => false);

        gate.TestSimulateForegroundChanged(new IntPtr(0xABCD));

        gate.EvaluateForegroundCallsForTest.Should().Be(1,
            "the steady-state path must remain unchanged — OnWinEvent should still call EvaluateForeground "
            + "for every foreground-changed event delivered while WPF is alive.");
    }

    [Fact]
    public void Shutdown_probe_is_consulted_on_every_event_not_just_the_first()
    {
        var gate = MakeGate();
        var shuttingDown = false;
        gate.SetShutdownProbeForTest(() => shuttingDown);

        // Three events delivered while alive — all three run EvaluateForeground.
        gate.TestSimulateForegroundChanged(new IntPtr(1));
        gate.TestSimulateForegroundChanged(new IntPtr(2));
        gate.TestSimulateForegroundChanged(new IntPtr(3));
        gate.EvaluateForegroundCallsForTest.Should().Be(3);

        // WPF starts shutting down; subsequent events are dropped at the gate.
        shuttingDown = true;
        gate.TestSimulateForegroundChanged(new IntPtr(4));
        gate.TestSimulateForegroundChanged(new IntPtr(5));
        gate.EvaluateForegroundCallsForTest.Should().Be(3,
            "once the shutdown probe flips to true, no further events should reach EvaluateForeground.");
    }

    private static ForegroundFocusGate MakeGate()
    {
        var gates = new ModuleGates();
        var gameConfig = new GameConfig { GameProcessName = "ProjectGorgon" };
        return new ForegroundFocusGate(gates, gameConfig);
    }
}
