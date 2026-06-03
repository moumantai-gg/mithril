using System.Threading;
using System.Threading.Tasks;
using Arda.World.Player;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.Overlay;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Failing tests for <see cref="ManualCalibrationCoordinator"/> state machine
/// (mithril#1046 §6.4). ManualCalibrationCoordinator does not exist yet —
/// Task C3 creates it. These tests define the expected contract.
/// </summary>
public sealed class ManualCalibrationCoordinatorTests
{
    private const string Asset = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Asset);

    private static AreaCalibration Stored() =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: 0.7)
        { Source = CalibrationSource.AutoCapture };

    private sealed class FakeRunner : IAutoCalibrationRunner
    {
        public int SolveCalls;
        public int DriftCalls;
        public DriftCheckOutcome DriftReturn = new DriftCheckOutcome.Ok(0.5, 6);
        public AutoCalibrationOutcome SolveReturn =
            new(Persisted: true, AreaKey: Asset, RejectReason: null, OutcomeCategory: "Accepted");

        public Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
        {
            SolveCalls++;
            return Task.FromResult(SolveReturn);
        }

        public Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct)
        {
            DriftCalls++;
            return Task.FromResult(DriftReturn);
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Build a <see cref="FakeCalibrationService"/> with an optional pre-seeded calibration
    /// for the test scene asset key.
    /// </summary>
    private static FakeCalibrationService ServiceWith(AreaCalibration? cal)
    {
        var svc = new FakeCalibrationService();
        if (cal is not null) svc.Seed(Asset, cal);
        return svc;
    }

    private static ManualCalibrationCoordinator NewCoordinator(
        FakeRunner runner,
        IMapCalibrationService calibrationService,
        TimeProvider? clock = null,
        IOverlayWindow? overlay = null,
        CapturingLogger? logger = null,
        MapSceneRef? scene = null) =>
        new(
            runner: runner,
            calibrationService: calibrationService,
            mapState: new FakeMapState { CurrentMapScene = scene ?? Scene },
            sceneCache: new FakeSceneAssetCache(),
            overlay: overlay ?? new FakeOverlayWindow(),
            timeProvider: clock ?? new FakeClock(),
            logger: (ILogger?)logger ?? NullLogger.Instance);

    // ── 1. Cold path (no stored calibration) ─────────────────────────────────

    [Fact]
    public async Task Hotkey_NoStoredCalibration_RunsFullSolve()
    {
        var runner = new FakeRunner();
        var coordinator = NewCoordinator(runner, calibrationService: ServiceWith(null));

        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        runner.SolveCalls.Should().Be(1);
        runner.DriftCalls.Should().Be(0);
    }

    // ── 2. Stored calibration + drift OK ─────────────────────────────────────

    [Fact]
    public async Task Hotkey_DriftOk_DoesNotArmDoesNotSolve()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Ok(0.5, 6) };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()), overlay: overlay);

        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        runner.SolveCalls.Should().Be(0);
        runner.DriftCalls.Should().Be(1);
        overlay.StatusMessage.Should().Contain("OK", Exactly.Once());
        coordinator.IsArmed.Should().BeFalse();
    }

    // ── 3. Stored calibration + drift detected → arm ─────────────────────────

    [Fact]
    public async Task Hotkey_Drift_ArmsAndSetsChip()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()), overlay: overlay);

        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        coordinator.IsArmed.Should().BeTrue();
        overlay.StatusMessage.Should().Contain("Drift detected")
            .And.Contain($"{ManualCalibrationCoordinator.ArmingSeconds}s");
    }

    // ── 4. Armed re-press within window → full solve ──────────────────────────

    [Fact]
    public async Task Hotkey_ArmedRePressWithinWindow_RunsFullSolveAndDisarms()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()), clock: clock);

        // First press: drift detected → arm
        await coordinator.HandleHotkeyAsync(CancellationToken.None);
        coordinator.IsArmed.Should().BeTrue();

        // Advance time but stay within the 10-second window
        clock.Now = clock.Now.AddSeconds(5);
        runner.SolveCalls = 0;

        // Second press: re-press within window → run full solve
        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        runner.SolveCalls.Should().Be(1);
        coordinator.IsArmed.Should().BeFalse();
    }

    // ── 5. Armed re-press after window expires → drift check again, no solve ──

    [Fact]
    public async Task Hotkey_ArmedRePressAfterWindow_RunsDriftCheckAgain()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()), clock: clock);

        // First press: drift detected → arm
        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        // Advance past the 10-second arming window
        clock.Now = clock.Now.AddSeconds(ManualCalibrationCoordinator.ArmingSeconds + 1);
        runner.DriftCalls = 0;
        runner.SolveCalls = 0;

        // Second press: window expired → stale arm cleared, drift check runs again
        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        runner.DriftCalls.Should().Be(1);
        runner.SolveCalls.Should().Be(0);
    }

    // ── 6. Expiry is logged ───────────────────────────────────────────────────

    [Fact]
    public async Task Hotkey_LogsWhenArmingWindowExpires()
    {
        var logger = new CapturingLogger();
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.Drift(5.0, 6, 2.1) };
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()),
            clock: clock, logger: logger);

        // First press: arm
        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        // Expire
        clock.Now = clock.Now.AddSeconds(ManualCalibrationCoordinator.ArmingSeconds + 1);

        // Second press: should log expiry
        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("arming window expired", System.StringComparison.OrdinalIgnoreCase));
    }

    // ── 7. Capture failed → actionable chip, no arm ───────────────────────────

    [Fact]
    public async Task Hotkey_DriftCheckCaptureFailed_SurfacesActionableChipAndDoesNotArm()
    {
        var runner = new FakeRunner { DriftReturn = new DriftCheckOutcome.CaptureFailed("no map bbox set") };
        var overlay = new FakeOverlayWindow();
        var coordinator = NewCoordinator(runner, ServiceWith(Stored()), overlay: overlay);

        await coordinator.HandleHotkeyAsync(CancellationToken.None);

        coordinator.IsArmed.Should().BeFalse();
        overlay.StatusMessage.Should().Contain("no map bbox set");
    }
}
