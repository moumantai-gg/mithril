using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Per-bundle generalisation check for the Phase 2.5 morph-open finding —
/// re-runs the openRadius × closeRadius sweep against the 06-15 live-verification
/// bundle (the bundle from PR #1170's Phase 3 live verification). The 06-13
/// canonical bundle's measurement said no combo splits B+C; this test checks
/// whether the conclusion generalises to a SECOND bundle whose merge is at
/// different aligned coordinates.
/// </summary>
public sealed class IndoorRecallMergeTuning0615Tests
{
    private readonly ITestOutputHelper _output;
    public IndoorRecallMergeTuning0615Tests(ITestOutputHelper output) => _output = output;

    private const string BundleName =
        "Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers";

    // The three NPC bbox positions Arthur named in the 06-15 screenshot,
    // converted from bbox (x, y, w, h) to centroid coordinates.
    private static readonly (string Label, int X, int Y)[] NpcCentroids =
    [
        ("a: upper-mid",    455, 212),
        ("b: upper-right",  478, 230),
        ("c: middle-right", 473, 291),
    ];

    private static string? BundleDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        var dir = Path.Combine(local, "Mithril", "diagnostics", "calibration", BundleName);
        return Directory.Exists(dir) ? dir : null;
    }

    public static IEnumerable<object?[]> OpenCloseCombinations()
    {
        if (BundleDir() is null)
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
    [MemberData(nameof(OpenCloseCombinations))]
    public void Measure_morph_open_pipeline_on_0615_bundle(int? openRadius, int? closeRadius)
    {
        if (openRadius is null || closeRadius is null)
        {
            _output.WriteLine($"SKIPPED — bundle '{BundleName}' not present.");
            return;
        }

        var dir = BundleDir()!;
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

        // Indoor profile shape gates (T1+T2 relaxed) + peak-luma DISABLED so
        // the per-blob class is reported purely on shape — the post-classifier
        // peak-luma filter is orthogonal to the merge question.
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

        _output.WriteLine($"=== 0615: openRadius={openRadius} closeRadius={closeRadius} (Indoor T1+T2 gates) ===");
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

        var npcBlobs = new (string Label, int X, int Y, BlobClassification? Blob)[NpcCentroids.Length];
        for (int i = 0; i < NpcCentroids.Length; i++)
        {
            var (label, x, y) = NpcCentroids[i];
            npcBlobs[i] = (label, x, y, ContainingBlob(x, y));
        }

        foreach (var (label, x, y, b) in npcBlobs)
        {
            if (b is null)
            {
                _output.WriteLine($"  NPC{label,-20} at ({x,4},{y,4})  NO blob contains");
            }
            else
            {
                _output.WriteLine(
                    $"  NPC{label,-20} at ({x,4},{y,4})  blob{b.BlobOrdinal,4} bbox({b.MinX},{b.MinY})+{b.W}x{b.H} " +
                    $"A={b.Area,5} sol={b.Solidity:F2} asp={b.Aspect:F2} peak={b.PeakDev:F2} -> {b.BlobClass}");
            }
        }

        var aBlob = npcBlobs[0].Blob;
        var bBlob = npcBlobs[1].Blob;
        var cBlob = npcBlobs[2].Blob;
        bool abMerged = aBlob is not null && bBlob is not null && aBlob.BlobOrdinal == bBlob.BlobOrdinal;
        _output.WriteLine($"  (a)+(b) merge status: {(abMerged ? "MERGED" : "SPLIT")} | (a)={aBlob?.BlobClass.ToString() ?? "none"} | (b)={bBlob?.BlobClass.ToString() ?? "none"} | (c)={cBlob?.BlobClass.ToString() ?? "none"}");

        int npcsReachingIcon = npcBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);
        _output.WriteLine($"  NPCs reaching Icon class: {npcsReachingIcon}/3");
    }

    /// <summary>
    /// mithril#1172 Phase 2.6 sweep on the 06-15 live-verification bundle.
    /// Sweeps <c>minLumaForDeviation ∈ {0, 140, 160, 180, 200}</c> × <c>closeRadius
    /// ∈ {0, 1}</c>. Cross-bundle confirmation that the threshold pick
    /// generalises beyond the 06-13 canonical bundle's icon positions.
    /// </summary>
    public static IEnumerable<object?[]> LumaCloseCombinations()
    {
        if (BundleDir() is null)
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

    [Theory]
    [MemberData(nameof(LumaCloseCombinations))]
    public void Measure_pre_deviation_luma_pipeline_on_0615_bundle(byte? minLumaForDeviation, int? closeRadius)
    {
        if (minLumaForDeviation is null || closeRadius is null)
        {
            _output.WriteLine($"SKIPPED — bundle '{BundleName}' not present.");
            return;
        }

        var dir = BundleDir()!;
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

        var opts = SceneCalibrationProfile.Indoor.BlobOptions with { MinPeakLuma = null };

        _ = DeviationBlobDetector.DetectIconBlobs(
            dev, shot.Width, shot.Height,
            lowNcc: 0.5, rim: RimMaskMode.DeviationFlood, opts,
            closeRadius: closeRadius.Value,
            hooks: hooks,
            meanNcc: meanNcc,
            logger: NullLogger.Instance,
            deviationMask: deviationMask);

        _output.WriteLine($"=== 0615: minLumaForDeviation={minLumaForDeviation} closeRadius={closeRadius} (Indoor T1+T2 gates, openRadius=0) ===");
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

        var npcBlobs = new (string Label, int X, int Y, BlobClassification? Blob)[NpcCentroids.Length];
        for (int i = 0; i < NpcCentroids.Length; i++)
        {
            var (label, x, y) = NpcCentroids[i];
            npcBlobs[i] = (label, x, y, ContainingBlob(x, y));
        }

        foreach (var (label, x, y, b) in npcBlobs)
        {
            if (b is null)
            {
                _output.WriteLine($"  NPC{label,-20} at ({x,4},{y,4})  NO blob contains");
            }
            else
            {
                _output.WriteLine(
                    $"  NPC{label,-20} at ({x,4},{y,4})  blob{b.BlobOrdinal,4} bbox({b.MinX},{b.MinY})+{b.W}x{b.H} " +
                    $"A={b.Area,5} sol={b.Solidity:F2} asp={b.Aspect:F2} peak={b.PeakDev:F2} -> {b.BlobClass}");
            }
        }

        var aBlob = npcBlobs[0].Blob;
        var bBlob = npcBlobs[1].Blob;
        var cBlob = npcBlobs[2].Blob;
        bool abMerged = aBlob is not null && bBlob is not null && aBlob.BlobOrdinal == bBlob.BlobOrdinal;
        bool aIsIcon = aBlob?.BlobClass == BlobClass.Icon;
        bool bIsIcon = bBlob?.BlobClass == BlobClass.Icon;
        _output.WriteLine($"  (a)+(b) status: {(abMerged ? "MERGED" : "SPLIT")} | (a)={(aIsIcon ? "Icon" : aBlob?.BlobClass.ToString() ?? "none")} | (b)={(bIsIcon ? "Icon" : bBlob?.BlobClass.ToString() ?? "none")} | (c)={cBlob?.BlobClass.ToString() ?? "none"}");

        int npcsReachingIcon = npcBlobs.Count(i => i.Blob?.BlobClass == BlobClass.Icon);
        _output.WriteLine($"  NPCs reaching Icon class: {npcsReachingIcon}/3");
    }
}
