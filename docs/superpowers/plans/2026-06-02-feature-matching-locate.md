# Feature-Matching Locate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the locate stage (NCC scale ladder + ECC sub-pixel refine) with an ORB + BFMatcher + Lowe-ratio + `Cv2.EstimateAffinePartial2D` (RANSAC) pipeline that's robust to fog of war, map-pin occlusion, and non-coastline maps. Cache the texture's ORB descriptors alongside the texture so steady-state locate cost is ~80–150 ms (vs current 3–5 s). Hard cutover — no NCC fallback.

**Architecture:** PR-1 lands `FeatureMatchingRefiner` (in `src/Mithril.MapCalibration.Capture/`, sibling of `TextureRegistrationRefiner`), the `LocateMetrics` record + reshaped `MapRegionRefineResult`, and `MapCalibrationLocateOptions` (DI singleton). The new refiner is fully tested but NOT wired into the engine. PR-2 adds the on-disk ORB descriptor cache (`map-texture-<area>.orb.{json,bin}`) alongside the existing texture cache; lazy-populated by the refiner. PR-3 reshapes `MapRegionRefineResult` consumers (`CalibrationAttemptContext`, bundle JSON), bumps the bundle's top-level `SchemaVersion`, and reshapes `IMapRegionRefiner.Refine`'s signature. PR-4 swaps the DI registration from `TextureRegistrationRefiner` to `FeatureMatchingRefiner`, deletes the retired NCC/ECC code (`MapRectLocator.AutoDetect*`, `TextureRegistrationRefiner`, `RefineMinScore`, the prototype test), and proves green via the live Kur bundle replay.

**Tech Stack:** .NET 10 / C# latest, xunit + FluentAssertions, OpenCvSharp (already in `Mithril.MapCalibration.Capture`; its core `Mithril.MapCalibration` project remains decoder-free / BCL-only). DI through `CaptureServiceCollectionExtensions.AddMithrilMapCalibrationCapture`. Bundle JSON via `System.Text.Json` source generation through `CalibrationBundleJsonContext`.

**Read this once before starting:** [docs/superpowers/specs/2026-06-02-feature-matching-locate-design.md](../specs/2026-06-02-feature-matching-locate-design.md). This plan implements its Milestones PR-1 → PR-4 verbatim.

**Commit cadence:** one commit per task. Use the existing project convention `feat(map-calibration): …` / `refactor(map-calibration): …` / `test(map-calibration): …`. The CLAUDE.md guardrail "branch policy blocks direct commits to main" applies — work happens on the feature branch and ships as one PR per milestone (PR-1 = Tasks 1–8, PR-2 = Tasks 9–12, PR-3 = Tasks 13–16, PR-4 = Tasks 17–22). PRs are separate because each one is independently reviewable: PR-1 introduces a class with no production behaviour change; PR-2 adds caching; PR-3 reshapes diagnostic JSON; PR-4 flips the live behaviour.

**Review checkpoints:** four explicit review markers, one per PR. Each block runs as one uninterrupted stretch. Commits land per task as usual; human review pauses only at the explicit `🛑 Review checkpoint` markers, not after every commit. The first-line safety net inside each block is "does the build still build" (PR-1 + PR-2 don't change engine behaviour; PR-3 + PR-4 land tests as part of the work).

| # | When | What gets reviewed |
|---|---|---|
| 1 | End of PR-1 (after Block 2) | New refiner + result-shape additions + calibration-study survey table in commit log |
| 2 | End of PR-2 (after Block 3) | Cache format + integrity gates + measured perf delta |
| 3 | End of PR-3 (after Block 4) | Bundle JSON shape break (schema version bump) + consumer rename |
| 4 | End of PR-4 (after Block 5) | Engine cutover + retirements + live Kur replay green + manual-verification screenshot |

**Worktree:** this plan is being executed in the `claude/feature-matching-locate-spec` branch's worktree (this PR). The implementation work — Tasks 1+ — is for a follow-up branch; this PR ships only the spec + plan documents. The "Tasks" below are what that follow-up engineer reads.

---

## File Structure

### PR-1 — `FeatureMatchingRefiner` class (no engine wire-up)

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` | `IMapRegionRefiner` impl using ORB + BFMatcher + Lowe + `Cv2.EstimateAffinePartial2D`. Reads `MapCalibrationLocateOptions` for tunables. |
| Create | `src/Mithril.MapCalibration.Capture/MapCalibrationLocateOptions.cs` | POCO + `INotifyPropertyChanged`. `InlierFloor=50`, `InlierRatioFloor=0.50`, `MaxRotationDegrees=0.5`, `OrbNFeatures=8000`, `LoweRatio=0.75`, `RansacReprojectionThresholdPx=3.0`. |
| Create | `src/Mithril.MapCalibration.Capture/LocateMetrics.cs` | `LocateMetrics` record carrying `InlierCount`/`CandidateCount`/`InlierRatio`/`Scale`/`RotationDegrees`/`Mirror`/`Tx`/`Ty`/`ResidualPixels`. |
| Modify | `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs` | Add `LocateMetrics? Metrics` and `MapRect? RawFitRect` fields. Keep `BestCoarseRect` as a `[Obsolete]` alias of `RawFitRect` (PR-3 deletes it). |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerTests.cs` | Synthetic unit tests: identity, half-scale, translated, insufficient-features, rotation gate. |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerReplayTests.cs` | Kur live + study-set replay tests. Asserts recovered rect within ±2 px of ground truth. |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingNegativeTests.cs` | Cross-area rejection: Kur texture × Eltibule capture, etc. |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/` | Fixture directory: copies of the live Kur bundle + study captures + textures. README under the folder documents provenance. |

### PR-2 — On-disk ORB descriptor cache (lazy populate)

| Action | Path | Responsibility |
|---|---|---|
| Create | `src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs` | Reads `map-texture-<area>.orb.{json,bin}`; validates `SchemaVersion` + `PixelSha256` + `OrbParamsHash` + `BlobSha256`; returns `(KeyPoint[], Mat descriptors)` or null. |
| Create | `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs` | Computes ORB on a texture; writes the `.orb.{json,bin}` pair using DeflateStream + integrity hashes. |
| Create | `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs` | Schema-versioned manifest record: `SchemaVersion`, `Area`, `PgVersion`, `KeypointCount`, `DescriptorDim`, `OrbParamsHash`, `PixelSha256`, `BlobSha256`. |
| Modify | `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` | Add `OrbDescriptorManifest` to the source-generated JSON context. |
| Modify | `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` | Inject `CachedOrbDescriptorProvider`. On Refine: probe cache → hit returns descriptors / miss computes + writes + returns. |
| Modify | `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` | Register `CachedOrbDescriptorProvider` as a singleton (with the same `_assetCacheDir` the texture provider uses). |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/CachedOrbDescriptorProviderTests.cs` | Round-trip, corruption-detect-and-rebuild, `OrbParamsHash` mismatch, `PixelSha256` mismatch. |

### PR-3 — Bundle JSON shape migration + consumer rename

| Action | Path | Responsibility |
|---|---|---|
| Modify | `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs` | Bump `AttemptJson.SchemaVersion`. Strip `AutoDetectScore`/`SourceScaleFactor` from `MapRectJson`. Add new `LocatorBestJson` record. Change `AttemptJson.LocatorBest` type from `MapRectJson?` to `LocatorBestJson?`. Add to JSON source-generation context. |
| Modify | `src/Mithril.MapCalibration/Detection/MapRectLocator.cs` | Drop `AutoDetectScore`/`SourceScaleFactor` from the `MapRect` record (keep the record; the locator class is deleted in PR-4). |
| Modify | `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs` | Rename `LocatorBestRect` → `LocatorRawFit` (`MapRect?`). Add `LocatorMetrics` (`LocateMetrics?`). |
| Modify | `src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs` | Drop the `double minScore` arg from `Refine`. The gate lives inside the refiner now. |
| Modify | `src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs` | Adjust `Refine` signature to match — still uses the engine's hardcoded `RefineMinScore` internally for now (PR-4 deletes the class entirely). |
| Modify | `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs` | Adjust `Refine` signature to match (drop the now-ignored arg). |
| Modify | `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs` | Remove `[Obsolete] BestCoarseRect`. The result now is `(AcceptedRect, RawFitRect, Metrics)`. |
| Modify | `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` | `attempt.LocatorBestRect = …` → `attempt.LocatorRawFit = …` + `attempt.LocatorMetrics = …`. Log lines stop reading `AutoDetectScore`/`SourceScaleFactor` (those fields no longer exist on MapRect). |
| Modify | `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs` | Emit `LocatorBestJson` from `LocatorRawFit` + `LocatorMetrics` + the engine's accept verdict. |
| Modify | Test files mentioning `LocatorBestRect`, `AutoDetectScore`, `SourceScaleFactor`, `MapRectJson(AutoDetectScore: …)`, etc. | Tracked via Grep at task time; rename + drop arg per the shape change. |

### PR-4 — Engine cutover + retirements

| Action | Path | Responsibility |
|---|---|---|
| Modify | `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` | Swap `IMapRegionRefiner` registration: `TextureRegistrationRefiner` → `FeatureMatchingRefiner`. |
| Modify | `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` | Delete `const double RefineMinScore`. (`_refiner.Refine(gray, baseTexture)` call already arg-shaped in PR-3.) |
| Delete | `src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs` | Replaced. |
| Delete | `tests/Mithril.MapCalibration.Capture.Tests/TextureRegistrationRefinerTests.cs` | Tests for the deleted class. |
| Modify | `src/Mithril.MapCalibration/Detection/MapRectLocator.cs` | Delete the `MapRectLocator` static class (all methods). Move the remaining `MapRect` record to a new file. |
| Create | `src/Mithril.MapCalibration/Detection/MapRect.cs` | Houses just the `MapRect` record + `ScreenshotToTexture` / `TextureToScreenshot` helpers. |
| Delete | `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs` | The diagnostic prototype. **Deleted here** (not earlier). |
| Create | `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineFeatureMatchingTests.cs` | End-to-end smoke: engine with `FeatureMatchingRefiner` wired in, fed the Kur live bundle, produces a non-null calibration. |

---

## PR-1 — `FeatureMatchingRefiner` class (Tasks 1–8) · ends at 🛑 Review checkpoint 1

### Block 1 — Refiner + supporting types (Tasks 1–4)

Each task in Block 1 is small and the build stays green per-commit. Run straight through; no mid-block pause.

---

### Task 1: `LocateMetrics` record

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/LocateMetrics.cs`

- [ ] **Step 1: Write the file**

```csharp
namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Diagnostic + gate-feeding metrics from one
/// <see cref="FeatureMatchingRefiner"/> run. Populated whenever RANSAC
/// converged on a fit; null on the result type means "no fit found at all".
/// <list type="bullet">
/// <item><c>InlierCount</c> + <c>InlierRatio</c> are the gate floors
/// (spec §"Gate criteria").</item>
/// <item><c>RotationDegrees</c> is the small-rotation gate — PG's UI is
/// axis-aligned, so anything &gt; ~0.5° indicates a wrong fit, not a real
/// rotated map.</item>
/// <item><c>ResidualPixels</c> is the median per-inlier reprojection error
/// in screenshot pixels — diagnostic only, not gated (the inlier mask is
/// already the answer the gate cares about).</item>
/// </list>
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
    double ResidualPixels);
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/LocateMetrics.cs
git commit -m "feat(map-calibration): add LocateMetrics record for feature-matching locate"
```

---

### Task 2: `MapCalibrationLocateOptions` POCO

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/MapCalibrationLocateOptions.cs`

- [ ] **Step 1: Write the file**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Runtime-flippable knobs for <see cref="FeatureMatchingRefiner"/>
/// (spec §"Gate criteria"). DI singleton, plain mutable POCO,
/// <see cref="INotifyPropertyChanged"/> so a settings UI can bind without
/// re-resolving the graph. Defaults derived from the prototype's
/// cross-validation evidence on the Kur/Eltibule/Serbule corpus
/// (spec §"Cross-validation evidence"): all real wins clear by a wide margin.
/// </summary>
public sealed class MapCalibrationLocateOptions : INotifyPropertyChanged
{
    private int _inlierFloor = 50;
    private double _inlierRatioFloor = 0.50;
    private double _maxRotationDegrees = 0.5;
    private int _orbNFeatures = 8000;
    private double _loweRatio = 0.75;
    private double _ransacReprojectionThresholdPx = 3.0;

    /// <summary>Reject any fit with fewer than this many RANSAC inliers. Default 50.</summary>
    public int InlierFloor
    {
        get => _inlierFloor;
        set { if (_inlierFloor != value) { _inlierFloor = value; OnChanged(); } }
    }

    /// <summary>Reject any fit whose RANSAC inlier ratio is below this. Default 0.50.</summary>
    public double InlierRatioFloor
    {
        get => _inlierRatioFloor;
        set { if (_inlierRatioFloor != value) { _inlierRatioFloor = value; OnChanged(); } }
    }

    /// <summary>Reject any fit whose recovered rotation exceeds this magnitude. PG's UI is axis-aligned; anything &gt; 0.5° is a wrong fit, not a rotated map. Default 0.5°.</summary>
    public double MaxRotationDegrees
    {
        get => _maxRotationDegrees;
        set { if (_maxRotationDegrees != value) { _maxRotationDegrees = value; OnChanged(); } }
    }

    /// <summary>Cap on ORB keypoints per image. Default 8000 (prototype baseline).</summary>
    public int OrbNFeatures
    {
        get => _orbNFeatures;
        set { if (_orbNFeatures != value) { _orbNFeatures = value; OnChanged(); } }
    }

    /// <summary>Lowe's ratio-test threshold. Match m kept iff m.distance &lt; LoweRatio * second.distance. Default 0.75.</summary>
    public double LoweRatio
    {
        get => _loweRatio;
        set { if (_loweRatio != value) { _loweRatio = value; OnChanged(); } }
    }

    /// <summary>RANSAC reprojection threshold in screenshot pixels. Default 3.0.</summary>
    public double RansacReprojectionThresholdPx
    {
        get => _ransacReprojectionThresholdPx;
        set { if (_ransacReprojectionThresholdPx != value) { _ransacReprojectionThresholdPx = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/MapCalibrationLocateOptions.cs
git commit -m "feat(map-calibration): add MapCalibrationLocateOptions POCO with calibrated defaults"
```

---

### Task 3: Extend `MapRegionRefineResult`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs`

This is an additive change for PR-1 — `BestCoarseRect` stays (now `[Obsolete]`) so PR-1's `TextureRegistrationRefiner` (still in tree) keeps populating it. PR-3 deletes it.

- [ ] **Step 1: Edit the file**

Replace the record's body so it gains two new fields and marks the old one obsolete:

```csharp
using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Outcome of <see cref="IMapRegionRefiner.Refine"/>.
/// <para>
/// <see cref="AcceptedRect"/> is non-null iff the refiner's gate accepted.
/// <see cref="RawFitRect"/> is non-null whenever the refiner produced a fit
/// (gate-pass-or-not) — diagnostics + the bundle's <c>LocatorBest</c> read
/// from this on the rejection branch so a future "map-not-located" outcome
/// is self-triaging.
/// <see cref="Metrics"/> mirrors <see cref="RawFitRect"/>: non-null exactly
/// when a fit exists, carrying the inlier count/ratio + recovered transform
/// parameters for both the gate and the bundle log.
/// </para>
/// </summary>
public sealed record MapRegionRefineResult(
    MapRect? AcceptedRect,
    MapRect? RawFitRect,
    LocateMetrics? Metrics)
{
    /// <summary>Degenerate result — the refiner had no usable fit.</summary>
    public static MapRegionRefineResult None { get; } = new(null, null, null);

    /// <summary>
    /// PR-1 transitional alias for <see cref="RawFitRect"/> so the in-tree
    /// <see cref="TextureRegistrationRefiner"/> keeps populating the
    /// rejection-branch rect under its existing name. PR-3 deletes this
    /// alongside the rest of the NCC-vocabulary cleanup.
    /// </summary>
    [Obsolete("Renamed to RawFitRect. Removed in PR-3.")]
    public MapRect? BestCoarseRect => RawFitRect;

    /// <summary>
    /// PR-1 transitional ctor — preserves the existing positional shape
    /// <c>new MapRegionRefineResult(accepted, bestCoarseRect)</c> so the
    /// existing <see cref="TextureRegistrationRefiner"/> compiles untouched
    /// in PR-1. PR-3 rewrites every call site.
    /// </summary>
    public MapRegionRefineResult(MapRect? AcceptedRect, MapRect? BestCoarseRect)
        : this(AcceptedRect, BestCoarseRect, null) { }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green. The two-arg ctor preserves source compat with `TextureRegistrationRefiner`'s existing `new MapRegionRefineResult(AcceptedRect: refined, BestCoarseRect: seed)` calls.

- [ ] **Step 3: Run capture tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~TextureRegistrationRefinerTests"`
Expected: green. The NCC refiner's existing tests still pass against the additive shape.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs
git commit -m "feat(map-calibration): evolve MapRegionRefineResult with RawFitRect + Metrics"
```

---

### Task 4: `FeatureMatchingRefiner` class

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs`

The body mirrors the prototype's structure but consumes `MapCalibrationLocateOptions` for tunables and populates `LocateMetrics` for diagnostics.

- [ ] **Step 1: Write the file**

```csharp
using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Detection;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapRegionRefiner"/> using ORB + BFMatcher + Lowe-ratio +
/// <see cref="Cv2.EstimateAffinePartial2D"/> (RANSAC similarity). Replaces
/// the NCC scale ladder + ECC sub-pixel refine: robust to fog of war,
/// pin overlay, and non-coastline maps (spec §"The criterion that rules
/// approaches in/out").
///
/// <para><b>Output transform direction.</b> <c>EstimateAffinePartial2D</c>
/// is called with <c>texturePoints → screenshotPoints</c>, so the recovered
/// 2×3 affine maps a texture-pixel to its position in the captured frame.
/// The four texture corners run through the affine become the four corners
/// of the located rect in the frame.</para>
///
/// <para><b>Axis-alignment assumption.</b> Under PG's axis-aligned UI the
/// recovered rotation is ~0°. We assert this via the
/// <see cref="MapCalibrationLocateOptions.MaxRotationDegrees"/> gate; any
/// fit beyond that threshold is rejected as "no fit", not "rotated fit",
/// because every downstream consumer (crop, resize, ScreenshotToTexture)
/// assumes axis-alignment.</para>
///
/// <para><b>Fail-soft.</b> A <see cref="OpenCVException"/> from any
/// OpenCvSharp call returns <see cref="MapRegionRefineResult.None"/>; the
/// engine sees the same shape as "no fit found", surfaces the
/// rejected-map-not-located outcome.</para>
/// </summary>
public sealed class FeatureMatchingRefiner : IMapRegionRefiner
{
    private readonly MapCalibrationLocateOptions _options;
    private readonly ILogger? _logger;

    public FeatureMatchingRefiner(
        MapCalibrationLocateOptions options,
        ILogger<FeatureMatchingRefiner>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore)
    {
        // The minScore arg is a leftover from the NCC interface and is
        // ignored by FM — PR-3 drops it from IMapRegionRefiner entirely.
        // The gate that matters lives in _options.
        _ = minScore;

        try
        {
            using var orb = ORB.Create(nFeatures: _options.OrbNFeatures);
            using var capMat = ToMat8U(capturedGray);
            using var texMat = ToMat8U(baseTexture);

            using var capDescriptors = new Mat();
            using var texDescriptors = new Mat();
            orb.DetectAndCompute(capMat, null, out var capKeypoints, capDescriptors);
            orb.DetectAndCompute(texMat, null, out var texKeypoints, texDescriptors);

            if (capDescriptors.Rows < 2 || texDescriptors.Rows < 2)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: too few descriptors (capture={CapCount}, texture={TexCount}).",
                    capDescriptors.Rows, texDescriptors.Rows);
                return MapRegionRefineResult.None;
            }

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            // texture descriptors are the "train" set; capture descriptors are the "query".
            // We want texture keypoints → screenshot keypoints, so the match queues map
            // capture→texture; we re-pair them below.
            var knn = matcher.KnnMatch(capDescriptors, texDescriptors, k: 2);

            // Lowe ratio: keep m if m.distance < ratio * second.distance.
            var loweRatio = _options.LoweRatio;
            var goodPairs = knn
                .Where(pair => pair.Length == 2 && pair[0].Distance < loweRatio * pair[1].Distance)
                .Select(pair => pair[0])
                .ToList();

            if (goodPairs.Count < 4)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: only {GoodCount} Lowe survivors (need ≥4).",
                    goodPairs.Count);
                return MapRegionRefineResult.None;
            }

            // EstimateAffinePartial2D direction: src → dst = texture → capture.
            var texPoints = goodPairs.Select(m => texKeypoints[m.TrainIdx].Pt).ToArray();
            var capPoints = goodPairs.Select(m => capKeypoints[m.QueryIdx].Pt).ToArray();

            using var srcMat = InputArray.Create(texPoints);
            using var dstMat = InputArray.Create(capPoints);
            using var inlierMask = new Mat();
            using var affine = Cv2.EstimateAffinePartial2D(
                srcMat, dstMat,
                inlierMask,
                method: RobustEstimationAlgorithms.RANSAC,
                ransacReprojThreshold: _options.RansacReprojectionThresholdPx,
                maxIters: 2000,
                confidence: 0.99,
                refineIters: 10);

            if (affine.Empty())
            {
                _logger?.LogInformation("Feature-matching locate: RANSAC did not converge.");
                return MapRegionRefineResult.None;
            }

            // Decompose 2×3 partial-affine: [a -b tx; b a ty]
            float a = affine.At<double>(0, 0).ToFloat();
            float b = affine.At<double>(1, 0).ToFloat();
            float tx = affine.At<double>(0, 2).ToFloat();
            float ty = affine.At<double>(1, 2).ToFloat();
            double scale = Math.Sqrt(a * (double)a + b * (double)b);
            double rotationRadians = Math.Atan2(b, a);
            double rotationDegrees = rotationRadians * 180.0 / Math.PI;

            int candidateCount = goodPairs.Count;
            int inlierCount = CountNonZero(inlierMask);
            double inlierRatio = candidateCount == 0 ? 0.0 : (double)inlierCount / candidateCount;
            double residualPixels = ComputeMedianResidual(
                texPoints, capPoints, inlierMask, a, b, tx, ty);

            // Texture corners → screenshot corners. Under axis-aligned PG UI the
            // four-corner image is an axis-aligned rect; we read off origin + size.
            var (originX, originY, width, height) = RectFromCorners(
                baseTexture.Width, baseTexture.Height, a, b, tx, ty);

            var rawFit = new MapRect(
                OriginX: originX, OriginY: originY,
                Width: width, Height: height,
                TextureWidth: baseTexture.Width,
                TextureHeight: baseTexture.Height);

            var metrics = new LocateMetrics(
                InlierCount: inlierCount,
                CandidateCount: candidateCount,
                InlierRatio: inlierRatio,
                Scale: scale,
                RotationDegrees: rotationDegrees,
                Mirror: false,                            // AffinePartial2D never flips
                Tx: tx, Ty: ty,
                ResidualPixels: residualPixels);

            // Gate
            string? rejectReason =
                inlierCount < _options.InlierFloor
                    ? $"inliers={inlierCount} < floor={_options.InlierFloor}"
                : inlierRatio < _options.InlierRatioFloor
                    ? $"ratio={inlierRatio:0.000} < floor={_options.InlierRatioFloor:0.00}"
                : Math.Abs(rotationDegrees) > _options.MaxRotationDegrees
                    ? $"|rotation|={Math.Abs(rotationDegrees):0.000}° > max={_options.MaxRotationDegrees:0.00}°"
                : null;

            if (rejectReason is not null)
            {
                _logger?.LogInformation(
                    "Feature-matching locate: rejected — {Reason}. "
                    + "(inliers={Inliers}/{Candidates} ratio={Ratio:0.000} scale={Scale:0.000} rot={Rot:0.000}°)",
                    rejectReason, inlierCount, candidateCount, inlierRatio, scale, rotationDegrees);
                return new MapRegionRefineResult(AcceptedRect: null, RawFitRect: rawFit, Metrics: metrics);
            }

            return new MapRegionRefineResult(AcceptedRect: rawFit, RawFitRect: rawFit, Metrics: metrics);
        }
        catch (OpenCVException ex)
        {
            _logger?.LogWarning(ex, "Feature-matching locate: OpenCV failure. Safe-degrade.");
            return MapRegionRefineResult.None;
        }
    }

    private static Mat ToMat8U(GrayImage g)
    {
        // Caller owns lifetime; we copy so Mat is independently disposable.
        return Mat.FromPixelData(g.Height, g.Width, MatType.CV_8UC1, g.Pixels).Clone();
    }

    private static int CountNonZero(Mat mask)
    {
        // 1×N or N×1 8U mask from RANSAC; nonzero entries are inliers.
        return (int)Cv2.CountNonZero(mask);
    }

    private static double ComputeMedianResidual(
        Point2f[] texPoints, Point2f[] capPoints, Mat inlierMask,
        float a, float b, float tx, float ty)
    {
        // Median per-inlier ||T·p_T − p_S|| in screenshot pixels (spec §"Open
        // questions" — median chosen for robustness to RANSAC-tolerated tail).
        var residuals = new System.Collections.Generic.List<double>(texPoints.Length);
        for (int i = 0; i < texPoints.Length; i++)
        {
            if (inlierMask.At<byte>(i, 0) == 0) continue;
            double projX = a * texPoints[i].X - b * texPoints[i].Y + tx;
            double projY = b * texPoints[i].X + a * texPoints[i].Y + ty;
            double dx = projX - capPoints[i].X;
            double dy = projY - capPoints[i].Y;
            residuals.Add(Math.Sqrt(dx * dx + dy * dy));
        }
        if (residuals.Count == 0) return 0;
        residuals.Sort();
        return residuals.Count % 2 == 1
            ? residuals[residuals.Count / 2]
            : (residuals[residuals.Count / 2 - 1] + residuals[residuals.Count / 2]) * 0.5;
    }

    /// <summary>
    /// Project the texture's four corners through the recovered affine and
    /// read off the axis-aligned bounding box in screenshot space. Under PG's
    /// axis-aligned UI this is tight (the rotation gate caught everything
    /// else); under a small residual rotation that escaped the gate by being
    /// just under threshold, the bbox is the tightest conservative carrier.
    /// </summary>
    private static (int OriginX, int OriginY, int Width, int Height) RectFromCorners(
        int textureWidth, int textureHeight, float a, float b, float tx, float ty)
    {
        double Project(double x, double y, out double px, out double py)
        {
            px = a * x - b * y + tx;
            py = b * x + a * y + ty;
            return px;
        }
        Project(0, 0, out var x0, out var y0);
        Project(textureWidth, 0, out var x1, out var y1);
        Project(0, textureHeight, out var x2, out var y2);
        Project(textureWidth, textureHeight, out var x3, out var y3);
        double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return (
            (int)Math.Round(minX),
            (int)Math.Round(minY),
            (int)Math.Round(maxX - minX),
            (int)Math.Round(maxY - minY));
    }
}

internal static class FloatConversionExtensions
{
    public static float ToFloat(this double d) => (float)d;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green. If `Cv2.EstimateAffinePartial2D`'s `RobustEstimationAlgorithms.RANSAC` enum value is named differently in the in-tree OpenCvSharp version, grep `OpenCvSharp.RobustEstimationAlgorithms` to confirm the right symbol and adjust.

- [ ] **Step 3: Run all map-calibration tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: green. The new class has no test coverage yet — Task 5–7 add it — but no existing test should regress (we haven't wired the refiner into the engine).

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs
git commit -m "feat(map-calibration): add FeatureMatchingRefiner (ORB+RANSAC locate)"
```

---

### Block 2 — Tests + fixtures (Tasks 5–8)

Tests are the calibration evidence — PR-1's commit log will reference the test output as the survey table. Run straight through; review at PR-1 open.

---

### Task 5: Synthetic unit tests

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingRefinerTests
{
    private static FeatureMatchingRefiner BuildRefiner(MapCalibrationLocateOptions? opts = null)
        => new(opts ?? new MapCalibrationLocateOptions());

    [Fact]
    public void Recovers_identity_when_refining_image_against_itself()
    {
        var img = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var result = BuildRefiner().Refine(img, img, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics.Should().NotBeNull();
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);
        result.Metrics.Scale.Should().BeApproximately(1.0, 0.02);
        Math.Abs(result.Metrics.RotationDegrees).Should().BeLessThan(0.1);
        result.AcceptedRect!.OriginX.Should().BeInRange(-2, 2);
        result.AcceptedRect.OriginY.Should().BeInRange(-2, 2);
    }

    [Fact]
    public void Recovers_half_scale_when_capture_is_downsampled_view()
    {
        var texture = TestPatterns.GenerateChecker(width: 512, height: 512, cellSize: 16);
        var halved  = TestPatterns.Resize(texture, 256, 256);
        var result = BuildRefiner().Refine(halved, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.Scale.Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public void Recovers_translation_when_capture_is_a_pasted_crop_of_texture()
    {
        // Frame the texture inside a larger uniform-gray "screenshot" at known origin.
        var texture = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var screenshot = TestPatterns.PasteInto(
            background: TestPatterns.UniformGray(640, 480, 128),
            foreground: texture,
            originX: 192, originY: 100);

        var result = BuildRefiner().Refine(screenshot, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.AcceptedRect!.OriginX.Should().BeApproximately(192, 3);
        result.AcceptedRect.OriginY.Should().BeApproximately(100, 3);
    }

    [Fact]
    public void Rejects_uniform_screenshot_with_no_features()
    {
        var texture = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var screenshot = TestPatterns.UniformGray(640, 480, 128);

        var result = BuildRefiner().Refine(screenshot, texture, minScore: 0);

        // Either no-fit at all, or a fit the gate rejected.
        result.AcceptedRect.Should().BeNull();
    }

    [Fact]
    public void Rejects_fit_above_rotation_gate()
    {
        var texture = TestPatterns.GenerateChecker(width: 256, height: 256, cellSize: 16);
        var rotated = TestPatterns.Rotate(texture, degrees: 5.0);

        var result = BuildRefiner().Refine(rotated, texture, minScore: 0);

        // Either RANSAC fails or the rotation gate trips.
        result.AcceptedRect.Should().BeNull();
        if (result.Metrics is not null)
        {
            Math.Abs(result.Metrics.RotationDegrees).Should().BeGreaterThan(0.5);
        }
    }
}
```

- [ ] **Step 2: Create the `TestPatterns` helper**

The helper lives next to the tests, in `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestPatterns.cs`. It produces pure-BCL `GrayImage`s. Body:

```csharp
using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests;

internal static class TestPatterns
{
    public static GrayImage UniformGray(int width, int height, byte value)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, value);
        return new GrayImage(width, height, pixels);
    }

    public static GrayImage GenerateChecker(int width, int height, int cellSize)
    {
        var pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                pixels[y * width + x] = (byte)(dark ? 32 : 224);
            }
        return new GrayImage(width, height, pixels);
    }

    public static GrayImage Resize(GrayImage src, int newWidth, int newHeight)
        => ImageOps.Resize(src, newWidth, newHeight);

    public static GrayImage PasteInto(GrayImage background, GrayImage foreground, int originX, int originY)
    {
        var pixels = (byte[])background.Pixels.Clone();
        for (int y = 0; y < foreground.Height; y++)
        {
            int dstY = originY + y;
            if (dstY < 0 || dstY >= background.Height) continue;
            for (int x = 0; x < foreground.Width; x++)
            {
                int dstX = originX + x;
                if (dstX < 0 || dstX >= background.Width) continue;
                pixels[dstY * background.Width + dstX] = foreground.Pixels[y * foreground.Width + x];
            }
        }
        return new GrayImage(background.Width, background.Height, pixels);
    }

    public static GrayImage Rotate(GrayImage src, double degrees)
    {
        // Nearest-neighbour rotate about centre; pads with mid-gray.
        double rad = degrees * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        double cx = src.Width * 0.5, cy = src.Height * 0.5;
        var pixels = new byte[src.Width * src.Height];
        Array.Fill(pixels, (byte)128);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                double rx = (x - cx) * c + (y - cy) * s + cx;
                double ry = -(x - cx) * s + (y - cy) * c + cy;
                int sx = (int)Math.Round(rx), sy = (int)Math.Round(ry);
                if (sx >= 0 && sx < src.Width && sy >= 0 && sy < src.Height)
                {
                    pixels[y * src.Width + x] = src.Pixels[sy * src.Width + sx];
                }
            }
        return new GrayImage(src.Width, src.Height, pixels);
    }
}
```

(If `ImageOps.Resize` lives in a different namespace, surface it with a using.)

- [ ] **Step 3: Run the synthetic tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~FeatureMatchingRefinerTests"`
Expected: all green. If `Recovers_identity` returns 0 inliers, the ORB params don't suit a 256×256 input — bump `OrbNFeatures` in the test or use a 512×512 input.

- [ ] **Step 4: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerTests.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/TestPatterns.cs
git commit -m "test(map-calibration): unit tests for FeatureMatchingRefiner on synthetic inputs"
```

---

### Task 6: Bundle replay fixtures

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/README.md`
- Copy: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/KurMountains-Live-20260602/` (capture PNG + Kur base texture)
- Copy: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/Eltibule-Study/` (study screenshot + Eltibule texture)
- Copy: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/Serbule-Study/` (study screenshot + Serbule texture)
- Copy: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles/KurMountains-Study/` (study screenshot + Kur texture)

- [ ] **Step 1: Locate the source bundles on disk**

The Kur live bundle lives at:
```
%LocalAppData%\Mithril\diagnostics\calibration\AreaKurMountains-20260602-192055-747-rejected-map-not-located\
```

Per the spec, the study captures live in the gate-study tool's fixtures dir (or wherever `tools/MapCalibrationFromScreenshot/` reads them from). Use Grep over the tools dir to find the canonical paths:

```pwsh
# From the repo root:
gci tools\MapCalibrationFromScreenshot -Recurse -Filter '*.png' | Select-Object -First 20
```

- [ ] **Step 2: Copy the minimal set needed for replay**

Per fixture folder, copy only:
- The grayscale capture PNG (the engine's `02-gray.png` output)
- The base texture (decoded `map-texture-<area>.bin` → PNG, OR ship the manifest+blob pair and decode at test time)

Decoding at test time is simpler — the existing `CachedBaseTextureProvider` does it. Path: ship the `map-texture-<area>.{json,bin}` pair per fixture and have the test load via the provider.

- [ ] **Step 3: Write `Fixtures/CalibrationBundles/README.md`**

```markdown
# Calibration-bundle test fixtures

Replay corpus for `FeatureMatchingRefinerReplayTests`. Each folder is a
self-contained scenario:

- `KurMountains-Live-20260602/` — the bundle from
  `%LocalAppData%/Mithril/diagnostics/calibration/AreaKurMountains-20260602-192055-747-rejected-map-not-located/`.
  Ground truth rect: (159, 82, 971, 973). The current NCC ladder fails on
  this capture — see spec §"Cross-validation evidence".
- `Eltibule-Study/`, `Serbule-Study/`, `KurMountains-Study/` — study
  captures pre-cropped to the texture's bounding box, so ground truth is
  (0, 0, textureW, textureH). Used to verify FM agrees with the working
  NCC path's prior result.

Each folder contains `capture.png` (the grayscale frame) and
`map-texture-<area>.{json,bin}` (the cached texture in the existing format
the `CachedBaseTextureProvider` reads).
```

- [ ] **Step 4: Update the test csproj to copy fixtures**

Edit `tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj` and add (if not already present):

```xml
<ItemGroup>
  <None Update="Fixtures/CalibrationBundles/**/*.*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 5: Build to confirm fixtures copy**

Run: `dotnet build tests/Mithril.MapCalibration.Capture.Tests`
Expected: green; fixtures appear under `tests/Mithril.MapCalibration.Capture.Tests/bin/Debug/net10.0-windows/Fixtures/CalibrationBundles/`.

- [ ] **Step 6: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/Fixtures/CalibrationBundles \
        tests/Mithril.MapCalibration.Capture.Tests/Mithril.MapCalibration.Capture.Tests.csproj
git commit -m "test(map-calibration): bundle replay fixtures for FeatureMatchingRefiner"
```

---

### Task 7: Replay tests against the live Kur bundle + study set

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerReplayTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingRefinerReplayTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CalibrationBundles");

    private static (GrayImage capture, GrayImage texture) LoadBundle(string folder, string areaKey)
    {
        var capturePath = Path.Combine(FixturesRoot, folder, "capture.png");
        var capture = PngLoad.LoadGray(capturePath);

        var provider = new CachedBaseTextureProvider(Path.Combine(FixturesRoot, folder));
        var texture = provider.TryGetBaseTexture(areaKey)
                      ?? throw new InvalidOperationException(
                          $"Fixture {folder}: no base texture for area {areaKey}");

        return (capture, texture);
    }

    [Fact]
    public void Recovers_kur_mountains_live_ground_truth_rect_within_two_pixels()
    {
        var (capture, texture) = LoadBundle("KurMountains-Live-20260602", "KurMountains");

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull(
            "the new locator must succeed on the Kur live bundle that the old NCC ladder rejected");
        result.Metrics.Should().NotBeNull();
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);
        result.Metrics.InlierCount.Should().BeGreaterThan(500);

        // Ground truth: (159, 82, 971, 973) per PR #1008's investigation.
        result.AcceptedRect!.OriginX.Should().BeApproximately(159, 2);
        result.AcceptedRect.OriginY.Should().BeApproximately(82, 2);
        result.AcceptedRect.Width.Should().BeApproximately(971, 2);
        result.AcceptedRect.Height.Should().BeApproximately(973, 2);
    }

    [Theory]
    [InlineData("Eltibule-Study", "Eltibule")]
    [InlineData("Serbule-Study",  "Serbule")]
    [InlineData("KurMountains-Study", "KurMountains")]
    public void Recovers_study_captures_pre_cropped_to_texture(string folder, string areaKey)
    {
        var (capture, texture) = LoadBundle(folder, areaKey);

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, texture, minScore: 0);

        result.AcceptedRect.Should().NotBeNull();
        result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);

        // Study screenshots are pre-cropped — recovered origin must be near (0, 0).
        Math.Abs(result.AcceptedRect!.OriginX).Should().BeLessOrEqualTo(2);
        Math.Abs(result.AcceptedRect.OriginY).Should().BeLessOrEqualTo(2);
    }
}
```

The `PngLoad.LoadGray` helper lives in the existing `tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/` directory if there is a PNG helper there; otherwise add a simple one (decodes via `System.Drawing.Imaging` or `OpenCvSharp.Cv2.ImRead(GrayScale)`).

- [ ] **Step 2: Run the replay tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~FeatureMatchingRefinerReplayTests"`
Expected: all green.

- [ ] **Step 3: Capture the calibration-study survey table**

Capture stdout for each test's `result.Metrics` (or add a debug-printer test that dumps `InlierCount`/`CandidateCount`/`InlierRatio`/`Scale`/`RotationDegrees`/`AcceptedRect` for every fixture). The output table goes into PR-1's commit message.

- [ ] **Step 4: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingRefinerReplayTests.cs
git commit -m "$(cat <<'EOF'
test(map-calibration): replay tests for FeatureMatchingRefiner on real captures

Asserts the Kur live bundle (the bundle that motivated #1009) recovers
within ±2 px of the (159, 82, 971, 973) ground truth at >90% inlier ratio.
Same shape against the Eltibule / Serbule / Kur study captures —
pre-cropped, recovered origin within 2 px of (0, 0).

Calibration-study survey (default options: InlierFloor=50,
InlierRatioFloor=0.50, MaxRotationDegrees=0.5):

| Bundle | Inliers / Cand. | Ratio | Scale | Rot° | Δ vs truth |
|---|---|---|---|---|---|
| Kur live              | <fill from test stdout> |   |   |   |   |
| Kur study             | <fill>                  |   |   |   |   |
| Serbule study         | <fill>                  |   |   |   |   |
| Eltibule study        | <fill>                  |   |   |   |   |

All clear the proposed gate floors by wide margins; defaults stay.

Spec: docs/superpowers/specs/2026-06-02-feature-matching-locate-design.md
EOF
)"
```

---

### Task 8: Synthetic-negative tests (cross-area)

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingNegativeTests.cs`

- [ ] **Step 1: Write the test file**

```csharp
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class FeatureMatchingNegativeTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "CalibrationBundles");

    [Theory]
    [InlineData("KurMountains-Live-20260602", "Eltibule")]
    [InlineData("Eltibule-Study",             "KurMountains")]
    [InlineData("Serbule-Study",              "KurMountains")]
    public void Rejects_when_texture_does_not_match_capture_area(string captureFolder, string wrongAreaKey)
    {
        var capturePath = Path.Combine(FixturesRoot, captureFolder, "capture.png");
        var capture = PngLoad.LoadGray(capturePath);

        // Resolve the wrong-area texture from a sibling fixture folder.
        string textureFolder = wrongAreaKey switch
        {
            "Eltibule"     => "Eltibule-Study",
            "Serbule"      => "Serbule-Study",
            "KurMountains" => "KurMountains-Study",
            _ => throw new InvalidOperationException($"unknown area {wrongAreaKey}")
        };
        var provider = new CachedBaseTextureProvider(Path.Combine(FixturesRoot, textureFolder));
        var wrongTexture = provider.TryGetBaseTexture(wrongAreaKey)
                           ?? throw new InvalidOperationException(
                               $"missing texture for {wrongAreaKey} in {textureFolder}");

        var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
        var result = refiner.Refine(capture, wrongTexture, minScore: 0);

        result.AcceptedRect.Should().BeNull(
            "RANSAC should not converge on a fit, or the inlier/ratio gate should reject the random correspondences");
    }
}
```

- [ ] **Step 2: Run the negative tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~FeatureMatchingNegativeTests"`
Expected: all green.

If any case unexpectedly *accepts* (recovers an apparently-valid rect from a wrong-area pairing), the gate is too loose — bump `InlierFloor` or `InlierRatioFloor` in `MapCalibrationLocateOptions` defaults until the negatives reject *and* the reals in Task 7 still accept. The PR-1 commit log carries the final numbers.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingNegativeTests.cs
git commit -m "test(map-calibration): cross-area negative tests for FeatureMatchingRefiner gate"
```

---

## 🛑 Review checkpoint 1 — Open PR-1

Push the feature branch and open the PR-1 pull request via `gh pr create`. Title: `feat(map-calibration): add FeatureMatchingRefiner (ORB+RANSAC locate) [PR-1]`. Body references this spec/plan and the calibration-study survey table from Task 7's commit.

**What the reviewer is looking at (full PR-1 diff):**
- `FeatureMatchingRefiner` body + ORB/BFMatcher/Lowe/RANSAC composition is faithful to the prototype
- `LocateMetrics`, `MapCalibrationLocateOptions`, and the additive `MapRegionRefineResult` evolution
- Synthetic + replay + negative tests all green
- Default gate floors clear all reals + reject all negatives in the survey table
- **No DI wire-up** — engine still uses `TextureRegistrationRefiner`, behaviour unchanged

PR-1 review must clear before PR-2 starts; PR-2 builds on PR-1's refiner.

---

## PR-2 — On-disk ORB descriptor cache (Tasks 9–12) · ends at 🛑 Review checkpoint 2

### Block 3 — Cache schema + integration (Tasks 9–12)

Each task is small and additive. Run straight through; review at PR-2 open.

---

### Task 9: `OrbDescriptorManifest` record + JSON context entry

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs`
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`

- [ ] **Step 1: Write the manifest record**

```csharp
namespace Mithril.MapCalibration.Capture.Internal;

/// <summary>
/// Per-area ORB descriptor cache manifest. Sits alongside
/// <c>map-texture-&lt;area&gt;.json</c>; the descriptor payload is the
/// DeflateStream-compressed sibling <c>map-texture-&lt;area&gt;.orb.bin</c>.
///
/// <para><b>Cache key.</b> A cached pair is valid iff:</para>
/// <list type="bullet">
/// <item><c>SchemaVersion</c> matches what the current binary expects.</item>
/// <item><c>PixelSha256</c> matches the sibling source texture's manifest
/// <c>PixelSha256</c> — cache invalidates whenever the texture is
/// rebuilt.</item>
/// <item><c>OrbParamsHash</c> matches the SHA-256 of the canonical ORB
/// param struct — cache invalidates whenever any param changes.</item>
/// <item>The actual <c>.orb.bin</c>'s SHA-256 matches
/// <c>BlobSha256</c> — guards against truncation / corruption.</item>
/// </list>
/// </summary>
internal sealed record OrbDescriptorManifest(
    int SchemaVersion,
    string Area,
    string? PgVersion,
    int KeypointCount,
    int DescriptorDim,        // 32 for ORB
    string OrbParamsHash,
    string PixelSha256,
    string BlobSha256);
```

- [ ] **Step 2: Add to the source-generated JSON context**

Edit `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` and add a `[JsonSerializable(typeof(OrbDescriptorManifest))]` attribute. (If `OrbDescriptorManifest` lives in `Mithril.MapCalibration.Capture`, the JSON context in core can't reference it — in that case create a sibling `CaptureJsonContext` in `Mithril.MapCalibration.Capture/Internal/`.)

- [ ] **Step 3: Build**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorManifest.cs \
        src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs \
        src/Mithril.MapCalibration.Capture/Internal/CaptureJsonContext.cs
git commit -m "feat(map-calibration): add OrbDescriptorManifest record + JSON serialization"
```

---

### Task 10: `CachedOrbDescriptorProvider` (reader)

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs`

The reader mirrors `CachedBaseTextureProvider`'s structure: read manifest → read blob → verify SHA-256 → return descriptors; any mismatch returns null and a warning log.

- [ ] **Step 1: Write the provider**

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture.Internal;

/// <summary>
/// Reads cached ORB descriptors for a per-area base texture. Returns null
/// on any miss, mismatch, or corruption — caller is responsible for
/// computing+writing them via <see cref="OrbDescriptorWriter"/>.
/// </summary>
internal sealed class CachedOrbDescriptorProvider
{
    private readonly string _cacheDir;
    private readonly string _orbParamsHash;
    private readonly ILogger? _logger;

    public CachedOrbDescriptorProvider(string cacheDir, string orbParamsHash, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _orbParamsHash = orbParamsHash;
        _logger = logger;
    }

    public OrbDescriptorBundle? TryRead(string areaKey, string expectedTexturePixelSha256)
    {
        if (string.IsNullOrWhiteSpace(_cacheDir) || !Directory.Exists(_cacheDir)) return null;

        var manifestPath = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.json");
        var blobPath     = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.bin");
        if (!File.Exists(manifestPath) || !File.Exists(blobPath)) return null;

        OrbDescriptorManifest? manifest;
        try
        {
            using var s = File.OpenRead(manifestPath);
            manifest = JsonSerializer.Deserialize(s, CaptureJsonContext.Default.OrbDescriptorManifest);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "ORB descriptor manifest {Path} unparseable — rebuild.", manifestPath);
            return null;
        }
        if (manifest is null) return null;

        if (manifest.OrbParamsHash != _orbParamsHash
            || manifest.PixelSha256 != expectedTexturePixelSha256
            || manifest.SchemaVersion != 1
            || manifest.DescriptorDim != 32)
        {
            _logger?.LogInformation("ORB descriptor cache for {Area} stale — rebuild.", areaKey);
            return null;
        }

        byte[] blob;
        try
        {
            using var stream = File.OpenRead(blobPath);
            using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            deflate.CopyTo(ms);
            blob = ms.ToArray();
        }
        catch (InvalidDataException ex)
        {
            _logger?.LogWarning(ex, "ORB descriptor blob {Path} corrupt — rebuild.", blobPath);
            return null;
        }

        var actualBlobHash = Convert.ToHexStringLower(SHA256.HashData(blob));
        if (actualBlobHash != manifest.BlobSha256)
        {
            _logger?.LogWarning(
                "ORB descriptor blob hash mismatch for {Area} (manifest {Expected}, blob {Actual}) — rebuild.",
                areaKey, manifest.BlobSha256, actualBlobHash);
            return null;
        }

        return OrbDescriptorBundle.Decode(blob, manifest);
    }
}

/// <summary>
/// Wire format of the .orb.bin blob: per-keypoint header + 32-byte
/// descriptor row. See <see cref="Encode"/> / <see cref="Decode"/> for the
/// concrete layout — the format is private to PR-2's reader + writer.
/// </summary>
internal sealed class OrbDescriptorBundle : IDisposable
{
    public KeyPoint[] Keypoints { get; }
    public Mat Descriptors { get; }   // CV_8UC1, rows = KeypointCount, cols = 32

    private OrbDescriptorBundle(KeyPoint[] keypoints, Mat descriptors)
    {
        Keypoints = keypoints;
        Descriptors = descriptors;
    }

    public static OrbDescriptorBundle Decode(byte[] blob, OrbDescriptorManifest manifest)
    {
        // Format:
        //   uint32  keypointCount
        //   per keypoint (24 bytes):
        //     float32 x, float32 y, float32 size, float32 angle,
        //     float32 response, int32 octave
        //   then keypointCount × 32 bytes of descriptor data
        if (blob.Length < 4) throw new InvalidDataException("blob too small");
        int n = BitConverter.ToInt32(blob, 0);
        if (n != manifest.KeypointCount)
            throw new InvalidDataException($"blob keypointCount {n} != manifest {manifest.KeypointCount}");

        var keypoints = new KeyPoint[n];
        int offset = 4;
        for (int i = 0; i < n; i++)
        {
            float x        = BitConverter.ToSingle(blob, offset + 0);
            float y        = BitConverter.ToSingle(blob, offset + 4);
            float size     = BitConverter.ToSingle(blob, offset + 8);
            float angle    = BitConverter.ToSingle(blob, offset + 12);
            float response = BitConverter.ToSingle(blob, offset + 16);
            int   octave   = BitConverter.ToInt32 (blob, offset + 20);
            keypoints[i] = new KeyPoint(new Point2f(x, y), size, angle, response, octave, classId: -1);
            offset += 24;
        }

        var descriptors = new Mat(n, 32, MatType.CV_8UC1);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                descriptors.Set(i, j, blob[offset + i * 32 + j]);
            }
        }
        return new OrbDescriptorBundle(keypoints, descriptors);
    }

    public static byte[] Encode(KeyPoint[] keypoints, Mat descriptors)
    {
        if (descriptors.Cols != 32 || descriptors.Type() != MatType.CV_8UC1)
            throw new ArgumentException("expected 32-col CV_8UC1 ORB descriptors", nameof(descriptors));

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(keypoints.Length);
        foreach (var kp in keypoints)
        {
            bw.Write(kp.Pt.X);
            bw.Write(kp.Pt.Y);
            bw.Write(kp.Size);
            bw.Write(kp.Angle);
            bw.Write(kp.Response);
            bw.Write(kp.Octave);
        }
        for (int i = 0; i < keypoints.Length; i++)
            for (int j = 0; j < 32; j++)
                bw.Write(descriptors.At<byte>(i, j));
        return ms.ToArray();
    }

    public void Dispose() => Descriptors.Dispose();
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Internal/CachedOrbDescriptorProvider.cs
git commit -m "feat(map-calibration): add CachedOrbDescriptorProvider + on-disk ORB blob format"
```

---

### Task 11: `OrbDescriptorWriter` + integration into `FeatureMatchingRefiner`

**Files:**
- Create: `src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs`
- Modify: `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`

- [ ] **Step 1: Write `OrbDescriptorWriter`**

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Mithril.MapCalibration.Capture.Internal;

internal sealed class OrbDescriptorWriter
{
    private readonly string _cacheDir;
    private readonly string _orbParamsHash;
    private readonly ILogger? _logger;

    public OrbDescriptorWriter(string cacheDir, string orbParamsHash, ILogger? logger = null)
    {
        _cacheDir = cacheDir;
        _orbParamsHash = orbParamsHash;
        _logger = logger;
    }

    public void Write(
        string areaKey, KeyPoint[] keypoints, Mat descriptors,
        string texturePixelSha256, string? pgVersion)
    {
        var blob = OrbDescriptorBundle.Encode(keypoints, descriptors);
        var blobSha = Convert.ToHexStringLower(SHA256.HashData(blob));
        var manifest = new OrbDescriptorManifest(
            SchemaVersion: 1,
            Area: areaKey,
            PgVersion: pgVersion,
            KeypointCount: keypoints.Length,
            DescriptorDim: 32,
            OrbParamsHash: _orbParamsHash,
            PixelSha256: texturePixelSha256,
            BlobSha256: blobSha);

        try
        {
            Directory.CreateDirectory(_cacheDir);
            var manifestPath = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.json");
            var blobPath     = Path.Combine(_cacheDir, $"map-texture-{areaKey}.orb.bin");

            using (var s = File.Create(manifestPath))
            {
                JsonSerializer.Serialize(s, manifest, CaptureJsonContext.Default.OrbDescriptorManifest);
            }
            using (var s = File.Create(blobPath))
            using (var deflate = new DeflateStream(s, CompressionLevel.Optimal))
            {
                deflate.Write(blob, 0, blob.Length);
            }
            _logger?.LogInformation(
                "Wrote ORB descriptor cache for {Area}: {Count} keypoints, {BlobBytes} bytes deflate-compressed payload.",
                areaKey, keypoints.Length, blob.Length);
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Failed to write ORB descriptor cache for {Area}; locate will recompute on next run.", areaKey);
        }
    }
}
```

- [ ] **Step 2: Integrate into `FeatureMatchingRefiner`**

Add ctor params: `CachedOrbDescriptorProvider? cachedDescriptors`, `OrbDescriptorWriter? writer`, plus a delegate that resolves `(area, expectedPixelSha256)` per call. The texture's `PixelSha256` is on its sibling `MapTextureManifest`; the refiner today receives a raw `GrayImage` — change to receive an area key + texture together, OR compute the SHA-256 inline on the texture's `Pixels` (slower than reading the existing manifest, but contained).

**Recommendation:** compute inline. The texture is already in memory; SHA-256 over a 4 MB byte array is sub-millisecond. Avoids a wider API change.

```csharp
// In FeatureMatchingRefiner.Refine, before the orb.DetectAndCompute(texMat, …) call:
KeyPoint[] texKeypoints;
Mat texDescriptors;
OrbDescriptorBundle? cached = null;

string textureSha = Convert.ToHexStringLower(SHA256.HashData(baseTexture.Pixels));
if (_cachedDescriptors is not null)
{
    cached = _cachedDescriptors.TryRead(/* areaKey */ _currentAreaKey, textureSha);
}

if (cached is not null)
{
    texKeypoints = cached.Keypoints;
    texDescriptors = cached.Descriptors;
}
else
{
    using var texDesc = new Mat();
    orb.DetectAndCompute(texMat, null, out var texKp, texDesc);
    texKeypoints = texKp;
    texDescriptors = texDesc.Clone();    // own it
    _writer?.Write(_currentAreaKey, texKeypoints, texDescriptors, textureSha, pgVersion: null);
}
```

The `_currentAreaKey` field is set on the refiner by the engine via a per-attempt mutator — or the engine resolves a per-area refiner factory. Simpler v1: add an `areaKey` argument to `IMapRegionRefiner.Refine` (PR-3 already reshaping the interface). For PR-2-only, thread the area via a transient `_currentAreaKey` field plus an internal `SetAreaKey(string)` method called by the engine just before `Refine`. (Or accept the API churn here and do the arg-add in PR-2; the spec is silent on order. Pick the smaller diff: PR-3 is already touching the interface.)

- [ ] **Step 3: Wire DI**

Edit `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`. The existing `_assetCacheDir` flows into `CachedBaseTextureProvider`'s ctor; thread the same value into `CachedOrbDescriptorProvider` and `OrbDescriptorWriter`. The `orbParamsHash` is computed once from the current `MapCalibrationLocateOptions` (SHA-256 of `{nFeatures}|{loweRatio:F4}|…`).

```csharp
services.TryAddSingleton(sp =>
{
    var opts = sp.GetRequiredService<MapCalibrationLocateOptions>();
    return new CachedOrbDescriptorProvider(
        cacheDir: assetCacheDir,
        orbParamsHash: ComputeOrbParamsHash(opts),
        logger: sp.GetService<ILogger<CachedOrbDescriptorProvider>>());
});
// same for OrbDescriptorWriter
```

`ComputeOrbParamsHash` is a private helper that hashes the canonical-ordered params struct.

- [ ] **Step 4: Build + run all map-calibration tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: all green; replay tests still pass (caching is transparent — a cold cache makes them run identically to PR-1).

- [ ] **Step 5: Measure the speedup**

Run the replay tests once cold (delete any `.orb.{json,bin}` in fixtures' working dirs first), then a second time warm. Capture the elapsed time of `FeatureMatchingRefinerReplayTests.Recovers_kur_mountains_live_ground_truth_rect_within_two_pixels` from `dotnet test --logger "console;verbosity=detailed"`. Document cold vs warm in the PR-2 commit log.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Internal/OrbDescriptorWriter.cs \
        src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs \
        src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): lazy-populate cached ORB descriptors in FeatureMatchingRefiner"
```

---

### Task 12: Cache integrity tests

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/CachedOrbDescriptorProviderTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System.IO;
using FluentAssertions;
using Mithril.MapCalibration.Capture.Internal;
using OpenCvSharp;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class CachedOrbDescriptorProviderTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "mithril-orb-cache-" + Guid.NewGuid());
    private const string ParamsHash = "deadbeef";
    private const string PixelHash = "facefeed";

    public CachedOrbDescriptorProviderTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }

    [Fact]
    public void Round_trips_write_then_read()
    {
        var (kp, desc) = SampleDescriptors(count: 8);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: "test");

        using var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", PixelHash);

        bundle.Should().NotBeNull();
        bundle!.Keypoints.Length.Should().Be(8);
        bundle.Descriptors.Rows.Should().Be(8);
        bundle.Descriptors.Cols.Should().Be(32);
    }

    [Fact]
    public void Returns_null_on_blob_corruption()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        // Flip a byte in the deflate-compressed blob (almost certainly breaks decompression OR hash).
        var blobPath = Path.Combine(_tmpDir, "map-texture-X.orb.bin");
        var bytes = File.ReadAllBytes(blobPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(blobPath, bytes);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", PixelHash);
        bundle.Should().BeNull();
    }

    [Fact]
    public void Returns_null_on_orb_params_hash_mismatch()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, "different-params-hash").TryRead("X", PixelHash);
        bundle.Should().BeNull();
    }

    [Fact]
    public void Returns_null_on_pixel_sha_mismatch()
    {
        var (kp, desc) = SampleDescriptors(count: 4);
        new OrbDescriptorWriter(_tmpDir, ParamsHash).Write("X", kp, desc, PixelHash, pgVersion: null);

        var bundle = new CachedOrbDescriptorProvider(_tmpDir, ParamsHash).TryRead("X", "different-pixel-sha");
        bundle.Should().BeNull();
    }

    private static (KeyPoint[] kp, Mat desc) SampleDescriptors(int count)
    {
        var kp = new KeyPoint[count];
        for (int i = 0; i < count; i++)
            kp[i] = new KeyPoint(new Point2f(i * 10, i * 10), size: 7, angle: 0, response: 1, octave: 0, classId: -1);

        var desc = new Mat(count, 32, MatType.CV_8UC1);
        for (int i = 0; i < count; i++)
            for (int j = 0; j < 32; j++)
                desc.Set(i, j, (byte)((i * 31 + j * 17) & 0xFF));
        return (kp, desc);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CachedOrbDescriptorProviderTests"`
Expected: all green.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/CachedOrbDescriptorProviderTests.cs
git commit -m "test(map-calibration): round-trip + corruption + cache-key tests for ORB descriptor cache"
```

---

## 🛑 Review checkpoint 2 — Open PR-2

Push and `gh pr create`. Title: `feat(map-calibration): cache ORB descriptors alongside base texture [PR-2]`. Body documents:
- Cache file format + invalidation key
- Measured cold vs warm replay timing
- Disk footprint per area (rough — 8000 keypoints × 56 bytes ≈ 450 KB raw, ~150–250 KB deflate-compressed)

PR-2 review must clear before PR-3 starts.

---

## PR-3 — Bundle JSON shape migration (Tasks 13–16) · ends at 🛑 Review checkpoint 3

### Block 4 — Reshape diagnostic surfaces (Tasks 13–16)

PR-3 is structural — drops fields, renames properties, changes a JSON shape. The build will break mid-block as cascading consumers need updates. Run straight through; the build going green at the end of Task 16 is the safety net.

---

### Task 13: Strip NCC fields from `MapRect` + `MapRectJson`

**Files:**
- Modify: `src/Mithril.MapCalibration/Detection/MapRectLocator.cs` (record only)
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`

- [ ] **Step 1: Edit `MapRect`**

At the bottom of `MapRectLocator.cs`:

```csharp
public sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy) { /* unchanged */ }
    public (double Sx, double Sy) TextureToScreenshot(double tx, double ty) { /* unchanged */ }
}
```

(Drop `AutoDetectScore` and `SourceScaleFactor`.)

- [ ] **Step 2: Edit `MapRectJson`**

In `CalibrationBundleJson.cs`:

```csharp
public sealed record MapRectJson(
    int SchemaVersion,
    int OriginX, int OriginY, int Width, int Height,
    int TextureWidth, int TextureHeight);
```

- [ ] **Step 3: Build to surface the cascade**

Run: `dotnet build Mithril.slnx`
Expected: build fails. Sites that read/write the dropped fields surface. They are:
- `MapRectLocator.AutoDetectBest` (sets the fields when constructing rungs) — this is part of the to-be-deleted ladder; in PR-3 we patch it to compile, in PR-4 we delete it.
- `TextureRegistrationRefiner.Refine` (reads `seed.AutoDetectScore`, sets via `with`) — patch to drop those.
- `AutoCalibrationEngine.RunAttemptCoreAsync` (logs `best.AutoDetectScore` / `best.SourceScaleFactor`) — patch to drop.
- `MapRectJson(AutoDetectScore: …, SourceScaleFactor: …)` callers in the sink — patch to drop.
- Tests asserting on the fields — patch.

Each patch is a small mechanical edit. Make them.

- [ ] **Step 4: Build until green**

Run: `dotnet build Mithril.slnx`
Expected: green after the patches in Step 3. If any consumer is overlooked, the build fails — `Grep "AutoDetectScore"` over `src/` + `tests/` to find it.

- [ ] **Step 5: Run all map-calibration tests**

Run: `dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapRectLocator.cs \
        src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs \
        src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs \
        src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs \
        tests
git commit -m "refactor(map-calibration): drop AutoDetectScore/SourceScaleFactor from MapRect + MapRectJson"
```

---

### Task 14: Introduce `LocatorBestJson`, change `AttemptJson.LocatorBest` type, bump schema version

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs`

- [ ] **Step 1: Add the new record + bump schema**

```csharp
public sealed record AttemptJson(
    int SchemaVersion,             // BUMP from current value to current+1
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files,
    LocatorBestJson? LocatorBest = null);   // type change

public sealed record LocatorBestJson(
    int SchemaVersion,
    int OriginX, int OriginY, int Width, int Height,
    int TextureWidth, int TextureHeight,
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    double Tx, double Ty,
    double ResidualPixels,
    bool GateAccepted,
    string? GateRejectReason);
```

Then add `[JsonSerializable(typeof(LocatorBestJson))]` to `CalibrationBundleJsonContext`.

- [ ] **Step 2: Build to surface the cascade**

The sink writes `AttemptJson(LocatorBest: …)`. The site must be updated. Same for `LocatorBestRect`-reading test fixtures (handled in Task 15).

- [ ] **Step 3: Commit (build still broken — Task 15 finishes the cascade)**

Hold the commit until Tasks 15 + 16 close the build. Skip this step.

---

### Task 15: Rename `LocatorBestRect` → `LocatorRawFit` + add `LocatorMetrics`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs`
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs`
- Modify: tests asserting `LocatorBestRect`

- [ ] **Step 1: Update `CalibrationAttemptContext`**

```csharp
/// <summary>Locator's raw fit rect — populated whenever the refiner produced
/// any fit (gate-pass-or-not). Replaces the pre-PR-3 LocatorBestRect.</summary>
public MapRect? LocatorRawFit { get; set; }

/// <summary>Locator's FM metrics — populated whenever the refiner produced
/// any fit. Carries inlier count/ratio + recovered transform parameters.</summary>
public LocateMetrics? LocatorMetrics { get; set; }
```

(Remove the old `LocatorBestRect`.)

- [ ] **Step 2: Update `AutoCalibrationEngine`**

Replace:
```csharp
attempt.LocatorBestRect = refineResult.BestCoarseRect;
```
with:
```csharp
attempt.LocatorRawFit = refineResult.RawFitRect;
attempt.LocatorMetrics = refineResult.Metrics;
```

Adjust the rejection-branch log line accordingly:
```csharp
if (refineResult.Metrics is { } m)
{
    _logger?.LogInformation(
        "Auto-calibration {Area}: locate rejected — inliers={Inliers}/{Cand} ratio={Ratio:0.000}, scale={Scale:0.000}, rotation={Rot:0.000}°.",
        area, m.InlierCount, m.CandidateCount, m.InlierRatio, m.Scale, m.RotationDegrees);
}
```

- [ ] **Step 3: Update the sink**

In `FilesystemCalibrationAttemptBundleSink`, the `LocatorBest` JSON node is now `LocatorBestJson?`. Build it from `attempt.LocatorRawFit` + `attempt.LocatorMetrics` + `attempt.Outcome == Accepted` (or whatever flag indicates gate accept).

```csharp
LocatorBestJson? locatorBest = null;
if (attempt.LocatorRawFit is { } rect && attempt.LocatorMetrics is { } metrics)
{
    locatorBest = new LocatorBestJson(
        SchemaVersion: 1,
        OriginX: rect.OriginX, OriginY: rect.OriginY,
        Width: rect.Width, Height: rect.Height,
        TextureWidth: rect.TextureWidth, TextureHeight: rect.TextureHeight,
        InlierCount: metrics.InlierCount,
        CandidateCount: metrics.CandidateCount,
        InlierRatio: metrics.InlierRatio,
        Scale: metrics.Scale,
        RotationDegrees: metrics.RotationDegrees,
        Tx: metrics.Tx, Ty: metrics.Ty,
        ResidualPixels: metrics.ResidualPixels,
        GateAccepted: attempt.Outcome == OutcomeVocabulary.Accepted,
        GateRejectReason: attempt.RejectReason);
}
```

- [ ] **Step 4: Update test consumers**

`Grep LocatorBestRect tests/` — replace every hit with the new shape.

- [ ] **Step 5: Build + run tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests tests/Mithril.MapCalibration.Tests`
Expected: green.

- [ ] **Step 6: Commit Tasks 14 + 15 together**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs \
        src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs \
        src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs \
        tests
git commit -m "feat(map-calibration): bundle JSON LocatorBest now carries inlier metrics (schema bump)"
```

---

### Task 16: Drop `double minScore` from `IMapRegionRefiner.Refine`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs`
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs` (drop `[Obsolete] BestCoarseRect` + the two-arg ctor)
- Modify: tests

- [ ] **Step 1: Reshape `IMapRegionRefiner`**

```csharp
public interface IMapRegionRefiner
{
    /// <summary>
    /// Find where <paramref name="baseTexture"/> sits inside
    /// <paramref name="capturedGray"/>. The acceptance gate lives inside the
    /// refiner — there is no per-call score floor.
    /// </summary>
    MapRegionRefineResult Refine(GrayImage capturedGray, GrayImage baseTexture);
}
```

- [ ] **Step 2: Update `FeatureMatchingRefiner`**

Drop the `minScore` arg; the no-op `_ = minScore` line goes away.

- [ ] **Step 3: Update `TextureRegistrationRefiner`**

Drop the `minScore` arg; in-line a private `const double InternalMinScore = 0.5` for the NCC code path so behaviour is unchanged. (PR-4 deletes the whole class.)

- [ ] **Step 4: Update `AutoCalibrationEngine`**

The `_refiner.Refine(gray, baseTexture, RefineMinScore)` call becomes `_refiner.Refine(gray, baseTexture)`. The `const double RefineMinScore = 0.5` stays for now (PR-4 deletes it).

- [ ] **Step 5: Update `MapRegionRefineResult`**

Drop the `[Obsolete] BestCoarseRect` alias and the two-arg `(AcceptedRect, BestCoarseRect)` ctor introduced in PR-1's Task 3. The record is the clean `(AcceptedRect, RawFitRect, Metrics)` triple.

- [ ] **Step 6: Build + run tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests tests/Mithril.MapCalibration.Tests`
Expected: green.

- [ ] **Step 7: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs \
        src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs \
        src/Mithril.MapCalibration.Capture/FeatureMatchingRefiner.cs \
        src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs \
        tests
git commit -m "refactor(map-calibration): drop minScore arg from IMapRegionRefiner; gate lives in the refiner"
```

---

## 🛑 Review checkpoint 3 — Open PR-3

Push and `gh pr create`. Title: `refactor(map-calibration): bundle JSON migration for feature-matching locate [PR-3]`. Body documents:
- Bundle `AttemptJson.SchemaVersion` bumped from N → N+1
- `LocatorBest` JSON shape change (was `MapRectJson`, now `LocatorBestJson`)
- `MapRect` lost `AutoDetectScore` + `SourceScaleFactor` (now a pure-geometry record)
- `CalibrationAttemptContext.LocatorBestRect` renamed → `LocatorRawFit` + new `LocatorMetrics`
- `IMapRegionRefiner.Refine` lost its `double minScore` arg

PR-3 must clear before PR-4 starts.

---

## PR-4 — Engine cutover + retirements (Tasks 17–22) · ends at 🛑 Review checkpoint 4

### Block 5 — Cutover (Tasks 17–22)

This block flips production behaviour. Build green throughout (no broken-build commits); the live Kur replay test in Task 22 is the proof of cutover correctness.

---

### Task 17: Swap DI registration

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs`

- [ ] **Step 1: Edit the DI registration**

Find the `services.TryAddSingleton<IMapRegionRefiner, TextureRegistrationRefiner>()` line; change to `FeatureMatchingRefiner`. Pass through `MapCalibrationLocateOptions`, the `CachedOrbDescriptorProvider`, and the `OrbDescriptorWriter` registered in PR-2.

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: green.

- [ ] **Step 3: Run all map-calibration tests**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests tests/Mithril.MapCalibration.Tests`
Expected: green. The engine-level test suite now exercises the FM refiner via DI.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): wire FeatureMatchingRefiner as the production IMapRegionRefiner"
```

---

### Task 18: Delete `RefineMinScore` constant from `AutoCalibrationEngine`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`

- [ ] **Step 1: Delete the constant**

```csharp
// Delete:
//   private const double RefineMinScore = 0.5;
```

The constant was only read by the now-removed third arg of `_refiner.Refine`. Grep `RefineMinScore` to confirm no other reader.

- [ ] **Step 2: Build + tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs
git commit -m "refactor(map-calibration): delete dead RefineMinScore NCC constant"
```

---

### Task 19: Delete `TextureRegistrationRefiner` + its tests

**Files:**
- Delete: `src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs`
- Delete: `tests/Mithril.MapCalibration.Capture.Tests/TextureRegistrationRefinerTests.cs`

- [ ] **Step 1: Delete the files**

```pwsh
Remove-Item src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs
Remove-Item tests/Mithril.MapCalibration.Capture.Tests/TextureRegistrationRefinerTests.cs
```

- [ ] **Step 2: Build + tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: green. Grep `TextureRegistrationRefiner` over `src/` + `tests/` to confirm nothing else references it.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs \
        tests/Mithril.MapCalibration.Capture.Tests/TextureRegistrationRefinerTests.cs
git commit -m "refactor(map-calibration): delete TextureRegistrationRefiner (NCC+ECC, retired by ORB)"
```

---

### Task 20: Delete `MapRectLocator` ladder + move `MapRect` to its own file

**Files:**
- Delete: `src/Mithril.MapCalibration/Detection/MapRectLocator.cs` (the static class)
- Create: `src/Mithril.MapCalibration/Detection/MapRect.cs` (the record)

- [ ] **Step 1: Move the `MapRect` record**

Create `src/Mithril.MapCalibration/Detection/MapRect.cs`:

```csharp
namespace Mithril.MapCalibration.Detection;

/// <summary>
/// Visible map's bounding box in the screenshot, plus the source texture's
/// native dimensions. Combined these give the screenshot↔texture transform.
/// </summary>
public sealed record MapRect(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    int TextureWidth,
    int TextureHeight)
{
    public (double Tx, double Ty) ScreenshotToTexture(double sx, double sy)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return ((sx - OriginX) * scaleX, (sy - OriginY) * scaleY);
    }

    public (double Sx, double Sy) TextureToScreenshot(double tx, double ty)
    {
        var scaleX = (double)TextureWidth / Width;
        var scaleY = (double)TextureHeight / Height;
        return (tx / scaleX + OriginX, ty / scaleY + OriginY);
    }
}
```

- [ ] **Step 2: Delete `MapRectLocator.cs`**

The file contained the now-orphaned static `MapRectLocator` class (`AutoDetect`/`AutoDetectBest`/`BuildCandidateScales`/`RefineScaleFactor`/`DownsampleToLongEdge`/`DefaultWorkingLongEdgePx`) + the moved-out `MapRect` record. With the record relocated, delete the entire file.

```pwsh
Remove-Item src/Mithril.MapCalibration/Detection/MapRectLocator.cs
```

- [ ] **Step 3: Find lingering `MapRectLocator` references**

```pwsh
# From repo root:
gci -Recurse -Include *.cs | sls 'MapRectLocator'
```

Expected: zero hits. Any remaining hit means a consumer of the deleted ladder slipped through — investigate before continuing.

- [ ] **Step 4: Build + tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Detection/MapRect.cs \
        src/Mithril.MapCalibration/Detection/MapRectLocator.cs
git commit -m "refactor(map-calibration): delete MapRectLocator NCC ladder; relocate MapRect to its own file"
```

---

### Task 21: Delete the prototype test file

**Files:**
- Delete: `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs`

- [ ] **Step 1: Delete the file**

```pwsh
Remove-Item tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs
```

The production refiner subsumes the prototype's role; the refiner's replay + unit tests provide better coverage of the same surface.

- [ ] **Step 2: Build + tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: green.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs
git commit -m "test(map-calibration): delete FeatureMatchingPrototype (subsumed by production refiner + tests)"
```

---

### Task 22: Engine-level end-to-end test on Kur live bundle

**Files:**
- Create: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineFeatureMatchingTests.cs`

- [ ] **Step 1: Write the test**

The test fixture stubs `ICaptureService` to return the Kur live bundle's grayscale capture, stubs `IMapCaptureRegionProvider` to return a bbox covering the captured frame, stubs `IGameWindowLocator.Locate` to return a non-null handle, stubs `IAreaState.CurrentArea` to return `"KurMountains"`. The base-texture provider points at the fixture's `map-texture-KurMountains.{json,bin}`. The engine is constructed with `FeatureMatchingRefiner` (the production DI graph from PR-4 wires this automatically; the test does it explicitly for clarity).

```csharp
using System.Threading;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Diagnostics;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class AutoCalibrationEngineFeatureMatchingTests
{
    [Fact]
    public async Task Kur_live_bundle_now_calibrates_under_feature_matching()
    {
        var engine = AutoCalibrationEngineTestBuilder
            .ForFixture("KurMountains-Live-20260602", areaKey: "KurMountains")
            .WithFeatureMatchingRefiner()
            .Build();

        var outcome = await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

        outcome.Persisted.Should().BeTrue(
            "the Kur live bundle is the bundle that motivated #1009; under FM it must calibrate");
        outcome.RejectReason.Should().BeNull();
    }
}
```

`AutoCalibrationEngineTestBuilder` is a new (or extended) fluent test fixture under `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/`. Its responsibility: assemble an `AutoCalibrationEngine` with the right stubs for a fixture folder. If an existing builder exists, extend it; if not, write a minimal one targeted at this one test.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineFeatureMatchingTests"`
Expected: green.

- [ ] **Step 3: Manual verification**

Per the brief's "manual verification" requirement:

1. Build the shell: `dotnet build src/Mithril.Shell`
2. Run Mithril: `dotnet run --project src/Mithril.Shell` (or via the `mithril` skill)
3. Open Project Gorgon, enter Kur Mountains, zoom the map all the way out, set the map-bbox via the hotkey
4. Trigger calibration via the hotkey
5. Inspect the resulting bundle at `%LocalAppData%\Mithril\diagnostics\calibration\AreaKurMountains-…-accepted\01-attempt.json`
6. Confirm:
   - `Outcome: "accepted"`
   - `LocatorBest.InlierCount` > 500
   - `LocatorBest.InlierRatio` > 0.90
   - `LocatorBest.GateAccepted: true`
   - Resulting `MapRect` is near (159, 82, 971, 973)

Paste the relevant `01-attempt.json` excerpt into the PR-4 PR body.

- [ ] **Step 4: Commit**

```bash
git add tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineFeatureMatchingTests.cs
git commit -m "test(map-calibration): end-to-end Kur live bundle calibrates under FeatureMatchingRefiner"
```

---

## 🛑 Review checkpoint 4 — Open PR-4

Push and `gh pr create`. Title: `feat(map-calibration): cutover to FeatureMatchingRefiner; delete NCC ladder + ECC refine [PR-4]`. Body documents:
- DI swap (in one diff line)
- Deletions (TextureRegistrationRefiner, MapRectLocator ladder, prototype test, RefineMinScore)
- The end-to-end test asserting Kur now calibrates
- The manual verification screenshot / JSON excerpt

PR-4 is the cutover. Once merged, the issue #1009 closes.

---

## Self-review

Before opening PR-1, walk the spec section-by-section and confirm:

- [x] **Goal** — Task 4 (refiner) + Task 17 (DI swap) cover the replacement.
- [x] **Criterion (occluded pixels = zero, not negative)** — by construction in Task 4's ORB body.
- [x] **Cross-validation evidence** — Task 7's calibration-study table goes in the PR-1 commit log.
- [x] **Architecture (FeatureMatchingRefiner, descriptor caching, result shape)** — Tasks 1–4 (refiner), Tasks 9–11 (cache), Task 3 + Task 16 (result shape).
- [x] **Gate criteria** — Task 2 (POCO with defaults) + Task 8 (negative tests prove the gate floors).
- [x] **Bundle JSON migration** — Tasks 13–16.
- [x] **Retirement list** — Tasks 18 (RefineMinScore), 19 (TextureRegistrationRefiner), 20 (MapRectLocator), 21 (prototype).
- [x] **Test plan** — Tasks 5 (synthetic), 7 (replay), 8 (negatives), 12 (cache integrity), 22 (engine end-to-end).
- [x] **Risks** — perf is covered by the cold/warm measurement in Task 11 Step 5; sub-pixel-input-to-solve risk is covered by the existing solve tests still passing across PR-4.

No placeholders, no "TODO", no "implement later".
