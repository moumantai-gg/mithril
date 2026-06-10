using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests.Diagnostics;

/// <summary>
/// mithril#1070 schema v3 round-trip + v2-fallback tests for
/// <see cref="LocatorBestJson"/>. Pins:
/// (a) a v3 payload round-trips through the source-gen context with
///     BlurAppliedSigma populated;
/// (b) a v2 payload (no blurAppliedSigma key) deserialises with the field
///     defaulted to null — pre-#1070 readers stay correct.
/// </summary>
public sealed class LocatorBestJsonV3Tests
{
    [Fact]
    public void V3_round_trips_with_BlurAppliedSigma()
    {
        var sut = new LocatorBestJson(
            SchemaVersion: 3,
            OriginX: 617, OriginY: 543,
            Width: 287, Height: 287,
            TextureWidth: 1024, TextureHeight: 1024,
            InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
            Scale: 0.2800, RotationDegrees: 0, Tx: 617.0, Ty: 543.0, ResidualPixels: 0,
            GateAccepted: false, GateRejectReason: "downstream solve failed",
            Algorithm: "sobel-padded-pyramid",
            FallbackNcc: 0.569,
            PadPx: 100,
            BlurAppliedSigma: 2.023);

        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.LocatorBestJson);
        json.Should().Contain("\"blurAppliedSigma\": 2.023");

        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.LocatorBestJson);

        round.Should().NotBeNull();
        round!.SchemaVersion.Should().Be(3);
        round.BlurAppliedSigma.Should().Be(2.023);
        round.Algorithm.Should().Be("sobel-padded-pyramid");
        round.FallbackNcc.Should().Be(0.569);
    }

    [Fact]
    public void V2_payload_reads_with_BlurAppliedSigma_defaulted_null()
    {
        // A v2 file written by a pre-#1070 engine. The new field is absent;
        // STJ source-gen must use the record's optional-parameter default.
        var v2Payload = """
        {
          "schemaVersion": 2,
          "originX": 127, "originY": 35,
          "width": 591, "height": 740,
          "textureWidth": 819, "textureHeight": 1024,
          "inlierCount": 0, "candidateCount": 0, "inlierRatio": 0,
          "scale": 0.7227, "rotationDegrees": 0,
          "tx": 127.5, "ty": 35.8, "residualPixels": 0,
          "gateAccepted": true, "gateRejectReason": null,
          "algorithm": "sobel-padded-pyramid",
          "fallbackNcc": 0.680,
          "padPx": 100
        }
        """;

        var round = JsonSerializer.Deserialize(v2Payload, CalibrationBundleJsonContext.Default.LocatorBestJson);

        round.Should().NotBeNull();
        round!.SchemaVersion.Should().Be(2);
        round.Algorithm.Should().Be("sobel-padded-pyramid");
        round.FallbackNcc.Should().Be(0.680);
        round.PadPx.Should().Be(100);
        // Default-null for v2-formatted input — the spec's reader contract.
        round.BlurAppliedSigma.Should().BeNull();
    }

    [Fact]
    public void V3_round_trips_with_null_BlurAppliedSigma_on_orb_primary()
    {
        // ORB primary doesn't blur — the sink emits null on this field even
        // though it writes SchemaVersion=3. Pin the contract.
        var sut = new LocatorBestJson(
            SchemaVersion: 3,
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
            // Algorithm defaults to "orb-lowe"; FallbackNcc, PadPx,
            // BlurAppliedSigma all default-null.

        var json = JsonSerializer.Serialize(sut, CalibrationBundleJsonContext.Default.LocatorBestJson);
        var round = JsonSerializer.Deserialize(json, CalibrationBundleJsonContext.Default.LocatorBestJson);

        round.Should().NotBeNull();
        round!.SchemaVersion.Should().Be(3);
        round.Algorithm.Should().Be("orb-lowe");
        round.BlurAppliedSigma.Should().BeNull();
    }
}
