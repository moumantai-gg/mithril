using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingRefinerReplayTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CalibrationBundles");

    private readonly ITestOutputHelper _output;

    public FeatureMatchingRefinerReplayTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static (GrayImage capture, GrayImage texture) LoadBundle(string folder, string areaKey)
    {
        var capturePath = Path.Combine(FixturesRoot, folder, "capture.png");
        var capture = PngFixtureLoader.LoadGray(capturePath);

        var provider = new CachedBaseTextureProvider(Path.Combine(FixturesRoot, folder));
        var texture = provider.TryGetBaseTexture(areaKey)
                      ?? throw new InvalidOperationException(
                          $"Fixture {folder}: no base texture for area {areaKey}");

        return (capture, texture);
    }

    private void DumpMetrics(string label, MapRegionRefineResult result)
    {
        _output.WriteLine($"--- {label} ---");
        if (result.Metrics is { } m)
        {
            _output.WriteLine(
                $"InlierCount={m.InlierCount} CandidateCount={m.CandidateCount} "
                + $"InlierRatio={m.InlierRatio:0.0000} Scale={m.Scale:0.000000} "
                + $"RotationDegrees={m.RotationDegrees:0.000000} "
                + $"Tx={m.Tx:0.0000} Ty={m.Ty:0.0000} "
                + $"ResidualPixels={m.ResidualPixels:0.0000}");
        }
        else
        {
            _output.WriteLine("Metrics: <null>");
        }
        if (result.AcceptedRect is { } r)
        {
            _output.WriteLine(
                $"AcceptedRect: OriginX={r.OriginX} OriginY={r.OriginY} "
                + $"Width={r.Width} Height={r.Height} "
                + $"TextureWidth={r.TextureWidth} TextureHeight={r.TextureHeight}");
        }
        else
        {
            _output.WriteLine("AcceptedRect: <null>");
        }
        if (result.RawFitRect is { } raw)
        {
            _output.WriteLine(
                $"RawFitRect:   OriginX={raw.OriginX} OriginY={raw.OriginY} "
                + $"Width={raw.Width} Height={raw.Height}");
        }
    }

    [Fact]
    public void Recovers_kur_mountains_live_ground_truth_rect_within_two_pixels()
    {
        var (capture, texture) = LoadBundle(
            "KurMountains-Live-20260602", "AreaKurMountains");

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, texture, minScore: 0);
        DumpMetrics("KurMountains-Live", result);

        result.AcceptedRect.Should().NotBeNull(
            "the new locator must succeed on the Kur live bundle that the old NCC ladder rejected");
        result.Metrics.Should().NotBeNull();
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);
        result.Metrics.InlierCount.Should().BeGreaterThan(500);

        // Ground truth: (159, 82, 971, 973) per PR #1008's investigation
        // (https://github.com/moumantai-gg/mithril/pull/1008).
        result.AcceptedRect!.OriginX.Should().BeCloseTo(159, 2);
        result.AcceptedRect.OriginY.Should().BeCloseTo(82, 2);
        result.AcceptedRect.Width.Should().BeCloseTo(971, 2);
        result.AcceptedRect.Height.Should().BeCloseTo(973, 2);
    }

    [Fact]
    public void Recovers_eltibule_accepted_rect_consistent_with_ncc_ground_truth()
    {
        var (capture, texture) = LoadBundle(
            "Eltibule-Accepted-20260602", "AreaEltibule");

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, texture, minScore: 0);
        DumpMetrics("Eltibule-Accepted", result);

        result.AcceptedRect.Should().NotBeNull(
            "Eltibule is the working-zone positive control");
        result.Metrics.Should().NotBeNull();
        // The Eltibule full-screenshot fixture clears the gate floor (0.50)
        // comfortably; observed ratio ~0.85 — lower than the synthetic-clean
        // Kur live bundle because the screenshot has more non-map area (HUD,
        // chat overlay, world) generating off-texture ORB descriptors that
        // Lowe's ratio test promotes but RANSAC drops as outliers.
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.80);

        // Ground truth from the source bundle's 04-maprect.json
        // (NCC-recovered rect from the accepted Eltibule attempt):
        // OriginX=366, OriginY=281, Width=562, Height=558.
        result.AcceptedRect!.OriginX.Should().BeCloseTo(366, 2);
        result.AcceptedRect.OriginY.Should().BeCloseTo(281, 2);
        result.AcceptedRect.Width.Should().BeCloseTo(562, 2);
        result.AcceptedRect.Height.Should().BeCloseTo(558, 2);
    }
}
