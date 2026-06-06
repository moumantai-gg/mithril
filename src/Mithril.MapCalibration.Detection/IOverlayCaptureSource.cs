namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Captures the live pixel content of the overlay region (the area on
/// screen the user has overlaid on PG's world map) as a single-channel
/// <see cref="GrayImage"/> for consumption by
/// <see cref="IMapViewProbe.TryProbe"/>. The seam between the platform-side
/// overlay window machinery (which owns the screen-capture surface) and
/// the calibration library (which is platform-free) — see
/// <c>spec.md</c> §4.4.
///
/// <para><b>Fail-soft:</b> returns <c>null</c> if the overlay isn't visible
/// or the capture itself fails; the probe propagates that as a null fix
/// and the status badge surfaces the cause.</para>
///
/// <para><b>Note:</b> defined here (in <c>Mithril.MapCalibration.Detection</c>)
/// rather than in <c>Mithril.Overlay</c> so that
/// <see cref="LiveMapViewService"/> — which wires the probe, capture, and
/// texture together — can live in this project without creating a circular
/// reference back to <c>Mithril.Overlay</c> (which already references
/// this project). The production implementation
/// <c>OverlayWindowCaptureSource</c> lives in <c>Mithril.Overlay</c>.
/// </para>
/// </summary>
public interface IOverlayCaptureSource
{
    /// <summary>Capture the current overlay region as gray pixels, or
    /// <c>null</c> if the overlay isn't capturable right now.</summary>
    GrayImage? Capture();
}
