using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// #1076 Task 2.3 — assert that <see cref="MapCalibrationService"/>'s typed
/// frame views route each stored <see cref="AreaCalibration"/> into the right
/// frame list based on its <see cref="AreaCalibration.Source"/>. The frame
/// inference rule (spec §7.2): AutoCapture / BundledBaseline / CommunitySync
/// → texture; UserRefinement → overlay (Schema-1 default since AutoCal had
/// never shipped at the cutover).
/// </summary>
public sealed class MapCalibrationServiceTypedFrameTests
{
    private const string Key = "Map_AreaTest";
    private static readonly MapSceneRef Scene = new("AreaTest", null, Key);

    private static AreaCalibration Cal(CalibrationSource source) =>
        new(Scale: 1.0, RotationRadians: 0, OriginX: 100, OriginY: 200,
            ReferenceCount: 6, ResidualPixels: 0.5) { Source = source };

    private static MapCalibrationService NewSvc(
        IReadOnlyDictionary<string, AreaCalibration>? baseline = null,
        IDictionary<string, AreaCalibration>? userRefs = null) =>
        new(
            baseline: baseline ?? new Dictionary<string, AreaCalibration>(),
            userStore: UserRefinementStore.ForTests(userRefs),
            logger: NullLogger.Instance);

    [Fact]
    public void BundledBaselineRecord_RoutesToTextureFrame()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.BundledBaseline) });

        svc.GetTextureRecords(Scene).Should().HaveCount(1);
        svc.GetOverlayRecords(Scene).Should().BeEmpty();
    }

    [Fact]
    public void AutoCaptureRecord_RoutesToTextureFrame()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.AutoCapture) });

        svc.GetTextureRecords(Scene).Should().HaveCount(1);
        svc.GetOverlayRecords(Scene).Should().BeEmpty();
    }

    [Fact]
    public void UserRefinementRecord_RoutesToOverlayFrame()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.UserRefinement) });

        svc.GetOverlayRecords(Scene).Should().HaveCount(1);
        svc.GetTextureRecords(Scene).Should().BeEmpty();
    }

    [Fact]
    public void MixedSources_PartitionCorrectly()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.BundledBaseline) },
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.UserRefinement) });

        svc.GetTextureRecords(Scene).Should().HaveCount(1, "baseline is texture frame");
        svc.GetOverlayRecords(Scene).Should().HaveCount(1, "user refinement is overlay frame");
    }

    [Fact]
    public void NoRecords_BothListsEmpty()
    {
        var svc = NewSvc();
        svc.GetTextureRecords(Scene).Should().BeEmpty();
        svc.GetOverlayRecords(Scene).Should().BeEmpty();
    }

    [Fact]
    public void WorldToTexture_ReturnsNull_WhenSceneHasOnlyOverlayRecords()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.UserRefinement) });

        svc.WorldToTexture(Scene, new WorldCoord(0, 0, 0), currentZoom: 1.0).Should().BeNull();
        svc.TextureToWorld(Scene, new TexturePixel(50, 60), currentZoom: 1.0).Should().BeNull();
    }

    [Fact]
    public void WorldToOverlay_ReturnsNull_WhenSceneHasOnlyTextureRecords()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.BundledBaseline) });

        svc.WorldToOverlay(Scene, new WorldCoord(0, 0, 0), currentZoom: 1.0).Should().BeNull();
        svc.OverlayToWorld(Scene, new OverlayPixel(50, 60), currentZoom: 1.0).Should().BeNull();
    }

    [Fact]
    public void WorldToTexture_ReturnsResult_FromTextureRecord()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.BundledBaseline) });

        var result = svc.WorldToTexture(Scene, new WorldCoord(10, 0, 5), currentZoom: 1.0);
        result.Should().NotBeNull();
    }

    [Fact]
    public void WorldToOverlay_ReturnsResult_FromOverlayRecord()
    {
        var svc = NewSvc(
            userRefs: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.UserRefinement) });

        var result = svc.WorldToOverlay(Scene, new WorldCoord(10, 0, 5), currentZoom: 1.0);
        result.Should().NotBeNull();
    }

    [Fact]
    public void WorldToTexture_RoundTripsThroughTextureToWorld()
    {
        var svc = NewSvc(
            baseline: new Dictionary<string, AreaCalibration> { [Key] = Cal(CalibrationSource.BundledBaseline) });

        var pix = svc.WorldToTexture(Scene, new WorldCoord(10, 0, 5), 1.0)!.Value;
        var roundTrip = svc.TextureToWorld(Scene, pix, 1.0);
        roundTrip.Should().NotBeNull();
        roundTrip!.Value.X.Should().BeApproximately(10, 1e-9);
        roundTrip.Value.Z.Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void TextureRecord_PreservesProjectionParameters()
    {
        var legacy = new AreaCalibration(
            Scale: 4.0, RotationRadians: 0.5,
            OriginX: 100, OriginY: 200,
            ReferenceCount: 6, ResidualPixels: 0.5)
        { Source = CalibrationSource.BundledBaseline, MirrorNorth = true, CalibrationZoom = 1.25 };

        var svc = NewSvc(baseline: new Dictionary<string, AreaCalibration> { [Key] = legacy });

        var tex = svc.GetTextureRecords(Scene)[0];
        tex.OriginX.Should().Be(100);
        tex.OriginY.Should().Be(200);
        tex.Scale.Should().Be(4.0);
        tex.RotationRadians.Should().Be(0.5);
        tex.MirrorNorth.Should().BeTrue();
        tex.CalibrationZoom.Should().Be(1.25);
    }
}
