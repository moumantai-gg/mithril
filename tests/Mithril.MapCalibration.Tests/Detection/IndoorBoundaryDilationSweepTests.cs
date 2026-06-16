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
/// <c>07a-deviation-mask.png</c> at the production dilation of 8. For a sweep
/// value <c>r ≤ 8</c>, the dilation=r mask is recovered by eroding the saved
/// mask by <c>(8 - r)</c> pixels using a square structuring element. The
/// identity <c>erode(dilate(B, 8), 8-r) = dilate(B, r)</c> holds when B is a
/// thin (1-px) boundary curve — which it is, by construction in
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

    /// <summary>Sweep values. 8 is the historical default; 2-6 are the candidates.</summary>
    private static readonly int[] DilationSweep = [2, 3, 4, 5, 6, 8];

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

        var (blobs, w, h, maskedCount, totalPx) = RunSweep(Live0615Name, dilationPx.Value);
        ReportTable("0615", dilationPx.Value, blobs, w, h, maskedCount, totalPx, Npcs0615, npcLayout: true);

        int iconCount = blobs.Count(b => b.BlobClass == BlobClass.Icon);
        int npcLowerDetected = NpcsInIconBlobs(blobs, [(475, 297)]);
        // mithril#1174 assertion at the production-picked value: the lower
        // pip MUST detect at SceneCalibrationProfile.Indoor.BoundaryDilationPx.
        // Other dilation values are diagnostic-only — they print to the output
        // for the sweep table but don't assert (regression guard is at the
        // production value).
        if (dilationPx.Value == (SceneCalibrationProfile.Indoor.BoundaryDilationPx ?? 8))
        {
            npcLowerDetected.Should().Be(1,
                $"at the production Indoor BoundaryDilationPx={dilationPx.Value}, the 06-15 NPCc lower pip at (475, 297) MUST be in an Icon-class blob — that's the #1174 lift contract.");
        }
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

        var (blobs, w, h, maskedCount, totalPx) = RunSweep(Canonical0613Name, dilationPx.Value);
        ReportTable("0613", dilationPx.Value, blobs, w, h, maskedCount, totalPx, Icons0613, npcLayout: false);

        int realIconCount = NpcsInIconBlobs(blobs, Icons0613.Select(i => (i.X, i.Y)).ToArray());
        // Regression guard: at the production-picked dilation, RIC must hold
        // at the post-#1172 baseline of 5/6 (IconA at (327, 180) is the
        // historical 1-of-6 miss that no prior pass has lifted).
        if (dilationPx.Value == (SceneCalibrationProfile.Indoor.BoundaryDilationPx ?? 8))
        {
            realIconCount.Should().BeGreaterOrEqualTo(5,
                $"at the production Indoor BoundaryDilationPx={dilationPx.Value}, the 06-13 canonical RIC must stay at the post-#1172 baseline of 5/6 (IconA at (327, 180) is the historical 1-of-6 miss).");
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

        // Load saved dilation=8 mask, simulate dilationPx by eroding (8 -
        // dilationPx) pixels. dilation=8 → no erosion (use mask as-is).
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

            int erodeRadius = 8 - dilationPx;
            if (erodeRadius > 0)
            {
                deviationMask = ErodeSquare(deviationMask, shot.Width, shot.Height, erodeRadius);
            }
            for (int i = 0; i < deviationMask.Length; i++) if (deviationMask[i]) maskedCount++;
        }

        var shotF = LocalNccDeviation.ToGrayFloat(shot);
        var texF = LocalNccDeviation.ToGrayFloat(tex);
        // Match the Indoor production pipeline: pre-deviation luma gate +
        // MinPeakLuma post-filter, Indoor BlobOptions shape gates.
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
        _output.WriteLine($"=== {bundleTag}: BoundaryDilationPx={dilationPx} (erodeBy={8 - dilationPx}) ===");
        _output.WriteLine($"  total blobs={blobs.Count}  Icon-class={iconCount}  mask coverage={maskedCount}/{totalPx} ({coveragePct:F1}%)");

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
                _output.WriteLine($"  {label,-26} at ({x,4},{y,4})  NO blob contains");
            }
            else
            {
                if (b.BlobClass == BlobClass.Icon) inIconClass++;
                _output.WriteLine(
                    $"  {label,-26} at ({x,4},{y,4})  blob#{b.BlobOrdinal,4} bbox({b.MinX},{b.MinY})+{b.W}x{b.H} " +
                    $"A={b.Area,5} sol={b.Solidity:F2} asp={b.Aspect:F2} peak={b.PeakDev:F2} -> {b.BlobClass}");
            }
        }

        if (npcLayout)
        {
            _output.WriteLine($"  NPCs (or NPC-positions) reaching Icon class: {inIconClass}/{targets.Length}");
        }
        else
        {
            _output.WriteLine($"  Real-Icon-Class (RIC): {inIconClass}/{targets.Length}");
        }
    }

    private static int NpcsInIconBlobs(List<BlobClassification> blobs, (int X, int Y)[] targets)
    {
        int count = 0;
        foreach (var (x, y) in targets)
        {
            foreach (var b in blobs)
            {
                if (b.BlobClass != BlobClass.Icon) continue;
                if (x >= b.MinX && x < b.MinX + b.W && y >= b.MinY && y < b.MinY + b.H)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Square-kernel binary erosion. A pixel survives iff every pixel within
    /// Chebyshev distance <paramref name="r"/> is set. Out-of-bounds neighbours
    /// count as "not set" (image-edge erosion). Mirrors the internal
    /// <see cref="Mithril.MapCalibration.Detection.Internal.FloorBoundaryMaskCache"/>
    /// dilation kernel so the simulated dilation-r mask matches the
    /// production-r mask byte-for-byte on alpha boundaries (the
    /// <c>erode(dilate(B,8), 8-r) = dilate(B, r)</c> identity for thin
    /// boundaries B).
    /// </summary>
    private static bool[] ErodeSquare(bool[] src, int w, int h, int r)
    {
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool all = true;
                for (int dy = -r; dy <= r && all; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h || !src[ny * w + nx])
                        {
                            all = false;
                            break;
                        }
                    }
                }
                dst[y * w + x] = all;
            }
        }
        return dst;
    }
}
