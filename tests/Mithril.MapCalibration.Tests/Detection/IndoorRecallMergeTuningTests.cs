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

    /// <summary>
    /// mithril#1155 Phase 2.5 sweep — (openRadius, closeRadius) combinations
    /// over the deviation kernel held at production <c>win=11</c>. Probes the
    /// audit's other T3 candidate ("morph-open BEFORE morph-close") that the
    /// <c>indoor-recall-merge-fix-candidates.md</c> measurement deferred to a
    /// follow-up. The headline question: at what <c>openRadius</c> do IconB
    /// (411, 185) and IconC (432, 202) end up in DIFFERENT connected
    /// components, both classified as <see cref="BlobClass.Icon"/> with
    /// <c>Area ≤ 900</c>?
    /// </summary>
    public static IEnumerable<object?[]> OpenCloseCombinations()
    {
        if (CanonicalBundleDir() is null)
        {
            yield return new object?[] { null, null };
            yield break;
        }
        int[] opens = [0, 1, 2, 3];
        int[] closes = [0, 1];
        foreach (var o in opens)
            foreach (var c in closes)
                yield return new object?[] { (int?)o, (int?)c };
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Measure_blob_pipeline(int? win, int? closeRadius)
    {
        // Review #1169-r2 finding #15: the previous `Canonical_bundle_presence_is_reported`
        // [Fact] had no Assert / Should, always passed regardless of state, and
        // showed up as a green test in CI metrics without providing verification.
        // Removed; the SKIPPED message below carries the same operator-facing
        // direction (where to populate the canonical bundle).
        if (win is null || closeRadius is null)
        {
            _output.WriteLine(
                $"SKIPPED — canonical bundle '{CanonicalBundleName}' not present under " +
                "%LOCALAPPDATA%/Mithril/diagnostics/calibration/. Run a calibration attempt " +
                "in-game against Hogan's Keep Basement to populate.");
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
    /// mithril#1172 Phase 2.6 sweep — extends the morph-open theory with a
    /// <c>minLumaForDeviation</c> dimension. Probes whether the pre-deviation
    /// luma gate splits the canonical IconB+IconC merge into two Icon-class
    /// components — the load-bearing question Phase 2.5 morph-open could not
    /// answer (Finding 5: the bridge is overlapping halos, not a thin
    /// filament). Acceptance: at one chosen threshold, B+C splits AND
    /// real-icon recall (IconD+E+F = 3/6 baseline) is preserved.
    /// </summary>
    public static IEnumerable<object?[]> LumaCloseCombinations()
    {
        if (CanonicalBundleDir() is null)
        {
            yield return new object?[] { null, null };
            yield break;
        }
        byte[] lumas = [0, 140, 160, 180, 200];
        int[] closes = [0, 1];
        foreach (var l in lumas)
            foreach (var c in closes)
                yield return new object?[] { (byte?)l, (int?)c };
    }

    /// <summary>
    /// mithril#1155 Phase 2.5 measurement — Sweeps <c>openRadius ∈ {0,1,2,3}</c>
    /// × <c>closeRadius ∈ {0,1}</c> at production <c>win=11</c> on the
    /// canonical bundle and reports per-icon containing-blob class + the B+C
    /// merge status. Driven by the
    /// <c>indoor-recall-merge-fix-candidates.md</c> Finding 1 ("merge survives
    /// every (win, closeRadius) tested") + Open Follow-up ("morph-open before
    /// close is the audit's other T3 candidate, file as Phase 2.5"). Result
    /// table commits to <c>indoor-recall-phase-2.5-morph-open.md</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(OpenCloseCombinations))]
    public void Measure_morph_open_pipeline(int? openRadius, int? closeRadius)
    {
        if (openRadius is null || closeRadius is null)
        {
            _output.WriteLine(
                $"SKIPPED — canonical bundle '{CanonicalBundleName}' not present under " +
                "%LOCALAPPDATA%/Mithril/diagnostics/calibration/.");
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

        bool[]? deviationMask = null;
        if (File.Exists(maskPath))
        {
            var mask = WicImageLoader.LoadGray(maskPath);
            deviationMask = new bool[mask.Pixels.Length];
            for (int i = 0; i < mask.Pixels.Length; i++) deviationMask[i] = mask.Pixels[i] >= 128;
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        var dev = LocalNccDeviation.DeviationMap(shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc, addedOnly: true);

        var blobs = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null, OnRimMask: null, OnMorph: null,
            OnBlobClassified: blobs.Add);

        // Indoor profile gates (T1+T2 relaxed) — Phase 2.5 sweeps the upstream
        // morph stages while keeping the classifier identical to the shipped
        // Indoor profile. That isolates the open/close effect from the gate
        // relaxation effect.
        var opts = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null };

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
            closeRadius: closeRadius.Value,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask,
            openRadius: openRadius.Value);

        _output.WriteLine($"=== openRadius={openRadius} closeRadius={closeRadius} (Indoor T1+T2 gates) ===");
        _output.WriteLine($"meanNcc={meanNcc:F4}  total blobs={blobs.Count}  Icon-class blobs={blobs.Count(b => b.BlobClass == BlobClass.Icon)}");

        BlobClassification? ContainingBlob(int x, int y)
        {
            BlobClassification? best = null;
            foreach (var b in blobs)
            {
                if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                {
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

        var bBlob = iconBlobs.First(i => i.Label.StartsWith("B")).Blob;
        var cBlob = iconBlobs.First(i => i.Label.StartsWith("C")).Blob;
        bool bcMerged = bBlob is not null && cBlob is not null && bBlob.BlobOrdinal == cBlob.BlobOrdinal;
        bool bIsIcon = bBlob?.BlobClass == BlobClass.Icon;
        bool cIsIcon = cBlob?.BlobClass == BlobClass.Icon;
        _output.WriteLine($"  B+C status: {(bcMerged ? "MERGED" : "SPLIT")} | B={(bIsIcon ? "Icon" : bBlob?.BlobClass.ToString() ?? "none")} | C={(cIsIcon ? "Icon" : cBlob?.BlobClass.ToString() ?? "none")}");

        int realIconAdmitted = iconBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);
        _output.WriteLine($"  Real icons reaching Icon class: {realIconAdmitted}/6");

        // Sanity guard at openRadius=0, closeRadius=1 — Indoor T1+T2 gates on
        // canonical 06-13 ⇒ 3/6 (IconD+E+F), same as
        // Indoor_profile_admits_3_of_6_real_icons_on_canonical_bundle.
        if (openRadius == 0 && closeRadius == 1 && BundleMatchesCanonicalHash(dir))
        {
            realIconAdmitted.Should().Be(3,
                "Phase 2.5 baseline (openRadius=0, closeRadius=1 + Indoor T1+T2) must reproduce the 3/6 Phase 2 baseline.");
        }
    }

    /// <summary>
    /// mithril#1172 Phase 2.6 measurement — Sweeps <c>minLumaForDeviation
    /// ∈ {0, 140, 160, 180, 200}</c> × <c>closeRadius ∈ {0, 1}</c> on the
    /// canonical bundle. Headline question: at which threshold does IconB
    /// (411, 185) and IconC (432, 202) sit in DIFFERENT connected components
    /// both classified <see cref="BlobClass.Icon"/>? Acceptance: at the
    /// chosen value B+C splits AND real-icon recall (IconD+E+F) is preserved
    /// at ≥ the Phase 3 baseline of 3/6.
    /// </summary>
    [Theory]
    [MemberData(nameof(LumaCloseCombinations))]
    public void Measure_pre_deviation_luma_pipeline(byte? minLumaForDeviation, int? closeRadius)
    {
        if (minLumaForDeviation is null || closeRadius is null)
        {
            _output.WriteLine(
                $"SKIPPED — canonical bundle '{CanonicalBundleName}' not present under " +
                "%LOCALAPPDATA%/Mithril/diagnostics/calibration/.");
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

        bool[]? deviationMask = null;
        if (File.Exists(maskPath))
        {
            var mask = WicImageLoader.LoadGray(maskPath);
            deviationMask = new bool[mask.Pixels.Length];
            for (int i = 0; i < mask.Pixels.Length; i++) deviationMask[i] = mask.Pixels[i] >= 128;
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        var dev = LocalNccDeviation.DeviationMap(
            shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc,
            addedOnly: true,
            minLumaForDeviation: minLumaForDeviation.Value);

        var blobs = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null, OnRimMask: null, OnMorph: null,
            OnBlobClassified: blobs.Add);

        // Indoor profile shape gates (T1+T2 relaxed) + peak-luma DISABLED so the
        // per-blob classification verdict is the upstream-merge-only outcome.
        // The post-classifier peak-luma filter would also gate the floor-noise
        // blobs — orthogonal to the merge question here.
        var opts = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null };

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
            closeRadius: closeRadius.Value,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask);

        _output.WriteLine($"=== minLumaForDeviation={minLumaForDeviation} closeRadius={closeRadius} (Indoor T1+T2 gates, openRadius=0) ===");
        _output.WriteLine($"meanNcc={meanNcc:F4}  total blobs={blobs.Count}  Icon-class blobs={blobs.Count(b => b.BlobClass == BlobClass.Icon)}");

        BlobClassification? ContainingBlob(int x, int y)
        {
            BlobClassification? best = null;
            foreach (var b in blobs)
            {
                if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                {
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

        var bBlob = iconBlobs.First(i => i.Label.StartsWith("B")).Blob;
        var cBlob = iconBlobs.First(i => i.Label.StartsWith("C")).Blob;
        bool bcMerged = bBlob is not null && cBlob is not null && bBlob.BlobOrdinal == cBlob.BlobOrdinal;
        bool bIsIcon = bBlob?.BlobClass == BlobClass.Icon;
        bool cIsIcon = cBlob?.BlobClass == BlobClass.Icon;
        _output.WriteLine($"  B+C status: {(bcMerged ? "MERGED" : "SPLIT")} | B={(bIsIcon ? "Icon" : bBlob?.BlobClass.ToString() ?? "none")} | C={(cIsIcon ? "Icon" : cBlob?.BlobClass.ToString() ?? "none")}");

        int realIconAdmitted = iconBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);
        _output.WriteLine($"  Real icons reaching Icon class: {realIconAdmitted}/6");

        // Sanity guard at minLumaForDeviation=0, closeRadius=1 — pre-#1172 path,
        // must reproduce the 3/6 Phase 2 baseline byte-identically.
        if (minLumaForDeviation == 0 && closeRadius == 1 && BundleMatchesCanonicalHash(dir))
        {
            realIconAdmitted.Should().Be(3,
                "Phase 2.6 baseline (minLumaForDeviation=0, closeRadius=1 + Indoor T1+T2) must reproduce the 3/6 Phase 2 baseline byte-identically (the gate is a no-op at 0).");
        }

        // Phase 2.6 load-bearing row pin at (minLumaForDeviation=200,
        // closeRadius=1) — the production-shipping value picked by the
        // sweep doc. Hash-gated to canonical 06-13 so dev-local captures
        // don't trip on different NPC positions. Review feedback: the
        // sweep table's headline ('200 | 1 (prod) | YES ✓ | 5') was
        // documentary-only without this pin.
        if (minLumaForDeviation == 200 && closeRadius == 1 && BundleMatchesCanonicalHash(dir))
        {
            realIconAdmitted.Should().Be(5,
                "Phase 2.6 production row (minLumaForDeviation=200, closeRadius=1 + Indoor T1+T2) must lift RIC from 3/6 to 5/6 on canonical 06-13 — the load-bearing claim from indoor-pre-deviation-luma-threshold.md.");
            // B and C MUST sit in different connected components (the merge
            // split — the entire point of #1172). bBlob.Area + cBlob.Area
            // values are pinned by the same measurement table.
            bBlob.Should().NotBeNull();
            cBlob.Should().NotBeNull();
            bBlob!.BlobOrdinal.Should().NotBe(cBlob!.BlobOrdinal,
                "IconB and IconC MUST be in different connected components at (200, 1) — splitting the merge is the load-bearing #1172 fix.");
            bBlob.BlobClass.Should().Be(BlobClass.Icon);
            cBlob.BlobClass.Should().Be(BlobClass.Icon);
        }
    }

    /// <summary>
    /// mithril#1172 Phase 2.6 acceptance test — the canonical 06-13 bundle
    /// run with <see cref="SceneCalibrationProfile.Indoor"/> applied
    /// END-TO-END (BlobOptions AND <see cref="SceneCalibrationProfile.MinLumaForDeviation"/>
    /// in <see cref="LocalNccDeviation.DeviationMap"/>) MUST split the
    /// previously-merged IconB+IconC into TWO Icon-class blobs at the
    /// two distinct centroid positions AND lift Real-Icon-Class recall
    /// from the Phase 3 baseline of 3/6 to 5/6 (IconB and IconC newly
    /// reach Icon-class individually).
    ///
    /// <para>This is the load-bearing acceptance gate for #1172 — the
    /// existing Phase 3 test
    /// <see cref="Indoor_profile_admits_3_of_6_real_icons_on_canonical_bundle"/>
    /// asserts 3/6 because it calls <see cref="LocalNccDeviation.DeviationMap"/>
    /// without forwarding the profile's MinLumaForDeviation (the pre-#1172
    /// path). This test threads the full profile to verify the Phase 2.6
    /// merge fix produces the expected behaviour.</para>
    /// </summary>
    [Fact]
    public void Indoor_profile_with_pre_deviation_luma_gate_splits_merged_NPC_pair()
    {
        if (CanonicalBundleDir() is null)
        {
            _output.WriteLine("SKIPPED — canonical bundle absent.");
            return;
        }
        var dir = CanonicalBundleDir()!;
        var shotPath = Path.Combine(dir, "06-aligned-screenshot.png");
        var texPath = Path.Combine(dir, "05-base-texture-resampled.png");
        var maskPath = Path.Combine(dir, "07a-deviation-mask.png");

        var shot = WicImageLoader.LoadGray(shotPath);
        var tex = WicImageLoader.LoadGray(texPath);

        bool[]? deviationMask = null;
        if (File.Exists(maskPath))
        {
            var maskImg = WicImageLoader.LoadGray(maskPath);
            deviationMask = new bool[maskImg.Pixels.Length];
            for (int i = 0; i < maskImg.Pixels.Length; i++) deviationMask[i] = maskImg.Pixels[i] >= 128;
        }

        var profile = SceneCalibrationProfile.Indoor;
        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);

        // Phase 2.6: apply the profile's MinLumaForDeviation END-TO-END.
        var dev = LocalNccDeviation.DeviationMap(
            shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc,
            addedOnly: true,
            minLumaForDeviation: profile.MinLumaForDeviation);

        var blobs = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null, OnRimMask: null, OnMorph: null,
            OnBlobClassified: blobs.Add);

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood,
            profile.BlobOptions with { MinPeakLuma = null },
            closeRadius: 1,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask);

        BlobClassification? ContainingBlob(int x, int y)
        {
            BlobClassification? best = null;
            foreach (var b in blobs)
            {
                if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                {
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

        var bBlob = iconBlobs.First(i => i.Label.StartsWith("B")).Blob;
        var cBlob = iconBlobs.First(i => i.Label.StartsWith("C")).Blob;
        int admitted = iconBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);

        _output.WriteLine($"Indoor end-to-end (MinLumaForDeviation={profile.MinLumaForDeviation}): RIC={admitted}/6");
        _output.WriteLine($"IconB blob: ord={bBlob?.BlobOrdinal} class={bBlob?.BlobClass} area={bBlob?.Area}");
        _output.WriteLine($"IconC blob: ord={cBlob?.BlobOrdinal} class={cBlob?.BlobClass} area={cBlob?.Area}");

        // Hash-gated specifics: ALL Phase 2.6 claims (split into separate
        // Icon-class blobs at the canonical centroids, RIC≥5) are bundle-
        // specific to the canonical 06-13 capture. CanonicalIcons centroids
        // (411,185) and (432,202) are aligned-frame coords that depend on
        // the canonical in-game zoom + player position. Review feedback
        // flagged that asserting these on a dev's own bundle (which lives
        // at the same path but with different NPC positions) would fail
        // misleadingly. Mirror the pattern from
        // Indoor_profile_admits_3_of_6_real_icons_on_canonical_bundle.
        if (BundleMatchesCanonicalHash(dir))
        {
            bBlob.Should().NotBeNull("Phase 2.6 merge fix means IconB now has a containing Icon-class blob on the canonical 06-13 bundle.");
            cBlob.Should().NotBeNull("Phase 2.6 merge fix means IconC now has a containing Icon-class blob on the canonical 06-13 bundle.");
            bBlob!.BlobOrdinal.Should().NotBe(cBlob!.BlobOrdinal,
                "Phase 2.6 #1172: IconB and IconC MUST sit in different connected components on the canonical 06-13 bundle.");
            bBlob.BlobClass.Should().Be(BlobClass.Icon, "IconB's containing blob must reach Icon-class.");
            cBlob.BlobClass.Should().Be(BlobClass.Icon, "IconC's containing blob must reach Icon-class.");
            admitted.Should().BeGreaterThanOrEqualTo(5,
                "Phase 2.6 #1172: Indoor profile with MinLumaForDeviation=200 lifts RIC from the Phase 3 baseline of 3/6 to 5/6 (IconB and IconC join IconD/E/F as Icon-class admits).");
        }
        else
        {
            // Soft invariant that holds across any Indoor bundle whose
            // capture predates the gate: the gate CAN admit (split a
            // merged blob) but never silently destroys recall — Indoor RIC
            // at the gate is ≥ Indoor RIC pre-gate. This is the structural
            // claim the broader-corpus measurement
            // (indoor-pre-deviation-luma-threshold.md + corpus tests)
            // documented as cross-bundle generalisation.
            admitted.Should().BeGreaterThanOrEqualTo(3,
                "On any Indoor bundle the Phase 2.6 gate cannot reduce admitted real icons below the Phase 3 baseline (3/6 minimum).");
            _output.WriteLine("Skipped canonical-only structural asserts — bundle SHA mismatch (3/6 RIC floor invariant still applies).");
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

    /// <summary>
    /// mithril#1155 Phase 3 acceptance test — the canonical 06-13 bundle run
    /// with <see cref="SceneCalibrationProfile.Indoor"/> + the raw-BGRA peak-
    /// luma pre-filter applied:
    ///
    /// <list type="number">
    ///   <item>The total Icon-class blob count drops sharply (20 Indoor T1+T2
    ///   admits → small post-filter, since 17 of the 18 base Outdoor admits +
    ///   the 0-of-2 newly-admitted-by-T1+T2 floor-noise blobs sit at PeakLuma
    ///   ≤ 0.55 per the broader corpus measurement and get rejected by the 0.7
    ///   threshold). Review #1169-r2 finding #14: this docstring previously
    ///   read "18 production" referring to the Outdoor production count; the
    ///   assertion below pins the Indoor T1+T2 count of 20.</item>
    ///   <item>The 3 real-icon blobs admitted by T1+T2 (IconD + IconE + IconF —
    ///   per <see cref="Indoor_profile_admits_3_of_6_real_icons_on_canonical_bundle"/>)
    ///   all SURVIVE the peak-luma filter. The
    ///   <c>indoor-recall-stage-attribution.md</c> §E finding established that
    ///   real-icon blobs sit at PeakLuma &gt; 0.78, so 0.7 leaves &gt; 0.08
    ///   headroom.</item>
    /// </list>
    ///
    /// <para>Together: Phase 2's recall lift is preserved AND Phase 3 suppresses
    /// the surviving noise — composing the two Indoor improvements without
    /// regression. This is the load-bearing acceptance gate for Phase 3.</para>
    /// </summary>
    [Fact]
    public void Indoor_profile_with_peak_luma_filter_drops_noise_blobs_and_keeps_real_icons()
    {
        if (CanonicalBundleDir() is null)
        {
            _output.WriteLine("SKIPPED — canonical bundle absent.");
            return;
        }
        var dir = CanonicalBundleDir()!;
        var shotPath = Path.Combine(dir, "06-aligned-screenshot.png");
        var texPath = Path.Combine(dir, "05-base-texture-resampled.png");

        var shot = WicImageLoader.LoadGray(shotPath);
        var tex = WicImageLoader.LoadGray(texPath);
        var (rawBgra, bgraW, bgraH) = WicImageLoader.LoadBgra(shotPath);
        Assert.True(bgraW == shot.Width && bgraH == shot.Height,
            $"BGRA dims {bgraW}x{bgraH} != gray dims {shot.Width}x{shot.Height} — the WIC decode should match.");

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

        // Run the Indoor profile twice: once with the peak-luma pre-filter
        // active (Indoor as shipped, including MinPeakLuma=0.7), once with it
        // disabled (Indoor minus the MinPeakLuma field) so we can attribute the
        // change to the new filter rather than to Phase 2's classifier gates.
        var indoorWithLuma = SceneCalibrationProfile.Indoor;
        var indoorWithoutLuma = SceneCalibrationProfile.Indoor with
        {
            BlobOptions = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null },
        };

        (IReadOnlyList<BlobFeat> Icons, int Total) Run(SceneCalibrationProfile profile, bool withBgra)
        {
            var blobs = new List<BlobClassification>();
            var hooks = new DetectionDiagnosticHooks(
                OnDeviation: null, OnRimMask: null, OnMorph: null,
                OnBlobClassified: blobs.Add);
            var icons = DeviationBlobDetector.DetectIconBlobs(
                dev, shot.Width, shot.Height,
                lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, profile.BlobOptions,
                closeRadius: 1,
                hooks: hooks,
                meanNcc: meanNcc,
                logger: NullLogger.Instance,
                deviationMask: deviationMask,
                rawBgra: withBgra ? rawBgra : null);
            return (icons, blobs.Count(b => b.BlobClass == BlobClass.Icon));
        }

        // Baseline — Indoor profile with peak-luma DISABLED. This is what Phase
        // 2 alone produces: the Icon-class blobs that survive T1+T2's relaxed
        // shape gates.
        //
        // Review #1169-r2 finding #11: the second tuple element is the count of
        // BlobClass.Icon entries seen by the OnBlobClassified hook, which fires
        // BEFORE the peak-luma filter — it is structurally the PRE-FILTER count
        // on BOTH branches (the hook runs the same regardless of whether the
        // filter then drops blobs). Naming it `…IconClassClassified` makes the
        // pre-filter semantics explicit so a future reader doesn't add a bogus
        // `secondBranch < firstBranch` assertion expecting a post-filter delta.
        var (preFilterIcons, preFilterIconClassClassified) = Run(indoorWithoutLuma, withBgra: false);
        _output.WriteLine($"Indoor (no peak-luma filter): {preFilterIconClassClassified} Icon-class classified, {preFilterIcons.Count} returned (no filter).");

        // With peak-luma — the post-#1155 Phase 3 path. The "classified" count
        // is unchanged by the filter (it's measured at the pre-filter hook); the
        // "returned" count IS the post-filter survivor set.
        var (postFilterIcons, postFilterIconClassClassified) = Run(indoorWithLuma, withBgra: true);
        _output.WriteLine($"Indoor (peak-luma 0.7):       {postFilterIconClassClassified} Icon-class classified (pre-filter), {postFilterIcons.Count} returned after filter.");

        // Real-icon admission counts on both branches — the headline.
        int CountRealIconsAdmitted(IReadOnlyList<BlobFeat> icons)
        {
            int admitted = 0;
            foreach (var (_, x, y) in CanonicalIcons)
            {
                bool contained = false;
                foreach (var blob in icons)
                {
                    if (x >= blob.MinX && x < blob.MinX + blob.W && y >= blob.MinY && y < blob.MinY + blob.H)
                    {
                        contained = true;
                        break;
                    }
                }
                if (contained) admitted++;
            }
            return admitted;
        }

        int preFilterReal = CountRealIconsAdmitted(preFilterIcons);
        int postFilterReal = CountRealIconsAdmitted(postFilterIcons);
        _output.WriteLine($"Real icons admitted: pre-filter {preFilterReal}/6, post-filter {postFilterReal}/6.");

        // The structural claim Phase 3 makes: the filter NEVER drops a real-icon
        // blob. Holds across all Indoor bundles where the §E spike finding
        // ("real-icon PeakLuma > 0.78, floor-noise PeakLuma ≤ 0.40")
        // generalises. This is the load-bearing invariant — assert it
        // unconditionally (no hash gate), so any Indoor bundle that violates it
        // surfaces as a Phase 3 regression.
        postFilterReal.Should().Be(preFilterReal,
            "Phase 3 peak-luma filter must never drop a real-icon blob — the §E separation (real-icon > 0.78 vs floor-noise < 0.40) leaves > 0.08 headroom above the 0.7 threshold across the audited Indoor corpus.");

        // Hash-gated specifics: the canonical 06-13 bundle drops from 18 → small
        // Icon-class count (specifically `postFilterIcons.Count` ≤ a handful)
        // AND the 3 real icons (IconD+E+F) survive. The exact post-filter count
        // depends on the residual scoring of low-luma blobs and is canonical-
        // bundle-specific, so we hash-gate.
        if (BundleMatchesCanonicalHash(dir))
        {
            preFilterIconClassClassified.Should().Be(20,
                "Phase 2 (Indoor T1+T2 — MaxAspect 2.7, MinSolidity 0.30) admits 20 Icon-class blobs on the canonical 06-13 bundle pre-filter (18 Outdoor-admitted + 2 lifted by T1+T2).");
            postFilterIcons.Count.Should().BeLessThan(preFilterIconClassClassified,
                "Phase 3 peak-luma filter must reject SOME blobs on the canonical bundle — otherwise the §E spike was wrong.");
            postFilterIcons.Count.Should().BeLessThanOrEqualTo(5,
                "Phase 3 leaves only the real-icon-luma blobs; the spike measured 1 in canonical (blob 176), and T1+T2 lifts 2 more icon-luma blobs to admission. ≤5 leaves headroom for the implementation's exact tally without pinning a fragile count.");
            postFilterReal.Should().Be(3,
                "IconD + IconE + IconF (the 3 Phase 2 admits) must ALL survive the peak-luma filter — the audit §E established they sit at PeakLuma > 0.78.");
        }
        else
        {
            _output.WriteLine("Skipped exact canonical-only asserts — bundle SHA mismatch (structural never-drops-a-real-icon invariant above still applies).");
        }
    }

    /// <summary>
    /// mithril#1155 Phase 3 broader-corpus PeakLuma measurement. Iterates every
    /// Indoor bundle present under <c>%LOCALAPPDATA%/Mithril/diagnostics/calibration/</c>
    /// (Hogan's + GoblinDungeon), runs the Indoor profile WITHOUT the peak-luma
    /// filter so all Icon-class blobs are reported, and prints the per-blob
    /// PeakLuma in BGRA-loaded <c>06-aligned-screenshot.png</c>. Emits a summary
    /// per bundle: count below 0.40, between 0.40–0.78, and above 0.78. The
    /// Phase 3 spec hypothesis is that the &gt; 0.78 group is exhaustively
    /// real-icon blobs and the &lt; 0.40 group is exhaustively floor noise; this
    /// test makes that distribution visible across the corpus so
    /// <c>indoor-peak-luma-threshold.md</c> can cite real numbers.
    ///
    /// <para>Skippable per the dev-local-bundles convention. Output via
    /// <see cref="ITestOutputHelper"/> is the durable measurement record.</para>
    ///
    /// <para><b>BGRA caveat.</b> <c>06-aligned-screenshot.png</c> is saved as
    /// Gray8 (see <c>FilesystemCalibrationAttemptBundleSink</c>), so the
    /// BGRA load produces R=G=B=gray. BT.601 weights sum to 1.0, so the peak
    /// over a gray-saved PNG is just the gray value normalised. This is a
    /// PROXY for the production raw BGRA from <c>captureResult.Color.Bgra</c>
    /// (true multi-channel), but for PG's Indoor icons (grayscale glyphs on
    /// grayscale floor per the §6.c spike), the proxy is equivalent — the
    /// chroma-zero finding rules out the multi-channel case adding signal.</para>
    /// </summary>
    [Fact]
    public void Measure_peak_luma_distribution_across_indoor_corpus()
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
            _output.WriteLine($"SKIPPED — {calRoot} does not exist.");
            return;
        }

        // Indoor bundles only — Hogan's + GoblinDungeon prefixes. Outdoor
        // bundles (Serbule, Eltibule, Kur) skip because peak-luma never fires
        // on the Outdoor profile (MinPeakLuma=null).
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

        _output.WriteLine($"Measuring peak-luma distribution across {bundles.Length} Indoor bundle(s).");
        _output.WriteLine($"Indoor profile: MaxAspect={SceneCalibrationProfile.Indoor.BlobOptions.MaxAspect}, MinSolidity={SceneCalibrationProfile.Indoor.BlobOptions.MinSolidity}, MinPeak={SceneCalibrationProfile.Indoor.BlobOptions.MinPeak} (peak-luma filter disabled for measurement).");
        _output.WriteLine("");

        int corpusTotalBlobs = 0;
        int corpusBelow040 = 0;
        int corpusBetween040And078 = 0;
        int corpusAbove078 = 0;
        int corpusAbove070 = 0;
        int bundlesWithData = 0;

        // Run Indoor profile with peak-luma DISABLED so we see the raw
        // distribution of all Icon-class blob luma values.
        var measurementProfile = SceneCalibrationProfile.Indoor with
        {
            BlobOptions = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null },
        };

        foreach (var bundle in bundles)
        {
            var bundleName = Path.GetFileName(bundle);
            var shotPath = Path.Combine(bundle, "06-aligned-screenshot.png");
            var texPath = Path.Combine(bundle, "05-base-texture-resampled.png");
            var maskPath = Path.Combine(bundle, "07a-deviation-mask.png");

            if (!File.Exists(shotPath) || !File.Exists(texPath))
            {
                _output.WriteLine($"  {bundleName} — SKIPPED (missing 06/05 PNG).");
                continue;
            }

            var shot = WicImageLoader.LoadGray(shotPath);
            var tex = WicImageLoader.LoadGray(texPath);
            if (shot.Width != tex.Width || shot.Height != tex.Height)
            {
                _output.WriteLine($"  {bundleName} — SKIPPED (dim mismatch: shot {shot.Width}x{shot.Height} != tex {tex.Width}x{tex.Height}).");
                continue;
            }
            // Review #1169-r2 finding #8: validate that LoadBgra produces dims
            // matching LoadGray for the same file. WIC's FormatConvertedBitmap
            // could in principle return a different effective size (EXIF
            // orientation, color-profile transforms); without this guard a
            // mismatch silently corrupts the measurement (PeakLumaFilter would
            // return 0.0 for every blob and the corpus table would report a
            // false 100%-noise distribution).
            var (rawBgra, bgraW, bgraH) = WicImageLoader.LoadBgra(shotPath);
            if (bgraW != shot.Width || bgraH != shot.Height)
            {
                _output.WriteLine($"  {bundleName} — SKIPPED (BGRA dim mismatch: {bgraW}x{bgraH} != gray {shot.Width}x{shot.Height}).");
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

            var shotF = LocalNccDeviation.ToGrayFloat(shot);
            var texF = LocalNccDeviation.ToGrayFloat(tex);
            var dev = LocalNccDeviation.DeviationMap(shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc, addedOnly: true);

            var icons = DeviationBlobDetector.DetectIconBlobs(
                dev, shot.Width, shot.Height,
                lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, measurementProfile.BlobOptions,
                closeRadius: 1,
                hooks: null,
                meanNcc: meanNcc,
                logger: NullLogger.Instance,
                deviationMask: deviationMask,
                rawBgra: null);  // filter inactive — just classify so we see all luma values.

            if (icons.Count == 0)
            {
                _output.WriteLine($"  {bundleName} — 0 Icon-class blobs.");
                continue;
            }

            bundlesWithData++;
            int below040 = 0, between = 0, above078 = 0, above070 = 0;
            var lumaValues = new List<double>(icons.Count);
            foreach (var blob in icons)
            {
                double pl = Mithril.MapCalibration.Detection.Internal.PeakLumaFilter
                    .PeakLuma(blob, rawBgra, shot.Width, shot.Height);
                lumaValues.Add(pl);
                if (pl < 0.40) below040++;
                else if (pl < 0.78) between++;
                else above078++;
                if (pl >= 0.70) above070++;
            }
            lumaValues.Sort();
            double median = lumaValues[lumaValues.Count / 2];
            double min = lumaValues[0];
            double max = lumaValues[^1];

            _output.WriteLine(
                $"  {bundleName,-78}  total={icons.Count,3}  <0.40:{below040,3}  [0.40,0.78):{between,3}  ≥0.78:{above078,3}  ≥0.70 (filter threshold):{above070,3}  range[{min:F2},{max:F2}]  median={median:F2}");

            corpusTotalBlobs += icons.Count;
            corpusBelow040 += below040;
            corpusBetween040And078 += between;
            corpusAbove078 += above078;
            corpusAbove070 += above070;
        }

        _output.WriteLine("");
        _output.WriteLine($"Corpus aggregate over {bundlesWithData} bundle(s):");
        _output.WriteLine($"  Total Icon-class blobs:        {corpusTotalBlobs}");
        _output.WriteLine($"  PeakLuma < 0.40 (floor noise): {corpusBelow040}");
        _output.WriteLine($"  PeakLuma in [0.40, 0.78):      {corpusBetween040And078}  (the spec's 'no-mans land' — should be near zero if §E generalises)");
        _output.WriteLine($"  PeakLuma >= 0.78 (real icons): {corpusAbove078}");
        _output.WriteLine($"  PeakLuma >= 0.70 (filter passes): {corpusAbove070}");
        _output.WriteLine("");
        _output.WriteLine("Hypothesis check (§E): blobs sit either at ≥0.78 (real icon) or ≤0.40 (floor noise);");
        _output.WriteLine("the [0.40, 0.78) gap should be small. The 0.7 filter threshold sits in the gap.");
    }
}
