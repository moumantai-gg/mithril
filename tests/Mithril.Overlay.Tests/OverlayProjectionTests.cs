using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Overlay.Tests.Fakes;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Tests for the pure projection helper inside
/// <see cref="OverlayWindowService"/>. Carved out as a static so it can be
/// exercised without a D3D surface — the per-tick projection logic
/// (call <see cref="IMapCalibrationService.WorldToWindow"/>, skip nulls,
/// keep style references) is unit-testable in isolation.
/// </summary>
public sealed class OverlayProjectionTests
{
    private sealed record TestStyle(string Tag) : IMarkerStyle;

    private static MarkerSnapshot Snap(double x, double z, IMarkerStyle style)
        => new(new MarkerHandle(Guid.NewGuid()), new WorldCoord(x, 0, z), style);

    [Fact]
    public void Projects_each_marker_through_WorldToWindow_when_area_is_calibrated()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        calibration.Projector = (_, world, _) => (PixelPoint?)new PixelPoint(world.X * 2, world.Z * 3);

        var styleA = new TestStyle("a");
        var styleB = new TestStyle("b");
        var markers = new[]
        {
            Snap(10.0, 20.0, styleA),
            Snap(-5.0, 7.0, styleB),
        };

        var projected = OverlayWindowService.ProjectMarkers(markers, new MapSceneRef("A", null, "A"), calibration, currentZoom: 1.0);

        projected.Should().HaveCount(2);
        projected[0].Should().Be((new OverlayPixel(20, 60), (IMarkerStyle)styleA));
        projected[1].Should().Be((new OverlayPixel(-10, 21), (IMarkerStyle)styleB));
    }

    [Fact]
    public void Returns_empty_when_marker_list_is_empty()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");

        OverlayWindowService
            .ProjectMarkers(Array.Empty<MarkerSnapshot>(), new MapSceneRef("A", null, "A"), calibration, 1.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void Skips_markers_whose_projection_returns_null()
    {
        // Calibration says the area is calibrated, but the projector returns
        // null for one specific marker — that marker is silently skipped,
        // not dropped from the registry and not throwing.
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        calibration.Projector = (_, world, _) =>
            world.X < 0 ? null : new PixelPoint(world.X, world.Z);

        var style = new TestStyle("s");
        var markers = new[]
        {
            Snap(1.0, 1.0, style),
            Snap(-1.0, 1.0, style), // projector returns null
            Snap(2.0, 2.0, style),
        };

        var projected = OverlayWindowService.ProjectMarkers(markers, new MapSceneRef("A", null, "A"), calibration, 1.0);

        projected.Should().HaveCount(2);
        projected[0].Pixel.Should().Be(new OverlayPixel(1, 1));
        projected[1].Pixel.Should().Be(new OverlayPixel(2, 2));
    }

    [Fact]
    public void Uncalibrated_area_yields_an_empty_projection_list()
    {
        var calibration = new FakeMapCalibrationService(); // nothing calibrated
        var style = new TestStyle("s");
        var markers = new[]
        {
            Snap(1.0, 1.0, style),
        };

        OverlayWindowService.ProjectMarkers(markers, new MapSceneRef("AreaUncalibrated", null, "AreaUncalibrated"), calibration, 1.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void Style_references_flow_through_projection_unmodified()
    {
        var calibration = new FakeMapCalibrationService();
        calibration.CalibratedAreas.Add("A");
        var style = new TestStyle("identity");
        var markers = new[]
        {
            Snap(0.0, 0.0, style),
        };

        var projected = OverlayWindowService.ProjectMarkers(markers, new MapSceneRef("A", null, "A"), calibration, 1.0);
        projected.Single().Style.Should().BeSameAs(style);
    }
}
