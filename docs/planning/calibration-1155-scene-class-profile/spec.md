# Spec — Map auto-cal: scene-class profile (Indoor vs Outdoor)

**Issue:** [mithril#1155](https://github.com/moumantai-gg/mithril/issues/1155) — TypeFloor gap (the Hogan's-basement symptom that surfaced this design)
**Parent:** [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) — indoor calibration close-out
**Status:** active, design (this spec); implementation per [`plan.md`](plan.md)
**Engine version captured:** `3.0.0.91+304a3d97b3` (includes [#1148](https://github.com/moumantai-gg/mithril/pull/1148) deviation-mask + [#1157](https://github.com/moumantai-gg/mithril/pull/1157) spatial dedup + [#1158](https://github.com/moumantai-gg/mithril/pull/1158) ReplayFixture dim alignment).

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
| **E. Untyped detection + RANSAC discriminates type** | Detector emits "icon-shape candidate" (no per-blob NCC typing). RANSAC pools each detection × all-type refs and types from the geometrically-consistent assignment. | **Accepted as indoor profile.** Real pips constrain to one consistent transform; noise blobs scatter across types with poor fits. RANSAC pool grows ~2-3× per detection (measurable, not catastrophic). |
| **F. Upstream chroma / saturation pre-filter** | Require min-chroma on blob pixels before NCC fires. PG icons are saturated white/cyan/red; floor / off-texture noise is desaturated. | **Accepted as indoor profile** (companion to E). Eliminates the 0.83/0.86 noise hits regardless of template scores. |
| **G. Synthesis-J as enforcement gate (indoor)** | Lower detector permissiveness, retain RANSAC, but require synthesis-J pass — geometric self-consistency replaces score thresholds as accept criterion. | **Accepted as indoor profile** (companion to E). Requires adaptive `jMin` scaling with `refsTotal` since static 8 is unreachable indoors. |
| **★ Chosen: Scene-class profile** | `enum SceneClass { Outdoor, Indoor }`; per-class `SceneCalibrationProfile` carries detection/solver/gate parameters. Indoor profile = E + F + G; Outdoor profile = today's constants unchanged. | See [§5](#5-chosen-direction). |

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

Indoor profile (new)
  RenderSizePx           = 12              (smaller pip render-size)            (verification owed §6.b)
  TypeFloor              = (n/a — untyped detection skips per-blob NCC typing)
  LowNcc                 = 0.5             (unchanged for v1; revisit if §6.c shows poor recall)
  BlobOpts.MinChroma     = 0.30            (saturation pre-filter)               (verification owed §6.c)
  Detector path          = untyped (new UntypedDeviationBlobDetector)
  RansacInlierPx         = 15              (unchanged for v1)
  Synthesis-J mode       = Enforced
  Synthesis-J jMin       = max(1.5, 0.6 × refsTotal)                             (verification owed §6.d)
  Synthesis-J nMin       = max(3, ⌈0.4 × refsTotal⌉)                             (verification owed §6.d)
  Gate                   = inherited from synthesis-J; legacy gate informational
```

Initial values for Indoor are starting points. Each diverged value is gated on its own [verification owed](#6-verification-owed) row before it lands in code.

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

### 5.3 Indoor detection — untyped icon-shape candidates

The indoor detection path replaces `DeviationBlobCalibrationDetector`'s per-blob template NCC typing with **untyped icon-shape emission**:

1. Same deviation + rim + morph + classify pipeline produces icon-class blobs.
2. **Chroma pre-filter** (Indoor only): reject blobs whose mean chroma in the original BGRA screenshot is below `BlobOpts.MinChroma`. Suppresses floor noise and off-texture (alpha-zero) hits upstream of NCC.
3. **No per-template NCC scoring.** Emit each surviving blob as an untyped `IconShapeCandidate(anchor, score = blob.PeakDev)` — anchor at the blob centroid (no pivot correction at detection time; pivot is type-dependent so it's applied at RANSAC time).
4. Diagnostic surface: `10-detections.json` schema bumps to v2 with optional `landmarkType: null` for Indoor candidates; `10b-blob-template-scores.json` is omitted entirely for Indoor (the per-template NCC step doesn't run).

### 5.4 Indoor solving — RANSAC discriminates type

`TypeAwareRansacSolver` extends to accept untyped detections:

1. Pool construction: each untyped detection pairs with **all** refs (irrespective of type).
2. Per-pair pivot lookup: the pair carries the ref's type, so `template.PivotX/PivotY` is resolved from the ref-type at pool-build time.
3. RANSAC sample + inlier check unchanged — the 2-point seed solver doesn't care about type.
4. Inlier de-dup (`bestPerRef`) unchanged.
5. The chosen inlier assignment IS the type label. Inliers are tagged with the ref's type for downstream diagnostics.

Search-space growth: in this bundle, 10 detections × ~13 refs (8 Portal + 3 NPC + 2 MediPillar) = 130 candidate pairs vs 80 with the typed pool. ~1.6× larger; RANSAC iteration cost is dominated by the seed-solve + linear pool scan, both linear in pool size. Expected wall-clock ≤ 2× the typed path. **Verification owed §6.e.**

### 5.5 Indoor enforcement — synthesis-J adaptive thresholds

Synthesis-J already runs as Shadow in [#1117](https://github.com/moumantai-gg/mithril/issues/1117)/[#1118](https://github.com/moumantai-gg/mithril/pull/1118). Indoor profile flips it to Enforced **with adaptive thresholds**:

- `jMin = max(1.5, 0.6 × refsTotal)`. Hogan's `refsTotal=11` → `jMin=6.6`. Indoor accept bundles measured at j ≈ 3-4 will need a more permissive minimum — actual constant comes from §6.d.
- `nMin = max(3, ⌈0.4 × refsTotal⌉)`. Hogan's → `nMin=5`. Floor at 3 keeps small-ref scenes solvable.
- Outdoor stays Shadow + static `jMin=8 / nMin=8`. No behavior change.

The legacy 12 px / 4-inlier gate stays informative for Indoor (logged + emitted to bundle) but doesn't drive accept/reject — synthesis-J is source of truth. Rationale: legacy gate accepted Hogan's 06-10 which synthesis-J would have rejected (§2.2); we trust synthesis-J more for Indoor.

### 5.6 Bundle schema additions

`01-attempt.json` schema v4 → v5:

```jsonc
{
  "sceneClass": "Indoor",                 // new field, "Outdoor" | "Indoor"
  "sceneClassSource": "alpha-coverage",   // new field — source provenance for the class label
  "sceneClassOpaqueFraction": 0.78,       // new field — measured alpha coverage
  "profile": {                            // new section — exact profile values used this attempt
    "renderSizePx": 12,
    "typeFloor": null,                    // null in Indoor; numeric in Outdoor
    "minChroma": 0.30,                    // null in Outdoor for v1
    "detectorPath": "untyped",            // "typed" | "untyped"
    "ransacInlierPx": 15,
    "synthesisJMode": "enforced",
    "synthesisJMin": 6.6,                 // resolved adaptive value
    "synthesisNMin": 5
  }
}
```

Existing fields unchanged. Outdoor attempts carry `sceneClass: "Outdoor"` + the profile values; everything else is unchanged.

## 6. Verification owed

Each item below MUST land a measured datapoint before the corresponding code change ships.

a. **Scene-class threshold.** Compute `opaqueFraction` for Serbule, Eltibule, Kur Mountains (Outdoor corpus) and Hogan's, GoblinDungeon, GoblinDungeon_TopFloor, KhyruleksCrypt (Indoor corpus). Confirm `≥ 0.95` separates the two cleanly. If borderline, revise the threshold or the rule. Lands as a one-off measurement step in Phase 1 (see [`plan.md` §1](plan.md)).

b. **Indoor `RenderSizePx` value.** v1 picks 12 from "icons render smaller indoors" intuition; needs an empirical pick. Run `IconRenderScaler.SelectRenderSize`'s aggregate-NCC sweep across the Indoor corpus per bundle and confirm the ladder peak. Could land at 10, 12, or 14.

c. **Chroma pre-filter threshold.** v1 picks `MinChroma=0.30` from "PG icons are saturated, floor noise is desaturated" intuition. Needs a measurement: for each Indoor bundle, compute per-blob mean chroma, plot against (is-real-pip ground truth from visual review). Pick the lowest value that keeps real pips and rejects noise. **If no separating value exists**, the chroma pre-filter doesn't land for v1 and indoor relies on E + G alone (still better than today).

d. **Adaptive synthesis-J `jMin / nMin` formula.** v1 picks `max(1.5, 0.6 × refsTotal) / max(3, ⌈0.4 × refsTotal⌉)`. Needs ground truth: collect J values from the three+ Indoor bundles we have, manually mark which are "real cal" vs "wrong cal," derive the threshold that separates them. **If no separating formula exists**, synthesis-J doesn't flip to Enforced for v1 — Indoor falls back to legacy gate with tighter parameters or a manual cal-with-pin prompt.

e. **Untyped RANSAC wall-clock cost.** v1 expects ≤ 2× the typed path on indoor scenes (small pool) and ~1.5× on outdoor (larger pool). Needs a benchmark: replay-fixture battery before/after on Outdoor accept corpus; assert no scene goes above 5 s solve.

f. **Outdoor accept-rate regression.** Scene-class refactor MUST NOT change Outdoor behavior. Replay-fixture battery on Serbule, Eltibule, Kur asserts identical inlier count, identical residual (within float ε), identical accept decision. **Gates every Indoor-profile divergence PR.**

g. **Alpha-zero hole gap (sibling).** `Map_HogansKeepBasement-20260613-...` blob 176 at (488, 668) sits at alpha=0 in the texture but inside the boundary mask. Confirm by reading `07a-deviation-mask.png` at that screenshot pixel; verify mask value. If confirmed, file as a #1148 follow-up — separate issue, not in this slug.

## 7. Out of scope + sibling issues

In this spec:
- Decoder-free guarantee for `src/**` is preserved (per project memory). No new image-processing dependencies; chroma pre-filter is pure-BCL pixel arithmetic on the existing BGRA screenshot.
- "Switch-as-registry" smell (per project memory): the Indoor/Outdoor split is a 2-arm enum, not a 3+-arm switch. If a third class (`Subterranean`? `Instanced`?) arises later, the `SceneCalibrationProfile` carrier already abstracts it — revisit registry framing at that point.

Out of scope (file separately, link from #1155 / #1116):

- **#1155-sibling: Alpha-zero interior mask gap.** `BuildDeviationMask` extends to gate `alpha < ε` regardless of boundary proximity. Small fix; landing path independent of this work. (Verification owed §6.g.)
- **#1155-sibling: Better / multi-scale templates** (candidate D). Root-cause fix for indoor type-discrimination failure; high engineering cost; needs a richer fixture corpus. Mode-B v1 sidesteps it via untyped detection; future Mode-B v2 could revisit.
- **#1116 close-out remaining work:** cross-scene landmark leak via `AreaCave1` aggregator (#1116's H1 hypothesis) — synthesis-J as enforcement gate (this spec) addresses it geometrically without needing landmarks.json structural changes.
- **#1153 ScaleMax adaptive ladder** — separate trivial follow-up, not blocked by this.
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
