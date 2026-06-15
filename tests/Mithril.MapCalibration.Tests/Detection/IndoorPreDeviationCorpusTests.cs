using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1172 broader-corpus measurement — runs the Indoor profile with
/// <c>MinLumaForDeviation = 0</c> (pre-#1172 baseline) and <c>= 200</c> (the
/// shipping Phase 2.6 value) against every Indoor bundle present under
/// <c>%LOCALAPPDATA%/Mithril/diagnostics/calibration/</c>, and reports per-
/// bundle: total blobs, Icon-class blobs, mean NCC. Shows the gate's effect
/// across the corpus beyond the two bundles the threshold-sweep targeted
/// directly.
///
/// <para>Skips when no bundles are present. Output via
/// <see cref="ITestOutputHelper"/> is the durable measurement record.</para>
/// </summary>
public sealed class IndoorPreDeviationCorpusTests
{
    private readonly ITestOutputHelper _output;
    public IndoorPreDeviationCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Measure_pre_deviation_luma_gate_across_indoor_corpus()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            _output.WriteLine("SKIPPED — no LocalApplicationData path.");
            return;
        }
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

        _output.WriteLine($"Indoor corpus: {bundles.Length} bundles.");
        _output.WriteLine($"Indoor profile (BlobOptions only, no peak-luma post-filter): MaxAspect={SceneCalibrationProfile.Indoor.BlobOptions.MaxAspect}, MinSolidity={SceneCalibrationProfile.Indoor.BlobOptions.MinSolidity}, MinPeak={SceneCalibrationProfile.Indoor.BlobOptions.MinPeak}");
        _output.WriteLine("");
        _output.WriteLine("Per-bundle table:");
        _output.WriteLine($"{"Bundle",-70} {"ncc_0",8} {"ico_0",6} {"ric_0",7} {"ncc_200",10} {"ico_200",8} {"ric_200",9} ric_lift");
        _output.WriteLine("  (RIC = blobs with PeakLuma > 0.78 → real-icon-luma proxy, post-Phase-3 survivors)");

        // Indoor blob options without peak-luma post-filter so we see all
        // upstream Icon-class blobs (the gate's effect is on the deviation map,
        // not the post-classifier filter).
        var opts = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null };

        int totalBundlesWithData = 0;
        int corpusIconLift = 0;
        int corpusIcon0Sum = 0, corpusIcon200Sum = 0;

        foreach (var bundle in bundles)
        {
            var bundleName = Path.GetFileName(bundle);
            var shotPath = Path.Combine(bundle, "06-aligned-screenshot.png");
            var texPath = Path.Combine(bundle, "05-base-texture-resampled.png");
            var maskPath = Path.Combine(bundle, "07a-deviation-mask.png");

            if (!File.Exists(shotPath) || !File.Exists(texPath))
            {
                _output.WriteLine($"{bundleName,-90} (skipped — missing 06/05 PNG)");
                continue;
            }

            var shot = WicImageLoader.LoadGray(shotPath);
            var tex = WicImageLoader.LoadGray(texPath);
            if (shot.Width != tex.Width || shot.Height != tex.Height)
            {
                _output.WriteLine($"{bundleName,-90} (skipped — dim mismatch)");
                continue;
            }

            bool[]? deviationMask = null;
            if (File.Exists(maskPath))
            {
                var maskImg = WicImageLoader.LoadGray(maskPath);
                if (maskImg.Width == shot.Width && maskImg.Height == shot.Height)
                {
                    deviationMask = new bool[maskImg.Pixels.Length];
                    for (int i = 0; i < maskImg.Pixels.Length; i++) deviationMask[i] = maskImg.Pixels[i] >= 128;
                }
            }

            var (rawBgra, bgraW, bgraH) = WicImageLoader.LoadBgra(shotPath);
            bool bgraOk = bgraW == shot.Width && bgraH == shot.Height;

            (int totalBlobs, int iconBlobs, int realIconBlobs, double meanNcc) RunAt(byte threshold)
            {
                // Fresh float buffers per call — DeviationMap mutates `a` in place
                // when threshold > 0.
                var shotF = LocalNccDeviation.ToGrayFloat(shot);
                var texF = LocalNccDeviation.ToGrayFloat(tex);
                var dev = LocalNccDeviation.DeviationMap(
                    shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc,
                    addedOnly: true,
                    minLumaForDeviation: threshold);

                var classified = new List<BlobClassification>();
                var hooks = new DetectionDiagnosticHooks(
                    OnDeviation: null, OnRimMask: null, OnMorph: null,
                    OnBlobClassified: classified.Add);

                // Icons = the BlobFeat list returned by DetectIconBlobs (the
                // Icon-class subset). Use this directly for peak-luma probing.
                var icons = DeviationBlobDetector.DetectIconBlobs(
                    dev, shot.Width, shot.Height,
                    lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
                    closeRadius: 1,
                    hooks: hooks,
                    meanNcc: meanNcc,
                    logger: NullLogger.Instance,
                    deviationMask: deviationMask);

                int realIcon = 0;
                if (bgraOk)
                {
                    // "Real-icon" proxy = PeakLuma > 0.78 per the Phase 3
                    // §E finding (real-icon blobs sit at PeakLuma 0.91; floor
                    // noise at 0.22-0.40). Counts surviving Icon-class blobs
                    // that are at real-pip luma.
                    foreach (var icon in icons)
                    {
                        double pl = Mithril.MapCalibration.Detection.Internal.PeakLumaFilter
                            .PeakLuma(icon, rawBgra, shot.Width, shot.Height);
                        if (pl > 0.78) realIcon++;
                    }
                }
                return (classified.Count, icons.Count, realIcon, meanNcc);
            }

            var (total0, icon0, realIcon0, ncc0) = RunAt(0);
            var (total200, icon200, realIcon200, ncc200) = RunAt(200);
            int lift = icon200 - icon0;
            int realLift = realIcon200 - realIcon0;
            corpusIconLift += lift;
            corpusIcon0Sum += icon0;
            corpusIcon200Sum += icon200;
            totalBundlesWithData++;

            // Truncate bundle name to fit the table.
            var label = bundleName.Length > 70 ? bundleName.Substring(0, 69) + "…" : bundleName;
            _output.WriteLine($"{label,-70} {ncc0,8:F3} {icon0,6} {realIcon0,7} {ncc200,10:F3} {icon200,8} {realIcon200,9} ric_lift={(realLift >= 0 ? "+" : "")}{realLift}");
        }

        _output.WriteLine("");
        _output.WriteLine($"Corpus over {totalBundlesWithData} bundles:");
        _output.WriteLine($"  Sum Icon-class blobs at threshold 0:   {corpusIcon0Sum}");
        _output.WriteLine($"  Sum Icon-class blobs at threshold 200: {corpusIcon200Sum}");
        _output.WriteLine($"  Net Icon-class lift (sum):             {(corpusIconLift >= 0 ? "+" : "")}{corpusIconLift}");
        _output.WriteLine("");
        _output.WriteLine("Interpretation:");
        _output.WriteLine("  - lift POSITIVE means the gate ADMITTED MORE Icon-class blobs (likely splitting merged Structure blobs into individual Icons).");
        _output.WriteLine("  - lift NEGATIVE means the gate REJECTED Icon-class blobs (could indicate over-gating on a bundle where icon luma is lower than the threshold).");
        _output.WriteLine("  - lift ≈ 0 means no merge change on that bundle (either no merge existed, or the merge wasn't gate-resolvable).");
    }
}
