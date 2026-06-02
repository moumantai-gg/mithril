using System.Text.Json;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class BundleJsonDtosTests
{
    [Fact]
    public void MapRectJson_round_trips()
    {
        const string json = """
            { "schemaVersion": 1,
              "originX": 130, "originY": 60,
              "width": 995, "height": 986,
              "textureWidth": 2048, "textureHeight": 2033,
              "autoDetectScore": null, "sourceScaleFactor": null }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.MapRectJson)!;

        parsed.SchemaVersion.Should().Be(1);
        parsed.OriginX.Should().Be(130);
        parsed.OriginY.Should().Be(60);
        parsed.Width.Should().Be(995);
        parsed.Height.Should().Be(986);
        parsed.TextureWidth.Should().Be(2048);
        parsed.TextureHeight.Should().Be(2033);
        parsed.AutoDetectScore.Should().BeNull();
        parsed.SourceScaleFactor.Should().BeNull();
    }

    [Fact]
    public void RecoveredCalibrationJson_round_trips_with_inliers()
    {
        const string json = """
            { "schemaVersion": 1,
              "scale": 0.31536, "rotationRadians": -3.14153,
              "originX": 1039.45, "originY": -36.38,
              "mirrorNorth": false, "calibrationZoom": 1.0,
              "residualPixels": 0.34, "referenceCount": 4,
              "source": "UserRefinement",
              "inliers": [
                { "label": "Meditation Pillar", "worldX": 916.8, "worldZ": 2428.8,
                  "pixelX": 179.8, "pixelY": 235.6, "matchScore": 0.921 }
              ] }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.RecoveredCalibrationJson)!;

        parsed.Scale.Should().BeApproximately(0.31536, 1e-9);
        parsed.MirrorNorth.Should().BeFalse();
        parsed.Inliers.Should().HaveCount(1);
        parsed.Inliers[0].Label.Should().Be("Meditation Pillar");
    }

    [Fact]
    public void AttemptJson_round_trips()
    {
        const string json = """
            { "schemaVersion": 1,
              "area": "AreaEltibule",
              "attemptStartedUtc": "2026-06-02T01:23:45Z",
              "attemptFinalizedUtc": "2026-06-02T01:23:46Z",
              "outcome": "accepted",
              "rejectReason": null,
              "engineVersion": "1.0.0",
              "files": {
                "rawScreenshot": "02-screenshot-raw.png",
                "grayScreenshot": "03-screenshot-gray.png",
                "mapRect": "04-maprect.json",
                "baseTextureResampled": "05-base-resampled.png",
                "alignedScreenshot": "06-aligned-screenshot.png",
                "deviation": "07-deviation.png",
                "detectionsImage": "08-detections.png",
                "projectionOverlay": "09-projection-overlay.png",
                "detections": "10-detections.json",
                "recoveredCalibration": "11-recovered-cal.json"
              } }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.AttemptJson)!;

        parsed.Area.Should().Be("AreaEltibule");
        parsed.Outcome.Should().Be("accepted");
        parsed.RejectReason.Should().BeNull();
        parsed.Files.Deviation.Should().Be("07-deviation.png");
        parsed.Files.RecoveredCalibration.Should().Be("11-recovered-cal.json");
    }

    [Fact]
    public void DetectionsJson_round_trips()
    {
        const string json = """
            { "schemaVersion": 1,
              "renderSizePx": 32,
              "detections": [
                { "landmarkType": "Portal", "iconName": "landmark_portal",
                  "anchorX": 123.45, "anchorY": 678.9, "score": 0.873 }
              ] }
            """;

        var parsed = JsonSerializer.Deserialize(json, BundleJsonContext.Default.DetectionsJson)!;

        parsed.SchemaVersion.Should().Be(1);
        parsed.RenderSizePx.Should().Be(32);
        parsed.Detections.Should().HaveCount(1);
        parsed.Detections[0].LandmarkType.Should().Be("Portal");
        parsed.Detections[0].IconName.Should().Be("landmark_portal");
        parsed.Detections[0].AnchorX.Should().BeApproximately(123.45, 1e-9);
        parsed.Detections[0].Score.Should().BeApproximately(0.873, 1e-9);
    }
}
