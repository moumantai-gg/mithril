using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class CalibrationBundleJsonTests
{
    [Fact]
    public void AttemptJson_round_trips_through_source_gen_context()
    {
        var sut = new AttemptJson(
            SchemaVersion: 2,
            Area: "AreaEltibule",
            AttemptStartedUtc: "2026-06-01T12:30:12.696Z",
            AttemptFinalizedUtc: "2026-06-01T12:30:14.812Z",
            Outcome: "accepted",
            RejectReason: null,
            EngineVersion: "0.5.0+test",
            Files: new AttemptFilesJson(
                RawScreenshot: "02-screenshot-raw.png",
                GrayScreenshot: "03-screenshot-gray.png",
                MapRect: "04-maprect.json",
                BaseTextureResampled: "05-base-texture-resampled.png",
                AlignedScreenshot: "06-aligned-screenshot.png",
                Deviation: "07-deviation.png",
                DetectionsImage: "08-detections.png",
                ProjectionOverlay: "09-projection-overlay.png",
                Detections: "10-detections.json",
                RecoveredCalibration: "11-recovered-cal.json"));

        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.AttemptJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.AttemptJson);

        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void MapRectJson_round_trips()
    {
        var sut = new MapRectJson(1, 12, 18, 1192, 1020, 4096, 4096);
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.MapRectJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.MapRectJson);
        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void LocatorBestJson_round_trips()
    {
        var sut = new LocatorBestJson(
            SchemaVersion: 1,
            OriginX: 192, OriginY: 100,
            Width: 909, Height: 909,
            TextureWidth: 2048, TextureHeight: 2048,
            InlierCount: 624,
            CandidateCount: 731,
            InlierRatio: 0.853,
            Scale: 1.0007,
            RotationDegrees: 0.12,
            Tx: 191.4,
            Ty: 99.8,
            ResidualPixels: 0.41,
            GateAccepted: true,
            GateRejectReason: null);
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.LocatorBestJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.LocatorBestJson);
        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void DetectionsJson_round_trips()
    {
        var sut = new DetectionsJson(1, 16,
            new[] { new DetectionJson("Portal", "landmark_portal", 412.7, 588.3, 0.94) });
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.DetectionsJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.DetectionsJson);
        round.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void RecoveredCalibrationJson_round_trips()
    {
        var sut = new RecoveredCalibrationJson(1,
            Scale: 0.31536, RotationRadians: -3.14159,
            OriginX: 1039.45, OriginY: -36.38,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.34, ReferenceCount: 8,
            Source: "AutoCapture",
            Inliers: new[] { new InlierJson("Portal:E→S", 234.1, -78.5, 612.3, 488.7, 0.94) });
        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.RecoveredCalibrationJson);
        round.Should().BeEquivalentTo(sut);
    }
}
