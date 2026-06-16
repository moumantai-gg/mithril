# Indoor-recall #1174 NPCc brainstorm — design notebook

Brainstorming pass for [`#1174`](https://github.com/moumantai-gg/mithril/issues/1174):
NPCc on the 06-15 Hogan's bundle remains undetected at every pre-deviation luma
threshold in the sweep `{0, 140, 160, 180, 200, 220}`. The issue body proposes
that NPCc's local NCC floor is raised by dense cobblestone — this notebook is
a measurement pass to either support that hypothesis or replace it, followed
by 3–5 candidate mechanisms with risks + measurement plans, and a load-bearing
recommendation.

## TL;DR — the issue body's hypothesis is empirically falsified

NPCc's deviation signal is **strong**, not floor-bound. Center 5×5 peak deviation
is ~0.74 (well above the production `lowNcc=0.5` threshold of dev≥0.5) and
center 5×5 peak luma is 214 (above the production `MinLumaForDeviation=200`
gate). The signal IS present in `07-deviation.png`; the pip survives the
threshold AND the morph-close.

**It is wiped by the deviation-mask subtract** ([`DeviationMaskCombiner`](../../../../src/Mithril.MapCalibration.Detection/Internal/DeviationMaskCombiner.cs)
→ [`DeviationBlobDetector.cs:185-226`](../../../../src/Mithril.MapCalibration.Detection/DeviationBlobDetector.cs)).
The floor-boundary mask, derived from the base texture's alpha channel and
dilated by `BoundaryDilationPx = 8` (mithril#1116, [`MapCalibrationDetectorOptions.cs:57`](../../../../src/Mithril.MapCalibration.Detection/MapCalibrationDetectorOptions.cs)),
catches the entire lower pip. Tuning `LowNcc`, `MinLumaForDeviation`, or
sub-window kernel size **cannot** rescue NPCc — those knobs all sit upstream
of the mask that's killing it.

The load-bearing finding is structurally different from the issue body and
forces a different solution space. Recommendation: **profile-specific
`BoundaryDilationPx` (Indoor 3 or 4, Outdoor unchanged at 8)**, gated on a
threshold sweep — but the measurement story for that gate has to land before
any code does.

## Step 2 — NPCc signal characterisation

Bundle: `Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers/`
(pre-#1172, so the bundle's `07-deviation.png` is the pre-fix deviation map;
the threshold sweep in [`IndoorPreDeviationLumaPipelineTests`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorPreDeviationLumaPipelineTests.cs)
re-runs the pipeline on this fixture under different `MinLumaForDeviation`
values).

Comparison of the three NPC pip locations, all from `07-deviation.png` and
`06-aligned-screenshot.png`:

| Pip | Aligned (x,y) | Center 5×5 luma | Center 5×5 dev | 31×31 dev mean | Luma ≥ 200 in 31×31 | Verdict |
|---|---:|---:|---:|---:|---:|---|
| NPCa | (455, 212) | 136–217 (mean 184) | 89–184 (mean 145) | 29.0 | 34/961 | Detected (merged blob #40, pre-#1172 Structure → post-#1172 Icon) |
| NPCb | (478, 230) | 136–215 (mean 184) | 80–164 (mean 131) | 23.9 | 33/961 | Same as NPCa |
| NPCc | (473, 291) | 130–214 (mean 182) | 90–179 (mean 139) | **34.9** | 32/961 | **Undetected at every threshold** |

NPCc's center-pip strength is **indistinguishable from NPCa/b** (center luma
mean 182 vs 184; center dev mean 139 vs 131–145). Its 31×31 neighbourhood is
modestly noisier (dev mean 34.9 vs 24–29) — the issue body's hunch was
directionally right that the *background* is busier, but the dev floor at
NPCc is still well below the icon-core signal.

### The actual mechanism: alpha-boundary dilation wipes NPCc

Inspecting `07a-deviation-mask.png` (the combined alpha-boundary + fog mask
that `DeviationBlobDetector` subtracts from the foreground at line 185) at a
81×81 window centered on NPCc:

```
 y=282 ........................................................  (UPPER PIP: y=283-292, detected as Icon blob #91)
 y=283 ........................................................
 y=284 ........................................................
 y=285 ........................................................
 y=286 ........................................................
 y=287 ........................................................
 y=288 ........................................................
 y=289 ##################################................######   <- ALPHA-BOUNDARY BAND STARTS
 y=290 ###################################................######
 y=291 ###################################................######   <- NPCc (lower pip core)
 y=292 ###################################................######
 ...
 y=297 ###################################................######   <- LOWER PIP CORE
```

`#` = mask hit (pixel is subtracted from foreground before connected-components
labelling). NPCc's lower-pip 5×5 core at (475, 297) is `[[255,255,255,255,255],
... ×5]` — **100% masked**. The mask transition in column 473 happens at
exactly y=289, which is where the upper pip ends.

There are TWO pips stacked here, ~10 px apart:
- **Upper pip** (centroid ~(473, 283), bbox `(463, 277) + 21×13`, area 212):
  classified as Icon blob #91 by the pre-#1172 pipeline. This is the pip the
  bundle JSON already reports as detected.
- **Lower pip** (centroid ~(475, 297), 14×5 visible in `07-deviation.png`):
  this is the structurally undetected pip. The issue's NPCc coordinate (473,
  291) sits between the two pips — at the dead-zone where the alpha-mask
  transition lands.

The lower pip's deviation signal is strong (dev≥128 from y=295-299, max 190
≡ normalized 0.75). Morph-close at radius 1 keeps it. The alpha-boundary
subtract erases it before connected-components ever runs.

### Why the alpha boundary catches the lower pip

`FloorBoundaryMaskCache` dilates the alpha boundary by `BoundaryDilationPx = 8`
to absorb sub-pixel renderer/anti-alias noise (see [`FloorBoundaryMaskCache.cs:62-118`](../../../../src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs)).
At this scene, the base texture's `alpha ≥ 128` floor ends near y=289 in the
column-473 vicinity (the indoor corridor pinches). The dilated 8-px band
extends MASKED OUT down to y=297+ — the lower pip's entire footprint.

This is the right behaviour at the **alpha boundary itself** (anti-alias
chrome there would otherwise read as "added content"). It's the wrong
behaviour for a CORRIDOR INTERIOR where the pip just happens to be near the
alpha edge — which in narrow indoor corridors (Hogan's keep) is anywhere
within 8 px of a wall.

### Two-pip structural quirk

The pip at (475, 297) is *unusually close* to the upper pip (~14 px apart
centroid-to-centroid in the same column). In the wider Hogan's bundle
inventory, NPC pips this close vertically are rare — most are >25 px apart
horizontally (the (455, 212)+(478, 230) merge pattern is ~28 px diagonally).
A boundary-dilation widening that *would* catch a single pip in isolation
becomes load-bearing when two pips stack across the alpha edge.

## Step 3 — Candidate mechanisms

The issue body's two starting candidates (`LowNcc` tuning and per-region
adaptive threshold) target a mechanism that **isn't the load-bearing one**.
Per project memory `review_no_speculative_guards`, both are flagged here as
ruled out by the empirical signal, not just deferred.

### C1 — `LowNcc` tuning  ⛔ RULED OUT BY MEASUREMENT

**Mechanism.** Lower the production `LowNcc = 0.5` (devThr = 0.5) to 0.4 or
0.3 so the lower pip's deviation crosses the foreground threshold.

**Why ruled out.** NPCc's center-5×5 dev peak is 0.74 normalized — it's
ALREADY above the production threshold of 0.5. The lower pip IS in
`07b-foreground.png` (the post-threshold mask). The next stage (deviation-
mask subtract) is what kills it. Lowering `LowNcc` does nothing because the
gate is already wide-open for this signal.

**Risk / cost / measurement.** N/A — the upstream finding rules it out.

### C2 — Per-region adaptive `LowNcc` floor  ⛔ RULED OUT BY MEASUREMENT

**Mechanism.** Compute a local NCC noise floor per ~30 px tile; lower the
deviation threshold proportionally in noisier tiles.

**Why ruled out.** Same upstream block as C1. Even with a zero threshold in
this tile, the deviation-mask subtract removes the pixels before
classification.

**Risk / cost / measurement.** N/A.

### C3 — Profile-specific `BoundaryDilationPx`: Indoor=3 (Outdoor unchanged)  ⭐ RECOMMENDED

**Mechanism.** Move `BoundaryDilationPx` from the global
`MapCalibrationDetectorOptions` onto `SceneCalibrationProfile`, with
Outdoor=8 (current default) and Indoor=3 or 4. At dilation=3 the lower-pip
core at y=295-299 sits ≥6 px from the dilated boundary edge and survives.
The Phase 1 finding that classified Outdoor's opaqueFraction = 1.00 (see
[`scene-class-classification.md`](scene-class-classification.md)) means the
mask is essentially a no-op outdoors — keeping `8` there is byte-identical
to the pre-change behaviour.

**Risk to existing detections.** The 8-px dilation exists to absorb
sub-pixel renderer/anti-alias chrome at floor edges. Shrinking to 3 px
re-admits a narrow band of edge noise. The chrome lands as Structure-class
or low-PeakLuma blobs that the Phase 3 `MinPeakLuma = 0.7` filter already
suppresses (icon real luma > 0.78, edge chrome luma in the 0.22–0.40 band
per [`indoor-recall-stage-attribution.md`](indoor-recall-stage-attribution.md) §E).
Composition is *additive*: shrinking dilation lets new boundary noise into
the Icon-class candidate pool, where the peak-luma filter then drops it.
The remaining risk surface is boundary noise that happens to be both bright
(luma > 0.7) and shape-icon-like (area + solidity + aspect). The 06-13
canonical bundle has 6 real icons, all >25 px from the alpha boundary;
RIC=5/6 should not regress.

**Implementation cost.** Small. ~15 LOC:
- Move `BoundaryDilationPx` from `MapCalibrationDetectorOptions` to
  `SceneCalibrationProfile` (or add it as a profile field; the global remains
  the Outdoor fallback).
- `FloorBoundaryMaskCache.GetOrCompute(mapAssetKey)` already gets the
  SceneClass for free; thread that into the dilation choice.
- One cache invalidation: the cached mask is keyed on `mapAssetKey` only
  today; a profile change means we need to key on `(mapAssetKey,
  dilationPx)` OR clear the cache on profile flip. The former is preferred.

**Measurement plan.** Sweep `BoundaryDilationPx ∈ {2, 3, 4, 5, 6, 8}` in a new
`IndoorBoundaryDilationTests` theory parallel to
`IndoorPreDeviationLumaPipelineTests`. Required reports per cell:
- 06-15 NPCc detected? (0/1 transition is the win condition)
- 06-13 RIC (must stay ≥ 5/6)
- 06-13 + 06-15 total Icon-class blob count before peak-luma filter
  (track noise admittance)
- 06-13 + 06-15 total Icon-class blob count after peak-luma filter
  (the post-filter recall is the actual contract)
- Outdoor regression: byte-identical alpha-boundary mask on
  Serbule/Eltibule/Kur (must hold at any dilation since opaqueFraction ≈ 1)

### C4 — Bright-luma exception inside the boundary band

**Mechanism.** Don't subtract a pixel from the foreground via the deviation
mask if its raw screenshot luma is ≥ some threshold (e.g., 180 — the start
of the bright peak in [`indoor-pre-deviation-luma-distribution.md`](indoor-pre-deviation-luma-distribution.md)).
Implemented as an extra check inside `DeviationBlobDetector`'s deviation-mask
subtract loop (line 195-226): instead of `if (deviationMask[i]) fg[i] = false`,
use `if (deviationMask[i] && rawLuma[i] < kBoundaryRescueLuma) fg[i] = false`.
NPCc's lower-pip core has luma 175–203 — survives at threshold 180.

**Risk to existing detections.** Specular highlights on map decorations
inside the alpha-boundary band would survive. The fog-of-war mask correctly
catches uniformly-bright fog regions via the colour-variance gate; map
decorations (door icons, lit fixtures, etc.) baked into the texture would
match texture too and produce zero deviation, so they wouldn't be candidates
anyway. The risk surface is asymmetric foreground-bright pixels that DO
deviate from texture in the boundary band — none observed across the
canonical corpus, but absence of evidence ≠ evidence of absence and the
broader Indoor corpus (#1176) hasn't sampled this.

**Implementation cost.** Small-medium. ~10 LOC change + threading `rawLuma`
(already threaded for the Phase 3 peak-luma filter, so no new plumbing).
The new knob (`BoundaryRescueLuma` on `SceneCalibrationProfile` or
`BlobOptions`) needs a measurement story; C3's knob does not (it's
narrowing an existing knob).

**Measurement plan.** Sweep `BoundaryRescueLuma ∈ {0, 160, 180, 200, 220}`
on the same corpus as C3. The C3 vs C4 trade-off is "narrow the band
everywhere" vs "keep band but admit bright signal" — both should rescue
NPCc; the regression profile differs. C3 admits more boundary-edge noise
upstream of the peak-luma filter; C4 admits ONLY boundary-edge bright noise.

### C5 — Sub-window deviation kernel (smaller `win`)  ⛔ RULED OUT BY UPSTREAM

**Mechanism.** Drop `LocalNccDeviation.win` from 11 to 7 or 5 so the NCC
window doesn't smear floor noise around the pip.

**Why ruled out.** Same as C1/C2 — the deviation map shows a strong, well-
isolated pip at (475, 297). Smaller `win` would reduce NPCa/b's halo
overlap but doesn't change the alpha-boundary-mask subtract. Also, the
pre-#1172 audit ([`indoor-recall-merge-fix-candidates.md`](indoor-recall-merge-fix-candidates.md))
already showed `win` tuning doesn't help the merge problem; the same
reasoning applies to the lower-pip rescue.

**Risk / cost / measurement.** N/A.

### C6 — Pre-rim-mask deviation gate composition ⛔ NOT ORTHOGONAL

**Mechanism.** Move the bright-luma rescue into the rim-mask layer.

**Why ruled out.** The rim mask is empty around NPCc (rim is the map's
*outer* edge — see the all-zero rim-mask values at (473, 291)). The kill is
the *floor*-boundary mask, which is a different layer. C4 already targets
the right layer; this is C4 mis-routed.

### Summary

| ID | Candidate | Recall lift | Risk | Cost | Measurement story |
|---|---|---:|---|---:|---|
| C1 | Lower `LowNcc` floor | ⛔ wrong layer | — | — | Ruled out |
| C2 | Per-region adaptive `LowNcc` | ⛔ wrong layer | — | — | Ruled out |
| C3 | Profile `BoundaryDilationPx` Indoor=3 | ✓ rescues lower pip | low (composes with peak-luma filter) | S | Sweep 2..8 on 06-13 + 06-15; Outdoor must be byte-id |
| C4 | Bright-luma rescue inside boundary band | ✓ rescues lower pip | medium (specular highlights survive) | S/M | Sweep 0..220 same corpus |
| C5 | Smaller deviation `win` | ⛔ wrong layer | — | — | Ruled out |
| C6 | Pre-rim-mask gate | ⛔ wrong mask | — | — | Ruled out |

## Step 4 — Recommendation

**Recommended candidate: C3 (profile-specific `BoundaryDilationPx`,
Indoor=3 or 4).**

The narrowing IS the load-bearing fix. The reasons C3 is preferred over C4:

1. **Existing knob, no new contract.** `BoundaryDilationPx` already exists
   and is already on the right layer. Moving it to `SceneCalibrationProfile`
   composes with the v1 carrier (cf. [`SceneCalibrationProfile.cs`](../../../../src/Mithril.MapCalibration.Detection/SceneCalibrationProfile.cs)).
   C4 adds a new knob whose semantics ("rescue bright pixels from the alpha
   boundary band") need their own threshold sweep + their own justification
   for every value seen in production — that's the "speculative guard"
   pattern the project memory cautions against, unless C3's sweep falsifies
   it.

2. **Outdoor is naturally protected.** `scene-class-classification.md`
   confirmed `opaqueFraction = 1.00` outdoors → the alpha boundary is the
   image edge → the dilated band is a thin rim that's already either covered
   by `RimMaskMode.DeviationFlood` or sits at the edge of the deviation
   crop. Outdoor regression is zero-cost. C4 by contrast applies its
   rescue check on every pixel of every scene — Outdoor cost is non-zero
   even when its outcome is byte-identical.

3. **The measurement is small.** C3 needs ONE knob value to land
   (Indoor=3 or =4). C4 needs TWO (`BoundaryRescueLuma` + composition with
   C3). The smaller measurement is the better first move.

4. **C4 is the natural fallback.** If C3's sweep shows Indoor=3 breaks
   the canonical bundle's RIC=5/6 (because the narrower band lets
   boundary-edge chrome past the peak-luma filter), C4 becomes the
   next candidate — it admits *less* noise (only bright noise) at the
   cost of letting the band stay wide. Run C3 first; C4 if C3 doesn't
   keep the regression budget.

### The new knob

Add to `SceneCalibrationProfile` (record-init like `MorphOpenRadiusPx`):

```csharp
/// <summary>
/// Floor-boundary mask dilation radius (px) for this scene class
/// (mithril#1174). Replaces the global
/// MapCalibrationDetectorOptions.BoundaryDilationPx in the per-profile
/// dispatch. Outdoor=8 preserves the pre-#1174 behaviour (a no-op when
/// opaqueFraction≈1). Indoor uses the smaller value sized to corridor
/// width — sub-8-px dilation rescues icons within ~8 px of an alpha
/// boundary without re-admitting renderer chrome past the Phase 3
/// peak-luma filter.
/// </summary>
/// <remarks>
/// Default 8 keeps existing Outdoor behaviour byte-identical. Indoor
/// ships at 3 (or 4 — pending the threshold-sweep measurement).
/// </remarks>
public int BoundaryDilationPx { get; init; } = 8;
```

Indoor profile field: `BoundaryDilationPx = 3` (pending sweep — could be 4).

### Next-step measurement (BLOCKS any PR)

Land BEFORE implementation:

1. **Threshold-sweep theory.** Extend the existing
   `IndoorPreDeviationLumaPipelineTests` battery — or stand up a sibling
   `IndoorBoundaryDilationTests` if the existing theory's threading is
   too tangled — that re-runs the pipeline with `BoundaryDilationPx ∈
   {2, 3, 4, 5, 6, 8}` against:
   - 06-15 bundle: must lift NPC-Icon count from 2 → 3 (NPCc lower
     pip detected as Icon).
   - 06-13 canonical: must hold RIC=5/6 (the post-#1172 baseline).
   - Outdoor batterystrap: Serbule + Eltibule + Kur, byte-identical mask
     at every dilation (since opaqueFraction=1, no edge → no dilation
     change).

2. **Local-density check.** Plot the count of Icon-class blobs whose centroid
   falls within `(boundaryDilation + 4)` px of an alpha boundary, per dilation
   value. The number that survive the peak-luma filter is the "boundary
   chrome that fooled the luma gate" count — if it climbs above ~2 at
   dilation=3 on the canonical bundle, fall back to C4 instead.

3. **Cross-bundle confirmation.** Re-run the sweep on the additional Hogan's
   bundles inventoried in the working directory
   (`Map_HogansKeepBasement-20260610-*` through `-20260616-*` — 11 bundles
   total). The acceptance band has to hold across the corpus, not just the
   two canonical ones. Surface any bundle that requires a different
   dilation as a separate issue under #1155.

The recommendation is **GATED on this sweep**, not "Indoor=3 ships now". If
the sweep falsifies C3 (e.g., dilation must drop to 1 to rescue NPCc, and
that breaks the 06-13 RIC), the right move is to defer to C4 — and if both
fail, to defer entirely to the broader-corpus expansion (#1176) so the
mechanism is litigated against more scenes.

## Why this notebook diverges from the issue body

The issue body's hypothesis (local cobblestone NCC noise raising the floor
under NPCc) was an extrapolation from
[`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md)
Finding 4. That Finding 4 was correct about the *outcome* ("undetected at
every threshold in the sweep") but its proposed *mechanism* was a guess —
no one had inspected `07a-deviation-mask.png` at NPCc's coordinate yet. The
brainstorming pass here did inspect it, found the mask catching the lower
pip, and the load-bearing mechanism flipped.

This matches the project memory's `spec_verify_briefs_against_live_code`
pattern: a brief's hypothesis is a starting point, not a load-bearing fact
until the bundle data confirms it. NPCc's mechanism is the boundary-
dilation, not the NCC floor. The "no speculative guards" pattern then
limits C3's introduction to one new field with a measured value, not a
broader rework of the deviation-mask stack.

## Reproducibility

Bundle path:
```
%LOCALAPPDATA%/Mithril/diagnostics/calibration/
  Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers/
```

Sampling code used to produce this notebook is one-shot Python (numpy +
PIL), not committed — measurements re-derivable from the bundle's PNGs +
`10c-blob-pipeline.json`. Per the project memory's
`map_calibration_replay_fixtures_dev_local` pattern, the bundle itself is
dev-local (PG art IP); the analysis here is the durable artifact.

## Open follow-ups (not blocking #1174)

1. **`BoundaryDilationPx` is documented as "Task 0-deferred measurement".**
   The `MapCalibrationDetectorOptions.BoundaryDilationPx` XML doc (line 80)
   says "the spec's shipping default; the Task 0-deferred measurement
   experiment will publish a revised curve if real captures argue for a
   different value." The 06-15 finding here IS that argument. If C3 lands,
   the Task 0 deferral is partially discharged.

2. **The two-pip-stack pattern is a corpus gap.** NPCa/b and other Hogan's
   merged pairs are 25-30 px apart; NPCc's upper+lower pip are ~14 px
   apart. The peak-luma + boundary-dilation thresholds were tuned against
   the 25-30 px pattern. A separate broader-corpus measurement (#1176)
   should sample the < 20 px stacked-pip pattern across Indoor scenes
   (GoblinDungeon, BrainBugCaverns, HumanCellar) to confirm whether
   NPCc-like cases are rare or load-bearing.

3. **Bundle's "NPCc" coordinate is the dead-zone between pips.** The issue
   body's coordinate (473, 291) sits BETWEEN the two stacked pips, not on
   either of them. The upper pip is at ~(473, 287), the lower at ~(475,
   297). When (or if) C3 lands and rescues the lower pip, the
   correspondences output should label the lower pip with its true
   centroid, and the issue's "(473, 291)" should be retired as an
   approximation.
