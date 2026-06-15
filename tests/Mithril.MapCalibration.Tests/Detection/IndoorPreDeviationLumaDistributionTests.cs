using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1172 pre-implementation measurement — scans the raw screenshot luma
/// distribution over each canonical merged-NPC-blob bbox to confirm (or
/// falsify) the hypothesis that PG indoor NPC pips (bright-white, luma &gt; 180)
/// separate cleanly from the connecting floor (mid-gray ~120–140) via a
/// single byte threshold.
///
/// <para>Findings commit to
/// <c>docs/planning/calibration-1155-scene-class-profile/measurements/indoor-pre-deviation-luma-distribution.md</c>.
/// If no clean valley exists between the bright and dim peaks, the
/// pre-deviation luma mechanism is falsified and the issue's chosen direction
/// has to revise upstream of implementation.</para>
///
/// <para>Bundle source: <c>06-aligned-screenshot.png</c> is Gray8 (saved by
/// <c>FilesystemCalibrationAttemptBundleSink</c> from <c>CapturedFrame.Gray</c>,
/// which is BT.601 luma of the live BGRA). So the bundle's gray IS the per-
/// pixel luma the production threshold will see at live-capture time — the
/// bundle is a faithful proxy for the live signal.</para>
/// </summary>
public sealed class IndoorPreDeviationLumaDistributionTests
{
    private readonly ITestOutputHelper _output;
    public IndoorPreDeviationLumaDistributionTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Per-bundle merged-NPC-bbox coordinates from the Phase 2.5 morph-open
    /// measurement's bbox traces (Finding 1 noted bbox dims ~36×44 to ~38×47
    /// across kernel settings; the bboxes below are the same regions at the
    /// production (openRadius=0, closeRadius=1) baseline).
    /// </summary>
    public static IEnumerable<object[]> Bundles()
    {
        yield return new object[] { "Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers", 410, 173, 48, 54 };
        yield return new object[] { "Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers", 453, 198, 39, 47 };
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Measure_luma_distribution_over_merged_NPC_bbox(string bundleName, int x0, int y0, int w, int h)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            _output.WriteLine("SKIPPED — no LocalApplicationData path.");
            return;
        }
        var dir = Path.Combine(local, "Mithril", "diagnostics", "calibration", bundleName);
        if (!Directory.Exists(dir))
        {
            _output.WriteLine($"SKIPPED — bundle '{bundleName}' not present.");
            return;
        }
        var shotPath = Path.Combine(dir, "06-aligned-screenshot.png");
        if (!File.Exists(shotPath))
        {
            _output.WriteLine($"SKIPPED — {shotPath} missing.");
            return;
        }

        var gray = WicImageLoader.LoadGray(shotPath);
        if (x0 + w > gray.Width || y0 + h > gray.Height || x0 < 0 || y0 < 0)
        {
            _output.WriteLine($"SKIPPED — bbox ({x0},{y0})+{w}x{h} out of frame {gray.Width}x{gray.Height}.");
            return;
        }

        int total = w * h;
        var lumaValues = new byte[total];
        var hist = new int[256];
        int idx = 0;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                byte luma = gray.Pixels[y * gray.Width + x];
                lumaValues[idx++] = luma;
                hist[luma]++;
            }
        }
        Array.Sort(lumaValues);

        _output.WriteLine($"=== {bundleName} ===");
        _output.WriteLine($"bbox ({x0},{y0})+{w}x{h} -> {total} pixels in 06-aligned-screenshot.png ({gray.Width}x{gray.Height})");
        _output.WriteLine($"min={lumaValues[0]}, p10={Pct(lumaValues, 10)}, p25={Pct(lumaValues, 25)}, p50={Pct(lumaValues, 50)}, p75={Pct(lumaValues, 75)}, p90={Pct(lumaValues, 90)}, max={lumaValues[^1]}");

        // 16-byte-wide-bin histogram (16 bins covering 0..255).
        _output.WriteLine("Histogram (16-byte bins):");
        const int binWidth = 16;
        const int nBins = 256 / binWidth;
        for (int i = 0; i < nBins; i++)
        {
            int binSum = 0;
            for (int v = i * binWidth; v < (i + 1) * binWidth; v++) binSum += hist[v];
            int pct = total == 0 ? 0 : (binSum * 100) / total;
            _output.WriteLine($"  [{i * binWidth,3}-{(i + 1) * binWidth - 1,3}] {binSum,5}  {pct,3}%  {new string('#', Math.Min(60, pct))}");
        }

        // Per-byte fine-grained sweep over the candidate threshold range
        // [120, 220] — narrows the valley location.
        _output.WriteLine("Fine-grained per-byte counts over [120, 220]:");
        for (int v = 120; v <= 220; v++)
        {
            int pct1000 = total == 0 ? 0 : (hist[v] * 1000) / total;
            if (hist[v] > 0)
                _output.WriteLine($"  luma={v,3}  count={hist[v],4}  {(pct1000 / 10.0):F1}%");
        }

        // Find the dim peak (bin with max count in 16-byte bins inside [0, 160))
        // and the bright peak (bin with max count in [160, 255]). Then locate
        // the bin with the SMALLEST count between them — that's the valley
        // candidate threshold.
        int dimPeakBin = -1, brightPeakBin = -1, dimPeakCount = -1, brightPeakCount = -1;
        for (int i = 0; i < nBins; i++)
        {
            int binSum = 0;
            for (int v = i * binWidth; v < (i + 1) * binWidth; v++) binSum += hist[v];
            int binCenter = i * binWidth + binWidth / 2;
            if (binCenter < 160 && binSum > dimPeakCount) { dimPeakCount = binSum; dimPeakBin = i; }
            if (binCenter >= 160 && binSum > brightPeakCount) { brightPeakCount = binSum; brightPeakBin = i; }
        }
        _output.WriteLine($"Dim peak: bin {dimPeakBin} ([{dimPeakBin * binWidth}-{(dimPeakBin + 1) * binWidth - 1}], count {dimPeakCount})");
        _output.WriteLine($"Bright peak: bin {brightPeakBin} ([{brightPeakBin * binWidth}-{(brightPeakBin + 1) * binWidth - 1}], count {brightPeakCount})");

        // Structural soft-invariant: the #1172 mechanism requires a bimodal
        // luma distribution over the merged-blob bbox — a dim peak (floor)
        // and a bright peak (icon pip) with the bright at higher luma than
        // the dim. If the distribution is unimodal or the peaks invert, the
        // pre-deviation luma gate has no separable threshold and the
        // mechanism's premise is falsified. This is a corpus-level claim
        // that holds across any Hogan's bundle of the relevant shape; it's
        // not bundle-hash-specific.
        dimPeakBin.Should().BeGreaterOrEqualTo(0, "merged-NPC bbox must contain dim (floor) pixels — falsifies if all bright.");
        brightPeakBin.Should().BeGreaterOrEqualTo(0, "merged-NPC bbox must contain bright (icon) pixels — falsifies if all dim.");
        brightPeakBin.Should().BeGreaterThan(dimPeakBin,
            "bright peak MUST sit at higher luma than dim peak — the #1172 mechanism's premise is bimodal floor-vs-icon separation. A peak inversion (bright below dim) falsifies the gate's viability.");

        if (dimPeakBin < 0 || brightPeakBin < 0 || brightPeakBin <= dimPeakBin)
        {
            _output.WriteLine("⚠ No clean bimodal distribution — mechanism may be falsified.");
            return;
        }

        // Find the per-byte minimum count between the two peaks.
        int valleyV = -1, valleyCount = int.MaxValue;
        int dimPeakEnd = (dimPeakBin + 1) * binWidth;
        int brightPeakStart = brightPeakBin * binWidth;
        for (int v = dimPeakEnd; v < brightPeakStart; v++)
        {
            if (hist[v] < valleyCount) { valleyCount = hist[v]; valleyV = v; }
        }
        _output.WriteLine($"Per-byte valley: luma={valleyV}, count={valleyCount}");

        // Recommended threshold: lowest byte >= bright-peak's lower edge where
        // the running cumulative count below stays at the valley's level. As
        // a rough heuristic, report what fraction of pixels would survive at
        // candidate thresholds 140, 160, 180, 200.
        _output.WriteLine("Survival fraction at candidate thresholds:");
        foreach (var thresh in new[] { 140, 160, 170, 180, 190, 200 })
        {
            int surv = 0;
            for (int v = thresh; v <= 255; v++) surv += hist[v];
            int pct = total == 0 ? 0 : (surv * 100) / total;
            _output.WriteLine($"  thresh={thresh}: {surv}/{total} survive ({pct}%)");
        }
    }

    private static byte Pct(byte[] sorted, int p)
    {
        if (sorted.Length == 0) return 0;
        int idx = Math.Min(sorted.Length - 1, (sorted.Length * p) / 100);
        return sorted[idx];
    }
}
