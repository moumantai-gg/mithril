# Spec — Map auto-cal: scene-class profile (Indoor vs Outdoor)

**Issue:** [mithril#1155](https://github.com/moumantai-gg/mithril/issues/1155) — TypeFloor gap (the Hogan's-basement symptom that surfaced this design)
**Parent:** [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) — indoor calibration close-out
**Implementation root-cause issue (Phase 2):** [mithril#1163](https://github.com/moumantai-gg/mithril/issues/1163) — Indoor icon-blob recall
**Status:** active, design (this spec); implementation per [`plan.md`](plan.md)
**Engine version captured:** `3.0.0.91+304a3d97b3` (includes [#1148](https://github.com/moumantai-gg/mithril/pull/1148) deviation-mask + [#1157](https://github.com/moumantai-gg/mithril/pull/1157) spatial dedup + [#1158](https://github.com/moumantai-gg/mithril/pull/1158) ReplayFixture dim alignment).

## Revision history

| When | What | Why |
|---|---|---|
| 2026-06-14 (initial) | Spec written — chose candidate E (untyped detection + RANSAC type discrimination) as Mode-B v1 load-bearing direction. | Spec inferred from `10b-blob-template-scores.json` distribution analysis; assumed real-pip blobs ARE detected and only mis-typed. |
| 2026-06-14 (this revision) | Re-sequenced after Phase 0 spike (PR [#1162](https://github.com/moumantai-gg/mithril/pull/1162), see [`measurements/`](measurements/)). | Spike falsified the antecedent. Of 18 Icon-class blobs in the canonical bundle, only 1 contains a real icon glyph; 4-5 of 5-6 visible real icons aren't detected as blobs at all. The detector has a detection-**recall** failure, not detection-**precision**. Untyped detection doesn't fix this — RANSAC has no correspondences to find regardless of typing strategy. The load-bearing fix lives upstream at deviation/mask/morph/classify, tracked as [#1163](https://github.com/moumantai-gg/mithril/issues/1163). Other spike-driven changes: chroma pre-filter replaced by peak-luma pre-filter (chroma doesn't separate in grayscale Indoor scenes; peak luma does, with 0.91 vs 0.22-0.40 separation); Indoor `RenderSizePx` revised 12→16 (matches Outdoor); Indoor synthesis-J downgraded from Enforced to Shadow (no ground-truth-good Indoor cals exist yet). |

## 1. Why this is bigger than the issue title

#1155 was filed as "the typeFloor gap lets wall artifacts through while real NPC pips score below threshold." That framing is correct at the symptom layer and wrong at the cause layer. Reading the Hogan's diagnostic bundles + the engine surface together makes it clear that:

- The TypeFloor (0.80) sits at the noise ceiling of indoor floor-texture-vs-icon-template NCC — we have measured ≥ 0.86 NCC against the `landmark_portal` template from blob crops sitting on cobblestone floor, and ≥ 0.83 from a blob crop sitting on an alpha-zero hole in the texture (both in `Map_HogansKeepBasement-20260613-230459-600`).
- Real NPC pips at indoor render scale (~5–12 px) score 0.60–0.70 *against every template*, with `(best − 2nd_best)` margins of 0.01–0.04. The per-blob NCC step cannot type-discriminate them.
- The Hogan's accepted cal (2026-06-10, residual 6.45 px) sits at synthesis-J `j=3.25 / jMin=8` — synthesis-J would reject it; the legacy gate accepted it. `disagree: accept_to_reject`.
- The engine's hot-path constants (`RenderSizePx=16`, `TypeFloor=0.80`, `LowNcc=0.5`, `BlobOpts`, synthesis-J `jMin=8 / nMin=8`) are quoted in the code as "the gate-study sweet-spot for real assets" — that gate study (mithril#897) ran on the outdoor Serbule/Eltibule/Kur corpus. **Every detection parameter is one universal set tuned outdoors, and indoor has been shoehorned in.**

This is the cause. The fix isn't a threshold tweak — it's introducing a scene-class axis the engine has been quietly leaning on `null` for.

## 2. Evidence inventory

### 2.1 Primary bundle — `Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers`

- **Outcome:** `rejected-solve-insufficient-inliers`, `rejectReason: "only 2 inliers (need >= 4)"`.
- **Locator:** `sobel-padded-pyramid`, scale 1.10, `fallbackNcc 0.754`. Sound.
- **Detections (`10-detections.json`):** 2 entries, both `landmark_portal`, scores 0.859 and 0.835. Dedup from [#1157](https://github.com/moumantai-gg/mithril/pull/1157) is working — zero duplicates.
- **Sub-floor cluster (`10b-blob-template-scores.json`, 176 records):**
  - 5 above-floor records (≥0.80).
  - 46 records in `[0.65, 0.80)`.
  - Top sub-floor scores all `landmark_portal`: 0.778, 0.755, 0.755, 0.752, 0.750.
- **Visual cross-check (`08-detections.png` vs `06-aligned-screenshot.png`):**
  - The 2 emitted detections are at noise positions. One on floor cobble; one inside the texture bbox but at an alpha-zero hole.
  - Real NPC pip cluster visible in the upper-middle of the dungeon — cream/white head-and-shoulder glyphs at screenshot pixels ~(640–730, 180–270).
  - Cross-referenced with `10b` and `07e-blob-classification.png`: the real-pip cluster IS detected (blobs 54 at (703, 180) and 75 at (701, 205)) but mistyped as `MeditationPillar` 0.70 with `(best − 2nd_best)` margins 0.01 and 0.04.
- **Per-blob best-score + margin (non-rotated pass, top entries):**

  | blob | dims    | area | best type        | best score | margin to 2nd |
  |------|---------|------|------------------|------------|---------------|
  |  96  |  8×10   |  72  | Portal           | 0.86       | 0.27          |
  | 176  | 13×23   | 152  | Portal           | 0.83       | 0.14          |
  |  68  |  8×18   | 118  | Portal           | 0.76       | 0.19          |
  |  37  | 23×31   | 296  | Portal           | 0.75       | 0.07          |
  |  20  | 40×23   | 425  | Portal           | 0.75       | 0.09          |
  |  75  | 19×18   | 128  | MeditationPillar | 0.70       | 0.04          |
  |  54  | 24×23   | 273  | MeditationPillar | 0.70       | 0.01          |

  Blobs 96 and 176 (the emitted detections) are the noise hits — both score above floor AND carry healthy margin. The pattern "high score implies real pip" and "high margin implies confident typing" are BOTH false at indoor render scales.
- **Synthesis-J:** `j: 3.01, refsAboveHalf: 3, refsTotal: 11`. Shadow-mode verdict: reject. Static `jMin=8` is unreachable for this scene.

### 2.2 Comparison bundle — `Map_HogansKeepBasement-20260610-091533-358-accepted`

- The only indoor cal we have ever accepted on this scene. Engine `3.0.0.81`.
- Solved residual 6.45 px on 4 inliers; gate accepted.
- Synthesis-J shadow: `j: 3.25, refsAboveHalf: 5, jMin: 8`. **`gateVerdict: accept, verdict: reject, disagree: true, disagreeChange: accept_to_reject`**. Synthesis-J would have rejected this cal if it had been flipped to enforcement.
- The cal currently live in users' `refinements.json` is structurally fragile (per [#1116](https://github.com/moumantai-gg/mithril/issues/1116) cross-scene leak hypothesis) and synthesis-J would have caught it.

### 2.3 Sibling sub-zone — `Map_GoblinDungeon_TopFloor-20260610-095806-692-rejected-solve-insufficient-inliers`

- Engine `3.0.0.82`. Outcome: only 3 inliers (need ≥ 4).
- Synthesis-J shadow: `j: 3.18, refsAboveHalf: 4, refsTotal: 9, jMin: 8` → reject. **Same scene-class signature as Hogan's.** Indoor ceiling on synthesis-J `j` looks structural, not bundle-specific.

### 2.4 The off-texture detection ("one outside map texture")

Blob 176 at screenshot (488, 668) is inside the locator's mapRect bbox `[(197, 117) → (1324, 1244)]` but sits at an **alpha-zero hole** in the base texture content. The [#1148](https://github.com/moumantai-gg/mithril/pull/1148) deviation mask is the dilated alpha-**boundary** band (alpha ≈ 0 ↔ alpha ≈ 1 transition), not an alpha-zero **interior** suppressor. Alpha-zero interior pixels survive the mask, get exercised by NCC, and can score 0.83 against a portal template by chance.

This is a **Mode-A residual gap masquerading as Mode-B noise**. Fix is small and structurally clean: extend `BuildDeviationMask` to also gate `alpha < ε` regardless of boundary proximity. Tracked as a sibling sub-issue under #1116 (see [§7](#7-out-of-scope--sibling-issues)).

## 3. The architectural diagnosis

### 3.1 The per-blob template NCC step is regime-dependent

What it does well (outdoor — Serbule/Eltibule/Kur):
- Icons render at ~14–20 px → templates carry distinguishing detail
- Outdoor textures (grass/dirt/water) have low NCC-correlation against icon templates
- Best-of-template gives clean 0.85–0.97 type discrimination

What it fails at (indoor — Hogan's/GoblinDungeon):
- Icons render at ~5–12 px → templates degrade to fuzzy blobs that all look alike
- Floor textures (cobble/stone) are high-frequency noise → random NCC max-over-search-window hits 0.7–0.86 against any template (measured: 0.86 on this bundle)
- Real-pip score ≈ floor-noise score ≈ off-texture-noise score; "best template" is essentially random

The empirical noise floor of floor-texture-vs-icon-template NCC in `Map_HogansKeepBasement-20260613-230459-600` is **≥ 0.86**. The TypeFloor is calibrated at **0.80**. The floor sits at the noise ceiling; there is no threshold value that separates signal from noise on this scene.

### 3.2 The engine has implicit per-class branching but no explicit profile axis

Audit of where the engine differs on indoor vs outdoor today:

| Component | Branches? | How |
|---|---|---|
| Rim mask (`BorderMask`, `DeviationFloodRimMask`) | Yes, implicit | Outdoor concept ("stone rim around outdoor zones"). Indoor scenes don't have a rim; masks are no-ops/trivial. The `RimMaskMode` enum is the explicit knob. |
| Locator algorithm (ORB primary → Sobel-padded-pyramid fallback) | Yes, by fallthrough | `LocatorBackedMapViewProbe.cs:15-17` — ORB outdoor-feature-rich, Sobel-NCC indoor-smooth-corridors. Not a switch on scene class; ORB fails indoors → Sobel takes over. |
| Floor-boundary mask (`FloorBoundaryMaskCache`, [#1116](https://github.com/moumantai-gg/mithril/issues/1116)/[#1148](https://github.com/moumantai-gg/mithril/pull/1148)) | Yes, implicit | Derives from texture alpha. Outdoor maps have alpha≈1 everywhere → mask is null no-op. Indoor maps gate by the dilated alpha-boundary band. Scene-class-aware via the alpha signal itself. |
| Sub-zone narrowing (`ReferenceDataAreaReferenceProvider`) | Yes, partial | NPCs filter by `SceneFriendlyName` for aggregator scenes. Landmarks don't (the documented [#1021](https://github.com/moumantai-gg/mithril/issues/1021) gap). Aggregator-aware, not strictly indoor-aware. |
| **Detection constants** (`AutoCalibrationEngine.cs:56–62` — `RenderSizePx`, `LowNcc`, `TypeFloor`, `BlobOpts`) | **No** | One universal set, quoted as "the gate-study sweet-spot for real assets." Gate study ran on outdoor corpus. |
| **Solver thresholds** (`TypeAwareRansacSolver` — `RansacInlierPx=15`, `RansacIterations=800`) | **No** | One universal set. |
| **Synthesis-J thresholds** (`MapCalibrationSolverOptions` — `SynthesisJMin=8`, `SynthesisNMin=8`) | **No** | Tuned for outdoor density. Unreachable indoors per §2.2 / §2.3. |
| **Gate thresholds** (`CalibrationConfidenceGate` — 12 px / 4 inliers) | **No** | One universal set. |

The engine acknowledges indoor vs outdoor at the masking and locator layers — where the difference is structurally undeniable — and ignores it at the parameter layer where it actually bites.

## 4. Candidate shapes considered

| Shape | Summary | Why not / why yes |
|---|---|---|
| **A. Lower TypeFloor uniformly (+ post-RANSAC trim)** | Drop TypeFloor 0.80 → 0.70; trim low-score survivors after RANSAC. | **Rejected.** Measured noise floor ≥ 0.86 — lowering admits more noise, not more signal. The post-trim is fighting the same problem under a different name. |
| **B. Discriminative TypeFloor (margin gate)** | `best ≥ 0.70 AND (best − 2nd_best) ≥ 0.12`. | **Rejected.** Empirically backwards on this bundle: floor-noise blob 96 has margin 0.27 (would pass); real-NPC blob 54 has margin 0.01 (would fail). Margin tracks template-vs-noise correlation, not signal confidence at indoor render scales. |
| **C. Per-type tuned absolute floors** | `landmark_portal=0.72, landmark_medipillar=0.80`. | **Rejected.** "Tuning thesis becomes load-bearing" — per [project memory](https://github.com/moumantai-gg/mithril) the engine should not depend on per-type magic numbers. |
| **D. Better / multi-scale templates** | Re-render templates at multiple scales; pick best fit. | **Defer (separate effort).** Strongest first-principles fix, addresses the root cause that 14×16 templates can't carry type info at <12 px render. Big engineering, needs richer fixture corpus. Sibling issue. |
| **E. Untyped detection + RANSAC discriminates type** | Detector emits "icon-shape candidate" (no per-blob NCC typing). RANSAC pools each detection × all-type refs and types from the geometrically-consistent assignment. | **DEMOTED after spike** — was the original load-bearing pick. The spike showed real-pip blobs aren't being *detected* (not just mis-typed), so untyped detection isn't sufficient. Retained as a tier-2 quality improvement (Phase 4 in [`plan.md`](plan.md)) once Phase 2 lifts blob recall. |
| **F. Upstream chroma / saturation pre-filter** | Require min-chroma on blob pixels before NCC fires. PG icons are saturated white/cyan/red; floor / off-texture noise is desaturated. | **REPLACED after spike** by F′ (peak-luma pre-filter). Chroma is essentially zero across the Indoor corpus (icons are grayscale glyphs on grayscale floor); the chroma assumption was wrong. See [`measurements/indoor-chroma-threshold.md`](measurements/indoor-chroma-threshold.md). |
| **F′. Peak-luma pre-filter (spike-discovered alternative)** | Require `PeakLuma > 0.7` (or `BrightPx ≥ 3`) on the blob bbox in raw screenshot space. PG indoor icons are bright-white glyphs (PeakLuma 0.91); floor noise is mid-gray (PeakLuma 0.22-0.40). | **Accepted as indoor profile** (Phase 3 in [`plan.md`](plan.md)). Cleanly separates real-icon blobs from floor-noise blobs in the measured corpus. |
| **G. Synthesis-J as enforcement gate (indoor)** | Lower detector permissiveness, retain RANSAC, but require synthesis-J pass — geometric self-consistency replaces score thresholds as accept criterion. | **DOWNGRADED after spike** from Enforced to Shadow for v1. Outdoor `j` (16-23) vs Indoor `j` (3-4) is clean, but zero ground-truth-good Indoor cals exist to derive a separating formula. Phase 5 ships Indoor in Shadow mode; revisit enforcement once Phase 2 produces known-good Indoor cals. See [`measurements/indoor-synthesis-j-threshold.md`](measurements/indoor-synthesis-j-threshold.md). |
| **H. Indoor icon-blob recall** (spike-discovered, [#1163](https://github.com/moumantai-gg/mithril/issues/1163)) | Lift the upstream detection-recall ceiling so real-icon blobs actually survive into the typing step. Stage-attribution audit + per-profile tuning of `LowNcc`, morph-close radius, chroma-aware deviation, `BlobOpts` floor. | **Accepted as indoor profile load-bearing piece** (Phase 2 in [`plan.md`](plan.md)). Of 18 Icon-class blobs in the canonical bundle, only 1 contains a real icon glyph; 4-5 of 5-6 visible icons aren't detected at all. The actual root cause. See [`measurements/detection-recall-pivot.md`](measurements/detection-recall-pivot.md). |
| **★ Chosen: Scene-class profile** | `enum SceneClass { Outdoor, Indoor }`; per-class `SceneCalibrationProfile` carries detection/solver/gate parameters. Indoor profile = **H + F′ + G(Shadow)** (post-spike); Outdoor profile = today's constants unchanged. | See [§5](#5-chosen-direction). |

## 5. Chosen direction — scene-class profile

### 5.1 The two profiles

```text
Outdoor profile (today's constants, preserved)
  RenderSizePx           = 16
  TypeFloor              = 0.80
  LowNcc                 = 0.5
  BlobOpts.MinChroma     = (unset — no pre-filter)
  Detector path          = typed (DeviationBlobCalibrationDetector)
  RansacInlierPx         = 15
  Synthesis-J mode       = Shadow (today)
  Synthesis-J jMin       = 8
  Synthesis-J nMin       = 8
  Gate                   = 12 px / 4 inliers

Indoor profile (post-spike v1)
  RenderSizePx           = 16              (same as Outdoor — spike §6.b)
  TypeFloor              = 0.80             (unchanged from Outdoor for v1 — untyped detection deferred to Phase 4)
  LowNcc                 = TBD per #1163 stage-attribution (current 0.5 likely too tight indoor; v1 tunes per spike outcome)
  BlobOpts.MinChroma     = (unset — spike showed chroma doesn't separate)
  BlobOpts.MinPeakLuma   = ~0.7             (peak-luma pre-filter, replaces chroma; v1 threshold from Phase 3 corpus)
  Detector path          = typed (current — untyped detection deferred to Phase 4)
  RansacInlierPx         = 15              (unchanged for v1)
  Synthesis-J mode       = Shadow           (downgraded from Enforced; no ground-truth-good Indoor cals exist)
  Synthesis-J jMin       = max(1.5, 0.6 × refsTotal)   (computed + logged but not enforced for v1)
  Synthesis-J nMin       = max(3, ⌈0.4 × refsTotal⌉)   (computed + logged but not enforced for v1)
  Gate                   = legacy 12 px / 4 inliers (synthesis-J observability adds context; not source of truth for Indoor v1)
```

The Indoor profile's biggest divergence from Outdoor is the **upstream detection pipeline tuning** ([#1163](https://github.com/moumantai-gg/mithril/issues/1163) Phase 2) — `LowNcc`, morph-close radius, possibly `BlobOpts.MinArea` and a chroma-aware deviation kernel. The detection-time constants get expressed as profile fields; the actual values land via Phase 2 corpus measurement.

The carrier shape is unchanged — `SceneCalibrationProfile` still bundles all detection/solver/gate parameters and the dispatcher reads from it. The post-spike pivot is *which* parameters diverge, not *whether* there's a profile axis.

### 5.2 Scene-class resolution

Source of truth: **alpha-channel coverage of the base texture**. Outdoor maps ship texture alpha = 1 everywhere (or ≥ 99 %). Indoor maps have alpha = 0 over the off-map regions (the [#1116](https://github.com/moumantai-gg/mithril/issues/1116) premise). `FloorBoundaryMaskCache` already loads alpha to derive its boundary band; the same load yields a class label for free.

```text
opaqueFraction = count(alpha ≥ 128) / (textureWidth × textureHeight)
SceneClass     = opaqueFraction ≥ 0.95 ? Outdoor : Indoor
```

Cached per `MapSceneRef.MapAssetKey` alongside the boundary mask. Threshold is a single named constant (`SceneClassOpaqueFractionThreshold`) so the verification step in §6.a can revise it.

Alternative classification sources considered + rejected:
- **Per-area metadata in landmarks/areas reference data.** PG's CDN doesn't ship a scene-class field. Adding one needs upstream cooperation we don't have.
- **Hand-curated per-scene config in the calibration baseline.** Maintenance burden; new scenes silently fall back to a default; auto-cal triggers BEFORE the baseline has a row.
- **Heuristic from `Map_<X>` name patterns** (`Map_AreaSerbule` outdoor, `Map_HogansKeepBasement` indoor). Fragile to PG naming churn; doesn't generalize.

Alpha-channel coverage is the only signal that is self-bootstrapping, dependency-free, and falls out of work already done.

### 5.3 Indoor detection — upstream recall fix ([#1163](https://github.com/moumantai-gg/mithril/issues/1163))

The Indoor detection path keeps the typed `DeviationBlobCalibrationDetector` for v1 (untyped detection deferred to Phase 4). The Indoor profile's actual divergence is **upstream-of-typing**:

1. **Stage-attribution audit** — per Indoor bundle in the corpus, trace where each visible-but-undetected icon gets lost (deviation map → rim mask → deviation mask → morph-close → classify). Output: per-icon, per-bundle attribution table.
2. **Per-profile tuning** of the parameters the audit identifies. Likely candidates:
   - `LowNcc` lower for Indoor (currently 0.5; Indoor low-contrast icons may need 0.3-0.4)
   - Per-profile morph-close radius (smaller for Indoor — current radius may merge icons into adjacent floor noise)
   - Chroma-aware deviation kernel (compare colour channels separately even though icons are grayscale — luma profile may still differ in HSV space)
   - Per-profile `BlobOptions.MinArea` floor (Indoor icons may form smaller blobs than the current 12 threshold)
3. **Peak-luma pre-filter** — after blob classification, reject blobs whose `PeakLuma` in the raw BGRA screenshot bbox is below `BlobOpts.MinPeakLuma` (~0.7). The spike showed this cleanly separates real-icon blobs (0.91) from floor noise (0.22-0.40). This is Phase 3 in [`plan.md`](plan.md).
4. **Typed per-blob NCC** then runs as today — for v1 we accept that some real-icon blobs will be mis-typed; RANSAC's same-type pool constraint catches what it catches and the legacy gate decides.

Diagnostic surface: `10-detections.json` schema bumps to v2 with optional `blobPeakLuma` per detection; `10c-blob-pipeline.json` carries the stage-attribution data needed for the corpus audit.

### 5.4 Indoor solving — typed RANSAC (unchanged for v1)

The Indoor RANSAC solver is **today's typed implementation** for v1. The post-spike re-sequence demotes untyped detection (the original §5.4) to Phase 4 because:

- v1's load-bearing improvement is detection recall (Phase 2) — once 4+ real-icon blobs survive into RANSAC, the existing typed pool has correspondences to find.
- Untyped detection's payoff is *when typing is wrong but blobs are right*. Today's bundles show blobs are *missing*, not mis-typed; fixing typing first solves the wrong problem.
- Phase 4 (untyped detection) becomes useful once Phase 2 recovers detection recall AND we observe Phase 2's typed-pool RANSAC failing because of typing errors specifically.

Detail design for Phase 4 (untyped detection + RANSAC type discrimination) is preserved in the [original §5.4 content](#) — see git history pre-revision for the full description. The implementation surface (`TypeAwareRansacSolver.SolveTopK` extending to accept `IReadOnlyList<UntypedDetection>`, per-pair pivot lookup, type label from inlier assignment) is unchanged; only the timing changed.

### 5.5 Indoor enforcement — synthesis-J Shadow mode (v1)

The original §5.5 chose synthesis-J enforcement with adaptive `jMin = max(1.5, 0.6 × refsTotal)`. The spike showed this is premature:

- Outdoor `j` (16-23) vastly exceeds Indoor (3-4). The gap is structural.
- The only Indoor "accept" sample (Hogan's 06-10) is the suspected-wrong cross-scene-leak cal. Zero ground-truth-good Indoor cals exist to derive a separating formula.
- A formula like `0.6 × refsTotal` rejects ALL current Indoor samples (including the disputed accept); `0.25 × refsTotal` accepts all (including ones likely wrong). The data doesn't discriminate.

**v1 ships Indoor synthesis-J in Shadow mode.** The `jMin / nMin` formulas land on the carrier and the values are computed + logged in the bundle, but they don't drive accept/reject. The legacy 12 px / 4-inlier gate stays as the Indoor v1 source of truth. Once Phase 2 recovers detection recall enough that we accumulate Indoor cals worth ground-truth verification (e.g. by manually inspecting landmark projection), Phase 5-v2 revisits enforcement with measured thresholds.

Outdoor stays Shadow + static `jMin=8 / nMin=8`. No behavior change.

### 5.6 Bundle schema additions

`01-attempt.json` schema v4 → v5:

```jsonc
{
  "sceneClass": "Indoor",                 // new field, "Outdoor" | "Indoor"
  "sceneClassSource": "alpha-coverage",   // new field — source provenance for the class label
  "sceneClassOpaqueFraction": 0.17,       // new field — measured alpha coverage (Hogan's example)
  "profile": {                            // new section — exact profile values used this attempt
    "renderSizePx": 16,                   // post-spike: same as Outdoor
    "typeFloor": 0.80,                    // post-spike: same as Outdoor for v1 (untyped detection deferred to Phase 4)
    "lowNcc": 0.40,                       // tuned per #1163 stage attribution (placeholder; v1 lands actual value)
    "minPeakLuma": 0.70,                  // peak-luma pre-filter (replaces minChroma); null in Outdoor
    "detectorPath": "typed",              // "typed" | "untyped" (Phase 4 introduces untyped for Indoor)
    "ransacInlierPx": 15,
    "synthesisJMode": "shadow",           // post-spike: Shadow for both classes in v1
    "synthesisJMin": 6.6,                 // computed adaptive value (logged, not enforced for Indoor v1)
    "synthesisNMin": 5
  }
}
```

Existing fields unchanged. Outdoor attempts carry `sceneClass: "Outdoor"` + the profile values; everything else is unchanged.

## 6. Verification owed — resolved status (post-spike)

Phase 0 spike (PR [#1162](https://github.com/moumantai-gg/mithril/pull/1162), see [`measurements/`](measurements/)) resolved most §6 items upfront. Status:

a. **Scene-class threshold.** ✅ **CONFIRMED.** Outdoor `OpaqueFraction = 1.00` (3 scenes); Indoor range `[0.07, 0.36]` (10 scenes); no overlap. Spec's `≥ 0.95` works with massive margin. See [`measurements/scene-class-classification.md`](measurements/scene-class-classification.md).

b. **Indoor `RenderSizePx` value.** ✅ **REVISED.** Should be `16` (same as Outdoor), not the original spec's `12`. PG renders icons at fixed screen-space size regardless of zoom; the "smaller indoor" intuition was wrong. See [`measurements/indoor-render-size.md`](measurements/indoor-render-size.md).

c. **Chroma pre-filter threshold.** ⚠️ **NEGATIVE → REPLACED.** Chroma doesn't separate (Indoor icons are grayscale glyphs on grayscale floor). Replaced by **peak-luma pre-filter** (`MinPeakLuma ≈ 0.7`) which cleanly separates real-icon blobs (PeakLuma 0.91) from floor-noise blobs (0.22-0.40). See [`measurements/indoor-chroma-threshold.md`](measurements/indoor-chroma-threshold.md). Threshold needs broader-corpus confirmation before Phase 3 ships.

d. **Adaptive synthesis-J `jMin / nMin` formula.** ⚠️ **PARTIAL.** Outdoor `j` (16-23) vastly exceeds Indoor `j` (3-4); static `jMin = 8` works for Outdoor. Zero ground-truth-good Indoor cals exist to derive an Indoor formula. Phase 5 ships Indoor in Shadow mode for v1. See [`measurements/indoor-synthesis-j-threshold.md`](measurements/indoor-synthesis-j-threshold.md).

e. **Untyped RANSAC wall-clock cost.** ✅ **CONFIRMED.** Pool growth ~3× indoor, ~10× outdoor; absolute wall-clock impact in millis. Within budget. See [`measurements/untyped-ransac-cost.md`](measurements/untyped-ransac-cost.md). Real benchmark waits on Phase 4 implementation.

f. **Outdoor accept-rate regression.** Still owed — gates every Indoor-profile divergence PR (unchanged by spike).

g. **Alpha-zero hole gap (sibling).** Confirmed in spike: blob 176 sits inside the texture bbox at an alpha-zero region. Filed as a `#1148` follow-up sibling issue (TBD). Out of scope for this slug.

h. **NEW: Indoor icon-blob recall ([#1163](https://github.com/moumantai-gg/mithril/issues/1163)).** Of 18 Icon-class blobs in the canonical bundle, only 1 contains a real icon glyph; 4-5 of 5-6 visible icons aren't detected as blobs at all. The detector has a **detection-recall failure**, not a detection-precision failure. This is the actual root cause and load-bearing fix; the original spec's chosen direction (E, untyped detection) doesn't address it. See [`measurements/detection-recall-pivot.md`](measurements/detection-recall-pivot.md). Phase 2 in [`plan.md`](plan.md) is now scoped to fix this.

## 7. Out of scope + sibling issues

In this spec:
- Decoder-free guarantee for `src/**` is preserved (per project memory). No new image-processing dependencies; chroma pre-filter is pure-BCL pixel arithmetic on the existing BGRA screenshot.
- "Switch-as-registry" smell (per project memory): the Indoor/Outdoor split is a 2-arm enum, not a 3+-arm switch. If a third class (`Subterranean`? `Instanced`?) arises later, the `SceneCalibrationProfile` carrier already abstracts it — revisit registry framing at that point.

Out of scope (file separately, link from #1155 / #1116):

- **[#1163](https://github.com/moumantai-gg/mithril/issues/1163) — Indoor icon-blob recall** (Phase 2 of this slug; tracked separately so spec/plan stay readable as the carrier doc).
- **#1155-sibling: Alpha-zero interior mask gap.** `BuildDeviationMask` extends to gate `alpha < ε` regardless of boundary proximity. Small fix; landing path independent of this work. (Confirmed by Phase 0 spike — to be filed as #1148 follow-up.)
- **#1155-sibling: Better / multi-scale templates** (candidate D). Root-cause fix for indoor type-discrimination failure; high engineering cost; needs a richer fixture corpus. Mode-B v1 sidesteps it via the Phase 2 recall fix + peak-luma pre-filter; future Mode-B v2 could revisit if the carrier still shows typing errors after Phase 4.
- **#1116 close-out remaining work:** cross-scene landmark leak via `AreaCave1` aggregator (#1116's H1 hypothesis) — once Phase 5 ships Indoor synthesis-J in Enforcement mode (post-Phase 2 / 3), geometric self-consistency addresses it without needing landmarks.json structural changes.
- **#1153 ScaleMax ceiling bump** — landed as a static `ScaleMax = 1.20 → 2.00` default-bump + v2→v3 schema migration in [PR #1181](https://github.com/moumantai-gg/mithril/pull/1181), not the adaptive-ladder shape originally sketched here. The adaptive-ladder follow-up (detect L1 winner at ladder edge, extend the search range one direction) and a downstream `ClampToFrame`/`ImageOps.Resize` correctness gap at scale > 1.0 are deferred — see #1181's body for the issue links.
- **#1151 wiki close-out** — post-Mode-B.

## 8. Open questions answered in [`plan.md`](plan.md)

- Phasing — what lands first, what gates what.
- File-level breakdown — which existing files mutate, which new ones get added.
- Test strategy — which existing replay fixtures cover what, which need new bundles.
- Schema version bumps and migration.
- PR boundaries.

## 9. Related design memory

- [`map_calibration_938_live_state`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_938_live_state.md) — solve-stall history; informs the wall-clock budget in §6.e.
- [`legolas_calibration_findings`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/legolas_calibration_findings.md) — PG map = per-area global isotropic similarity, sub-pixel, no warp; geometric-fit-as-truth is well-grounded for outdoor and we're extending it to indoor.
- [`map_calibration_engine_914_plan`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_engine_914_plan.md) — engine plan; this spec extends Phase 1's detector with the scene-class axis.
- [`cold_orientation_select_mean_not_median`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/cold_orientation_select_mean_not_median.md) — gate-study findings around outdoor regime where typed detection works.
- [`asset_decoding_out_of_process_sidecar`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/asset_decoding_out_of_process_sidecar.md) — alpha-channel availability via [`sidecar-rgba-alpha-surface`](../sidecar-rgba-alpha-surface/) — the source of `opaqueFraction` for §5.2.
