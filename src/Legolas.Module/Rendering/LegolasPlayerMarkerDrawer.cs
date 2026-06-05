// #1076 Phase 5a shim: this file is reserved for Phase 5b migration; the
// global PixelPoint alias was retired in Phase 5a, so re-import it locally.
using PixelPoint = Mithril.MapCalibration.PixelPoint;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;

namespace Legolas.Rendering;

/// <summary>
/// <see cref="Mithril.Overlay.Internal.MarkerSceneRenderer"/> drawer for the
/// player anchor pin. Per-marker lift of today's
/// <c>PinSceneRenderer.DrawPlayerAnchor</c> branch.
///
/// <para>Unlike Survey pins, the outer layer's <see cref="PinLayerStyle.Size"/>
/// is the visible diameter — see <c>LegolasPinStyle.PlayerDefaults()</c> for
/// the rationale. So the drawer passes <c>style.Outer.Size</c> as the outer
/// diameter, not a separate field on the style.</para>
/// </summary>
internal static class LegolasPlayerMarkerDrawer
{
    public static void Draw(
        LegolasPlayerMarkerStyle style,
        OverlayPixel pixel,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // #1076: Overlay-facing OverlayPixel; Core stays PixelPoint until PR 5.
        var pos = new PixelPoint(pixel.X, pixel.Y);
        LegolasMarkerDrawerCore.DrawPin(
            rt, factory, brushes, pos,
            style.Outer, style.Center, style.Outer.Size);
    }
}
