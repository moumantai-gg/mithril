using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

/// <summary>
/// mithril#1081 — AreaCalibration grows a PixelSha256 field carrying the
/// base texture's identity. AutoCal stamps at solve time; the overlay
/// uses it to look up dims in the canonical-asset-hash catalogue. Pre-#1081
/// records load with PixelSha256 = null and fail-soft at the render path.
/// </summary>
public sealed class AreaCalibrationTextureShaTests
{
    [Fact]
    public void PixelSha256_RoundTripThroughJson()
    {
        var record = new AreaCalibration(
            Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            ReferenceCount: 5, ResidualPixels: 0.5)
        {
            Source = CalibrationSource.AutoCapture,
            Frame = CalibrationFrame.Texture,
            PixelSha256 = "abc123def",
        };

        var json = JsonSerializer.Serialize(record, MapCalibrationJsonContext.Default.AreaCalibration);

        json.Should().Contain("\"pixelSha256\": \"abc123def\"");

        var roundTrip = JsonSerializer.Deserialize(json, MapCalibrationJsonContext.Default.AreaCalibration);
        roundTrip!.PixelSha256.Should().Be("abc123def");
    }

    [Fact]
    public void AbsentPixelSha256_DeserialiseAsNull()
    {
        // Pre-#1081 records omit pixelSha256. STJ should default to null,
        // which the overlay's compose helper short-circuits to "no render."
        var preStampJson = """
            {
              "scale": 1.0,
              "rotationRadians": 0.0,
              "originX": 0.0,
              "originY": 0.0,
              "referenceCount": 5,
              "residualPixels": 0.5,
              "source": "AutoCapture",
              "frame": "Texture"
            }
            """;

        var deserialised = JsonSerializer.Deserialize(preStampJson, MapCalibrationJsonContext.Default.AreaCalibration);

        deserialised!.PixelSha256.Should().BeNull();
    }

    [Fact]
    public void UnknownFutureField_IgnoredWhenLoading()
    {
        // Forward-compat — STJ ignores unknown fields next to PixelSha256.
        var futureJson = """
            {
              "scale": 1.0, "rotationRadians": 0.0, "originX": 0.0, "originY": 0.0,
              "referenceCount": 5, "residualPixels": 0.5,
              "source": "AutoCapture", "frame": "Texture",
              "pixelSha256": "abc123",
              "futureFieldX": "ignored"
            }
            """;

        var deserialised = JsonSerializer.Deserialize(futureJson, MapCalibrationJsonContext.Default.AreaCalibration);

        deserialised!.PixelSha256.Should().Be("abc123");
    }
}
