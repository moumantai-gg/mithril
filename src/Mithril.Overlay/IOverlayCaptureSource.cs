using Mithril.MapCalibration.Detection;

namespace Mithril.Overlay;

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
/// </summary>
public interface IOverlayCaptureSource
{
    /// <summary>Capture the current overlay region as gray pixels, or
    /// <c>null</c> if the overlay isn't capturable right now.</summary>
    GrayImage? Capture();
}
