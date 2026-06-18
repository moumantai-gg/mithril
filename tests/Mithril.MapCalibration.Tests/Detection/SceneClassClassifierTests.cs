using System.Collections.Generic;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// mithril#1163 Phase 1 — covers the
/// <see cref="FloorBoundaryMaskCache.GetSceneClass"/> /
/// <see cref="FloorBoundaryMaskCache.TryGetOpaqueFraction"/> API. The
/// classification rule (Outdoor when alpha-coverage ≥
/// <see cref="MapCalibrationDetectorOptions.SceneClassOpaqueFractionThreshold"/>,
/// Indoor otherwise) lives in the cache so the boundary mask + scene-class
/// label share the alpha load.
/// </summary>
public sealed class SceneClassClassifierTests
{
    [Fact]
    public void All_opaque_alpha_classifies_as_Outdoor()
    {
        // Outdoor maps (Serbule / Eltibule / Kur per the spike measurement) ship
        // alpha = 255 over the entire texture — opaque fraction 1.00, well above
        // the 0.95 threshold.
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_Outdoor", MakeUniformAlpha(64, 64, value: 255));
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_Outdoor").Should().Be(SceneClass.Outdoor);
        cache.TryGetOpaqueFraction("Map_Outdoor").Should().Be(1.0);
    }

    [Fact]
    public void Mostly_transparent_alpha_classifies_as_Indoor()
    {
        // Indoor maps (Hogan's etc per the spike) ship alpha = 0 over the
        // off-map regions — opaque fraction 0.07-0.36 per the measurement,
        // well below the 0.95 threshold.
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_Indoor", MakeAlphaWithOpaqueFraction(width: 100, height: 100, opaqueFraction: 0.20));
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_Indoor").Should().Be(SceneClass.Indoor);
        cache.TryGetOpaqueFraction("Map_Indoor").Should().BeApproximately(0.20, 0.001);
    }

    [Fact]
    public void Threshold_boundary_falls_on_Outdoor_side()
    {
        // The default threshold is 0.95 inclusive — a texture at exactly 0.95
        // opaque fraction classifies as Outdoor. The wide gap between Outdoor
        // (1.00) and Indoor (≤0.36) measured in the corpus gives this room.
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_Boundary", MakeAlphaWithOpaqueFraction(width: 100, height: 100, opaqueFraction: 0.95));
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_Boundary").Should().Be(SceneClass.Outdoor);
    }

    [Fact]
    public void Configurable_threshold_moves_the_boundary()
    {
        // Settings UI can tune the threshold; verify the cache honours it.
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_50pct", MakeAlphaWithOpaqueFraction(width: 100, height: 100, opaqueFraction: 0.50));
        var options = new MapCalibrationDetectorOptions { SceneClassOpaqueFractionThreshold = 0.40 };
        var cache = new FloorBoundaryMaskCache(provider, options);

        // 0.50 ≥ 0.40 → Outdoor under the tightened-down threshold.
        cache.GetSceneClass("Map_50pct").Should().Be(SceneClass.Outdoor);
    }

    [Fact]
    public void Missing_alpha_safe_degrades_to_Outdoor()
    {
        // Per spec §5.2 — when the provider can't furnish alpha (degraded
        // capture, asset cache miss, etc.) the safe-degrade path is Outdoor.
        // The Outdoor profile carries today's universal constants, so this
        // preserves pre-#1163 behaviour byte-identically.
        var provider = new FakeAlphaProvider();   // no SetAlpha — returns null
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_Absent").Should().Be(SceneClass.Outdoor);
        // Opaque fraction stays null when we couldn't measure.
        cache.TryGetOpaqueFraction("Map_Absent").Should().BeNull();
    }

    [Fact]
    public void Result_is_cached_across_calls()
    {
        // Caching invariant: repeated calls to the SAME public API on the same
        // key hit the provider exactly once. mithril#1183 review C3 dropped the
        // cross-API alpha cache (the prior pre-review state held the alpha
        // buffer ~1.5 MB/area indefinitely as a DI singleton — N areas → N
        // alpha buffers held forever). The trade is: GetSceneClass + GetOrCompute
        // on the same key now each pay one provider call. Subsequent calls of
        // each API short-circuit through the per-API cache (_sceneClassCache,
        // _cache). Net: 2 provider calls per area first-touch instead of 1, with
        // no resident alpha memory.
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_Cached", MakeAlphaWithOpaqueFraction(width: 32, height: 32, opaqueFraction: 0.50));
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_Cached").Should().Be(SceneClass.Indoor);
        cache.GetSceneClass("Map_Cached").Should().Be(SceneClass.Indoor);
        _ = cache.GetOrCompute("Map_Cached", dilationPx: 8);
        _ = cache.GetOrCompute("Map_Cached", dilationPx: 8);

        provider.AlphaCallCount("Map_Cached").Should().Be(2,
            "GetSceneClass + GetOrCompute each pay one alpha load (cross-API alpha sharing was dropped in #1183 review C3 to avoid the per-area memory growth); repeat calls of each API short-circuit through their per-API caches.");
    }

    [Fact]
    public void All_transparent_alpha_classifies_as_Indoor()
    {
        // Pathological: alpha = 0 everywhere is a degenerate boundary mask
        // input — but it still has a well-defined SceneClass (Indoor — opaque
        // fraction 0.00).
        var provider = new FakeAlphaProvider();
        provider.SetAlpha("Map_AllTransparent", MakeUniformAlpha(64, 64, value: 0));
        var cache = new FloorBoundaryMaskCache(provider, new MapCalibrationDetectorOptions());

        cache.GetSceneClass("Map_AllTransparent").Should().Be(SceneClass.Indoor);
        cache.TryGetOpaqueFraction("Map_AllTransparent").Should().Be(0.0);
        // Boundary mask path returns null on degenerate alpha (no boundary to
        // dilate) — the scene-class label is independent of that.
        cache.GetOrCompute("Map_AllTransparent", dilationPx: 8).Should().BeNull();
    }

    private static GrayImage MakeUniformAlpha(int w, int h, byte value)
    {
        var p = new byte[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = value;
        return new GrayImage(w, h, p);
    }

    private static GrayImage MakeAlphaWithOpaqueFraction(int width, int height, double opaqueFraction)
    {
        var p = new byte[width * height];
        int opaqueCount = (int)(width * height * opaqueFraction);
        for (int i = 0; i < opaqueCount; i++) p[i] = 255;
        return new GrayImage(width, height, p);
    }

    private sealed class FakeAlphaProvider : IBaseTextureProvider
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
