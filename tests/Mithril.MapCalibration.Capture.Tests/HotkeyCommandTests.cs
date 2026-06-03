using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Hotkeys;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 25 (#914): the two hotkeys. Capture-&amp;-calibrate respects the focus gate
/// (fire only with PG focused, spec §10) and invokes the engine via the
/// <see cref="ManualCalibrationCoordinator"/> (mithril#1046 C4 rewire);
/// draw-map-bbox does NOT respect the focus gate (the drag setup happens with the
/// overlay focused). Id / gate / wiring are unit-tested; the drag interaction
/// itself is WPF/manual-verify (Task 28).
/// </summary>
public sealed class HotkeyCommandTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static readonly MapSceneRef TestScene =
        new("AreaTest", null, "Map_AreaTest");

    /// <summary>
    /// Build a coordinator with no stored calibration (cold path: every hotkey
    /// press runs a full solve) backed by the given engine spy.
    /// </summary>
    private static ManualCalibrationCoordinator ColdCoordinator(
        SpyAutoCalibrationEngine engine,
        FakeOverlayWindow overlay)
    {
        var svc = new FakeCalibrationService(); // no stored calibration
        return new ManualCalibrationCoordinator(
            runner: engine,
            calibrationService: svc,
            mapState: new FakeMapState { CurrentMapScene = TestScene },
            sceneCache: new FakeSceneAssetCache(),
            overlay: overlay,
            timeProvider: TimeProvider.System);
    }

    // ── CaptureCalibrateCommand metadata + focus gate ──────────────────────────

    [Fact]
    public async Task Capture_command_respects_focus_gate_and_invokes_the_engine()
    {
        var engine = new SpyAutoCalibrationEngine();
        var overlay = new FakeOverlayWindow();
        var cmd = new CaptureCalibrateCommand(ColdCoordinator(engine, overlay));

        cmd.RespectsFocusGate.Should().BeTrue();
        cmd.Id.Should().Be("mapcalibration.capture");
        cmd.Category.Should().Be("Map Calibration");
        cmd.DefaultBinding.Should().BeNull();

        await cmd.ExecuteAsync(default);
        engine.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Capture_command_clears_the_status_chip_on_a_persisted_success()
    {
        var engine = new SpyAutoCalibrationEngine(persisted: true);
        var overlay = new FakeOverlayWindow();
        overlay.SetStatusMessage("a stale prior message");

        await new CaptureCalibrateCommand(ColdCoordinator(engine, overlay)).ExecuteAsync(default);

        overlay.StatusMessage.Should().BeNull("a persisted success clears the chip");
    }

    [Fact]
    public async Task Capture_command_surfaces_the_reject_reason_on_a_fail_soft_outcome()
    {
        var engine = new SpyAutoCalibrationEngine(persisted: false, rejectReason: "no map bbox set — use the draw-map-bbox hotkey first");
        var overlay = new FakeOverlayWindow();

        await new CaptureCalibrateCommand(ColdCoordinator(engine, overlay)).ExecuteAsync(default);

        overlay.StatusMessage.Should().NotBeNullOrWhiteSpace("a reject must surface an actionable chip");
        overlay.StatusMessage.Should().Be(CalibrationStatusFormatter.ForOutcome(
            new AutoCalibrationOutcome(false, "Map_AreaTest", "no map bbox set — use the draw-map-bbox hotkey first")));
    }

    // ── DrawMapBboxCommand ─────────────────────────────────────────────────────

    [Fact]
    public void Draw_bbox_command_does_not_respect_focus_gate()
    {
        var cmd = new DrawMapBboxCommand(new SpyBboxDrawController());
        cmd.RespectsFocusGate.Should().BeFalse();
        cmd.Id.Should().Be("mapcalibration.draw_bbox");
        cmd.Category.Should().Be("Map Calibration");
    }

    [Fact]
    public async Task Draw_bbox_command_begins_the_draw_mode()
    {
        var controller = new SpyBboxDrawController();
        await new DrawMapBboxCommand(controller).ExecuteAsync(default);
        controller.BeginCalls.Should().Be(1);
    }
}

internal sealed class SpyAutoCalibrationEngine : IAutoCalibrationRunner
{
    private readonly bool _persisted;
    private readonly string? _rejectReason;

    /// <summary>Default: a persisted success (matches the prior fixed behaviour).
    /// Pass <paramref name="persisted"/> false to simulate a fail-soft reject.</summary>
    public SpyAutoCalibrationEngine(bool persisted = true, string? rejectReason = null)
    {
        _persisted = persisted;
        _rejectReason = rejectReason ?? (persisted ? null : "no geometrically-consistent fit");
    }

    public int Calls { get; private set; }
    public Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new AutoCalibrationOutcome(_persisted, "Map_AreaTest", _persisted ? null : _rejectReason));
    }

    public Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct) =>
        throw new NotImplementedException("Implemented in Task B4.");
}

internal sealed class SpyBboxDrawController : IMapBboxDrawController
{
    public int BeginCalls { get; private set; }
    public void BeginDraw() => BeginCalls++;
}
