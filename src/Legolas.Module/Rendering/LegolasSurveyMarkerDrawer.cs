using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;

namespace Legolas.Rendering;

/// <summary>
/// <see cref="Mithril.Overlay.Internal.MarkerSceneRenderer"/> drawer for a
/// single Survey pin. The dispatch model is "one marker — one drawer call",
/// so this is the per-pin lift of today's
/// <c>PinSceneRenderer.DrawSurveyPins</c> + <c>DrawActivePin</c> branches.
///
/// <para>The <c>PinScene</c>-level "active pin index" model has collapsed
/// into per-marker state on <see cref="LegolasSurveyMarkerStyle.ActiveTreatment"/>:
/// markers without a treatment draw a plain pin (the non-active branch in
/// the old loop); markers with a treatment draw the treatment + pin
/// (mirroring the old <c>DrawActivePin</c> call).</para>
///
/// <para>Drawing order between active/non-active pins (today: non-active
/// first, then active on top so the halo can't be occluded) is now the
/// caller's responsibility — <see cref="MarkerSceneRenderer.Render"/>
/// iterates markers in insertion order, so producers should add the active
/// marker last. Step 3 will be the producer-side change.</para>
/// </summary>
internal static class LegolasSurveyMarkerDrawer
{
    public static void Draw(
        LegolasSurveyMarkerStyle style,
        OverlayPixel pixel,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // #1076: the Mithril.Overlay-facing drawer signature is OverlayPixel,
        // but LegolasMarkerDrawerCore still takes PixelPoint (Phase 5 migrates
        // the Legolas internals). Convert at the boundary; same X/Y components.
        var pos = new PixelPoint(pixel.X, pixel.Y);
        if (style.ActiveTreatment is { } spec)
        {
            LegolasMarkerDrawerCore.DrawActivePin(
                rt, factory, brushes, pos,
                style.Outer, style.Center, style.OuterDiameter, spec);
        }
        else
        {
            LegolasMarkerDrawerCore.DrawPin(
                rt, factory, brushes, pos,
                style.Outer, style.Center, style.OuterDiameter);
        }
    }
}
