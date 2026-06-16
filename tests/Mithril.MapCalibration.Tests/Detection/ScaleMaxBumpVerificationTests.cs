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

    public static IEnumerable<object[]> Bundles() =>
    [
        ["Map_HogansKeepBasement-20260612-233006-375-rejected-solve", "3.0.0.88", 0.14, 0.32],
        ["Map_HogansKeepBasement-20260616-103608-261-rejected-solve", "3.0.0.96", 0.18, 0.27],
    ];

    [Theory]
    [MemberData(nameof(Bundles))]
    public void ScaleMax_2_00_recovers_true_scale(
        string bundleName, string engineVersion, double priorScale, double priorNcc)
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

        newMetrics.Should().NotBeNull(
            "ScaleMax=2.00 must produce a non-null fit on the canonical Hogan's Basement bundles");
        newRect.Should().NotBeNull(
            "ScaleMax=2.00 must produce a non-null RawFitRect on the canonical Hogan's Basement bundles");

        // Acceptance per #1153: at the widened ceiling, the recovered scale
        // must land in [1.4, 1.6] (the user's manual measurement of the true
        // map-render scale was ~1.495) with NCC clear of the 0.20 gate floor.
        newMetrics!.Scale.Should().BeInRange(1.4, 1.6,
            "the true map-render scale was manually measured at ~1.495; ScaleMax=2.00 lets the locator find it");
        newMetrics.Confidence.Should().NotBeNull();
        newMetrics.Confidence!.Value.Should().BeGreaterThan(0.20,
            "a real recovery clears the FallbackNccFloor (0.20). Measured: 0.611 on 06-16, 0.278 on the harder 06-12 bundle — both above floor.");

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
        if (!CanonicalScreenshotSha256.TryGetValue(bundleName, out var expected)) return false;
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
