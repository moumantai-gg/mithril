using Mithril.MapCalibration;

namespace Mithril.Overlay;

/// <summary>
/// Conversion between a WPF Canvas's pixel space (mouse-event coordinates)
/// and the Mithril overlay window's pixel space. Today this is identity at
/// DPI=1 — when per-monitor DPI scaling lands, this is the one type that
/// needs to learn about real DPI math; the type system catches every site
/// that needs to update.
///
/// See spec §5 of the pixel-frame-typing refactor (#1076) and §12's
/// per-monitor-DPI follow-up note.
/// </summary>
public readonly record struct CanvasOverlayMapping(double DpiScale)
{
    public OverlayPixel CanvasToOverlay(CanvasPixel pixel) =>
        new(pixel.X * DpiScale, pixel.Y * DpiScale);

    public CanvasPixel OverlayToCanvas(OverlayPixel pixel) =>
        new(pixel.X / DpiScale, pixel.Y / DpiScale);
}
