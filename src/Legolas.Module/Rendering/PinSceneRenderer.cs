// #1076 Phase 5a: PinScene now carries OverlayPixel fields, so PinSceneRenderer's
// private D2D helpers were migrated to take OverlayPixel directly. This file was
// originally tagged for Phase 5b but the Phase 4 drawer-bridging idiom did not
// extend to PinScene-driven rendering, so the migration landed here in 5a.
using System.Numerics;
using Legolas.Domain;
// #835: D2DBrushCache lifted to Mithril.Overlay.Internal — InternalsVisibleTo
// keeps it reachable from this file until Migration step 6 retires the whole
// Legolas-side renderer.
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;

namespace Legolas.Rendering;

/// <summary>
/// Pure draw logic for the D2D-rendered pin layer. Stateless static class —
/// every render reads from a <see cref="PinScene"/> snapshot, draws against
/// the supplied <see cref="ID2D1RenderTarget"/>, and uses a
/// <see cref="D2DBrushCache"/> to avoid per-call allocation. Stroke styles
/// are recreated each call; they're cheap factory-side objects in D2D and
/// optimising that comes in step G if it ever shows up in a profile.
///
/// Step C scope: route polyline (dashed) + active segment (dashed, thicker)
/// + bearing-uncertainty wedges. Pins, treatments, and the player anchor
/// land in steps D/E/F.
/// </summary>
internal static class PinSceneRenderer
{
    private const float RouteThickness = 2f;
    private const float ActiveSegmentThickness = 4f;
    private const float WedgeStrokeThickness = 1f;

    public static void Render(
        PinScene scene,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        // Wedges sit behind the route lines — drawing order matters; D2D has
        // no depth buffer for transparent geometry, so painters' algorithm.
        DrawWedges(scene, rt, factory, brushes);
        DrawRoute(scene, rt, factory, brushes);
        DrawActiveSegment(scene, rt, factory, brushes);
        DrawSurveyPins(scene, rt, factory, brushes);
        DrawMotherlodePins(scene, rt, factory, brushes);
        DrawMotherlodeGuidance(scene, rt, factory, brushes);
        DrawPlayerAnchor(scene, rt, factory, brushes);
        // #495: the calibration-validation reference markers are no longer
        // drawn here — they moved to a WPF ItemsControl layered over this
        // surface so each can carry a readable, decluttered label (D2D has no
        // DirectWrite). WPF draws above the D2D surface, preserving the
        // "markers never occluded by a real pin" property.
    }

    // #113 Layer 5: solved-treasure markers. Survey and Motherlode modes are
    // mutually exclusive, so reusing the survey pin style keeps the marker
    // theme-consistent without threading a second style through the cache.
    // No active-pin treatment (no per-target identity to single out).
    private static void DrawMotherlodePins(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        for (var i = 0; i < scene.MotherlodePins.Count; i++)
            DrawPin(rt, factory, brushes, scene.MotherlodePins[i],
                scene.SurveyOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
    }

    private static void DrawMotherlodeGuidance(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.MotherlodeGuidance is not { } g || g.RadiusPixels <= 0) return;
        var stroke = brushes.Get(g.StrokeColor);
        if (stroke is null) return;

        using var dashStyle = CreateDashStyle(factory, new[] { 6f, 4f }, dashOffset: 0f);
        var cx = (float)g.Center.X;
        var cy = (float)g.Center.Y;
        var r = (float)g.RadiusPixels;
        var ellipse = new Ellipse(new Vector2(cx, cy), r, r);
        rt.DrawEllipse(ellipse, stroke, 2f, dashStyle);

        // Centre tick — visible even when the ring is large.
        const float tick = 5f;
        rt.DrawLine(new Vector2(cx - tick, cy), new Vector2(cx + tick, cy), stroke, 2f, dashStyle);
        rt.DrawLine(new Vector2(cx, cy - tick), new Vector2(cx, cy + tick), stroke, 2f, dashStyle);
    }

    private static void DrawPlayerAnchor(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.PlayerPosition is not { } pos) return;
        // Player pin's outer Size is meaningful (drives the visible diameter)
        // unlike survey pins where outer size comes from SurveyPinRadiusMetres.
        // See LegolasPinStyle.PlayerDefaults() for the rationale.
        DrawPin(rt, factory, brushes, pos, scene.PlayerOuter, scene.PlayerCenter, scene.PlayerOuter.Size);
    }

    private static void DrawWedges(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.Wedges.Count == 0) return;
        var fill = brushes.Get(scene.WedgeFillColor);
        var stroke = brushes.Get(scene.WedgeStrokeColor);
        if (fill is null || stroke is null) return;

        foreach (var wedge in scene.Wedges)
        {
            using var path = BuildWedgeGeometry(factory, wedge);
            rt.FillGeometry(path, fill);
            rt.DrawGeometry(path, stroke, WedgeStrokeThickness);
        }
    }

    private static void DrawRoute(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.RoutePoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

        // Matches the WPF version's StrokeDashArray="4 2" on the static route polyline.
        using var dashStyle = CreateDashStyle(factory, new[] { 4f, 2f }, dashOffset: 0f);
        DrawPolyline(rt, scene.RoutePoints, brush, RouteThickness, dashStyle);
    }

    private static void DrawActiveSegment(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.ActiveSegmentPoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

        // Matches the WPF Storyboard: StrokeDashArray="6 3", animating
        // StrokeDashOffset 0 → -9 over 0.6s. The clock advance lives in
        // step F's MarchingAntsClock; for step C we accept whatever value
        // the scene carries (default 0 = static dashes).
        using var dashStyle = CreateDashStyle(factory, new[] { 6f, 3f }, dashOffset: (float)scene.ActiveSegmentDashOffset);
        DrawPolyline(rt, scene.ActiveSegmentPoints, brush, ActiveSegmentThickness, dashStyle);
    }

    private static void DrawPolyline(
        ID2D1RenderTarget rt,
        IReadOnlyList<OverlayPixel> points,
        ID2D1SolidColorBrush brush,
        float thickness,
        ID2D1StrokeStyle? style)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            rt.DrawLine(
                new Vector2((float)a.X, (float)a.Y),
                new Vector2((float)b.X, (float)b.Y),
                brush, thickness, style);
        }
    }

    private static ID2D1StrokeStyle CreateDashStyle(ID2D1Factory factory, float[] dashes, float dashOffset)
    {
        // CapStyle.Flat keeps dash ends crisp at the configured length; Round
        // would balloon them. LineJoin.Miter matches WPF's default polyline join.
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

    private static void DrawSurveyPins(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes)
    {
        if (scene.SurveyPins.Count == 0) return;
        // Render non-active pins first; the active pin (if any) layers its
        // treatment on top so glow halos can't be occluded by neighbouring
        // pins. Halo + Glow add visuals around the normal pin draw; ScaleUp
        // and FillSwap replace it.
        for (var i = 0; i < scene.SurveyPins.Count; i++)
        {
            if (i == scene.ActivePinIndex) continue;
            DrawPin(rt, factory, brushes, scene.SurveyPins[i],
                scene.SurveyOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
        }

        if (scene.ActivePinIndex is { } idx
            && idx >= 0 && idx < scene.SurveyPins.Count
            && scene.ActiveTreatment is { } spec)
        {
            DrawActivePin(rt, factory, brushes, scene.SurveyPins[idx], scene, spec);
        }
    }

    private static void DrawPin(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinLayerStyle outer, PinLayerStyle center, double diameter)
    {
        DrawPinLayer(rt, factory, brushes, pos, diameter, outer);
        DrawPinLayer(rt, factory, brushes, pos, center.Size, center);
    }

    private static void DrawActivePin(
        ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes,
        OverlayPixel pos, PinScene scene, ActivePinTreatmentSpec spec)
    {
        switch (spec.Treatment)
        {
            case ActivePinTreatment.Halo:
                DrawHalo(rt, factory, brushes, pos, scene.SurveyOuter.Shape, scene.SurveyOuterDiameter, spec);
                DrawPin(rt, factory, brushes, pos, scene.SurveyOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
                break;

            case ActivePinTreatment.Glow:
                DrawGlow(rt, factory, brushes, pos, scene.SurveyOuter.Shape, scene.SurveyOuterDiameter, spec);
                DrawPin(rt, factory, brushes, pos, scene.SurveyOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
                break;

            case ActivePinTreatment.ScaleUp:
                // 1.5× scale matches a comfortable "noticeably bigger but not
                // overwhelming" jump for a 16px default pin (-> 24px). Both
                // outer diameter and centre size scale together so the
                // proportions stay right.
                const double scale = 1.5;
                var scaledCenter = scene.SurveyCenter with { Size = scene.SurveyCenter.Size * scale };
                DrawPin(rt, factory, brushes, pos,
                    scene.SurveyOuter, scaledCenter, scene.SurveyOuterDiameter * scale);
                break;

            case ActivePinTreatment.FillSwap:
                // Outer fill becomes the active colour. The default outer fill
                // is near-transparent (#01000000) so users get a vivid solid
                // disc; if they've already coloured the outer, the swap is
                // still visible because the colour changes outright.
                var swappedOuter = scene.SurveyOuter with { FillColor = spec.Color };
                DrawPin(rt, factory, brushes, pos, swappedOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
                break;
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
    /// </summary>
    private static void DrawPinLayer(
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

    private static ID2D1PathGeometry BuildWedgeGeometry(ID2D1Factory factory, WedgeArc wedge)
    {
        var geom = factory.CreatePathGeometry();
        using var sink = geom.Open();
        var bMin = wedge.BearingRadians - wedge.HalfAngleRadians;
        var bMax = wedge.BearingRadians + wedge.HalfAngleRadians;
        var origin = new Vector2((float)wedge.Origin.X, (float)wedge.Origin.Y);
        var pStart = new Vector2(
            (float)(wedge.Origin.X + wedge.DistancePx * Math.Sin(bMin)),
            (float)(wedge.Origin.Y - wedge.DistancePx * Math.Cos(bMin)));
        var pEnd = new Vector2(
            (float)(wedge.Origin.X + wedge.DistancePx * Math.Sin(bMax)),
            (float)(wedge.Origin.Y - wedge.DistancePx * Math.Cos(bMax)));

        sink.BeginFigure(origin, FigureBegin.Filled);
        sink.AddLine(pStart);
        sink.AddArc(new ArcSegment
        {
            Point = pEnd,
            Size = new Size((float)wedge.DistancePx, (float)wedge.DistancePx),
            RotationAngle = 0,
            SweepDirection = SweepDirection.Clockwise,
            ArcSize = ArcSize.Small,
        });
        sink.AddLine(origin);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geom;
    }
}
