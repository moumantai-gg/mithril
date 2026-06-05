// #1076 Phase 5a: P.3 audit found all Legolas test PixelPoint sites are
// overlay-frame, so this Rendering test file was migrated alongside the 5a
// scope (its PinScene/drawer dependencies now take OverlayPixel).
using FluentAssertions;
using Legolas.Domain;
using Legolas.Rendering;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Color = System.Windows.Media.Color;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Tests.Rendering;

/// <summary>
/// Byte-parity snapshot tests for the #835 step 5 calibration drawer
/// (<see cref="LegolasCalibrationMarkerDrawer"/>).
///
/// <para><b>Why this set differs from <see cref="LegolasMarkerDrawerSnapshotTests"/>.</b>
/// PR #853 explicitly deferred the calibration drawer because today's
/// calibration markers are rendered by a WPF <c>ItemsControl</c> in
/// <c>MapOverlayView.xaml</c>, not by <c>PinSceneRenderer</c> — there is no
/// D2D source-of-truth to byte-parity-compare against. These tests therefore
/// commit the checked-in baselines as the NEW ground truth and assert
/// regression against them. Step 6 will inherit this contract as the lone
/// calibration-marker rendering guarantee once the legacy <c>ItemsControl</c>
/// path is retired.</para>
///
/// <para><b>Fixtures.</b> Two states: selected (Outer ring + Center dot) and
/// unselected (Center dot only). Pixel anchored at canvas centre for
/// determinism.</para>
///
/// <para>Same <c>MITHRIL_REGEN_SNAPSHOTS=1</c> regen workflow as PR #853 —
/// missing baseline fails hard; env var writes-and-fails so the verify
/// re-run is forced.</para>
/// </summary>
public sealed class LegolasCalibrationMarkerSnapshotTests
{
    private const int CanvasWidth = 240;
    private const int CanvasHeight = 240;
    private const string RegenEnvVar = "MITHRIL_REGEN_SNAPSHOTS";
    private const string SkipNoD3DPrefix =
        "No usable D3D11 driver (neither Hardware nor WARP). Inner driver error: ";

    // Calibration default colours (mirrors LegolasPinStyle.CalibrationDefaults
    // via the brushes Legolas.Views uses). Magenta ring + magenta dot —
    // matches the "in-flow calibration" treatment in MapOverlayView.xaml's
    // ItemsControl branch.
    private static readonly Color RingStroke = Color.FromArgb(0xFF, 0xE0, 0x40, 0xE0);
    private static readonly Color DotFill = Color.FromArgb(0xFF, 0xE0, 0x40, 0xE0);

    private static PinLayerStyle CalibrationOuter() => new(
        Shape: PinShape.Circle,
        FillColor: Color.FromArgb(0, 0, 0, 0),
        StrokeColor: RingStroke,
        StrokeStyle: PinStrokeStyle.Solid,
        StrokeThickness: 2.0,
        Size: 16.0);

    private static PinLayerStyle CalibrationCenter() => new(
        Shape: PinShape.Circle,
        FillColor: DotFill,
        StrokeColor: Color.FromArgb(0, 0, 0, 0),
        StrokeStyle: PinStrokeStyle.None,
        StrokeThickness: 0,
        Size: 4.0);

    [SkippableFact]
    public void Calibration_unselected_renders_only_center_dot()
    {
        RunCalibrationSnapshot(
            fixtureName: "calibration_unselected",
            isSelected: false);
    }

    [SkippableFact]
    public void Calibration_selected_renders_selection_ring_plus_center_dot()
    {
        RunCalibrationSnapshot(
            fixtureName: "calibration_selected",
            isSelected: true);
    }

    private static void RunCalibrationSnapshot(string fixtureName, bool isSelected)
    {
        using var rt = HeadlessD2DRenderTarget.TryCreate(
            CanvasWidth, CanvasHeight, out var driverError);
        Skip.If(rt is null, SkipNoD3DPrefix + (driverError?.Message ?? "(unknown)"));

        // Drive the production wire-up: AddMarker (identity calibration) →
        // CurrentAreaMarkers → ProjectMarkers → MarkerSceneRenderer.Render.
        // Picks up the same dispatch + projection path the live overlay uses.
        var registry = new WorldOverlayMarkers { CurrentArea = "AreaTest" };
        var style = new LegolasCalibrationMarkerStyle(
            CalibrationOuter(), CalibrationCenter(), IsSelected: isSelected);
        registry.AddMarker("AreaTest", 120, 120, style);

        var snapshot = registry.CurrentAreaMarkers;
        snapshot.Should().HaveCount(1, "the registered marker must reach the snapshot.");

        // mithril#1081: ProjectMarkers now takes a WorldToOverlayCalibration?
        // directly. MirrorNorth=true reproduces the old IdentityCalibrationService
        // mapping (world.X, world.Z) → OverlayPixel(world.X, world.Z) so
        // byte-parity baselines are preserved.
        var composed = new WorldToOverlayCalibration(
            OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: true, CalibrationZoom: 1.0);
        var projected = OverlayWindowService.ProjectMarkers(
            snapshot, composed, currentZoom: 1.0);
        projected.Should().HaveCount(1, "the identity calibration projects the only marker.");

        var renderer = new MarkerSceneRenderer();
        LegolasOverlayDrawerRegistrations.RegisterAll(renderer);
        // #835 step 6 iter-1 touch-up: production RegisterAll no longer
        // wires the calibration drawer (placement pins now draw pixel-
        // native via LegolasOverlaySceneDrawer per dissolved-#868). The
        // snapshot test still locally registers the dead-but-not-yet-
        // deleted drawer so its byte-parity baseline keeps regression
        // coverage on the file until step 7 retires it.
        renderer.RegisterDrawer<LegolasCalibrationMarkerStyle>(LegolasCalibrationMarkerDrawer.Draw);

        using var brushes = new D2DBrushCache();
        brushes.Bind(rt!.RenderTarget);
        rt.RenderTarget.BeginDraw();
        rt.RenderTarget.Clear(new Color4(0, 0, 0, 0));
        renderer.Render(projected, rt.RenderTarget, rt.Factory, brushes);
        rt.RenderTarget.EndDraw();
        var pipelinePng = rt.EncodePng();

        var baselinePath = ResolveBaselinePath(fixtureName);
        var baselinePng = LoadBaselineOrFail(baselinePath, pipelinePng);

        pipelinePng.Should().Equal(baselinePng,
            "the new calibration drawer must reproduce its checked-in baseline PNG; "
            + "this is the regression contract that replaces the (absent) PinSceneRenderer "
            + "parity contract for calibration markers per the #835 step 5 ground-truth note.");
    }

    private static string ResolveBaselinePath(string fixtureName)
    {
        var dllDir = AppContext.BaseDirectory;
        var baselineDir = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(dllDir, "..", "..", "..", "Rendering", "Snapshots", "Baselines"));
        System.IO.Directory.CreateDirectory(baselineDir);
        return System.IO.Path.Combine(baselineDir, fixtureName + ".png");
    }

    private static byte[] LoadBaselineOrFail(string path, byte[] freshBaselinePng)
    {
        if (System.IO.File.Exists(path))
        {
            return System.IO.File.ReadAllBytes(path);
        }

        if (Environment.GetEnvironmentVariable(RegenEnvVar) == "1")
        {
            System.IO.File.WriteAllBytes(path, freshBaselinePng);
            throw new Xunit.Sdk.XunitException(
                "Regenerated missing baseline at " + path
                + ". Re-run WITHOUT " + RegenEnvVar
                + "=1 to verify that the calibration drawer produces these bytes.");
        }

        throw new System.IO.FileNotFoundException(
            "Snapshot baseline missing: " + path
            + ". Set " + RegenEnvVar + "=1 to regenerate, then re-run without it.",
            path);
    }

    private sealed class IdentityCalibrationService : IMapCalibrationService
    {
        public bool IsCalibrated(MapSceneRef scene) => true;
        public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) => null;
        public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom)
            => new OverlayPixel(world.X, world.Z);
        public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom)
            => new WorldCoord(pixel.X, 0, pixel.Y);
        public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => null;
        public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => null;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations
            => new Dictionary<string, AreaCalibration>();
        public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration) { }
        public void ClearUserRefinement(MapSceneRef scene) { }
        public void DeleteUserRefinement(MapSceneRef scene, CalibrationFrame frame) =>
            throw new NotSupportedException("Test fake does not implement DeleteUserRefinement.");
        public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
    }
}
