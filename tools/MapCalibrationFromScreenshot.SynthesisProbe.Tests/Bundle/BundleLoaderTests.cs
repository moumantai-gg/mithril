using System.IO;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class BundleLoaderTests
{
    [Fact]
    public void Loads_full_bundle_with_recovered_cal()
    {
        var dir = NewBundleDir();
        try
        {
            WriteAttemptJson(dir, outcome: "accepted", includeRecoveredCal: true);
            WriteMapRectJson(dir);
            WriteRecoveredCalJson(dir);
            File.WriteAllBytes(Path.Combine(dir, "07-deviation.png"), new byte[1]); // placeholder

            var bundle = BundleLoader.Open(dir);

            bundle.Attempt.Outcome.Should().Be("accepted");
            bundle.Attempt.Area.Should().Be("AreaEltibule");
            bundle.MapRect.Should().NotBeNull();
            bundle.RecoveredCal.Should().NotBeNull();
            bundle.DeviationPath.Should().EndWith("07-deviation.png");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Loads_rejected_bundle_with_null_recovered_cal()
    {
        var dir = NewBundleDir();
        try
        {
            WriteAttemptJson(dir, outcome: "rejected-3inliers", includeRecoveredCal: false);
            WriteMapRectJson(dir);
            File.WriteAllBytes(Path.Combine(dir, "07-deviation.png"), new byte[1]);

            var bundle = BundleLoader.Open(dir);

            bundle.Attempt.Outcome.Should().Be("rejected-3inliers");
            bundle.MapRect.Should().NotBeNull();
            bundle.RecoveredCal.Should().BeNull("rejected attempts have no recovered-cal");
            bundle.DeviationPath.Should().EndWith("07-deviation.png");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Throws_when_attempt_json_missing()
    {
        var dir = NewBundleDir();
        try
        {
            var act = () => BundleLoader.Open(dir);
            act.Should().Throw<FileNotFoundException>();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewBundleDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "synth-probe-bundle-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteAttemptJson(string dir, string outcome, bool includeRecoveredCal)
    {
        var recoveredField = includeRecoveredCal ? "\"11-recovered-cal.json\"" : "null";
        File.WriteAllText(Path.Combine(dir, "01-attempt.json"), $$"""
            { "schemaVersion": 1,
              "area": "AreaEltibule",
              "attemptStartedUtc": "2026-06-02T01:00:00Z",
              "attemptFinalizedUtc": "2026-06-02T01:00:01Z",
              "outcome": "{{outcome}}",
              "rejectReason": null,
              "engineVersion": "1.0.0",
              "files": {
                "rawScreenshot": "02-screenshot-raw.png",
                "grayScreenshot": "03-screenshot-gray.png",
                "mapRect": "04-maprect.json",
                "baseTextureResampled": null,
                "alignedScreenshot": null,
                "deviation": "07-deviation.png",
                "detectionsImage": null,
                "projectionOverlay": null,
                "detections": null,
                "recoveredCalibration": {{recoveredField}}
              } }
            """);
    }

    private static void WriteMapRectJson(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "04-maprect.json"), """
            { "schemaVersion": 1, "originX": 130, "originY": 60,
              "width": 1013, "height": 1001,
              "textureWidth": 2048, "textureHeight": 2033,
              "autoDetectScore": null, "sourceScaleFactor": null }
            """);
    }

    private static void WriteRecoveredCalJson(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "11-recovered-cal.json"), """
            { "schemaVersion": 1, "scale": 0.31536, "rotationRadians": -3.14153,
              "originX": 1039.45, "originY": -36.38, "mirrorNorth": false,
              "calibrationZoom": 1.0, "residualPixels": 0.34,
              "referenceCount": 4, "source": "UserRefinement", "inliers": [] }
            """);
    }
}
