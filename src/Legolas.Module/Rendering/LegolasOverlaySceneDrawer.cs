// #1076 Phase 5a: PinScene's pixel fields became OverlayPixel; this file's
// private D2D helpers were migrated to take OverlayPixel directly. Originally
// tagged for Phase 5b but minimum-touch keep-it-building forced the rename
// to land here in 5a — the helpers are frame-blind D2D rasterisation, so
// the rename is cosmetic; mouse-event boundaries still live in 5b.
using System.Numerics;
using Legolas.Domain;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;

namespace Legolas.Rendering;

/// <summary>
/// Legolas's overlay scene drawer (#835 step 6). Near-verbatim relocation of
/// the body of <see cref="PinSceneRenderer.Render"/> so the same wedges +
/// route polyline + active segment + survey pins + motherlode pins +
/// motherlode guidance ring + player anchor are drawn against the shared
/// <see cref="IOverlayWindow"/>'s surface via the
/// <see cref="IOverlaySceneContext"/> handed in per tick.
///
/// <para><b>Scope.</b> Per the dissolved-#866/#867/#868 decisions on the
/// platform reframe, this drawer is responsible for Legolas's <em>full
/// scene</em>: route polylines, pixel nudges, world-projected pins,
/// guidance ring, player anchor, calibration-validation ghosts, and
/// calibration placement pins. Calibration placement pins are drawn
/// pixel-native by <see cref="DrawCalibrationPlacementPins"/> per the
/// dissolved-#868 framing ("no seed-calibration requirement"); the
/// registry-side <c>LegolasCalibrationMarkerDrawer</c> is unregistered
/// from <see cref="MarkerSceneRenderer"/> as part of this step.</para>
///
/// <para><b>Coordinate handling.</b> World geometry uses
/// <see cref="IOverlaySceneContext.Project"/> (zoom-aware); pixel-native
/// data (route polyline points, motherlode guidance, player anchor,
/// already-projected survey pin pixels stored on the survey model) uses
/// pixels directly. The <see cref="PinScene"/> intermediate model already
/// caches the per-survey projected pixel, so this drawer feeds the
/// existing per-frame path: it builds the scene from the live
/// <see cref="MapOverlayViewModel"/> + <see cref="MarchingAntsClock"/> and
/// hands it off to the shared
/// <see cref="DrawScene(PinScene, ID2D1RenderTarget, ID2D1Factory, IOverlayBrushes)"/>
/// helper below (the relocated bodies of the old static
/// <see cref="PinSceneRenderer"/>).</para>
///
/// <para><b>Threading.</b> The shared overlay invokes
/// <see cref="Draw(IOverlaySceneContext)"/> on the WPF dispatcher inside
/// the surface's <c>BeginDraw</c>/<c>EndDraw</c> pair. The drawer reads
/// the VM's observable properties directly &#8212; same threading
/// contract as today's <c>MapOverlayView.OnMapSurfaceRender</c>.</para>
/// </summary>
internal sealed class LegolasOverlaySceneDrawer
{
    private const float RouteThickness = 2f;
    private const float ActiveSegmentThickness = 4f;
    private const float WedgeStrokeThickness = 1f;

    private readonly MapOverlayViewModel _vm;
    private readonly MarchingAntsClock _antsClock = new();

    public LegolasOverlaySceneDrawer(MapOverlayViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>The scene-drawer callback registered via
    /// <see cref="IOverlayWindow.RegisterScene"/>. Builds a fresh
    /// <see cref="PinScene"/> from the VM each tick (same data flow as the
    /// legacy <c>MapOverlayView.OnMapSurfaceRender</c>) and hands it to
    /// <see cref="DrawScene"/>. Calibration-validation ghost markers
    /// (#495, review iteration-1 B3) are drawn after the main scene as a
    /// separate pass &#8212; they're pixel-native (the VM projects them
    /// through the calibration) and out-of-band from the
    /// <see cref="PinScene"/> contract that the snapshot baselines pin.</summary>
    public void Draw(IOverlaySceneContext ctx)
    {
        var scene = BuildScene();
        DrawScene(scene, ctx.RenderTarget, ctx.Factory, ctx.Brushes);
        DrawCalibrationGhosts(ctx);
        DrawCalibrationPlacementPins(ctx);
    }

    /// <summary>Render in-flight calibration placement pins pixel-native
    /// (per dissolved-#868 framing on #835): each
    /// <see cref="MapOverlayViewModel.CalibrationMarkers"/> entry draws a
    /// center dot at the click pixel, with an outer ring layered over the
    /// selected marker only — matching the legacy
    /// <c>MapOverlayView.xaml</c> <c>CalibrationMarkers</c>
    /// <c>ItemsControl</c> contract (zero-size Canvas + selection-ring
    /// visibility binding). Gated on
    /// <see cref="MapOverlayViewModel.IsCalibrationCapturing"/> so the
    /// pass is a no-op outside the Drop/Pair walkthrough. Reuses the
    /// same <see cref="DrawPinLayer"/> helper as the survey/motherlode/
    /// player pin drawers, so shape (Circle/Square/Diamond/Cross),
    /// stroke style, and fill are all honored from
    /// <see cref="MapOverlayViewModel.CalibrationPinStyle"/>.</summary>
    private void DrawCalibrationPlacementPins(IOverlaySceneContext ctx)
    {
        var vm = _vm;
        if (!vm.IsCalibrationCapturing) return;
        var markers = vm.CalibrationMarkers;
        if (markers is null || markers.Count == 0) return;

        var pinStyle = vm.CalibrationPinStyle;
        var outerStyle = BuildPlacementPinLayer(pinStyle.Outer);
        var centerStyle = BuildPlacementPinLayer(pinStyle.Center);

        for (var i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            // Center dot — always drawn (matches the legacy "always-on dot"
            // Path element in the placement ItemsControl).
            DrawPinLayer(ctx.RenderTarget, ctx.Factory, ctx.Brushes, m.Pixel, centerStyle.Size, centerStyle);
            // Outer ring — selected marker only (matches the legacy
            // Visibility=IsSelected binding on the outer Path element).
            if (m.IsSelected)
                DrawPinLayer(ctx.RenderTarget, ctx.Factory, ctx.Brushes, m.Pixel, outerStyle.Size, outerStyle);
        }
    }

    private static PinLayerStyle BuildPlacementPinLayer(LegolasPinShapeStyle s) =>
        new PinLayerStyle(
            Shape: s.Shape,
            FillColor: ParseColor(s.FillColor),
            StrokeColor: ParseColor(s.StrokeColor),
            StrokeStyle: s.StrokeStyle,
            StrokeThickness: s.StrokeThickness,
            Size: s.Size);

    /// <summary>Test seam (#835 step 6 iter-1 touch-up B placement pins):
    /// drive the placement-pin pass against a supplied context exactly as
    /// <see cref="Draw"/> would. Lets future unit tests assert the
    /// draw-path-entered/short-circuited contract without standing up a
    /// real D2D render target. Mirrors
    /// <see cref="DrawCalibrationGhostsForTest"/>.</summary>
    internal void DrawCalibrationPlacementPinsForTest(IOverlaySceneContext ctx) => DrawCalibrationPlacementPins(ctx);

    /// <summary>Render the #495 calibration-validation ghost markers as a
    /// magenta hollow ring + a magenta center dot per
    /// <see cref="MapOverlayViewModel.CalibrationGhosts"/> entry. Labels
    /// are deferred (review iteration-1 B3 accepted dots-only ship; #875
    /// tracks the D2D-text lift via <c>ID2D1DeviceContext</c> +
    /// <c>IDWriteFactory</c>). Skips silently when the user hasn't toggled
    /// the validation panel.</summary>
    private void DrawCalibrationGhosts(IOverlaySceneContext ctx)
    {
        var vm = _vm;
        if (!vm.ShowCalibrationGhosts) return;
        var ghosts = vm.CalibrationGhosts;
        if (ghosts.Count == 0) return;

        var stroke = ctx.Brushes.Get(GhostStrokeColor);
        var fill = ctx.Brushes.Get(GhostStrokeColor);
        if (stroke is null || fill is null) return;

        for (var i = 0; i < ghosts.Count; i++)
        {
            var g = ghosts[i];
            var cx = (float)g.Pixel.X;
            var cy = (float)g.Pixel.Y;
            // Outer hollow ring — matches the legacy XAML's
            // <Ellipse Stroke="#FFE040E0" StrokeThickness="2" Width="16" Height="16">.
            var outer = new Ellipse(new System.Numerics.Vector2(cx, cy), 8f, 8f);
            ctx.RenderTarget.DrawEllipse(outer, stroke, 2f);
            // Center dot — matches <Ellipse Fill="#FFE040E0" Width="4" Height="4">.
            var center = new Ellipse(new System.Numerics.Vector2(cx, cy), 2f, 2f);
            ctx.RenderTarget.FillEllipse(center, fill);
        }
        // TODO(#875): #495 ghost-label re-add. D2D text needs
        // ID2D1DeviceContext + IDWriteFactory which the current surface
        // doesn't expose; dots-only ships this iteration (alignment
        // validation works without labels). #875 also tracks the
        // validation-status banner deferred to the step-7 chrome lift.
    }

    private static readonly System.Windows.Media.Color GhostStrokeColor =
        System.Windows.Media.Color.FromArgb(0xFF, 0xE0, 0x40, 0xE0);

    /// <summary>Test seam: build the per-tick PinScene from the VM exactly
    /// as <see cref="Draw"/> would. Lets the
    /// <c>MapOverlayMarkerRegistrationOrderTests</c> assert that the
    /// scene-drawer's enumeration of <c>SessionState.Surveys</c> + the
    /// renderer's "active pin last" path replaces the previous registry's
    /// "active-last insertion order" invariant.</summary>
    internal PinScene BuildSceneForTest() => BuildScene();

    /// <summary>Test seam (review iteration-1 B3): drive the calibration-
    /// ghost pass against a supplied context exactly as <see cref="Draw"/>
    /// would. Lets <c>LegolasOverlaySceneDrawerGhostTests</c> assert the
    /// draw path is entered (brush fetched → ellipses drawn) when ghosts
    /// are populated + <see cref="MapOverlayViewModel.ShowCalibrationGhosts"/>
    /// is on, and short-circuited (no brush fetch, no draw) when off or
    /// empty — without standing up a real D2D render target.</summary>
    internal void DrawCalibrationGhostsForTest(IOverlaySceneContext ctx) => DrawCalibrationGhosts(ctx);

    private PinScene BuildScene()
    {
        var vm = _vm;
        var wedges = new List<WedgeArc>(vm.Surveys.Count);
        var pins = new List<OverlayPixel>(vm.Surveys.Count);
        var selected = vm.Session.SelectedSurvey;
        var listening = vm.IsListening;
        int? activeIndex = null;
        foreach (var s in vm.Surveys)
        {
            if (s.WedgeArc is { } arc) wedges.Add(arc);
            if (s.IsVisible)
            {
                if (listening && ReferenceEquals(s, selected))
                    activeIndex = pins.Count;
                pins.Add(s.EffectivePixel!.Value);
            }
        }

        ActivePinTreatmentSpec? activeSpec = null;
        if (activeIndex.HasValue)
        {
            var aps = vm.ActivePinStyle;
            activeSpec = new ActivePinTreatmentSpec(
                Treatment: aps.Treatment,
                Color: ParseColor(aps.Color),
                HaloPaddingPx: aps.HaloPaddingPx,
                StrokeThickness: aps.HaloThickness,
                GlowBlurRadius: aps.GlowBlurRadius);
        }

        var pinStyle = vm.PinStyle;
        var outerStyle = new PinLayerStyle(
            Shape: pinStyle.Outer.Shape,
            FillColor: ParseColor(pinStyle.Outer.FillColor),
            StrokeColor: ParseColor(pinStyle.Outer.StrokeColor),
            StrokeStyle: pinStyle.Outer.StrokeStyle,
            StrokeThickness: pinStyle.Outer.StrokeThickness,
            Size: 0);
        var centerStyle = new PinLayerStyle(
            Shape: pinStyle.Center.Shape,
            FillColor: ParseColor(pinStyle.Center.FillColor),
            StrokeColor: ParseColor(pinStyle.Center.StrokeColor),
            StrokeStyle: pinStyle.Center.StrokeStyle,
            StrokeThickness: pinStyle.Center.StrokeThickness,
            Size: pinStyle.Center.Size);

        var playerStyle = vm.PlayerPinStyle;
        var playerOuterStyle = new PinLayerStyle(
            Shape: playerStyle.Outer.Shape,
            FillColor: ParseColor(playerStyle.Outer.FillColor),
            StrokeColor: ParseColor(playerStyle.Outer.StrokeColor),
            StrokeStyle: playerStyle.Outer.StrokeStyle,
            StrokeThickness: playerStyle.Outer.StrokeThickness,
            Size: playerStyle.Outer.Size);
        var playerCenterStyle = new PinLayerStyle(
            Shape: playerStyle.Center.Shape,
            FillColor: ParseColor(playerStyle.Center.FillColor),
            StrokeColor: ParseColor(playerStyle.Center.StrokeColor),
            StrokeStyle: playerStyle.Center.StrokeStyle,
            StrokeThickness: playerStyle.Center.StrokeThickness,
            Size: playerStyle.Center.Size);

        return new PinScene(
            RoutePoints: vm.RoutePoints,
            ActiveSegmentPoints: vm.ActiveSegmentPoints,
            Wedges: wedges,
            SurveyPins: pins,
            MotherlodePins: vm.MotherlodeMarkerPixels,
            MotherlodeGuidance: vm.MotherlodeGuidanceOverlay,
            ActivePinIndex: activeIndex,
            ActiveTreatment: activeSpec,
            SurveyOuter: outerStyle,
            SurveyCenter: centerStyle,
            SurveyOuterDiameter: vm.PinDiameter,
            PlayerPosition: vm.PlayerMarkerPixel,
            PlayerOuter: playerOuterStyle,
            PlayerCenter: playerCenterStyle,
            RouteLineColor: vm.Brushes.RouteLine.Color,
            WedgeFillColor: vm.Brushes.BearingWedgeFill.Color,
            WedgeStrokeColor: vm.Brushes.BearingWedgeStroke.Color,
            ActiveSegmentDashOffset: _antsClock.Advance());
    }

    private static System.Windows.Media.Color ParseColor(string hex) => LegolasBrushes.Parse(hex);

    // ============================================================
    // Below: relocated PinSceneRenderer body. Near-verbatim from
    // src/Legolas.Module/Rendering/PinSceneRenderer.cs (#835 step 2).
    // Step 7 deletes the original; the snapshot baselines keep the
    // legacy renderer as the byte-parity reference until then.
    // ============================================================

    internal static void DrawScene(
        PinScene scene,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        IOverlayBrushes brushes)
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
    }

    private static void DrawMotherlodePins(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
    {
        for (var i = 0; i < scene.MotherlodePins.Count; i++)
            DrawPin(rt, factory, brushes, scene.MotherlodePins[i],
                scene.SurveyOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
    }

    private static void DrawMotherlodeGuidance(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
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

        const float tick = 5f;
        rt.DrawLine(new Vector2(cx - tick, cy), new Vector2(cx + tick, cy), stroke, 2f, dashStyle);
        rt.DrawLine(new Vector2(cx, cy - tick), new Vector2(cx, cy + tick), stroke, 2f, dashStyle);
    }

    private static void DrawPlayerAnchor(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
    {
        if (scene.PlayerPosition is not { } pos) return;
        DrawPin(rt, factory, brushes, pos, scene.PlayerOuter, scene.PlayerCenter, scene.PlayerOuter.Size);
    }

    private static void DrawWedges(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
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

    private static void DrawRoute(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
    {
        if (scene.RoutePoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

        using var dashStyle = CreateDashStyle(factory, new[] { 4f, 2f }, dashOffset: 0f);
        DrawPolyline(rt, scene.RoutePoints, brush, RouteThickness, dashStyle);
    }

    private static void DrawActiveSegment(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
    {
        if (scene.ActiveSegmentPoints.Count < 2) return;
        var brush = brushes.Get(scene.RouteLineColor);
        if (brush is null) return;

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

    private static void DrawSurveyPins(PinScene scene, ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes)
    {
        if (scene.SurveyPins.Count == 0) return;
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
        ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes,
        OverlayPixel pos, PinLayerStyle outer, PinLayerStyle center, double diameter)
    {
        DrawPinLayer(rt, factory, brushes, pos, diameter, outer);
        DrawPinLayer(rt, factory, brushes, pos, center.Size, center);
    }

    private static void DrawActivePin(
        ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes,
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
                const double scale = 1.5;
                var scaledCenter = scene.SurveyCenter with { Size = scene.SurveyCenter.Size * scale };
                DrawPin(rt, factory, brushes, pos,
                    scene.SurveyOuter, scaledCenter, scene.SurveyOuterDiameter * scale);
                break;

            case ActivePinTreatment.FillSwap:
                var swappedOuter = scene.SurveyOuter with { FillColor = spec.Color };
                DrawPin(rt, factory, brushes, pos, swappedOuter, scene.SurveyCenter, scene.SurveyOuterDiameter);
                break;
        }
    }

    private static void DrawHalo(
        ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes,
        OverlayPixel pos, PinShape shape, double pinDiameter, ActivePinTreatmentSpec spec)
    {
        var haloDiameter = pinDiameter + 2 * spec.HaloPaddingPx;
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
        ID2D1RenderTarget rt, ID2D1Factory factory, IOverlayBrushes brushes,
        OverlayPixel pos, PinShape shape, double pinDiameter, ActivePinTreatmentSpec spec)
    {
        if (spec.GlowBlurRadius <= 0) return;
        const int rings = 4;
        var pinRadius = pinDiameter / 2.0;
        for (var i = 0; i < rings; i++)
        {
            var t = (rings - i) / (double)rings;
            var radius = pinRadius + spec.GlowBlurRadius * t;
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

    private static void DrawPinLayer(
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        IOverlayBrushes brushes,
        OverlayPixel center,
        double diameter,
        PinLayerStyle style)
    {
        if (style.Shape == PinShape.None || diameter <= 0) return;

        var thickness = style.StrokeStyle == PinStrokeStyle.None ? 0 : (float)style.StrokeThickness;
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
