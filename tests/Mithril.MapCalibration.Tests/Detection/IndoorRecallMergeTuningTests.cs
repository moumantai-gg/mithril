using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1163 Phase 2 sub-step 2 measurement — varies the deviation window
/// (<c>LocalNccDeviation.DeviationMap.win</c>) and the morph close-radius
/// (<c>DeviationBlobDetector.DetectIconBlobs.closeRadius</c>) against the
/// canonical Hogan's 06-13 diagnostic bundle, then reports per-icon containing
/// blob features (area, solidity, aspect, classification) and the B+C merge
/// status (whether the two distinct in-game icons at aligned (411,185) and
/// (432,202) end up in the same connected component).
///
/// <para>Driven by the
/// <c>docs/planning/calibration-1155-scene-class-profile/measurements/indoor-recall-stage-attribution.md</c>
/// audit's T3 question: smaller deviation window OR no morph-close — does
/// either split blob 40 (production 1242-area Structure blob covering IconB +
/// IconC) into two separate components? The audit's T1/T2 (relaxed classifier
/// gates) don't address the merge; T3 does, but the right knob needs
/// measurement.</para>
///
/// <para>Skippable per ReplayFixture convention: gated on the canonical bundle
/// being present in <c>%LOCALAPPDATA%/Mithril/diagnostics/calibration/</c>.
/// Bundles are dev-local (PG art + 2-decimal zoom-slider give-rule out
/// contributor reproducibility) so this never runs in CI. The output written
/// via <see cref="ITestOutputHelper"/> is the durable measurement record —
/// committed measurement docs cite the values, not the test's pass/fail
/// status.</para>
/// </summary>
public sealed class IndoorRecallMergeTuningTests
{
    private readonly ITestOutputHelper _output;
    public IndoorRecallMergeTuningTests(ITestOutputHelper output) => _output = output;

    private const string CanonicalBundleName =
        "Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers";

    /// <summary>
    /// SHA-256 of the canonical bundle's <c>06-aligned-screenshot.png</c> at
    /// the time the production-parity asserts below were measured. Asserts only
    /// fire when this hash matches — protects other devs whose own bundles
    /// happen to live at the same path but produce different blob counts.
    /// mithril#1168 review feedback ("dev-local bundles are foot-guns").
    /// </summary>
    private const string CanonicalScreenshotSha256 =
        "57B01CE5D4BB2DF60124B32DAB2102B2E05BE9C37ECE5BE2DBEAA87B09F9EA0B";

    /// <summary>Per-icon aligned-space centroids from the stage-attribution audit.</summary>
    private static readonly (string Label, int X, int Y)[] CanonicalIcons =
    [
        ("A: upper-mid",       327, 180),
        ("B: upper-mid-right", 411, 185),
        ("C: upper-right",     432, 202),
        ("D: middle",          428, 257),
        ("E: lower-middle",    375, 667),
        ("F: lower-mid-right", 500, 680),
    ];

    private static string? CanonicalBundleDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        var dir = Path.Combine(local, "Mithril", "diagnostics", "calibration", CanonicalBundleName);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// True when the on-disk <c>06-aligned-screenshot.png</c> matches the
    /// canonical hash the production-parity asserts were measured against.
    /// </summary>
    private static bool BundleMatchesCanonicalHash(string bundleDir)
    {
        var shotPath = Path.Combine(bundleDir, "06-aligned-screenshot.png");
        if (!File.Exists(shotPath)) return false;
        using var stream = File.OpenRead(shotPath);
        var bytes = SHA256.HashData(stream);
        var hex = System.Convert.ToHexString(bytes);
        return string.Equals(hex, CanonicalScreenshotSha256, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (win, closeRadius) combinations measured. win=11/closeRadius=1 is production
    /// — included as a baseline so the table is self-documenting. The smaller-window
    /// variants probe T3 from the deviation-kernel side; closeRadius=0 probes T3
    /// from the morph side.
    /// </summary>
    public static IEnumerable<object?[]> Combinations()
    {
        if (CanonicalBundleDir() is null)
        {
            // Sentinel row — keeps Theory from failing with "No data found"; the
            // test recognises null params and prints SKIPPED.
            yield return new object?[] { null, null };
            yield break;
        }
        int[] wins = [11, 9, 7, 5];
        int[] closes = [0, 1];
        foreach (var w in wins)
            foreach (var c in closes)
                yield return new object?[] { (int?)w, (int?)c };
    }

    [Fact]
    public void Canonical_bundle_presence_is_reported()
    {
        var dir = CanonicalBundleDir();
        if (dir is null)
        {
            _output.WriteLine(
                $"SKIPPED — canonical bundle '{CanonicalBundleName}' not present under " +
                "%LOCALAPPDATA%/Mithril/diagnostics/calibration/. Run a calibration attempt " +
                "in-game against Hogan's Keep Basement to populate.");
            return;
        }
        _output.WriteLine($"Canonical bundle present at: {dir}");
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Measure_blob_pipeline(int? win, int? closeRadius)
    {
        if (win is null || closeRadius is null)
        {
            _output.WriteLine("SKIPPED — canonical bundle absent (see Canonical_bundle_presence_is_reported).");
            return;
        }

        var dir = CanonicalBundleDir()!;
        var shotPath = Path.Combine(dir, "06-aligned-screenshot.png");
        var texPath = Path.Combine(dir, "05-base-texture-resampled.png");
        var maskPath = Path.Combine(dir, "07a-deviation-mask.png");
        Assert.True(File.Exists(shotPath), $"missing {shotPath}");
        Assert.True(File.Exists(texPath), $"missing {texPath}");

        var shot = WicImageLoader.LoadGray(shotPath);
        var tex = WicImageLoader.LoadGray(texPath);
        Assert.True(
            shot.Width == tex.Width && shot.Height == tex.Height,
            $"shot {shot.Width}x{shot.Height} != tex {tex.Width}x{tex.Height}");

        // Load the production deviation mask from the bundle. It's alpha-derived
        // (FloorBoundaryMaskCache) + fog-OR'd (FogOfWarDetector), independent of
        // `win`, so the same mask is correct across all parameter combinations.
        // The PNG writes 255 = masked / 0 = passes; convert to bool[] with the
        // same `>= 128` threshold AutoCalibrationEngine uses.
        bool[]? deviationMask = null;
        int maskedCount = 0;
        if (File.Exists(maskPath))
        {
            var mask = WicImageLoader.LoadGray(maskPath);
            Assert.True(mask.Width == shot.Width && mask.Height == shot.Height,
                $"mask {mask.Width}x{mask.Height} != shot {shot.Width}x{shot.Height}");
            deviationMask = new bool[mask.Pixels.Length];
            for (int i = 0; i < mask.Pixels.Length; i++)
            {
                if (mask.Pixels[i] >= 128) { deviationMask[i] = true; maskedCount++; }
            }
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        var dev = LocalNccDeviation.DeviationMap(shotF, texF, shot.Width, shot.Height, win.Value, out var meanNcc, addedOnly: true);

        var blobs = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null,
            OnRimMask: null,
            OnMorph: null,
            OnBlobClassified: blobs.Add);

        // Production BlobOptions (AutoCalibrationEngine.cs:61-62).
        var opts = new BlobOptions(
            MinArea: 12, MaxIconArea: 900, MinSolidity: 0.35,
            MaxAspect: 2.5, MinPeak: 0.7);

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
            closeRadius: closeRadius.Value,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask);

        _output.WriteLine($"=== win={win} closeRadius={closeRadius} ===");
        _output.WriteLine($"meanNcc={meanNcc:F4}  total blobs={blobs.Count}  Icon-class blobs={blobs.Count(b => b.BlobClass == BlobClass.Icon)}  mask coverage={maskedCount}/{shot.Width * shot.Height} ({(deviationMask is null ? "no mask loaded" : (maskedCount * 100.0 / (shot.Width * shot.Height)).ToString("F1") + "%")})");

        // Per-icon containing-blob report.
        BlobClassification? ContainingBlob(int x, int y)
        {
            BlobClassification? best = null;
            foreach (var b in blobs)
            {
                if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                {
                    // Largest container wins — for the B+C merge case the bigger
                    // Structure-class blob is the relevant outcome.
                    if (best is null || b.Area > best.Area) best = b;
                }
            }
            return best;
        }

        var iconBlobs = new (string Label, int X, int Y, BlobClassification? Blob)[CanonicalIcons.Length];
        for (int i = 0; i < CanonicalIcons.Length; i++)
        {
            var (label, x, y) = CanonicalIcons[i];
            iconBlobs[i] = (label, x, y, ContainingBlob(x, y));
        }

        foreach (var (label, x, y, b) in iconBlobs)
        {
            if (b is null)
            {
                _output.WriteLine($"  Icon{label,-25} at ({x,4},{y,4})  NO blob contains");
            }
            else
            {
                _output.WriteLine(
                    $"  Icon{label,-25} at ({x,4},{y,4})  blob{b.BlobOrdinal,4} bbox({b.MinX},{b.MinY})+{b.W}x{b.H} " +
                    $"A={b.Area,5} sol={b.Solidity:F2} asp={b.Aspect:F2} peak={b.PeakDev:F2} -> {b.BlobClass}");
            }
        }

        // The headline measurement question: do IconB and IconC sit in the SAME
        // connected component? In production (win=11, closeRadius=1) the audit
        // showed yes — blob 40, area 1242, classified Structure. Phase 2 needs
        // a parameter choice that breaks this merge.
        var bBlob = iconBlobs.First(i => i.Label.StartsWith("B")).Blob;
        var cBlob = iconBlobs.First(i => i.Label.StartsWith("C")).Blob;
        bool bcMerged = bBlob is not null && cBlob is not null && bBlob.BlobOrdinal == cBlob.BlobOrdinal;
        _output.WriteLine($"  B+C merge status: {(bcMerged ? "MERGED (same blob)" : "SPLIT (different blobs)")}");

        int realIconAdmitted = iconBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);
        _output.WriteLine($"  Real icons reaching Icon class: {realIconAdmitted}/6");

        // Production-parity sanity guard at production parameters (win=11,
        // closeRadius=1). These numbers come straight from the canonical
        // bundle's 10c-blob-pipeline.json and 10-detections.json. Hash-gated
        // so a dev who populates the same bundle name from a different
        // capture session doesn't get red on what should be measurement-only
        // data (mithril#1168 review feedback).
        if (win == 11 && closeRadius == 1 && BundleMatchesCanonicalHash(dir))
        {
            Assert.Equal(197, blobs.Count);
            Assert.Equal(18, blobs.Count(b => b.BlobClass == BlobClass.Icon));
            var iconF = iconBlobs.First(i => i.Label.StartsWith("F")).Blob;
            Assert.NotNull(iconF);
            Assert.Equal(BlobClass.Icon, iconF.BlobClass);
            Assert.Equal(152, iconF.Area);
            Assert.Equal(176, iconF.BlobOrdinal);
        }
        else if (win == 11 && closeRadius == 1)
        {
            _output.WriteLine("Production-parity asserts skipped — the on-disk bundle's SHA256 doesn't match the measured canonical (numbers above are still useful as a measurement record).");
        }
    }

    /// <summary>
    /// mithril#1163 Phase 2 acceptance test — the canonical 06-13 bundle run
    /// with <see cref="SceneCalibrationProfile.Indoor"/> (T1 + T2: MaxAspect
    /// 2.7, MinSolidity 0.30) MUST admit IconD + IconE + IconF as Icon-class
    /// blobs (3 of 6 real icons reach Icon class). IconA isn't recovered (its
    /// blob bbox doesn't contain its centroid in production geometry — see
    /// indoor-recall-merge-fix-candidates.md "What 'RIC' counts"); IconB and
    /// IconC sit in the same Structure blob (the merge problem the
    /// measurement showed isn't reachable via classifier tuning). Phase 2 v1
    /// acceptance is 3/6; the implementation PR's spec revision must reflect
    /// this.
    ///
    /// <para>Compares against the production-parameter baseline (1/6 — only
    /// IconF reaches Icon class with Outdoor profile gates) so a future
    /// regression in either profile fails loudly.</para>
    /// </summary>
    [Fact]
    public void Indoor_profile_admits_3_of_6_real_icons_on_canonical_bundle()
    {
        if (CanonicalBundleDir() is null)
        {
            _output.WriteLine("SKIPPED — canonical bundle absent.");
            return;
        }
        var dir = CanonicalBundleDir()!;
        var shot = WicImageLoader.LoadGray(Path.Combine(dir, "06-aligned-screenshot.png"));
        var tex = WicImageLoader.LoadGray(Path.Combine(dir, "05-base-texture-resampled.png"));

        // Load production deviation mask (same as the Measure_blob_pipeline
        // theory does — see that test for the rationale).
        bool[]? deviationMask = null;
        var maskPath = Path.Combine(dir, "07a-deviation-mask.png");
        if (File.Exists(maskPath))
        {
            var maskImg = WicImageLoader.LoadGray(maskPath);
            deviationMask = new bool[maskImg.Pixels.Length];
            for (int i = 0; i < maskImg.Pixels.Length; i++) deviationMask[i] = maskImg.Pixels[i] >= 128;
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        var dev = LocalNccDeviation.DeviationMap(shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc, addedOnly: true);

        int RunWithProfile(SceneCalibrationProfile profile)
        {
            var blobs = new List<BlobClassification>();
            var hooks = new DetectionDiagnosticHooks(
                OnDeviation: null, OnRimMask: null, OnMorph: null,
                OnBlobClassified: blobs.Add);
            _ = DeviationBlobDetector.DetectIconBlobs(
                dev, shot.Width, shot.Height,
                lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, profile.BlobOptions,
                closeRadius: 1,
                hooks: hooks,
                meanNcc: meanNcc,
                logger: NullLogger.Instance,
                deviationMask: deviationMask);

            int admitted = 0;
            foreach (var (_, x, y) in CanonicalIcons)
            {
                BlobClassification? container = null;
                foreach (var b in blobs)
                {
                    if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                    {
                        if (container is null || b.Area > container.Area) container = b;
                    }
                }
                if (container?.BlobClass == BlobClass.Icon) admitted++;
            }
            return admitted;
        }

        int outdoorAdmitted = RunWithProfile(SceneCalibrationProfile.Outdoor);
        int indoorAdmitted = RunWithProfile(SceneCalibrationProfile.Indoor);
        _output.WriteLine($"Outdoor profile: {outdoorAdmitted}/6 real icons admitted (baseline = production parameters).");
        _output.WriteLine($"Indoor  profile: {indoorAdmitted}/6 real icons admitted (T1+T2 — MaxAspect 2.7, MinSolidity 0.30).");

        // Hash-gate the load-bearing asserts so devs running their own
        // bundles don't trip on what should be canonical-bundle-only numbers.
        if (BundleMatchesCanonicalHash(dir))
        {
            outdoorAdmitted.Should().Be(1, "production parameters (Outdoor profile) admit only IconF — the baseline pre-#1163 recall on canonical 06-13.");
            indoorAdmitted.Should().Be(3, "Indoor profile (T1+T2) should admit IconD + IconE + IconF — the +2 lift the Phase 2 measurement proved.");
        }
        else
        {
            // Soft assertion: Indoor must admit MORE than Outdoor on any
            // Indoor bundle (the structural claim the +2 lift makes). This
            // holds across all bundles where the audit's failure modes
            // reproduce; the exact admit-count is canonical-bundle-specific.
            indoorAdmitted.Should().BeGreaterThanOrEqualTo(outdoorAdmitted, "Indoor profile gates can only ADMIT — never reject — relative to Outdoor.");
            _output.WriteLine("Skipped exact 1/3 assertion — bundle SHA mismatch with the canonical measurement (general Indoor ≥ Outdoor invariant still asserted).");
        }
    }
}
