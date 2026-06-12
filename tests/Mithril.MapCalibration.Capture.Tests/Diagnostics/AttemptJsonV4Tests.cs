using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

/// <summary>
/// mithril#1116 — AttemptJson v3 → v4 additive bump that adds
/// <see cref="AttemptFilesJson.DeviationMask"/> (the new
/// <c>07a-deviation-mask.png</c> artifact path).
///
/// <para>v4 is additive: a v3-shaped JSON payload (no <c>deviationMask</c>
/// key) must still deserialize cleanly, with the missing property defaulting
/// to <c>null</c>. New writers stamp <c>schemaVersion: 4</c> and populate
/// <c>deviationMask</c> when the engine wrote the mask PNG.</para>
/// </summary>
public sealed class AttemptJsonV4Tests
{
    [Fact]
    public void V3_AttemptJson_without_DeviationMask_reads_as_null()
    {
        // Synthetic v3 JSON — no deviationMask key in the files map. The
        // file shape mirrors what pre-#1116 sinks produced (all 10c-era
        // optional fields stay null; we drop them entirely from the JSON
        // to prove DEFAULT-VALUE behaviour rather than relying on the
        // PropertyNamingPolicy to spell them out).
        var json = """
        {
          "schemaVersion": 3,
          "area": "Map_Test",
          "attemptStartedUtc": "2026-06-12T00:00:00Z",
          "attemptFinalizedUtc": "2026-06-12T00:00:01Z",
          "outcome": "accepted",
          "rejectReason": null,
          "engineVersion": "test",
          "files": {
            "rawScreenshot": null,
            "grayScreenshot": null,
            "mapRect": null,
            "baseTextureResampled": null,
            "alignedScreenshot": null,
            "deviation": null,
            "detectionsImage": null,
            "projectionOverlay": null,
            "detections": null,
            "recoveredCalibration": null
          }
        }
        """;

        var attempt = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.AttemptJson);

        attempt.Should().NotBeNull();
        attempt!.SchemaVersion.Should().Be(3);
        attempt.Files.DeviationMask.Should().BeNull();
    }

    [Fact]
    public void V4_AttemptJson_with_DeviationMask_round_trips()
    {
        // Build a v4 record explicitly through the C# API and round-trip
        // it through the source-gen context. The serialized JSON must
        // carry the camelCase "deviationMask" property with the artifact
        // filename, and the round-tripped record must preserve both the
        // schema version and the mask path.
        var attempt = new AttemptJson(
            SchemaVersion: 4,
            Area: "Map_Test",
            AttemptStartedUtc: "2026-06-12T00:00:00Z",
            AttemptFinalizedUtc: "2026-06-12T00:00:01Z",
            Outcome: "accepted",
            RejectReason: null,
            EngineVersion: "test",
            Files: new AttemptFilesJson(
                RawScreenshot: null,
                GrayScreenshot: null,
                MapRect: null,
                BaseTextureResampled: null,
                AlignedScreenshot: null,
                Deviation: null,
                DetectionsImage: null,
                ProjectionOverlay: null,
                Detections: null,
                RecoveredCalibration: null,
                DeviationMask: "07a-deviation-mask.png"));

        var json = JsonSerializer.Serialize(attempt, CalibrationBundleJsonContext.Default.AttemptJson);
        // The source-gen context uses WriteIndented = true, so the key/value
        // are separated by ": " (with a space). Match by substring on the
        // key + the value to stay tolerant of formatting.
        json.Should().Contain("\"deviationMask\"");
        json.Should().Contain("\"07a-deviation-mask.png\"");

        var rt = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.AttemptJson);
        rt.Should().NotBeNull();
        rt!.Files.DeviationMask.Should().Be("07a-deviation-mask.png");
        rt.SchemaVersion.Should().Be(4);
    }
}
