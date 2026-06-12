# Map auto-calibration: deviation-map mask — plan

**Spec:** [`spec.md`](spec.md). **Issue:** [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116). **Prereq:** [`sidecar-rgba-alpha-surface`](../sidecar-rgba-alpha-surface/) — Tasks 1-3 of that plan must land first. **Branch posture:** main fix lands on a feature branch + PR; the wiki Findings update + #1116 close-out happen post-merge.

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

Twelve tasks. Task 0 tunes the mask defaults on the corpus. Tasks 1-4 build the new vocabulary types (options class + three internal components) with TDD-first tests. Tasks 5-6 extend the existing detector seam to consume the mask. Tasks 7-8 wire it through the engine to bundles. Task 9 is telemetry plumbing. Tasks 10-11 are corpus + manual verification.

Tasks 1-7 can land independently and the code stays runtime-inert (mask defaults to null; `DeviationMaskingEnabled` defaults to true in §5.6 BUT the engine doesn't wire it until Task 8). This keeps each commit independently reviewable.

---

## Task 0 — Measurement: tune `BoundaryDilationPx` + fog detector constants

**Files:** Throwaway harness under `tools/DeviationMaskTuningSpike/` — analogous to #1061's `SparseLocateSpike.cs`, deleted when implementation lands.

**Goal:** Produce empirically-justified defaults for `BoundaryDilationPx`, `FogVarianceThreshold`, `FogColorMin`, `FogColorMax`. Plus the corpus-test floor numbers (`MegaBlobAreaCeiling`, `IconCountFloor`) that spec §D8 commits to.

**Steps:**

1. **Pre-req:** sidecar plan Tasks 1-5 must have landed; the alpha cache for `Map_HogansKeepBasement` + `Map_GoblinDungeon_TopFloor` + `Map_AreaEltibule` exists locally.

2. **Build a synthetic test harness.** Load each of the 5 reference bundles' `screenshot+texture+alpha` triplet (paths under `%LocalAppData%/Mithril/diagnostics/calibration/<bundle>/`). For each candidate `BoundaryDilationPx ∈ {2, 4, 6, 8, 10, 12}`, run the masked-deviation pipeline (the synchronous code paths from Tasks 2-6 — they don't need the engine wired) and capture the resulting `Structure mega-blob area` + `Icon-class blob count`.

3. **Pareto-pick the dilation:** lowest mega-blob area that doesn't drop Icon count below pre-fix baseline. Likely 6-10 px; default to 8 if there's no clear minimum.

4. **Tune fog detector constants** independently on Hogan's 091533 (has fog) + TopFloor 095806 (no fog). Sweep `FogVarianceThreshold ∈ {10, 20, 30, 40, 50}` × `FogColorMin/Max` ranges (start at `[100, 150]`, narrow). The criterion: Hogan's fog detected at ≥ 80 % of the user-observed fog regions AND TopFloor false-positive coverage < 5 %.

5. **Record the tuned defaults** in this plan (replace placeholders) AND in `MapCalibrationDetectorOptions.cs`'s constructor defaults AND in the corpus tests' expected thresholds.

**Tests:** None (spike).

**Acceptance:** Five production-ready defaults (with provenance — the corpus + measurement procedure), plus corpus-test floor numbers.

**Status placeholder (replace with tuned values when Task 0 lands):**

```text
BoundaryDilationPx       = TBD (target ~8)
FogVarianceThreshold     = TBD (target ~30)
FogColorMin / FogColorMax = TBD / TBD (target ~110 / 140)
MegaBlobAreaCeiling      = TBD (target ≤ 30,000 from baseline 119,655)
IconCountFloor           = TBD (target ≥ 60 from baseline ~50)
Corpus                   = 5 bundles: …list…
```

---

## Task 1 — `MapCalibrationDetectorOptions` (new schema-versioned settings class)

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs`
- Modify: [`src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs`](../../../src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs)
- Test: `tests/Mithril.MapCalibration.Tests/MapCalibrationDetectorOptionsTests.cs` (new)

**Steps:**

1. **Write the failing test first:**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapCalibrationDetectorOptionsTests
{
    [Fact]
    public void Defaults_match_spec_D5_and_D6()
    {
        var opts = new MapCalibrationDetectorOptions();
        opts.DeviationMaskingEnabled.Should().BeTrue();
        opts.BoundaryDilationPx.Should().Be(8);              // spec §D5 (Task 0-tuned)
        opts.FogOfWarDetectionEnabled.Should().BeTrue();
        opts.FogVarianceThreshold.Should().Be(30.0);          // spec §D6 (Task 0-tuned)
        opts.FogColorMin.Should().Be((byte)110);
        opts.FogColorMax.Should().Be((byte)140);
        opts.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Migrate_is_identity_for_v1()
    {
        var loaded = new MapCalibrationDetectorOptions { BoundaryDilationPx = 12 };
        var migrated = MapCalibrationDetectorOptions.Migrate(loaded);
        migrated.SchemaVersion.Should().Be(1);
        migrated.BoundaryDilationPx.Should().Be(12);
    }
}
```

Run, expect FAIL (class doesn't exist).

2. **Create `MapCalibrationDetectorOptions.cs`:**

```csharp
using Mithril.Shared.Settings;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Settings for the detector pipeline's deviation-mask step (mithril#1116).
/// Persists to <c>map-calibration-detector.json</c> via
/// <c>AddMithrilVersionedSettings</c> — parallel to
/// <see cref="MapCalibrationLocateOptions"/>.
/// </summary>
public sealed class MapCalibrationDetectorOptions : IVersionedState<MapCalibrationDetectorOptions>
{
    private bool   _deviationMaskingEnabled  = true;
    private int    _boundaryDilationPx       = 8;     // Task 0-tuned
    private bool   _fogOfWarDetectionEnabled = true;
    private double _fogVarianceThreshold     = 30.0;  // Task 0-tuned
    private byte   _fogColorMin              = 110;   // Task 0-tuned
    private byte   _fogColorMax              = 140;   // Task 0-tuned

    /// <summary>Master switch for the mithril#1116 mask. Off = pre-#1116 behavior.</summary>
    public bool DeviationMaskingEnabled
    {
        get => _deviationMaskingEnabled;
        set { if (_deviationMaskingEnabled != value) { _deviationMaskingEnabled = value; OnChanged(); } }
    }

    /// <summary>Boundary band dilation in px around floor-not-floor alpha edge.</summary>
    public int BoundaryDilationPx
    {
        get => _boundaryDilationPx;
        set { if (_boundaryDilationPx != value) { _boundaryDilationPx = value; OnChanged(); } }
    }

    public bool FogOfWarDetectionEnabled
    {
        get => _fogOfWarDetectionEnabled;
        set { if (_fogOfWarDetectionEnabled != value) { _fogOfWarDetectionEnabled = value; OnChanged(); } }
    }

    public double FogVarianceThreshold
    {
        get => _fogVarianceThreshold;
        set { if (_fogVarianceThreshold != value) { _fogVarianceThreshold = value; OnChanged(); } }
    }

    public byte FogColorMin
    {
        get => _fogColorMin;
        set { if (_fogColorMin != value) { _fogColorMin = value; OnChanged(); } }
    }

    public byte FogColorMax
    {
        get => _fogColorMax;
        set { if (_fogColorMax != value) { _fogColorMax = value; OnChanged(); } }
    }

    public int SchemaVersion { get; set; } = 1;
    public static int Version => 1;
    public static MapCalibrationDetectorOptions Migrate(MapCalibrationDetectorOptions loaded) => loaded;

    public event Action? Changed;
    private void OnChanged() => Changed?.Invoke();
}
```

(If the actual `IVersionedState<T>` interface differs in this codebase — verify against `MapCalibrationLocateOptions.cs` and copy that pattern exactly. The interface members shown above are placeholders for whatever the real contract is.)

3. **Register in DI.** Find the existing `MapCalibrationLocateOptions` registration in `DetectionServiceCollectionExtensions.cs` and add a parallel one for `MapCalibrationDetectorOptions`.

4. Run tests, watch them PASS.

5. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs
git add src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs
git add tests/Mithril.MapCalibration.Tests/MapCalibrationDetectorOptionsTests.cs
git commit -m "feat(map-calibration): MapCalibrationDetectorOptions + default settings (#1116)"
```

---

## Task 2 — `FloorBoundaryMaskCache` (new + TDD)

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs`
- Test: `tests/Mithril.MapCalibration.Tests/Detection/FloorBoundaryMaskCacheTests.cs` (new)

**Steps:**

1. **Write failing tests first:**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using NSubstitute;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class FloorBoundaryMaskCacheTests
{
    [Fact]
    public void GetOrCompute_caches_per_area_key()
    {
        var provider = Substitute.For<IBaseTextureProvider>();
        provider.TryGetTextureAlpha("Map_A").Returns(SyntheticAlpha(10, 10, true));
        var opts = new MapCalibrationDetectorOptions { BoundaryDilationPx = 2 };
        var cache = new FloorBoundaryMaskCache(provider, opts);

        var first  = cache.GetOrCompute("Map_A");
        var second = cache.GetOrCompute("Map_A");

        first.Should().NotBeNull();
        second.Should().BeSameAs(first);                  // identity = cache hit
        provider.Received(1).TryGetTextureAlpha("Map_A"); // once, not twice
    }

    [Fact]
    public void GetOrCompute_traces_alpha_edge_with_dilation()
    {
        // 10×10 alpha: 5×5 opaque square at (2,2). Edge is a 1-px outline at the
        // square boundary. With BoundaryDilationPx=2, the mask should be 2-px-thick
        // band straddling the square's outline.
        var alpha = SyntheticRectAlpha(width: 10, height: 10, x0: 2, y0: 2, x1: 6, y1: 6);
        var provider = Substitute.For<IBaseTextureProvider>();
        provider.TryGetTextureAlpha("Map_X").Returns(alpha);
        var opts = new MapCalibrationDetectorOptions { BoundaryDilationPx = 2 };
        var cache = new FloorBoundaryMaskCache(provider, opts);

        var mask = cache.GetOrCompute("Map_X")!;

        mask.Should().NotBeNull();
        // Center of the opaque square — well inside, mask should be 0.
        mask.Pixels[4 * 10 + 4].Should().Be(0);
        // Far from the square — outside, mask should be 0.
        mask.Pixels[0].Should().Be(0);
        // At the square's edge — masked.
        mask.Pixels[2 * 10 + 4].Should().Be(255);
    }

    [Fact]
    public void GetOrCompute_returns_null_on_missing_alpha()
    {
        var provider = Substitute.For<IBaseTextureProvider>();
        provider.TryGetTextureAlpha("Map_Missing").Returns((GrayImage?)null);
        var opts = new MapCalibrationDetectorOptions();
        var cache = new FloorBoundaryMaskCache(provider, opts);

        cache.GetOrCompute("Map_Missing").Should().BeNull();
    }

    [Fact]
    public void GetOrCompute_returns_null_on_degenerate_alpha()
    {
        // All-opaque alpha: no boundary anywhere.
        var alpha = SyntheticAlpha(10, 10, allOpaque: true);
        var provider = Substitute.For<IBaseTextureProvider>();
        provider.TryGetTextureAlpha("Map_Solid").Returns(alpha);
        var opts = new MapCalibrationDetectorOptions();
        var cache = new FloorBoundaryMaskCache(provider, opts);

        cache.GetOrCompute("Map_Solid").Should().BeNull();
    }

    // Test helpers (or move to a shared TestImages.cs):
    private static GrayImage SyntheticAlpha(int w, int h, bool allOpaque)
        => new(w, h, Enumerable.Repeat((byte)(allOpaque ? 255 : 0), w * h).ToArray());

    private static GrayImage SyntheticRectAlpha(int width, int height, int x0, int y0, int x1, int y1)
    {
        var pixels = new byte[width * height];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                pixels[y * width + x] = 255;
        return new GrayImage(width, height, pixels);
    }
}
```

Run, expect FAIL (class doesn't exist).

2. **Create `FloorBoundaryMaskCache.cs`:**

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-area cache for the floor-boundary mask used by the mithril#1116
/// deviation-mask step. The mask is the dilated edge of the texture's alpha
/// channel — opaque pixels (floor) bordering transparent pixels (not floor)
/// produce a band that's subtracted from the deviation map's foreground.
/// </summary>
internal sealed class FloorBoundaryMaskCache
{
    private readonly IBaseTextureProvider _provider;
    private readonly MapCalibrationDetectorOptions _options;
    private readonly Dictionary<string, GrayImage> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ILogger? _logger;

    public FloorBoundaryMaskCache(
        IBaseTextureProvider provider,
        MapCalibrationDetectorOptions options,
        ILogger<FloorBoundaryMaskCache>? logger = null)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    /// <returns>The boundary-band mask for <paramref name="mapAssetKey"/> (255 = masked,
    /// 0 = include). <see langword="null"/> when the alpha is unavailable or degenerate
    /// (all opaque / all transparent) — caller safe-degrades.</returns>
    public GrayImage? GetOrCompute(string mapAssetKey)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(mapAssetKey, out var cached)) return cached;
        }
        var alpha = _provider.TryGetTextureAlpha(mapAssetKey);
        if (alpha is null)
        {
            _logger?.LogWarning(
                "Floor boundary mask unavailable for {MapAsset} (sidecar alpha miss); fog mask only.",
                mapAssetKey);
            return null;
        }
        var mask = ComputeBoundaryMask(alpha, _options.BoundaryDilationPx);
        if (mask is null)
        {
            _logger?.LogWarning(
                "Floor boundary mask degenerate for {MapAsset} (all-opaque or all-transparent alpha); fog mask only.",
                mapAssetKey);
            return null;
        }
        lock (_gate)
        {
            _cache[mapAssetKey] = mask;
        }
        _logger?.LogInformation(
            "Floor boundary mask computed for {MapAsset} ({W}x{H}, dilation={Dilation}px).",
            mapAssetKey, mask.Width, mask.Height, _options.BoundaryDilationPx);
        return mask;
    }

    /// <summary>
    /// Edge-detect on alpha channel (binary threshold + Sobel, or convolutional
    /// edge), then dilate the edge by <paramref name="dilationPx"/>.
    /// Returns null when the alpha is degenerate (all 0 or all 255).
    /// </summary>
    private static GrayImage? ComputeBoundaryMask(GrayImage alpha, int dilationPx)
    {
        // Degenerate-alpha guard: if alpha has no variation, there's no boundary.
        bool sawOpaque = false, sawTransparent = false;
        foreach (var p in alpha.Pixels)
        {
            if (p >= 128) sawOpaque = true; else sawTransparent = true;
            if (sawOpaque && sawTransparent) break;
        }
        if (!sawOpaque || !sawTransparent) return null;

        int w = alpha.Width, h = alpha.Height;
        var edge = new bool[w * h];

        // 4-connected boundary detection: a pixel is on the edge if it's opaque
        // (alpha >= 128) but at least one 4-neighbor is transparent. The dilation
        // expands the edge zone outward both into the floor interior and into the
        // not-floor exterior, since both contribute boundary-band deviation.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                bool here = alpha.Pixels[idx] >= 128;
                bool boundary =
                       (x > 0     && (alpha.Pixels[idx - 1] >= 128) != here)
                    || (x < w - 1 && (alpha.Pixels[idx + 1] >= 128) != here)
                    || (y > 0     && (alpha.Pixels[idx - w] >= 128) != here)
                    || (y < h - 1 && (alpha.Pixels[idx + w] >= 128) != here);
                edge[idx] = boundary;
            }

        // Dilate by `dilationPx` (square structuring element of size (2*d+1)).
        // Pure morphological dilation: any pixel within d of an edge pixel becomes set.
        // Use a separable approach for cost: horizontal pass, then vertical pass.
        var dilated = Dilate(edge, w, h, dilationPx);

        var maskPixels = new byte[w * h];
        for (int i = 0; i < w * h; i++) maskPixels[i] = dilated[i] ? (byte)255 : (byte)0;
        return new GrayImage(w, h, maskPixels);
    }

    private static bool[] Dilate(bool[] src, int w, int h, int radius)
    {
        // 1D dilation along each row, then each column. Equivalent to a single
        // (2r+1)×(2r+1) max-filter for a binary input.
        var horiz = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int yw = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                bool any = false;
                for (int xi = x0; xi <= x1; xi++)
                {
                    if (src[yw + xi]) { any = true; break; }
                }
                horiz[yw + x] = any;
            }
        }
        var vert = new bool[w * h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                bool any = false;
                for (int yi = y0; yi <= y1; yi++)
                {
                    if (horiz[yi * w + x]) { any = true; break; }
                }
                vert[y * w + x] = any;
            }
        }
        return vert;
    }
}
```

3. Run tests, expect PASS.

4. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs
git add tests/Mithril.MapCalibration.Tests/Detection/FloorBoundaryMaskCacheTests.cs
git commit -m "feat(map-calibration): FloorBoundaryMaskCache — alpha-edge mask + per-area cache (#1116)"
```

---

## Task 3 — `FogOfWarDetector` (new + TDD)

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/Internal/FogOfWarDetector.cs`
- Test: `tests/Mithril.MapCalibration.Tests/Detection/FogOfWarDetectorTests.cs` (new)

**Steps:**

1. **Write failing tests:**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class FogOfWarDetectorTests
{
    [Fact]
    public void Detect_marks_uniform_low_variance_fog_color_region()
    {
        // Half "fog" (uniform grey 125) + half "floor detail" (high-variance noise).
        var img = SplitImage(width: 20, height: 20, fogValue: 125, detailSeed: 42);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0, FogColorMin = 110, FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);

        var fog = detector.Detect(img);

        // Center of fog half: marked.
        fog.Pixels[5 * 20 + 5].Should().Be(255);
        // Center of detail half: not marked.
        fog.Pixels[5 * 20 + 15].Should().Be(0);
    }

    [Fact]
    public void Detect_rejects_uniform_bright_region_outside_fog_color_window()
    {
        // Uniform grey 200 — low variance, but above FogColorMax = 140.
        var img = UniformImage(20, 20, 200);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0, FogColorMin = 110, FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);

        var fog = detector.Detect(img);

        fog.Pixels.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void Detect_rejects_high_variance_grey_region()
    {
        // Noisy fog-color noise (mean ~125, variance well above threshold).
        var img = NoisyImage(20, 20, mean: 125, range: 60, seed: 1);
        var opts = new MapCalibrationDetectorOptions
        {
            FogVarianceThreshold = 30.0, FogColorMin = 110, FogColorMax = 140,
        };
        var detector = new FogOfWarDetector(opts);

        var fog = detector.Detect(img);

        // Center pixel — should NOT be marked because local variance exceeds threshold.
        fog.Pixels[5 * 20 + 5].Should().Be(0);
    }

    [Fact]
    public void Detect_returns_empty_mask_when_disabled()
    {
        var img = UniformImage(20, 20, 125);
        var opts = new MapCalibrationDetectorOptions { FogOfWarDetectionEnabled = false };
        var detector = new FogOfWarDetector(opts);

        var fog = detector.Detect(img);

        fog.Pixels.Should().AllBeEquivalentTo((byte)0);
    }

    private static GrayImage UniformImage(int w, int h, byte value)
        => new(w, h, Enumerable.Repeat(value, w * h).ToArray());

    private static GrayImage NoisyImage(int w, int h, byte mean, byte range, int seed)
    {
        var rng = new Random(seed);
        var p = new byte[w * h];
        for (int i = 0; i < p.Length; i++)
            p[i] = (byte)Math.Clamp(mean + rng.Next(-range / 2, range / 2 + 1), 0, 255);
        return new GrayImage(w, h, p);
    }

    private static GrayImage SplitImage(int width, int height, byte fogValue, int detailSeed)
    {
        var rng = new Random(detailSeed);
        var p = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                p[y * width + x] = (byte)(x < width / 2 ? fogValue : rng.Next(0, 255));
        return new GrayImage(width, height, p);
    }
}
```

2. **Create `FogOfWarDetector.cs`:**

```csharp
using System;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Per-screenshot detector for fog-of-war regions in the recovered map crop.
/// Residual-coverage component for mithril#1116: <c>LocalNccDeviation.DeviationMap</c>'s
/// <c>addedOnly: true</c> mode already suppresses fog-INTERIOR pixels (the
/// canonical fog discriminator); this catches fog-region EDGES where the
/// screenshot's variance from the soft fog falloff produces asymmetric
/// <c>va &gt; vb</c> and slips through the addedOnly gate.
/// </summary>
internal sealed class FogOfWarDetector
{
    private readonly MapCalibrationDetectorOptions _options;

    public FogOfWarDetector(MapCalibrationDetectorOptions options) => _options = options;

    /// <summary>Returns a binary fog mask (255 = fog, 0 = not fog).</summary>
    public GrayImage Detect(GrayImage screenshotRoi)
    {
        int w = screenshotRoi.Width, h = screenshotRoi.Height;
        var mask = new byte[w * h];

        if (!_options.FogOfWarDetectionEnabled)
            return new GrayImage(w, h, mask);   // all zeros

        const int win = 7;
        int r = win / 2;
        byte colorMin = _options.FogColorMin;
        byte colorMax = _options.FogColorMax;
        double varThr = _options.FogVarianceThreshold;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                byte v = screenshotRoi.Pixels[idx];

                // Cheap luminance window check first — short-circuit if outside.
                if (v < colorMin || v > colorMax) { mask[idx] = 0; continue; }

                // Local variance in win×win neighborhood.
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
                long sum = 0, sumSq = 0;
                int n = 0;
                for (int yi = y0; yi <= y1; yi++)
                {
                    int yw = yi * w;
                    for (int xi = x0; xi <= x1; xi++)
                    {
                        byte p = screenshotRoi.Pixels[yw + xi];
                        sum += p;
                        sumSq += p * p;
                        n++;
                    }
                }
                double mean = (double)sum / n;
                double variance = (double)sumSq / n - mean * mean;

                mask[idx] = variance < varThr ? (byte)255 : (byte)0;
            }
        }

        return new GrayImage(w, h, mask);
    }
}
```

3. Run tests, expect PASS.

4. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/Internal/FogOfWarDetector.cs
git add tests/Mithril.MapCalibration.Tests/Detection/FogOfWarDetectorTests.cs
git commit -m "feat(map-calibration): FogOfWarDetector — variance+luminance fog-edge mask (#1116)"
```

---

## Task 4 — `DeviationMaskCombiner` (new + TDD)

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/Internal/DeviationMaskCombiner.cs`
- Test: `tests/Mithril.MapCalibration.Tests/Detection/DeviationMaskCombinerTests.cs` (new)

**Steps:**

1. **Write failing tests:**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class DeviationMaskCombinerTests
{
    [Fact]
    public void Combine_ORs_two_masks()
    {
        var floor = new GrayImage(2, 1, new byte[] { 255, 0 });
        var fog   = new GrayImage(2, 1, new byte[] { 0,   255 });
        var combined = DeviationMaskCombiner.Combine(floor, fog, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 255, 255 });
    }

    [Fact]
    public void Combine_returns_floor_when_fog_null()
    {
        var floor = new GrayImage(2, 1, new byte[] { 255, 0 });
        var combined = DeviationMaskCombiner.Combine(floor, fog: null, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 255, 0 });
    }

    [Fact]
    public void Combine_returns_fog_when_floor_null()
    {
        var fog = new GrayImage(2, 1, new byte[] { 0, 255 });
        var combined = DeviationMaskCombiner.Combine(floor: null, fog, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 0, 255 });
    }

    [Fact]
    public void Combine_returns_empty_when_both_null()
    {
        var combined = DeviationMaskCombiner.Combine(floor: null, fog: null, 2, 1);
        combined.Pixels.Should().Equal(new byte[] { 0, 0 });
    }
}
```

2. **Create the combiner:**

```csharp
namespace Mithril.MapCalibration.Detection.Internal;

internal static class DeviationMaskCombiner
{
    /// <summary>
    /// OR-combine two binary masks into one. Either mask may be null; null = "nothing
    /// masked" for that source. Output is always non-null at the requested dimensions.
    /// </summary>
    public static GrayImage Combine(GrayImage? floor, GrayImage? fog, int width, int height)
    {
        int n = width * height;
        var combined = new byte[n];
        if (floor is null && fog is null) return new GrayImage(width, height, combined);

        for (int i = 0; i < n; i++)
        {
            byte a = floor?.Pixels[i] ?? 0;
            byte b = fog?.Pixels[i] ?? 0;
            combined[i] = (a > 0 || b > 0) ? (byte)255 : (byte)0;
        }
        return new GrayImage(width, height, combined);
    }
}
```

3. Run tests, expect PASS.

4. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/Internal/DeviationMaskCombiner.cs
git add tests/Mithril.MapCalibration.Tests/Detection/DeviationMaskCombinerTests.cs
git commit -m "feat(map-calibration): DeviationMaskCombiner — OR-combine boundary + fog masks (#1116)"
```

---

## Task 5 — `DeviationMaskSnapshot` record + `DetectionDiagnosticHooks.OnDeviationMask` hook

**Files:**
- Modify: [`src/Mithril.MapCalibration.Detection/DetectionDiagnosticHooks.cs`](../../../src/Mithril.MapCalibration.Detection/DetectionDiagnosticHooks.cs) (find the actual file via grep — wherever `OnRimMask` is declared today)
- Create: a `DeviationMaskSnapshot` record alongside the existing `RimMaskSnapshot`.

**Steps:**

1. Find where `RimMaskSnapshot` + `DetectionDiagnosticHooks.OnRimMask` are declared:

```bash
grep -rn "RimMaskSnapshot" src/Mithril.MapCalibration.Detection/
```

Likely in `DetectionDiagnosticHooks.cs` or `RimMaskSnapshot.cs`. Whatever the file, locate the existing pattern.

2. Add the new snapshot record next to `RimMaskSnapshot`:

```csharp
/// <summary>
/// Mithril#1116: emitted by <see cref="DeviationBlobDetector.DetectIconBlobs"/>
/// right after the deviation-mask subtract step, before morph-close. Mirrors
/// <see cref="RimMaskSnapshot"/>'s shape so the bundle observability path stays
/// uniform.
/// </summary>
public sealed record DeviationMaskSnapshot(
    bool Rotate180,
    int Width,
    int Height,
    int MaskPixelCount,
    int FgInputCount,
    int FgSurvivorCount,
    ReadOnlyMemory<bool> MaskBuffer);
```

3. Add the hook member to `DetectionDiagnosticHooks`:

```csharp
public Action<DeviationMaskSnapshot>? OnDeviationMask { get; init; }
```

4. Build & run existing tests; ensure nothing broke (this is additive, so should be green).

5. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/DetectionDiagnosticHooks.cs   # adjust path as found
git commit -m "feat(map-calibration): DeviationMaskSnapshot record + OnDeviationMask hook (#1116)"
```

---

## Task 6 — `DetectionRequest.DeviationMask` field + `DeviationBlobDetector.DetectIconBlobs` mask subtract

**Files:**
- Modify: [`src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs) (where `DetectionRequest` is declared — find with grep)
- Modify: [`src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs)
- Modify: [`src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs) (thread the new field through)
- Test: `tests/Mithril.MapCalibration.Tests/Detection/DeviationStepMaskTests.cs` (new)

**Steps:**

1. **Write a failing test** that drives the integration:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class DeviationStepMaskTests
{
    [Fact]
    public void DetectIconBlobs_zeroes_fg_pixels_where_mask_is_set()
    {
        int w = 20, h = 20;
        // dev: above threshold across the full image
        var dev = new float[w * h];
        for (int i = 0; i < dev.Length; i++) dev[i] = 0.9f;

        // Mask: cover left half
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++) mask[y * w + x] = true;

        var blobs = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, lowNcc: 0.5, RimMaskMode.None,
            new BlobOptions(MinArea: 1, MaxIconArea: 1000, MinSolidity: 0.0, MaxAspect: 100, MinPeak: 0.0),
            closeRadius: 0,
            deviationMask: mask);

        // No icon-class blobs should land in the masked half.
        foreach (var b in blobs)
            (b.MinX >= w / 2).Should().BeTrue("masked region should contribute no blobs");
    }

    [Fact]
    public void DetectIconBlobs_preserves_behavior_when_mask_null()
    {
        int w = 20, h = 20;
        var dev = new float[w * h];
        for (int i = 0; i < dev.Length; i++) dev[i] = 0.9f;

        var blobsMasked = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, 0.5, RimMaskMode.None,
            new BlobOptions(1, 1000, 0.0, 100, 0.0), closeRadius: 0, deviationMask: null);
        var blobsNoMask = DeviationBlobDetector.DetectIconBlobs(
            dev, w, h, 0.5, RimMaskMode.None,
            new BlobOptions(1, 1000, 0.0, 100, 0.0), closeRadius: 0);

        blobsMasked.Count.Should().Be(blobsNoMask.Count);
    }
}
```

(If the `DeviationBlobDetector.DetectIconBlobs` signature is different than I'm assuming, adjust the test args — but the assertion shape stays.)

2. **Add the new param to `DetectIconBlobs`:**

```csharp
public static IReadOnlyList<BlobFeat> DetectIconBlobs(
    float[] dev, int w, int h, double lowNcc, RimMaskMode rim, BlobOptions opts, int closeRadius,
    DetectionDiagnosticHooks? hooks = null,
    double meanNcc = double.NaN,
    ILogger? logger = null,
    bool[]? deviationMask = null)   // NEW — last param; default null preserves call-sites
{
    /* …existing threshold loop unchanged… */
    /* …existing rim-subtract block unchanged… */

    // NEW: deviation-mask subtract (#1116). Same fg[i] = false shape as rim subtract.
    if (deviationMask is not null)
    {
        if (hooks?.OnDeviationMask is not null)
        {
            int maskedCount = 0, fgInputCount = 0, fgSurvivorCount = 0;
            for (int i = 0; i < n; i++) if (fg[i]) fgInputCount++;
            for (int i = 0; i < n; i++)
            {
                if (deviationMask[i]) { maskedCount++; fg[i] = false; }
            }
            for (int i = 0; i < n; i++) if (fg[i]) fgSurvivorCount++;
            hooks.OnDeviationMask(new DeviationMaskSnapshot(
                Rotate180: false, Width: w, Height: h,
                MaskPixelCount: maskedCount,
                FgInputCount: fgInputCount,
                FgSurvivorCount: fgSurvivorCount,
                MaskBuffer: ((bool[])deviationMask.Clone()).AsMemory()));
            logger?.LogTrace(
                "DeviationMask (rotate180=False): masked={Masked} of {Total} px, fg pre={Pre} post={Post}.",
                maskedCount, n, fgInputCount, fgSurvivorCount);
        }
        else
        {
            for (int i = 0; i < n; i++) if (deviationMask[i]) fg[i] = false;
        }
    }

    /* …existing morph-close + components + classify unchanged… */
}
```

The mask subtract MUST happen AFTER the rim subtract but BEFORE morph-close. Same shape as the existing rim subtract; the hook is null-fast-pathed.

3. **Add `DeviationMask` to `DetectionRequest`** (find the record/class — grep for `record DetectionRequest` or `class DetectionRequest`):

```csharp
public sealed record DetectionRequest(
    /* …existing fields… */
    bool[]? DeviationMask = null);   // NEW — additive; default null
```

4. **Thread it through `DeviationBlobCalibrationDetector.Detect`:**

Locate the existing `DeviationBlobDetector.DetectIconBlobs(...)` call at [`DeviationBlobCalibrationDetector.cs:67-71`](../../../src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs#L67-L71). Add the new param:

```csharp
var blobs = DeviationBlobDetector.DetectIconBlobs(
    dev, w, h, request.LowNcc, rim, request.BlobOptions, closeRadius: 1,
    hooks: request.Diagnostics,
    meanNcc: meanNcc,
    logger: _logger,
    deviationMask: request.DeviationMask);   // NEW
```

5. Run all detector tests:

```bash
dotnet test tests/Mithril.MapCalibration.Tests/ --filter "FullyQualifiedName~Detection"
```

Expected: all PASS, including the new `DeviationStepMaskTests`.

6. **Commit:**
```bash
git add src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs
git add src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs
git add src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs   # or wherever DetectionRequest lives
git add tests/Mithril.MapCalibration.Tests/Detection/DeviationStepMaskTests.cs
git commit -m "feat(map-calibration): plumb deviationMask through DetectionRequest → DetectIconBlobs (#1116)"
```

---

## Task 7 — `AttemptJson` v3→v4 + `AttemptFilesJson.DeviationMask` additive field

**Files:**
- Modify: [`src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptJsonV4Tests.cs` (new, or extend existing JSON round-trip test)

**Steps:**

1. **Write the failing round-trip test:**

```csharp
[Fact]
public void V3_AttemptJson_without_DeviationMask_reads_as_null()
{
    var json = """
    {
      "schemaVersion": 3,
      "area": "Map_Test",
      "attemptStartedUtc": "2026-06-12T00:00:00Z",
      "attemptFinalizedUtc": "2026-06-12T00:00:01Z",
      "outcome": "accepted",
      "rejectReason": null,
      "engineVersion": "3.0.0.X",
      "files": { /* no deviationMask */ }
    }
    """;
    var attempt = JsonSerializer.Deserialize<AttemptJson>(json, /* generator-context */);
    attempt!.Files.DeviationMask.Should().BeNull();
}

[Fact]
public void V4_AttemptJson_with_DeviationMask_round_trips()
{
    var attempt = new AttemptJson(
        SchemaVersion: 4, Area: "Map_Test",
        AttemptStartedUtc: "…", AttemptFinalizedUtc: "…",
        Outcome: "accepted", RejectReason: null, EngineVersion: "3.0.0.X",
        Files: new AttemptFilesJson(
            /* …existing fields all null… */,
            DeviationMask: "07a-deviation-mask.png"));
    var json = JsonSerializer.Serialize(attempt, /* generator-context */);
    json.Should().Contain("\"deviationMask\":\"07a-deviation-mask.png\"");
    var roundTrip = JsonSerializer.Deserialize<AttemptJson>(json, /* generator-context */);
    roundTrip!.Files.DeviationMask.Should().Be("07a-deviation-mask.png");
}
```

(Fill in the existing JSON-source-generator context — likely `CalibrationBundleJsonContext` or similar.)

2. **Add the field to `AttemptFilesJson`:**

Find the record at [`CalibrationBundleJson.cs:66`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs#L66) and append:

```csharp
public sealed record AttemptFilesJson(
    string? RawScreenshot,
    string? GrayScreenshot,
    /* …existing fields… */,
    /* mithril#1116: additive bundle artifact for the deviation-mask. Null on
       pre-#1116 bundles. */
    string? DeviationMask = null);
```

3. **Bump `AttemptJson.SchemaVersion` writes to 4** wherever the engine constructs the record (find with grep; likely in `AutoCalibrationEngine.cs` or `FilesystemCalibrationAttemptBundleSink.cs`):

```bash
grep -rn "new AttemptJson(" src/
```

For each write site that constructs a fresh `AttemptJson`, replace `SchemaVersion: 3` with `SchemaVersion: 4`. (Pre-#1116 readers handle the bump because the only additive change is on `Files.DeviationMask`, which they ignore.)

4. Run tests, expect PASS.

5. **Commit:**
```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs           # or wherever the writes are
git add tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/AttemptJsonV4Tests.cs
git commit -m "feat(map-calibration): AttemptJson v3→v4 with additive DeviationMask file ref (#1116)"
```

---

## Task 8 — `AutoCalibrationEngine` wiring + `07a-deviation-mask.png` write

**Files:**
- Modify: [`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)
- Modify: [`src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs)
- Modify: `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs` (DI registration)

**Steps:**

1. **DI registration.** In `DetectionServiceCollectionExtensions.cs`, add singleton registrations:

```csharp
services.AddSingleton<FloorBoundaryMaskCache>();
services.AddSingleton<FogOfWarDetector>();
// DeviationMaskCombiner is static — no registration needed.
```

(These services don't currently take a logger param explicitly; check the existing `CachedBaseTextureProvider` pattern — DI gives them `ILogger<T>` automatically.)

2. **In `AutoCalibrationEngine.cs`**, find where the detector is invoked (likely inside the `calibration.refine`/`calibration.solve` flow at lines 551+ / 706+ that we saw earlier). Before invoking the detector, build the mask:

```csharp
// mithril#1116: build the deviation mask before invoking the detector.
GrayImage? boundary = null;
GrayImage? fog      = null;
bool[]?    deviationMask = null;

if (_detectorOptions.DeviationMaskingEnabled)
{
    using var maskSpan = MithrilActivitySources.MapCalibration.StartActivity("calibration.detect.mask");
    maskSpan?.SetTag("area", areaKey);

    boundary = _boundaryMaskCache.GetOrCompute(areaKey);
    maskSpan?.SetTag("mask.boundary.available", boundary is not null);

    if (_detectorOptions.FogOfWarDetectionEnabled)
        fog = _fogDetector.Detect(screenshotInRoi);
    maskSpan?.SetTag("mask.fog.available", fog is not null);

    if (boundary is not null || fog is not null)
    {
        var combinedGray = DeviationMaskCombiner.Combine(boundary, fog, roiWidth, roiHeight);
        // Convert GrayImage → bool[] for the detector inner-loop fast path.
        deviationMask = new bool[combinedGray.Pixels.Length];
        for (int i = 0; i < deviationMask.Length; i++)
            deviationMask[i] = combinedGray.Pixels[i] != 0;
        int set = deviationMask.Count(b => b);
        maskSpan?.SetTag("mask.coverage", (double)set / deviationMask.Length);

        // Write the bundle artifact for the triager.
        _bundleSink.WriteMaskPng(attemptDir, combinedGray);
    }
}

var request = new DetectionRequest(
    /* …existing fields… */,
    DeviationMask: deviationMask);
```

3. **In `FilesystemCalibrationAttemptBundleSink.cs`**, add a method:

```csharp
public void WriteMaskPng(string attemptDir, GrayImage mask)
{
    var path = Path.Combine(attemptDir, "07a-deviation-mask.png");
    // Mirror the existing 07c-rim-mask.png write pattern. Use whatever PNG
    // encoder the existing path uses — likely System.Drawing or a thin helper.
    PngWriter.WriteGray(path, mask);   // OR reuse whatever helper exists
}
```

And update the `AttemptFilesJson` construction to populate `DeviationMask = "07a-deviation-mask.png"` when the mask was written.

4. Build the solution; run full test suite:

```bash
dotnet test Mithril.slnx
```

Expected: all tests still PASS. The engine wiring doesn't have a dedicated unit test (it's the integration point); Task 10's corpus tests + Task 11's manual smoke verify it end-to-end.

5. **Commit:**
```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs
git add src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs
git add src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): wire deviation mask through engine + write 07a artifact (#1116)"
```

---

## Task 9 — Telemetry doc + tests

**Files:**
- Modify: [`docs/perf-trace-schema.md`](../../perf-trace-schema.md)
- Modify: [`tests/Mithril.Shared.Tests/PerfTracerTests.cs`](../../../tests/Mithril.Shared.Tests/PerfTracerTests.cs)

**Steps:**

1. Add a row to the spans-catalog table near line 68 of `perf-trace-schema.md`:

```markdown
| `calibration.detect.mask` | Capture | `area`, `mask.boundary.available`, `mask.boundary.degenerate`, `mask.fog.available`, `mask.coverage` | mithril#1116 |
```

2. Add per-tag descriptions in the detailed tag-list section around line 325.

3. Update `PerfTracerTests.cs` byte-parity test — find the existing canonical-vocabulary array/dictionary and add the new span + tags.

4. Run:

```bash
dotnet test tests/Mithril.Shared.Tests/ --filter "FullyQualifiedName~PerfTracer"
```

Expected: PASS.

5. **Commit:**
```bash
git add docs/perf-trace-schema.md tests/Mithril.Shared.Tests/PerfTracerTests.cs
git commit -m "docs(perf-trace): add calibration.detect.mask span + tags (#1116)"
```

---

## Task 10 — Corpus tests (Hogan's 091533 + TopFloor 095806 + Eltibule)

**Files:**
- Test: `tests/Mithril.MapCalibration.Tests/Detection/BoundaryMaskCorpusTests.cs` (new)
- Test fixtures: `tests/Mithril.MapCalibration.Tests/Detection/boundary_mask_corpus/` (new)

**Steps:**

1. **Extract fixtures.** From the existing diagnostic bundles, copy:
   - `Map_HogansKeepBasement-…091533/{03-screenshot-gray.png, 05-base-texture-resampled.png, 06-aligned-screenshot.png}`
   - `Map_GoblinDungeon_TopFloor-…095806/{same three files}`
   - `Map_AreaEltibule-…101911/{same three files}` (outdoor regression lock)

   Each fixture also needs the texture's alpha file (from Task 0 of the sidecar plan). Copy `<bundle>/<map-texture-alpha>.png` extracted via the sidecar.

2. **Write the corpus tests:**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public class BoundaryMaskCorpusTests
{
    [Fact]
    public void Hogans_091533_mega_blob_area_drops_below_ceiling()
    {
        var (screenshot, gray, alpha) = LoadFixture("hogans_091533");
        var blobs = RunFullPipeline(screenshot, gray, alpha, masked: true);

        var structureBlobs = blobs.Where(b => b.BlobClass == BlobClass.Structure);
        var maxArea = structureBlobs.Any() ? structureBlobs.Max(b => b.Area) : 0;

        // Task 0-tuned: target ≤ 30,000 from baseline 119,655.
        maxArea.Should().BeLessOrEqualTo(30_000);
    }

    [Fact]
    public void Hogans_091533_icon_count_rises_above_floor()
    {
        var (screenshot, gray, alpha) = LoadFixture("hogans_091533");
        var blobs = RunFullPipeline(screenshot, gray, alpha, masked: true);

        var iconCount = blobs.Count(b => b.BlobClass == BlobClass.Icon);
        // Task 0-tuned: target ≥ 60 from baseline ~50.
        iconCount.Should().BeGreaterOrEqualTo(60);
    }

    [Fact]
    public void TopFloor_095806_no_fog_false_positives()
    {
        var (screenshot, gray, alpha) = LoadFixture("topfloor_095806");
        var opts = new MapCalibrationDetectorOptions { /* default */ };
        var detector = new FogOfWarDetector(opts);
        var fog = detector.Detect(screenshot);

        int total = fog.Pixels.Length;
        int marked = fog.Pixels.Count(p => p != 0);
        ((double)marked / total).Should().BeLessThan(0.05);  // < 5 % false positive
    }

    [Fact]
    public void Eltibule_outdoor_unmasked_unchanged()
    {
        var (screenshot, gray, alpha) = LoadFixture("eltibule");
        var blobsMasked   = RunFullPipeline(screenshot, gray, alpha, masked: true);
        var blobsUnmasked = RunFullPipeline(screenshot, gray, alpha, masked: false);

        // Outdoor scenes shouldn't see the mask affect their detection set
        // (boundary mask is built from texture alpha; fog detector runs but
        // shouldn't false-positive on a fully-explored outdoor map).
        Math.Abs(blobsMasked.Count - blobsUnmasked.Count).Should().BeLessOrEqualTo(2);
    }

    private (GrayImage screenshot, GrayImage texture, GrayImage alpha) LoadFixture(string name)
    {
        // Load PNGs from boundary_mask_corpus/<name>/ … decoder lives somewhere in the existing test infra.
    }

    private IReadOnlyList<BlobFeat> RunFullPipeline(
        GrayImage screenshot, GrayImage texture, GrayImage alpha, bool masked)
    {
        // Wire the full detector pipeline with optional masking.
        // …matches the engine's invocation shape from Task 8…
    }
}
```

(`BlobFeat` doesn't directly expose `BlobClass` in the prod code — it lives on the `BlobClassification` snapshot. Adjust the test queries to use the classification hook + collect classifications during the test run.)

3. Run:

```bash
dotnet test tests/Mithril.MapCalibration.Tests/ --filter "FullyQualifiedName~BoundaryMaskCorpus"
```

Expected: all 4 PASS (given Task 0's tuning lands the defaults right).

4. **Commit:**

```bash
git add tests/Mithril.MapCalibration.Tests/Detection/BoundaryMaskCorpusTests.cs
git add tests/Mithril.MapCalibration.Tests/Detection/boundary_mask_corpus/
git commit -m "test(map-calibration): corpus tests for deviation mask on Hogan's + TopFloor + Eltibule (#1116)"
```

---

## Task 11 — Manual smoke + wiki Findings update + #1116 close

**Files:**
- Wiki: [`I:\src\project-gorgon.wiki\Auto-Calibration-Sub-Zone-Findings.md`](../../../I:/src/project-gorgon.wiki/Auto-Calibration-Sub-Zone-Findings.md) (modify)
- GitHub: post comment on #1116

**Steps:**

1. **Manual smoke** on a live Mithril boot:
   - Launch Mithril against the user's PG install with the sidecar cache populated.
   - Trigger an auto-cal on Hogan's Basement (zoom to a scale similar to 0.776).
   - Open the produced bundle. Confirm `07a-deviation-mask.png` exists and visually traces wall-edge bands + fog-edges; floor interior is black (not masked).
   - Open `07b-foreground.png`. Confirm wall-edge bands are CLEARLY REDUCED vs the 2026-06-10 091533 baseline.
   - Open `09-projection-overlay.png`. Confirm green crosses land on or near visible NPC pips, not on fog.

2. **Update wiki Findings:**
   - In the "Mode A" section, append a 2026-06-XX entry: "Deviation-mask fix shipped via #YYYY. New bundle artifacts: 07a-deviation-mask.png. Mode-A signature confirmed eliminated on Hogan's 091533-equivalent live capture."
   - In the "Open questions" table, flip the "What's the precision target for #1070?" row from open to closed (mithril#1070 path didn't move the symptom; the deviation-mask did instead).

3. **Post on #1116:**

```
[ai-trailer: drafted by Claude (Opus 4.7), posted by @arthur-conde]

Mode-A path closed by the deviation-mask fix (PR #ZZZZ). Live smoke on Hogan's
Basement at scale 0.776 produced:
  - 07b-foreground.png: wall-edge bands eliminated; only per-icon dots remain
  - 09-projection-overlay.png: green crosses land on visible NPC pips
  - Synthesis-J: rose from ~3.25 to ≥ 8 (positive externality, see #1117)

Two sub-issues split off:
  - #YYYY: locator's ≤3 px Y-anisotropic-scale residual (not load-bearing for
    Mode A; tracked as a separate follow-up)
  - #ZZZZ: Mode-B solver-side rejection (sparse-reference / cross-scene-leak;
    Goblin Dungeon main 095904 territory; separate fix)

Closing as fixed.
```

4. **Commit (wiki):**

```bash
git -C "I:\src\project-gorgon.wiki" add Auto-Calibration-Sub-Zone-Findings.md
git -C "I:\src\project-gorgon.wiki" commit -m "Wiki: mode-A deviation-mask fix shipped (#1116)"
git -C "I:\src\project-gorgon.wiki" push
```

**Acceptance:**
- Live Mithril boot with all PR changes runs a clean Hogan's cal — green crosses land on pips.
- Wiki updated.
- #1116 closed.
- Two sub-issues filed for follow-ups (anisotropic-Y + Mode-B).

---

## Cross-references

- Prereq plan + spec: [`../sidecar-rgba-alpha-surface/`](../sidecar-rgba-alpha-surface/)
- Detector pipeline observability: [`../calibration-pipeline-observability-1123/`](../calibration-pipeline-observability-1123/)
- Locator fallback (this fix sits downstream of): [`../map-calibration-sparse-locate-fallback-1061/`](../map-calibration-sparse-locate-fallback-1061/)
- Sister umbrella spec (Mode-B): to-be-filed
