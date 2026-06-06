using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Mithril.Overlay.Tests.Fakes;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Tests for the pure projection helper inside
/// <see cref="OverlayWindowService"/>. Carved out as a static so it can be
/// exercised without a D3D surface — the per-tick projection logic (apply the
/// bound <see cref="WorldToOverlayCalibration"/> + <see cref="MapViewFix"/>,
/// skip frame when null) is unit-testable in isolation.
///
/// mithril#1081: <c>ProjectMarkers</c> was reshaped to take a
/// <see cref="WorldToOverlayCalibration?"/> directly.
/// mithril#1095: <c>ProjectMarkers</c> now takes a <see cref="MapViewFix"/>
/// instead of <c>double currentZoom</c>; the fix is the real layer-2
/// measurement (pan + scale from the cross-correlation probe).
/// </summary>
public sealed class OverlayProjectionTests
{
    private sealed record TestStyle(string Tag) : IMarkerStyle;

    private static MarkerSnapshot Snap(double x, double z, IMarkerStyle style)
        => new(new MarkerHandle(Guid.NewGuid()), new WorldCoord(x, 0, z), style);

    /// <summary>
    /// Identity cal: OriginX=0, OriginY=0, Scale=1.0, RotationRadians=0,
    /// MirrorNorth=false. At scale=1.0:
    /// canonical pixel = (world.X, -world.Z).
    /// </summary>
    private static WorldToOverlayCalibration IdentityCal() =>
        new(OriginX: 0, OriginY: 0, Scale: 1.0,
            RotationRadians: 0, MirrorNorth: false);

    /// <summary>Identity fix: pan=(0,0), viewScale=1.0 — passes canonical
    /// pixels through unchanged.</summary>
    private static MapViewFix IdentityFix() =>
        new(PanTexPxX: 0, PanTexPxY: 0, ViewScale: 1.0,
            Confidence: 1.0, MeasuredAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Projects_each_marker_through_composed_cal_and_fix()
    {
        // Scale=2.0, no rotation, no mirror, identity fix:
        //   canonical = (0 + 2*X, 0 - 2*Z); fix pan=0, scale=1 → same result
        // Snap(10, 20) → (20, -40); Snap(-5, 7) → (-10, -14)
        var cal = new WorldToOverlayCalibration(
            OriginX: 0, OriginY: 0, Scale: 2.0,
            RotationRadians: 0, MirrorNorth: false);

        var styleA = new TestStyle("a");
        var styleB = new TestStyle("b");
        var markers = new[]
        {
            Snap(10.0, 20.0, styleA),
            Snap(-5.0, 7.0, styleB),
        };

        var projected = OverlayWindowService.ProjectMarkers(markers, cal, IdentityFix());

        projected.Should().HaveCount(2);
        projected[0].Should().Be((new OverlayPixel(20, -40), (IMarkerStyle)styleA));
        projected[1].Should().Be((new OverlayPixel(-10, -14), (IMarkerStyle)styleB));
    }

    [Fact]
    public void Returns_empty_when_marker_list_is_empty()
    {
        OverlayWindowService
            .ProjectMarkers(Array.Empty<MarkerSnapshot>(), IdentityCal(), IdentityFix())
            .Should().BeEmpty();
    }

    [Fact]
    public void Null_composedCal_yields_empty_projection_list()
    {
        // mithril#1081: a null composed cal (no usable calibration this frame —
        // uncalibrated area, catalogue miss, null-sha, or surface unsized) must
        // silently skip all markers. Per-scene miss telemetry fires at the
        // OnSurfaceRender level, not here.
        var style = new TestStyle("s");
        var markers = new[]
        {
            Snap(1.0, 1.0, style),
            Snap(2.0, 2.0, style),
        };

        OverlayWindowService.ProjectMarkers(markers, composedCal: null, IdentityFix())
            .Should().BeEmpty("null composed cal means no usable calibration this frame — all markers silently suppressed.");
    }

    [Fact]
    public void Uncalibrated_area_yields_an_empty_projection_list()
    {
        // Passing null composedCal is the standard path for an uncalibrated area
        // (ResolveComposedOverlayCalibration returns null when no calibration exists).
        var style = new TestStyle("s");
        var markers = new[]
        {
            Snap(1.0, 1.0, style),
        };

        OverlayWindowService.ProjectMarkers(markers, composedCal: null, IdentityFix())
            .Should().BeEmpty();
    }

    [Fact]
    public void Style_references_flow_through_projection_unmodified()
    {
        var style = new TestStyle("identity");
        var markers = new[]
        {
            Snap(0.0, 0.0, style),
        };

        var projected = OverlayWindowService.ProjectMarkers(markers, IdentityCal(), IdentityFix());
        projected.Single().Style.Should().BeSameAs(style);
    }
}
