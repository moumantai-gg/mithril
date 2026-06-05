using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Frame-aware drift-check behaviour (mithril#1076 spec §2.4 / §13 P.1b).
///
/// <para>The catalyst Map_KhyruleksCrypt 2026-06-04 record was a Legolas-wizard
/// fit — overlay-frame. AutoCalibration's drift-check is bound to TEXTURE
/// space; running it against an overlay-frame record would silently produce
/// 0/N matches even after the #1076 crop-fix lands. Refuse honestly instead.</para>
/// </summary>
public sealed class AutoCalibrationEngineDriftCheckFrameAwareTests
{
    private const string Asset = "Map_AreaOverlayOnly";
    private static readonly MapSceneRef Scene = new("AreaOverlayOnly", null, Asset);

    [Fact]
    public async Task DriftCheck_ReturnsNoTextureFrameRecord_WhenSceneHasOnlyOverlayCalibration()
    {
        // The scene's stored record is UserRefinement (a Legolas-wizard
        // overlay-frame fit per spec §7.2). MapCalibrationService.GetTextureCalibration
        // returns null because no texture-frame source has fit this scene.
        var engine = NewEngine(textureCal: null);

        var outcome = await engine.CheckDriftAsync(CancellationToken.None);

        outcome.Should().BeOfType<DriftCheckOutcome.NoTextureFrameRecord>();
    }

    [Fact]
    public async Task DriftCheck_DoesNotInvokeRefiner_WhenNoTextureFrameRecord()
    {
        // The refusal is an EARLY return — no capture, no refine, no detect work.
        // Throwing-refiner asserts the engine doesn't reach the locate stage.
        var engine = NewEngine(
            textureCal: null,
            refiner: new ThrowingRefiner());

        var outcome = await engine.CheckDriftAsync(CancellationToken.None);

        outcome.Should().BeOfType<DriftCheckOutcome.NoTextureFrameRecord>();
    }

    private static AutoCalibrationEngine NewEngine(
        WorldToTextureCalibration? textureCal,
        IMapRegionRefiner? refiner = null)
    {
        var mapState = new FakeMapState
        {
            CurrentArea = Scene.ParentAreaKey,
            CurrentMapScene = Scene,
        };
        var sceneCache = new FakeSceneAssetCache();
        var windowLocator = new FakeWindowLocator(
            new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)));
        var regionProvider = new FakeRegionProvider(new CaptureRect(0, 0, 400, 400));
        var capture = new SpyCapture(new GrayImage(400, 400, new byte[400 * 400]));
        var actualRefiner = refiner ?? FakeMapRegionRefinerDrift.Accept();
        var baseTextures = new FakeBaseTextureProvider(new GrayImage(400, 400, new byte[400 * 400]));
        var references = new FakeDriftAreaRefs();
        var solver = new FakeCalibrationSolverDrift();
        var iconTemplates = new FakeIconTemplateProvider(IconTemplateSet.Empty);
        var calService = new FakeCalibrationService();
        // Seed an overlay-frame UserRefinement record so GetCalibration still
        // returns non-null (the scene IS "calibrated", just not in the texture
        // frame the drift check needs). MapCalibrationService.GetTextureCalibration
        // returns null because the source maps to Overlay; the fake mirrors that
        // via the textureCal parameter.
        var fakeStored = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: 0.5)
        {
            Source = CalibrationSource.UserRefinement,
        };
        calService.Seed(Asset, fakeStored);
        if (textureCal is not null)
            calService.SeedTextureCalibration(Asset, textureCal.Value);

        return new AutoCalibrationEngine(
            mapState, sceneCache, windowLocator, regionProvider, capture,
            actualRefiner, baseTextures, references, solver, iconTemplates,
            calService, logger: null);
    }

    /// <summary>Throws if Refine is invoked — proves the engine short-circuited.</summary>
    private sealed class ThrowingRefiner : IMapRegionRefiner
    {
        public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture) =>
            throw new InvalidOperationException(
                "Engine must NOT invoke the refiner when no texture-frame record exists.");
    }
}
