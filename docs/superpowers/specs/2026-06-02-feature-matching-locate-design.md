# ORB+RANSAC Feature-Matching Locate Design

**Status:** design spec (2026-06-02). Replaces the NCC scale-ladder locate ([`MapRectLocator.AutoDetect`](../../../src/Mithril.MapCalibration/Detection/MapRectLocator.cs)) + ECC sub-pixel refine ([`TextureRegistrationRefiner`](../../../src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs)) with an ORB + BFMatcher + Lowe-ratio + `Cv2.EstimateAffinePartial2D` (RANSAC) pipeline. Origin: [issue #1009](https://github.com/moumantai-gg/mithril/issues/1009); failure mode surfaced by [PR #1008](https://github.com/moumantai-gg/mithril/pull/1008) on Kur Mountains.

This spec is the architectural counterpart to [the synthesis-J re-rank spec](2026-06-02-synthesis-rerank-design.md) — that spec replaces the **solve-stage** acceptance gate; this one replaces the **locate-stage** algorithm. The two are independent; they happen to land in the same week.

## Goal

Replace the discrete NCC scale-ladder + parabolic refinement + ECC sub-pixel refinement at the locate stage with a single ORB-feature + RANSAC partial-affine fit. The current ladder is structurally fragile in the presence of partial occlusion (fog of war), high-contrast overlays (map pins), and any non-coastline-anchored map (caves, indoor, areas with little distinctive shoreline). The Kur Mountains live capture in PR #1008 made this concrete: the ladder's best score (0.473) sat below the production gate (0.50) at the **wrong** rect, while the ground-truth rect scored 0.674 — within ladder reach, but the locator never tried it. ORB + RANSAC recovers the ground-truth rect to sub-pixel accuracy on that same bundle (158.8, 82.4, 971.2 × 971.2 vs ground truth 159, 82, 971, 973) at 97.9% inlier ratio.

## The criterion that rules approaches in/out

> **Occluded pixels must contribute zero to the locate metric, not negative.**

Every dense-correlation metric we've tried — vanilla NCC ([`NccTemplateMatch`](../../../src/Mithril.MapCalibration/Detection/NccTemplateMatch.cs)), gradient/edge NCC, masked NCC — fails this criterion because the metric is a per-pixel sum: when the screenshot's pixel is *opaque fog gray* (constant value) the per-pixel agreement with the texture's varied content is *bad* (low covariance / high error), and the sum is pulled down. Worse, the gradient of this penalty steers the discrete scale ladder toward **smaller** templates that fit inside the unfogged interior — exactly the failure mode seen in PR #1008's Kur bundle, where the ladder picked a 909×909 rect avoiding the fogged coast over the true 971×971 rect that straddles it.

Three classes of fix don't work:

1. **Lower the NCC gate.** Score-truth gap is small (0.473 wrong-rect vs 0.674 true-rect, both clearable under a 0.45 gate). The wrong rect would *also* clear, and the ladder picks by max-score, so this just substitutes "too strict" with "happy to pick the wrong rect".
2. **Mask out fog before NCC.** Requires a robust fog detector. Fog is opaque gray of varying intensity by zone; the boundary is fuzzy; pins overlay both fogged and unfogged regions. Detecting it from the screenshot alone is a research problem of similar difficulty to the original.
3. **Edge / gradient NCC.** Reduces but does not eliminate the penalty — a foggy region has *no* gradient, so it scores 0 contribution where a true match would score positive, but a misaligned ridge in the visible region still scores negative. Bias toward smaller templates persists.

Feature matching + RANSAC satisfies the criterion **by construction**:

- ORB finds features in regions with corners/ridges/transitions; uniform-gray fog produces **no features**, contributes **no descriptors**, has **zero pull** on the result.
- Map-pin features have descriptors but no analogue in the texture; RANSAC flags them as **outliers** and excludes them.
- The metric is `(inlier count, inlier ratio)` — both monotone-good. Doubling the visible area doubles the inliers; halving it doesn't introduce negative contributions, it just makes the fit harder.
- No template-size dimension to bias. The transform is recovered globally from feature correspondences, not by sweeping a template across scale rungs.

**Universality across map types is the second-order win.** The current ladder works on Eltibule because Eltibule has a distinctive coastline (high gradient, asymmetric); it has not been tested on caves, indoor maps (e.g. crypts), or zones with a uniform terrain palette. The codebase carries no per-map assumption about "has a coastline"; it just happened that the maps tested first had one. ORB finds whatever structure exists — walls, doorways, terrain transitions — without that assumption. This is what makes the cutover defensible across the whole game.

## Cross-validation evidence

The prototype is `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs` (to be deleted by the implementation PR — *not* by this spec/plan). It runs OpenCvSharp's `ORB.Create(nFeatures: 8000)` over the captured frame and the base texture, BFMatcher with the Lowe ratio test (0.75), then `Cv2.EstimateAffinePartial2D` (RANSAC, 4-DoF similarity: scale, rotation, tx, ty). Numbers below are the prototype's stdout against the four bundles in the brief — quoted verbatim, not re-derived:

| Map | Inliers / total | Inlier ratio | Recovered scale | Recovered rotation | Recovered (Tx, Ty) | Delta vs ground truth |
|---|---|---|---|---|---|---|
| **Kur Mountains LIVE** | **1066 / 1089** | **97.9%** | 0.474 | 0.002° | (158.9, 82.4) | **dx=−0.2, dy=+0.4, dw=+0.2, dh=−1.8** (sub-pixel) |
| Kur Mountains (study) | 1076 / 1127 | 95.5% | 0.479 | 0.009° | (0.5, 0.0) | study screenshot pre-cropped — consistent with ground truth |
| Serbule (study) | 1135 / 1159 | 97.9% | 0.450 | −0.003° | (−0.3, −0.4) | study screenshot pre-cropped — consistent |
| Eltibule (study) | 451 / 472 | 95.6% | 0.414 | −0.008° | (212.6, 139.8) | working zone — FM agrees with NCC's prior accept |

**Inlier ratio** is the fraction of Lowe-survivor matches that survive RANSAC. All four sit at 95–98%. The cross-area floor is 95.5% on the *worst* case in the set; the *Eltibule* case (smaller texture → fewer features → 472 vs 1089) still clears the floor by 40 percentage points.

**Three things to read off this table that matter for the spec:**

1. The Kur-live bundle that motivated this work recovers **sub-pixel** (max abs delta 1.8 px on the rect's height; all other deltas under 0.5 px). The current ladder rejected at score 0.473. The new metric reports inlier-ratio 0.979.
2. The rotation column is the noise floor. Native PG map UI rotation is 0° (axis-aligned). Anything > ~0.5° in production means "no fit" much more strongly than the current ladder's "low NCC" — wrong-fit RANSAC results don't recover a near-zero rotation.
3. Scales span 0.41–0.48 across the four bundles. The current ladder hard-coded a discrete factor list (1.0, 1.1, 1.2, 1.35, 1.5, … per [`MapRectLocator.BuildCandidateScales`](../../../src/Mithril.MapCalibration/Detection/MapRectLocator.cs)) that would have required parabolic interpolation between rungs to express the Eltibule 0.414. RANSAC's scale is continuous by construction.

**Speed.** Prototype reports ~200–300 ms total per locate (ORB on capture ~50–100 ms, ORB on texture ~100–200 ms, KNN match ~30 ms, RANSAC < 1 ms). The current NCC ladder is ~3–5 s. The texture-side ORB cost is amortizable (cache descriptors per asset version — see *Descriptor caching* below); steady-state locate is then ~80–150 ms.

## Architecture

### `FeatureMatchingRefiner : IMapRegionRefiner`

New type in [`src/Mithril.MapCalibration.Capture/`](../../../src/Mithril.MapCalibration.Capture/) (sibling of [`TextureRegistrationRefiner`](../../../src/Mithril.MapCalibration.Capture/TextureRegistrationRefiner.cs)). **Not** in the core `Mithril.MapCalibration` project — that project is decoder-free (the `ShippedGraphDecoderFreeTests` invariant) and BCL-only, and ORB requires OpenCvSharp. The Capture project already references OpenCvSharp (today's `TextureRegistrationRefiner` uses it for ECC), so the dependency footprint is unchanged.

The refiner implements [`IMapRegionRefiner.Refine(GrayImage capturedGray, GrayImage baseTexture, double minScore)`](../../../src/Mithril.MapCalibration.Capture/IMapRegionRefiner.cs). The third arg's name is a historical NCC-vocabulary leak (`minScore` is the 0.5 NCC threshold today). PR plan: keep the interface signature in PR-1 (the new refiner ignores the third arg / uses it as a no-op floor), then rename the param + reshape `MapRegionRefineResult` in PR-3.

Refiner body:

```
ORB.Create(nFeatures: ORBnFeatures)
  → detectAndCompute(capturedGray) → (keypoints_S, descriptors_S)
  → detectAndCompute(baseTexture)   → (keypoints_T, descriptors_T)
BFMatcher(NormHamming, crossCheck: false)
  → knnMatch(descriptors_S, descriptors_T, k: 2)
  → Lowe filter: keep m if m.distance < 0.75 * m_second.distance
Cv2.EstimateAffinePartial2D(
    pointsT, pointsS,  // src = texture keypoints (Lowe survivors), dst = screenshot
    method: RANSAC,
    ransacReprojThreshold: 3.0,
    maxIters: 2000,
    confidence: 0.99)
  → 2x3 affine (similarity: scale, rotation, tx, ty), inlierMask
```

The `EstimateAffinePartial2D` direction is **texture → screenshot** so the returned affine maps a texture-pixel to its position in the captured frame. The four corners of the texture, run through the affine, become the four corners of the located rect in the frame. Decomposing the 2×3 matrix:

```
[ a  b  tx ]      a = s·cos(θ),  b = -s·sin(θ)
[ c  d  ty ]      c = s·sin(θ),  d =  s·cos(θ)
                  scale = sqrt(a² + c²),  rotation = atan2(c, a)
```

Under PG's axis-aligned UI the rotation is ~0, so the four-corner rect is axis-aligned and reduces to `(originX, originY, width, height)` — the existing `MapRect` shape. Refiner output therefore stays a `MapRect` (no rotation field needed for v1). A **small-rotation gate** (default 0.5°) rejects the rare case where RANSAC converges on a rotated solution — that's a "no fit" verdict, not a "fit a rotated rect" verdict, because every downstream consumer (crop, resize, ScreenshotToTexture) assumes axis-alignment.

### Result shape: `MapRegionRefineResult` semantics evolve

Today: [`MapRegionRefineResult(MapRect? AcceptedRect, MapRect? BestCoarseRect)`](../../../src/Mithril.MapCalibration.Capture/MapRegionRefineResult.cs). `BestCoarseRect` carries `AutoDetectScore` + `SourceScaleFactor` on the `MapRect` record itself.

Under feature matching there is no "coarse" stage — RANSAC produces a single best fit directly (sub-pixel, no separate refine step). The "best of a discrete ladder" abstraction is meaningless. Three choices:

| Option | Result type | `MapRect` carries | Verdict |
|---|---|---|---|
| (a) Reuse names with new semantics | `MapRegionRefineResult(AcceptedRect, BestCoarseRect)` | strip NCC fields | Saves API churn but `BestCoarseRect` is a lie under FM (it *is* the final fit, not a coarse one). Future readers will be confused. |
| (b) Rename | `MapRegionRefineResult(AcceptedRect, RawFitRect, LocateMetrics? Metrics)` | strip NCC fields | Names match reality. One renamed field is the cost. `RawFitRect` = the FM fit before the small-rotation / clamp gates; `AcceptedRect` = `null` when those gates reject. |
| (c) Evolve the record | `MapRegionRefineResult(AcceptedRect, RawFitRect, LocateMetrics? Metrics)` AND drop `AutoDetectScore`/`SourceScaleFactor` from `MapRect` itself | strip NCC fields | (b) + the source-of-truth `MapRect` is decoupled from any one locator. Bundles read metrics off `LocateMetrics`, not off `MapRect`. |

**Recommendation: (c).** `MapRect` is consumed in many places ([`AutoCalibrationEngine`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs), [`CalibrationAttemptContext`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs), [`MapRectJson`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs), the screenshot↔texture transform helpers); only `MapRect.AutoDetectScore` and `MapRect.SourceScaleFactor` are NCC-specific. Stripping them removes a clean point of confusion and keeps `MapRect` purely geometric. Bundle JSON migrates accordingly (see *Bundle JSON migration*).

```csharp
public sealed record LocateMetrics(
    int InlierCount,        // RANSAC survivors of EstimateAffinePartial2D
    int CandidateCount,     // Lowe-filtered candidates RANSAC chose from
    double InlierRatio,     // = InlierCount / CandidateCount
    double Scale,           // recovered scale (texture pixel → screenshot pixel)
    double RotationDegrees, // signed; expected ~0
    bool Mirror,            // always false for AffinePartial2D; reserved for symmetry
    double Tx, double Ty,   // recovered translation
    double ResidualPixels); // mean of per-inlier (||T·p_T − p_S||) in screenshot pixels

public sealed record MapRegionRefineResult(
    MapRect? AcceptedRect,        // null = rejected by gate
    MapRect? RawFitRect,          // populated whenever RANSAC found a fit, gate-pass-or-not
    LocateMetrics? Metrics);      // populated whenever RANSAC found a fit
```

`Metrics` is non-null exactly when `RawFitRect` is non-null. `AcceptedRect` is non-null when the gate (see below) accepts. Three-way state: `(null, null, null)` = RANSAC found no fit (too few correspondences); `(null, rect, m)` = RANSAC found a fit the gate rejected (close miss or wrong-fit — `m` lets the engine and the bundle log *which*); `(rect, rect, m)` = accept.

### Descriptor caching

Texture-side ORB cost is ~100–200 ms per locate. The texture is **canonical** (asset-pipe-driven, gated on PG version + hash via [`CanonicalAssetHashGate`](../../../src/Mithril.MapCalibration/Detection/CanonicalAssetHashGate.cs)); the descriptors derived from it are equally canonical for fixed ORB parameters. Cache them.

**Format.** Two new files alongside the existing `map-texture-<area>.{json,bin}` in the per-area asset cache directory:

| File | Contents |
|---|---|
| `map-texture-<area>.orb.json` | Schema-versioned manifest: `SchemaVersion`, `Area`, `PgVersion`, `KeypointCount`, `DescriptorDim` (32 for ORB), `OrbParamsHash`, `PixelSha256` (links to the source texture's pixel hash for invalidation), `BlobSha256` (integrity over the .orb.bin payload) |
| `map-texture-<area>.orb.bin` | DeflateStream-compressed payload: `keypointCount × (Pt2f x, Pt2f y, float size, float angle, float response, int octave) + keypointCount × 32 bytes` (the descriptor matrix). |

**Cache key / invalidation.** A cached `.orb.{json,bin}` pair is valid iff:

- It exists.
- `orb.json.SchemaVersion` matches what the current binary expects.
- `orb.json.PixelSha256` matches the source texture's manifest `PixelSha256` (cache invalidates when the texture is rebuilt for a new PG version, or when the texture file is touched out-of-band).
- `orb.json.OrbParamsHash` matches the SHA-256 of the canonical ORB params struct (`nFeatures, scaleFactor, nLevels, edgeThreshold, firstLevel, WTA_K, scoreType, patchSize, fastThreshold`). When we tune ORB params, the hash changes, every area's cache rebuilds on first locate.
- The actual `orb.bin` content's SHA-256 matches `BlobSha256`.

All checks fail-soft: any mismatch → discard, recompute, write fresh. Never read a wrong descriptor cache.

**Where the cache populates.** Two viable paths:

1. **Lazy populate from `FeatureMatchingRefiner`.** First call for an area: descriptor cache absent → ORB the texture → write the pair. Subsequent calls: cache hit, ORB the screenshot only, BFMatch, RANSAC. **Recommended for v1.**
2. **Sidecar precompute.** The asset-extractor sidecar (issue #931) writes the descriptors alongside the texture in the same extract step. Requires the sidecar to take a dependency on OpenCvSharp (today it doesn't — it uses AssetsTools / System.Drawing).

**Recommendation: (1).** Sidecar precompute is the eventual right answer (first-locate latency goes to ~80–150 ms instead of paying the ~150 ms texture-ORB price once per area), but it adds OpenCvSharp to the sidecar's dependency footprint and requires schema coordination across two PRs (sidecar emits, refiner consumes). Lazy-populate gets the steady-state win immediately and the file format is identical, so the sidecar can fill the same slot later without a second migration. Filed as a follow-up: see *Open questions / follow-ups*.

The cache reader/writer lives in `src/Mithril.MapCalibration.Capture/Internal/` (e.g. `CachedOrbDescriptorProvider`), mirroring [`CachedBaseTextureProvider`](../../../src/Mithril.MapCalibration/Detection/Internal/CachedBaseTextureProvider.cs)'s shape but in the Capture project so it can hold OpenCvSharp keypoint/descriptor types. BCL-only types (the manifest record) live alongside or in `MapCalibrationJsonContext` if a shared serializer is needed.

### Engine integration

[`AutoCalibrationEngine.RunAttemptCoreAsync`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) line ~263 calls `_refiner.Refine(gray, baseTexture, RefineMinScore)`. The engine doesn't know anything about NCC or ORB — it talks to `IMapRegionRefiner`. Cutover work:

- DI registration in [`CaptureServiceCollectionExtensions`](../../../src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs): replace the `TextureRegistrationRefiner` singleton with `FeatureMatchingRefiner`.
- Delete the `const double RefineMinScore = 0.5` field from `AutoCalibrationEngine`; replace the call site with `_refiner.Refine(gray, baseTexture)`. (The third arg goes away when `IMapRegionRefiner` reshapes — Task in PR-3.)
- The `LocatorBestRect` property on `CalibrationAttemptContext` reshapes: today it's a `MapRect?` carrying NCC fields; under FM it becomes `RawFitRect` + `LocateMetrics?` (two properties replacing one). Rename the property to `LocatorRawFit` and add `LocatorMetrics`.
- Log lines that read `best.AutoDetectScore` / `best.SourceScaleFactor` switch to reading `metrics.InlierRatio` / `metrics.Scale`.

The accept path downstream (`ImageOps.Crop`, `ImageOps.Resize(baseTexture, …)`, `_solver.Solve(request, references)`) is **unchanged**. A more-accurate located rect gives the solve cleaner input — strictly better — but there's no semantic seam to revisit.

## Gate criteria

Today: `RefineMinScore = 0.5` (NCC). One scalar. The Kur live failure is exactly this gate rejecting at 0.473.

Under FM, the natural metric is two-dimensional: inlier count + inlier ratio. Both have intrinsic meaning ("how many independent correspondences agree on the same transform" and "how much of the candidate pool agrees"). Prototype evidence:

| Bundle | Inliers | Candidates | Ratio |
|---|---|---|---|
| Kur live | 1066 | 1089 | 0.979 |
| Kur study | 1076 | 1127 | 0.955 |
| Serbule study | 1135 | 1159 | 0.979 |
| Eltibule study | 451 | 472 | 0.956 |

All clean wins sit ≥0.95 ratio. The lowest *count* is Eltibule's 451 (smaller texture → fewer keypoints → fewer Lowe survivors → fewer RANSAC inliers, but the *ratio* holds). A wrong-area negative test (run FM with the Eltibule texture against a Kur screenshot, or vice versa) should produce **either** "no fit" (RANSAC fails to converge — most likely) **or** a fit with very few inliers and a much lower ratio (random correspondences don't agree on the same transform).

**Proposed gate floors (initial, subject to calibration on the study set):**

| Field | Floor | Source |
|---|---|---|
| `InlierCount` | ≥ **50** | Well below the 451 floor observed in the study set; preserves margin for areas with smaller textures than Eltibule's. |
| `InlierRatio` | ≥ **0.50** | Half of the 0.95+ observed in real wins; high enough that random-correspondence noise won't clear it. |
| `\|RotationDegrees\|` | ≤ **0.5** | The four study captures range 0.002°–0.009°. 0.5° is two orders of magnitude above the noise floor; anything beyond is "not a real fit". |

**Initial settings — proposed location.** A `MapCalibrationLocateOptions` POCO in `src/Mithril.MapCalibration.Capture/` (parallel to `CaptureDiagnosticsOptions`), carrying `InlierFloor` (default 50), `InlierRatioFloor` (default 0.50), `MaxRotationDegrees` (default 0.5), `OrbNFeatures` (default 8000), `LoweRatio` (default 0.75), `RansacReprojectionThresholdPx` (default 3.0). DI singleton. The defaults ship with PR-1 and stay there unless the calibration study below moves them.

**Calibration plan (PR-1 deliverable, before defaults land).** Run `FeatureMatchingRefiner` against:

1. Every bundle currently in the study set (`tests/Mithril.MapCalibration.Tests/Fixtures/` style — or whatever folder the implementer surveys; the Eltibule / Serbule / Kur bundles called out above are the known set).
2. The live Kur-rejected bundle from `~/AppData/Local/Mithril/diagnostics/calibration/AreaKurMountains-20260602-192055-747-rejected-map-not-located/`.
3. A handful of synthetic negatives: feed the Kur texture against an Eltibule screenshot, the Serbule texture against a Kur screenshot, etc. Expect either no-fit or `(low count, low ratio)`.

Record `InlierCount`, `InlierRatio`, `RotationDegrees` per bundle. The defaults above must:

- **Accept** every real bundle in (1) and (2).
- **Reject** every synthetic negative in (3).

If either fails on the candidate defaults, retune *before* the PR-1 PR is opened. The PR-1 commit log carries the survey table.

**Why no `inlierCount` *or* `inlierRatio` gate is enough alone.** A high-count low-ratio result means "lots of correspondences but most disagree on the transform" — could indicate two competing transforms (e.g. self-similar terrain), inadmissible. A low-count high-ratio result means "few correspondences but they all agreed" — could indicate a sparse-feature area where the agreement is coincidental. Requiring both floors filters both modes.

## Bundle JSON migration

Current shape ([`CalibrationBundleJson.cs:6-19`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs)):

```csharp
public sealed record AttemptJson(
    int SchemaVersion,        // current top-level version
    /* … */,
    MapRectJson? LocatorBest = null);  // populated on accept + map-not-located reject

public sealed record MapRectJson(
    int SchemaVersion,
    int OriginX, int OriginY, int Width, int Height,
    int TextureWidth, int TextureHeight,
    double? AutoDetectScore,   // NCC ladder peak — meaningless under FM
    double? SourceScaleFactor);// ladder rung factor — meaningless under FM
```

`MapRectJson` is also used for `04-maprect.json` (the **accepted** rect that the engine handed downstream). The two uses are semantically different — one is the locator's verdict, the other is the geometric record — but share the type today, which is why `MapRectJson` carries the NCC fields awkwardly.

**Proposed change — break them apart.**

```csharp
public sealed record AttemptJson(
    int SchemaVersion,        // BUMP from current to current+1
    /* … */,
    LocatorBestJson? LocatorBest = null);  // type change

public sealed record MapRectJson(           // pure geometry; NCC fields dropped
    int SchemaVersion,
    int OriginX, int OriginY, int Width, int Height,
    int TextureWidth, int TextureHeight);

public sealed record LocatorBestJson(
    int SchemaVersion,                       // own SchemaVersion: 1 (new)
    // The raw fit rect — populated whenever RANSAC found a fit, gate-pass-or-not:
    int OriginX, int OriginY, int Width, int Height,
    int TextureWidth, int TextureHeight,
    // FM metrics:
    int InlierCount,
    int CandidateCount,
    double InlierRatio,
    double Scale,
    double RotationDegrees,
    double Tx, double Ty,
    double ResidualPixels,
    bool GateAccepted,                       // = whether AcceptedRect was non-null
    string? GateRejectReason);               // human-readable when GateAccepted = false
```

**Schema version on `AttemptJson` bumps.** A reader of an old bundle can dispatch on `AttemptJson.SchemaVersion`: ≤current shape → expect `LocatorBest: MapRectJson`; ≥current+1 → expect `LocatorBest: LocatorBestJson`. Treat this as a **breaking shape change** rather than additive: the type at that JSON path is different. New version of the engine writes the new shape only; old bundles on disk continue to deserialize via whatever legacy reader exists in tools (today there is no consumer; the diagnostic bundles are read by humans + the synthesis probe which reads only `07-deviation.png`-class artifacts, not `01-attempt.json`).

**Why not preserve the field name `LocatorBest` with a *union* type.** `System.Text.Json` source-generation discourages polymorphic union DTOs without explicit `[JsonDerivedType]` plumbing; the cost of supporting both shapes in the same field is higher than the cost of bumping the top-level `SchemaVersion`. The bundles are not a long-lived API — they're per-attempt diagnostic snapshots — so a shape bump is the right granularity.

**No follow-up MapRect cleanup needed.** Stripping `AutoDetectScore`/`SourceScaleFactor` off the `MapRect` record itself (option (c) under *Result shape*) means `MapRectJson` (used for `04-maprect.json`) no longer has the two `double?` fields. Existing readers tolerate missing optional fields (default `null`); the field count just shrinks.

## What's retired

Under the hard cutover (see *Rollout* below), the following code paths are deleted entirely — *not* deprecated, *not* kept behind a flag:

| Surface | Why retired |
|---|---|
| `MapRectLocator.AutoDetect(GrayImage, GrayImage, double[, int])` + `AutoDetectBest(…)` overloads | The NCC scale ladder. Replaced by `FeatureMatchingRefiner.Refine`. No remaining consumer. |
| `MapRectLocator.DefaultWorkingLongEdgePx`, `RefineScaleFactor`, `BuildCandidateScales` | Ladder internals. Same fate. |
| `MapRectLocator` (the class) | Empty after the above. Delete the file. The `MapRect` record moves to its own file under `Detection/` (e.g. `Detection/MapRect.cs`). |
| `MapRect.AutoDetectScore`, `MapRect.SourceScaleFactor` (record properties) | NCC-specific metadata on a geometric type. Removed (see *Result shape* option (c)). |
| `TextureRegistrationRefiner` | The whole class. Replaced by `FeatureMatchingRefiner`. Delete the file. |
| ECC sub-pixel refinement (`Cv2.FindTransformECC`, `MaxIterations = 200`, `Epsilon = 1e-6`, `GaussFiltSize = 5`) | Bolt-on workaround for NCC's coarse output. RANSAC over hundreds of inliers is sub-pixel by construction; no ECC step needed. Disappears with `TextureRegistrationRefiner`. |
| `RefineMinScore = 0.5` (constant in `AutoCalibrationEngine`) | Hardcoded NCC threshold. Replaced by the inlier-count + ratio + rotation gate inside the refiner. |
| `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs` | The diagnostic prototype. Production refiner subsumes it. **Deleted by the engine-cutover PR, not before** — the prototype is the reference implementation against which `FeatureMatchingRefiner`'s extraction is reviewed. |
| `TextureRegistrationRefinerTests.cs` | Tests for the deleted class. |

**Surfaces that explicitly stay:**

| Surface | Why preserved |
|---|---|
| `NccTemplateMatch` | The solve step still uses it (icon detection per [`WholeImageTemplateDetector`](../../../src/Mithril.MapCalibration/Detection/WholeImageTemplateDetector.cs) and [`DeviationBlobCalibrationDetector`](../../../src/Mithril.MapCalibration/Detection/DeviationBlobCalibrationDetector.cs)). Different problem, different scale, NCC is still the right tool there. |
| `MapRect` (record) | Pure geometry, consumed everywhere the locate → solve pipeline carries a rect. Keep, with the two NCC properties dropped. |
| `MapRegionRefineResult` (record) | Shape evolves (`BestCoarseRect` → `RawFitRect` + `LocateMetrics`). Same role: locator output for the engine. |
| `IMapRegionRefiner` interface | Same role: insulates the engine from the locator implementation. Signature evolves in PR-3 (the `double minScore` arg drops). |
| `CachedBaseTextureProvider` | Texture caching is unchanged; FM descriptors cache *alongside* the texture, not in place of it. |

## Rollout

**Recommendation: hard cutover, no fallback to the NCC ladder.**

Three reasons:

1. **The current locate is broken on Kur.** Anyone running today's code on Kur Mountains hits "rejected-map-not-located" (PR #1008's evidence). Shipping FM with a fallback to NCC means a user whose FM run rejects falls back to … the locate that we know rejects Kur. Worst case: the fallback also rejects, the user sees the same error message, no progress. Best case: the fallback accepts a wrong-rect on a borderline zone, and now the user has a wrong calibration persisted via the fallback path with no diagnostic clarity about which locator produced it.
2. **No semantic seam.** The engine talks to `IMapRegionRefiner`. There is no "which refiner did this" outcome value in the API; a fallback would have to be an internal-to-refiner two-tier strategy. The internal-strategy refiner becomes a much harder thing to reason about than either pure refiner alone.
3. **Production code carries the cost of fallback forever.** A two-week deprecation window doesn't help — bundles written in that window become bimodal, callers handling both shapes ship forever. The cleanest break is the cheapest break.

**What a fallback would look like (rejected).** Keep `TextureRegistrationRefiner` registered in DI; add a wrapper refiner that calls FM first, falls back to ECC-NCC on RANSAC convergence failure. The wrapper persists. Three months later when nobody remembers why, removing it requires re-validating that no production path quietly relies on the NCC backstop. The added flexibility buys nothing the calibration study above doesn't already buy — if FM doesn't pass the negative tests, we don't ship.

**Rollback plan if FM ships and is wrong.** Revert the engine-cutover PR. The PR-1 + PR-2 work (refiner class + descriptor cache) stays in tree as dead-but-tested code; the next attempt at a cutover re-uses it after fixing the failure mode. No data on disk is corrupted — bundle JSON shape moved forward, but it's a *diagnostic* format; failed FM runs simply produce empty `LocatorBest` blocks under the new schema.

## Risks and open questions

| Risk | Mitigation | Verification |
|---|---|---|
| Perf under contended CPU (other concurrent capture / encoding) | Prototype's ~200–300 ms is already well below the 3–5 s NCC ladder it replaces; the descriptor cache cuts steady-state by ~50%. The locate is single-attempt-per-hotkey-press, not a hot loop. | Time the refiner on the live Kur bundle as a perf smoke; record actual ms in the PR-1 commit log. No hard threshold gate. |
| A zone with so few ORB features that the InlierCount floor (50) rejects a correct fit | Calibration study against the bundle corpus + synthetic negatives nails the floor *before* PR-1 lands defaults. If a future zone misses, the floor was wrong, not the locator. | The PR-1 calibration table goes into the commit log. A future bundle showing "real area, real fit, count=42" reopens the gate calibration. |
| Solve-step assumptions about locate's output break under sub-pixel-accurate input | Solve consumes `crop = ImageOps.Crop(gray, ...)` + `alignedTexture = ImageOps.Resize(baseTexture, clamped.Width, clamped.Height)`. Both crop and resize integerise; sub-pixel locate accuracy is rounded away at the engine boundary today. No solve-step change anticipated. | Run the existing `MapCalibrationSolveEngineTests` replay suite against an FM-located bundle (vs current locator-located fixture) — expect equivalent or better solve outcomes. |
| ORB params (`nFeatures=8000`, scale factor, levels) need per-zone tuning | The prototype uses one param set across four very different zones; no evidence yet that per-zone tuning is necessary. If it is, the `OrbParamsHash` in the descriptor cache forces re-extraction on a param change — the seam is in place. | Track in calibration study; if any zone needs tuning, file a follow-up. |
| Live capture rotation is *not* zero on some setting (e.g. user rotated map) | Modern PG UI exposes no rotation. The 0.5° gate rejects rotated fits — symptom would be "intermittent locate rejection" on a rotated config we don't anticipate. | Bundle JSON's `LocatorBest.RotationDegrees` makes a real rotation visible in the diagnostic. If it ever happens, expand the rect carrier to non-axis-aligned (significant scope). |
| Descriptor cache file corruption | All reads validate `BlobSha256` against manifest before use; mismatch → discard + rebuild. Mirrors `CachedBaseTextureProvider`'s integrity check. | Unit test: write a cache pair, corrupt one byte of `.bin`, verify reader rebuilds without throwing. |

**Open questions for the implementer:**

1. **Should `LocateMetrics.ResidualPixels` be the mean, median, or 95th-percentile of per-inlier residuals?** Mean is the simplest; median is robust to a long-tail outlier RANSAC let through; 95th-percentile is the most informative for diagnostic triage. Recommend median in v1.
2. **Where exactly does `MapRect` move when `MapRectLocator.cs` is deleted?** New file `src/Mithril.MapCalibration/Detection/MapRect.cs` is the obvious answer; mention in the engine-cutover PR description that this is a pure relocation, no logic change.
3. **Should the descriptor cache be sidecar-precomputed in a follow-up?** Out of scope here. File as a follow-up issue (see below) once PR-1's measurement of texture-ORB cost on real hardware is in the commit log.

## Test plan

Three classes of test, all in `tests/Mithril.MapCalibration.Capture.Tests/`:

### 1. Unit tests on synthetic inputs (PR-1)

`FeatureMatchingRefinerTests` — pure unit tests against generated inputs:

- **Identity fit.** Construct a `GrayImage` with a checkerboard or sinusoidal grating; refine itself against itself. Assert `RawFitRect ≈ (0, 0, W, H)`, `Scale ≈ 1.0`, `RotationDegrees ≈ 0`, `InlierRatio > 0.9`.
- **Half-scale fit.** Resize the source to 0.5× and refine against the original. Assert `Scale ≈ 0.5`, the recovered rect's W/H ≈ 0.5 × source.
- **Translated fit.** Crop a region from the source at known origin and refine that crop against the source. Assert `(Tx, Ty)` recover the crop origin.
- **Insufficient features (rejection).** Refine a uniform-gray image against a textured image; assert `Result.AcceptedRect == null`, `Metrics == null` *or* `Metrics.InlierCount < 50`.
- **Rotation gate.** Construct a rotated input (5° rotation); assert the rotation gate rejects (`AcceptedRect == null` even when RANSAC converges).

### 2. Replay tests against the live Kur bundle (PR-1)

`FeatureMatchingRefinerReplayTests` — reads the Kur live bundle from a fixtures directory and asserts production-quality outcomes:

```csharp
[Fact]
public void Recovers_kur_ground_truth_rect_within_two_pixels()
{
    var capture = LoadGrayCaptureFromBundle("AreaKurMountains-20260602-192055-747");
    var texture = LoadBaseTextureForArea("KurMountains");

    var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
    var result = refiner.Refine(capture, texture, minScore: 0 /* ignored */);

    result.AcceptedRect.Should().NotBeNull();
    result.Metrics.Should().NotBeNull();
    result.Metrics!.InlierRatio.Should().BeGreaterThan(0.90);
    result.AcceptedRect!.OriginX.Should().BeApproximately(159, 2);
    result.AcceptedRect.OriginY.Should().BeApproximately(82, 2);
    result.AcceptedRect.Width.Should().BeApproximately(971, 2);
    result.AcceptedRect.Height.Should().BeApproximately(973, 2);
}
```

Same shape for Eltibule and Serbule study bundles (recovered rect within ±2 px of the study's pre-cropped origin = (0, 0) for those two).

### 3. Synthetic negatives (PR-1)

`FeatureMatchingNegativeTests` — cross-area rejection:

```csharp
[Fact]
public void Rejects_kur_texture_against_eltibule_capture()
{
    var capture = LoadGrayCaptureFromBundle("eltibule-working-bundle");
    var kurTexture = LoadBaseTextureForArea("KurMountains");

    var refiner = new FeatureMatchingRefiner(new MapCalibrationLocateOptions());
    var result = refiner.Refine(capture, kurTexture, minScore: 0);

    result.AcceptedRect.Should().BeNull();
}
```

### 4. Descriptor cache integration (PR-2)

`CachedOrbDescriptorProviderTests` — under a temp-dir fixture:

- Write descriptors for an area; reading them back matches.
- Corrupt one byte of `.bin`; reader detects mismatch and rebuilds without throwing.
- Change `OrbParamsHash` between write and read; reader detects mismatch and rebuilds.
- Change source texture's `PixelSha256`; cache invalidates.

### 5. Engine-cutover smoke test (PR-4)

`AutoCalibrationEngineFeatureMatchingTests` — end-to-end with `FeatureMatchingRefiner` wired into the engine:

- Run the engine against the Kur live bundle (capture + bbox provider stubbed); assert it produces a `CalibrationSolveResult` with a non-null calibration. Today this scenario produces a `rejected-map-not-located` outcome; the cutover PR is the cell where that flips green.

### 6. Manual verification (PR-4 PR description)

Per the brief: rerun calibration against the Kur live bundle through Mithril proper, confirm a new `01-attempt.json` shows the `accepted` outcome with a high inlier ratio + correct rect. Document the result in the PR-4 PR body (paste the relevant `01-attempt.json` excerpt).

---

## Milestones

| PR | Scope | Depends on |
|---|---|---|
| **PR-1** | `FeatureMatchingRefiner` class + `LocateMetrics` record + `MapRegionRefineResult` shape evolution + `MapCalibrationLocateOptions` POCO + unit tests + replay tests + negative tests. **No DI wire-up** — engine still uses `TextureRegistrationRefiner`. PR commit log carries the calibration-study survey table. | This spec |
| **PR-2** | `CachedOrbDescriptorProvider` + `map-texture-<area>.orb.{json,bin}` format + cache integration into `FeatureMatchingRefiner`. Measured locate-cost reduction in PR body. | PR-1 |
| **PR-3** | Bundle JSON shape migration (`AttemptJson.SchemaVersion` bump; new `LocatorBestJson`; strip `AutoDetectScore` / `SourceScaleFactor` from `MapRectJson` and `MapRect`). Rename `LocatorBestRect` → `LocatorRawFit` on `CalibrationAttemptContext`, add `LocatorMetrics`. `IMapRegionRefiner.Refine` signature reshape (`double minScore` arg drops). | PR-2 |
| **PR-4** | DI swap (`TextureRegistrationRefiner` → `FeatureMatchingRefiner` in `CaptureServiceCollectionExtensions`). Delete `MapRectLocator.cs`, `TextureRegistrationRefiner.cs`, `TextureRegistrationRefinerTests.cs`, `tests/Mithril.MapCalibration.Capture.Tests/FeatureMatchingPrototype.cs`. Delete `RefineMinScore` from `AutoCalibrationEngine`. Move `MapRect` record to its own file. Manual-verification screenshot in PR body. | PR-3 |

## Follow-ups (out of scope)

- **Sidecar precompute of ORB descriptors.** Move ORB into the asset-extractor sidecar; have it write the `.orb.{json,bin}` pair as part of the extract step. Adds OpenCvSharp to the sidecar's footprint; saves the one-time ~150 ms texture-ORB cost on first locate per area. Format-compatible with PR-2 — same files, same schema. File when PR-2 lands.
- **Non-zero rotation support.** If a future PG UI exposes map rotation (or we add rotated-input support for VR / multi-monitor edge cases), broaden the rect carrier from axis-aligned `MapRect` to a quad. Big scope; not anticipated in PG today.
- **Per-area ORB tuning.** If the calibration study turns up an area that needs different `nFeatures` / `scaleFactor`, add a per-area override on `MapCalibrationLocateOptions`. The `OrbParamsHash` in the descriptor cache already accommodates this.

## Verification owed

- **Calibration study table in PR-1 commit log.** `InlierCount` / `InlierRatio` / `RotationDegrees` per bundle in the corpus + the synthetic negatives. The proposed defaults (50 / 0.50 / 0.5°) must accept all reals and reject all negatives in the table; if not, retune before opening PR-1.
- **Live Kur bundle replay test (PR-1 deliverable, asserted in CI).** The rejected bundle becomes a green test asserting the recovered rect within ±2 px of (159, 82, 971, 973).
- **Descriptor cache round-trip test (PR-2 deliverable, asserted in CI).** Write descriptors; corrupt a byte; assert detect + rebuild.
- **Manual-verification screenshot in PR-4.** Re-run the calibration against the Kur live bundle; paste the resulting `01-attempt.json` excerpt showing `Outcome: accepted` + the new `LocatorBest` shape with the high inlier ratio.
