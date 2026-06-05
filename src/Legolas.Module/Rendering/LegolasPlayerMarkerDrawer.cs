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
        LegolasMarkerDrawerCore.DrawPin(
            rt, factory, brushes, pixel,
            style.Outer, style.Center, style.Outer.Size);
    }
}
