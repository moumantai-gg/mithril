# Plan — Map auto-cal: scene-class profile (Indoor vs Outdoor)

Companion to [`spec.md`](spec.md). All section anchors below refer to the spec unless qualified.

## Phasing summary

| Phase | Scope | Behavior change? | Gated on |
|-------|-------|------------------|----------|
| **0. Spike** (recommended) | Throwaway harness that computes all §6 verification-owed measurements against the existing bundle corpus — opaque-fraction split, indoor render-size sweep, chroma distribution, synthesis-J distribution, untyped-RANSAC wall-clock. No production code. Output is the `measurements/*.md` files (and the decision on whether F / G ship as no-ops). | **No** (not merged to main) | Spec approval |
| **1. Scaffolding** | Introduce `SceneClass` + `SceneCalibrationProfile`; alpha-coverage classifier; bundle schema v4→v5. Both profiles initially identical to today's constants. | **No** | Phase 0 measurements committed |
| **2. Indoor untyped detection** | New `UntypedDeviationBlobDetector`; `TypeAwareRansacSolver` accepts untyped pool. Indoor profile activates untyped path. | Indoor only | Phase 1 |
| **3. Indoor chroma pre-filter** | Chroma pre-filter in `BlobClassifier`; Indoor `MinChroma` sets. | Indoor only | Phase 2 + Phase 0 §6.c |
| **4. Indoor adaptive synthesis-J** | Synthesis-J `jMin/nMin` becomes profile-driven; Indoor flips to Enforced. | Indoor only | Phase 2 + Phase 0 §6.d |
| **5. Outdoor regression battery** | Replay-fixture comparison: Outdoor accept rate / inlier count / residual unchanged. | None | Each prior phase before merge |

Phases 2–4 are independent landing paths after Phase 1. Phase 5 is a gating activity on every prior phase's PR; it's not its own PR. Phase 1 is a pure refactor and lands first.

**Why Phase 0 as a spike.** The spec commits to specific starter values for Indoor (`RenderSizePx=12`, `MinChroma=0.30`, the synthesis-J formula). Each is verification-gated, but having Phase 1 land with unvalidated placeholders and then revising them under §6 measurements is messier than running a throwaway spike upfront that produces validated values *before* code lands. The spike is unbranched (or branched-and-discarded); only its measurement docs land via Phase 1's PR.

### Phase 0 — Spike (recommended, no production code)

**Goal.** Produce all §6 measurement docs against the existing diagnostic-bundle corpus so Phase 1 lands with validated starting points instead of placeholders.

**Deliverables (commit under `docs/planning/calibration-1155-scene-class-profile/measurements/`):**

- `scene-class-classification.md` (§6.a) — opaque-fraction for every scene we have a base texture for. Confirms the `≥ 0.95` threshold (or revises it).
- `indoor-render-size-sweep.md` (§6.b) — `IconRenderScaler.SelectRenderSize` results across the Indoor corpus. Picks Indoor `RenderSizePx` from the ladder peak.
- `indoor-chroma-threshold.md` (§6.c) — per-blob chroma + visual-truth labeling across the Indoor corpus. Picks `MinChroma` or concludes no separating value exists.
- `indoor-synthesis-j-threshold.md` (§6.d) — synthesis-J `j` distribution across Indoor bundles + accept/reject ground truth. Picks the formula or concludes no separating formula exists.
- `untyped-ransac-benchmark.md` (§6.e) — pool size + wall-clock estimate from a one-shot benchmark.

**Implementation hint.** A `tools/Spike-1155/Program.cs` harness that mounts the existing `IconBlobPipeline` against bundle inputs and emits CSVs the measurement docs reference. Or done in PowerShell + jq against the bundle JSON. Either way, the harness itself is not preserved — only the measurement docs.

**What changes if the spike finds bad news:**

- §6.a fails (alpha-coverage doesn't separate cleanly) → revisit classification source; spec §5.2 alternative-sources table is the menu.
- §6.b ladder peak is at e.g. 8 px → Indoor `RenderSizePx=8`; small change, no spec rewrite.
- §6.c no separating chroma → Phase 3 ships disabled; sibling issue filed.
- §6.d no separating synthesis-J formula → Phase 4 ships with Indoor in Shadow + a tighter legacy gate; sibling issue filed.
- §6.e untyped RANSAC > 5 s on any scene → keep typed-detection as outdoor fast-path; spec already supports this via `DetectorPath`.

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

## Phase 2 — Indoor untyped detection

**Goal.** Indoor profile detects icon-shape blobs without per-blob template typing; `TypeAwareRansacSolver` discriminates type from geometric fit.

### Phase 2 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/UntypedDeviationBlobDetector.cs` | **new** | Same deviation + rim + morph + classify pipeline as `DeviationBlobCalibrationDetector` (factored shared internals out into `Internal/IconBlobPipeline.cs`). Skips the `IconRenderScaler.RenderSized` + per-template-NCC loop. Emits `IReadOnlyList<UntypedDetection>` (one per surviving icon-class blob, anchor at centroid, score from `blob.PeakDev`). |
| `src/Mithril.MapCalibration.Detection/Internal/IconBlobPipeline.cs` | **new** | Factored shared pipeline (deviation → flood-rim → morph → classify → blob list) extracted from `DeviationBlobCalibrationDetector`. Both detectors call this; one stops here, the other continues with per-blob NCC. |
| `src/Mithril.MapCalibration.Detection/UntypedDetection.cs` | **new** | `record UntypedDetection(CroppedFramePixel Anchor, double Score, int BlobOrdinal)`. |
| `src/Mithril.MapCalibration.Detection/DeviationBlobCalibrationDetector.cs` | **edit** | Refactor to call shared `IconBlobPipeline.RunIconClassBlobs`; keep the per-template-NCC loop. No behavior change for Outdoor. |
| `src/Mithril.MapCalibration.Detection/ICalibrationDetector.cs` | **edit** | Either split into `ITypedCalibrationDetector` + `IUntypedCalibrationDetector`, or unify via `union` (`OneOf` style) — pick at Phase 2 design micro-review. Default leaning: split, dispatch in `MapCalibrationSolveEngine` based on `profile.DetectorPath`. |
| `src/Mithril.MapCalibration.Detection/TypeAwareRansacSolver.cs` | **extend** | New overload `SolveTopK(IReadOnlyList<UntypedDetection> untyped, IReadOnlyList<LandmarkReference> allRefs, MapRect mapRect, int k, ILogger? logger)`. Pool construction: each detection × all refs (no type filter); per-pair pivot lookup via the ref's type → corresponding template's `PivotX/PivotY`; per-pair anchor correction applied at pool-build time. Inlier selection unchanged. Type label assigned at inlier resolution. |
| `src/Mithril.MapCalibration.Detection/Internal/PivotResolver.cs` | **new** | `IPivotResolver.GetPivot(string landmarkType) → (double X, double Y)` reading from the loaded `IconTemplate` set. Cached per-template lookup. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` | **edit** | Dispatch on `profile.DetectorPath`: Typed calls `DeviationBlobCalibrationDetector` → `Solve(Dictionary<string, List<TypedDetection>>)`; Untyped calls `UntypedDeviationBlobDetector` → `Solve(IReadOnlyList<UntypedDetection>)`. Synthesis-J path consumes the same `TopKCandidate` shape — no downstream change. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Flip Indoor's `DetectorPath = Untyped`, `TypeFloor = null`, `RenderSizePx = 12`. |
| `tests/Mithril.MapCalibration.Detection.Tests/UntypedDeviationBlobDetectorTests.cs` | **new** | Synthetic input: produces same blob set as typed detector minus the per-template NCC scores; coordinates byte-identical via shared pipeline. |
| `tests/Mithril.MapCalibration.Detection.Tests/UntypedRansacSolverTests.cs` | **new** | Pool size assertion (10 untyped × 13 refs = 130 candidates); type discrimination (synthetic mixed-type ground truth resolves to the right type via inliers); regression: typed-pool result preserved for same input. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Indoor replay fixture (Hogan's 2026-06-13) solves with ≥ 4 inliers via untyped path. Acceptance criterion measured in the bundle, not asserted as a pass criterion in v1 — first measurement establishes the baseline. |

### Phase 2 verification

- Verification owed §6.b — pick Indoor `RenderSizePx`. Add a one-off harness `tools/SceneClassRenderSizeSweep/Program.cs` that runs `IconRenderScaler.SelectRenderSize` against every Indoor replay bundle's screenshot. Commit results to `measurements/indoor-render-size-sweep.md`.
- Verification owed §6.e — wall-clock benchmark. Extend the existing replay-fixture timing harness; assert no scene > 5 s solve.
- **Verification owed §6.f (outdoor regression)** — full Outdoor replay battery byte-identical. Gates the PR.

### Phase 2 PR boundary

One PR. Title: `feat(map-calibration): indoor untyped detection (#1155 phase 2)`. Depends on Phase 1 merged.

## Phase 3 — Indoor chroma pre-filter

**Goal.** Suppress floor-noise and alpha-zero-interior noise upstream of detection by requiring blob mean chroma ≥ profile threshold.

### Phase 3 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/Internal/ChromaPreFilter.cs` | **new** | Pure-BCL pixel arithmetic: `Chroma = (max(R,G,B) - min(R,G,B)) / max(R,G,B)` averaged over blob pixels in the original BGRA screenshot. Returns mean chroma per blob. |
| `src/Mithril.MapCalibration.Detection/BlobOptions.cs` | **extend** | Add `MinChroma` (nullable double). Null → pre-filter disabled. |
| `src/Mithril.MapCalibration.Detection/Internal/IconBlobPipeline.cs` | **edit** | After blob classification, drop blobs whose `ChromaPreFilter.MeanChroma(blob) < BlobOpts.MinChroma`. Emits per-blob diagnostic stage record (chroma value + drop reason) — feeds `07e-blob-classification.png` color coding (new color for chroma-rejected). |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Indoor's `BlobOpts.MinChroma = <verification §6.c result>`. Outdoor's `MinChroma = null` (pre-filter inactive). |
| `tests/Mithril.MapCalibration.Detection.Tests/ChromaPreFilterTests.cs` | **new** | Synthetic blobs: saturated white pip → high chroma; gray cobble blob → low chroma; alpha-zero hole → undefined / zero (handle). |
| `tests/Mithril.MapCalibration.Detection.Tests/IconBlobPipelineTests.cs` | **extend** | Indoor pipeline drops low-chroma blobs; Outdoor pipeline unchanged. |

### Phase 3 verification

- Verification owed §6.c — measure per-blob chroma in each Indoor bundle against visual-truth labeling. Commit to `measurements/indoor-chroma-threshold.md`. **If no separating threshold exists, Phase 3 ships with `Indoor.MinChroma = null` and a deferred sub-issue.** Indoor falls back on E + G alone.
- Verification owed §6.f — full Outdoor replay battery byte-identical (chroma pre-filter is no-op outdoors).

### Phase 3 PR boundary

One PR. Title: `feat(map-calibration): indoor chroma pre-filter (#1155 phase 3)`. Depends on Phase 2 merged. Body links verification §6.c result.

## Phase 4 — Indoor adaptive synthesis-J

**Goal.** Indoor profile flips synthesis-J from Shadow to Enforced with `jMin/nMin` formulas that scale with `refsTotal`.

### Phase 4 files

| File | Change | Notes |
|---|---|---|
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolverOptions.cs` | **edit** | `SynthesisJMin` and `SynthesisNMin` constants stay (Outdoor source of truth); Indoor formulas live in `SceneCalibrationProfile.Indoor.SynthesisJMinFn / SynthesisNMinFn`. |
| `src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs` | **edit** | Synthesis-J resolution: `jMin = profile.SynthesisJMinFn(refsTotal); nMin = profile.SynthesisNMinFn(refsTotal);`. Mode (Shadow/Enforced) per profile. |
| `src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs` | **edit** | Indoor's `SynthesisMode = Enforced`, `SynthesisJMinFn = refsTotal => max(1.5, 0.6 * refsTotal)`, `SynthesisNMinFn = refsTotal => max(3, ceil(0.4 * refsTotal))`. **Actual constants from verification §6.d.** |
| `tests/Mithril.MapCalibration.Detection.Tests/SynthesisJAdaptiveThresholdTests.cs` | **new** | `refsTotal=11` → expected `jMin/nMin` from Indoor formula; `refsTotal=8` (outdoor border) → unchanged static; Outdoor profile path takes the static branch. |
| `tests/Mithril.MapCalibration.Tests/ReplayFixtureTests.cs` | **extend** | Hogan's 2026-06-13 bundle accepts with adaptive jMin (if §6.d concludes accept is correct). |

### Phase 4 verification

- Verification owed §6.d — collect synthesis-J `j` measurements from all Indoor bundles (rejected + accepted); derive threshold. **If no separating formula exists, Phase 4 ships with `Indoor.SynthesisMode = Shadow` (no enforcement change) and a deferred sub-issue.** Indoor relies on legacy gate.
- Verification owed §6.f — Outdoor replay battery byte-identical.

### Phase 4 PR boundary

One PR. Title: `feat(map-calibration): indoor adaptive synthesis-J enforcement (#1155 phase 4)`. Depends on Phase 2 merged (Phase 3 not a hard prereq). Body links verification §6.d result.

## Test strategy summary

### Existing replay fixtures (preserve)

- Outdoor: Serbule, Eltibule, Kur Mountains. Each phase's PR **must** show byte-identical solve.
- Indoor: Hogan's (the existing PR #1148 bundle smoke). Mode-A scope; Mode-B re-uses for solve coverage.

### New replay fixtures (add)

- **Mode-B Indoor #1: `Map_HogansKeepBasement-20260613-230459-600`** — the primary bundle from this spec. ReplayFixture canonical-hash-keyed. Solve assertion: Phase 2 produces ≥ 4 inliers via untyped path; Phase 4 accepts via adaptive synthesis-J (if §6.d concludes accept).
- **Mode-B Indoor #2: `Map_HogansKeepBasement-20260610-091533-358-accepted`** — the comparison "accepted but synthesis-J disagrees" bundle. Asserts that adaptive synthesis-J in Phase 4 also accepts this (or, if §6.d concludes the original accept was wrong, that it's rejected). Captures the diagnostic delta either way.
- **Mode-B Indoor #3: `Map_GoblinDungeon_TopFloor-20260610-095806-692`** — sibling sub-zone. Phase 2 produces ≥ 4 inliers via untyped path.

Fixtures committed under `tests/Mithril.MapCalibration.Tests/Fixtures/Mode-B/` per existing replay-fixture conventions; dev-local-only screenshots remain unrepresentable in CI (per [memory `map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md)) — instead, we commit the canonical-asset-hashed pre-decoded inputs that the existing replay infrastructure uses.

### Measurement docs (commit to this folder)

- `measurements/scene-class-classification.md` — Phase 1 §6.a results.
- `measurements/indoor-render-size-sweep.md` — Phase 2 §6.b results.
- `measurements/indoor-chroma-threshold.md` — Phase 3 §6.c results.
- `measurements/indoor-synthesis-j-threshold.md` — Phase 4 §6.d results.
- `measurements/untyped-ransac-benchmark.md` — Phase 2 §6.e results.

These are not committed yet; they're produced inside the phase PR they unblock.

## Sequencing argument

**Why Phase 1 first.** It's a pure refactor with one fact to confirm: alpha-coverage cleanly separates the known scenes. If it does, every later phase has a stable carrier for its parameter divergence. If it doesn't, we learn that EARLY without coding the wrong abstraction.

**Why Phase 2 before Phase 3 / 4.** Untyped detection is the only change that fixes the *root cause* of the bundle's reject reason ("only 2 inliers"). Chroma pre-filter (Phase 3) is a defense-in-depth quality improvement; adaptive synthesis-J (Phase 4) is an enforcement-layer change. Either of them on top of typed detection still leaves indoor blocked. Phase 2 is the load-bearing piece.

**Why Phase 3 and Phase 4 can land independently.** Both depend on Phase 2; neither depends on the other. Phase 3 improves Indoor input quality; Phase 4 improves Indoor accept-criterion quality. They compose but don't sequence.

**Why Phase 5 is gating-not-phase.** Outdoor regression isn't a feature to add — it's a property to preserve. Each phase's PR runs the Outdoor replay battery in CI; merge is gated on identical results.

**Phase abandonment paths.** If verification §6.c fails (no chroma separation), Phase 3 ships as a no-op disabled-by-default and we file a deferred sibling. If §6.d fails (no synthesis-J separation), same for Phase 4. Phase 2 has no abandonment path — if untyped detection doesn't recover Indoor inliers, the whole Mode-B premise is wrong and we go back to the spec.

**Slug status flips.** Spec [`INDEX.md`](../INDEX.md) row stays `active` through Phase 1–4. Flips to `shipped` after Phase 4 merge + #1155 close. Phase abandonment paths (3 / 4 no-ops) don't change the status.

## PR-by-PR checklist

For each phase PR:

1. Spec link in PR description.
2. Verification doc committed under `docs/planning/calibration-1155-scene-class-profile/measurements/`.
3. Outdoor replay battery green (CI evidence in PR body).
4. Indoor replay battery measurements committed (whether asserts changed or not).
5. Phase scope respected — no cross-phase work.
6. `instrumentation_surveys_include_static_utilities` (per project memory) — any new static utility (e.g. `ChromaPreFilter`) gets an `ILogger?` + `MithrilActivitySources` span at decision points from day one.
7. Schema bump (Phase 1) carries a migration test for v4 attempts read as v5.
8. Branch policy: feature branch off main → `gh pr create`. Never push to main.

## Out-of-band sibling work (file separately, link from this slug)

- **Alpha-zero interior mask gap** — `BuildDeviationMask` extends to gate `alpha < ε`. Verification §6.g confirms the gap on this bundle. Small fix; file as #1148 follow-up.
- **Better / multi-scale templates** — defer to Mode-B v2 / a separate effort. File as #1155 follow-up labeled `area:map-calibration`.
- **Locator under-extension into chrome margin** — only if §6.g determines blob 176's alpha-zero hole IS chrome-margin and not interior alpha-zero. File as #1095 follow-up labeled `area:map-calibration`.

These three are tracked separately so this slug stays focused on the scene-class refactor.
