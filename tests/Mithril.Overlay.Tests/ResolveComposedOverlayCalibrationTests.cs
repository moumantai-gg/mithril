using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// mithril#1081 — the per-frame compose helper resolves an effective
/// WorldToOverlayCalibration? for the current scene, either directly from
/// an overlay-frame record or by composing a texture-frame record onto the
/// overlay surface size via WorldToTextureCalibration.ProjectThroughOverlay.
/// Dims are content-addressed via IMapTextureDimensions (cal.PixelSha256
/// lookup); failure modes are: null sha (pre-#1081), catalogue miss
/// (uncatalogued / newer PG), unsized surface (first frame after Show).
/// </summary>
public sealed class ResolveComposedOverlayCalibrationTests
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

    private sealed class StubDims : IMapTextureDimensions
    {
        public (int W, int H)? Result { get; set; }
        public (int Width, int Height)? TryGetSizeBySha(string? sha) => Result;
    }

    private static StubDims DimsWith(int w, int h) => new() { Result = (w, h) };
    private static StubDims DimsNull() => new() { Result = null };

    [Fact]
    public void WizardOnly_ReturnsDirectOverlayCal()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: MakeOverlayCal(),
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.DirectOverlay);
        cal!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void AutoCalOnly_ShaInCatalogue_ReturnsComposedFromTexture()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.ComposedFromTexture);
    }

    [Fact]
    public void AutoCalOnly_NullSha_ReturnsNone()
    {
        // Pre-#1081 record.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(sha: null),
            dims: DimsWith(1024, 1024),  // catalogue knows things, but cal has no sha
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void AutoCalOnly_ShaNotInCatalogue_ReturnsNone()
    {
        // Newer PG patch than catalogue, or uncatalogued asset.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void AutoCalOnly_UnsizedSurface_ReturnsNone()
    {
        // First frame after Show(); ActualWidth/Height not yet laid out.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 0, surfaceHeight: 0);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void BothFramesPresent_PrefersDirectOverlay()
    {
        // Per #1082's per-frame slots, both records can exist; the overlay
        // takes the direct-overlay path, composition is dead code.
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: MakeOverlayCal(),
            textureCal: MakeTexCal(),
            dims: DimsWith(1024, 1024),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().NotBeNull();
        path.Should().Be(OverlayWindowService.CalPath.DirectOverlay);
        cal!.Value.OriginX.Should().Be(100);
    }

    [Fact]
    public void Uncalibrated_ReturnsNone()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: Scene,
            overlayCal: null,
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }

    [Fact]
    public void NullScene_ReturnsNone()
    {
        var (cal, path) = OverlayWindowService.ResolveComposedOverlayCalibrationForTest(
            scene: null,
            overlayCal: null,
            textureCal: null,
            dims: DimsNull(),
            surfaceWidth: 800, surfaceHeight: 600);

        cal.Should().BeNull();
        path.Should().Be(OverlayWindowService.CalPath.None);
    }
}
