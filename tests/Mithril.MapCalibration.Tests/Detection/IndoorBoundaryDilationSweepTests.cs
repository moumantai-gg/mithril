using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1174 Phase 2 — sweep <see cref="SceneCalibrationProfile.BoundaryDilationPx"/>
/// ∈ {2, 3, 4, 5, 6, 8} against the canonical 06-13 + 06-15 Hogan's bundles to
/// pick the load-bearing Indoor value. The brainstorm in
/// <c>indoor-recall-1174-npcc-brainstorm.md</c> Step 2 found NPCc's lower pip on
/// 06-15 is wiped by the production <c>BoundaryDilationPx = 8</c> alpha-corridor
/// band — empirical falsification of the issue body's local-NCC hypothesis. C3
/// (Indoor narrower dilation) is the proposed mechanism; this theory measures
/// whether it works and at what value.
///
/// <para><b>Sweep methodology.</b> The bundle saves
/// <c>07a-deviation-mask.png</c> at the production dilation of
/// <see cref="SavedMaskDilationPx"/>. For a sweep value
/// <c>r ≤ SavedMaskDilationPx</c>, the dilation=r mask is recovered by eroding
/// the saved mask by <c>(SavedMaskDilationPx - r)</c> pixels using a square
/// structuring element. The identity
/// <c>erode(dilate(B, k), k-r) = dilate(B, r)</c> holds when B is a thin (1-px)
/// boundary curve — which it is, by construction in
/// <see cref="Mithril.MapCalibration.Detection.Internal.FloorBoundaryMaskCache"/>.
/// </para>
///
/// <para><b>Confound — the OR with fog-of-war.</b> The saved mask is the OR of
/// (alpha-boundary dilated, fog-of-war). Eroding the OR shrinks the fog
/// contribution too, which isn't the production semantics. In the NPCc
/// neighbourhood (the 81×81 inspection in the brainstorm) the mask was a clean
/// alpha-corridor shape with no fog blobs, so the erosion approximation is
/// faithful for the NPCc lift question. For the canonical 06-13 RIC question
/// (coarser-grained), fog confounds are small relative to the icon's own
/// deviation signal. The theory documents this approximation in the per-row
/// output so future re-runs against fog-heavy bundles know the load-bearing
/// limit.</para>
///
/// <para><b>Skip semantics.</b> Both bundles dev-local per
/// <c>map_calibration_replay_fixtures_dev_local</c>. Either bundle absent →
/// the corresponding rows print SKIPPED. The chosen value is set on the
/// <see cref="SceneCalibrationProfile.Indoor"/> field; the theory's assertion
/// fires at that value to guard the production picked value going forward.</para>
/// </summary>
public sealed class IndoorBoundaryDilationSweepTests
{
    private readonly ITestOutputHelper _output;
    public IndoorBoundaryDilationSweepTests(ITestOutputHelper output) => _output = output;

    private const string Canonical0613Name =
        "Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers";
    private const string Live0615Name =
        "Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers";

    /// <summary>
    /// Dilation radius the production pipeline used when the saved
    /// <c>07a-deviation-mask.png</c> was generated. Capturing this as a named
    /// constant rather than the literal 8 makes the methodology load-bearing:
    /// if the canonical bundle is ever re-captured AFTER #1174 ships (when
    /// production Indoor dilation is 3), this constant must drop to 3 too OR
    /// the bundle must be re-captured under an Outdoor profile path that
    /// still uses 8. mithril#1183 review S1.
    /// </summary>
    private const int SavedMaskDilationPx = 8;

    /// <summary>06-13 canonical icon centroids — copied from <see cref="IndoorRecallMergeTuningTests"/>.</summary>
    private static readonly (string Label, int X, int Y)[] Icons0613 =
    [
        ("A: upper-mid",       327, 180),
        ("B: upper-mid-right", 411, 185),
        ("C: upper-right",     432, 202),
        ("D: middle",          428, 257),
        ("E: lower-middle",    375, 667),
        ("F: lower-mid-right", 500, 680),
    ];

    /// <summary>
    /// 06-15 NPC pip coordinates. The brainstorm's pixel-level inspection
    /// revealed NPCc at the issue body's "(473, 291)" is actually the dead-zone
    /// BETWEEN two vertically-stacked pips — upper pip at ~(473, 287),
    /// lower pip at ~(475, 297). The upper pip detects at production
    /// dilation=8 (Icon blob #91 in <c>10c-blob-pipeline.json</c>); the lower
    /// pip is what's wiped by the boundary band. NPCc-lower is the lift target.
    /// </summary>
    private static readonly (string Label, int X, int Y)[] Npcs0615 =
    [
        ("a: upper-mid",        455, 212),
        ("b: upper-right",      478, 230),
        ("c-upper: at (473,287)", 473, 287),  // already detected pre-#1174
        ("c-lower: at (475,297)", 475, 297),  // mithril#1174 lift target
    ];

    /// <summary>
    /// Sweep values: 8 is the historical default; 2-6 are the candidates;
    /// <see cref="SceneCalibrationProfile.Indoor.BoundaryDilationPx"/> is
    /// ALWAYS included (even if it moves outside the static range) so the
    /// production-value assertion below can never silently miss its row —
    /// mithril#1183 review C8.
    /// </summary>
    private static IEnumerable<int> DilationSweep
    {
        get
        {
            int[] candidates = [2, 3, 4, 5, 6, 8];
            int production = SceneCalibrationProfile.Indoor.BoundaryDilationPx ?? SavedMaskDilationPx;
            return production <= SavedMaskDilationPx
                ? candidates.Append(production).Distinct().OrderBy(r => r)
                : candidates.OrderBy(r => r);
        }
    }

    /// <summary>
    /// Pin the production-picked Indoor dilation here so the sweep's
    /// pass-the-production-value assertion has an absolute reference point.
    /// If this fails after a deliberate tuning, update both this constant
    /// AND the <see cref="SceneCalibrationProfile.Indoor"/> field together
    /// (and re-run the sweep against the canonical bundles to confirm the
    /// new pick still lifts NPCc and holds RIC).
    /// </summary>
    [Fact]
    public void Indoor_profile_pins_BoundaryDilationPx_at_3()
    {
        SceneCalibrationProfile.Indoor.BoundaryDilationPx.Should().Be(3,
            "mithril#1174 sweep determined 3 is the load-bearing Indoor value; a deliberate change requires re-running the sweep + updating this pin together.");
    }

    private static string? BundleDir(string name)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        var dir = Path.Combine(local, "Mithril", "diagnostics", "calibration", name);
        return Directory.Exists(dir) ? dir : null;
    }

    public static IEnumerable<object?[]> Sweep0613()
    {
        if (BundleDir(Canonical0613Name) is null)
        {
            yield return new object?[] { null };
            yield break;
        }
        foreach (var r in DilationSweep) yield return new object?[] { (int?)r };
    }

    public static IEnumerable<object?[]> Sweep0615()
    {
        if (BundleDir(Live0615Name) is null)
        {
            yield return new object?[] { null };
            yield break;
        }
        foreach (var r in DilationSweep) yield return new object?[] { (int?)r };
    }

    [Theory]
    [MemberData(nameof(Sweep0615))]
    public void Measure_boundary_dilation_sweep_on_0615_bundle(int? dilationPx)
    {
        if (dilationPx is null)
        {
            _output.WriteLine($"SKIPPED — bundle '{Live0615Name}' not present.");
            return;
        }

        // mithril#1183 review C11: guard against silently skipping erosion if a
        // future sweep value ever exceeds the saved-mask dilation.
        Assert.True(dilationPx.Value <= SavedMaskDilationPx,
            $"DilationSweep value {dilationPx.Value} exceeds SavedMaskDilationPx={SavedMaskDilationPx}; " +
            "either re-capture the bundle at the higher dilation OR re-derive the boundary mask from a real alpha provider.");

        var (blobs, w, h, maskedCount, totalPx) = RunSweep(Live0615Name, dilationPx.Value);
        ReportTable("0615", dilationPx.Value, blobs, w, h, maskedCount, totalPx, Npcs0615, npcLayout: true);

        // mithril#1183 review S6: foreground-pixel membership, NOT bbox
        // containment. The review caught a load-bearing FALSIFICATION here —
        // the brainstorm's "dilation=3 lifts NPCc-lower" claim turned out to
        // be a bbox-containment artifact: the upper-pip blob's tall bbox
        // (y∈[279, 303] at dilation=3) covers (475, 297) but the foreground
        // pixels of that blob do not. NPCc-lower is NOT detected at any
        // dilation in the sweep under foreground-pixel semantics. The
        // brainstorm + sweep docs are amended to reflect this; the #1174
        // load-bearing benefit IS the IconA recovery on 06-13 (asserted in
        // Measure_boundary_dilation_sweep_on_0613_bundle), not NPCc.
        //
        // This sweep theory now reports per-row foreground-pixel hits so a
        // future investigator (and any retry of the lift mechanism) can read
        // the table directly. No production-value assertion fires here for
        // 06-15 because the real lift target is the 06-13 sweep.
        int npcLowerDetected = NpcsInIconBlobs(blobs, [(475, 297)], w);
        _output.WriteLine(
            $"  [mithril#1174 status] NPCc-lower at (475, 297) " +
            $"is{(npcLowerDetected > 0 ? "" : " NOT")} in an Icon-class blob's foreground at dilation={dilationPx.Value}. " +
            $"(Brainstorm's bbox-based lift was a review-falsified artifact — see #1183 review S6.)");
    }

    [Theory]
    [MemberData(nameof(Sweep0613))]
    public void Measure_boundary_dilation_sweep_on_0613_bundle(int? dilationPx)
    {
        if (dilationPx is null)
        {
            _output.WriteLine($"SKIPPED — bundle '{Canonical0613Name}' not present.");
            return;
        }

        Assert.True(dilationPx.Value <= SavedMaskDilationPx,
            $"DilationSweep value {dilationPx.Value} exceeds SavedMaskDilationPx={SavedMaskDilationPx}.");

        var (blobs, w, h, maskedCount, totalPx) = RunSweep(Canonical0613Name, dilationPx.Value);
        ReportTable("0613", dilationPx.Value, blobs, w, h, maskedCount, totalPx, Icons0613, npcLayout: false);

        int realIconCount = NpcsInIconBlobs(
            blobs,
            Icons0613.Select(i => (i.X, i.Y)).ToArray(),
            w);

        if (dilationPx.Value == (SceneCalibrationProfile.Indoor.BoundaryDilationPx ?? SavedMaskDilationPx))
        {
            // mithril#1183 review S2: the headline #1174 finding is RIC 5/6 →
            // 6/6 at the production-picked dilation (IconA at (327, 180) was
            // a boundary-dilation casualty, not a previously-mysterious
            // recall gap). Assert ≥ 6 so a future regression to 5 — which
            // would silently undo the bonus IconA recovery — fails the test
            // loudly instead of passing the weaker ≥ 5 baseline.
            realIconCount.Should().Be(Icons0613.Length,
                $"at the production Indoor BoundaryDilationPx={dilationPx.Value}, the 06-13 canonical RIC must hold at 6/6 — the #1174 bonus finding (IconA recovery).");
        }
    }

    private static (List<BlobClassification> Blobs, int W, int H, int MaskedCount, int Total) RunSweep(
        string bundleName, int dilationPx)
    {
        var dir = BundleDir(bundleName)!;
        var shotPath = Path.Combine(dir, "06-aligned-screenshot.png");
        var texPath = Path.Combine(dir, "05-base-texture-resampled.png");
        var maskPath = Path.Combine(dir, "07a-deviation-mask.png");
        Assert.True(File.Exists(shotPath), $"missing {shotPath}");
        Assert.True(File.Exists(texPath), $"missing {texPath}");

        var shot = WicImageLoader.LoadGray(shotPath);
        var tex = WicImageLoader.LoadGray(texPath);
        var (rawBgra, bgraW, bgraH) = WicImageLoader.LoadBgra(shotPath);
        Assert.True(bgraW == shot.Width && bgraH == shot.Height,
            $"BGRA dims {bgraW}x{bgraH} != gray {shot.Width}x{shot.Height}");

        bool[]? deviationMask = null;
        int maskedCount = 0;
        if (File.Exists(maskPath))
        {
            var mask = WicImageLoader.LoadGray(maskPath);
            Assert.True(mask.Width == shot.Width && mask.Height == shot.Height);
            deviationMask = new bool[mask.Pixels.Length];
            for (int i = 0; i < mask.Pixels.Length; i++)
            {
                if (mask.Pixels[i] >= 128) deviationMask[i] = true;
            }

            // mithril#1183 review S1: pull the saved-mask dilation from a
            // named constant so future re-captures (or extension to a non-
            // production saved dilation) update one place.
            int erodeRadius = SavedMaskDilationPx - dilationPx;
            if (erodeRadius > 0)
            {
                // mithril#1183 review C21: share the production Morphology.Erode
                // (promoted from private to internal) instead of cloning the
                // pixel-walk loop. The identity erode(dilate(B, k), k-r) =
                // dilate(B, r) only holds if both sides use the same Chebyshev
                // square-kernel semantics — sharing the implementation makes
                // the identity load-bearing.
                deviationMask = Morphology.Erode(deviationMask, shot.Width, shot.Height, erodeRadius);
            }
            for (int i = 0; i < deviationMask.Length; i++) if (deviationMask[i]) maskedCount++;
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        byte minLuma = SceneCalibrationProfile.Indoor.MinLumaForDeviation;
        var dev = LocalNccDeviation.DeviationMap(
            shotF, texF, shot.Width, shot.Height, win: 11, out var meanNcc,
            addedOnly: true, minLumaForDeviation: minLuma);

        var blobs = new List<BlobClassification>();
        var hooks = new DetectionDiagnosticHooks(
            OnDeviation: null, OnRimMask: null, OnMorph: null,
            OnBlobClassified: blobs.Add);

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood,
            opts: SceneCalibrationProfile.Indoor.BlobOptions,
            closeRadius: 1,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask,
            rawBgra: rawBgra,
            openRadius: SceneCalibrationProfile.Indoor.MorphOpenRadiusPx);

        return (blobs, shot.Width, shot.Height, maskedCount, shot.Width * shot.Height);
    }

    private void ReportTable(
        string bundleTag, int dilationPx,
        List<BlobClassification> blobs, int w, int h,
        int maskedCount, int totalPx,
        (string Label, int X, int Y)[] targets,
        bool npcLayout)
    {
        int iconCount = blobs.Count(b => b.BlobClass == BlobClass.Icon);
        double coveragePct = totalPx == 0 ? 0.0 : 100.0 * maskedCount / totalPx;
        _output.WriteLine($"=== {bundleTag}: BoundaryDilationPx={dilationPx} (erodeBy={SavedMaskDilationPx - dilationPx}) ===");
        _output.WriteLine($"  total blobs={blobs.Count}  Icon-class={iconCount}  mask coverage={maskedCount}/{totalPx} ({coveragePct:F1}%)");

        // Report-side blob lookup — bbox containment is fine for the table
        // (it's human-readable triage), but the assertions use foreground-
        // pixel membership via NpcsInIconBlobs below (review S6).
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

        int inIconClass = 0;
        foreach (var (label, x, y) in targets)
        {
            var b = ContainingBlob(x, y);
            if (b is null)
            {
                _output.WriteLine($"  {label,-26} at ({x,4},{y,4})  NO blob bbox contains");
            }
            else
            {
                bool pipelineHit = b.BlobClass == BlobClass.Icon && b.Pixels.Contains(y * w + x);
                if (pipelineHit) inIconClass++;
                _output.WriteLine(
                    $"  {label,-26} at ({x,4},{y,4})  blob#{b.BlobOrdinal,4} bbox({b.MinX},{b.MinY})+{b.W}x{b.H} " +
                    $"A={b.Area,5} sol={b.Solidity:F2} asp={b.Aspect:F2} peak={b.PeakDev:F2} -> {b.BlobClass} " +
                    $"{(pipelineHit ? "[pixel-hit]" : "[bbox-only]")}");
            }
        }

        if (npcLayout)
        {
            _output.WriteLine($"  NPCs (or NPC-positions) reaching Icon class (foreground-pixel hit): {inIconClass}/{targets.Length}");
        }
        else
        {
            _output.WriteLine($"  Real-Icon-Class (RIC, foreground-pixel hit): {inIconClass}/{targets.Length}");
        }
    }

    /// <summary>
    /// Count of targets whose (x, y) coordinate falls in an Icon-class blob's
    /// FOREGROUND PIXEL SET — not bbox. mithril#1183 review S6: bbox
    /// containment is over-permissive (a noise pixel at the corner of an
    /// elongated blob bbox can make an unrelated target appear "detected"
    /// without the blob's foreground actually covering that location).
    /// Foreground membership is the load-bearing test for "this NPC pip's
    /// pixels are in an Icon-class blob" — the production-relevant property
    /// that RANSAC will consume as a real correspondence.
    /// </summary>
    private static int NpcsInIconBlobs(List<BlobClassification> blobs, (int X, int Y)[] targets, int w)
    {
        int count = 0;
        foreach (var (x, y) in targets)
        {
            int linearIndex = y * w + x;
            foreach (var b in blobs)
            {
                if (b.BlobClass != BlobClass.Icon) continue;
                if (b.Pixels.Contains(linearIndex))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }
}
