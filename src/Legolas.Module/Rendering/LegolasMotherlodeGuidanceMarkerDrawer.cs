using System.Numerics;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;

namespace Legolas.Rendering;

/// <summary>
/// <see cref="Mithril.Overlay.Internal.MarkerSceneRenderer"/> drawer for the
/// Motherlode guidance circle (#506). Per-marker lift of today's
/// <c>PinSceneRenderer.DrawMotherlodeGuidance</c> branch.
///
/// <para>The dashed ring + centre-tick cross are drawn as a single composite
/// marker — keeping them together preserves the painters'-algorithm layering
/// guarantee they had inside <c>PinSceneRenderer.Render</c> (where the
/// guidance was drawn after the Motherlode pins but before the player
/// anchor).</para>
/// </summary>
internal static class LegolasMotherlodeGuidanceMarkerDrawer
{
    public static void Draw(
        LegolasMotherlodeGuidanceMarkerStyle style,
        OverlayPixel pixel,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // #1076: Overlay-facing OverlayPixel; this drawer paints directly off
        // pixel.X/.Y (no Core dispatch), so no conversion is required.
        if (style.RadiusPixels <= 0) return;
        var stroke = brushes.Get(style.StrokeColor);
        if (stroke is null) return;

        // Dash pattern + thickness lifted verbatim from PinSceneRenderer's
        // DrawMotherlodeGuidance: 6/4 dash for both the ring and the centre
        // tick lines, 2px stroke.
        using var dashStyle = LegolasMarkerDrawerCore.CreateDashStyle(factory, new[] { 6f, 4f }, dashOffset: 0f);
        var cx = (float)pixel.X;
        var cy = (float)pixel.Y;
        var r = (float)style.RadiusPixels;
        var ellipse = new Ellipse(new Vector2(cx, cy), r, r);
        rt.DrawEllipse(ellipse, stroke, 2f, dashStyle);

        // Centre tick — visible even when the ring is large.
        const float tick = 5f;
        rt.DrawLine(new Vector2(cx - tick, cy), new Vector2(cx + tick, cy), stroke, 2f, dashStyle);
        rt.DrawLine(new Vector2(cx, cy - tick), new Vector2(cx, cy + tick), stroke, 2f, dashStyle);
    }
}
