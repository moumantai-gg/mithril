using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Real-screenshot regression for <see cref="LocatorBackedMapViewProbe"/> on
/// both outdoor (Serbule) and indoor (KhyruleksCrypt) PG captures. Loads a real
/// PG-captured screenshot from <c>study/diagnostic-bundles/</c> (gitignored,
/// populated locally from <c>%LocalAppData%/Mithril/diagnostics/calibration/</c>)
/// and the per-area base texture from <c>%LocalAppData%/Mithril/assets/</c>, runs
/// the probe with a <see cref="Stopwatch"/>, asserts a non-null fix, and
/// cross-checks the recovered (pan, viewScale) against the auto-cal bundle's
/// own locator result. Skips loudly in CI (assets absent).
///
/// <para>Catches both the failure mode the original <c>CrossCorrelationMapViewProbe</c>
/// had: (1) it took ~5 minutes on outdoor + ~25s on indoor, and (2) it returned
/// null on BOTH because raw NCC over a screenshot containing UI chrome around
/// the map produces a flat correlation landscape (mithril#1107).</para>
/// </summary>
public sealed class LiveMapViewProbeRealScreenshotBenchmark
{
    private readonly ITestOutputHelper _output;
    public LiveMapViewProbeRealScreenshotBenchmark(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("outdoor", "Map_AreaSerbule-accepted", "Map_AreaSerbule")]
    [InlineData("indoor", "Map_KhyruleksCrypt-locator-ok", "Map_KhyruleksCrypt")]
    public void LocatorBackedProbe_RecoversFix(string flavor, string bundleName, string mapAssetKey)
    {
        var bundleDir = LocateBundle(bundleName);
        if (bundleDir is null)
        {
            _output.WriteLine($"SKIPPED [{flavor}] — study/diagnostic-bundles/{bundleName}/ absent (gitignored; copy from %LocalAppData%/Mithril/diagnostics/calibration/ locally).");
            return;
        }

        var screenshotPath = Path.Combine(bundleDir, "03-screenshot-gray.png");
        var attemptJsonPath = Path.Combine(bundleDir, "01-attempt.json");
        if (!File.Exists(screenshotPath) || !File.Exists(attemptJsonPath))
        {
            _output.WriteLine($"SKIPPED [{flavor}] — bundle incomplete (need 03-screenshot-gray.png + 01-attempt.json).");
            return;
        }

        var baseTex = TryLoadBaseTexture(mapAssetKey);
        if (baseTex is null)
        {
            _output.WriteLine($"SKIPPED [{flavor}] — base texture for {mapAssetKey} not present in %LocalAppData%/Mithril/assets/.");
            return;
        }

        var expected = ReadAttemptLocatorBest(attemptJsonPath);
        var screenshot = WicImageLoader.LoadGray(screenshotPath);
        _output.WriteLine(
            $"[{flavor}/{mapAssetKey}] inputs: screenshot {screenshot.Width}x{screenshot.Height}, " +
            $"baseTex {baseTex.Width}x{baseTex.Height}. " +
            $"Bundle's locator: origin=({expected.OriginX},{expected.OriginY}) size={expected.Width}x{expected.Height} scale={expected.Scale:0.0000}.");

        var probe = BuildProbe();

        var sw = Stopwatch.StartNew();
        var fix = probe.TryProbe(screenshot, baseTex);
        sw.Stop();

        _output.WriteLine($"[{flavor}/{mapAssetKey}] TryProbe took {sw.ElapsedMilliseconds} ms.");
        fix.Should().NotBeNull($"the locator-backed probe must recover a fix for {flavor} area {mapAssetKey}");
        var f = fix!.Value;
        _output.WriteLine($"  recovered: pan=({f.PanTexPxX:F1},{f.PanTexPxY:F1}) viewScale={f.ViewScale:F4} conf={f.Confidence:F3}");

        // Expected viewScale = width / textureWidth (the locator's recovered
        // similarity for the visible map rect). Match bundle's own value within
        // 5% to allow for: (a) Sobel-pyramid's parabolic scale refine, which
        // may converge slightly off the bundle's coarse pick, (b) the locator
        // running fresh here rather than reading the bundle's cached result.
        double expectedViewScale = expected.Width / (double)baseTex.Width;
        f.ViewScale.Should().BeApproximately(expectedViewScale, expectedViewScale * 0.05,
            "viewScale should match the bundle's locator within 5%");

        // Expected pan = -origin / viewScale (overlay(origin) ↔ texture(0,0)).
        double expectedPanX = -expected.OriginX / f.ViewScale;
        double expectedPanY = -expected.OriginY / f.ViewScale;
        Math.Abs(f.PanTexPxX - expectedPanX).Should().BeLessThan(20,
            "panTexPxX should match bundle's recovered origin within 20 px");
        Math.Abs(f.PanTexPxY - expectedPanY).Should().BeLessThan(20,
            "panTexPxY should match bundle's recovered origin within 20 px");

        // Budget: spec target is <1s; allow 5s here so a slow CI / cold OpenCv
        // load doesn't flake. The original hand-rolled NCC took 5 minutes on
        // outdoor and 25s on indoor — anything in 5s territory proves the swap
        // delivered orders of magnitude.
        sw.Elapsed.TotalSeconds.Should().BeLessThan(5.0,
            "the locator-backed probe should be sub-5s per call");
    }

    private LocatorBackedMapViewProbe BuildProbe()
    {
        var options = new MapCalibrationLocateOptions();
        var fm = new FeatureMatchingRefiner(options,
            new XunitOutputLogger<FeatureMatchingRefiner>(_output));
        var sobel = new SobelPaddedPyramidRefiner(options,
            new XunitOutputLogger<SobelPaddedPyramidRefiner>(_output));
        var composite = new CompositeMapRegionRefiner(fm, sobel,
            new XunitOutputLogger<CompositeMapRegionRefiner>(_output));
        return new LocatorBackedMapViewProbe(composite,
            new XunitOutputLogger<LocatorBackedMapViewProbe>(_output));
    }

    private static string? LocateBundle(string bundleName)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "Mithril.slnx")))
            {
                var bundle = Path.Combine(dir, "study", "diagnostic-bundles", bundleName);
                return Directory.Exists(bundle) ? bundle : null;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// Loads the per-area base texture from the on-disk asset-extractor cache
    /// (<c>%LocalAppData%/Mithril/assets/map-texture-&lt;area&gt;.{json,bin}</c>).
    /// Same format as <c>CachedBaseTextureProvider</c> (manifest + DeflateStream-
    /// compressed gray pixel payload) but read directly here since that type is
    /// internal to <c>Mithril.MapCalibration.Detection</c>.
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

    /// <summary>
    /// Reads the bundle's <c>01-attempt.json</c> <c>locatorBest</c> rect — the
    /// auto-cal's own recovered map rect inside the screenshot, used here as the
    /// ground truth for the probe's recovered fix.
    /// </summary>
    private static LocatorBest ReadAttemptLocatorBest(string attemptJsonPath)
    {
        using var stream = File.OpenRead(attemptJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var lb = doc.RootElement.GetProperty("locatorBest");
        return new LocatorBest(
            OriginX: lb.GetProperty("originX").GetInt32(),
            OriginY: lb.GetProperty("originY").GetInt32(),
            Width: lb.GetProperty("width").GetInt32(),
            Height: lb.GetProperty("height").GetInt32(),
            Scale: lb.GetProperty("scale").GetDouble());
    }

    private readonly record struct LocatorBest(int OriginX, int OriginY, int Width, int Height, double Scale);
}

internal sealed class XunitOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => output.WriteLine($"[{logLevel}] {formatter(state, exception)}");
}
