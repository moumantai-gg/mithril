using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.MapCalibration;
using Mithril.Overlay;
using Mithril.Shared.Reference;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// mithril#1096 — VM consumers route through IComposedOverlayCalibrationResolver
/// when wired. Headline behaviour: a scene with only a texture-frame record
/// (no overlay-frame record) projects ghosts via the composed path.
///
/// Post-#1107 review fix: composer is surface-dim-free (direct rebrand of
/// texture cal), so the stub overlay window + StubDims dropped — the
/// resolver no longer needs them.
/// </summary>
public sealed class MapOverlayComposedCalMigrationTests
{
    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    /// <summary>Minimal ILiveMapViewService stub returning an identity fix
    /// (pan=0, viewScale=1) so the post-#1107 ghost-rebuild path doesn't
    /// short-circuit on no_live_fix.</summary>
    private sealed class StubLiveView : ILiveMapViewService
    {
        public MapViewFix? GetCurrent(string mapAssetKey) => new MapViewFix(
            PanTexPxX: 0, PanTexPxY: 0, ViewScale: 1.0,
            Confidence: 1.0, MeasuredAt: DateTimeOffset.UnixEpoch);
        public LiveMapViewStatus GetStatus(string mapAssetKey) => LiveMapViewStatus.Detected;
        public Task RefreshAsync(string mapAssetKey, CancellationToken ct) => Task.CompletedTask;
#pragma warning disable CS0067
        public event Action<string>? Changed;
#pragma warning restore CS0067
    }

    private sealed class StubCal : IMapCalibrationService
    {
        public WorldToOverlayCalibration? OverlayCal { get; set; }
        public WorldToTextureCalibration? TextureCal { get; set; }
        public WorldToOverlayCalibration? GetOverlayCalibration(MapSceneRef scene) => OverlayCal;
        public WorldToTextureCalibration? GetTextureCalibration(MapSceneRef scene) => TextureCal;
        public AreaCalibration? GetCalibration(MapSceneRef scene) => null;
        public bool IsCalibrated(MapSceneRef scene) => OverlayCal is not null || TextureCal is not null;
        public TexturePixel? WorldToTexture(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? TextureToWorld(MapSceneRef scene, TexturePixel pixel, double currentZoom) => null;
        public OverlayPixel? WorldToOverlay(MapSceneRef scene, WorldCoord world, double currentZoom) => null;
        public WorldCoord? OverlayToWorld(MapSceneRef scene, OverlayPixel pixel, double currentZoom) => null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } =
            new Dictionary<string, AreaCalibration>();
        public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene) => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration) { }
        public void ClearUserRefinement(MapSceneRef scene) { }
        public void DeleteUserRefinement(MapSceneRef scene, CalibrationFrame frame) { }
        public event EventHandler<MapSceneRef>? Changed { add { } remove { } }
    }

    /// <summary>IAreaCalibrationService double where the area IS calibrated
    /// (texture-frame record exists) but CurrentOverlayCalibration returns
    /// null — exactly the pre-#1096 silent-drop shape. Pre-migration, the
    /// VM read .CurrentOverlayCalibration directly and got null → no ghosts.
    /// Post-migration, the composer is consulted with the texture-frame stub
    /// and a non-null cal lands → ghosts project.</summary>
    private sealed class TextureFrameOnlyAreaCalibration : IAreaCalibrationService
    {
        private readonly IReadOnlyList<CalibrationReference> _refs;
        public TextureFrameOnlyAreaCalibration(params CalibrationReference[] refs) { _refs = refs; }

        public WorldToOverlayCalibration? CurrentOverlayCalibration => null;
        public bool IsCurrentAreaCalibrated => true;
        public AreaCalibration? CurrentCalibration =>
            new AreaCalibration(1.0, 0.0, 0, 0, 0, 0.0);
        public MapSceneRef? CurrentScene =>
            new MapSceneRef("AreaTest", null, "Map_AreaTest");
        public string? CurrentAreaFriendlyName => "Test Area";
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => _refs;
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();

        public event EventHandler? Changed { add { } remove { } }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }

        public void SelectScene(MapSceneRef scene) { }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, OverlayPixel Pixel)> placements,
            double calibrationZoom = 1.0) => null;
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
    }

    [Fact]
    public void RebuildCalibrationGhosts_TextureFrameOnly_ComposesAndRenders()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaCal = new TextureFrameOnlyAreaCalibration(
            Ref("Statue", 10, 5), Ref("Well", -4, 12));

        var stubCal = new StubCal
        {
            TextureCal = new WorldToTextureCalibration(
                OriginX: 50, OriginY: 75, Scale: 2.0,
                RotationRadians: 0, MirrorNorth: false),
        };
        var composer = new Mithril.Overlay.Internal.ComposedOverlayCalibrationResolver(stubCal);

        var map = new MapOverlayViewModel(
            session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null,
            areaCalibration: areaCal,
            motherlode: null, characterPin: null, markers: null, areaState: null,
            loggerFactory: null, liveView: new StubLiveView(),
            composedResolver: composer);

        map.IsCurrentAreaCalibrated.Should().BeTrue(
            "the area IS calibrated — texture-frame record exists");
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeTrue();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.CalibrationGhosts.Should().HaveCount(2,
            "post-#1096: texture-frame-only record is rebranded into an overlay cal and projects both refs");
    }
}
