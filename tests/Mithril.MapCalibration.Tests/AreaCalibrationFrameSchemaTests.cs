using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// Schema 2 (mithril#1076) bumps <see cref="AreaCalibration"/> with an explicit
/// <c>frame</c> field tagging the output frame of the projection
/// (<see cref="CalibrationFrame.Texture"/> for AutoCalibration-RANSAC output,
/// <see cref="CalibrationFrame.Overlay"/> for Legolas-wizard output). The field
/// is additive; Schema-1 records (no <c>frame</c> on disk) load via the
/// Source-based inference table documented in spec §7.2.
/// </summary>
public sealed class AreaCalibrationFrameSchemaTests
{
    [Fact]
    public void Schema2_RoundTripsFrameField_Overlay()
    {
        var record = new AreaCalibration(
            Scale: 1.5, RotationRadians: 0.1, OriginX: 200.0, OriginY: 150.0,
            ReferenceCount: 5, ResidualPixels: 1.2)
        {
            Source = CalibrationSource.UserRefinement,
            Frame = CalibrationFrame.Overlay,
        };

        var json = JsonSerializer.Serialize(record, MapCalibrationJsonContext.Default.AreaCalibration);

        json.Should().Contain("\"frame\"", "the camelCase property name must be present");
        json.Should().Contain("\"Overlay\"", "UseStringEnumConverter must emit the enum member name");

        var roundTrip = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        roundTrip.Should().NotBeNull();
        roundTrip!.Frame.Should().Be(CalibrationFrame.Overlay);
    }

    [Fact]
    public void Schema2_RoundTripsFrameField_Texture()
    {
        var record = new AreaCalibration(
            Scale: 0.8, RotationRadians: 0.0, OriginX: 100.0, OriginY: 200.0,
            ReferenceCount: 8, ResidualPixels: 0.5)
        {
            Source = CalibrationSource.AutoCapture,
            Frame = CalibrationFrame.Texture,
        };

        var json = JsonSerializer.Serialize(record, MapCalibrationJsonContext.Default.AreaCalibration);

        json.Should().Contain("\"Texture\"");

        var roundTrip = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        roundTrip!.Frame.Should().Be(CalibrationFrame.Texture);
    }
}
