using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// WindowToWorld must invert WorldToWindow within numeric tolerance for any
/// valid similarity transform + zoom factor. Covers both MirrorNorth branches
/// and the no-pan zoom-factor scaling.
/// </summary>
public sealed class AreaCalibrationRoundTripTests
{
    [Theory]
    [InlineData(false, 1.0, 1.0)]
    [InlineData(true, 1.0, 1.0)]
    [InlineData(false, 1.0, 2.5)]   // currentZoom > CalibrationZoom
    [InlineData(false, 2.0, 1.0)]   // currentZoom < CalibrationZoom
    [InlineData(true, 0.5, 1.75)]   // mirrored, both zoom branches
    public void Round_trip_recovers_input_pixel(bool mirrorNorth, double calibrationZoom, double currentZoom)
    {
        var rng = new Random(1234);
        var cal = new AreaCalibration(
            Scale: 1.875,
            RotationRadians: 0.731,
            OriginX: 412.5,
            OriginY: 389.2,
            ReferenceCount: 3,
            ResidualPixels: 4.2)
        {
            MirrorNorth = mirrorNorth,
            CalibrationZoom = calibrationZoom,
        };

        for (var i = 0; i < 100; i++)
        {
            var pixel = new PixelPoint(rng.NextDouble() * 1000 - 500, rng.NextDouble() * 1000 - 500);
            var world = cal.WindowToWorld(pixel, currentZoom);
            world.Should().NotBeNull();
            var back = cal.WorldToWindow(world!.Value, currentZoom);
            back.X.Should().BeApproximately(pixel.X, 1e-9);
            back.Y.Should().BeApproximately(pixel.Y, 1e-9);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void World_round_trip_preserves_ground_plane(bool mirrorNorth)
    {
        var cal = new AreaCalibration(
            Scale: 2.0,
            RotationRadians: -0.42,
            OriginX: 100,
            OriginY: 200,
            ReferenceCount: 4,
            ResidualPixels: 1.0)
        {
            MirrorNorth = mirrorNorth,
        };

        var world = new WorldCoord(123.5, 17.0, -45.25); // Y elevation is irrelevant to projection
        var pixel = cal.WorldToWindow(world, currentZoom: 1.0);
        var roundTripped = cal.WindowToWorld(pixel, currentZoom: 1.0);

        roundTripped.Should().NotBeNull();
        roundTripped!.Value.X.Should().BeApproximately(world.X, 1e-9);
        roundTripped.Value.Z.Should().BeApproximately(world.Z, 1e-9);
        // Y cannot be recovered from a 2D pixel; spec says result is always 0.
        roundTripped.Value.Y.Should().Be(0);
    }

    [Fact]
    public void WindowToWorld_returns_null_for_degenerate_scale()
    {
        var degenerate = new AreaCalibration(
            Scale: 0,
            RotationRadians: 0,
            OriginX: 0,
            OriginY: 0,
            ReferenceCount: 0,
            ResidualPixels: 0);
        degenerate.WindowToWorld(new PixelPoint(1, 1), currentZoom: 1.0).Should().BeNull();
    }

    [Fact]
    public void Roundtrip_preserves_LocatorScale()
    {
        var original = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            ReferenceCount: 5, ResidualPixels: 0.5)
        {
            LocatorScale = 0.408,
        };

        var json = JsonSerializer.Serialize(original, MapCalibrationJsonContext.Default.AreaCalibration);
        var rt = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);

        rt.Should().NotBeNull();
        rt!.LocatorScale.Should().Be(0.408);
    }

    [Fact]
    public void Roundtrip_omits_LocatorScale_from_JSON_when_null()
    {
        var original = new AreaCalibration(1.0, 0.0, 0.0, 0.0, 5, 0.5);

        var json = JsonSerializer.Serialize(original, MapCalibrationJsonContext.Default.AreaCalibration);

        // DefaultIgnoreCondition = WhenWritingDefault means null nullable doubles
        // are omitted. This is the downgrade-safety guarantee.
        json.Should().NotContain("locatorScale", "null nullable doubles should be omitted");

        var rt = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        rt!.LocatorScale.Should().BeNull();
    }

    [Fact]
    public void Roundtrip_accepts_legacy_JSON_without_LocatorScale_field()
    {
        const string legacyJson = """
            {"scale":1.0,"rotationRadians":0.0,"originX":0.0,"originY":0.0,
             "referenceCount":5,"residualPixels":0.5}
            """;

        var rt = JsonSerializer.Deserialize(legacyJson, MapCalibrationJsonContext.Default.AreaCalibration);

        rt.Should().NotBeNull();
        rt!.LocatorScale.Should().BeNull();
    }

    [Fact]
    public void Zoom_factor_scales_world_to_window_linearly()
    {
        var cal = new AreaCalibration(
            Scale: 4.0,
            RotationRadians: 0,
            OriginX: 100,
            OriginY: 100,
            ReferenceCount: 3,
            ResidualPixels: 0)
        {
            CalibrationZoom = 1.0,
        };

        var world = new WorldCoord(10, 0, 5);

        var at1x = cal.WorldToWindow(world, currentZoom: 1.0);
        var at2x = cal.WorldToWindow(world, currentZoom: 2.0);

        // At 2x zoom, the offset from origin doubles.
        (at2x.X - cal.OriginX).Should().BeApproximately(2.0 * (at1x.X - cal.OriginX), 1e-9);
        (at2x.Y - cal.OriginY).Should().BeApproximately(2.0 * (at1x.Y - cal.OriginY), 1e-9);
    }
}
