using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Hotkeys;
using Legolas.Services;

namespace Legolas.Hotkeys;

/// <summary>
/// Hotkey: re-detect the live map view by re-running the probe for the
/// current area. The "I just changed PG's zoom or pan; resync" affordance.
/// See <c>docs/planning/calibration-1095-live-view-detector/spec.md</c> §6.
/// </summary>
public sealed class RedetectMapViewHotkey : IHotkeyCommand
{
    private readonly ILiveMapViewService _liveView;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly ILogger<RedetectMapViewHotkey>? _logger;

    public string Id => "legolas.redetect_map_view";
    public string DisplayName => "Re-detect Map View (after panning or zooming PG)";
    public string? Category => "Legolas · Calibration";
    public HotkeyBinding? DefaultBinding => null;
    // Calibration work happens while PG has focus — must stay registered.
    public bool RespectsFocusGate => false;

    public RedetectMapViewHotkey(
        ILiveMapViewService liveView,
        IAreaCalibrationService areaCalibration,
        ILogger<RedetectMapViewHotkey>? logger = null)
    {
        _liveView = liveView;
        _areaCalibration = areaCalibration;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var area = _areaCalibration.CurrentScene?.MapAssetKey;
        if (string.IsNullOrEmpty(area))
        {
            _logger?.LogInformation("Redetect hotkey fired but no area is current — no-op.");
            return;
        }
        _logger?.LogInformation("Redetect hotkey fired for {Area}.", area);
        await _liveView.RefreshAsync(area, ct);
    }
}
