namespace Legolas.Tests.ViewModels;

/// <summary>
/// mithril#1095: SessionState.CurrentMapZoom deleted as part of the
/// CalibrationZoom removal. Live view state is now tracked by ILiveMapViewService
/// (MapViewFix). This file is intentionally empty — the original clamping /
/// INPC tests have no production surface to pin.
/// </summary>
public class SessionStateZoomTests
{
    // Tests removed in mithril#1095 P2.4: SessionState.CurrentMapZoom deleted;
    // zoom input is replaced by MapViewFix from ILiveMapViewService.
}
