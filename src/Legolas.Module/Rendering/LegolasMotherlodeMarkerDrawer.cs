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
        LegolasMarkerDrawerCore.DrawPin(
            rt, factory, brushes, pixel,
            style.Outer, style.Center, style.OuterDiameter);
    }
}
