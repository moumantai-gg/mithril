// #1076 Phase 5a shim: this file is reserved for Phase 5b migration; the
// global PixelPoint alias was retired in Phase 5a, so re-import it locally.
using PixelPoint = Mithril.MapCalibration.PixelPoint;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;

namespace Legolas.Rendering;

/// <summary>
/// <see cref="Mithril.Overlay.Internal.MarkerSceneRenderer"/> drawer for a
/// single Motherlode pin (#113 Layer 5). Per-pin lift of today's
/// <c>PinSceneRenderer.DrawMotherlodePins</c> branch, which itself just
/// calls the shared <c>DrawPin</c> per pin — no active-pin treatment exists
/// for Motherlode because there is no per-target "selected pin" identity.
/// </summary>
internal static class LegolasMotherlodeMarkerDrawer
{
    public static void Draw(
        LegolasMotherlodeMarkerStyle style,
        OverlayPixel pixel,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // #1076: Overlay-facing OverlayPixel; Core stays PixelPoint until PR 5.
        var pos = new PixelPoint(pixel.X, pixel.Y);
        LegolasMarkerDrawerCore.DrawPin(
            rt, factory, brushes, pos,
            style.Outer, style.Center, style.OuterDiameter);
    }
}
