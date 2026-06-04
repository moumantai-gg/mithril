# Sparse-Interior Locate-Stage Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Sobel-magnitude + 100 px padded + 3-level Gaussian pyramid `matchTemplate` fallback `IMapRegionRefiner` that runs after ORB+Lowe fails, so the auto-calibration locate stage succeeds on sparse-interior maps (dungeons, basements, caves) instead of returning `rejected-map-not-located`.

**Architecture:** A new `SobelPaddedPyramidRefiner : IMapRegionRefiner` lives alongside the existing `FeatureMatchingRefiner` in `Mithril.MapCalibration.Detection`. A new `CompositeMapRegionRefiner : IMapRegionRefiner` owns dispatch (primary first, fallback on no-fit/reject). The engine's DI binding changes from `FeatureMatchingRefiner` to `CompositeMapRegionRefiner`. The engine's hard-coded `is FeatureMatchingRefiner` cast becomes `is IAreaContextualRefiner`, threaded through the composite. `LocateMetrics` gains `Provenance` + `Confidence`; `LocatorBestJson` schema bumps to v2 with optional algorithm/NCC/pad/level fields. A new `rejected-map-low-confidence` outcome distinguishes input-pathology rejects from "no fit at all."

**Tech Stack:** C# (.NET 10), OpenCvSharp4 4.10 (core only — Sobel, MatchTemplate, MinMaxLoc, PyrDown, CopyMakeBorder, Normalize), xUnit + FluentAssertions for tests, MEL `ILogger` for logging, `System.Diagnostics.ActivitySource` for telemetry.

**Reference:** [spec.md](spec.md) is the design source-of-truth. The throwaway spike — `tools/MapCalibrationFromScreenshot/SparseLocateSpike.cs::TemplateMatchSobelPaddedPyramid3` on branch `claude/ecstatic-mccarthy-b6e5ad` — is the algorithm reference, NOT a code-to-port. Re-implement cleanly.

---

## File Inventory

**Created:**
- `src/Mithril.MapCalibration.Detection/Internal/SobelMagnitudeHelpers.cs` — Sobel magnitude + sub-pixel-refinement static helpers shared by the new refiner (and available to future detectors).
- `src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs` — the new fallback refiner.
- `src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs` — dispatcher refiner (primary, fallback).
- `src/Mithril.MapCalibration.Detection/IAreaContextualRefiner.cs` — tiny marker interface for `SetAreaKey(string?)`.
- `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptionsJsonContext.cs` — STJ source-gen context for the locate-options versioned settings file.
- `tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs` — synthetic + corpus regression tests for the new refiner.
- `tests/Mithril.MapCalibration.Capture.Tests/CompositeMapRegionRefinerTests.cs` — dispatch tests with fake primary/fallback.
- `tests/Mithril.MapCalibration.Capture.Tests/MapCalibrationLocateOptionsPersistenceTests.cs` — round-trip + Migrate + auto-save tests for the new settings store.
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/capture.png` — corpus regression fixture (extracted from the live bundle's `02-gray-screenshot.png`).
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/baseTexture.png` — corpus regression fixture (extracted from the asset cache).

**Modified:**
- `src/Mithril.MapCalibration/LocateMetrics.cs` — add `Provenance` enum + `Confidence` field.
- `src/Mithril.MapCalibration/LocateProvenance.cs` — new enum file (it would crowd LocateMetrics.cs).
- `src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs` — declare `IAreaContextualRefiner` on the type, populate `Provenance = OrbRansac` on `LocateMetrics` construction.
- `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs` — add `FallbackNccFloor` (default 0.20), `FallbackPadPx` (default 100), `ScaleMin/Max/Step`, `MinScaledDim`, `MinScaledDimHalf`, `MinScaledDimCoarse`; implement `IVersionedState<MapCalibrationLocateOptions>` with `Version = 1`, `SchemaVersion`, `Migrate`.
- `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj` — add `<ProjectReference Include="..\Mithril.Persistence\Mithril.Persistence.csproj" />` (zero-dependency project; lets the options type implement `IVersionedState<T>`).
- `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs` — register concrete refiner types, replace `IMapRegionRefiner` binding with `CompositeMapRegionRefiner`. **Keep** `services.TryAddSingleton<MapCalibrationLocateOptions>()` as the fallback when Capture's persistence-wired singleton isn't pre-registered (e.g. unit tests).
- `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` — add a `settingsDir` parameter to `AddMithrilMapCalibrationCapture`; before calling the Detection extension, register the versioned-settings singleton via `services.AddMithrilVersionedSettings<MapCalibrationLocateOptions>(Path.Combine(settingsDir, "map-calibration-locate.json"), MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions)`.
- `src/Mithril.Shell/DependencyInjection/ShellComposition.cs` — thread the per-machine settings dir (already in scope as `o.SettingsDir` or equivalent — check call site) into the `AddMithrilMapCalibrationCapture(...)` call.
- `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` — change `_refiner is FeatureMatchingRefiner` to `_refiner is IAreaContextualRefiner`; branch on `refineResult.Metrics?.Provenance` for outcome text + category; capture `refineResult.RawFitRect` + `Metrics` already happens.
- `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs` — extend `LocatorBestJson` to v2 with `Algorithm`, `FallbackNcc`, `PadPx`, `LevelScales`.
- `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs` — populate the new `LocatorBestJson` fields from `LocateMetrics`.
- `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs` — add `RejectedMapLowConfidence` constant.
- `docs/perf-trace-schema.md` — append the two new span names + the metric instrument.

**Test infrastructure modified:**
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` (if a fake `IMapRegionRefiner` lives here) — add a `Provenance` parameter to the test-locate-metrics builder.
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestLocateMetrics.cs` — ditto.
- `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationBundleJsonTests.cs` — add a v2 round-trip test that asserts the new fields survive write/read; v1-back-compat read test.

---

## Phase 1 — Shared helpers + Provenance enum

### Task 1.1: Add `LocateProvenance` enum

**Files:**
- Create: `src/Mithril.MapCalibration/LocateProvenance.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Which locate-stage algorithm produced a <see cref="LocateMetrics"/>
/// record. Bundle JSON + status copy + telemetry tags route on this.
/// </summary>
public enum LocateProvenance
{
    /// <summary>ORB + Lowe + RANSAC partial-affine (#1009 primary).</summary>
    OrbRansac = 0,

    /// <summary>Sobel magnitude + 100 px padded matchTemplate + 3-level pyramid (#1061 fallback).</summary>
    SobelPaddedPyramid = 1,
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.MapCalibration/Mithril.MapCalibration.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/LocateProvenance.cs
git commit -m "feat(map-calibration): introduce LocateProvenance enum (mithril#1061)"
```

### Task 1.2: Extend `LocateMetrics` with Provenance + Confidence

**Files:**
- Modify: `src/Mithril.MapCalibration/LocateMetrics.cs`

- [ ] **Step 1: Add the two fields to the record**

Replace the existing record with:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Diagnostic + gate-feeding metrics from one <see cref="IMapRegionRefiner"/>
/// run. Populated whenever the refiner produced a fit (gate-pass-or-not);
/// <c>null</c> on <see cref="MapRegionRefineResult"/> means "no fit found at all".
///
/// <para><b>Provenance.</b> <see cref="OrbRansac"/> populates Inlier* /
/// RotationDegrees / ResidualPixels; <see cref="Confidence"/> is null because
/// the gate reads InlierCount/InlierRatio.
/// <see cref="SobelPaddedPyramid"/> populates Scale + Tx + Ty + Confidence;
/// Inlier* / RotationDegrees / ResidualPixels are zero — consumers route on
/// <see cref="Provenance"/>.</para>
/// </summary>
public sealed record LocateMetrics(
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    bool Mirror,
    double Tx,
    double Ty,
    double ResidualPixels,
    LocateProvenance Provenance = LocateProvenance.OrbRansac,
    double? Confidence = null);
```

- [ ] **Step 2: Build solution**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds. Existing positional callers using the old 9-arg constructor still work because the two new fields are defaulted.

- [ ] **Step 3: Run the existing locator-metrics tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~LocateMetrics" --no-build`
Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~FeatureMatchingRefiner" --no-build`
Expected: both pass — Provenance defaults to `OrbRansac` so no behaviour change.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration/LocateMetrics.cs
git commit -m "feat(map-calibration): extend LocateMetrics with Provenance + Confidence (mithril#1061)"
```

### Task 1.3: Add `IAreaContextualRefiner` marker interface

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/IAreaContextualRefiner.cs`

- [ ] **Step 1: Create the interface**

```csharp
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Refiners that need per-area state set before <see cref="IMapRegionRefiner.Refine"/>
/// (currently: the ORB-descriptor cache key in <see cref="FeatureMatchingRefiner"/>).
/// The engine probes this interface instead of hard-casting to a concrete refiner type
/// so the dispatching <see cref="CompositeMapRegionRefiner"/> can transparently
/// forward the call to its inner refiners.
/// </summary>
public interface IAreaContextualRefiner
{
    /// <summary>
    /// Set the area-key context for the next <see cref="IMapRegionRefiner.Refine"/> call.
    /// Implementations may treat <c>null</c> as "no per-area context" — equivalent to
    /// never having called this. Not thread-safe by contract (calibration runs
    /// single-attempt-per-hotkey-press).
    /// </summary>
    void SetAreaKey(string? areaKey);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/IAreaContextualRefiner.cs
git commit -m "feat(map-calibration): introduce IAreaContextualRefiner marker (mithril#1061)"
```

### Task 1.4: Declare `FeatureMatchingRefiner : IAreaContextualRefiner` and stamp Provenance

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs`

- [ ] **Step 1: Declare the interface on the class**

Change the declaration line (around line 33):

```csharp
public sealed class FeatureMatchingRefiner : IMapRegionRefiner, IAreaContextualRefiner
```

`SetAreaKey` already exists at line 89, so the interface implementation is satisfied — no method body change.

- [ ] **Step 2: Stamp `Provenance = OrbRansac` on the metrics construction**

Find the `var metrics = new LocateMetrics(` site around line 218. Add two trailing named arguments:

```csharp
var metrics = new LocateMetrics(
    InlierCount: inlierCount,
    CandidateCount: candidateCount,
    InlierRatio: inlierRatio,
    Scale: scale,
    RotationDegrees: rotationDegrees,
    Mirror: false,
    Tx: tx, Ty: ty,
    ResidualPixels: residualPixels,
    Provenance: LocateProvenance.OrbRansac,
    Confidence: null);
```

- [ ] **Step 3: Run existing FM tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~FeatureMatchingRefiner"`
Expected: all pass (no behavioural change; Provenance was already the default).

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs
git commit -m "feat(map-calibration): FM refiner declares IAreaContextualRefiner + stamps Provenance (mithril#1061)"
```

### Task 1.5: Add `SobelMagnitudeHelpers` static class

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/Internal/SobelMagnitudeHelpers.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/SobelMagnitudeHelpersTests.cs`

- [ ] **Step 1: Write the failing tests first**

Create `tests/Mithril.MapCalibration.Capture.Tests/SobelMagnitudeHelpersTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Detection.Internal;
using OpenCvSharp;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class SobelMagnitudeHelpersTests
{
    [Fact]
    public void SobelMagnitude8U_returns_8U_single_channel_same_dims_as_input()
    {
        using var src = new Mat(64, 96, MatType.CV_8UC1, new Scalar(128));
        using var mag = SobelMagnitudeHelpers.SobelMagnitude8U(src);

        mag.Type().Should().Be(MatType.CV_8UC1);
        mag.Rows.Should().Be(64);
        mag.Cols.Should().Be(96);
    }

    [Fact]
    public void SobelMagnitude8U_emits_nonzero_response_at_a_vertical_edge()
    {
        using var src = new Mat(32, 32, MatType.CV_8UC1, new Scalar(0));
        // Left half black, right half white → strong vertical edge at x=16.
        Cv2.Rectangle(src, new Rect(16, 0, 16, 32), new Scalar(255), thickness: -1);

        using var mag = SobelMagnitudeHelpers.SobelMagnitude8U(src);
        var indexer = mag.GetGenericIndexer<byte>();

        indexer[16, 16].Should().BeGreaterThan((byte)50, "the edge column should be strongly lit");
        indexer[16, 0].Should().BeLessThan((byte)10, "the flat-black region should be near zero");
    }

    [Fact]
    public void RefineLocationSubPixel_returns_zero_at_a_boundary_peak()
    {
        using var ncc = new Mat(5, 5, MatType.CV_32FC1, new Scalar(0f));
        var (dx, dy) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(0, 0));
        dx.Should().Be(0);
        dy.Should().Be(0);
    }

    [Fact]
    public void RefineLocationSubPixel_finds_a_centered_offset_on_a_symmetric_parabolic_peak()
    {
        using var ncc = new Mat(5, 5, MatType.CV_32FC1, new Scalar(0f));
        var idx = ncc.GetGenericIndexer<float>();
        // Symmetric concave-down peak at (2,2) — vertex offset should be (0,0).
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
                idx[y, x] = 1.0f - 0.1f * ((x - 2) * (x - 2) + (y - 2) * (y - 2));

        var (dx, dy) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(2, 2));
        dx.Should().BeApproximately(0.0, 1e-6);
        dy.Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void RefineLocationSubPixel_clamps_to_unit_interval()
    {
        using var ncc = new Mat(3, 3, MatType.CV_32FC1, new Scalar(0f));
        var idx = ncc.GetGenericIndexer<float>();
        // Near-flat curvature → would return runaway value pre-clamp.
        idx[1, 0] = 0.50001f; idx[1, 1] = 0.50002f; idx[1, 2] = 0.50000f;
        idx[0, 1] = 0.50001f; idx[2, 1] = 0.50001f;
        var (dx, _) = SobelMagnitudeHelpers.RefineLocationSubPixel(ncc, new Point(1, 1));
        dx.Should().BeInRange(-1.0, 1.0);
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail (class does not exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SobelMagnitudeHelpers"`
Expected: compile error or CS0246 — `SobelMagnitudeHelpers` not found.

- [ ] **Step 3: Implement the static helper class**

Create `src/Mithril.MapCalibration.Detection/Internal/SobelMagnitudeHelpers.cs`:

```csharp
using OpenCvSharp;

namespace Mithril.MapCalibration.Detection.Internal;

/// <summary>
/// Shared OpenCV helpers for the Sobel-padded-pyramid locate fallback
/// (mithril#1061). Extracted into a static class so the refiner stays focused
/// on dispatch + the helpers are unit-testable on synthetic Mats without
/// constructing a refiner instance.
/// </summary>
internal static class SobelMagnitudeHelpers
{
    /// <summary>
    /// Sobel gradient magnitude normalised into 8-bit single-channel range.
    /// Continuous-valued (no binary thresholding) — the round-5 corpus measured
    /// a consistent 1.5–2× NCC strengthening over Canny binary edges.
    /// Caller owns the returned Mat.
    /// </summary>
    public static Mat SobelMagnitude8U(Mat src)
    {
        using var gx = new Mat();
        Cv2.Sobel(src, gx, MatType.CV_32F, 1, 0, ksize: 3);
        using var gy = new Mat();
        Cv2.Sobel(src, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        var dst = new Mat();
        Cv2.Normalize(mag, dst, 0, 255, NormTypes.MinMax, MatType.CV_8U);
        return dst;
    }

    /// <summary>
    /// 2D parabolic peak refinement on an NCC response map at the integer peak.
    /// Fits independent 1D parabolas through each axis's 3-pixel neighborhood
    /// and returns the vertex offsets clamped to ±1 px. Returns (0,0) when the
    /// peak sits on a boundary (no neighbors on one side) or when curvature is
    /// not concave-down on that axis.
    /// </summary>
    public static (double dx, double dy) RefineLocationSubPixel(Mat ncc, Point peakLoc)
    {
        int px = peakLoc.X, py = peakLoc.Y;
        if (px <= 0 || py <= 0 || px >= ncc.Cols - 1 || py >= ncc.Rows - 1)
            return (0, 0);
        var idx = ncc.GetGenericIndexer<float>();
        double c = idx[py, px];
        double left = idx[py, px - 1], right = idx[py, px + 1];
        double up = idx[py - 1, px], down = idx[py + 1, px];
        double denomX = left - 2 * c + right;
        double denomY = up - 2 * c + down;
        double dx = denomX < -1e-9 ? 0.5 * (left - right) / denomX : 0;
        double dy = denomY < -1e-9 ? 0.5 * (up - down) / denomY : 0;
        return (System.Math.Clamp(dx, -1.0, 1.0), System.Math.Clamp(dy, -1.0, 1.0));
    }
}
```

- [ ] **Step 4: Tests now pass — need `InternalsVisibleTo` for the test project**

The helpers are `internal`. Check whether `Mithril.MapCalibration.Detection.csproj` already exposes internals to `Mithril.MapCalibration.Capture.Tests`. If yes, skip. If no:

Add to the `.csproj` (or to a top-of-file `[assembly: InternalsVisibleTo(...)]` somewhere in the project — match existing style):

```xml
<ItemGroup>
  <InternalsVisibleTo Include="Mithril.MapCalibration.Capture.Tests" />
</ItemGroup>
```

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SobelMagnitudeHelpers"`
Expected: all 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/Internal/SobelMagnitudeHelpers.cs \
        tests/Mithril.MapCalibration.Capture.Tests/SobelMagnitudeHelpersTests.cs \
        src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj
git commit -m "feat(map-calibration): Sobel magnitude + sub-pixel-refine helpers (mithril#1061)"
```

### Task 1.6: Extend `MapCalibrationLocateOptions` with all fallback knobs

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs`

This task adds every magic number the new refiner will read from options instead of `const` — both the gate knobs (`FallbackNccFloor`, `FallbackPadPx`) and the ladder knobs (`ScaleMin/Max/Step`, `MinScaledDim`, `MinScaledDimHalf`, `MinScaledDimCoarse`). Promoting them now (before Phase 2 writes the refiner) means the refiner reads `_options.ScaleMin` from the start — no follow-up refactor.

- [ ] **Step 1: Add all eight new properties**

Append after `RansacReprojectionThresholdPx` (around line 63):

```csharp
    private double _fallbackNccFloor = 0.20;
    private int _fallbackPadPx = 100;
    private double _scaleMin = 0.20;
    private double _scaleMax = 1.20;
    private double _scaleStep = 0.02;
    private int _minScaledDim = 20;
    private int _minScaledDimHalf = 10;
    private int _minScaledDimCoarse = 5;

    /// <summary>
    /// Reject any Sobel-padded-pyramid fallback fit whose refined NCC is below
    /// this floor. Default 0.20 — round-5 corpus: real recoveries hit 0.45+,
    /// input-pathology cases sit at 0.20–0.32 (mithril#1061).
    /// </summary>
    public double FallbackNccFloor
    {
        get => _fallbackNccFloor;
        set { if (_fallbackNccFloor != value) { _fallbackNccFloor = value; OnChanged(); } }
    }

    /// <summary>
    /// Zero padding (px, all four sides) applied to the capture's Sobel
    /// magnitude before matchTemplate runs in the fallback. Default 100 px —
    /// enough headroom for the corpus's worst spill (HogansKeep-223119 = 34 px)
    /// without ballooning the pyramid's coarse stage (mithril#1061).
    /// </summary>
    public int FallbackPadPx
    {
        get => _fallbackPadPx;
        set { if (_fallbackPadPx != value) { _fallbackPadPx = value; OnChanged(); } }
    }

    /// <summary>Lower bound of the fallback's scale ladder. Default 0.20 (mithril#1061).</summary>
    public double ScaleMin
    {
        get => _scaleMin;
        set { if (_scaleMin != value) { _scaleMin = value; OnChanged(); } }
    }

    /// <summary>Upper bound of the fallback's scale ladder. Default 1.20 (mithril#1061).</summary>
    public double ScaleMax
    {
        get => _scaleMax;
        set { if (_scaleMax != value) { _scaleMax = value; OnChanged(); } }
    }

    /// <summary>Step between rungs of the fallback's coarse + fine ladders. Default 0.02 (mithril#1061).</summary>
    public double ScaleStep
    {
        get => _scaleStep;
        set { if (_scaleStep != value) { _scaleStep = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's full-resolution stage. Default 20 (mithril#1061).</summary>
    public int MinScaledDim
    {
        get => _minScaledDim;
        set { if (_minScaledDim != value) { _minScaledDim = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's half-resolution stage. Default 10 (mithril#1061).</summary>
    public int MinScaledDimHalf
    {
        get => _minScaledDimHalf;
        set { if (_minScaledDimHalf != value) { _minScaledDimHalf = value; OnChanged(); } }
    }

    /// <summary>Minimum scaled template dimension (px) at the fallback's quarter-resolution stage. Default 5 (mithril#1061).</summary>
    public int MinScaledDimCoarse
    {
        get => _minScaledDimCoarse;
        set { if (_minScaledDimCoarse != value) { _minScaledDimCoarse = value; OnChanged(); } }
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs
git commit -m "feat(map-calibration): extend locate options with fallback gate + ladder knobs (mithril#1061)"
```

### Task 1.7: Make `MapCalibrationLocateOptions` versioned (`IVersionedState<T>`)

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
- Modify: `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs`

`IVersionedState<T>` lives in `Mithril.Persistence` (zero-dep project). Detection currently does not reference it.

- [ ] **Step 1: Add the Mithril.Persistence project reference**

In `src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`, inside the `<ItemGroup>` that holds `Mithril.MapCalibration`:

```xml
<ItemGroup>
  <ProjectReference Include="..\Mithril.MapCalibration\Mithril.MapCalibration.csproj" />
  <ProjectReference Include="..\Mithril.Persistence\Mithril.Persistence.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Implement `IVersionedState<MapCalibrationLocateOptions>`**

In `MapCalibrationLocateOptions.cs`, modify the class declaration:

```csharp
using Mithril.Shared.Character;  // IVersionedState lives in this namespace despite the project name

public sealed class MapCalibrationLocateOptions
    : INotifyPropertyChanged, IVersionedState<MapCalibrationLocateOptions>
```

Add at the top of the class body:

```csharp
public const int Version = 1;
public static int CurrentVersion => Version;

/// <summary>
/// Persisted schema version. Defaults to <c>1</c> so a v1 JSON file (no
/// pre-existing schema field) deserialises as v1; fresh in-memory instances
/// also start at <c>1</c> — Migrate is a no-op for v1.
///
/// <para>Future deltas document themselves in this comment block, mirroring
/// the <c>LegolasSettings</c> convention. The first time a property is
/// renamed/removed or a new dependent default needs back-filling, bump
/// <see cref="Version"/> and add a branch to <see cref="Migrate"/>.</para>
/// </summary>
public int SchemaVersion { get; set; } = 1;

/// <summary>
/// v1 is the first persisted version. Identity passthrough today; future
/// schema changes add branches here (e.g. v1 → v2: rename
/// <c>FallbackPadPx</c> → <c>FallbackPaddingPx</c> would carry the old
/// value into the new property name).
/// </summary>
public static MapCalibrationLocateOptions Migrate(MapCalibrationLocateOptions loaded)
{
    if (loaded.SchemaVersion >= Version) return loaded;
    return loaded;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj`
Expected: succeeds. The `IVersionedState<T>` constraint `where T : class, IVersionedState<T>, new()` is satisfied because the class already has a parameterless ctor.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/Mithril.MapCalibration.Detection.csproj \
        src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptions.cs
git commit -m "feat(map-calibration): MapCalibrationLocateOptions implements IVersionedState v1 (mithril#1061)"
```

### Task 1.8: STJ source-gen context + persistence round-trip tests

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptionsJsonContext.cs`
- Create: `tests/Mithril.MapCalibration.Capture.Tests/MapCalibrationLocateOptionsPersistenceTests.cs`

- [ ] **Step 1: Write the failing round-trip tests first**

Create `tests/Mithril.MapCalibration.Capture.Tests/MapCalibrationLocateOptionsPersistenceTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.Shared.Settings;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class MapCalibrationLocateOptionsPersistenceTests
{
    [Fact]
    public void Load_returns_defaults_when_file_is_absent()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mithril-locate-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonSettingsStore<MapCalibrationLocateOptions>(
                tmp, MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
            var loaded = store.Load();

            loaded.Should().NotBeNull();
            loaded.SchemaVersion.Should().Be(MapCalibrationLocateOptions.Version);
            loaded.FallbackNccFloor.Should().Be(0.20);
            loaded.ScaleMin.Should().Be(0.20);
            loaded.OrbNFeatures.Should().Be(8000);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Save_then_load_preserves_custom_values()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mithril-locate-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonSettingsStore<MapCalibrationLocateOptions>(
                tmp, MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
            var write = new MapCalibrationLocateOptions
            {
                FallbackNccFloor = 0.30,
                ScaleMin = 0.15,
                ScaleMax = 1.50,
                OrbNFeatures = 12000,
            };
            store.Save(write);

            var read = store.Load();
            read.FallbackNccFloor.Should().Be(0.30);
            read.ScaleMin.Should().Be(0.15);
            read.ScaleMax.Should().Be(1.50);
            read.OrbNFeatures.Should().Be(12000);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Migrate_returns_loaded_instance_unchanged_at_current_version()
    {
        var loaded = new MapCalibrationLocateOptions { FallbackNccFloor = 0.42 };
        var migrated = MapCalibrationLocateOptions.Migrate(loaded);
        migrated.FallbackNccFloor.Should().Be(0.42);
        migrated.SchemaVersion.Should().Be(MapCalibrationLocateOptions.Version);
    }

    [Fact]
    public void Migrate_no_op_passes_through_when_schema_version_zero()
    {
        // A hypothetical legacy file without schemaVersion deserialises with the
        // default value 1; this test exercises the explicit-0 path to lock the
        // no-op contract (so a future v1 → v2 migration starts from a known place).
        var legacy = new MapCalibrationLocateOptions
        {
            SchemaVersion = 0,
            FallbackNccFloor = 0.33,
        };
        var migrated = MapCalibrationLocateOptions.Migrate(legacy);
        migrated.FallbackNccFloor.Should().Be(0.33,
            "Migrate must not silently zero out user customisations");
    }
}
```

- [ ] **Step 2: Confirm the tests fail (context does not exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~MapCalibrationLocateOptionsPersistence"`
Expected: CS0246 — `MapCalibrationLocateOptionsJsonContext` not found.

- [ ] **Step 3: Create the STJ source-gen context**

Create `src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptionsJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// System.Text.Json source-gen context for the persisted
/// <see cref="MapCalibrationLocateOptions"/>. Used by
/// <see cref="Mithril.Shared.Settings.JsonSettingsStore{T}"/> +
/// <see cref="Mithril.Shared.DependencyInjection.ServiceCollectionExtensions.AddMithrilVersionedSettings{T}"/>
/// to load/save <c>map-calibration-locate.json</c> with no reflection at runtime
/// (mithril#1061).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MapCalibrationLocateOptions))]
public partial class MapCalibrationLocateOptionsJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Tests pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~MapCalibrationLocateOptionsPersistence"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/MapCalibrationLocateOptionsJsonContext.cs \
        tests/Mithril.MapCalibration.Capture.Tests/MapCalibrationLocateOptionsPersistenceTests.cs
git commit -m "feat(map-calibration): STJ source-gen context + persistence tests for locate options (mithril#1061)"
```

### Task 1.9: Wire `AddMithrilVersionedSettings<MapCalibrationLocateOptions>` into Capture DI

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`
- Modify: `src/Mithril.Shell/DependencyInjection/ShellComposition.cs` (caller — thread the settings dir)

- [ ] **Step 1: Find the existing Capture extension signature**

Open `CaptureServiceCollectionExtensions.cs` and locate `AddMithrilMapCalibrationCapture` (search for the method). It currently takes `assetCacheDir`; we add a `settingsDir` parameter.

- [ ] **Step 2: Add the parameter + the persistence registration**

Change the signature and add the registration call at the top of the method body, **before** the call that delegates to Detection (`AddMithrilMapCalibrationDetection`):

```csharp
public static IServiceCollection AddMithrilMapCalibrationCapture(
    this IServiceCollection services,
    string assetCacheDir,
    string settingsDir,
    /* … existing args … */)
{
    if (string.IsNullOrWhiteSpace(settingsDir))
        throw new System.ArgumentException("settingsDir required", nameof(settingsDir));

    services.AddMithrilVersionedSettings<MapCalibrationLocateOptions>(
        settingsPath: System.IO.Path.Combine(settingsDir, "map-calibration-locate.json"),
        typeInfo: MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);

    // existing code (e.g. AddMithrilMapCalibrationDetection(assetCacheDir, ...))
    // — Detection's TryAddSingleton<MapCalibrationLocateOptions>() is now a no-op
    //   because we already pre-registered the singleton with the persisted backing.
    /* … */
}
```

Add the `using`:

```csharp
using Mithril.MapCalibration.Detection;
using Mithril.Shared.DependencyInjection;
```

(Check existing imports — `Mithril.MapCalibration.Detection` may already be imported transitively.)

- [ ] **Step 3: Find the Shell composition call site**

In `src/Mithril.Shell/DependencyInjection/ShellComposition.cs`, locate the `.AddMithrilMapCalibrationCapture(o.AssetCacheDir)` line (around line 150 per earlier grep). The `o` object already exposes paths for other settings (e.g. `o.PreferencesPath`); use the same convention.

Check whether `o` has a property like `o.SettingsDir`, `o.LocalAppData`, or `o.MithrilDataDir`. If yes, pass it; if no, add it to whatever options type `o` is and thread it through.

The plain pattern based on the existing `AddMithrilVersionedSettings<TelemetrySettings>` call (around line 189) is to compose the directory inline:

```csharp
var mithrilDataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Mithril");
// …
.AddMithrilMapCalibrationCapture(o.AssetCacheDir, mithrilDataDir)
```

— but copy whatever the existing telemetry/settings call already does. There is almost certainly a variable in scope already.

- [ ] **Step 4: Build + run the engine + DI tests**

Run: `dotnet build Mithril.slnx`
Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CaptureDependencyInjection"`
Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngine"`
Expected: all green.

- [ ] **Step 5: Verify the auto-saver fires end-to-end**

Append to `MapCalibrationLocateOptionsPersistenceTests.cs`:

```csharp
[Fact]
public async Task SettingsAutoSaver_writes_to_disk_within_debounce_window_after_property_change()
{
    var tmp = Path.Combine(Path.GetTempPath(), $"mithril-locate-test-{Guid.NewGuid():N}.json");
    try
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMithrilVersionedSettings<MapCalibrationLocateOptions>(
            tmp, MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
        await using var sp = services.BuildServiceProvider();

        // Start the hosted-service saver (AddMithrilVersionedSettings wires it).
        var hostedServices = sp.GetServices<IHostedService>();
        foreach (var hs in hostedServices)
            await hs.StartAsync(CancellationToken.None);

        var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
        opts.FallbackNccFloor = 0.42;

        // Allow the auto-saver's debounce to flush (the existing SettingsAutoSaver
        // implementation should expose a "flush now" API — use it here. If not,
        // fall back to a bounded wait + assert file exists with the new value).
        var saver = sp.GetRequiredService<SettingsAutoSaver<MapCalibrationLocateOptions>>();
        await saver.FlushAsync();  // — if this method doesn't exist, sleep briefly and check.

        var diskValue = JsonSerializer.Deserialize(
            File.ReadAllText(tmp),
            MapCalibrationLocateOptionsJsonContext.Default.MapCalibrationLocateOptions);
        diskValue!.FallbackNccFloor.Should().Be(0.42);

        foreach (var hs in hostedServices)
            await hs.StopAsync(CancellationToken.None);
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
```

If `SettingsAutoSaver<T>` lacks a `FlushAsync()` hook, look at how the existing `TelemetrySettings` auto-save round-trip is tested (search `tests/` for `SettingsAutoSaver`) and follow that pattern — do NOT introduce a new flush API in this PR if a different idiom already exists.

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SettingsAutoSaver_writes_to_disk"`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs \
        src/Mithril.Shell/DependencyInjection/ShellComposition.cs \
        tests/Mithril.MapCalibration.Capture.Tests/MapCalibrationLocateOptionsPersistenceTests.cs
git commit -m "feat(map-calibration): persist locate options via versioned settings store (mithril#1061)"
```

### Phase 1 Review Checkpoint

- All new types compile.
- Existing FM tests still green.
- `MapCalibrationLocateOptions` is now persisted as a versioned JSON; the file appears at `%LocalAppData%/Mithril/map-calibration-locate.json` after the first runtime change.
- No behaviour change yet — the new options/enums/helpers are unused at the refiner layer; defaults match the previous `const` values exactly.

---

## Phase 2 — `SobelPaddedPyramidRefiner`

### Task 2.1: Write the synthetic translation-recovery test

**Files:**
- Test: `tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs`

- [ ] **Step 1: Create the test file with the simplest translation case**

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class SobelPaddedPyramidRefinerTests
{
    private static SobelPaddedPyramidRefiner BuildRefiner(MapCalibrationLocateOptions? opts = null)
        => new(opts ?? new MapCalibrationLocateOptions());

    [Fact]
    public void Recovers_translation_when_capture_is_a_pasted_crop_at_known_origin()
    {
        // RichNoise has the high-frequency content Sobel-magnitude needs to lock
        // onto. PasteInto places the texture at (192, 100) inside a larger gray
        // background — the refiner must recover that origin.
        var texture = TestPatterns.RichNoise(width: 256, height: 256);
        var screenshot = TestPatterns.PasteInto(
            background: TestPatterns.UniformGray(640, 480, 128),
            foreground: texture,
            originX: 192, originY: 100);

        var result = BuildRefiner().Refine(screenshot, texture);

        result.AcceptedRect.Should().NotBeNull();
        result.AcceptedRect!.OriginX.Should().BeCloseTo(192, 2);
        result.AcceptedRect.OriginY.Should().BeCloseTo(100, 2);
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence.Should().NotBeNull();
        result.Metrics.Confidence!.Value.Should().BeGreaterThan(0.5);
    }
}
```

- [ ] **Step 2: Confirm the test fails (refiner does not exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SobelPaddedPyramidRefiner"`
Expected: CS0246 — `SobelPaddedPyramidRefiner` not found.

### Task 2.2: Implement `SobelPaddedPyramidRefiner` (minimal — fails on translation test, then iterate)

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs`

- [ ] **Step 1: Write the refiner**

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection.Internal;
using OpenCvSharp;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// <see cref="IMapRegionRefiner"/> using Sobel gradient magnitude + 100 px zero
/// padding + 3-level Gaussian pyramid + matchTemplate (CCoeffNormed) + parabolic
/// scale + sub-pixel translation refinement. The mithril#1061 fallback for
/// sparse-interior maps where <see cref="FeatureMatchingRefiner"/> (ORB+Lowe)
/// produces fewer than 4 Lowe survivors.
///
/// <para><b>Algorithm.</b> See <c>docs/planning/map-calibration-sparse-locate-fallback-1061/spec.md</c> §2.
/// The round-5 spike <c>tools/MapCalibrationFromScreenshot/SparseLocateSpike.cs::TemplateMatchSobelPaddedPyramid3</c>
/// is the algorithm reference; the production implementation is a clean
/// rewrite of the same pipeline.</para>
///
/// <para><b>Gate.</b> Refined NCC &lt; <see cref="MapCalibrationLocateOptions.FallbackNccFloor"/>
/// → <see cref="MapRegionRefineResult"/> with null <c>AcceptedRect</c>, the
/// <c>RawFitRect</c> + <c>Metrics</c> populated so the bundle and the engine's
/// reason copy are self-triaging.</para>
/// </summary>
public sealed class SobelPaddedPyramidRefiner : IMapRegionRefiner
{
    // All knobs live on MapCalibrationLocateOptions and persist via
    // map-calibration-locate.json (Phase 1.7-1.9). The refiner reads
    // _options.ScaleMin etc. so the user can tune without recompile.

    private readonly MapCalibrationLocateOptions _options;
    private readonly ILogger? _logger;

    public SobelPaddedPyramidRefiner(
        MapCalibrationLocateOptions options,
        ILogger<SobelPaddedPyramidRefiner>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
    {
        try
        {
            return RefineCore(capturedGray, baseTexture);
        }
        catch (OpenCVException ex)
        {
            _logger?.LogWarning(ex, "Sobel-padded-pyramid locate: OpenCV failure. Safe-degrade.");
            return MapRegionRefineResult.None;
        }
    }

    private MapRegionRefineResult RefineCore(GrayImage capturedGray, GrayImage baseTexture)
    {
        int pad = _options.FallbackPadPx;
        double scaleMin = _options.ScaleMin;
        double scaleMax = _options.ScaleMax;
        double scaleStep = _options.ScaleStep;
        int minDimFull = _options.MinScaledDim;
        int minDimHalf = _options.MinScaledDimHalf;
        int minDimCoarse = _options.MinScaledDimCoarse;

        using var capMat = ToMat8U(capturedGray);
        using var texMat = ToMat8U(baseTexture);
        using var capSobel = SobelMagnitudeHelpers.SobelMagnitude8U(capMat);
        using var texSobel = SobelMagnitudeHelpers.SobelMagnitude8U(texMat);
        using var capPadded = new Mat();
        Cv2.CopyMakeBorder(capSobel, capPadded, pad, pad, pad, pad,
            BorderTypes.Constant, Scalar.All(0));

        using var capL1 = new Mat(); Cv2.PyrDown(capPadded, capL1);
        using var capL2 = new Mat(); Cv2.PyrDown(capL1, capL2);
        using var texL1 = new Mat(); Cv2.PyrDown(texSobel, texL1);
        using var texL2 = new Mat(); Cv2.PyrDown(texL1, texL2);

        if (!TryFullLadder(capL2, texL2, minDimCoarse, scaleMin, scaleMax, scaleStep, out double l2Scale))
            return MapRegionRefineResult.None;

        if (!TryNarrowLadder(capL1, texL1, l2Scale, minDimHalf, scaleStep, out double l1Scale))
            return MapRegionRefineResult.None;

        var fineLadder = NarrowLadderWithLoc(capPadded, texSobel, l1Scale, minDimFull, scaleStep);
        if (fineLadder.Count == 0)
            return MapRegionRefineResult.None;

        int fineIdx = ArgMax(fineLadder);
        var fineWinner = fineLadder[fineIdx];
        double refinedScale = fineWinner.Scale;
        double refinedTx = fineWinner.Loc.X - pad;
        double refinedTy = fineWinner.Loc.Y - pad;
        double refinedNcc = fineWinner.Score;

        if (fineIdx > 0 && fineIdx < fineLadder.Count - 1)
        {
            double y1 = fineLadder[fineIdx - 1].Score;
            double y2 = fineLadder[fineIdx].Score;
            double y3 = fineLadder[fineIdx + 1].Score;
            double denom = y1 - 2 * y2 + y3;
            if (denom < -1e-9)
            {
                double subStep = 0.5 * (y1 - y3) / denom;
                if (Math.Abs(subStep) <= 1.0)
                {
                    double candidate = fineWinner.Scale + scaleStep * subStep;
                    int sw = (int)Math.Round(texSobel.Width * candidate);
                    int sh = (int)Math.Round(texSobel.Height * candidate);
                    if (sw >= minDimFull && sh >= minDimFull
                        && sw <= capPadded.Width && sh <= capPadded.Height)
                    {
                        using var scaled = new Mat();
                        Cv2.Resize(texSobel, scaled, new Size(sw, sh),
                            interpolation: InterpolationFlags.Area);
                        using var result = new Mat();
                        Cv2.MatchTemplate(capPadded, scaled, result, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
                        var (sdx, sdy) = SobelMagnitudeHelpers.RefineLocationSubPixel(result, maxLoc);
                        refinedScale = candidate;
                        refinedTx = maxLoc.X + sdx - pad;
                        refinedTy = maxLoc.Y + sdy - pad;
                        refinedNcc = maxVal;
                    }
                }
            }
        }

        int originX = (int)Math.Round(refinedTx);
        int originY = (int)Math.Round(refinedTy);
        int width = (int)Math.Round(texSobel.Width * refinedScale);
        int height = (int)Math.Round(texSobel.Height * refinedScale);

        var rawFit = new MapRect(
            OriginX: originX, OriginY: originY,
            Width: width, Height: height,
            TextureWidth: baseTexture.Width,
            TextureHeight: baseTexture.Height);

        var metrics = new LocateMetrics(
            InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
            Scale: refinedScale, RotationDegrees: 0, Mirror: false,
            Tx: refinedTx, Ty: refinedTy, ResidualPixels: 0,
            Provenance: LocateProvenance.SobelPaddedPyramid,
            Confidence: refinedNcc);

        if (refinedNcc < _options.FallbackNccFloor)
        {
            _logger?.LogInformation(
                "Sobel-padded-pyramid locate: rejected — NCC={Ncc:0.000} < floor={Floor:0.000} "
                + "(scale={Scale:0.000}, tx={Tx:0.0}, ty={Ty:0.0}).",
                refinedNcc, _options.FallbackNccFloor, refinedScale, refinedTx, refinedTy);
            return new MapRegionRefineResult(
                AcceptedRect: null, RawFitRect: rawFit, Metrics: metrics);
        }

        return new MapRegionRefineResult(
            AcceptedRect: rawFit, RawFitRect: rawFit, Metrics: metrics);
    }

    private static bool TryFullLadder(
        Mat cap, Mat tex, int minDim,
        double scaleMin, double scaleMax, double scaleStep,
        out double bestScale)
    {
        bestScale = 0;
        var ladder = new List<(double S, double Score)>(64);
        for (double s = scaleMin; s <= scaleMax + 1e-6; s += scaleStep)
        {
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladder.Add((s, maxVal));
        }
        if (ladder.Count == 0) return false;
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        bestScale = ladder[idx].S;
        return true;
    }

    private static bool TryNarrowLadder(
        Mat cap, Mat tex, double centreScale, int minDim, double scaleStep,
        out double bestScale)
    {
        bestScale = 0;
        var ladder = new List<(double S, double Score)>(8);
        for (double s = centreScale - 2 * scaleStep; s <= centreScale + 2 * scaleStep + 1e-6; s += scaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ladder.Add((s, maxVal));
        }
        if (ladder.Count == 0) return false;
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        bestScale = ladder[idx].S;
        return true;
    }

    private static List<(double Scale, double Score, Point Loc)> NarrowLadderWithLoc(
        Mat cap, Mat tex, double centreScale, int minDim, double scaleStep)
    {
        var ladder = new List<(double Scale, double Score, Point Loc)>(8);
        for (double s = centreScale - 2 * scaleStep; s <= centreScale + 2 * scaleStep + 1e-6; s += scaleStep)
        {
            if (s <= 0) continue;
            int sw = (int)Math.Round(tex.Width * s);
            int sh = (int)Math.Round(tex.Height * s);
            if (sw < minDim || sh < minDim || sw > cap.Width || sh > cap.Height) continue;
            using var scaled = new Mat();
            Cv2.Resize(tex, scaled, new Size(sw, sh), interpolation: InterpolationFlags.Area);
            using var result = new Mat();
            Cv2.MatchTemplate(cap, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);
            ladder.Add((s, maxVal, maxLoc));
        }
        return ladder;
    }

    private static int ArgMax(List<(double Scale, double Score, Point Loc)> ladder)
    {
        int idx = 0;
        for (int i = 1; i < ladder.Count; i++)
            if (ladder[i].Score > ladder[idx].Score) idx = i;
        return idx;
    }

    private static Mat ToMat8U(GrayImage g)
        => Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels).Clone();

    internal IReadOnlyList<double> LastLevelScales => _lastLevelScales;
    private double[] _lastLevelScales = System.Array.Empty<double>();
}
```

(The `LastLevelScales` field is a placeholder — Task 4.2 wires the bundle to read level scales; leave the field but it will be populated then. For now the field is unused; remove if it bothers the warnings-as-errors gate, and re-add in Task 4.2.)

**Actually,** to keep warnings-clean now, drop the `_lastLevelScales` field for this commit; it gets reintroduced in Task 4.2 with the bundle wiring.

- [ ] **Step 2: Run the synthetic test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SobelPaddedPyramidRefiner"`
Expected: `Recovers_translation_when_capture_is_a_pasted_crop_at_known_origin` passes.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs \
        tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs
git commit -m "feat(map-calibration): SobelPaddedPyramidRefiner v1 + synthetic translation test (mithril#1061)"
```

### Task 2.3: Add a half-scale recovery test

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs`

- [ ] **Step 1: Append a half-scale test**

```csharp
[Fact]
public void Recovers_half_scale_when_capture_is_a_downsampled_view()
{
    var texture = TestPatterns.RichNoise(width: 512, height: 512);
    var halved = TestPatterns.Resize(texture, 256, 256);

    var result = BuildRefiner().Refine(halved, texture);

    result.AcceptedRect.Should().NotBeNull();
    result.Metrics!.Scale.Should().BeApproximately(0.5, 0.05);
    result.Metrics.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~Recovers_half_scale"`
Expected: passes.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs
git commit -m "test(map-calibration): half-scale recovery test for fallback refiner (mithril#1061)"
```

### Task 2.4: Add a confidence-floor rejection test

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs`

- [ ] **Step 1: Append the rejection test**

```csharp
[Fact]
public void Rejects_when_inputs_are_unrelated_uniform_noise()
{
    // Two independent RichNoise patches — no structural overlap.
    // NCC peak should sit below the default floor (0.20).
    var texture = TestPatterns.RichNoise(width: 256, height: 256, seed: 1);
    var screenshot = TestPatterns.RichNoise(width: 640, height: 480, seed: 2);

    var result = BuildRefiner().Refine(screenshot, texture);

    result.AcceptedRect.Should().BeNull(
        "unrelated noise → NCC below floor → engine surfaces low-confidence reject");
    result.RawFitRect.Should().NotBeNull("raw fit is recorded even on rejection");
    result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
    result.Metrics.Confidence.Should().NotBeNull();
    result.Metrics.Confidence!.Value.Should().BeLessThan(0.20);
}

[Fact]
public void Accepts_with_lowered_floor_when_only_a_weak_fit_exists()
{
    // Same unrelated-noise scenario, but with the floor pushed to 0 — the refiner
    // accepts whatever the response map's best location is.
    var texture = TestPatterns.RichNoise(width: 256, height: 256, seed: 1);
    var screenshot = TestPatterns.RichNoise(width: 640, height: 480, seed: 2);
    var refiner = BuildRefiner(new MapCalibrationLocateOptions { FallbackNccFloor = 0.0 });

    var result = refiner.Refine(screenshot, texture);

    result.AcceptedRect.Should().NotBeNull();
}
```

**Note:** `TestPatterns.RichNoise` may not currently take a `seed` parameter — check `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestPatterns.cs`. If not, add a `seed: int = 0` overload that threads a `Random(seed)` through the pattern; if the existing helper is deterministic by signature only, generate two slightly different patches another way (e.g. one RichNoise + one UniformGray with very small perturbation).

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~SobelPaddedPyramidRefiner"`
Expected: 4 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestPatterns.cs  # if you modified RichNoise
git commit -m "test(map-calibration): NCC-floor accept + reject paths (mithril#1061)"
```

### Phase 2 Review Checkpoint

- New refiner produces sensible answers on synthetic inputs.
- Confidence floor correctly partitions accept vs. reject.
- Existing FM tests still pass.

---

## Phase 3 — `CompositeMapRegionRefiner` + DI swap

### Task 3.1: Write the composite-refiner dispatch tests

**Files:**
- Test: `tests/Mithril.MapCalibration.Capture.Tests/CompositeMapRegionRefinerTests.cs`

- [ ] **Step 1: Create the test file**

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CompositeMapRegionRefinerTests
{
    private static GrayImage Img(int w = 4, int h = 4) =>
        new(w, h, new byte[w * h]);

    private sealed class FakeRefiner : IMapRegionRefiner, IAreaContextualRefiner
    {
        public MapRegionRefineResult Next = MapRegionRefineResult.None;
        public int RefineCalls;
        public string? LastAreaKey;
        public int SetAreaKeyCalls;

        public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
        {
            RefineCalls++;
            return Next;
        }

        public void SetAreaKey(string? areaKey)
        {
            SetAreaKeyCalls++;
            LastAreaKey = areaKey;
        }
    }

    private static MapRect Rect() => new(0, 0, 4, 4, 4, 4);
    private static LocateMetrics OrbAcceptMetrics() => new(
        InlierCount: 100, CandidateCount: 120, InlierRatio: 0.83,
        Scale: 1.0, RotationDegrees: 0, Mirror: false,
        Tx: 0, Ty: 0, ResidualPixels: 0.5,
        Provenance: LocateProvenance.OrbRansac, Confidence: null);
    private static LocateMetrics NccAcceptMetrics(double ncc) => new(
        InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
        Scale: 1.0, RotationDegrees: 0, Mirror: false,
        Tx: 0, Ty: 0, ResidualPixels: 0,
        Provenance: LocateProvenance.SobelPaddedPyramid, Confidence: ncc);

    [Fact]
    public void Returns_primary_result_when_primary_accepts()
    {
        var primary = new FakeRefiner { Next = new(Rect(), Rect(), OrbAcceptMetrics()) };
        var fallback = new FakeRefiner();
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.OrbRansac);
        primary.RefineCalls.Should().Be(1);
        fallback.RefineCalls.Should().Be(0);
    }

    [Fact]
    public void Falls_through_to_fallback_when_primary_returns_none()
    {
        var primary = new FakeRefiner { Next = MapRegionRefineResult.None };
        var fallback = new FakeRefiner { Next = new(Rect(), Rect(), NccAcceptMetrics(0.5)) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        primary.RefineCalls.Should().Be(1);
        fallback.RefineCalls.Should().Be(1);
    }

    [Fact]
    public void Falls_through_when_primary_rejects_with_metrics_but_no_accepted_rect()
    {
        // Primary populated RawFitRect + Metrics but gate rejected → still falls through.
        var rejectMetrics = OrbAcceptMetrics() with { InlierCount = 2, InlierRatio = 0.10 };
        var primary = new FakeRefiner { Next = new(null, Rect(), rejectMetrics) };
        var fallback = new FakeRefiner { Next = new(Rect(), Rect(), NccAcceptMetrics(0.6)) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        fallback.RefineCalls.Should().Be(1);
    }

    [Fact]
    public void Surfaces_fallback_rejection_when_neither_branch_accepts()
    {
        var primary = new FakeRefiner { Next = MapRegionRefineResult.None };
        var rejectMetrics = NccAcceptMetrics(0.10);
        var fallback = new FakeRefiner { Next = new(null, Rect(), rejectMetrics) };
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        var result = composite.Refine(Img(), Img());

        result.AcceptedRect.Should().BeNull();
        result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
        result.Metrics.Confidence!.Value.Should().BeLessThan(0.20);
    }

    [Fact]
    public void Forwards_SetAreaKey_to_both_inner_refiners_that_support_it()
    {
        var primary = new FakeRefiner();
        var fallback = new FakeRefiner();
        var composite = new CompositeMapRegionRefiner(primary, fallback);

        composite.SetAreaKey("Map_GoblinDungeon");

        primary.LastAreaKey.Should().Be("Map_GoblinDungeon");
        fallback.LastAreaKey.Should().Be("Map_GoblinDungeon");
    }
}
```

- [ ] **Step 2: Confirm tests fail (composite does not exist)**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CompositeMapRegionRefiner"`
Expected: CS0246 — `CompositeMapRegionRefiner` not found.

### Task 3.2: Implement `CompositeMapRegionRefiner`

**Files:**
- Create: `src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs`

- [ ] **Step 1: Write the dispatcher**

```csharp
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Two-stage <see cref="IMapRegionRefiner"/>: try the primary; if it returns
/// no <see cref="MapRegionRefineResult.AcceptedRect"/> (whether "no fit at all"
/// or "fit produced but gate rejected"), run the fallback and return its
/// result. The mithril#1061 dispatcher: primary = ORB+Lowe, fallback =
/// Sobel-padded-pyramid.
///
/// <para><b>Area-context forwarding.</b> Implements
/// <see cref="IAreaContextualRefiner"/> so the engine can call
/// <see cref="SetAreaKey"/> without knowing about the composition. The call
/// forwards to whichever inner refiners implement
/// <see cref="IAreaContextualRefiner"/> (currently only the FM primary uses
/// per-area state, but the contract symmetrically supports either branch).</para>
/// </summary>
public sealed class CompositeMapRegionRefiner : IMapRegionRefiner, IAreaContextualRefiner
{
    private readonly IMapRegionRefiner _primary;
    private readonly IMapRegionRefiner _fallback;
    private readonly ILogger? _logger;

    public CompositeMapRegionRefiner(
        IMapRegionRefiner primary,
        IMapRegionRefiner fallback,
        ILogger<CompositeMapRegionRefiner>? logger = null)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
    {
        var primary = _primary.Refine(capturedGray, baseTexture);
        if (primary.AcceptedRect is not null) return primary;

        _logger?.LogInformation(
            "Composite locate: primary did not accept (raw fit {HasFit}); trying fallback.",
            primary.RawFitRect is not null);
        return _fallback.Refine(capturedGray, baseTexture);
    }

    public void SetAreaKey(string? areaKey)
    {
        if (_primary is IAreaContextualRefiner p) p.SetAreaKey(areaKey);
        if (_fallback is IAreaContextualRefiner f) f.SetAreaKey(areaKey);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CompositeMapRegionRefiner"`
Expected: all 5 dispatch tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs \
        tests/Mithril.MapCalibration.Capture.Tests/CompositeMapRegionRefinerTests.cs
git commit -m "feat(map-calibration): CompositeMapRegionRefiner dispatcher (mithril#1061)"
```

### Task 3.3: Switch DI to register the composite

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs`

- [ ] **Step 1: Replace the IMapRegionRefiner registration**

Find the existing block (around line 71):

```csharp
services.AddSingleton<IMapRegionRefiner>(sp =>
    new FeatureMatchingRefiner(
        options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
        logger: sp.GetService<ILogger<FeatureMatchingRefiner>>(),
        cachedDescriptors: sp.GetService<CachedOrbDescriptorProvider>(),
        writer: sp.GetService<OrbDescriptorWriter>()));
```

Replace with:

```csharp
services.AddSingleton<FeatureMatchingRefiner>(sp =>
    new FeatureMatchingRefiner(
        options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
        logger: sp.GetService<ILogger<FeatureMatchingRefiner>>(),
        cachedDescriptors: sp.GetService<CachedOrbDescriptorProvider>(),
        writer: sp.GetService<OrbDescriptorWriter>()));
services.AddSingleton<SobelPaddedPyramidRefiner>(sp =>
    new SobelPaddedPyramidRefiner(
        options: sp.GetRequiredService<MapCalibrationLocateOptions>(),
        logger: sp.GetService<ILogger<SobelPaddedPyramidRefiner>>()));
services.AddSingleton<IMapRegionRefiner>(sp =>
    new CompositeMapRegionRefiner(
        primary: sp.GetRequiredService<FeatureMatchingRefiner>(),
        fallback: sp.GetRequiredService<SobelPaddedPyramidRefiner>(),
        logger: sp.GetService<ILogger<CompositeMapRegionRefiner>>()));
```

The internal `FeatureMatchingRefiner` constructor is internal — check that the existing call site already compiles inside this project; if it's reached via `InternalsVisibleTo`, no change. The above instantiates with the same internal constructor as before.

- [ ] **Step 2: Build the solution**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds.

- [ ] **Step 3: Run the detection DI tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CaptureDependencyInjection"`
Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~Detection.EngineRegistrationTests"`
Expected: green — the `IMapRegionRefiner` singleton resolves; it's now `CompositeMapRegionRefiner` but consumers only see the interface.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/DependencyInjection/DetectionServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): DI wires composite refiner (FM primary + Sobel fallback) (mithril#1061)"
```

### Task 3.4: Engine cast — `is FeatureMatchingRefiner` → `is IAreaContextualRefiner`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`

- [ ] **Step 1: Replace the two casts**

There are two sites — `CheckDriftAsync` (around line 221) and `RunAttemptCoreAsync` (around line 500).

Before:

```csharp
if (_refiner is FeatureMatchingRefiner fmDrift)
    fmDrift.SetAreaKey(sceneRef.ParentAreaKey);
```

After:

```csharp
if (_refiner is IAreaContextualRefiner refinerCtx)
    refinerCtx.SetAreaKey(sceneRef.ParentAreaKey);
```

And:

```csharp
if (_refiner is FeatureMatchingRefiner fmRefiner)
{
    fmRefiner.SetAreaKey(area);
}
```

After:

```csharp
if (_refiner is IAreaContextualRefiner refinerCtx)
{
    refinerCtx.SetAreaKey(area);
}
```

Add the `using Mithril.MapCalibration.Detection;` import if not already present (it already is — `FeatureMatchingRefiner` reference was there).

- [ ] **Step 2: Run the engine tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngine"`
Expected: green. The ORB-descriptor cache pre-warm still fires (composite forwards to FM); behaviour unchanged for the happy path.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs
git commit -m "refactor(map-calibration): engine probes IAreaContextualRefiner not concrete FM (mithril#1061)"
```

### Phase 3 Review Checkpoint

- Engine no longer hard-couples to a concrete refiner.
- Composite dispatches correctly; FM cache pre-warm still fires.
- Full test suite green.

---

## Phase 4 — Diagnostic bundle schema additions

### Task 4.1: Extend `LocatorBestJson` to schema v2

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`

- [ ] **Step 1: Add the new fields with defaults**

Replace the `LocatorBestJson` record with:

```csharp
/// <summary>
/// Carries the locator's raw fit rect (gate-pass-or-not), the per-algorithm
/// metrics, and the gate verdict that drove the engine's outcome.
/// <para><b>Schema v2 (mithril#1061):</b> adds <see cref="Algorithm"/>,
/// <see cref="FallbackNcc"/>, <see cref="PadPx"/>, <see cref="LevelScales"/>.
/// Readers should treat absence of these as v1 ORB-only.</para>
/// </summary>
public sealed record LocatorBestJson(
    int SchemaVersion,
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight,
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    double Tx,
    double Ty,
    double ResidualPixels,
    bool GateAccepted,
    string? GateRejectReason,
    string Algorithm = "orb-lowe",
    double? FallbackNcc = null,
    int? PadPx = null,
    double[]? LevelScales = null);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`
Expected: succeeds — record's positional ctor stays backward-compatible for existing call sites.

### Task 4.2: Populate the new fields in the bundle sink

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`
- Modify: `src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs` (re-introduce + populate `LastLevelScales`)

- [ ] **Step 1: Re-introduce `LastLevelScales` on the refiner**

In `SobelPaddedPyramidRefiner.cs`, add a private mutable field and an internal accessor (used only by the bundle sink — keep narrow):

```csharp
private double[] _lastLevelScales = System.Array.Empty<double>();

/// <summary>
/// Quarter / half / refined-full scale winners from the most recent
/// <see cref="Refine"/> call, in order [L2, L1, refined]. Used by the
/// diagnostic bundle to surface where the pyramid landed for triage —
/// thread-unsafe / single-attempt-at-a-time by construction (same
/// constraint as <see cref="FeatureMatchingRefiner.SetAreaKey"/>).
/// </summary>
internal IReadOnlyList<double> LastLevelScales => _lastLevelScales;
```

In `RefineCore`, after determining `l2Scale`, `l1Scale`, and `refinedScale`:

```csharp
_lastLevelScales = new[] { l2Scale, l1Scale, refinedScale };
```

(Set this on both the accept and the floor-reject return paths.)

- [ ] **Step 2: Find the sink call site that writes `LocatorBestJson`**

In `FilesystemCalibrationAttemptBundleSink.cs`, locate the `WriteAttemptJson` method (or wherever `LocatorBestJson` is constructed — search for `new LocatorBestJson(`). Use `Grep` if needed.

- [ ] **Step 3: Pass-through new fields from `attempt.LocatorMetrics`**

When constructing `LocatorBestJson`, branch on `attempt.LocatorMetrics?.Provenance`:

```csharp
string algorithm = attempt.LocatorMetrics?.Provenance == LocateProvenance.SobelPaddedPyramid
    ? "sobel-padded-pyramid"
    : "orb-lowe";
double? fallbackNcc = attempt.LocatorMetrics?.Provenance == LocateProvenance.SobelPaddedPyramid
    ? attempt.LocatorMetrics?.Confidence
    : null;
int? padPx = attempt.LocatorMetrics?.Provenance == LocateProvenance.SobelPaddedPyramid
    ? options.FallbackPadPx
    : (int?)null;
double[]? levelScales = null;
// We don't currently surface level scales out of the refiner contract; if a
// composite refiner is in use, the fallback's LastLevelScales is reachable via
// a cast. Optional polish — leave null for v1.
```

For v1 of this task, leave `LevelScales = null`. (The level scales would require either widening the result type to carry them, or having the sink obtain them via the DI'd `SobelPaddedPyramidRefiner` singleton — the latter works because there's one instance per process. If choosing the latter, inject `SobelPaddedPyramidRefiner?` into the sink and read `LastLevelScales` post-attempt; mark a follow-up issue if you skip.)

Then construct the JSON:

```csharp
var locatorBest = new LocatorBestJson(
    SchemaVersion: 2,
    OriginX: rawFit.OriginX,
    OriginY: rawFit.OriginY,
    Width: rawFit.Width,
    Height: rawFit.Height,
    TextureWidth: rawFit.TextureWidth,
    TextureHeight: rawFit.TextureHeight,
    InlierCount: m.InlierCount,
    CandidateCount: m.CandidateCount,
    InlierRatio: m.InlierRatio,
    Scale: m.Scale,
    RotationDegrees: m.RotationDegrees,
    Tx: m.Tx,
    Ty: m.Ty,
    ResidualPixels: m.ResidualPixels,
    GateAccepted: attempt.MapRect is not null,
    GateRejectReason: attempt.Outcome == OutcomeVocabulary.RejectedMapNotLocated
        ? "no fit"
        : attempt.Outcome == OutcomeVocabulary.RejectedMapLowConfidence
            ? $"ncc={m.Confidence:0.000} < floor"
            : null,
    Algorithm: algorithm,
    FallbackNcc: fallbackNcc,
    PadPx: padPx,
    LevelScales: levelScales);
```

(The exact surrounding glue depends on the existing method shape — match it; the structure above is illustrative.)

- [ ] **Step 4: Add a v2 round-trip test**

Modify `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationBundleJsonTests.cs` — append:

```csharp
[Fact]
public void LocatorBestJson_round_trips_v2_fallback_fields()
{
    var json = new LocatorBestJson(
        SchemaVersion: 2,
        OriginX: 127, OriginY: 35,
        Width: 591, Height: 740,
        TextureWidth: 819, TextureHeight: 1024,
        InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
        Scale: 0.7227, RotationDegrees: 0, Tx: 127.5, Ty: 35.8, ResidualPixels: 0,
        GateAccepted: true, GateRejectReason: null,
        Algorithm: "sobel-padded-pyramid",
        FallbackNcc: 0.680,
        PadPx: 100,
        LevelScales: new[] { 0.70, 0.72, 0.7227 });

    var s = JsonSerializer.Serialize(json, CalibrationBundleJsonContext.Default.LocatorBestJson);
    var round = JsonSerializer.Deserialize(s, CalibrationBundleJsonContext.Default.LocatorBestJson);

    round.Should().NotBeNull();
    round!.Algorithm.Should().Be("sobel-padded-pyramid");
    round.FallbackNcc.Should().Be(0.680);
    round.PadPx.Should().Be(100);
    round.LevelScales.Should().Equal(0.70, 0.72, 0.7227);
}

[Fact]
public void LocatorBestJson_reads_v1_payload_with_default_orb_lowe_algorithm()
{
    // v1 payload — none of the new fields present.
    var v1Payload = """
    {
      "schemaVersion": 1,
      "originX": 0, "originY": 0,
      "width": 100, "height": 100,
      "textureWidth": 100, "textureHeight": 100,
      "inlierCount": 50, "candidateCount": 60, "inlierRatio": 0.83,
      "scale": 1.0, "rotationDegrees": 0.0,
      "tx": 0, "ty": 0, "residualPixels": 1.5,
      "gateAccepted": true, "gateRejectReason": null
    }
    """;

    var round = JsonSerializer.Deserialize(v1Payload, CalibrationBundleJsonContext.Default.LocatorBestJson);

    round.Should().NotBeNull();
    round!.Algorithm.Should().Be("orb-lowe");
    round.FallbackNcc.Should().BeNull();
    round.PadPx.Should().BeNull();
    round.LevelScales.Should().BeNull();
}
```

- [ ] **Step 5: Run the JSON tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationBundleJsonTests"`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs \
        src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs \
        src/Mithril.MapCalibration.Detection/SobelPaddedPyramidRefiner.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationBundleJsonTests.cs
git commit -m "feat(map-calibration): LocatorBestJson v2 — algorithm/NCC/pad/level fields (mithril#1061)"
```

### Phase 4 Review Checkpoint

- Bundle JSON shape is v2-ready; v1 payloads still parse.
- Sink populates new fields from `LocateMetrics.Provenance`.
- Round-trip tests prove forward and backward compatibility.

---

## Phase 5 — Corpus regression test (HogansKeep-223119)

### Task 5.1: Extract the corpus fixture

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/capture.png` (binary)
- Create: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/baseTexture.png` (binary)

- [ ] **Step 1: Locate the live bundle on disk**

In a PowerShell prompt:

```powershell
$root = "$env:LocalAppData\Mithril\diagnostics\calibration"
Get-ChildItem $root -Directory | Where-Object Name -like "*HogansKeep*223119*" | Select-Object FullName
```

You're looking for the bundle subdir whose name starts with `HogansKeepBasement-20260603-223119-` (or similar; the timestamp matches the issue's round-5 entry). The bundle contains `02-gray-screenshot.png` plus a separately-cached base texture.

- [ ] **Step 2: Copy the gray screenshot**

```powershell
Copy-Item "<bundle-subdir>/02-gray-screenshot.png" `
  "tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/capture.png"
```

- [ ] **Step 3: Copy the base texture**

The base texture for `Map_HogansKeepBasement` (or the relevant `Map_<X>` per the bundle's `01-attempt.json`) is cached at `%LocalAppData%/Mithril/assets/<map-asset-key>.png`. Find it via:

```powershell
$assetKey = (Get-Content "<bundle-subdir>/01-attempt.json" | ConvertFrom-Json).area
Copy-Item "$env:LocalAppData/Mithril/assets/$assetKey.png" `
  "tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/baseTexture.png"
```

Note: the bundle's `01-attempt.json`'s `area` field may be the per-scene `Map_<X>` key (post-#1041), or the parent area key — check the actual file. The base-texture filename uses the per-scene `Map_<X>` key.

- [ ] **Step 4: Ensure the PNGs are tracked + sized sanely**

Check sizes:

```powershell
Get-Item "tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/*.png" | Format-Table Name, Length
```

Both should be in the few-hundred-kB range. If larger than ~1 MB each, downsize is unnecessary (the test project pulls them at build time, not at every test); but if either exceeds a reasonable limit for the repo, recompress with `magick` or `oxipng`.

- [ ] **Step 5: Add the PNGs as content with copy-to-output**

In `tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="Fixtures/HogansKeep223119/*.png">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

(If the test project already has a generic `Fixtures/**` rule, this may be redundant — check and skip if so.)

- [ ] **Step 6: Commit the fixtures**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/Fixtures/HogansKeep223119/ \
        tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
git commit -m "test(map-calibration): HogansKeep-223119 corpus fixture (mithril#1061)"
```

### Task 5.2: Write the corpus regression test

**Files:**
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs`

- [ ] **Step 1: Append the regression test**

```csharp
[Fact]
public void Recovers_HogansKeep_223119_truth_from_corpus_bundle()
{
    // Truth from @arthur-conde's GIMP alignment in round-5 comment of #1061:
    //   (originX, originY, scale) = (126, 35, 0.7227)
    // Confidence: NCC ≥ 0.40 (round 5 measured 0.680 on this bundle).
    var capturePath = Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "HogansKeep223119", "capture.png");
    var texturePath = Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "HogansKeep223119", "baseTexture.png");

    var capture = PngFixtureLoader.LoadGray(capturePath);
    var texture = PngFixtureLoader.LoadGray(texturePath);

    var refiner = BuildRefiner();
    var result = refiner.Refine(capture, texture);

    result.AcceptedRect.Should().NotBeNull(
        "the converged algorithm recovers this corpus bundle with NCC > 0.40");
    result.Metrics!.Provenance.Should().Be(LocateProvenance.SobelPaddedPyramid);
    result.Metrics.Confidence.Should().NotBeNull();
    result.Metrics.Confidence!.Value.Should().BeGreaterThan(0.40);

    // (originX, originY) recovered within ±2 px of GIMP truth.
    result.AcceptedRect!.OriginX.Should().BeInRange(124, 128);
    result.AcceptedRect.OriginY.Should().BeInRange(33, 37);

    // Scale recovered within ±0.005 of truth 0.7227.
    result.Metrics.Scale.Should().BeApproximately(0.7227, 0.005);
}
```

- [ ] **Step 2: Run the regression test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~Recovers_HogansKeep_223119"`
Expected: PASS. If it fails, the implementation has diverged from the spike — debug by comparing the L2/L1/refined ladder winners against the round-5 measured values.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/SobelPaddedPyramidRefinerTests.cs
git commit -m "test(map-calibration): HogansKeep-223119 corpus regression (mithril#1061)"
```

### Phase 5 Review Checkpoint

- Corpus regression locks the converged algorithm against future drift.
- Truth `(126, 35, 0.7227)` recovered within tolerance.

---

## Phase 6 — Engine outcome routing + user-facing copy

### Task 6.1: Add `RejectedMapLowConfidence` outcome category

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`

- [ ] **Step 1: Add the constant**

After `RejectedMapNotLocated` (around line 18):

```csharp
public const string RejectedMapLowConfidence = "rejected-map-low-confidence";
```

- [ ] **Step 2: Verify `ShouldWriteBundle` doesn't need to change**

The new outcome is bundle-worthy (we want diagnostics for low-confidence rejects). It is NOT in `NoBundleOutcomes` — no change needed.

- [ ] **Step 3: Build**

Run: `dotnet build src/Mithril.MapCalibration.Capture/Mithril.MapCalibration.Capture.csproj`
Expected: succeeds.

- [ ] **Step 4: Add the vocabulary test**

`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/OutcomeVocabularyTests.cs` — append:

```csharp
[Fact]
public void RejectedMapLowConfidence_writes_a_bundle()
{
    OutcomeVocabulary.ShouldWriteBundle(OutcomeVocabulary.RejectedMapLowConfidence)
        .Should().BeTrue("low-confidence rejects are bundle-worthy for diagnostics");
}
```

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~OutcomeVocabulary"`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/OutcomeVocabularyTests.cs
git commit -m "feat(map-calibration): add RejectedMapLowConfidence outcome (mithril#1061)"
```

### Task 6.2: Engine branches outcome text on Provenance

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`

- [ ] **Step 1: Update the `mapRect is null` branch in `RunAttemptCoreAsync`**

Find the block around line 517:

```csharp
if (mapRect is null)
{
    attempt.Outcome = OutcomeVocabulary.RejectedMapNotLocated;
    if (refineResult.Metrics is { } m)
    {
        _logger?.LogInformation(
            "Auto-calibration {Area}: locate rejected — inliers={Inliers}/{Cand} ratio={Ratio:0.000}, scale={Scale:0.000}, rotation={Rot:0.000}°.",
            area, m.InlierCount, m.CandidateCount, m.InlierRatio, m.Scale, m.RotationDegrees);
    }
    else if (refineResult.RawFitRect is { } best)
    {
        _logger?.LogInformation(
            "Auto-calibration {Area}: locate rejected — raw fit rect at origin = ({X}, {Y}), size = {W}x{H}.",
            area, best.OriginX, best.OriginY, best.Width, best.Height);
    }
    return Fail(area, "couldn't locate the map in the captured frame — zoom the in-game map all the way out and draw the capture box tightly around the map", OutcomeVocabulary.RejectedMapNotLocated);
}
```

Replace with:

```csharp
if (mapRect is null)
{
    // Distinguish low-confidence fallback rejects (input pathology — try a
    // different zoom / explore more) from ORB primary's "no fit at all"
    // (framing problem — zoom out / re-draw the box).
    bool lowConfidenceFallback = refineResult.Metrics is { } mm
        && mm.Provenance == LocateProvenance.SobelPaddedPyramid
        && mm.Confidence is not null;

    if (refineResult.Metrics is { } m)
    {
        if (m.Provenance == LocateProvenance.SobelPaddedPyramid)
        {
            _logger?.LogInformation(
                "Auto-calibration {Area}: locate rejected — fallback NCC={Ncc:0.000} < floor, scale={Scale:0.000}, tx={Tx:0.0}, ty={Ty:0.0}.",
                area, m.Confidence ?? 0, m.Scale, m.Tx, m.Ty);
        }
        else
        {
            _logger?.LogInformation(
                "Auto-calibration {Area}: locate rejected — inliers={Inliers}/{Cand} ratio={Ratio:0.000}, scale={Scale:0.000}, rotation={Rot:0.000}°.",
                area, m.InlierCount, m.CandidateCount, m.InlierRatio, m.Scale, m.RotationDegrees);
        }
    }
    else if (refineResult.RawFitRect is { } best)
    {
        _logger?.LogInformation(
            "Auto-calibration {Area}: locate rejected — raw fit rect at origin = ({X}, {Y}), size = {W}x{H}.",
            area, best.OriginX, best.OriginY, best.Width, best.Height);
    }

    if (lowConfidenceFallback)
    {
        attempt.Outcome = OutcomeVocabulary.RejectedMapLowConfidence;
        return Fail(area,
            "couldn't locate the map confidently — try a different zoom or explore more of the area first",
            OutcomeVocabulary.RejectedMapLowConfidence);
    }

    attempt.Outcome = OutcomeVocabulary.RejectedMapNotLocated;
    return Fail(area,
        "couldn't locate the map in the captured frame — zoom the in-game map all the way out and draw the capture box tightly around the map",
        OutcomeVocabulary.RejectedMapNotLocated);
}
```

- [ ] **Step 2: Write the engine outcome test**

In `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineOutcomeCategoryTests.cs` (or wherever outcome routing is tested), add:

```csharp
[Fact]
public async Task Low_confidence_fallback_surfaces_RejectedMapLowConfidence()
{
    // Wire an engine whose refiner is a CompositeMapRegionRefiner with a
    // primary that always returns None and a fallback that returns
    // (null, rawFitRect, metrics-with-low-confidence). The engine should
    // emit RejectedMapLowConfidence, not RejectedMapNotLocated.
    var harness = EngineHarness.Build(refiner: new FakeLowConfidenceFallbackRefiner());
    var outcome = await harness.Engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

    outcome.OutcomeCategory.Should().Be(OutcomeVocabulary.RejectedMapLowConfidence);
    outcome.RejectReason.Should().Contain("try a different zoom");
}

private sealed class FakeLowConfidenceFallbackRefiner : IMapRegionRefiner
{
    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
        => new(
            AcceptedRect: null,
            RawFitRect: new MapRect(0, 0, 100, 100, 100, 100),
            Metrics: new LocateMetrics(
                InlierCount: 0, CandidateCount: 0, InlierRatio: 0,
                Scale: 0.5, RotationDegrees: 0, Mirror: false,
                Tx: 0, Ty: 0, ResidualPixels: 0,
                Provenance: LocateProvenance.SobelPaddedPyramid,
                Confidence: 0.10));
}
```

(`EngineHarness.Build` is the existing test fixture — check the signature. If it doesn't accept a refiner override, follow the local convention to wire one — see `tests/.../Fixtures/EngineHarness.cs`.)

- [ ] **Step 3: Run the engine outcome tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineOutcomeCategory"`
Expected: green, including the new low-confidence test.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineOutcomeCategoryTests.cs
git commit -m "feat(map-calibration): engine surfaces RejectedMapLowConfidence on fallback floor-reject (mithril#1061)"
```

### Task 6.3: Wire status-formatter routing (if it exists)

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationStatusFormatter.cs` (if it exists — check first)

- [ ] **Step 1: Check whether a status formatter routes by category**

Run: `grep -rn "RejectedMapNotLocated" src/ tests/`

If `CalibrationStatusFormatter.ForOutcome` (or similar) has a switch on `OutcomeCategory`, add a `RejectedMapLowConfidence` arm with copy:

> *"couldn't locate the map confidently — try a different zoom or explore more of the area first"*

If no such formatter exists, the `AutoCalibrationOutcome.RejectReason` already carries the copy from Task 6.2 and surfaces directly. Skip this task.

- [ ] **Step 2: If modified — run the formatter tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationStatusFormatter"`
Expected: green.

- [ ] **Step 3: Commit (only if modified)**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationStatusFormatter.cs \
        tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs
git commit -m "feat(map-calibration): status formatter routes RejectedMapLowConfidence (mithril#1061)"
```

### Phase 6 Review Checkpoint

- New outcome category is plumbed end-to-end.
- Low-confidence fallback rejects surface distinct copy from "no fit at all".
- The "zoom out + redraw box" guidance no longer fires on a believable-but-low-NCC fallback.

---

## Phase 7 — Telemetry

### Task 7.1: Emit primary/fallback spans inside the composite refiner

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs`

- [ ] **Step 1: Import the diagnostics source**

Add at the top:

```csharp
using Mithril.MapCalibration.Diagnostics;
```

- [ ] **Step 2: Wrap each branch in a span**

Replace `Refine` with:

```csharp
public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture)
{
    MapRegionRefineResult primary;
    using (var primaryAct = MapCalibrationDiagnostics.ActivitySource
        .StartActivity("calibration.refine.primary"))
    {
        primary = _primary.Refine(capturedGray, baseTexture);
        primaryAct?.SetTag("outcome",
            primary.AcceptedRect is not null ? "accepted"
            : primary.RawFitRect is not null ? "rejected"
            : "no_fit");
    }
    if (primary.AcceptedRect is not null) return primary;

    _logger?.LogInformation(
        "Composite locate: primary did not accept (raw fit {HasFit}); trying fallback.",
        primary.RawFitRect is not null);

    MapRegionRefineResult fallback;
    using (var fallbackAct = MapCalibrationDiagnostics.ActivitySource
        .StartActivity("calibration.refine.fallback"))
    {
        fallback = _fallback.Refine(capturedGray, baseTexture);
        if (fallback.Metrics is { } m)
        {
            if (m.Confidence is double ncc) fallbackAct?.SetTag("ncc", ncc);
            fallbackAct?.SetTag("scale", m.Scale);
        }
        fallbackAct?.SetTag("outcome",
            fallback.AcceptedRect is not null ? "accepted"
            : fallback.Metrics?.Confidence is double c && c < 0.20 ? "rejected_low_confidence"
            : fallback.RawFitRect is not null ? "rejected"
            : "no_fit");
    }
    return fallback;
}
```

(Note: the `0.20` literal in the tag is duplicated from the option default. Since `MapCalibrationLocateOptions` is now persisted (Phase 1.9), the literal can drift if the user customises `FallbackNccFloor` — prefer injecting `MapCalibrationLocateOptions` into the composite and reading `_options.FallbackNccFloor` for this tag classification. If the constructor surface change is unwelcome at this point in the plan, accept the literal-vs-knob drift for v1 — it only affects the *tag value* shown in Seq/OTLP, not behaviour — and file a follow-up to inject the options.)

- [ ] **Step 3: Build + run the composite tests**

Run: `dotnet build Mithril.slnx`
Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CompositeMapRegionRefiner"`
Expected: green. The span emit is zero-cost when no listener is attached.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs
git commit -m "feat(map-calibration): emit primary/fallback locate spans on composite (mithril#1061)"
```

### Task 7.2: Update `docs/perf-trace-schema.md`

**Files:**
- Modify: `docs/perf-trace-schema.md`

- [ ] **Step 1: Read the current shape**

Run: `Read docs/perf-trace-schema.md`

- [ ] **Step 2: Append the two new span definitions**

Add a `calibration.refine.primary` and `calibration.refine.fallback` entry in the appropriate section (match the existing format — usually a table of `event` × `properties`). At minimum:

| Span | Source | Tags | Notes |
|---|---|---|---|
| `calibration.refine.primary` | `Mithril.MapCalibration.Detection` | `outcome` ∈ `{accepted, rejected, no_fit}` | Wraps ORB+Lowe primary inside the composite (mithril#1061). |
| `calibration.refine.fallback` | `Mithril.MapCalibration.Detection` | `outcome` ∈ `{accepted, rejected, rejected_low_confidence, no_fit}`, `ncc` (double, fallback only), `scale` (double, fallback only) | Wraps Sobel-padded-pyramid fallback inside the composite (mithril#1061). |

- [ ] **Step 3: If a perf-tracer shape-contract test exists, update its fixtures**

Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~PerfTracer"`

If a byte-parity test fails because the new spans appear in a recorded sample, update the expected fixture. (If the test doesn't exercise the new spans, no change needed.)

- [ ] **Step 4: Commit**

```bash
git add docs/perf-trace-schema.md tests/Mithril.Shared.Tests/  # if any fixtures changed
git commit -m "docs(map-calibration): document fallback locate spans (mithril#1061)"
```

### Task 7.3 (optional): Add an algorithm-distribution metric

**Files:**
- Modify: `src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs`
- Modify: `src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs`

- [ ] **Step 1: Add the counter to the diagnostics catalog**

Append to the `Meters` static class:

```csharp
/// <summary>
/// Locate-stage attempts, broken down by which algorithm produced the
/// result. Tag: <c>algorithm</c> ∈ {<c>orb_lowe</c>, <c>sobel_padded_pyramid</c>}.
/// Lets the OTLP export surface "what fraction of attempts hit the fallback"
/// without parsing logs (mithril#1061).
/// </summary>
public static readonly Counter<long> LocateAlgorithm =
    Meter.CreateCounter<long>("mithril.map_calibration.locate.algorithm");
```

- [ ] **Step 2: Record from the composite**

Inside `Refine`, after the primary branch returns or the fallback branch completes, record:

```csharp
MapCalibrationDiagnostics.Meters.LocateAlgorithm.Add(1,
    new KeyValuePair<string, object?>("algorithm",
        primary.AcceptedRect is not null ? "orb_lowe" : "sobel_padded_pyramid"));
```

(Place the call once at the return path; bind the tag from whichever branch produced the final result.)

- [ ] **Step 3: Update the perf-trace doc with the metric**

Same `docs/perf-trace-schema.md` — append the metric to whatever table describes meter instruments.

- [ ] **Step 4: Build + run perf-tracer tests**

Run: `dotnet build Mithril.slnx`
Run: `dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~PerfTracer"`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs \
        src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs \
        docs/perf-trace-schema.md
git commit -m "feat(map-calibration): locate-algorithm counter for fallback share (mithril#1061)"
```

### Phase 7 Review Checkpoint

- Spans emit on both branches with the right tags.
- Perf-trace doc updated.
- Optional metric tracks fallback-hit-rate over time.

---

## Final Verification

- [ ] **Step 1: Full test suite green**

Run: `dotnet test Mithril.slnx`
Expected: green.

- [ ] **Step 2: Manual smoke**

Per CLAUDE.md "For UI or frontend changes, start the dev server and use the feature in a browser before reporting the task as complete" — calibration is an in-game hotkey-triggered flow, so the equivalent is: launch Mithril against PG (Steam) with a dungeon area on screen, hit the autocal hotkey, confirm a non-null `mapRect` lands and the bundle's `01-attempt.json` reports `algorithm: "sobel-padded-pyramid"` with NCC ≥ 0.40.

If PG is not available in the implementation environment, document that smoke is owed and flag the PR as "test plan: needs in-game verification" — do NOT claim success without the smoke.

- [ ] **Step 3: PR + issue cross-link**

Title: `feat(map-calibration): Sobel-padded-pyramid locate fallback for sparse interiors (#1061)`

Body should reference [docs/planning/map-calibration-sparse-locate-fallback-1061/spec.md](../map-calibration-sparse-locate-fallback-1061/spec.md), close #1061, and document any deferred follow-up (e.g. frame-validity precheck for late-pixel-capture pathology — file as new issue when this lands).

- [ ] **Step 4: Flip INDEX row to `shipped`**

After merge, edit `docs/planning/INDEX.md` to flip the slug's status from `active` → `shipped`, append the merged PR number alongside the issue.

---

## Out-of-Scope Reminders (do NOT add to this implementation)

- AKAZE / Generalized-Hough / Borgefors / phaseCorrelate — all ruled out in rounds 1–4 of the spike.
- Anisotropic `(sx, sy)` search — PG is empirically isotropic.
- Pin-anchor solver behaviour on dungeons — that is [#1036](https://github.com/moumantai-gg/mithril/issues/1036).
- Frame-validity precheck (minimum Sobel variance / Canny edge count) for late-pixel-capture pathology — file as separate issue if observed in practice.
- WolfCave-223519 1% scale residual — below the user-visible threshold; renderer-blur-aware kernel is the future lever if ever needed.
- Removing the spike harness — delete `tools/MapCalibrationFromScreenshot/SparseLocateSpike.cs` in a separate cleanup PR after this lands and bakes for a release cycle.
- **Promoting detect-pipeline constants to settings.** `RenderSizePx`, `LowNcc`, `TypeFloor`, `BlobOptions` in `AutoCalibrationEngine` are detect/solve concerns — file separately when their tuning surface becomes interesting.
- **A settings UI surface for these knobs.** The persisted file is dev/power-user-editable on disk for v1; a Settings panel binding to `MapCalibrationLocateOptions` is a follow-up.
- **Settings-file migration tooling.** First persisted version is v1, so there's nothing to migrate yet; `Migrate` is a documented stub for future schema bumps.
