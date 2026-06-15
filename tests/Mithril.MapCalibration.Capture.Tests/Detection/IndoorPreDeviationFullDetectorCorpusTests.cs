using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Capture.Tests.Detection;

/// <summary>
/// mithril#1172 full-detector corpus measurement — runs the Indoor profile
/// through <see cref="DeviationBlobCalibrationDetector.Detect"/> end-to-end
/// (icon-template NCC included), and reports per-bundle the count of
/// <c>TypedDetection</c> records produced at <c>MinLumaForDeviation=0</c>
/// (pre-#1172 baseline) vs <c>=200</c> (the shipping Phase 2.6 value).
///
/// <para>This is the load-bearing downstream check the upstream corpus
/// measurement (<see cref="Mithril.MapCalibration.Tests.Detection.IndoorPreDeviationCorpusTests"/>)
/// couldn't run: Icon-class blob count is the classifier output; TypedDetection
/// count is what RANSAC actually consumes — the per-blob NCC against icon
/// templates (TypeFloor = 0.80) gates between the two.</para>
///
/// <para>Uses the on-disk icon-template cache the asset-extractor sidecar
/// populates (<c>%LOCALAPPDATA%/Mithril/assets/icon-templates.{json,bin}</c>).
/// Skips if absent. Skips entirely when no Indoor bundles are present.</para>
/// </summary>
public sealed class IndoorPreDeviationFullDetectorCorpusTests
{
    private readonly ITestOutputHelper _output;
    public IndoorPreDeviationFullDetectorCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Measure_typed_detection_count_across_indoor_corpus()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            _output.WriteLine("SKIPPED — no LocalApplicationData path.");
            return;
        }

        // Load icon templates from the on-disk sidecar cache.
        var assetsCacheDir = Path.Combine(local, "Mithril", "assets");
        var templates = BundledIconTemplateLoader.LoadFromDirectory(assetsCacheDir, NullLogger.Instance);
        if (templates.Templates.Count == 0)
        {
            _output.WriteLine($"SKIPPED — icon-templates cache empty at {assetsCacheDir}.");
            return;
        }
        _output.WriteLine($"Loaded {templates.Templates.Count} icon templates from {assetsCacheDir}.");

        var calRoot = Path.Combine(local, "Mithril", "diagnostics", "calibration");
        if (!Directory.Exists(calRoot))
        {
            _output.WriteLine($"SKIPPED — {calRoot} absent.");
            return;
        }

        var bundles = Directory.GetDirectories(calRoot)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.StartsWith("Map_HogansKeepBasement", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Map_GoblinDungeon", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (bundles.Length == 0)
        {
            _output.WriteLine($"SKIPPED — no Indoor bundles under {calRoot}.");
            return;
        }

        var profile = SceneCalibrationProfile.Indoor;
        // Use Indoor BlobOptions without the peak-luma post-filter so we see
        // the upstream Icon-class blob set the template-NCC consumes. The
        // post-filter is orthogonal to the question "do templates match?".
        var opts = profile.BlobOptions with { MinPeakLuma = null };
        var detector = new DeviationBlobCalibrationDetector();

        _output.WriteLine("");
        _output.WriteLine($"Indoor full-detector pipeline: BlobOptions={opts}, TypeFloor=0.80, RenderSizePx=16");
        _output.WriteLine($"{"Bundle",-70} {"icon_0",6} {"typed_0",8} {"icon_200",8} {"typed_200",9}  typed_lift");

        int corpusTypedAt0 = 0, corpusTypedAt200 = 0, corpusTypedLift = 0, bundlesWithData = 0;

        foreach (var bundle in bundles)
        {
            var bundleName = Path.GetFileName(bundle);
            var shotPath = Path.Combine(bundle, "06-aligned-screenshot.png");
            var texPath = Path.Combine(bundle, "05-base-texture-resampled.png");
            if (!File.Exists(shotPath) || !File.Exists(texPath))
            {
                _output.WriteLine($"{bundleName,-70} (skipped — missing 06/05 PNG)");
                continue;
            }

            var shot = PngFixtureLoader.LoadGray(shotPath);
            var tex = PngFixtureLoader.LoadGray(texPath);
            if (shot.Width != tex.Width || shot.Height != tex.Height)
            {
                _output.WriteLine($"{bundleName,-70} (skipped — dim mismatch)");
                continue;
            }

            // MapRect: aligned-frame rect spanning the whole crop, carrying
            // the same dims as the texture (the bundle's 05 is already
            // resampled to the aligned crop size).
            var mapRect = new MapRect(0, 0, shot.Width, shot.Height, tex.Width, tex.Height);

            (int icon, int typed) RunAt(byte threshold)
            {
                var request = new DetectionRequest(
                    Screenshot: shot,
                    BaseTexture: tex,
                    MapRect: mapRect,
                    Templates: templates,
                    RimMask: RimMaskMode.DeviationFlood,
                    LowNcc: 0.5,
                    TypeFloor: 0.80,
                    BlobOptions: opts)
                {
                    RenderSizePx = 16,
                    MinLumaForDeviation = threshold,
                };

                // Also count Icon-class blobs by running detect-iconblobs
                // alongside, since the dictionary returned by Detect doesn't
                // carry the upstream classifier count.
                var shotF = LocalNccDeviation.ToGrayFloat(shot);
                var texF = LocalNccDeviation.ToGrayFloat(tex);
                var dev = LocalNccDeviation.DeviationMap(
                    shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc,
                    addedOnly: true, minLumaForDeviation: threshold);
                var icons = DeviationBlobDetector.DetectIconBlobs(
                    dev, shot.Width, shot.Height,
                    lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
                    closeRadius: 1, meanNcc: meanNcc,
                    logger: NullLogger.Instance);

                // Now the full detector — DetectionRequest is consumed; it
                // re-runs DeviationMap inside (with the same threshold) and
                // produces the typed detections.
                var byType = detector.Detect(request);
                int typed = 0;
                foreach (var list in byType.Values) typed += list.Count;
                return (icons.Count, typed);
            }

            // F8: Read the threshold from the profile rather than hardcoding
            // the 200 literal. If a follow-up sweep moves the production
            // value (e.g. broader-corpus measurement picks 180 or 220), this
            // test tracks production automatically instead of reporting
            // measurement-vs-production drift.
            byte indoorThreshold = profile.MinLumaForDeviation;
            var (icon0, typed0) = RunAt(0);
            var (iconThresh, typedThresh) = RunAt(indoorThreshold);
            int lift = typedThresh - typed0;
            corpusTypedAt0 += typed0;
            corpusTypedAt200 += typedThresh;
            corpusTypedLift += lift;
            bundlesWithData++;

            var label = bundleName.Length > 68 ? bundleName.Substring(0, 67) + "…" : bundleName;
            _output.WriteLine($"{label,-70} {icon0,6} {typed0,8} {iconThresh,8} {typedThresh,9}  {(lift >= 0 ? "+" : "")}{lift}");
        }

        _output.WriteLine("");
        _output.WriteLine($"Corpus over {bundlesWithData} bundles:");
        _output.WriteLine($"  Sum TypedDetections at threshold 0:   {corpusTypedAt0}");
        _output.WriteLine($"  Sum TypedDetections at threshold {profile.MinLumaForDeviation}: {corpusTypedAt200}");
        _output.WriteLine($"  Net TypedDetection lift:              {(corpusTypedLift >= 0 ? "+" : "")}{corpusTypedLift}");
        _output.WriteLine("");
        _output.WriteLine("Interpretation:");
        _output.WriteLine("  typed > 0 = at least one Icon-class blob cleared TypeFloor=0.80 against some template.");
        _output.WriteLine("  typed >= 4 = RANSAC's 4-inlier minimum is REACHABLE (with luck on geometric consistency).");
        _output.WriteLine("  typed_lift POSITIVE = the gate surfaced MORE NCC-template-matching candidates.");

        // Structural soft-invariant: at least one bundle must produce data,
        // otherwise the test was a silent no-op. The 'no asserts' anti-
        // pattern review #1169-r2 finding #15 flagged hits exactly here
        // without this floor.
        bundlesWithData.Should().BeGreaterThan(0,
            "at least one Indoor bundle must produce data, otherwise the full-detector corpus test was a silent no-op (the test skipped at every bundle).");
    }
}
