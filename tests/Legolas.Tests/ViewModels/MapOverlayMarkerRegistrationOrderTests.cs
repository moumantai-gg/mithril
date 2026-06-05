using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Rendering;
using Legolas.Services;
using Legolas.Tests.Rendering;
using Legolas.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// Producer-side iteration-2 regression test (#835 review iteration 2 — B3),
/// repurposed for #835 step 6.
///
/// <para><b>Step 6 cutover.</b> Survey pins are no longer routed through
/// <see cref="WorldOverlayMarkers"/>; they're drawn by
/// <see cref="LegolasOverlaySceneDrawer"/> via the freeform scene-hook
/// callback registered with <see cref="IOverlayWindow.RegisterScene"/>.
/// The active-last invariant moves with the drawer: <c>BuildScene</c>
/// enumerates <see cref="SessionState.Surveys"/> in source order, records
/// the active survey's index via <see cref="PinScene.ActivePinIndex"/>,
/// and <see cref="LegolasOverlaySceneDrawer.DrawScene"/> renders that
/// active pin LAST (with its halo on top) by iterating SurveyPins,
/// skipping the active index, then drawing the active pin at the end.</para>
///
/// <para><b>What this asserts beyond the rest of the snapshot suite.</b>
/// <see cref="Legolas.Tests.Rendering.MarkerPipelineSnapshotTests"/> calls
/// the registry directly with hand-crafted styles and explicit insertion
/// order — it doesn't exercise the <see cref="MapOverlayViewModel"/> or
/// <see cref="LegolasOverlaySceneDrawer"/> producer at all. A bug in
/// <c>BuildScene</c> that records the wrong ActivePinIndex (e.g. one off
/// the visibility filter) would pass every other test in the suite —
/// <c>DrawScene</c> iterates whatever ActivePinIndex the scene carries,
/// and the hand-crafted tests build the right one by hand.</para>
///
/// <para>The test constructs a real <see cref="MapOverlayViewModel"/> +
/// <see cref="LegolasOverlaySceneDrawer"/>, seeds three surveys (active
/// at the middle index 1, deliberately not the last source-order index),
/// builds the scene, and asserts both
/// <list type="number">
/// <item>the scene's <c>ActivePinIndex</c> points at the active survey's
/// position in <c>SurveyPins</c> (the invariant the renderer's
/// "active halo on top" contract depends on), and</item>
/// <item>the render of the scene byte-matches PR #853's checked-in
/// <c>multi_marker_survey</c> baseline — proving the end-to-end
/// production path (settings → BuildScene → DrawScene) produces the
/// same bytes as the hand-crafted control.</item>
/// </list></para>
/// </summary>
public sealed class MapOverlayMarkerRegistrationOrderTests
{
    private const int CanvasWidth = 240;
    private const int CanvasHeight = 240;
    private const string AreaKey = "AreaTest";
    private const string SkipNoD3DPrefix =
        "No usable D3D11 driver (neither Hardware nor WARP). Inner driver error: ";

    [Fact]
    public void BuildScene_places_active_pin_at_its_source_index_in_SurveyPins()
    {
        var (_, _, selected, _, drawer, session) = BuildSutWithThreeSurveys();

        // Sanity: middle survey is the one selected. If ActivePinIndex
        // points to the source-order-last (index 2) instead of the
        // selected (source index 1), the active-last invariant is broken.
        session.SelectedSurvey = selected;

        var scene = drawer.BuildSceneForTest();
        scene.SurveyPins.Should().HaveCount(3,
            "all three seeded surveys must reach the rendered pin list — a producer that " +
            "silently drops the selected/active one would let this fail and the ordering " +
            "bug hide behind the drop.");

        scene.ActivePinIndex.Should().Be(1,
            "PinSceneRenderer/LegolasOverlaySceneDrawer.DrawSurveyPins renders the active " +
            "pin at ActivePinIndex LAST so its halo sits on top of neighbouring pins. " +
            "BuildScene enumerates SessionState.Surveys in source order and records the " +
            "active survey's enumeration index — selected was seeded at source index 1, " +
            "so ActivePinIndex must equal 1. A wrong value here means the wrong pin gets " +
            "the active-halo treatment, the same shape of visible bug iteration-1 of " +
            "#835 hit through registry insertion order.");
    }

    [SkippableFact]
    public void Producer_path_byte_matches_multi_marker_survey_baseline()
    {
        using var rt = HeadlessD2DRenderTarget.TryCreate(
            CanvasWidth, CanvasHeight, out var driverError);
        Skip.If(rt is null, SkipNoD3DPrefix + (driverError?.Message ?? "(unknown)"));

        var (_, _, selected, _, drawer, session) = BuildSutWithThreeSurveys();
        session.SelectedSurvey = selected;

        var scene = drawer.BuildSceneForTest();
        scene.SurveyPins.Should().HaveCount(3, "producer must surface every visible survey.");

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt!.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        LegolasOverlaySceneDrawer.DrawScene(scene, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        var producerPng = rt.EncodePng();

        var baselinePath = ResolveBaselinePath("multi_marker_survey");
        System.IO.File.Exists(baselinePath).Should().BeTrue(
            "PR #853 must have checked in multi_marker_survey.png at " + baselinePath + ".");
        var baselinePng = System.IO.File.ReadAllBytes(baselinePath);

        producerPng.Should().Equal(baselinePng,
            "the end-to-end producer pipeline (settings -> BuildScene -> DrawScene) " +
            "must produce the same bytes as the hand-crafted multi_marker_survey fixture. " +
            "If this fails, either (a) the active-last ActivePinIndex assignment regressed " +
            "or (b) BuildScene's settings -> PinLayerStyle mapping drifted.");
    }

    /// <summary>Build the system under test: a real <see cref="MapOverlayViewModel"/>
    /// + <see cref="LegolasOverlaySceneDrawer"/> with three surveys seeded at
    /// world (80, 100), (120, 180), (160, 100) — the active one is index 1
    /// (middle) at (120, 180), which matches the existing
    /// <c>multi_marker_survey</c> baseline's active-pin pixel layout and
    /// deliberately places the selected pin in the middle of source order
    /// so an ordering bug (active-last-not-honoured) is reachable.</summary>
    private static (SessionState session, WorldOverlayMarkers registry, SurveyItemViewModel selected, MapOverlayViewModel map, LegolasOverlaySceneDrawer drawer, SessionState sessionEcho)
        BuildSutWithThreeSurveys()
    {
        var session = new SessionState();
        var settings = new LegolasSettings
        {
            // Diameter = 2 * 12.0 = 24.0, matching the multi_marker_survey
            // fixture's hand-crafted OuterDiameter.
            SurveyPinRadiusMetres = 12.0,
        };
        // Match the hand-crafted multi_marker_survey fixture's PinLayerStyle
        // values exactly.
        settings.PinStyle.Center.FillColor = "#FF00FFFF";
        settings.PinStyle.Center.StrokeColor = "#00000000";
        settings.PinStyle.Center.StrokeStyle = PinStrokeStyle.None;
        settings.PinStyle.Center.StrokeThickness = 0;

        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var registry = new WorldOverlayMarkers(NullLogger.Instance) { CurrentArea = AreaKey };
        var areaState = new StubAreaState(AreaKey);

        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes, settings,
            pinCalibration: null, positionState: null, bus: null,
            areaCalibration: null, motherlode: null, characterPin: null,
            markers: registry, areaState: areaState);

        var drawer = new LegolasOverlaySceneDrawer(map);

        // Three surveys. World coords cover the same three pixel positions
        // the multi_marker_survey baseline uses, ordered so the active one
        // is the MIDDLE of source order (not last). The fix's post-condition:
        // BuildScene records ActivePinIndex=1 even though SurveyPins still
        // enumerates in source order.
        var s0 = SeedSurvey(session, world: (80, 100), name: "p0");
        var selected = SeedSurvey(session, world: (120, 180), name: "p_active");
        var s2 = SeedSurvey(session, world: (160, 100), name: "p2");
        _ = (s0, s2);

        return (session, registry, selected, map, drawer, session);
    }

    private static SurveyItemViewModel SeedSurvey(
        SessionState session, (double X, double Z) world, string name)
    {
        // CreateAbsolute stamps Survey.World + the projected pixel. Scene
        // drawer reads EffectivePixel off SurveyItemViewModel, so the
        // seeded pixel IS the pin's rendered position — matches the
        // identity-calibration behaviour the original registry-driven
        // test relied on.
        var pixelPlaceholder = new OverlayPixel(world.X, world.Z);
        var w = new WorldCoord(world.X, 0, world.Z);
        var model = Survey.CreateAbsolute(name, w, pixelPlaceholder, gridIndex: 0);
        var vm = new SurveyItemViewModel(model);
        session.Surveys.Add(vm);
        return vm;
    }

    private static string ResolveBaselinePath(string fixtureName)
    {
        var dllDir = AppContext.BaseDirectory;
        var baselineDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(dllDir, "..", "..", "..", "Rendering", "Snapshots", "Baselines"));
        return System.IO.Path.Combine(baselineDir, fixtureName + ".png");
    }

    private sealed class StubAreaState : Arda.World.Player.IAreaState
    {
        public StubAreaState(string area) { CurrentArea = area; }
        public string? CurrentArea { get; }
    }
}
