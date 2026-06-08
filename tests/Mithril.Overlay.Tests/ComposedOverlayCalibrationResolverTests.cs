using System;
using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// mithril#1096 — IComposedOverlayCalibrationResolver decision table.
///
/// Post-#1107 review fix: the composer is a direct rebrand of the texture cal
/// (not a surface-scaled composition), so the resolver takes no surface dims and
/// the F-* branches that needed catalogue lookups + dim guards are gone. The
/// remaining cases are: scene-null, overlay-frame-present, texture-frame-only,
/// uncalibrated, both-frames-present (precedence).
///
/// Pre-review history: ResolveComposedOverlayCalibrationTests → 8 cases including
/// null_sha / unsized_surface / catalogue_miss. Those failure modes can't fire
/// post-#1107 because no catalogue lookup or surface-dim guard runs.
/// </summary>
public sealed class ComposedOverlayCalibrationResolverTests
{
    private static readonly MapSceneRef Scene =
        new(ParentAreaKey: "AreaTest", SceneFriendlyName: null, MapAssetKey: "Map_Test");

    private static WorldToOverlayCalibration MakeOverlayCal() =>
        new(OriginX: 100, OriginY: 200, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false);

    private static WorldToTextureCalibration MakeTexCal() =>
        new(OriginX: 50, OriginY: 75, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false);

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

    private static IComposedOverlayCalibrationResolver Make(
        WorldToOverlayCalibration? overlayCal = null,
        WorldToTextureCalibration? textureCal = null)
        => new ComposedOverlayCalibrationResolver(
            new StubCal { OverlayCal = overlayCal, TextureCal = textureCal });

    [Fact]
    public void WizardOnly_ReturnsDirectOverlayCal()
    {
        var r = Make(overlayCal: MakeOverlayCal()).Resolve(Scene);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.DirectOverlay);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void AutoCalOnly_RebrandsTextureCalAsComposedFromTexture()
    {
        var r = Make(textureCal: MakeTexCal()).Resolve(Scene);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.ComposedFromTexture);
        r.MissReason.Should().BeNull();
        // Rebrand preserves the texture cal's transform fields verbatim — no
        // surface scaling. Downstream ToLiveOverlay applies the layer-2 fix.
        r.Calibration!.Value.OriginX.Should().Be(50);
        r.Calibration!.Value.OriginY.Should().Be(75);
        r.Calibration!.Value.Scale.Should().Be(2.0);
    }

    [Fact]
    public void BothFramesPresent_PrefersComposedFromTexture()
    {
        // mithril#1107 manual-verify fix: Texture-frame is the right input space
        // for ToLiveOverlay's layer-2 fix. Pre-#1095 wizard Overlay-frame cals
        // are in canonical-overlay-pixel units, which produces a unit mismatch
        // when fed through ToLiveOverlay (markers projected at ~2x wrong scale
        // for a typical Serbule-shaped overlay). Texture-frame wins when both
        // exist — see the resolver's type doc for the unit rationale.
        var r = Make(overlayCal: MakeOverlayCal(), textureCal: MakeTexCal()).Resolve(Scene);

        r.Calibration.Should().NotBeNull();
        r.Path.Should().Be(CalPath.ComposedFromTexture);
        r.MissReason.Should().BeNull();
        r.Calibration!.Value.OriginX.Should().Be(50);
        r.Calibration!.Value.Scale.Should().Be(2.0);
    }

    [Fact]
    public void Uncalibrated_ReturnsNone_NoUsableCalibration()
    {
        var r = Make().Resolve(Scene);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_usable_calibration");
    }

    [Fact]
    public void NullScene_ReturnsNone_NoScene()
    {
        var r = Make().Resolve(null);

        r.Calibration.Should().BeNull();
        r.Path.Should().Be(CalPath.None);
        r.MissReason.Should().Be("no_scene");
    }
}
