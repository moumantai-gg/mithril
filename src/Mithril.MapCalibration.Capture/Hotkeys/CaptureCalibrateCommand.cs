using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mithril.Shared.Hotkeys;

namespace Mithril.MapCalibration.Capture.Hotkeys;

/// <summary>
/// "Capture &amp; calibrate the current map" hotkey (spec §10). Delegates to
/// <see cref="ManualCalibrationCoordinator.HandleHotkeyAsync"/> which owns the
/// drift-check + arming + chip routing state machine (mithril#1046 §6.4).
/// <see cref="RespectsFocusGate"/> is <see langword="true"/>: it must fire only
/// with Project Gorgon focused, so the capture reads the game's framebuffer (not
/// Mithril's or another app's). No default binding (Legolas convention —
/// game-key collision avoidance).
/// </summary>
public sealed class CaptureCalibrateCommand : IHotkeyCommand
{
    private readonly ManualCalibrationCoordinator _coordinator;
    private readonly ILogger? _logger;

    public CaptureCalibrateCommand(ManualCalibrationCoordinator coordinator, ILogger<CaptureCalibrateCommand>? logger = null)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public string Id => "mapcalibration.capture";
    public string DisplayName => "Capture & Calibrate Map";
    public string? Category => "Map Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => true;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _coordinator.HandleHotkeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Manual calibrate hotkey threw; chip will not update.");
        }
    }
}
