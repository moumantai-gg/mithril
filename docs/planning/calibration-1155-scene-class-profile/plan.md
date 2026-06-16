# Plan — Map auto-cal: scene-class profile (Indoor vs Outdoor)

Companion to [`spec.md`](spec.md). All section anchors below refer to the spec unless qualified.

## Revision history

This plan was re-sequenced after Phase 0 spike findings ([PR #1162](https://github.com/moumantai-gg/mithril/pull/1162), [`measurements/`](measurements/)). Key changes from original:

- **Phase 2 re-scoped from "Indoor untyped detection" to "Indoor icon-blob recall ([#1163](https://github.com/moumantai-gg/mithril/issues/1163))"** — the actual root cause per spike.
- **Phase 3 re-scoped from "chroma pre-filter" to "peak-luma pre-filter"** — chroma doesn't separate; peak luma does (real-icon: 0.91; floor noise: 0.22-0.40).
- **Untyped detection demoted to Phase 4** — was the original load-bearing pick; preserved as a tier-2 quality improvement once Phase 2 lifts blob recall.
- **Synthesis-J demoted to Phase 5 (Shadow mode)** — no ground-truth-good Indoor cals exist to derive an Enforcement threshold; v1 ships Shadow-only with the formulas computed-and-logged. Phase 5-v2 revisits enforcement once Phase 2 produces verifiable Indoor cals.

The original Phase 2/3/4 content is preserved verbatim in the relevant new-phase sections; the *timing* changes, not the implementation details.

## Phasing summary

| Phase | Scope | Behavior change? | Status / gated on |
|-------|-------|------------------|-------------------|
| **0. Spike** | Throwaway harness — computed §6 verification-owed measurements against existing bundle corpus. Output: `measurements/*.md` files. | **No** | ✅ **SHIPPED** ([PR #1162](https://github.com/moumantai-gg/mithril/pull/1162)) |
| **1. Scaffolding** | Introduce `SceneClass` + `SceneCalibrationProfile`; alpha-coverage classifier; bundle schema v4→v5. Both profiles initially identical to today's constants. | **No** | ✅ **SHIPPED** (folded into [PR #1168](https://github.com/moumantai-gg/mithril/pull/1168)) |
| **2. Indoor icon-blob recall** ([#1163](https://github.com/moumantai-gg/mithril/issues/1163)) | Stage-attribution audit + per-profile tuning of `LowNcc`, morph-close radius, chroma-aware deviation kernel, `BlobOptions.MinArea`. The load-bearing fix. | Indoor only | ✅ **SHIPPED** ([PR #1168](https://github.com/moumantai-gg/mithril/pull/1168), closed [#1163](https://github.com/moumantai-gg/mithril/issues/1163)) |
| **2.5. Morph-open carrier** (post-Phase-3 follow-up) | Audit-recommended candidate to split the IconB+C merge blob the Phase 2 measurement (`indoor-recall-merge-fix-candidates.md`) deferred. Adds `Morphology.Open` + `MorphOpenRadiusPx` profile knob + `openRadius` parameter on `DetectIconBlobs`. **Negative measurement result** (`indoor-recall-phase-2.5-morph-open.md`) — no `(openRadius, closeRadius)` combo splits B+C; ships disabled (`MorphOpenRadiusPx = 0` on both profiles) as a carrier for future flips. | None (carrier-only ship) | ✅ **SHIPPED** ([PR #1171](https://github.com/moumantai-gg/mithril/pull/1171)) |
| **2.6. Pre-deviation luma threshold** ([#1172](https://github.com/moumantai-gg/mithril/issues/1172)) | Load-bearing alternative to the negative 2.5 result: pre-deviation raw-luma byte threshold inside `LocalNccDeviation.DeviationMap` severs the overlapping deviation halos of merged NPC pips BEFORE the NCC window can smear them together. Indoor profile ships `MinLumaForDeviation = 200`; Outdoor stays 0 (byte-identical). Static-bundle measurements: 06-13 RIC 3/6→5/6 with B+C split; 06-15 NPC detections 0/3→2/3. | Indoor only | ✅ **SHIPPED** ([PR #1173](https://github.com/moumantai-gg/mithril/pull/1173)). Live verification per [`phase-2.6-live-verification.md`](measurements/phase-2.6-live-verification.md) still owed; gated by #1172 closing once Arthur's capture is in. |
| **3. Peak-luma pre-filter** | After blob classification, reject blobs whose raw-BGRA bbox `PeakLuma < MinPeakLuma` (~0.7). Cleanly suppresses floor-noise blobs that survived recall improvement. | Indoor only | ✅ **SHIPPED** ([PR #1169](https://github.com/moumantai-gg/mithril/pull/1169)) |
| **4. Indoor untyped detection** (demoted from original Phase 2) | New `UntypedDeviationBlobDetector`; `TypeAwareRansacSolver` accepts untyped pool. Tier-2 quality improvement; useful when Phase 2's typed pool shows residual typing-error mis-correspondences. | Indoor only | **DEFERRED (conditional)** — only ships if Phases 2 / 2.6 / 3 leave residual typing-error failures after live verification. |
| **5. Indoor synthesis-J Shadow-mode** | Synthesis-J `jMin/nMin` becomes profile-driven; Indoor runs Shadow-only for v1 (formulas computed + logged, not enforced). Phase 5-v2 revisits enforcement. | Indoor only | **DEFERRED** — gated on ground-truth Indoor cals accumulating from #1172 live verification + corpus expansion ([#1176](https://github.com/moumantai-gg/mithril/issues/1176)). |
| **6. Outdoor regression battery** | Replay-fixture comparison: Outdoor accept rate / inlier count / residual byte-identical. | None | Gating activity — green on each shipped PR (1168 / 1169 / 1171 / 1173) by construction (Outdoor profile carries pre-#1155 constants). |

Phases 2 / 3 / 5 can run in parallel after Phase 1 lands; Phase 4 is sequenced after Phase 2. Phase 6 is a gating activity on every prior phase's PR.

**Post-2.6 follow-ups filed:**

- [#1174](https://github.com/moumantai-gg/mithril/issues/1174) — Indoor recall: NPCc on 06-15 undetected at every luma threshold (separate mechanism).
- [#1175](https://github.com/moumantai-gg/mithril/issues/1175) — `BuildDeviationMask` alpha-zero interior gap (#1148 follow-up).
- [#1176](https://github.com/moumantai-gg/mithril/issues/1176) — Broader-corpus expansion for `MinLumaForDeviation = 200` (GoblinDungeon_TopFloor / BrainBugCaverns / HumanCellar once dev-local bundles exist).

**Why Phase 0 was run as a spike.** The original spec committed to specific starter values for Indoor (`RenderSizePx=12`, `MinChroma=0.30`, synthesis-J formula). Each was verification-gated, but having Phase 1 land with unvalidated placeholders and then revising them under §6 measurements would have been messier than running a throwaway spike upfront. The spike found that some of those starter values were wrong AND surfaced a bigger architectural issue (detection-recall failure); both saved real work downstream.

### Phase 0 — Spike (DONE, [PR #1162](https://github.com/moumantai-gg/mithril/pull/1162))

**Goal.** ✅ Produce all §6 measurement docs against existing diagnostic-bundle corpus before code lands.

**Deliverables shipped under [`docs/planning/calibration-1155-scene-class-profile/measurements/`](measurements/):**

- ✅ [`scene-class-classification.md`](measurements/scene-class-classification.md) — §6.a CONFIRMED.
- ✅ [`indoor-render-size.md`](measurements/indoor-render-size.md) — §6.b REVISED (16, not 12).
- ✅ [`indoor-chroma-threshold.md`](measurements/indoor-chroma-threshold.md) — §6.c NEGATIVE → peak luma replaces chroma.
- ✅ [`indoor-synthesis-j-threshold.md`](measurements/indoor-synthesis-j-threshold.md) — §6.d PARTIAL → Shadow for v1.
- ✅ [`untyped-ransac-cost.md`](measurements/untyped-ransac-cost.md) — §6.e CONFIRMED.
- ✅ [`detection-recall-pivot.md`](measurements/detection-recall-pivot.md) — NEW out-of-§6 finding; spawned [#1163](https://github.com/moumantai-gg/mithril/issues/1163).

**Outcome:** spec + plan revised; [#1163](https://github.com/moumantai-gg/mithril/issues/1163) filed for Phase 2 root-cause work.

## Phase 1 — Scaffolding (pure refactor)

**Goal.** Introduce the scene-class axis end-to-end without changing any behavior. Both profiles emit the exact same parameters as today's universal constants.

### Phase 1 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/SceneClass.cs` | **new** | `enum SceneClass { Outdoor, Indoor }` with XML doc explaining intent + classification source. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **new** | `record SceneCalibrationProfile(int RenderSizePx, double? TypeFloor, double LowNcc, BlobOptions BlobOpts, DetectorPath DetectorPath, double RansacInlierPx, SynthesisRerankMode SynthesisMode, Func<int, double> SynthesisJMinFn, Func<int, int> SynthesisNMinFn)`. `Outdoor` static carries today's constants; `Indoor` initially identical. |
| `src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs` | **extend** | Expose `SceneClass GetSceneClass(string mapAssetKey)`. Same cache key as boundary mask. Reads `opaqueFraction = count(alpha ≥ 128) / (w × h)`; threshold `SceneClassOpaqueFractionThreshold = 0.95`. Cached fail-soft (null alpha → Outdoor default). |
| `src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs` | **extend** | Add `SceneClassOpaqueFractionThreshold` (default 0.95). |
| `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` | **edit** | Replace the `private const` block (lines 56–62) with `private readonly SceneCalibrationProfile _profile = ...` resolved per attempt via `_floorBoundary.GetSceneClass(mapAssetKey)`. Pass profile into `DetectionRequest` build. |
| `src/Mithril.MapCalibration.Detection/DetectionRequest.cs` | **extend** | Add `SceneCalibrationProfile Profile` field. Existing fields preserved (no per-field migration — profile bundles them). |
| `src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttempt.cs` (or wherever the schema lives) | **extend** | Schema v4 → v5 — add `sceneClass`, `sceneClassSource`, `sceneClassOpaqueFraction`, `profile` per spec §5.6. Migration: v4 attempts read as `sceneClass: "Outdoor"` for back-compat replay; new attempts emit v5. |
| `src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs` | **edit** | Consume `request.Profile` for `TypeFloor`, `LowNcc`, `BlobOpts`, `RenderSizePx`. No behavior delta — values still come from the Outdoor profile constants. |
| `src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs` | **edit** | Consume `request.Profile.RansacInlierPx`. No behavior delta. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` | **edit** | Consume `request.Profile.SynthesisMode`, `SynthesisJMinFn(refsTotal)`, `SynthesisNMinFn(refsTotal)`. No behavior delta. |
| `tests/Mithril.MapCalibration.Detection.Tests/SceneClassClassifierTests.cs` | **new** | Synthetic alpha buffers: all-opaque → Outdoor; 30% transparent → Indoor; degenerate (null alpha) → Outdoor (fail-soft). |
| `tests/Mithril.MapCalibration.Detection.Tests/SceneCalibrationProfileTests.cs` | **new** | `Outdoor.Equals(Outdoor)` (idempotent), Indoor and Outdoor initially identical at Phase 1, profile carries through `DetectionRequest`. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Assert all existing Outdoor replay fixtures continue to solve unchanged; assert one Indoor replay fixture (Hogan's) gets classified Indoor. |

### Phase 1 verification

- Verification owed §6.a (alpha-coverage threshold) — measure once against the canonical hash inventory; commit measurements as a Markdown table in this folder (`measurements/scene-class-classification.md`).
- Outdoor regression: full replay-fixture battery passes byte-identically. **Phase 1 PR must show this in CI before merge.**

### Phase 1 PR boundary

One PR. Title: `feat(map-calibration): scene-class profile scaffolding (#1155 phase 1)`. Body links spec.md + verification §6.a results.

## Phase 2 — Indoor icon-blob recall ([#1163](https://github.com/moumantai-gg/mithril/issues/1163))

**Goal.** Lift the upstream detection-recall ceiling so real-icon blobs actually survive into the typing/RANSAC step. The actual root cause per the spike — in the canonical Hogan's bundle, 4-5 of 5-6 visible icons aren't detected as blobs at all, and 17 of 18 Icon-class blobs are pure floor noise. No downstream typing strategy recovers correspondences that don't exist.

Implementation owned by [#1163](https://github.com/moumantai-gg/mithril/issues/1163); this section is the high-level breakdown.

### Phase 2 sub-steps

1. **Stage-attribution audit (no production code).** For each visible-but-undetected icon in 3+ Indoor bundles, trace where in the pipeline it gets lost:
   - Survives the deviation map (`07-deviation.png`)?
   - Survives the rim mask (`07c-rim-mask.png`)?
   - Survives the deviation mask (`07a-deviation-mask.png`)?
   - Survives morph-close (`07d-morphed.png`)?
   - Survives blob classification (`07e-blob-classification.png`)?

   Output: per-icon, per-bundle attribution table at `measurements/indoor-recall-stage-attribution.md`.

2. **Per-profile tuning candidates** (informed by the audit). Likely candidates:
   - `LowNcc` lower for Indoor (currently 0.5; Indoor low-contrast icons may need 0.3-0.4)
   - Per-profile morph-close radius (smaller for Indoor — current radius may merge icons into adjacent floor noise)
   - Chroma-aware deviation kernel (compare colour channels separately — luma profile may still differ in HSV space even if RGB chroma is zero)
   - Per-profile `BlobOptions.MinArea` floor (Indoor icons may form smaller blobs than the current 12 threshold)

3. **Comparison bundle:** The accepted Hogan's 06-10 cal solved with 4 inliers, meaning that bundle had ≥ 4 real-icon blobs detected — better recall than the 06-13 bundle. Identifying what differs (player position, ambient lighting, alpha-coverage of the captured region, in-game zoom level) informs the v1 fix.

### Phase 2 files (sketch — details land in [#1163](https://github.com/moumantai-gg/mithril/issues/1163))

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Add tunables identified by the audit — most likely `LowNcc`, `MorphCloseRadiusPx`, `BlobOptions.MinArea`. Indoor diverges; Outdoor stays identical to today's constants. |
| `src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs` | **edit** | Consume the new profile fields. No new types. |
| `src/Mithril.MapCalibration.Detection/Internal/DeviationBlobDetector.cs` | **edit** | Same — consume profile-driven knobs. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Indoor replay fixture (Hogan's 2026-06-13) asserts ≥ 4 Icon-class blobs with `PeakLuma > 0.7` (i.e., real-icon blobs survive). |

### Phase 2 verification

- **#1163 Phase 2 acceptance** — Hogan's 06-13 bundle's Icon-class blob count whose bbox contains ≥ 3 raw-BGRA pixels with luma > 0.78 must reach ≥ 4. Three other Indoor bundles measured to the same criterion.
- **Verification owed §6.f (outdoor regression)** — full Outdoor replay battery byte-identical. Gates the PR.

### Phase 2 PR boundary

One PR (could split if the stage-attribution audit identifies multiple independent fix candidates). Title: `feat(map-calibration): indoor icon-blob recall (#1163)`. Depends on Phase 1 merged.

## Phase 3 — Indoor peak-luma pre-filter

**Goal.** After Phase 2 lifts blob recall, suppress the floor-noise blobs that survived blob classification by requiring `PeakLuma > MinPeakLuma` per blob bbox. The spike showed real-icon blobs sit at PeakLuma 0.91 and floor noise at 0.22-0.40 — clean separation.

### Phase 3 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/Internal/PeakLumaFilter.cs` | **new** | Pure-BCL pixel arithmetic over the raw BGRA screenshot bbox. Returns peak luma (`max(R+G+B)/3 / 255.0`) per blob. |
| `src/Mithril.MapCalibration.Detection/BlobOptions.cs` | **extend** | Add `MinPeakLuma` (nullable double). Null → pre-filter disabled. |
| `src/Mithril.MapCalibration.Detection/Internal/IconBlobPipeline.cs` *(extracted in Phase 2 if not earlier)* | **edit** | After blob classification, drop blobs whose `PeakLumaFilter.PeakLuma(blob, rawShot) < BlobOpts.MinPeakLuma`. Emits per-blob diagnostic stage record (peak-luma value + drop reason) — feeds `07e-blob-classification.png` colour coding. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Indoor's `BlobOpts.MinPeakLuma ≈ 0.7` (revisit per broader-corpus measurement). Outdoor's `MinPeakLuma = null` (pre-filter inactive). |
| `tests/Mithril.MapCalibration.Detection.Tests/PeakLumaFilterTests.cs` | **new** | Synthetic blobs: bright pip → high peak luma; gray cobble blob → low peak luma; dark alpha-zero hole → near-zero peak luma. |
| `tests/Mithril.MapCalibration.Detection.Tests/IconBlobPipelineTests.cs` | **extend** | Indoor pipeline drops low-peak-luma blobs; Outdoor pipeline unchanged. |

### Phase 3 verification

- **Threshold corpus measurement** — expand the spike's single-bundle measurement to the broader Indoor corpus. Commit to `measurements/indoor-peak-luma-threshold.md`. **If no separating threshold exists in the broader corpus**, Phase 3 ships with `Indoor.MinPeakLuma = null` and a deferred sub-issue.
- Verification owed §6.f — full Outdoor replay battery byte-identical (peak-luma pre-filter is no-op outdoors).

### Phase 3 PR boundary

One PR. Title: `feat(map-calibration): indoor peak-luma pre-filter (#1155 phase 3)`. Depends on Phase 1 merged. Can run in parallel with Phase 2.

## Phase 4 — Indoor untyped detection (demoted from original Phase 2)

**Goal.** Once Phase 2 has lifted blob recall and Phase 3 has suppressed noise, observe whether typed-detection RANSAC's typing errors are limiting inlier count. If yes, untyped detection lets RANSAC discriminate type from geometric fit instead of per-blob NCC.

This phase is **conditional** — it only ships if Phases 2/3 leave residual typing-error failures. Otherwise it stays as a deferred follow-up.

### Phase 4 files

(Original Phase 2 file list, preserved verbatim — same implementation; just deferred.)

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/UntypedDeviationBlobDetector.cs` | **new** | Same deviation + rim + morph + classify pipeline as `DeviationBlobCalibrationDetector` (factored shared internals out into `Internal/IconBlobPipeline.cs`). Skips the `IconRenderScaler.RenderSized` + per-template-NCC loop. Emits `IReadOnlyList<UntypedDetection>` (one per surviving icon-class blob, anchor at centroid, score from `blob.PeakDev`). |
| `src/Mithril.MapCalibration.Detection/Internal/IconBlobPipeline.cs` | **new** (or **extracted by Phase 3** if it lands first) | Factored shared pipeline (deviation → flood-rim → morph → classify → blob list) extracted from `DeviationBlobCalibrationDetector`. Both detectors call this; one stops here, the other continues with per-blob NCC. |
| `src/Mithril.MapCalibration.Detection/UntypedDetection.cs` | **new** | `record UntypedDetection(CroppedFramePixel Anchor, double Score, int BlobOrdinal)`. |
| `src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs` | **edit** | Refactor to call shared `IconBlobPipeline.RunIconClassBlobs`; keep the per-template-NCC loop. No behavior change for Outdoor. |
| `src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs` | **edit** | Either split into `ITypedCalibrationDetector` + `IUntypedCalibrationDetector`, or unify via union (`OneOf` style) — pick at Phase 4 design micro-review. Default leaning: split, dispatch in `MapCalibrationSolveEngine` based on `profile.DetectorPath`. |
| `src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs` | **extend** | New overload `SolveTopK(IReadOnlyList<UntypedDetection> untyped, IReadOnlyList<LandmarkReference> allRefs, MapRect mapRect, int k, ILogger? logger)`. Pool construction: each detection × all refs (no type filter); per-pair pivot lookup via the ref's type → corresponding template's `PivotX/PivotY`; per-pair anchor correction applied at pool-build time. Inlier selection unchanged. Type label assigned at inlier resolution. |
| `src/Mithril.MapCalibration.Detection/Internal/PivotResolver.cs` | **new** | `IPivotResolver.GetPivot(string landmarkType) → (double X, double Y)` reading from the loaded `IconTemplate` set. Cached per-template lookup. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` | **edit** | Dispatch on `profile.DetectorPath`: Typed calls `DeviationBlobCalibrationDetector` → `Solve(Dictionary<string, List<TypedDetection>>)`; Untyped calls `UntypedDeviationBlobDetector` → `Solve(IReadOnlyList<UntypedDetection>)`. Synthesis-J path consumes the same `TopKCandidate` shape — no downstream change. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Flip Indoor's `DetectorPath = Untyped`, `TypeFloor = null`. Indoor `RenderSizePx` stays at 16 (no longer changes per spike §6.b). |
| `tests/Mithril.MapCalibration.Detection.Tests/UntypedDeviationBlobDetectorTests.cs` | **new** | Synthetic input: produces same blob set as typed detector minus the per-template NCC scores; coordinates byte-identical via shared pipeline. |
| `tests/Mithril.MapCalibration.Detection.Tests/UntypedRansacSolverTests.cs` | **new** | Pool size assertion (10 untyped × 13 refs = 130 candidates); type discrimination (synthetic mixed-type ground truth resolves to the right type via inliers); regression: typed-pool result preserved for same input. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Indoor replay fixture solves with same or better inlier count via untyped path vs typed path (assert improvement or no regression). |

### Phase 4 verification

- **Wall-clock benchmark** — extend the existing replay-fixture timing harness; assert no scene > 5 s solve. Per spike §6.e the estimate is "millis added" but a real benchmark with the actual code is needed before merge.
- **Outdoor regression** — full Outdoor replay battery byte-identical. Gates the PR.
- **Indoor improvement** — at least one Indoor bundle's inlier count strictly improves via untyped path vs typed path. If no improvement, Phase 4 doesn't ship.

### Phase 4 PR boundary

One PR. Title: `feat(map-calibration): indoor untyped detection (#1155 phase 4)`. Depends on Phase 2 merged + observed typing-error failure mode.

## Phase 5 — Indoor synthesis-J Shadow mode

**Goal.** Synthesis-J `jMin/nMin` becomes profile-driven; Indoor runs Shadow-only for v1 (formulas computed + logged, not enforced). Sets up Phase 5-v2 where enforcement gets revisited with ground-truth Indoor cals.

### Phase 5 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolverOptions.cs` | **edit** | `SynthesisJMin` and `SynthesisNMin` constants stay (Outdoor source of truth); Indoor formulas live in `SceneCalibrationProfile.Indoor.SynthesisJMinFn / SynthesisNMinFn`. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` | **edit** | Synthesis-J resolution: `jMin = profile.SynthesisJMinFn(refsTotal); nMin = profile.SynthesisNMinFn(refsTotal);`. Mode (Shadow/Enforced) per profile. For Indoor v1, mode = Shadow — value computed and logged but doesn't drive accept/reject. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Indoor's `SynthesisMode = Shadow`, `SynthesisJMinFn = refsTotal => max(1.5, 0.6 * refsTotal)`, `SynthesisNMinFn = refsTotal => max(3, ceil(0.4 * refsTotal))`. Outdoor unchanged. |
| `tests/Mithril.MapCalibration.Detection.Tests/SynthesisJAdaptiveThresholdTests.cs` | **new** | `refsTotal=11` → expected `jMin/nMin` from Indoor formula; `refsTotal=8` (outdoor border) → unchanged static; Outdoor profile path takes the static branch. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Hogan's 2026-06-13 bundle synthesis section shows the computed adaptive jMin/nMin in the bundle (`profile.synthesisJMin` field per §5.6). |

### Phase 5 verification

- Synthesis-J Shadow values visible in bundle `01-attempt.json` per §5.6 schema.
- **Outdoor regression** — full Outdoor replay battery byte-identical (Outdoor stays static jMin=8 / nMin=8).

### Phase 5 PR boundary

One PR. Title: `feat(map-calibration): indoor synthesis-J shadow-mode (#1155 phase 5)`. Depends on Phase 1 merged. Can run in parallel with Phase 2 / 3.

**Phase 5-v2** (deferred, separate issue when ready): once Phase 2 / 3 produce verifiable Indoor cals (e.g., manual spot-check of landmark projection on accepted cals), revisit the Indoor `SynthesisMode = Enforced` flip with measured thresholds. Open as a sibling sub-issue at that point.

## Test strategy summary

### Existing replay fixtures (preserve)

- Outdoor: Serbule, Eltibule, Kur Mountains. Each phase's PR **must** show byte-identical solve.
- Indoor: Hogan's (the existing PR #1148 bundle smoke). Mode-A scope; Mode-B re-uses for solve coverage.

### New replay fixtures (add)

- **Mode-B Indoor #1: `Map_HogansKeepBasement-20260613-230459-600`** — the primary bundle from this spec. ReplayFixture canonical-hash-keyed. Phase 2 assertion: ≥ 4 Icon-class blobs with PeakLuma > 0.7. Phase 3 assertion: those 4+ blobs survive the peak-luma pre-filter.
- **Mode-B Indoor #2: `Map_HogansKeepBasement-20260610-091533-358-accepted`** — the comparison "accepted but synthesis-J disagrees" bundle. Phase 2 assertion: recall improves to ≥ 4 (was already 4 inliers but with different blob distribution; the fix should preserve or improve). Phase 5 assertion: synthesis-J Shadow values match expectation.
- **Mode-B Indoor #3: `Map_GoblinDungeon_TopFloor-20260610-095806-692`** — sibling sub-zone. Phase 2 assertion: recall improves to ≥ 4.

Fixtures committed under `tests/Mithril.MapCalibration.Tests/Fixtures/Mode-B/` per existing replay-fixture conventions; dev-local-only screenshots remain unrepresentable in CI (per [memory `map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md)) — instead, we commit the canonical-asset-hashed pre-decoded inputs that the existing replay infrastructure uses.

### Measurement docs (commit to this folder)

Phase 0 spike docs (already committed):

- ✅ [`measurements/scene-class-classification.md`](measurements/scene-class-classification.md) — §6.a results.
- ✅ [`measurements/indoor-render-size.md`](measurements/indoor-render-size.md) — §6.b results.
- ✅ [`measurements/indoor-chroma-threshold.md`](measurements/indoor-chroma-threshold.md) — §6.c results + peak-luma alternative.
- ✅ [`measurements/indoor-synthesis-j-threshold.md`](measurements/indoor-synthesis-j-threshold.md) — §6.d results.
- ✅ [`measurements/untyped-ransac-cost.md`](measurements/untyped-ransac-cost.md) — §6.e results.
- ✅ [`measurements/detection-recall-pivot.md`](measurements/detection-recall-pivot.md) — NEW finding, drives Phase 2.

Phase 2 + 3 measurement docs (to be produced):

- `measurements/indoor-recall-stage-attribution.md` — Phase 2 stage-attribution audit.
- `measurements/indoor-peak-luma-threshold.md` — Phase 3 corpus expansion of the spike's single-bundle measurement.

## Sequencing argument

**Why Phase 1 first.** It's a pure refactor with one fact to confirm (already done via spike): alpha-coverage cleanly separates the known scenes. Every later phase has a stable carrier for its parameter divergence.

**Why Phase 2 is now the load-bearing piece (vs original Phase 2 = untyped detection).** The spike showed that real-icon blobs aren't being detected at all in 4-5 of 5-6 visible icons. No downstream change (untyped detection, peak-luma pre-filter, synthesis-J enforcement) fixes a recall failure upstream of itself. Phase 2 is now the only phase whose absence definitively blocks Indoor calibration.

**Why Phase 3 / 4 / 5 can land independently after Phase 1.** Phase 3 (peak-luma pre-filter) is a noise-suppression overlay that composes with any blob-recall regime; Phase 5 (synthesis-J Shadow) is an observability change. Phase 4 (untyped detection) is gated on Phase 2 succeeding AND observed typing-error failures persisting; it's not a hard sequence dependency on Phase 3 or 5.

**Why Phase 6 is gating-not-phase.** Outdoor regression isn't a feature to add — it's a property to preserve. Each phase's PR runs the Outdoor replay battery in CI; merge is gated on identical results.

**Phase abandonment paths.**

- Phase 2 ([#1163](https://github.com/moumantai-gg/mithril/issues/1163)) has no abandonment path — if the stage-attribution audit + tuning don't lift recall, the whole Mode-B v1 premise is wrong and we re-spec.
- Phase 3 — if the broader-corpus peak-luma measurement doesn't reproduce the spike's separation, Phase 3 ships disabled and we file a deferred sibling. Indoor relies on Phase 2's recall lift alone.
- Phase 4 — if Phase 2's typed-pool RANSAC succeeds without observable typing errors, Phase 4 doesn't ship; the original spec's chosen direction stays a deferred follow-up.
- Phase 5 — if synthesis-J Shadow values turn out uninformative, the formulas land as carrier code but no further work. Phase 5-v2 enforcement waits for ground-truth Indoor cals.

**Slug status flips.** Spec [`INDEX.md`](../INDEX.md) row stays `active` through Phase 1-5. Flips to `shipped` when #1163 closes (Phase 2 done) AND #1155 closes — Phases 3-5 are quality/observability and don't block the close. Phase abandonment paths don't change the status.

## PR-by-PR checklist

For each phase PR:

1. Spec link in PR description.
2. Verification doc committed under `docs/planning/calibration-1155-scene-class-profile/measurements/`.
3. Outdoor replay battery green (CI evidence in PR body).
4. Indoor replay battery measurements committed (whether asserts changed or not).
5. Phase scope respected — no cross-phase work.
6. `instrumentation_surveys_include_static_utilities` (per project memory) — any new static utility (e.g. `PeakLumaFilter`, the deviation/morph tuning code paths in [#1163](https://github.com/moumantai-gg/mithril/issues/1163)) gets an `ILogger?` + `MithrilActivitySources` span at decision points from day one.
7. Schema bump (Phase 1) carries a migration test for v4 attempts read as v5.
8. Branch policy: feature branch off main → `gh pr create`. Never push to main.

## Out-of-band sibling work (file separately, link from this slug)

- **[#1163](https://github.com/moumantai-gg/mithril/issues/1163) Indoor icon-blob recall** — Phase 2's root-cause fix. Tracked separately so the spec/plan stay readable as the carrier doc; the actual implementation design lives in #1163.
- **Alpha-zero interior mask gap** — `BuildDeviationMask` extends to gate `alpha < ε`. Spike confirmed the gap on the canonical bundle. Small fix; file as #1148 follow-up.
- **Better / multi-scale templates** — defer to Mode-B v2 / a separate effort. File as #1155 follow-up labeled `area:map-calibration` if Phase 4 ships and still shows typing errors.
- **Locator under-extension into chrome margin** — spike confirmed this is interior alpha-zero, not chrome-margin (blob 176 sits inside the texture bbox at an alpha-zero region). Folded into the #1148 follow-up above.
- **Phase 5-v2 — Indoor synthesis-J enforcement** — file once Phase 2 produces verifiable Indoor cals.

These are tracked separately so this slug stays focused on the scene-class refactor.
