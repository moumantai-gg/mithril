namespace Legolas.Tests.Rendering;

/// <summary>
/// mithril#1095: IOverlayZoomSource / LegolasOverlayZoomSource were deleted as
/// part of the CalibrationZoom removal (P2.1/P2.2). The live view state is now
/// tracked by ILiveMapViewService (MapViewFix). This file is intentionally empty
/// — the original DI-order tests have no production surface to pin.
/// </summary>
public sealed class LegolasZoomSourceDiOrderTests
{
    // Tests removed in mithril#1095 P2.4: IOverlayZoomSource and
    // LegolasOverlayZoomSource deleted; live zoom is now MapViewFix from
    // ILiveMapViewService, not a scalar zoom-source adapter.
}
