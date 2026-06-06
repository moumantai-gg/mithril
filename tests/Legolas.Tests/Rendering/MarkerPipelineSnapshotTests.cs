// #1076 Phase 5a: P.3 audit found all Legolas test PixelPoint sites are
// overlay-frame, so this Rendering test file was migrated alongside the 5a
// scope (its PinScene/drawer dependencies now take OverlayPixel).
using FluentAssertions;
using Legolas.Domain;
using Legolas.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Color = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Tests.Rendering;

/// <summary>
/// End-to-end byte-parity tests for the #835 step 3/4/5 production wire-up.
/// Routes through the same pipeline production runs: producers call
/// <see cref="IWorldOverlayMarkers.AddMarker"/>; the projection driver
/// (<see cref="OverlayWindowService.ProjectMarkers"/>) reads
/// <see cref="WorldOverlayMarkers.CurrentAreaMarkers"/>, projects via
/// <see cref="IMapCalibrationService.WorldToOverlay"/>, and hands the
/// pixel+style list to <see cref="MarkerSceneRenderer.Render"/>.
///
/// <para><b>Why both this and <see cref="LegolasMarkerDrawerSnapshotTests"/>.</b>
/// PR #853's snapshot suite proves <c>MarkerSceneRenderer + Legolas drawers</c>
/// matches <c>PinSceneRenderer</c> byte-for-byte given the same pixel list.
/// This suite proves the production wire-up actually feeds that pixel list —
/// catching a switch that drops markers, mis-orders them, or otherwise breaks
/// the producer → registry → projection → render path. Without this, a
/// silently empty marker registry would still pass PR #853's tests.</para>
///
/// <para><b>Risk addressed (brief §"Risks I want you to specifically be alert to").</b>
/// "Byte-parity could pass trivially if both paths render nothing." Each
/// fixture asserts the projected pixel list is non-empty before rendering.</para>
///
/// <para><b>Identity calibration.</b> A fake <see cref="IMapCalibrationService"/>
/// maps world <c>(x, _, z)</c> → pixel <c>(x, z)</c> 1:1 so the test's world
/// coords double as the pin pixels and we can reuse PR #853's baselines verbatim.
/// The <see cref="OverlayWindowService.ProjectMarkers"/> overload used here is
/// the internal test-friendly variant (no miss-callback).</para>
/// </summary>
public sealed class MarkerPipelineSnapshotTests
{
    private const int CanvasWidth = 240;
    private const int CanvasHeight = 240;
    private const string AreaKey = "AreaTest";

    private const string SkipNoD3DPrefix =
        "No usable D3D11 driver (neither Hardware nor WARP). Inner driver error: ";

    private static readonly Color CyanFill = Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF);
    private static readonly Color WhiteStroke = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color GoldStroke = Color.FromArgb(0xFF, 0xFF, 0xD2, 0x3F);
    private static readonly Color Green = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);

    private static PinLayerStyle SurveyOuterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: Color.FromArgb(1, 0, 0, 0),
        StrokeColor: CyanFill,
        StrokeStyle: PinStrokeStyle.Dashed,
        StrokeThickness: 2.0,
        Size: 0);

    private static PinLayerStyle SurveyCenterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: CyanFill,
        StrokeColor: Color.FromArgb(0, 0, 0, 0),
        StrokeStyle: PinStrokeStyle.None,
        StrokeThickness: 0,
        Size: 5.0);

    private static PinLayerStyle PlayerOuterStyle() => new(
        Shape: PinShape.Circle,
        FillColor: Color.FromArgb(0, 0, 0, 0),
        StrokeColor: WhiteStroke,
        StrokeStyle: PinStrokeStyle.Solid,
        StrokeThickness: 2.0,
        Size: 18.0);

    private static PinLayerStyle PlayerCenterStyle() => new(
        Shape: PinShape.Square,
        FillColor: Green,
        StrokeColor: Color.FromArgb(0, 0, 0, 0),
        StrokeStyle: PinStrokeStyle.None,
        StrokeThickness: 0,
        Size: 2.0);

    // -----------------------------------------------------------------
    // Survey (#835 step 3)
    // -----------------------------------------------------------------

    [SkippableFact]
    public void Survey_default_through_marker_pipeline_matches_PinSceneRenderer_baseline()
    {
        var markers = new[]
        {
            (World: new WorldCoord(120, 0, 120),
             Style: (IMarkerStyle)new LegolasSurveyMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 24.0, ActiveTreatment: null)),
        };
        RunPipelineComparison("survey_default", markers);
    }

    [SkippableTheory]
    [InlineData(ActivePinTreatment.Halo)]
    [InlineData(ActivePinTreatment.Glow)]
    [InlineData(ActivePinTreatment.ScaleUp)]
    [InlineData(ActivePinTreatment.FillSwap)]
    public void Survey_active_through_marker_pipeline_matches_PinSceneRenderer_baseline(ActivePinTreatment treatment)
    {
        var spec = new ActivePinTreatmentSpec(
            Treatment: treatment,
            Color: WhiteStroke,
            HaloPaddingPx: 3.0,
            StrokeThickness: 2.0,
            GlowBlurRadius: 12.0);
        var markers = new[]
        {
            (World: new WorldCoord(120, 0, 120),
             Style: (IMarkerStyle)new LegolasSurveyMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 24.0, ActiveTreatment: spec)),
        };
        RunPipelineComparison(
            "survey_active_" + treatment.ToString().ToLowerInvariant(),
            markers);
    }

    [SkippableFact]
    public void Multi_marker_survey_through_marker_pipeline_matches_PinSceneRenderer_baseline()
    {
        var spec = new ActivePinTreatmentSpec(
            Treatment: ActivePinTreatment.Halo,
            Color: WhiteStroke,
            HaloPaddingPx: 3.0,
            StrokeThickness: 2.0,
            GlowBlurRadius: 12.0);

        // Insertion order MUST match PinSceneRenderer's active-last ordering
        // so the marker pipeline produces the same render. This is the
        // contract MapOverlayViewModel's remove-then-re-add active-treatment
        // handling depends on (see RefreshAllSurveyMarkers).
        var markers = new[]
        {
            (World: new WorldCoord(80, 0, 100),
             Style: (IMarkerStyle)new LegolasSurveyMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 24.0, ActiveTreatment: null)),
            (World: new WorldCoord(160, 0, 100),
             Style: (IMarkerStyle)new LegolasSurveyMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 24.0, ActiveTreatment: null)),
            (World: new WorldCoord(120, 0, 180),
             Style: (IMarkerStyle)new LegolasSurveyMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 24.0, ActiveTreatment: spec)),
        };
        RunPipelineComparison("multi_marker_survey", markers);
    }

    // -----------------------------------------------------------------
    // Motherlode (#835 step 4)
    // -----------------------------------------------------------------

    [SkippableFact]
    public void Motherlode_pin_through_marker_pipeline_matches_PinSceneRenderer_baseline()
    {
        var markers = new[]
        {
            (World: new WorldCoord(140, 0, 110),
             Style: (IMarkerStyle)new LegolasMotherlodeMarkerStyle(
                 SurveyOuterStyle(), SurveyCenterStyle(), 30.0)),
        };
        RunPipelineComparison("motherlode_pin", markers);
    }

    [SkippableFact]
    public void Motherlode_guidance_through_marker_pipeline_matches_PinSceneRenderer_baseline()
    {
        var markers = new[]
        {
            (World: new WorldCoord(120, 0, 120),
             Style: (IMarkerStyle)new LegolasMotherlodeGuidanceMarkerStyle(
                 RadiusPixels: 60.0, StrokeColor: GoldStroke)),
        };
        RunPipelineComparison("motherlode_guidance", markers);
    }

    // -----------------------------------------------------------------
    // Player anchor (covered by step 3's broader Survey switch but proven
    // through the pipeline here too for completeness)
    // -----------------------------------------------------------------

    [SkippableFact]
    public void Player_anchor_through_marker_pipeline_matches_PinSceneRenderer_baseline()
    {
        var markers = new[]
        {
            (World: new WorldCoord(120, 0, 120),
             Style: (IMarkerStyle)new LegolasPlayerMarkerStyle(
                 PlayerOuterStyle(), PlayerCenterStyle())),
        };
        RunPipelineComparison("player_anchor", markers);
    }

    // -----------------------------------------------------------------
    // Pipeline core
    // -----------------------------------------------------------------

    private static void RunPipelineComparison(
        string fixtureName,
        IReadOnlyList<(WorldCoord World, IMarkerStyle Style)> markers)
    {
        using var rt = HeadlessD2DRenderTarget.TryCreate(CanvasWidth, CanvasHeight, out var driverError);
        Skip.If(rt is null, SkipNoD3DPrefix + (driverError?.Message ?? "(unknown)"));

        // Drive the production wire-up: AddMarker → CurrentAreaMarkers →
        // ProjectMarkers → MarkerSceneRenderer.Render. Anything that drops
        // markers, mis-orders them, or projects to the wrong pixel breaks
        // byte parity here.
        var registry = new WorldOverlayMarkers(NullLogger.Instance) { CurrentArea = AreaKey };
        foreach (var (world, style) in markers)
        {
            registry.AddMarker(AreaKey, world.X, world.Z, style);
        }

        var snapshot = registry.CurrentAreaMarkers;
        snapshot.Should().HaveCount(markers.Count,
            "all registered markers must reach the snapshot — a producer that " +
            "silently drops markers would still pass PR #853's drawer-level " +
            "tests, so this assertion is the catch-net.");

        // mithril#1081: ProjectMarkers now takes a WorldToOverlayCalibration?
        // directly. MirrorNorth=true reproduces the old IdentityCalibrationService
        // mapping (world.X, world.Z) → OverlayPixel(world.X, world.Z) so
        // byte-parity baselines are preserved.
        var composed = new WorldToOverlayCalibration(
            OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: true);
        // mithril#1095: ProjectMarkers now takes a MapViewFix (not currentZoom).
        // Identity fix: ViewScale=1.0, PanTex=0 — maps canonical pixel 1:1 to overlay.
        var identityFix = new MapViewFix(PanTexPxX: 0, PanTexPxY: 0, ViewScale: 1.0,
            Confidence: 1.0, MeasuredAt: DateTimeOffset.UtcNow);
        var projected = OverlayWindowService.ProjectMarkers(
            snapshot, composed, identityFix);
        projected.Should().HaveCount(markers.Count,
            "the identity calibration must project every snapshot entry — " +
            "if it doesn't, the projection driver is dropping markers.");

        var renderer = new MarkerSceneRenderer();
        LegolasOverlayDrawerRegistrations.RegisterAll(renderer);

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt!.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        renderer.Render(projected, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        var pipelinePng = rt.EncodePng();

        var baselinePath = ResolveBaselinePath(fixtureName);
        System.IO.File.Exists(baselinePath).Should().BeTrue(
            "PR #853 must have checked in the baseline at " + baselinePath +
            " — re-run LegolasMarkerDrawerSnapshotTests with MITHRIL_REGEN_SNAPSHOTS=1 if missing.");
        var baselinePng = System.IO.File.ReadAllBytes(baselinePath);

        pipelinePng.Should().Equal(baselinePng,
            "the production wire-up (AddMarker → projection driver → MarkerSceneRenderer) " +
            "must reproduce PinSceneRenderer's output byte-for-byte. A diff means the " +
            "switch in MapOverlayViewModel / PinCalibrationCoordinator / MotherlodeFlowController " +
            "is dropping, mis-ordering, or mis-styling markers; fix the producer side, " +
            "do NOT re-baseline. The checked-in PNG at " + baselinePath + " is the contract.");
    }

    private static string ResolveBaselinePath(string fixtureName)
    {
        var dllDir = AppContext.BaseDirectory;
        var baselineDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(dllDir, "..", "..", "..", "Rendering", "Snapshots", "Baselines"));
        return System.IO.Path.Combine(baselineDir, fixtureName + ".png");
    }

}
