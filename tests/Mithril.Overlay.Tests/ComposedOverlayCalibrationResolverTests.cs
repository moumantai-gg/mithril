using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// mithril#1096 — IComposedOverlayCalibrationResolver covers the same 8-case
/// decision table as the pre-#1096 OverlayWindowService internal helper, plus
/// MissReason vocabulary assertions for the None-returning cases.
///
/// (Pre-#1096 history: this file was ResolveComposedOverlayCalibrationTests
/// targeting OverlayWindowService.ResolveComposedOverlayCalibrationForTest;
/// the 8 cases are preserved verbatim — only the call shape changed.)
/// </summary>
public sealed class ComposedOverlayCalibrationResolverTests
{
    private static readonly MapSceneRef Scene =
        new(ParentAreaKey: "AreaTest", SceneFriendlyName: null, MapAssetKey: "Map_Test");

    private const string KnownSha = "abc123def";

    private static WorldToOverlayCalibration MakeOverlayCal() =>
        new(OriginX: 100, OriginY: 200, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false);

    private static WorldToTextureCalibration MakeTexCal(string? sha = KnownSha) =>
        new(OriginX: 50, OriginY: 75, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false)
        {
            PixelSha256 = sha,
        };

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

    private sealed class StubDims : IMapTextureDimensions
    {
        public (int W, int H)? Result { get; set; }
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => Result;
    }

    private static IComposedOverlayCalibrationResolver Make(
        WorldToOverlayCalibration? overlayCal = null,
        WorldToTextureCalibration? textureCal = null,
        (int W, int H)? dims = null)
        => new ComposedOverlayCalibrationResolver(
            new StubCal { OverlayCal = overlayCal, TextureCal = textureCal },
            new StubDims { Result = dims });

    [Fact]
    public void WizardOnly_ReturnsDirectOverlayCal()
    {
        var r = Make(overlayCal: MakeOverlayCal()).Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.DirectOverlay);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void AutoCalOnly_ShaInCatalogue_ReturnsComposedFromTexture()
    {
        var r = Make(textureCal: MakeTexCal(), dims: (1024, 1024)).Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.ComposedFromTexture);
        r.MissReason.Should().BeNull();
    }

    [Fact]
    public void AutoCalOnly_NullSha_ReturnsNone_NullSha()
    {
        var r = Make(textureCal: MakeTexCal(sha: null), dims: (1024, 1024)).Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("null_sha");
    }

    [Fact]
    public void AutoCalOnly_ShaNotInCatalogue_ReturnsNone_CatalogueMiss()
    {
        var r = Make(textureCal: MakeTexCal(), dims: null).Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("catalogue_miss");
    }

    [Fact]
    public void AutoCalOnly_UnsizedSurface_ReturnsNone_UnsizedSurface()
    {
        var r = Make(textureCal: MakeTexCal(), dims: (1024, 1024)).Resolve(Scene, 0, 0);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("unsized_surface");
    }

    [Fact]
    public void BothFramesPresent_PrefersDirectOverlay()
    {
        var r = Make(overlayCal: MakeOverlayCal(), textureCal: MakeTexCal(), dims: (1024, 1024))
            .Resolve(Scene, 800, 600);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.DirectOverlay);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void Uncalibrated_ReturnsNone_NoUsableCalibration()
    {
        var r = Make().Resolve(Scene, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_usable_calibration");
    }

    [Fact]
    public void NullScene_ReturnsNone_NoScene()
    {
        var r = Make().Resolve(null, 800, 600);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_scene");
    }
}
