# calibration-1095 live-view detector — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split marker projection into a durable layer-1 (`world → texture_px` via the cal record, no zoom factor) and a lightweight layer-2 (`texture_px → overlay_px` via an on-demand screenshot × base-texture probe). Delete `CalibrationZoom` from all three carriers, the zoom slider, `SessionState.CurrentMapZoom`, and `IOverlayZoomSource`. End users never type or drag a zoom value.

**Architecture:** Two-phase shipping. Phase 1 builds the new infrastructure additively (no behavior change). Phase 2 is the cutover — drop `CalibrationZoom` everywhere, swap consumers to layer-1 + layer-2 composition, delete the slider, wire trigger sites. Each phase is one PR with one review at the end. Within a phase, tasks are sequenced so each commit compiles and existing tests stay green.

**Tech Stack:** .NET 10, C# latest, xUnit + FluentAssertions, WPF, MahApps Lucide icon pack. Direct2D rendering via `Vortice.Direct2D1`. Mithril module conventions per [CLAUDE.md](../../CLAUDE.md).

**Spec:** [spec.md](spec.md). Issue: [#1095](https://github.com/moumantai-gg/mithril/issues/1095).

**Review cadence:** One code-review per PR (Phase 1 PR → review → merge → Phase 2 PR → review → merge). No per-task reviews. Inside a phase, commits run TDD-fast but stay on the feature branch until the PR opens.

---

## Phase 1 PR — new infrastructure (additive)

PR-1 adds new types and a screen-capture seam. The slider, `IOverlayZoomSource`, and `CalibrationZoom` all keep working. Nothing in the runtime engine changes behavior. Detector + service are wired into DI but only invoked by tests until Phase 2.

**PR title:** `feat(calibration): live-view detector infrastructure (refs #1095)`

### Task P1.1: MapViewFix record struct

**Files:**
- Create: `src/Mithril.MapCalibration/MapViewFix.cs`
- Test: `tests/Mithril.MapCalibration.Tests/MapViewFixTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Mithril.MapCalibration.Tests/MapViewFixTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class MapViewFixTests
{
    [Fact]
    public void Construction_RoundTripsAllFields()
    {
        var t = new DateTimeOffset(2026, 6, 6, 12, 4, 32, TimeSpan.Zero);
        var fix = new MapViewFix(
            PanTexPxX: 100.5, PanTexPxY: 200.25,
            ViewScale: 0.65,
            Confidence: 0.92,
            MeasuredAt: t);

        fix.PanTexPxX.Should().Be(100.5);
        fix.PanTexPxY.Should().Be(200.25);
        fix.ViewScale.Should().Be(0.65);
        fix.Confidence.Should().Be(0.92);
        fix.MeasuredAt.Should().Be(t);
    }

    [Fact]
    public void TextureToOverlay_AppliesPanAndScale()
    {
        var fix = new MapViewFix(
            PanTexPxX: 100, PanTexPxY: 50,
            ViewScale: 2.0,
            Confidence: 1.0,
            MeasuredAt: DateTimeOffset.UnixEpoch);

        // texture pixel (150, 75) is offset (50, 25) from pan, scaled 2× = overlay (100, 50)
        var (ox, oy) = fix.TextureToOverlay(150, 75);

        ox.Should().Be(100);
        oy.Should().Be(50);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapViewFixTests" --no-restore`
Expected: build failure (type doesn't exist).

- [ ] **Step 3: Implement MapViewFix**

```csharp
// src/Mithril.MapCalibration/MapViewFix.cs
namespace Mithril.MapCalibration;

/// <summary>
/// A measurement of PG's live world-map view state at a moment in time: where
/// the visible region sits in base-texture-pixel coordinates and how many
/// overlay pixels cover one texture pixel. Produced by an <see cref="Detection.IMapViewProbe"/>;
/// consumed by the layer-2 composition that maps a Texture-frame projection
/// to live overlay pixels. Ephemeral — the user never sees this; it lives
/// in memory and replaces the deleted manual zoom slider.
///
/// <para>See <c>docs/planning/calibration-1095-live-view-detector/spec.md</c>
/// §4.2 for the two-layer projection model.</para>
/// </summary>
public readonly record struct MapViewFix(
    double PanTexPxX,
    double PanTexPxY,
    double ViewScale,
    double Confidence,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Compose a Texture-frame pixel with this fix to produce live
    /// overlay-pixel coordinates: <c>(tex − pan) × viewScale</c>.</summary>
    public (double X, double Y) TextureToOverlay(double texPxX, double texPxY)
        => ((texPxX - PanTexPxX) * ViewScale, (texPxY - PanTexPxY) * ViewScale);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapViewFixTests" --no-restore`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/MapViewFix.cs tests/Mithril.MapCalibration.Tests/MapViewFixTests.cs
git commit -m "feat(calibration): add MapViewFix record struct (refs #1095)"
```

---

### Task P1.2: IMapViewProbe interface

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/IMapViewProbe.cs`

- [ ] **Step 1: Define the interface (no test — pure contract)**

```csharp
// src/Mithril.MapCalibration.Detection/IMapViewProbe.cs
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Cross-correlates a current overlay screenshot against the cached base
/// texture for an area to produce a <see cref="MapViewFix"/> describing
/// PG's live world-map view state (pan + scale). The mechanism the
/// runtime engine uses to "ground the current view" so the durable
/// layer-1 cal (which projects to texture pixels) can be composed into
/// live overlay pixels — see <c>spec.md</c> §4.2.
///
/// <para><b>Fail-soft:</b> returns <c>null</c> when (a) the base texture is
/// missing, (b) the screenshot doesn't show enough of the map (UI overlay,
/// no map open), (c) the correlation peak fails the confidence gate, or
/// (d) the capture itself fails. Producers refuse to render rather than
/// rendering through a guessed layer-2.</para>
///
/// <para><b>Cost target:</b> sub-1s per call. Implementations should
/// coarse-to-fine and bound search ranges; callers invoke on a background
/// thread and marshal back to the UI thread.</para>
/// </summary>
public interface IMapViewProbe
{
    /// <summary>
    /// Probe for the current view state by correlating <paramref name="screenshot"/>
    /// against <paramref name="baseTexture"/>. Returns the measured fix, or
    /// <c>null</c> if no acceptable peak emerged.
    /// </summary>
    MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture);
}
```

- [ ] **Step 2: Build to confirm the interface compiles**

Run: `dotnet build src/Mithril.MapCalibration.Detection --no-restore`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/IMapViewProbe.cs
git commit -m "feat(calibration): add IMapViewProbe contract (refs #1095)"
```

---

### Task P1.3: CrossCorrelationMapViewProbe implementation

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/CrossCorrelationMapViewProbe.cs`
- Create: `src/Mithril.MapCalibration.Detection/Internal/Fft2D.cs` (Cooley–Tukey 2-D FFT helper)
- Test: `tests/Mithril.MapCalibration.Tests/CrossCorrelationMapViewProbeTests.cs`

Algorithm: coarse-to-fine cross-correlation. (1) at each of ~8 geometrically-spaced candidate scales over `[0.25, 4.0]`, downsample the screenshot to the scale, FFT-cross-correlate against the base texture, record peak `(pan, score)`. (2) Golden-section refine over a narrow scale window around the best coarse candidate. (3) Confidence gate: `peakScore > absoluteThreshold` AND `peakScore / secondPeakScore > ratioThreshold`. Else return null.

For the FFT helper, ship a basic iterative Cooley–Tukey 2-D FFT for power-of-two sizes (pad to next power of two). Sources of inspiration in the existing codebase: `Mithril.MapCalibration.Detection/ImageOps.cs` (pixel primitives), `DeviationBlobDetector.cs` (matching strategy). Do NOT pull in a third-party FFT library — keep the dependency surface flat.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Mithril.MapCalibration.Tests/CrossCorrelationMapViewProbeTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class CrossCorrelationMapViewProbeTests
{
    [Fact]
    public void IdenticalScreenshot_ReturnsZeroPanAndUnitScale()
    {
        var texture = MakeStripedGray(256, 256);
        var screenshot = MakeStripedGray(256, 256);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.PanTexPxX.Should().BeApproximately(0, 1.0);
        fix.Value.PanTexPxY.Should().BeApproximately(0, 1.0);
        fix.Value.ViewScale.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void PannedScreenshot_ReturnsExpectedPan()
    {
        var texture = MakeStripedGray(256, 256);
        var screenshot = CropShifted(texture, 64, 32, 128, 128);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.PanTexPxX.Should().BeApproximately(64, 2.0);
        fix.Value.PanTexPxY.Should().BeApproximately(32, 2.0);
        fix.Value.ViewScale.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void ScaledScreenshot_ReturnsExpectedScale()
    {
        var texture = MakeStripedGray(256, 256);
        // 128×128 screenshot = texture rendered at 0.5× (viewScale)
        var screenshot = DownsampleHalf(texture);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(screenshot, texture);

        fix.Should().NotBeNull();
        fix!.Value.ViewScale.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void NoiseScreenshot_ReturnsNull()
    {
        var texture = MakeStripedGray(256, 256);
        var noise = MakeRandomGray(256, 256, seed: 42);
        var probe = new CrossCorrelationMapViewProbe();

        var fix = probe.TryProbe(noise, texture);

        fix.Should().BeNull();
    }

    private static GrayImage MakeStripedGray(int w, int h)
    {
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = (byte)((x ^ y) & 0xFF);
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage CropShifted(GrayImage src, int dx, int dy, int w, int h)
    {
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = (x + dx) % src.Width;
                int sy = (y + dy) % src.Height;
                pixels[y * w + x] = src.Pixels[sy * src.Width + sx];
            }
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage DownsampleHalf(GrayImage src)
    {
        int w = src.Width / 2, h = src.Height / 2;
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int a = src.Pixels[(2 * y) * src.Width + 2 * x];
                int b = src.Pixels[(2 * y) * src.Width + 2 * x + 1];
                int c = src.Pixels[(2 * y + 1) * src.Width + 2 * x];
                int d = src.Pixels[(2 * y + 1) * src.Width + 2 * x + 1];
                pixels[y * w + x] = (byte)((a + b + c + d) / 4);
            }
        return new GrayImage(w, h, pixels);
    }

    private static GrayImage MakeRandomGray(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[w * h];
        rng.NextBytes(pixels);
        return new GrayImage(w, h, pixels);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CrossCorrelationMapViewProbeTests" --no-restore`
Expected: build failure (type doesn't exist).

- [ ] **Step 3: Implement the FFT helper**

```csharp
// src/Mithril.MapCalibration.Detection/Internal/Fft2D.cs
using System.Numerics;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Iterative Cooley–Tukey 2-D FFT over power-of-two complex grids. BCL-only;
/// keeps the calibration library's dependency surface flat. Used by
/// <see cref="CrossCorrelationMapViewProbe"/> for FFT-accelerated
/// cross-correlation (phase-correlation via element-wise spectrum product).
/// </summary>
internal static class Fft2D
{
    /// <summary>Round <paramref name="n"/> up to the next power of two (≥ 1).</summary>
    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>In-place forward FFT over a <paramref name="rows"/>×<paramref name="cols"/>
    /// complex grid (both dimensions must be powers of two).</summary>
    public static void Forward(Complex[] grid, int rows, int cols)
        => Transform(grid, rows, cols, inverse: false);

    /// <summary>In-place inverse FFT. Normalises by 1/(rows*cols).</summary>
    public static void Inverse(Complex[] grid, int rows, int cols)
    {
        Transform(grid, rows, cols, inverse: true);
        double inv = 1.0 / (rows * cols);
        for (int i = 0; i < grid.Length; i++) grid[i] *= inv;
    }

    private static void Transform(Complex[] grid, int rows, int cols, bool inverse)
    {
        // Row-wise then column-wise 1-D FFTs.
        var rowBuf = new Complex[cols];
        for (int r = 0; r < rows; r++)
        {
            Array.Copy(grid, r * cols, rowBuf, 0, cols);
            Fft1D(rowBuf, inverse);
            Array.Copy(rowBuf, 0, grid, r * cols, cols);
        }
        var colBuf = new Complex[rows];
        for (int c = 0; c < cols; c++)
        {
            for (int r = 0; r < rows; r++) colBuf[r] = grid[r * cols + c];
            Fft1D(colBuf, inverse);
            for (int r = 0; r < rows; r++) grid[r * cols + c] = colBuf[r];
        }
    }

    private static void Fft1D(Complex[] x, bool inverse)
    {
        int n = x.Length;
        // Bit-reverse permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (x[i], x[j]) = (x[j], x[i]);
        }
        // Cooley–Tukey butterflies.
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = (inverse ? 2 : -2) * Math.PI / len;
            var wLen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = x[i + k];
                    var v = x[i + k + len / 2] * w;
                    x[i + k] = u + v;
                    x[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
```

- [ ] **Step 4: Implement CrossCorrelationMapViewProbe**

```csharp
// src/Mithril.MapCalibration.Detection/CrossCorrelationMapViewProbe.cs
using System.Numerics;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// FFT-accelerated cross-correlation probe. Searches over view-scale
/// candidates and picks the (pan, scale) maximising the correlation peak
/// of screenshot against base texture. Rotation/mirror are held by the
/// caller's cal record — PG's world-map view doesn't independently rotate.
/// </summary>
public sealed class CrossCorrelationMapViewProbe : IMapViewProbe
{
    private const double MinScale = 0.25;
    private const double MaxScale = 4.0;
    private const int CoarseScaleCount = 8;
    private const double AbsoluteThreshold = 0.30;   // tune from golden tests
    private const double RatioThreshold = 1.25;      // peak / 2nd-peak

    public MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture)
    {
        if (screenshot is null || baseTexture is null) return null;
        if (screenshot.Width < 8 || screenshot.Height < 8) return null;
        if (baseTexture.Width < 8 || baseTexture.Height < 8) return null;

        var coarse = ScaleSweepCoarse(screenshot, baseTexture);
        if (coarse is null) return null;

        var refined = GoldenSectionRefine(screenshot, baseTexture, coarse.Value);

        var fix = refined ?? coarse.Value;
        if (!PassesConfidenceGate(fix)) return null;

        return new MapViewFix(
            PanTexPxX: fix.PanX,
            PanTexPxY: fix.PanY,
            ViewScale: fix.Scale,
            Confidence: fix.PeakScore,
            MeasuredAt: DateTimeOffset.UtcNow);
    }

    private readonly record struct ScaleCandidate(double Scale, double PanX, double PanY, double PeakScore, double SecondPeakScore);

    private ScaleCandidate? ScaleSweepCoarse(GrayImage screenshot, GrayImage baseTexture)
    {
        ScaleCandidate? best = null;
        for (int i = 0; i < CoarseScaleCount; i++)
        {
            double t = (double)i / (CoarseScaleCount - 1);
            double s = MinScale * Math.Pow(MaxScale / MinScale, t);
            var candidate = EvaluateAtScale(screenshot, baseTexture, s);
            if (candidate is { } c && (best is null || c.PeakScore > best.Value.PeakScore))
                best = c;
        }
        return best;
    }

    private ScaleCandidate? GoldenSectionRefine(GrayImage screenshot, GrayImage baseTexture, ScaleCandidate seed)
    {
        // Narrow window: ± one coarse step in log-scale around the seed.
        double step = Math.Pow(MaxScale / MinScale, 1.0 / (CoarseScaleCount - 1));
        double lo = seed.Scale / step;
        double hi = seed.Scale * step;
        const double phi = 0.61803398875;
        double a = lo, b = hi;
        ScaleCandidate? best = seed;
        for (int i = 0; i < 6; i++)  // 6 iterations narrows the bracket ~12×
        {
            double c = b - phi * (b - a);
            double d = a + phi * (b - a);
            var fc = EvaluateAtScale(screenshot, baseTexture, c);
            var fd = EvaluateAtScale(screenshot, baseTexture, d);
            if (fc?.PeakScore >= fd?.PeakScore)
            {
                b = d;
                if (fc is { } x && x.PeakScore > best!.Value.PeakScore) best = x;
            }
            else
            {
                a = c;
                if (fd is { } x && x.PeakScore > best!.Value.PeakScore) best = x;
            }
        }
        return best;
    }

    private static ScaleCandidate? EvaluateAtScale(GrayImage screenshot, GrayImage baseTexture, double scale)
    {
        // Downsample the screenshot to its appearance "at canonical": each
        // overlay pixel covers `scale` texture pixels, so the equivalent
        // texture-frame patch is screenshot scaled by 1/scale.
        var resampled = Resample(screenshot, scale);
        if (resampled.Width >= baseTexture.Width || resampled.Height >= baseTexture.Height)
            return null;  // screenshot patch must fit inside the base texture

        var corr = FftCrossCorrelate(resampled, baseTexture);
        var (peakX, peakY, peakScore, secondPeak) = FindTopTwoPeaks(corr, resampled.Width, resampled.Height,
            corrW: baseTexture.Width, corrH: baseTexture.Height);

        return new ScaleCandidate(
            Scale: scale,
            PanX: peakX,
            PanY: peakY,
            PeakScore: peakScore,
            SecondPeakScore: secondPeak);
    }

    private static GrayImage Resample(GrayImage src, double scaleInv)
    {
        // Resample src so that one src pixel becomes 1/scaleInv texture-pixels.
        // If scaleInv > 1, src shrinks; if < 1, src grows.
        int w = Math.Max(8, (int)Math.Round(src.Width / scaleInv));
        int h = Math.Max(8, (int)Math.Round(src.Height / scaleInv));
        var pixels = new byte[w * h];
        // Nearest-neighbour resample — adequate for the coarse pass; the
        // refine stage doesn't need sub-pixel accuracy because the FFT peak
        // localises pan to ~1 texel by itself.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(src.Width - 1, (int)(x * scaleInv));
                int sy = Math.Min(src.Height - 1, (int)(y * scaleInv));
                pixels[y * w + x] = src.Pixels[sy * src.Width + sx];
            }
        return new GrayImage(w, h, pixels);
    }

    private static double[] FftCrossCorrelate(GrayImage patch, GrayImage texture)
    {
        // Zero-pad both to a common pow-2 grid (next pow2 of max dims).
        int n = Fft2D.NextPow2(Math.Max(patch.Width, texture.Width));
        int m = Fft2D.NextPow2(Math.Max(patch.Height, texture.Height));

        var f = ToComplexGrid(patch, n, m);
        var g = ToComplexGrid(texture, n, m);

        Fft2D.Forward(f, m, n);
        Fft2D.Forward(g, m, n);

        // Cross-correlation = ifft(conj(F) * G).
        for (int i = 0; i < f.Length; i++) f[i] = Complex.Conjugate(f[i]) * g[i];

        Fft2D.Inverse(f, m, n);

        var corr = new double[f.Length];
        for (int i = 0; i < f.Length; i++) corr[i] = f[i].Real;
        return corr;
    }

    private static Complex[] ToComplexGrid(GrayImage img, int n, int m)
    {
        var g = new Complex[n * m];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                g[y * n + x] = new Complex(img.Pixels[y * img.Width + x] / 255.0, 0);
        return g;
    }

    private static (double X, double Y, double Peak, double SecondPeak) FindTopTwoPeaks(
        double[] corr, int patchW, int patchH, int corrW, int corrH)
    {
        // Valid pan range: [0, corrW − patchW] × [0, corrH − patchH] — anything
        // outside spills the patch off the base texture.
        int n = Fft2D.NextPow2(Math.Max(patchW, corrW));
        double peak = double.NegativeInfinity, second = double.NegativeInfinity;
        int peakX = 0, peakY = 0;
        int maxX = corrW - patchW, maxY = corrH - patchH;
        for (int y = 0; y <= maxY; y++)
            for (int x = 0; x <= maxX; x++)
            {
                double v = corr[y * n + x];
                if (v > peak)
                {
                    second = peak;
                    peak = v;
                    peakX = x; peakY = y;
                }
                else if (v > second)
                {
                    second = v;
                }
            }
        return (peakX, peakY, peak, second);
    }

    private static bool PassesConfidenceGate(ScaleCandidate c)
    {
        if (c.PeakScore < AbsoluteThreshold) return false;
        if (c.SecondPeakScore <= 0) return c.PeakScore > AbsoluteThreshold;
        return (c.PeakScore / c.SecondPeakScore) > RatioThreshold;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CrossCorrelationMapViewProbeTests" --no-restore`
Expected: PASS (4 tests). If the thresholds need tuning, adjust `AbsoluteThreshold` / `RatioThreshold` constants until the noise-rejection test passes AND the three positive tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/CrossCorrelationMapViewProbe.cs src/Mithril.MapCalibration.Detection/Internal/Fft2D.cs tests/Mithril.MapCalibration.Tests/CrossCorrelationMapViewProbeTests.cs
git commit -m "feat(calibration): FFT cross-correlation MapViewProbe (refs #1095)"
```

---

### Task P1.4: IOverlayCaptureSource interface

**Files:**
- Create: `src/Mithril.Overlay/IOverlayCaptureSource.cs`

- [ ] **Step 1: Define the interface**

```csharp
// src/Mithril.Overlay/IOverlayCaptureSource.cs
using Mithril.MapCalibration.Detection;

namespace Mithril.Overlay;

/// <summary>
/// Captures the live pixel content of the overlay region (the area on
/// screen the user has overlaid on PG's world map) as a single-channel
/// <see cref="GrayImage"/> for consumption by
/// <see cref="IMapViewProbe.TryProbe"/>. The seam between the platform-side
/// overlay window machinery (which owns the screen-capture surface) and
/// the calibration library (which is platform-free) — see
/// <c>spec.md</c> §4.4.
///
/// <para><b>Fail-soft:</b> returns <c>null</c> if the overlay isn't visible
/// or the capture itself fails; the probe propagates that as a null fix
/// and the status badge surfaces the cause.</para>
/// </summary>
public interface IOverlayCaptureSource
{
    /// <summary>Capture the current overlay region as gray pixels, or
    /// <c>null</c> if the overlay isn't capturable right now.</summary>
    GrayImage? Capture();
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/Mithril.Overlay --no-restore`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.Overlay/IOverlayCaptureSource.cs
git commit -m "feat(overlay): IOverlayCaptureSource contract (refs #1095)"
```

---

### Task P1.5: OverlayWindowCaptureSource implementation

**Files:**
- Create: `src/Mithril.Overlay/Internal/OverlayWindowCaptureSource.cs`
- Test: `tests/Mithril.Overlay.Tests/OverlayWindowCaptureSourceTests.cs`

This task captures pixels from the shared overlay window. The implementation uses `System.Windows.Media.Imaging.RenderTargetBitmap` to snapshot the live overlay backdrop and the underlying screen region behind it. Look at the existing `OverlayWindowService` ([OverlayWindowService.cs](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs)) for how it accesses the shared window; capture goes through the same `_window` reference. Mind [`docs/wpf-gotchas.md`](../../wpf-gotchas.md) — particularly hit-testing + virtualization sections for the screenshot path.

The simplest correct implementation: ask the OS for the screen region under the overlay window via `System.Drawing.Graphics.CopyFromScreen`, convert to gray pixels. This bypasses WPF rendering entirely — what we want, since we're capturing PG's pixels underneath the overlay.

- [ ] **Step 1: Write a unit test that exercises capture failure when no window is registered**

```csharp
// tests/Mithril.Overlay.Tests/OverlayWindowCaptureSourceTests.cs
using FluentAssertions;
using Mithril.Overlay;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

public sealed class OverlayWindowCaptureSourceTests
{
    [Fact]
    public void Capture_WithoutRegisteredWindow_ReturnsNull()
    {
        var source = new OverlayWindowCaptureSource(windowAccessor: () => null);

        source.Capture().Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~OverlayWindowCaptureSourceTests" --no-restore`
Expected: build failure (type doesn't exist).

- [ ] **Step 3: Implement OverlayWindowCaptureSource**

```csharp
// src/Mithril.Overlay/Internal/OverlayWindowCaptureSource.cs
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Captures the screen region under the shared overlay window into a
/// single-channel <see cref="GrayImage"/>. Uses <c>Graphics.CopyFromScreen</c>
/// against the overlay window's screen rect — we want PG's pixels, not the
/// transparent overlay layer's. The overlay window is excluded from capture
/// via <c>WDA_EXCLUDEFROMCAPTURE</c> on construction (memory:
/// <c>wda_excludefromcapture_canonical_for_overlay_chrome.md</c>), so this
/// reads PG's underlying map view directly.
///
/// <para>Constructor takes a window accessor (a no-arg <c>Func</c>) so the
/// type stays testable: production wires it to read the live overlay window
/// from <see cref="OverlayWindowService"/>; tests pass a fake.</para>
/// </summary>
internal sealed class OverlayWindowCaptureSource : IOverlayCaptureSource
{
    private readonly Func<Window?> _windowAccessor;
    private readonly ILogger? _logger;

    public OverlayWindowCaptureSource(Func<Window?> windowAccessor, ILogger? logger = null)
    {
        _windowAccessor = windowAccessor;
        _logger = logger;
    }

    public GrayImage? Capture()
    {
        var window = _windowAccessor();
        if (window is null) return null;

        try
        {
            int x = (int)window.Left, y = (int)window.Top;
            int w = (int)window.Width, h = (int)window.Height;
            if (w <= 0 || h <= 0) return null;

            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var stride = data.Stride;
                var pixels = new byte[w * h];
                unsafe
                {
                    var src = (byte*)data.Scan0;
                    for (int row = 0; row < h; row++)
                        for (int col = 0; col < w; col++)
                        {
                            byte b = src[row * stride + col * 3];
                            byte gch = src[row * stride + col * 3 + 1];
                            byte r = src[row * stride + col * 3 + 2];
                            // Rec. 601 luma — adequate for correlation against
                            // a gray-decoded base texture.
                            pixels[row * w + col] = (byte)((r * 299 + gch * 587 + b * 114) / 1000);
                        }
                }
                return new GrayImage(w, h, pixels);
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Overlay capture failed — safe-degrade to null fix.");
            return null;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~OverlayWindowCaptureSourceTests" --no-restore`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Overlay/Internal/OverlayWindowCaptureSource.cs tests/Mithril.Overlay.Tests/OverlayWindowCaptureSourceTests.cs
git commit -m "feat(overlay): OverlayWindowCaptureSource impl (refs #1095)"
```

---

### Task P1.6: ILiveMapViewService interface

**Files:**
- Create: `src/Mithril.MapCalibration/ILiveMapViewService.cs`

- [ ] **Step 1: Define the interface**

```csharp
// src/Mithril.MapCalibration/ILiveMapViewService.cs
namespace Mithril.MapCalibration;

/// <summary>
/// Holds the current <see cref="MapViewFix"/> per area and the trigger
/// for refreshing it on user gestures (toggle validation, enable motherlode
/// overlay, enable survey overlay, manual re-detect hotkey). Replaces the
/// deleted manual zoom slider as the source-of-truth for live view state.
///
/// <para><b>Threading.</b> <see cref="RefreshAsync"/> runs the probe on a
/// background thread; <see cref="Changed"/> is raised on the UI thread.
/// Concurrent <see cref="RefreshAsync"/> calls for the same area are
/// deduped — the second caller awaits the in-flight probe.</para>
///
/// <para><b>Fail-soft.</b> When the probe returns null, the prior fix
/// stays in place (markers keep rendering from the last good measurement);
/// the UI separately surfaces the failure status. When no fix has ever
/// been measured for an area, <see cref="GetCurrent"/> returns null and
/// consumers refuse to render.</para>
/// </summary>
public interface ILiveMapViewService
{
    /// <summary>The most recently measured fix for the area, or null if no
    /// measurement has ever succeeded for it.</summary>
    MapViewFix? GetCurrent(string mapAssetKey);

    /// <summary>The status of the most recent probe attempt for the area.</summary>
    LiveMapViewStatus GetStatus(string mapAssetKey);

    /// <summary>Trigger a fresh probe for the area. Concurrent calls for
    /// the same area dedupe to one in-flight probe.</summary>
    Task RefreshAsync(string mapAssetKey, CancellationToken ct = default);

    /// <summary>Raised on the UI thread after <see cref="RefreshAsync"/>
    /// completes (success or failure).</summary>
    event Action<string>? Changed;
}

public enum LiveMapViewStatus
{
    NeverMeasured,
    Detecting,
    Detected,
    FailedNoBaseTexture,
    FailedNoCapture,
    FailedLowConfidence,
}
```

- [ ] **Step 2: Build to confirm**

Run: `dotnet build src/Mithril.MapCalibration --no-restore`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/ILiveMapViewService.cs
git commit -m "feat(calibration): ILiveMapViewService + LiveMapViewStatus contract (refs #1095)"
```

---

### Task P1.7: LiveMapViewService implementation

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/LiveMapViewService.cs`
- Test: `tests/Mithril.MapCalibration.Tests/LiveMapViewServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Mithril.MapCalibration.Tests/LiveMapViewServiceTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Internal;
using Mithril.Overlay;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class LiveMapViewServiceTests
{
    [Fact]
    public async Task GetCurrent_NeverMeasured_ReturnsNull()
    {
        var svc = NewServiceWith(probe: _ => null);

        svc.GetCurrent("Map_AreaSerbule").Should().BeNull();
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.NeverMeasured);
    }

    [Fact]
    public async Task RefreshAsync_SuccessfulProbe_StoresFixAndRaisesChanged()
    {
        var fix = new MapViewFix(10, 20, 1.0, 0.9, DateTimeOffset.UnixEpoch);
        var raised = new List<string>();
        var svc = NewServiceWith(probe: _ => fix);
        svc.Changed += area => raised.Add(area);

        await svc.RefreshAsync("Map_AreaSerbule");

        svc.GetCurrent("Map_AreaSerbule").Should().Be(fix);
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.Detected);
        raised.Should().ContainSingle().Which.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public async Task RefreshAsync_FailedProbe_PreservesPriorFixAndSetsFailureStatus()
    {
        var fix = new MapViewFix(10, 20, 1.0, 0.9, DateTimeOffset.UnixEpoch);
        int callCount = 0;
        var svc = NewServiceWith(probe: _ =>
        {
            callCount++;
            return callCount == 1 ? fix : null;  // first OK, second fails
        });

        await svc.RefreshAsync("Map_AreaSerbule");
        await svc.RefreshAsync("Map_AreaSerbule");

        svc.GetCurrent("Map_AreaSerbule").Should().Be(fix);  // prior preserved
        svc.GetStatus("Map_AreaSerbule").Should().Be(LiveMapViewStatus.FailedLowConfidence);
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCallsForSameArea_DedupeToOneProbe()
    {
        int callCount = 0;
        var gate = new TaskCompletionSource();
        var svc = NewServiceWith(probe: _ =>
        {
            Interlocked.Increment(ref callCount);
            gate.Task.Wait();
            return new MapViewFix(0, 0, 1, 1, DateTimeOffset.UnixEpoch);
        });

        var t1 = svc.RefreshAsync("Map_AreaSerbule");
        var t2 = svc.RefreshAsync("Map_AreaSerbule");
        gate.SetResult();
        await Task.WhenAll(t1, t2);

        callCount.Should().Be(1);
    }

    private static LiveMapViewService NewServiceWith(Func<string, MapViewFix?> probe)
    {
        var probeAdapter = new TestProbe(probe);
        var capture = new TestCapture();
        var textures = new TestBaseTextureProvider();
        return new LiveMapViewService(probeAdapter, capture, textures, uiSynchronizer: a => a());
    }

    private sealed class TestProbe : IMapViewProbe
    {
        private readonly Func<string, MapViewFix?> _impl;
        public TestProbe(Func<string, MapViewFix?> impl) { _impl = impl; }
        public MapViewFix? TryProbe(GrayImage screenshot, GrayImage baseTexture) => _impl("ignored");
    }

    private sealed class TestCapture : IOverlayCaptureSource
    {
        public GrayImage? Capture() => new GrayImage(8, 8, new byte[64]);
    }

    private sealed class TestBaseTextureProvider : IBaseTextureProvider
    {
        public GrayImage? TryGetBaseTexture(string mapAssetKey) => new GrayImage(16, 16, new byte[256]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LiveMapViewServiceTests" --no-restore`
Expected: build failure (type doesn't exist).

- [ ] **Step 3: Implement LiveMapViewService**

```csharp
// src/Mithril.MapCalibration/Internal/LiveMapViewService.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Per-area <see cref="MapViewFix"/> holder + refresh orchestrator. See
/// <see cref="ILiveMapViewService"/> for the contract.
/// </summary>
public sealed class LiveMapViewService : ILiveMapViewService
{
    private readonly IMapViewProbe _probe;
    private readonly IOverlayCaptureSource _capture;
    private readonly IBaseTextureProvider _textures;
    private readonly Action<Action> _uiSynchronizer;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, MapViewFix> _fixes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveMapViewStatus> _status = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);

    public event Action<string>? Changed;

    public LiveMapViewService(
        IMapViewProbe probe,
        IOverlayCaptureSource capture,
        IBaseTextureProvider textures,
        Action<Action> uiSynchronizer,
        ILogger<LiveMapViewService>? logger = null)
    {
        _probe = probe;
        _capture = capture;
        _textures = textures;
        _uiSynchronizer = uiSynchronizer;
        _logger = logger;
    }

    public MapViewFix? GetCurrent(string mapAssetKey)
        => _fixes.TryGetValue(mapAssetKey, out var f) ? f : null;

    public LiveMapViewStatus GetStatus(string mapAssetKey)
        => _status.TryGetValue(mapAssetKey, out var s) ? s : LiveMapViewStatus.NeverMeasured;

    public Task RefreshAsync(string mapAssetKey, CancellationToken ct = default)
        => _inflight.GetOrAdd(mapAssetKey, key => RunProbe(key, ct))
            .ContinueWith(_ => _inflight.TryRemove(mapAssetKey, out _), ct, TaskContinuationOptions.None, TaskScheduler.Default);

    private async Task RunProbe(string mapAssetKey, CancellationToken ct)
    {
        _status[mapAssetKey] = LiveMapViewStatus.Detecting;
        RaiseChanged(mapAssetKey);

        var status = LiveMapViewStatus.FailedLowConfidence;
        MapViewFix? fix = null;
        await Task.Run(() =>
        {
            var screenshot = _capture.Capture();
            if (screenshot is null) { status = LiveMapViewStatus.FailedNoCapture; return; }

            var baseTex = _textures.TryGetBaseTexture(mapAssetKey);
            if (baseTex is null) { status = LiveMapViewStatus.FailedNoBaseTexture; return; }

            fix = _probe.TryProbe(screenshot, baseTex);
            status = fix.HasValue ? LiveMapViewStatus.Detected : LiveMapViewStatus.FailedLowConfidence;
        }, ct).ConfigureAwait(false);

        if (fix.HasValue) _fixes[mapAssetKey] = fix.Value;
        _status[mapAssetKey] = status;
        RaiseChanged(mapAssetKey);
    }

    private void RaiseChanged(string area)
        => _uiSynchronizer(() => Changed?.Invoke(area));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LiveMapViewServiceTests" --no-restore`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/LiveMapViewService.cs tests/Mithril.MapCalibration.Tests/LiveMapViewServiceTests.cs
git commit -m "feat(calibration): LiveMapViewService impl + tests (refs #1095)"
```

---

### Task P1.8: DI wiring for new infrastructure

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs`
- Modify: `src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs`
- Create: `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs` (if absent — check first)

Register the new types so they're constructible. Don't replace `IOverlayZoomSource` yet — that's Phase 2.

- [ ] **Step 1: Inspect existing DI extension files**

Run: `cat "src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs"`
Run: `cat "src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs"`
Run: `ls "src/Mithril.MapCalibration/DependencyInjection/" 2>&1 || echo "no DI folder"`

- [ ] **Step 2: Add IMapViewProbe registration**

Edit `DetectionServiceCollectionExtensions.cs`, inside the `AddCalibrationDetection` method (or its equivalent — the exact method name is in the file), add:

```csharp
services.TryAddSingleton<IMapViewProbe, CrossCorrelationMapViewProbe>();
```

- [ ] **Step 3: Add IOverlayCaptureSource + ILiveMapViewService registration**

Edit `OverlayServiceCollectionExtensions.cs`, inside `AddMithrilOverlay`, add (next to the existing `IOverlayZoomSource` registration, which stays for now):

```csharp
services.TryAddSingleton<IOverlayCaptureSource>(sp =>
{
    var overlay = sp.GetRequiredService<Mithril.Overlay.Internal.OverlayWindowService>();
    var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.Overlay.Capture");
    return new Mithril.Overlay.Internal.OverlayWindowCaptureSource(
        windowAccessor: () => overlay.Window,
        logger: logger);
});

services.TryAddSingleton<ILiveMapViewService>(sp =>
{
    var probe = sp.GetRequiredService<IMapViewProbe>();
    var capture = sp.GetRequiredService<IOverlayCaptureSource>();
    var textures = sp.GetRequiredService<IBaseTextureProvider>();
    var dispatcher = System.Windows.Application.Current?.Dispatcher;
    Action<Action> ui = dispatcher is null
        ? a => a()
        : a => dispatcher.Invoke(a);
    var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<Mithril.MapCalibration.Internal.LiveMapViewService>();
    return new Mithril.MapCalibration.Internal.LiveMapViewService(probe, capture, textures, ui, logger);
});
```

If `OverlayWindowService.Window` is private or non-accessible, expose an internal accessor (e.g., `internal Window? Window => _window;`) — small, additive change. If `IBaseTextureProvider` isn't registered here, fall back to whichever extension registers it (likely `AddCalibrationDetection`).

- [ ] **Step 4: Build the solution**

Run: `dotnet build Mithril.slnx --no-restore`
Expected: success.

- [ ] **Step 5: Run the full test suite to confirm nothing regressed**

Run: `dotnet test Mithril.slnx --no-restore`
Expected: all existing tests pass; the new tests added in P1.1–P1.7 pass.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs
git commit -m "feat(calibration): DI for IMapViewProbe + IOverlayCaptureSource + ILiveMapViewService (refs #1095)"
```

---

### Task P1.9: Open Phase 1 PR

- [ ] **Step 1: Push the branch + open the PR**

```bash
git push -u origin claude/strange-wilbur-d81f9a
gh pr create --title "feat(calibration): live-view detector infrastructure (Phase 1 of #1095)" --body "$(cat <<'EOF'
## Summary

Phase 1 of [#1095](https://github.com/moumantai-gg/mithril/issues/1095) — adds new infrastructure for the live-view detector without changing runtime behavior.

- `MapViewFix` record struct (pan, viewScale, confidence, timestamp).
- `IMapViewProbe` + `CrossCorrelationMapViewProbe` (FFT-accelerated screenshot × base-texture probe).
- `IOverlayCaptureSource` + `OverlayWindowCaptureSource` (screen-region capture).
- `ILiveMapViewService` + `LiveMapViewService` (per-area fix holder + refresh orchestrator with dedup + UI-thread marshaling).
- DI wiring for all three.

Spec: [docs/planning/calibration-1095-live-view-detector/spec.md](../tree/claude/strange-wilbur-d81f9a/docs/planning/calibration-1095-live-view-detector/spec.md).

**No behavior change in this PR** — none of the new types have runtime callers yet. The zoom slider, `IOverlayZoomSource`, and `CalibrationZoom` still work as before. Phase 2 PR does the cutover.

## Test plan

- [ ] `dotnet test Mithril.slnx` — full suite green
- [ ] Manual: shell builds, runs, and behaves identically to `main` (slider still drives projection; no detector calls fire)
EOF
)"
```

- [ ] **Step 2: Pause for code review** — wait for review + approval before merging Phase 1, then continue with Phase 2 tasks.

---

## Phase 2 PR — cutover

PR-2 deletes `CalibrationZoom` from all carriers, simplifies the projection math, deletes `IOverlayZoomSource` + slider + `SessionState.CurrentMapZoom`, swaps consumers to layer-1 + layer-2 composition, wires trigger sites, and ships the user-facing change.

**Branch off Phase 1 once merged.** Create a new feature branch from `main`:

```bash
git checkout main && git pull
git checkout -b claude/calibration-1095-cutover
```

**PR title:** `feat(calibration): cutover to layer-1 + layer-2 projection model (closes #1095)`

### Task P2.1: Drop CalibrationZoom from projection math + cal record + structs

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/AreaProjectionCore.cs`
- Modify: `src/Mithril.MapCalibration/AreaCalibration.cs`
- Modify: `src/Mithril.MapCalibration/WorldToTextureCalibration.cs`
- Modify: `src/Mithril.MapCalibration/WorldToOverlayCalibration.cs`
- Modify: existing tests that pass `currentZoom` / `calibrationZoom` (the compiler will list them — propagate fixes mechanically)

This task is the breaking change. The compiler is the worklist. Do all the math + struct + record removals in one commit so the codebase compiles before and after; non-test consumer call sites will follow in P2.2–P2.7.

- [ ] **Step 1: Simplify AreaProjectionCore**

Replace [AreaProjectionCore.cs:18-58](../../../src/Mithril.MapCalibration/Internal/AreaProjectionCore.cs:18) with:

```csharp
public static (double X, double Y) Project(
    double originX, double originY, double scale, double rotationRadians,
    bool mirrorNorth, WorldCoord world)
{
    var east = world.X;
    var north = mirrorNorth ? -world.Z : world.Z;
    var cos = Math.Cos(rotationRadians);
    var sin = Math.Sin(rotationRadians);
    var rotE = east * cos + north * sin;
    var rotN = -east * sin + north * cos;
    return (originX + scale * rotE, originY - scale * rotN);
}

public static WorldCoord? Unproject(
    double originX, double originY, double scale, double rotationRadians,
    bool mirrorNorth, double pixelX, double pixelY)
{
    if (scale <= 1e-9) return null;
    var rotE = (pixelX - originX) / scale;
    var rotN = -(pixelY - originY) / scale;
    var cos = Math.Cos(rotationRadians);
    var sin = Math.Sin(rotationRadians);
    var east = rotE * cos - rotN * sin;
    var north = rotE * sin + rotN * cos;
    var worldX = east;
    var worldZ = mirrorNorth ? -north : north;
    return new WorldCoord(worldX, 0, worldZ);
}
```

Update the class XML-doc to remove the zoom-factor commentary; reference `MapViewFix` for live composition.

- [ ] **Step 2: Update WorldToTextureCalibration**

In [WorldToTextureCalibration.cs](../../../src/Mithril.MapCalibration/WorldToTextureCalibration.cs):
- Remove `double CalibrationZoom` from the primary constructor (line 21).
- Replace `ToTexture` overloads with a single overload:

```csharp
public TexturePixel ToTexture(WorldCoord world)
{
    var (x, y) = AreaProjectionCore.Project(
        OriginX, OriginY, Scale, RotationRadians, MirrorNorth, world);
    return new TexturePixel(x, y);
}

public WorldCoord? FromTexture(TexturePixel pixel) =>
    AreaProjectionCore.Unproject(
        OriginX, OriginY, Scale, RotationRadians, MirrorNorth, pixel.X, pixel.Y);
```

- Update `ProjectThroughOverlay`:

```csharp
public WorldToOverlayCalibration ProjectThroughOverlay(MapRect overlayRect)
{
    var sx = overlayRect.Width / (double)overlayRect.TextureWidth;
    var sy = overlayRect.Height / (double)overlayRect.TextureHeight;
    return new WorldToOverlayCalibration(
        OriginX: overlayRect.OriginX + OriginX * sx,
        OriginY: overlayRect.OriginY + OriginY * sy,
        Scale: Scale * sx,
        RotationRadians: RotationRadians,
        MirrorNorth: MirrorNorth);
}
```

- [ ] **Step 3: Update WorldToOverlayCalibration**

In [WorldToOverlayCalibration.cs](../../../src/Mithril.MapCalibration/WorldToOverlayCalibration.cs):
- Remove `double CalibrationZoom` from the primary constructor.
- Replace `ToOverlay` / `FromOverlay` with single-arg versions:

```csharp
public OverlayPixel ToOverlay(WorldCoord world)
{
    var (x, y) = AreaProjectionCore.Project(
        OriginX, OriginY, Scale, RotationRadians, MirrorNorth, world);
    return new OverlayPixel(x, y);
}

public WorldCoord? FromOverlay(OverlayPixel pixel) =>
    AreaProjectionCore.Unproject(
        OriginX, OriginY, Scale, RotationRadians, MirrorNorth, pixel.X, pixel.Y);

/// <summary>Compose this canonical-overlay projection with a live MapViewFix
/// (in texture-pixel coords) to produce the live overlay pixel. Used by the
/// Layer-2 composition path when the cal is Texture-frame and has been routed
/// through <see cref="WorldToTextureCalibration.ProjectThroughOverlay"/>.</summary>
public OverlayPixel ToLiveOverlay(WorldCoord world, MapViewFix fix)
{
    var canonical = ToOverlay(world);
    var (lx, ly) = fix.TextureToOverlay(canonical.X, canonical.Y);
    return new OverlayPixel(lx, ly);
}
```

- [ ] **Step 4: Drop CalibrationZoom from AreaCalibration**

In [AreaCalibration.cs](../../../src/Mithril.MapCalibration/AreaCalibration.cs):
- Delete lines 37–44 (the `CalibrationZoom` property + docs).
- Change `SchemaVersion` default from 1 to 3 (line 60).

- [ ] **Step 5: Build to find call sites**

Run: `dotnet build Mithril.slnx --no-restore 2>&1 | tee /tmp/build-errors.txt`
Expected: many errors. The compiler enumerates every site that referenced the removed APIs — these are the work items for P2.2–P2.7.

- [ ] **Step 6: Sweep test fixtures**

In `tests/Mithril.MapCalibration.Tests/` and `tests/Mithril.MapCalibration.Capture.Tests/`, replace literal `CalibrationZoom = ...` constructions and 2-arg `ToOverlay(world, zoom)` / `ToTexture(world, zoom)` calls with 1-arg forms. The compiler errors enumerate the sites.

- [ ] **Step 7: Build green at this checkpoint**

Run: `dotnet build Mithril.slnx --no-restore`
Expected: any remaining errors are in Legolas / Mithril.Overlay consumer code, which the next tasks address.

- [ ] **Step 8: Commit (do NOT push until later tasks compile too)**

```bash
git add -p  # stage only the math + struct + record changes
git commit -m "refactor(calibration): drop CalibrationZoom from projection math + records (refs #1095)"
```

---

### Task P2.2: Delete IOverlayZoomSource + LegolasOverlayZoomSource + OverlayWindowService swap

**Files:**
- Delete: `src/Mithril.Overlay/IOverlayZoomSource.cs`
- Delete: `src/Legolas.Module/Rendering/LegolasOverlayZoomSource.cs`
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`
- Modify: `src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs`
- Modify: `src/Legolas.Module/LegolasModule.cs` (around lines 227–234)

- [ ] **Step 1: Delete the interface + adapter files**

```bash
git rm src/Mithril.Overlay/IOverlayZoomSource.cs
git rm src/Legolas.Module/Rendering/LegolasOverlayZoomSource.cs
```

- [ ] **Step 2: Update OverlayWindowService**

In [OverlayWindowService.cs](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs):
- Remove the `IOverlayZoomSource _zoomSource` field (line 80) and its ctor param.
- Add `ILiveMapViewService _liveView` field + ctor param.
- Replace the per-tick zoom read in the projection driver (search for `_zoomSource.CurrentZoom`): per-tick, fetch `_liveView.GetCurrent(currentArea)`; if null, skip marker projection (status badge "not measured" path); if non-null, route through `ToLiveOverlay(world, fix)` via `ResolveComposedOverlayCalibration` → layer-2 composition.

The exact projection-driver call site is around line 368 / 663 (where `ResolveComposedOverlayCalibration` is invoked today). After computing `composedCal` (a `WorldToOverlayCalibration`), look up the live fix and substitute `composedCal.ToLiveOverlay(world, fix)` for the previous `composedCal.ToOverlay(world, currentZoom)` form.

- [ ] **Step 3: Update DI registration**

In [OverlayServiceCollectionExtensions.cs:55](../../../src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs:55):
- Remove the `services.TryAddSingleton<IOverlayZoomSource>(...)` line.

- [ ] **Step 4: Remove the Legolas override**

In [LegolasModule.cs around lines 227–234](../../../src/Legolas.Module/LegolasModule.cs):
- Remove the block that registers `LegolasOverlayZoomSource` as `IOverlayZoomSource` (the override referenced in the file's comment "#835 step 6: override the platform's default FixedOverlayZoomSource").

- [ ] **Step 5: Build the calibration / overlay path**

Run: `dotnet build src/Mithril.Overlay --no-restore`
Run: `dotnet build src/Legolas.Module --no-restore 2>&1 | tee /tmp/legolas-errors.txt`
Expected: success on `Mithril.Overlay`; consumer errors in Legolas (next tasks).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(overlay): delete IOverlayZoomSource; swap to ILiveMapViewService (refs #1095)"
```

---

### Task P2.3: Delete SessionState.CurrentMapZoom + MapOverlayViewModel zoom-seed + zoom-mismatch UI

**Files:**
- Modify: `src/Legolas.Module/ViewModels/SessionState.cs`
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`

- [ ] **Step 1: Delete CurrentMapZoom from SessionState**

In [SessionState.cs:147](../../../src/Legolas.Module/ViewModels/SessionState.cs:147):
- Delete the `CurrentMapZoom` `[ObservableProperty]` field (and the partial `OnCurrentMapZoomChanged` clamp method).

- [ ] **Step 2: Strip zoom-seed + zoom-mismatch from MapOverlayViewModel**

In [MapOverlayViewModel.cs](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs):
- Delete the `SessionState.CurrentMapZoom` PropertyChanged subscription (around line 215).
- Delete the zoom-seed block in `OnCalibrationChanged` (lines 809–815 — the `_session.CurrentMapZoom = cal.CalibrationZoom;` assignment plus the `_lastSeenAreaKey` tracking it gates on).
- Delete `IsZoomMismatchWarningVisible`, `ZoomMismatchText`, `CalibrationZoomLabel`, `IsCalibrationZoomLabelVisible` properties (and any private fields they expose).
- Delete the `OnPropertyChanged(nameof(IsZoomMismatchWarningVisible))` etc. calls scattered through `OnCalibrationChanged` (also around lines 795–799).
- Inject `ILiveMapViewService` into the constructor and subscribe to its `Changed` event; on event, call `OnPropertyChanged` for marker collections so they re-project against the new fix.

- [ ] **Step 3: Build Legolas**

Run: `dotnet build src/Legolas.Module --no-restore 2>&1 | tee /tmp/legolas-errors2.txt`
Expected: the consumer-projection errors (next task) remain; the SessionState + VM cleanup compiles.

- [ ] **Step 4: Commit**

```bash
git add src/Legolas.Module/ViewModels/SessionState.cs src/Legolas.Module/ViewModels/MapOverlayViewModel.cs
git commit -m "refactor(legolas): delete CurrentMapZoom + zoom-mismatch UI (refs #1095)"
```

---

### Task P2.4: Swap projection-consumer call sites to layer-2 composition

**Files:**
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs` (further — the marker projection sites)
- Modify: `src/Legolas.Module/Services/PlayerLogIngestionService.cs`
- Modify: `src/Legolas.Module/Rendering/LegolasOverlaySceneDrawer.cs`

- [ ] **Step 1: Update RebuildCalibrationGhosts**

In `MapOverlayViewModel.cs` around line 696 (the existing `RebuildCalibrationGhosts` method):
- Pull the live fix: `var fix = _liveView.GetCurrent(area);`
- If fix is null: clear `CalibrationGhosts`, set status badge to "not measured", return. The existing telemetry meter call (`ProjectionSkipped.Add(1, ...)`) stays.
- If non-null: pass `fix` through to `GhostLabelDeclutter.Build(refs, cal, fix)`. This requires updating `GhostLabelDeclutter` too — see Step 4.

- [ ] **Step 2: Update MotherlodeMarkerPixels + MotherlodeGuidanceOverlay**

Around `MapOverlayViewModel.cs:1318` and `:1343`: same pattern — pull fix; if null, return empty / hide; if non-null, route the canonical overlay-pixel through `fix.TextureToOverlay` (or use the `ToLiveOverlay` helper on the cal).

- [ ] **Step 3: Update HandleMapTarget**

In `PlayerLogIngestionService.cs:191` (`HandleMapTarget`): pull fix; if null, refuse to add the survey pin (it'd render at wrong position); if non-null, route through layer-2.

- [ ] **Step 4: Update GhostLabelDeclutter.Build signature**

If the existing `GhostLabelDeclutter.Build` takes a `currentZoom` parameter, replace it with `MapViewFix fix` and apply `ToLiveOverlay` internally. Trace its call sites with the LSP and update each.

- [ ] **Step 5: Update LegolasOverlaySceneDrawer**

In `LegolasOverlaySceneDrawer.cs:147` (`DrawCalibrationGhosts`): drop the `currentZoom` parameter from the drawer signature; the ghosts have already been projected to live overlay pixels by the VM upstream.

- [ ] **Step 6: Build + run full test suite**

Run: `dotnet build Mithril.slnx --no-restore`
Run: `dotnet test Mithril.slnx --no-restore`
Expected: clean build. Tests that broke on the math change need fixing in this pass — propagate `MapViewFix` through test setup where projection was being verified. For tests that don't care about live view, pass `MapViewFix(0, 0, 1, 1, DateTimeOffset.UnixEpoch)` (canonical identity fix).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(legolas): swap projection consumers to layer-2 composition (refs #1095)"
```

---

### Task P2.5: Trigger sites + RedetectMapViewHotkey + status badge

**Files:**
- Create: `src/Legolas.Module/Hotkeys/RedetectMapViewHotkey.cs`
- Modify: `src/Legolas.Module/Hotkeys/OverlayController.cs`
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`
- Modify: `src/Legolas.Module/Views/MapOverlayView.xaml` (delete zoom strip; add status badge)
- Modify: `src/Legolas.Module/Views/MapOverlayView.xaml.cs` (any code-behind referencing removed bindings)
- Modify: `src/Legolas.Module/LegolasModule.cs` (register the hotkey command + module DI for `ILiveMapViewService` if needed)

- [ ] **Step 1: Create the re-detect hotkey command**

```csharp
// src/Legolas.Module/Hotkeys/RedetectMapViewHotkey.cs
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Mithril.Shared.Hotkeys;

namespace Legolas.Hotkeys;

/// <summary>
/// Hotkey: re-detect the live map view by re-running the probe for the
/// current area. The "I just changed PG's zoom or pan; resync" affordance.
/// See <c>docs/planning/calibration-1095-live-view-detector/spec.md</c> §6.
/// </summary>
public sealed class RedetectMapViewHotkey : IHotkeyCommand
{
    private readonly ILiveMapViewService _liveView;
    private readonly Arda.Contracts.IAreaState _areaState;
    private readonly ILogger<RedetectMapViewHotkey>? _logger;

    public string Id => "legolas.redetect_map_view";
    public string Description => "Re-detect map view (after panning or zooming PG)";

    public RedetectMapViewHotkey(
        ILiveMapViewService liveView,
        Arda.Contracts.IAreaState areaState,
        ILogger<RedetectMapViewHotkey>? logger = null)
    {
        _liveView = liveView;
        _areaState = areaState;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var area = _areaState.CurrentArea?.MapAssetKey;
        if (string.IsNullOrEmpty(area))
        {
            _logger?.LogInformation("Redetect hotkey fired but no area is current — no-op.");
            return;
        }
        _logger?.LogInformation("Redetect hotkey fired for {Area}.", area);
        await _liveView.RefreshAsync(area, ct);
    }
}
```

(If `IHotkeyCommand`'s actual surface differs from the above, match the existing pattern in the Hotkeys folder — `cat src/Legolas.Module/Hotkeys/Commands.cs` to check.)

- [ ] **Step 2: Wire OverlayController trigger sites**

In `OverlayController.cs`, find the spots that fire on:
- Validation-toggle enabled (`SetCalibrationValidation(true)` callers)
- Motherlode-overlay enabled
- Survey-overlay enabled

After each enable, invoke `_liveView.RefreshAsync(currentArea)`. The exact pattern depends on the controller's existing event topology — read the file once first.

- [ ] **Step 3: Register the hotkey in LegolasModule.Register**

In `LegolasModule.cs`, register the new hotkey command:

```csharp
services.AddSingleton<IHotkeyCommand, RedetectMapViewHotkey>();
```

(Place it next to existing `IHotkeyCommand` registrations.)

- [ ] **Step 4: Replace zoom strip with status badge in MapOverlayView.xaml**

In [MapOverlayView.xaml:87-92](../../../src/Legolas.Module/Views/MapOverlayView.xaml:87) (and any related zoom-strip XAML up the file):
- Delete the `<Slider>` and `<TextBox>` bound to `Session.CurrentMapZoom`.
- Delete the zoom-mismatch chip binding (whatever ties to `IsZoomMismatchWarningVisible`).
- Delete the cal-zoom label binding (`CalibrationZoomLabel`).
- Add a status badge bound to `LiveViewStatusText` (new property on `MapOverlayViewModel`):

```xml
<TextBlock Text="{Binding LiveViewStatusText}"
           Style="{StaticResource OverlayBadge}"
           Margin="6,2"/>
```

Add `LiveViewStatusText` to `MapOverlayViewModel`:

```csharp
public string LiveViewStatusText
{
    get
    {
        var area = _areaCalibration?.CurrentScene?.MapAssetKey;
        if (string.IsNullOrEmpty(area)) return string.Empty;
        var status = _liveView.GetStatus(area);
        var fix = _liveView.GetCurrent(area);
        return status switch
        {
            LiveMapViewStatus.Detected when fix is { } f =>
                $"View: detected ({f.MeasuredAt.LocalDateTime:HH:mm:ss}) — {f.ViewScale:0.00}×",
            LiveMapViewStatus.Detecting => "View: detecting…",
            LiveMapViewStatus.FailedNoBaseTexture => "View: failed — no base texture for this area",
            LiveMapViewStatus.FailedNoCapture => "View: failed — overlay not capturable",
            LiveMapViewStatus.FailedLowConfidence => "View: failed — couldn't match base texture",
            _ => "View: not measured — re-detect hotkey on the world map",
        };
    }
}
```

In the `Changed` subscription, also raise `OnPropertyChanged(nameof(LiveViewStatusText))`.

- [ ] **Step 5: Drop WizardView zoom strip**

In [WizardView.xaml:35-53](../../../src/Legolas.Module/Views/WizardView.xaml:35) and the second binding around line 730:
- Delete the slider + textbox bound to `Session.CurrentMapZoom`.
- Replace with a one-line prompt: `<TextBlock Text="Zoom out fully (entire map visible), then click the first landmark." Margin="6,2"/>`.

- [ ] **Step 6: Build + run tests**

Run: `dotnet build Mithril.slnx --no-restore`
Run: `dotnet test Mithril.slnx --no-restore`
Expected: clean.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(legolas): live-view status badge + re-detect hotkey + trigger sites (refs #1095)"
```

---

### Task P2.6: Migration log for legacy CalibrationZoom field on first load

**Files:**
- Modify: wherever `AreaCalibration` is loaded from disk (likely `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` and any baseline loader)

The persisted JSON ignores unknown properties by default with `System.Text.Json`, so loads of legacy `calibrationZoom` succeed silently. We want the one-time migration log per cal.

- [ ] **Step 1: Find the load sites**

Run: `grep -rn "JsonSerializer.Deserialize.*AreaCalibration\|JsonSerializer.*AreaCalibration" "src/Mithril.MapCalibration"`

- [ ] **Step 2: Add a load-side migration logger**

In the deserialization site, after deserializing, if the source JSON contained `calibrationZoom`, emit an Info log:

```csharp
// Pseudocode — adapt to the actual deserialization plumbing.
if (rawJsonElement.TryGetProperty("calibrationZoom", out var legacyZoom))
{
    _logger?.LogInformation(
        "Migrated AreaCalibration {Area} — dropped CalibrationZoom={Value} (no longer load-bearing).",
        mapAssetKey, legacyZoom.GetDouble());
}
```

If deserialization is currently typed-only (no raw JsonElement available), the simplest path is to add an `internal` shim: deserialize into a typed-but-with-legacy-field DTO that includes `CalibrationZoom`, then map to the modern `AreaCalibration` while emitting the log.

- [ ] **Step 3: Add a test**

```csharp
// tests/Mithril.MapCalibration.Tests/AreaCalibrationLegacyZoomMigrationTests.cs
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public sealed class AreaCalibrationLegacyZoomMigrationTests
{
    [Fact]
    public void Deserialize_RecordWithLegacyCalibrationZoom_LoadsCleanly()
    {
        var json = """
          { "scale": 1.0, "rotationRadians": 0, "originX": 0, "originY": 0,
            "referenceCount": 4, "residualPixels": 0.5, "calibrationZoom": 0.42 }
        """;

        var cal = System.Text.Json.JsonSerializer.Deserialize<AreaCalibration>(json);

        cal.Should().NotBeNull();
        cal!.Scale.Should().Be(1.0);
        // The legacy property is ignored on load; the type no longer carries it.
    }
}
```

- [ ] **Step 4: Build + test**

Run: `dotnet test Mithril.slnx --filter "FullyQualifiedName~AreaCalibrationLegacyZoomMigrationTests" --no-restore`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(calibration): log + ignore legacy CalibrationZoom on AreaCalibration load (refs #1095)"
```

---

### Task P2.7: Manual E2E + follow-up issues + open PR

- [ ] **Step 1: Manual E2E smoke**

Build + launch the shell via `scripts/start.ps1` (skill: `mithril`). Manual checklist:
1. Open the map overlay on **Serbule** at PG zoom = canonical (fully zoomed out). Toggle validation on. Within ~1s, ghosts render at the expected reference positions. Badge reads `View: detected (...) — 1.00×`.
2. Zoom PG in 2–3 steps. Markers do not auto-update (expected). Press the re-detect hotkey (set one if unset). Within ~1s, ghosts re-render at correct positions; badge updates with the new scale.
3. Open the inventory dialog over the map. Press re-detect. Badge transitions to `View: failed — couldn't match base texture`; prior ghost positions stay rendered (graceful degradation per spec §6).
4. Close inventory. Re-detect. Badge recovers.
5. Switch to a small dungeon (e.g. **Khyruleks Crypt**). Validate. Ghosts render. (Khyruleks was the "always-worked" reference case; should still work.)
6. WizardView: open the wizard. Confirm the zoom strip is gone; the prompt asks the user to zoom fully out.

If any step fails, debug + fix before opening the PR; the failure mode is the one #1095 was filed to close.

- [ ] **Step 2: File follow-up issues**

```bash
gh issue create --title "Periodic background detection for live-view fix" --body "$(cat <<'EOF'
Follow-up to #1095. Today the live-view detector fires on user gestures (overlay-marker-enable triggers + manual re-detect hotkey). Consider a low-rate periodic refresh so users who pan/zoom PG mid-session don't need to remember to re-detect.

Acceptance: a configurable background loop that re-runs `ILiveMapViewService.RefreshAsync` while the overlay is visible, gated by overlay visibility + a sensible interval (e.g. 5s). Should not exceed the spec's sub-1s probe budget per fire. Plumbing: a hosted service that ticks; can be disabled in settings.

Spec: docs/planning/calibration-1095-live-view-detector/spec.md §10.1.
EOF
)" --label "area:map-calibration,module:legolas"

gh issue create --title "Verify PG log signals for world-map pan/zoom" --body "$(cat <<'EOF'
Follow-up to #1095. The spec assumes PG emits no observable log signal for world-map UI state changes (pan or zoom). Verification owed — grep \`Player.log\` for any pattern that fires on map-open / map-zoom / map-pan and see if it can drive \`ILiveMapViewService\` directly (eliminating the user-gesture latency).

Acceptance: a wiki page (Player-Log-Signals §World map UI) documenting findings, plus an issue closure either negative (no signal exists) or positive (signal exists — wire it up).

Spec: docs/planning/calibration-1095-live-view-detector/spec.md §10.2.
EOF
)" --label "area:map-calibration,area:telemetry"

gh issue create --title "Wizard solving directly in Texture frame" --body "$(cat <<'EOF'
Follow-up to #1095. Today's wizard solves in Overlay frame and is composed through #1087 \`ProjectThroughOverlay\` at runtime. A wizard that solves directly in Texture frame would retire the Overlay frame from wizard solves entirely, simplifying the runtime path and removing the Overlay → Texture conversion question raised in #1095 spec §4.2.

Acceptance: the wizard captures landmark positions in base-texture-pixel space (using the cached base texture as the reference), solves via \`LandmarkCalibrationSolver\` with Texture-frame output, persists as Texture-frame. The Overlay-frame wizard path is removed.

Spec: docs/planning/calibration-1095-live-view-detector/spec.md §10.3.
EOF
)" --label "area:map-calibration,module:legolas"

gh issue create --title "Migrator: convert stored Overlay-frame wizard cals to Texture frame" --body "$(cat <<'EOF'
Follow-up to #1095 (filed only if PR-1 didn't take the inverse-compose path). User-stored Overlay-frame wizard cals don't get live-view detector support today. A one-shot migrator that walks them, inverse-composes through \`IMapTextureDimensions\`, and re-saves as Texture-frame so they pick up layer-2 detection without re-solving.

Acceptance: a startup migration that visits every Overlay-frame cal, attempts conversion, logs success/failure per area, persists in-place.

Spec: docs/planning/calibration-1095-live-view-detector/spec.md §10.4.
EOF
)" --label "area:map-calibration,module:legolas"
```

- [ ] **Step 3: Flip the spec INDEX row to shipped (when PR merges — do this after merge, not now)**

Note for future: when the Phase 2 PR merges, edit `docs/planning/INDEX.md` to flip the row from `active` to `shipped` with the merged-PR link.

- [ ] **Step 4: Push + open Phase 2 PR**

```bash
git push -u origin claude/calibration-1095-cutover
gh pr create --title "feat(calibration): cutover to layer-1 + layer-2 projection model (closes #1095)" --body "$(cat <<'EOF'
## Summary

Phase 2 of [#1095](https://github.com/moumantai-gg/mithril/issues/1095) — cutover to the layer-1 + layer-2 projection model. Builds on Phase 1's infrastructure (already merged).

**Math.** `CalibrationZoom` removed from `AreaCalibration` (record), `WorldToTextureCalibration` (struct field), `WorldToOverlayCalibration` (struct field). `AreaProjectionCore.Project` / `Unproject` simplified — no `zoomFactor`, no `currentZoom`, no `calibrationZoom`.

**Layer-2 composition.** `WorldToOverlayCalibration.ToLiveOverlay(world, fix)` composes the canonical projection with a `MapViewFix` from the live-view detector. Consumed by `RebuildCalibrationGhosts`, `MotherlodeMarkerPixels` / `MotherlodeGuidanceOverlay`, `HandleMapTarget`, `OverlayWindowService`'s projection driver.

**Slider deleted.** `SessionState.CurrentMapZoom`, the slider XAML in `MapOverlayView` + `WizardView`, `IsZoomMismatchWarningVisible` + related properties — all gone. `IOverlayZoomSource` + `FixedOverlayZoomSource` + `LegolasOverlayZoomSource` deleted.

**Detection triggers.** Toggle validation, motherlode overlay enable, survey overlay enable, and a new `RedetectMapViewHotkey` all fire `LiveMapViewService.RefreshAsync(currentArea)`. Status badge surfaces detection outcome.

**Migration.** Legacy `calibrationZoom` JSON fields on `AreaCalibration` records are silently ignored on load with a one-shot Info log per cal.

Spec: [docs/planning/calibration-1095-live-view-detector/spec.md](../tree/claude/calibration-1095-live-view-detector/docs/planning/calibration-1095-live-view-detector/spec.md).
Follow-ups filed: #N (periodic detection), #N+1 (log-signal verification), #N+2 (wizard in Texture frame), #N+3 (Overlay→Texture cal migrator).

## Test plan

- [ ] `dotnet test Mithril.slnx` — full suite green
- [ ] Manual E2E (per spec §9 + plan P2.7 step 1):
  - [ ] Serbule at canonical: validation toggle → ghosts render < 1s; badge reads `detected`
  - [ ] Serbule zoomed in 2–3 steps: re-detect hotkey → ghosts re-render correctly; badge updates
  - [ ] Inventory dialog over map: re-detect → badge reads `failed`; prior ghosts preserved
  - [ ] Khyruleks Crypt: validation works (regression check against the "always-worked" reference case)
  - [ ] Wizard view: zoom strip gone, prompt instructs user to zoom out fully

Closes [#1095](https://github.com/moumantai-gg/mithril/issues/1095).
EOF
)"
```

- [ ] **Step 5: Pause for code review** — wait for review + approval before merging Phase 2.

---

## Post-merge

After both PRs merge:

- [ ] Flip the `docs/planning/INDEX.md` row for `calibration-1095-live-view-detector` from `active` to `shipped`, linking the Phase 1 + Phase 2 PR URLs.
- [ ] Verify the follow-up issues link to the spec and have correct labels.
- [ ] Close [#1095](https://github.com/moumantai-gg/mithril/issues/1095) (the Phase 2 PR's "closes #1095" should auto-close on merge).
