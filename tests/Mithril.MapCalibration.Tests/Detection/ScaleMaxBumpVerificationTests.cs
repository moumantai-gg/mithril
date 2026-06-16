using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1153 verification — replays the
/// <see cref="SobelPaddedPyramidRefiner"/> at the prior <c>ScaleMax = 1.20</c>
/// (pre-#1153) and the new <c>ScaleMax = 2.00</c> default against the two
/// corroborating Hogan's Keep Basement bundles (engine 3.0.0.88 from
/// 2026-06-12 and 3.0.0.96 from 2026-06-16). The 1.20 row reproduces the
/// ladder-bottom failure documented in the issue; the 2.00 row demonstrates
/// the recovered fit lands at the true ~1.50× scale with confidence well
/// above the 0.20 gate floor.
///
/// <para>Skippable per the dev-local-bundle convention (PG art + 2-decimal
/// zoom-slider give-rule out contributor reproducibility — see
/// <c>map_calibration_replay_fixtures_dev_local</c> in memory). Bundles live
/// under <c>%LOCALAPPDATA%/Mithril/diagnostics/calibration/</c>; the base
/// texture cache lives at <c>%LOCALAPPDATA%/Mithril/assets/</c>. Both must
/// be present for assertions to fire; otherwise the test prints SKIPPED
/// and returns clean.</para>
///
/// <para>SHA-256 of each bundle's <c>03-screenshot-gray.png</c> at the time
/// the assertions below were authored protects other devs whose own bundles
/// happen to live at the same path but contain a different capture
/// (mithril#1168 review: "dev-local bundles are foot-guns").</para>
/// </summary>
public sealed class ScaleMaxBumpVerificationTests
{
    private readonly ITestOutputHelper _output;
    public ScaleMaxBumpVerificationTests(ITestOutputHelper output) => _output = output;

    private const string MapAssetKey = "Map_HogansKeepBasement";

    /// <summary>
    /// SHA-256 of each bundle's <c>03-screenshot-gray.png</c> at the time the
    /// assertions in <see cref="ScaleMax_2_00_recovers_true_scale"/> were
    /// measured. When the on-disk hash differs, the test skips asserting and
    /// only prints the measurement so divergent dev-local bundles surface as
    /// data, not as red.
    /// </summary>
    private static readonly Dictionary<string, string> CanonicalScreenshotSha256 = new()
    {
        ["Map_HogansKeepBasement-20260612-233006-375-rejected-solve"] =
            "EB68F2E9F9F31FAB61ED25BDA297A441EC2DB0A4844B3C5EE6F12CAD2643C69E",
        ["Map_HogansKeepBasement-20260616-103608-261-rejected-solve"] =
            "B3D2009C1E6C1FF7A45A936BD9C9A46D18A51F7E283A99350B2672F36B34597C",
    };

    /// <summary>
    /// (bundleName, engineVersion, priorScale, priorNcc, newExpectedScale,
    /// newExpectedNcc). The "prior" pair is the auto-cal bundle's own recovered
    /// locator state (history — what the user saw). The "new expected" pair is
    /// the value measured by replaying the refiner at ScaleMax=2.00 at PR time;
    /// asserts use it with ±0.02 (one ladder step) on scale and ±0.10 on NCC.
    /// </summary>
    public static IEnumerable<object[]> Bundles() =>
    [
        ["Map_HogansKeepBasement-20260612-233006-375-rejected-solve", "3.0.0.88", 0.14, 0.32, 1.460, 0.278],
        ["Map_HogansKeepBasement-20260616-103608-261-rejected-solve", "3.0.0.96", 0.18, 0.27, 1.427, 0.611],
    ];

    [Theory]
    [MemberData(nameof(Bundles))]
    public void ScaleMax_2_00_recovers_true_scale(
        string bundleName, string engineVersion, double priorScale, double priorNcc,
        double newExpectedScale, double newExpectedNcc)
    {
        var bundleDir = BundleDir(bundleName);
        if (bundleDir is null)
        {
            _output.WriteLine($"SKIPPED [{bundleName}] — bundle absent in %LOCALAPPDATA%/Mithril/diagnostics/calibration/.");
            return;
        }

        var screenshotPath = Path.Combine(bundleDir, "03-screenshot-gray.png");
        if (!File.Exists(screenshotPath))
        {
            _output.WriteLine($"SKIPPED [{bundleName}] — bundle missing 03-screenshot-gray.png.");
            return;
        }

        var baseTexture = TryLoadBaseTexture(MapAssetKey);
        if (baseTexture is null)
        {
            _output.WriteLine($"SKIPPED [{bundleName}] — base texture for {MapAssetKey} absent in %LOCALAPPDATA%/Mithril/assets/.");
            return;
        }

        var screenshot = WicImageLoader.LoadGray(screenshotPath);
        bool sameAsCanonical = BundleMatchesCanonicalHash(screenshotPath, bundleName);

        _output.WriteLine($"=== {bundleName} (engine {engineVersion}) ===");
        _output.WriteLine($"  screenshot {screenshot.Width}x{screenshot.Height}; baseTex {baseTexture.Width}x{baseTexture.Height}");
        _output.WriteLine($"  prior auto-cal bundle: scale={priorScale:0.00}, fallbackNcc={priorNcc:0.000} (rejected-solve)");
        _output.WriteLine($"  canonical-hash match: {sameAsCanonical}");

        // Row 1: prior default (1.20) — must reproduce the ladder-bottom degenerate fit.
        var priorRefiner = new SobelPaddedPyramidRefiner(
            new MapCalibrationLocateOptions { ScaleMax = 1.20 },
            NullLogger<SobelPaddedPyramidRefiner>.Instance);
        var priorResult = priorRefiner.Refine(screenshot, baseTexture);

        _output.WriteLine($"  [ScaleMax=1.20] scale={priorResult.Metrics?.Scale:0.000}, "
            + $"ncc={priorResult.Metrics?.Confidence:0.000}, "
            + $"origin=({priorResult.RawFitRect?.OriginX},{priorResult.RawFitRect?.OriginY}), "
            + $"size={priorResult.RawFitRect?.Width}x{priorResult.RawFitRect?.Height}, "
            + $"accepted={priorResult.AcceptedRect is not null}");

        // Row 2: new default (2.00) — must recover the true ~1.50× scale.
        var newRefiner = new SobelPaddedPyramidRefiner(
            new MapCalibrationLocateOptions { ScaleMax = 2.00 },
            NullLogger<SobelPaddedPyramidRefiner>.Instance);
        var newResult = newRefiner.Refine(screenshot, baseTexture);

        var newRect = newResult.RawFitRect;
        var newMetrics = newResult.Metrics;
        _output.WriteLine($"  [ScaleMax=2.00] scale={newMetrics?.Scale:0.000}, "
            + $"ncc={newMetrics?.Confidence:0.000}, "
            + $"origin=({newRect?.OriginX},{newRect?.OriginY}), "
            + $"size={newRect?.Width}x{newRect?.Height}, "
            + $"accepted={newResult.AcceptedRect is not null}");
        if (newRect is not null)
        {
            // Real-zoom captures have scale > 1.0 — the texture at 1.46× is
            // LARGER than the 1313-px-tall capture, so the natural recovered
            // origin is negative on the truncated axis (the visible map view
            // shows the texture's interior, not its top-left). The meaningful
            // bounds check is that the rect INTERSECTS the capture — i.e. the
            // locator hasn't drifted entirely off-screen.
            bool intersectsCapture =
                newRect.OriginX + newRect.Width > 0 && newRect.OriginX < screenshot.Width &&
                newRect.OriginY + newRect.Height > 0 && newRect.OriginY < screenshot.Height;
            _output.WriteLine($"  [ScaleMax=2.00] rect intersects capture: {intersectsCapture}");
        }

        if (!sameAsCanonical)
        {
            _output.WriteLine($"  SKIPPED asserts — bundle's screenshot hash diverges from the canonical at the time these assertions were authored. The measurement above is the durable record.");
            return;
        }

        // === Prior-default (1.20) assertions — pin the documented ladder-bottom
        // failure mode so a future refactor that lets 1.20 also recover (e.g.
        // an adaptive coarse stage) fails loudly here. Without these the
        // measurement is a noisy log line, not a regression signal.
        priorResult.Metrics.Should().NotBeNull(
            "ScaleMax=1.20 should still produce a (degenerate) fit — the locator finds SOMETHING at the ladder bottom");
        priorResult.Metrics!.Scale.Should().BeLessThan(0.30,
            "ScaleMax=1.20 truncates the true ~1.5× scale; the locator picks ladder-bottom (measured 0.14 / 0.18 on these bundles). " +
            "If a future PR makes 1.20 also recover the true scale, update this assertion AND drop the bump in MapCalibrationLocateOptions.");
        priorResult.RawFitRect.Should().NotBeNull();
        priorResult.RawFitRect!.Width.Should().BeLessThan(300,
            "ladder-bottom fits are tiny degenerate patches (measured 143×143 and 184×184 on these bundles)");

        // === New-default (2.00) assertions — non-null fit, recovered scale
        // matches the measured value within one ladder step, NCC within a
        // wide tolerance of the measured value, recovered rect intersects
        // the capture.
        newMetrics.Should().NotBeNull(
            "ScaleMax=2.00 must produce a non-null fit on the canonical Hogan's Basement bundles");
        newRect.Should().NotBeNull(
            "ScaleMax=2.00 must produce a non-null RawFitRect on the canonical Hogan's Basement bundles");

        // Scale within ±0.02 of measured value (one ladder step at ScaleStep=0.02).
        newMetrics!.Scale.Should().BeInRange(newExpectedScale - 0.02, newExpectedScale + 0.02,
            $"the measured-at-PR-time recovered scale was {newExpectedScale:0.000} (true map-render scale ~1.495); " +
            "drift beyond one ladder step is a refiner regression");
        newMetrics.Confidence.Should().NotBeNull();

        // NCC within ±0.10 of measured value. Wider tolerance than the gate
        // floor (0.20) because legitimate downstream tuning (blur σ-curve,
        // FallbackPadPx, OpenCV impl) can nudge NCC by a few hundredths
        // without #1153 regressing. The 06-12 bundle's measured NCC is 0.278
        // — only 0.078 above the gate floor — so the previous flat 'NCC > 0.20'
        // assertion was brittle to any drop in that 0.08 margin.
        newMetrics.Confidence!.Value.Should().BeInRange(newExpectedNcc - 0.10, newExpectedNcc + 0.10,
            $"the measured-at-PR-time recovered NCC was {newExpectedNcc:0.000}; drift beyond ±0.10 is a refiner regression. " +
            "A drop below the FallbackNccFloor (0.20) trips the gate independently and shows as AcceptedRect == null.");

        // Above-floor floor: a final independent sanity check that the gate
        // accepts. This is the bedrock — even if the per-bundle NCC drifts,
        // the production gate must still be cleared, or the fix is no-op.
        newMetrics.Confidence!.Value.Should().BeGreaterThan(0.20,
            "the FallbackNccFloor (0.20) is the production gate; below this the rect is rejected and the fix is no-op");

        // The recovered rect must intersect the capture. At scale > 1.0 the
        // texture is LARGER than the screenshot, so the natural rect can have
        // negative origin on the truncated axis (the visible map shows the
        // texture's interior). The pre-#1153 ladder-bottom pick had origin
        // (1048, 891) at size 143×143 — a tiny degenerate patch in the bottom
        // corner of the screenshot, far from any sensible map view.
        newRect!.Width.Should().BeGreaterThan(0);
        newRect.Height.Should().BeGreaterThan(0);
        (newRect.OriginX + newRect.Width).Should().BeGreaterThan(0,
            "the recovered rect must intersect the capture horizontally");
        newRect.OriginX.Should().BeLessThan(screenshot.Width,
            "the recovered rect must intersect the capture horizontally");
        (newRect.OriginY + newRect.Height).Should().BeGreaterThan(0,
            "the recovered rect must intersect the capture vertically");
        newRect.OriginY.Should().BeLessThan(screenshot.Height,
            "the recovered rect must intersect the capture vertically");
    }

    private static string? BundleDir(string bundleName)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        var dir = Path.Combine(local, "Mithril", "diagnostics", "calibration", bundleName);
        return Directory.Exists(dir) ? dir : null;
    }

    private static bool BundleMatchesCanonicalHash(string shotPath, string bundleName)
    {
        // FAIL LOUDLY on a Bundles()-vs-CanonicalScreenshotSha256 dataset
        // inconsistency — without this, a future Theory row added without a
        // matching SHA entry would silently SKIP all assertions for that row
        // (TryGetValue=false is indistinguishable from hash divergence further
        // down). Real on-disk hash divergence still degrades to false → the
        // test prints the measurement and returns clean.
        if (!CanonicalScreenshotSha256.TryGetValue(bundleName, out var expected))
        {
            Assert.Fail($"bundle '{bundleName}' is in Bundles() but missing from CanonicalScreenshotSha256 — " +
                "add its 03-screenshot-gray.png SHA-256 to the dictionary, or remove the row from Bundles().");
            return false; // unreachable; here for the compiler
        }
        using var stream = File.OpenRead(shotPath);
        var bytes = SHA256.HashData(stream);
        var hex = Convert.ToHexString(bytes);
        return string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads the per-area base texture from <c>%LocalAppData%/Mithril/assets/</c>.
    /// Same manifest+blob shape as <c>CachedBaseTextureProvider</c>; reproduced
    /// here because that type is internal to Detection (matches the loader in
    /// <see cref="LiveMapViewProbeRealScreenshotBenchmark"/>).
    /// </summary>
    private static GrayImage? TryLoadBaseTexture(string mapAssetKey)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mithril", "assets");
        var manifestPath = Path.Combine(cacheDir, $"map-texture-{mapAssetKey}.json");
        var blobPath = Path.Combine(cacheDir, $"map-texture-{mapAssetKey}.bin");
        if (!File.Exists(manifestPath) || !File.Exists(blobPath)) return null;

        using var manifestStream = File.OpenRead(manifestPath);
        using var doc = JsonDocument.Parse(manifestStream);
        int w = doc.RootElement.GetProperty("width").GetInt32();
        int h = doc.RootElement.GetProperty("height").GetInt32();

        using var fileStream = File.OpenRead(blobPath);
        using var deflate = new DeflateStream(fileStream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        deflate.CopyTo(ms);
        var pixels = ms.ToArray();
        if (pixels.Length != w * h) return null;

        return new GrayImage(w, h, pixels);
    }
}
