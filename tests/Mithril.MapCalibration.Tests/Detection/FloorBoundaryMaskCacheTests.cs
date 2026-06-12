using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1116 Task 2 — <see cref="FloorBoundaryMaskCache"/> derives a dilated
/// floor-boundary mask from the texture's alpha channel and caches the result
/// per area key. Returns null when alpha is unavailable or degenerate (all-opaque
/// or all-transparent) so the deviation-mask detector safe-degrades to the
/// unmasked-screenshot path.
/// </summary>
public sealed class FloorBoundaryMaskCacheTests
{
    [Fact]
    public void GetOrCompute_caches_per_area_key()
    {
        var provider = new FakeBaseTextureProvider();
        provider.SetAlpha("Map_A", MakeRectAlpha(10, 10, 2, 2, 6, 6));
        var opts = new MapCalibrationDetectorOptions { BoundaryDilationPx = 2 };
        var cache = new FloorBoundaryMaskCache(provider, opts);

        var first = cache.GetOrCompute("Map_A");
        var second = cache.GetOrCompute("Map_A");

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);                          // cache hit
        provider.AlphaCallCount("Map_A").Should().Be(1);          // provider called only once
    }

    [Fact]
    public void GetOrCompute_marks_band_around_alpha_edge_at_configured_dilation()
    {
        // 20×20 alpha with 10×10 opaque square at (5,5)-(14,14). Edge runs along
        // the square's outline. With BoundaryDilationPx=2, the mask should be a
        // 2-px-thick band straddling the outline (both inside and outside the
        // square).
        var alpha = MakeRectAlpha(20, 20, 5, 5, 14, 14);
        var provider = new FakeBaseTextureProvider();
        provider.SetAlpha("Map_X", alpha);
        var opts = new MapCalibrationDetectorOptions { BoundaryDilationPx = 2 };
        var cache = new FloorBoundaryMaskCache(provider, opts);

        var mask = cache.GetOrCompute("Map_X")!;
        mask.Should().NotBeNull();
        mask.Width.Should().Be(20);
        mask.Height.Should().Be(20);

        // Centre of square (10,10) — well inside, should NOT be masked.
        mask.Pixels[10 * 20 + 10].Should().Be(0);
        // Far from square (0,0) — well outside, should NOT be masked.
        mask.Pixels[0].Should().Be(0);
        // On the square's edge (5,10) — should be masked (on the boundary).
        mask.Pixels[10 * 20 + 5].Should().Be(255);
        // 1 px outside the boundary (4,10), within dilation radius — masked.
        mask.Pixels[10 * 20 + 3].Should().Be(255);
        // 1 px inside the boundary (7,10), within dilation radius — masked.
        mask.Pixels[10 * 20 + 7].Should().Be(255);
        // 3 px outside the boundary (1,10), beyond dilation radius — NOT masked.
        mask.Pixels[10 * 20 + 1].Should().Be(0);
    }

    [Fact]
    public void GetOrCompute_returns_null_on_missing_alpha()
    {
        var provider = new FakeBaseTextureProvider();
        // No alpha registered for "Map_Missing" → provider returns null.
        var opts = new MapCalibrationDetectorOptions();
        var cache = new FloorBoundaryMaskCache(provider, opts);

        cache.GetOrCompute("Map_Missing").Should().BeNull();
    }

    [Fact]
    public void GetOrCompute_returns_null_on_all_opaque_alpha()
    {
        var alpha = MakeUniformAlpha(10, 10, 255);
        var provider = new FakeBaseTextureProvider();
        provider.SetAlpha("Map_AllOpaque", alpha);
        var opts = new MapCalibrationDetectorOptions();
        var cache = new FloorBoundaryMaskCache(provider, opts);

        cache.GetOrCompute("Map_AllOpaque").Should().BeNull();
    }

    [Fact]
    public void GetOrCompute_returns_null_on_all_transparent_alpha()
    {
        var alpha = MakeUniformAlpha(10, 10, 0);
        var provider = new FakeBaseTextureProvider();
        provider.SetAlpha("Map_AllTransparent", alpha);
        var opts = new MapCalibrationDetectorOptions();
        var cache = new FloorBoundaryMaskCache(provider, opts);

        cache.GetOrCompute("Map_AllTransparent").Should().BeNull();
    }

    private static GrayImage MakeRectAlpha(int w, int h, int x0, int y0, int x1, int y1)
    {
        var p = new byte[w * h];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                p[y * w + x] = 255;
        return new GrayImage(w, h, p);
    }

    private static GrayImage MakeUniformAlpha(int w, int h, byte v)
    {
        var p = new byte[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = v;
        return new GrayImage(w, h, p);
    }

    /// <summary>
    /// Hand-rolled <see cref="IBaseTextureProvider"/> fake — the test project
    /// has no mocking framework dependency (FluentAssertions + xUnit only), so
    /// we register call counts manually. <see cref="TryGetBaseTexture"/> is
    /// unused by <see cref="FloorBoundaryMaskCache"/> and returns null.
    /// </summary>
    private sealed class FakeBaseTextureProvider : IBaseTextureProvider
    {
        private readonly Dictionary<string, GrayImage> _alpha = new();
        private readonly Dictionary<string, int> _alphaCalls = new();

        public void SetAlpha(string key, GrayImage image) => _alpha[key] = image;

        public int AlphaCallCount(string key) =>
            _alphaCalls.TryGetValue(key, out var n) ? n : 0;

        public GrayImage? TryGetBaseTexture(string mapAssetKey) => null;

        public GrayImage? TryGetTextureAlpha(string mapAssetKey)
        {
            _alphaCalls[mapAssetKey] = (_alphaCalls.TryGetValue(mapAssetKey, out var n) ? n : 0) + 1;
            return _alpha.TryGetValue(mapAssetKey, out var image) ? image : null;
        }
    }
}
