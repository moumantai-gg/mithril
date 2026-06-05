using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Regression marker for mithril#1076. Pre-fix, <see cref="AutoCalibrationEngine.CheckDriftAsync"/>
/// projected reference predictions into TEXTURE space and added
/// (<see cref="LocateMetrics.Tx"/>, <see cref="LocateMetrics.Ty"/>) to land
/// in CAPTURED-FRAME space — but the detector emits anchors in
/// CROP-FRAME space (the screenshot it consumed is the cropped sub-rect of
/// the captured frame). The mismatch was exactly (Tx, Ty); on the catalyst
/// Map_KhyruleksCrypt 2026-06-04 20:28:01 attempt those values were
/// (320.1, 57.6), pushing every reference outside DriftMatchGatePx=20 →
/// 0/N matched → "inconclusive, no arming." See spec §2.1.
///
/// <para>The other drift-check tests at <see cref="AutoCalibrationEngineDriftCheckTests"/>
/// pre-aligned their inputs so the located rect originated at (0, 0) in the
/// captured frame — collapsing (Tx, Ty) to ~0, which coincidentally hid the
/// bug. This test exercises a NON-ZERO located-rect origin so a future
/// refactor that re-introduces the bug class fails loudly.</para>
///
/// <para>Today's design rules the bug out at compile time (<see cref="TexturePixel"/>
/// vs <see cref="CroppedFramePixel"/> are different types and don't mix),
/// but this test still passes only when the engine performs the correct
/// frame conversion via <c>alignedRect.TextureToCropped(predTex)</c>.</para>
/// </summary>
public sealed class AutoCalibrationEngineDriftCheck1076RegressionTests
{
    private const string Asset = "Map_AreaTest1076";
    private static readonly MapSceneRef Scene = new("AreaTest1076", null, Asset);

    /// <summary>
    /// Stored texture-frame calibration. Scale=1, Origin=(100,100),
    /// MirrorNorth=false, Rotation=0 → world→texture projection collapses to
    ///   texX = 100 + worldX
    ///   texY = 100 - worldZ
    /// matching <see cref="TestDetections.AtPredictedPositions"/>.
    /// </summary>
    private static AreaCalibration Stored() =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 100,
            ReferenceCount: 6, ResidualPixels: 0.7)
        {
            Source = CalibrationSource.AutoCapture,
        };

    [Fact]
    public async Task DriftCheck_WithNonZeroLocateOffset_MatchesAtLeastDriftMinMatchedReferences()
    {
        // Synthesise a capture where the located rect originates at (320, 58) —
        // mirroring the live Map_KhyruleksCrypt 2026-06-04 attempt that
        // exposed #1076. Crop dims 400x400 = texture dims 400x400 (alignedRect
        // is texture↔crop identity), so detections placed at "100 + worldX,
        // 100 - worldZ" land in the same coords as the texture-space
        // predictions — exactly what the crop-frame comparison expects.
        //
        // Pre-fix, the engine added (loc.Tx=320, loc.Ty=58) to texture
        // predictions then compared against crop-frame detections at the
        // un-offset positions — every reference falls ~327 px away (well
        // outside DriftMatchGatePx=20). With the fix, predTex maps through
        // alignedRect.TextureToCropped to the un-offset crop-frame position;
        // matches succeed.
        var engine = NewEngine(
            cal: Stored(),
            seededDetections: TestDetections.AtPredictedPositions(offsetPx: 0.5),
            refiner: FakeMapRegionRefinerDrift.AcceptAt(
                originX: 320, originY: 58,
                width: 400, height: 400,
                textureWidth: 400, textureHeight: 400),
            // Captured frame must be big enough to contain the (320, 58)-origin
            // 400×400 located rect: 720+ wide, 458+ tall.
            capturedWidth: 800, capturedHeight: 600);

        var outcome = await engine.CheckDriftAsync(CancellationToken.None);

        outcome.Should().BeOfType<DriftCheckOutcome.Ok>(
            "with a correct crop-frame comparison every reference matches a "
            + "detection within the gate; pre-fix the same scenario returned "
            + "Inconclusive with 0 refs matched.")
            .Which.MatchedReferences.Should().BeGreaterThanOrEqualTo(3);
    }

    private static AutoCalibrationEngine NewEngine(
        AreaCalibration? cal,
        IReadOnlyList<TypedDetection>? seededDetections = null,
        IMapRegionRefiner? refiner = null,
        int capturedWidth = 400,
        int capturedHeight = 400)
    {
        var mapState = new FakeMapState
        {
            CurrentArea = Scene.ParentAreaKey,
            CurrentMapScene = Scene,
        };
        var sceneCache = new FakeSceneAssetCache();
        var windowLocator = new FakeWindowLocator(
            new GameWindow(1, new CaptureRect(0, 0, 1920, 1080)));
        var regionProvider = new FakeRegionProvider(
            new CaptureRect(0, 0, capturedWidth, capturedHeight));
        var gray = new GrayImage(capturedWidth, capturedHeight, new byte[capturedWidth * capturedHeight]);
        var capture = new SpyCapture(gray);
        var actualRefiner = refiner ?? FakeMapRegionRefinerDrift.Accept();
        var baseTextures = new FakeBaseTextureProvider(
            new GrayImage(capturedWidth, capturedHeight, new byte[capturedWidth * capturedHeight]));
        var references = new FakeDriftAreaRefs();
        var solver = new FakeCalibrationSolverDrift
        {
            SeededDetections = seededDetections ?? Array.Empty<TypedDetection>(),
        };
        var iconTemplates = new FakeIconTemplateProvider(IconTemplateSet.Empty);
        var calService = new FakeCalibrationService();
        if (cal is not null)
            calService.Seed(Asset, cal);

        return new AutoCalibrationEngine(
            mapState,
            sceneCache,
            windowLocator,
            regionProvider,
            capture,
            actualRefiner,
            baseTextures,
            references,
            solver,
            iconTemplates,
            calService,
            logger: null);
    }
}
