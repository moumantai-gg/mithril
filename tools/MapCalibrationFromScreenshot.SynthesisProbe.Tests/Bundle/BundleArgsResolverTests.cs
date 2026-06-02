using System;
using System.IO;
using FluentAssertions;
using Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Bundle;
using Xunit;

namespace Mithril.Tools.MapCalibrationFromScreenshot.SynthesisProbe.Tests.Bundle;

public class BundleArgsResolverTests
{
    // ─── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal bundle directory fixture.
    /// </summary>
    private static BundleFixture CreateFixture(
        string area = "AreaEltibule",
        string? grayScreenshot = "03-screenshot-gray.png",
        string? rawScreenshot = "02-screenshot-raw.png",
        int mapRectOriginX = 130,
        int mapRectOriginY = 60,
        int mapRectWidth = 1006,
        int mapRectHeight = 999,      // intentionally stale — Bundle B 031130-122
        bool writeDeviationPng = false,
        int deviationWidth = 1006,
        int deviationHeight = 986)    // actual post-clamp value
    {
        return new BundleFixture(
            area, grayScreenshot, rawScreenshot,
            mapRectOriginX, mapRectOriginY, mapRectWidth, mapRectHeight,
            writeDeviationPng, deviationWidth, deviationHeight);
    }

    // ─── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FillsArea_FromBundleWhenExplicitEmpty()
    {
        using var f = CreateFixture(area: "AreaEltibule");
        var (resolvedArea, _, _) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: null,
            alignedDeviationPath: null,
            explicitArea: "",                // not set by caller
            explicitScreenshotPath: null,
            explicitMapRect: null);

        resolvedArea.Should().Be("AreaEltibule");
    }

    [Fact]
    public void Resolve_FillsScreenshot_PreferringGray()
    {
        // Both grayScreenshot and rawScreenshot are in the manifest.
        using var f = CreateFixture(
            grayScreenshot: "03-screenshot-gray.png",
            rawScreenshot: "02-screenshot-raw.png");

        var (_, screenshotPath, _) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: null,
            alignedDeviationPath: null,
            explicitArea: "AreaEltibule",
            explicitScreenshotPath: null,
            explicitMapRect: null);

        screenshotPath.Should().NotBeNull();
        screenshotPath!.Should().EndWith("03-screenshot-gray.png");
    }

    [Fact]
    public void Resolve_FillsScreenshot_FallsBackToRaw()
    {
        // Only rawScreenshot is available (gray is null).
        using var f = CreateFixture(
            grayScreenshot: null,
            rawScreenshot: "02-screenshot-raw.png");

        var (_, screenshotPath, _) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: null,
            alignedDeviationPath: null,
            explicitArea: "AreaEltibule",
            explicitScreenshotPath: null,
            explicitMapRect: null);

        screenshotPath.Should().NotBeNull();
        screenshotPath!.Should().EndWith("02-screenshot-raw.png");
    }

    [Fact]
    public void Resolve_MapRectSizeFromDeviationOverridesJson()
    {
        // Bundle B 031130-122 regression: 04-maprect.json records height=999 but the
        // engine ran at height=986 (clamped to fit the 1047-tall screenshot). The
        // deviation PNG's actual W×H is the authoritative post-clamp truth.
        using var f = CreateFixture(
            mapRectOriginX: 10,
            mapRectOriginY: 20,
            mapRectWidth: 1006,
            mapRectHeight: 999,         // stale value in JSON
            writeDeviationPng: true,
            deviationWidth: 1006,
            deviationHeight: 986);       // authoritative post-clamp size

        var (_, _, mapRect) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: f.MapRectJsonPath,
            alignedDeviationPath: f.DeviationPath,
            explicitArea: "AreaEltibule",
            explicitScreenshotPath: null,
            explicitMapRect: null);

        mapRect.Should().NotBeNull();
        var (x, y, w, h) = mapRect!.Value;
        x.Should().Be(10);
        y.Should().Be(20);
        w.Should().Be(1006);
        h.Should().Be(986, "deviation PNG dimensions override the stale JSON height");
    }

    [Fact]
    public void Resolve_MapRectSizeFallsBackToJson_WhenNoDeviation()
    {
        // Same fixture but no deviation PNG → use JSON dims as-is.
        using var f = CreateFixture(
            mapRectOriginX: 10,
            mapRectOriginY: 20,
            mapRectWidth: 1006,
            mapRectHeight: 999,
            writeDeviationPng: false);

        var (_, _, mapRect) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: f.MapRectJsonPath,
            alignedDeviationPath: null,
            explicitArea: "AreaEltibule",
            explicitScreenshotPath: null,
            explicitMapRect: null);

        mapRect.Should().NotBeNull();
        var (x, y, w, h) = mapRect!.Value;
        x.Should().Be(10);
        y.Should().Be(20);
        w.Should().Be(1006);
        h.Should().Be(999, "no deviation PNG → fall back to JSON height");
    }

    [Fact]
    public void Resolve_DoesNotOverrideExplicitFlags()
    {
        using var f = CreateFixture(area: "AreaEltibule");

        var (area, screenshotPath, mapRect) = BundleArgsResolver.Resolve(
            f.Bundle, f.Dir,
            mapRectJsonPath: null,
            alignedDeviationPath: null,
            explicitArea: "OverrideArea",
            explicitScreenshotPath: "/other.png",
            explicitMapRect: (1, 2, 3, 4));

        area.Should().Be("OverrideArea");
        screenshotPath.Should().Be("/other.png");
        mapRect.Should().Be((1, 2, 3, 4));
    }

    // ─── fixture ────────────────────────────────────────────────────────────────

    private sealed class BundleFixture : IDisposable
    {
        public string Dir { get; }
        public LoadedBundle Bundle { get; }
        public string MapRectJsonPath { get; }
        public string? DeviationPath { get; }

        public BundleFixture(
            string area,
            string? grayScreenshot,
            string? rawScreenshot,
            int originX, int originY, int width, int height,
            bool writeDeviationPng,
            int deviationWidth, int deviationHeight)
        {
            Dir = Path.Combine(Path.GetTempPath(),
                "synth-probe-resolver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);

            // Write 04-maprect.json
            MapRectJsonPath = Path.Combine(Dir, "04-maprect.json");
            File.WriteAllText(MapRectJsonPath, $$"""
                { "schemaVersion": 1,
                  "originX": {{originX}}, "originY": {{originY}},
                  "width": {{width}}, "height": {{height}},
                  "textureWidth": 2048, "textureHeight": 2033,
                  "autoDetectScore": null, "sourceScaleFactor": null }
                """);

            // Optionally write deviation PNG
            if (writeDeviationPng)
            {
                var devPath = Path.Combine(Dir, "07-deviation.png");
                WritePng(devPath, deviationWidth, deviationHeight);
                DeviationPath = devPath;
            }
            else
            {
                DeviationPath = null;
            }

            // Write 01-attempt.json
            var grayField = grayScreenshot is not null ? $"\"{grayScreenshot}\"" : "null";
            var rawField = rawScreenshot is not null ? $"\"{rawScreenshot}\"" : "null";
            var attemptPath = Path.Combine(Dir, "01-attempt.json");
            File.WriteAllText(attemptPath, $$"""
                { "schemaVersion": 1,
                  "area": "{{area}}",
                  "attemptStartedUtc": "2026-06-02T01:00:00Z",
                  "attemptFinalizedUtc": "2026-06-02T01:00:01Z",
                  "outcome": "accepted",
                  "rejectReason": null,
                  "engineVersion": "1.0.0",
                  "files": {
                    "rawScreenshot": {{rawField}},
                    "grayScreenshot": {{grayField}},
                    "mapRect": "04-maprect.json",
                    "baseTextureResampled": null,
                    "alignedScreenshot": null,
                    "deviation": {{(writeDeviationPng ? "\"07-deviation.png\"" : "null")}},
                    "detectionsImage": null,
                    "projectionOverlay": null,
                    "detections": null,
                    "recoveredCalibration": null
                  } }
                """);

            Bundle = BundleLoader.Open(Dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        /// <summary>
        /// Writes a minimal valid PNG of the given dimensions using System.Drawing.
        /// </summary>
        private static void WritePng(string path, int width, int height)
        {
            using var bmp = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
