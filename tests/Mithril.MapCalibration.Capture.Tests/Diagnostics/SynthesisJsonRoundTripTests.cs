using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

public sealed class SynthesisJsonRoundTripTests
{
    [Fact]
    public void SynthesisJson_round_trips_through_source_gen_context()
    {
        var original = new SynthesisJson(
            SchemaVersion: 1,
            Mode: "shadow",
            Rotate180: false,
            J: 7.5,
            JMin: 8.0,
            RefsAboveHalf: 6,
            RefsTotal: 11,
            RefsOffCrop: 2,
            NMin: 8,
            Verdict: "reject",
            GateVerdict: "accept",
            Disagree: true,
            DisagreeChange: "accept_to_reject");

        var json = JsonSerializer.Serialize(original, CalibrationBundleJsonContext.Default.SynthesisJson);
        var parsed = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.SynthesisJson);

        parsed.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void AttemptJson_schemaVersion_is_3_and_carries_optional_Synthesis()
    {
        var attempt = new AttemptJson(
            SchemaVersion: 3,
            Area: "Map_Test",
            AttemptStartedUtc: "2026-06-08T19:37:13Z",
            AttemptFinalizedUtc: "2026-06-08T19:37:14Z",
            Outcome: "accepted",
            RejectReason: null,
            EngineVersion: "1.0.0",
            Files: new AttemptFilesJson(null, null, null, null, null, null, null, null, null, null),
            LocatorBest: null,
            Synthesis: new SynthesisJson(
                SchemaVersion: 1, Mode: "shadow", Rotate180: false,
                J: 2.0, JMin: 8.0, RefsAboveHalf: 1, RefsTotal: 4, RefsOffCrop: 0,
                NMin: 8, Verdict: "reject", GateVerdict: "accept",
                Disagree: true, DisagreeChange: "accept_to_reject"));

        var json = JsonSerializer.Serialize(attempt, CalibrationBundleJsonContext.Default.AttemptJson);

        json.Should().Contain("\"schemaVersion\": 3");
        json.Should().Contain("\"synthesis\":");
        json.Should().Contain("\"disagreeChange\": \"accept_to_reject\"");
    }
}
