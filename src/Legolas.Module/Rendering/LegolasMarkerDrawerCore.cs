using System.Numerics;
using Legolas.Domain;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Legolas.Rendering;

/// <summary>
/// Shared pure-D2D drawing primitives used by every Legolas marker drawer.
/// Lifted verbatim from <c>PinSceneRenderer</c>'s private helpers so the
/// snapshot byte-parity invariant holds &#8212; same dash patterns, same
/// stroke styles, same geometry. The helpers live here (not duplicated per
/// drawer) so a future tweak (e.g. step G's Direct2D Gaussian) lands in one
/// place.
///
/// <para>Step 6 of #835 will retire <c>PinSceneRenderer</c>; the corresponding
/// private helpers there can then be deleted because this static class is
/// the only caller.</para>
/// </summary>
internal static class LegolasMarkerDrawerCore
{
    /// <summary>
    /// Draw one composite pin (outer ring + centre indicator) centred on the
    /// given pixel. Mirrors <c>PinSceneRenderer.DrawPin</c>.
    /// </summary>
    public static void DrawPin(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinLayerStyle outer, PinLayerStyle center, double outerDiameter)
    {
        DrawPinLayer(rt, factory, brushes, pos, outerDiameter, outer);
        DrawPinLayer(rt, factory, brushes, pos, center.Size, center);
    }

    /// <summary>
    /// Active-pin treatment dispatch. Layered atop or replaces the plain pin
    /// per the resolved <see cref="ActivePinTreatmentSpec.Treatment"/>. Mirrors
    /// <c>PinSceneRenderer.DrawActivePin</c>.
    /// </summary>
    public static void DrawActivePin(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinLayerStyle outer, PinLayerStyle center, double outerDiameter,
        ActivePinTreatmentSpec spec)
    {
        switch (spec.Treatment)
        {
            case ActivePinTreatment.Halo:
                DrawHalo(rt, factory, brushes, pos, outer.Shape, outerDiameter, spec);
                DrawPin(rt, factory, brushes, pos, outer, center, outerDiameter);
                break;

            case ActivePinTreatment.Glow:
                DrawGlow(rt, factory, brushes, pos, outer.Shape, outerDiameter, spec);
                DrawPin(rt, factory, brushes, pos, outer, center, outerDiameter);
                break;

            case ActivePinTreatment.ScaleUp:
                // 1.5× scale matches a comfortable "noticeably bigger but not
                // overwhelming" jump for a 16px default pin (-> 24px). Both
                // outer diameter and centre size scale together so the
                // proportions stay right. Lifted from PinSceneRenderer.
                const double scale = 1.5;
                var scaledCenter = center with { Size = center.Size * scale };
                DrawPin(rt, factory, brushes, pos, outer, scaledCenter, outerDiameter * scale);
                break;

            case ActivePinTreatment.FillSwap:
                // Outer fill becomes the active colour. The default outer fill
                // is near-transparent (#01000000) so users get a vivid solid
                // disc; if they've already coloured the outer, the swap is
                // still visible because the colour changes outright.
                var swappedOuter = outer with { FillColor = spec.Color };
                DrawPin(rt, factory, brushes, pos, swappedOuter, center, outerDiameter);
                break;

            default:
                // A new ActivePinTreatment enum value would otherwise silently
                // render nothing — the caller's
                // `if (style.ActiveTreatment is { } spec)` routes here instead
                // of to DrawPin, so even the plain pin would be skipped. Fail
                // loud so unhandled values surface during dev rather than as
                // an invisible-pin field bug.
                //
                // TODO #835 step 6: PinSceneRenderer.DrawActivePin has NO
                // default arm today, so a new enum value silently no-ops
                // there. This is a deliberate behavioural divergence from
                // the byte-parity source (byte parity still holds for every
                // currently-defined enum value — they all hit the explicit
                // cases above). When step 6 retires PinSceneRenderer, the
                // divergence becomes moot.
                throw new ArgumentOutOfRangeException(
                    nameof(spec),
                    spec.Treatment,
                    "Unknown ActivePinTreatment — add a case to LegolasMarkerDrawerCore.DrawActivePin.");
        }
    }

    private static void DrawHalo(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinShape shape, double pinDiameter, ActivePinTreatmentSpec spec)
    {
        var haloDiameter = pinDiameter + 2 * spec.HaloPaddingPx;
        // Transparent fill so the halo is a ring, not a filled blob obscuring
        // the pin underneath.
        var haloStyle = new PinLayerStyle(
            Shape: shape,
            FillColor: System.Windows.Media.Color.FromArgb(0, 0, 0, 0),
            StrokeColor: spec.Color,
            StrokeStyle: PinStrokeStyle.Solid,
            StrokeThickness: spec.StrokeThickness,
            Size: 0);
        DrawPinLayer(rt, factory, brushes, pos, haloDiameter, haloStyle);
    }

    private static void DrawGlow(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinShape shape, double pinDiameter, ActivePinTreatmentSpec spec)
    {
        if (spec.GlowBlurRadius <= 0) return;
        // Multi-ring fake blur: 4 concentric filled rings with falling alpha,
        // approximates a soft Gaussian glow without setting up a Direct2D
        // effect chain (which needs ID2D1DeviceContext + a separate
        // intermediate bitmap). Step G can swap this for a real D2D Gaussian
        // if the difference is visible at extreme blur radii.
        const int rings = 4;
        var pinRadius = pinDiameter / 2.0;
        for (var i = 0; i < rings; i++)
        {
            // Outer rings are larger and dimmer; t goes 1 → 1/rings.
            var t = (rings - i) / (double)rings;
            var radius = pinRadius + spec.GlowBlurRadius * t;
            // Alpha ramp: nearest ring keeps ~50% of the configured colour
            // alpha, outermost ring fades almost to nothing.
            var alphaScale = 0.5 * (1 - (double)i / rings);
            var c = spec.Color;
            var glowColor = System.Windows.Media.Color.FromArgb(
                (byte)Math.Clamp(c.A * alphaScale, 0, 255), c.R, c.G, c.B);
            var glowStyle = new PinLayerStyle(
                Shape: shape,
                FillColor: glowColor,
                StrokeColor: System.Windows.Media.Color.FromArgb(0, 0, 0, 0),
                StrokeStyle: PinStrokeStyle.None,
                StrokeThickness: 0,
                Size: 0);
            DrawPinLayer(rt, factory, brushes, pos, radius * 2, glowStyle);
        }
    }

    /// <summary>
    /// Draw one shape (fill + optional stroke) centred on the given pixel
    /// with the given outer-bound diameter. Stroke is inset so the outer
    /// extent of the rendered shape matches <paramref name="diameter"/>,
    /// matching the WPF version's <c>Stretch="Uniform"</c> behaviour where
    /// the stroke fits inside the layout slot rather than overflowing it.
    /// Lifted from <c>PinSceneRenderer.DrawPinLayer</c>.
    /// </summary>
    public static void DrawPinLayer(
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes,
        OverlayPixel center,
        double diameter,
        PinLayerStyle style)
    {
        if (style.Shape == PinShape.None || diameter <= 0) return;

        var thickness = style.StrokeStyle == PinStrokeStyle.None ? 0 : (float)style.StrokeThickness;
        // Outer-bound diameter -> geometry size = diameter - thickness so
        // the half-stroke on each side stays within the layout slot.
        var geomSize = (float)(diameter - thickness);
        if (geomSize <= 0) return;
        var halfGeom = geomSize / 2f;
        var cx = (float)center.X;
        var cy = (float)center.Y;

        var fill = brushes.Get(style.FillColor);
        var stroke = thickness > 0 ? brushes.Get(style.StrokeColor) : null;

        ID2D1StrokeStyle? strokeStyle = null;
        if (stroke is not null && style.StrokeStyle == PinStrokeStyle.Dashed)
        {
            // Matches LegolasPinShapeStyle's "4 2" dash convention from the
            // legacy WPF PinShapeStyleToDashArrayConverter.
            strokeStyle = CreateDashStyle(factory, new[] { 4f, 2f }, dashOffset: 0f);
        }
        try
        {
            switch (style.Shape)
            {
                case PinShape.Circle:
                    var ellipse = new Ellipse(new Vector2(cx, cy), halfGeom, halfGeom);
                    if (fill is not null) rt.FillEllipse(ellipse, fill);
                    if (stroke is not null) rt.DrawEllipse(ellipse, stroke, thickness, strokeStyle);
                    break;

                case PinShape.Square:
                    var rect = new Rect(cx - halfGeom, cy - halfGeom, geomSize, geomSize);
                    if (fill is not null) rt.FillRectangle(rect, fill);
                    if (stroke is not null) rt.DrawRectangle(rect, stroke, thickness, strokeStyle);
                    break;

                case PinShape.Diamond:
                    using (var diamond = BuildDiamondGeometry(factory, cx, cy, halfGeom))
                    {
                        if (fill is not null) rt.FillGeometry(diamond, fill);
                        if (stroke is not null) rt.DrawGeometry(diamond, stroke, thickness, strokeStyle);
                    }
                    break;

                case PinShape.Cross:
                    // Cross is stroke-only; ignore fill.
                    if (stroke is not null)
                    {
                        rt.DrawLine(new Vector2(cx - halfGeom, cy), new Vector2(cx + halfGeom, cy), stroke, thickness, strokeStyle);
                        rt.DrawLine(new Vector2(cx, cy - halfGeom), new Vector2(cx, cy + halfGeom), stroke, thickness, strokeStyle);
                    }
                    break;
            }
        }
        finally
        {
            strokeStyle?.Dispose();
        }
    }

    /// <summary>
    /// Build a dashed stroke style. Lifted from <c>PinSceneRenderer.CreateDashStyle</c>.
    /// CapStyle.Flat keeps dash ends crisp at the configured length; Round would
    /// balloon them. LineJoin.Miter matches WPF's default polyline join.
    /// </summary>
    public static ID2D1StrokeStyle CreateDashStyle(ID2D1Factory factory, float[] dashes, float dashOffset)
    {
        var props = new StrokeStyleProperties
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Flat,
            LineJoin = LineJoin.Miter,
            MiterLimit = 10f,
            DashStyle = DashStyle.Custom,
            DashOffset = dashOffset,
        };
        return factory.CreateStrokeStyle(props, dashes);
    }

    private static ID2D1PathGeometry BuildDiamondGeometry(ID2D1Factory factory, float cx, float cy, float half)
    {
        var geom = factory.CreatePathGeometry();
        using var sink = geom.Open();
        sink.BeginFigure(new Vector2(cx, cy - half), FigureBegin.Filled);
        sink.AddLine(new Vector2(cx + half, cy));
        sink.AddLine(new Vector2(cx, cy + half));
        sink.AddLine(new Vector2(cx - half, cy));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geom;
    }
}
