# Indoor recall — stage attribution

Phase 2 sub-step 1 (no production code) of [#1163](https://github.com/moumantai-gg/mithril/issues/1163).
Per-icon, per-bundle attribution of where in the detector pipeline each visible-but-undetected real
icon gets lost. Drives the design micro-review for sub-step 2 (per-profile tuning).

Method, raw data, and the canonical-bundle pivot framing are in
[`detection-recall-pivot.md`](detection-recall-pivot.md); this doc inherits that bundle's coordinate
conventions (aligned XY = raw XY − origin per `04-maprect.json`).

## Headline

**For the two Indoor bundles where the locator succeeds AND the captured scene has enough icons
(canonical 06-13 + Map_HogansKeepBasement-20260612-235416-091), 100 % of missed real icons survive
deviation+mask+rim+morph and die at the classifier.** Every visible-but-undetected real-icon glyph
either (a) sits inside a blob that fails one of the four classifier gates
(`MinArea / MaxIconArea / MinSolidity / MaxAspect / MinPeak`), or (b) sits inside a much larger blob
that classifies as `Structure` because the icon's deviation halo merged with adjacent floor noise into
a single ≥ 900-area component.

No icon dies at deviation, deviation-mask, rim-mask, or morph-close. The recall ceiling is
classifier shape gates + connected-component merging — not the upstream signal.

The broader Indoor corpus splits four ways across the 10 usable bundles: 2 "Phase 2 target" bundles
where the recall fix is the load-bearing improvement, 3 scene-degenerate bundles where the deviation
field is one giant Structure blob, 3 locator-mismap bundles where the cropped region doesn't contain
the visible icons at all, and 2 insufficient-icon-scene bundles (only 2–3 icons visible). The other
2 of 12 bundles are excluded due to scanner contamination (UI overlay bright pixels masquerading as
icons — needs an in-game-viewport mask the audit doesn't have). See "Corpus extension" below — the
recall fix helps the 2 Phase 2-target bundles directly and must avoid regressing the other 8. The
plan's "three other Indoor bundles measured to the same criterion" verification target needs
adjusting since 12-235416 is currently the only Phase 2-target sibling.

The audit also falsifies the spec's premise that the Hogan's 06-10 "accepted" bundle is a
better-recall comparison target: in 06-10, the **entire active map region** condenses into one
119,655-pixel Structure blob that engulfs all six visible icons, and the four "inliers" that produced
the accept were RANSAC fitting noise to a similarity transform (see §3).

## Production parameter values audited against

From `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs:58-62` (the "gate-study sweet-spot
for real assets" universal constants the spec's profile axis is meant to displace):

| Knob | Value | Stage it gates |
|---|---|---|
| `RenderSizePx` | 16 | per-blob NCC template scale (not in this audit's scope) |
| `LowNcc` | 0.5 | deviation threshold → `aboveThresholdCount` in `10c-blob-pipeline.json` |
| `TypeFloor` | 0.80 | per-template NCC score floor (post-classifier; not in scope) |
| `BlobOpts.MinArea` | 12 | classifier — area < 12 → Noise |
| `BlobOpts.MaxIconArea` | 900 | classifier — area > 900 → Structure or Fog (never Icon) |
| `BlobOpts.MinSolidity` | 0.35 | classifier — solidity < 0.35 and icon-band → Noise |
| `BlobOpts.MaxAspect` | 2.5 | classifier — aspect > 2.5 and icon-band → Noise |
| `BlobOpts.MinPeak` | 0.7 | classifier — peakDev < 0.7 and icon-band → Noise |
| morph `closeRadius` | 1 | morphological close kernel half-radius (hardcoded in `DeviationBlobCalibrationDetector.cs:69`) |

Classifier source: `Mithril.MapCalibration.Detection/DeviationBlobDetector.cs:312-327`. Per-pixel
color encoding for `07e-blob-classification.png`:
`Icon = green(0,200,0)`, `Fog = blueish(40,100,200)`, `Structure = red(200,0,0)`, `Noise = gray(80,80,80)`,
`no blob = black(0,0,0)` — established in `FilesystemCalibrationAttemptBundleSink.cs:491-498`.

## Stage-by-stage audit (canonical bundle, 06-13)

Bundle: `Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers`.
Locator: `sobel-padded-pyramid`, scale 1.10, origin (197, 117), aligned crop 1127 × 1127. Outcome:
`rejected-solve-insufficient-inliers` (2 inliers, RANSAC floor = 4).

The six real-icon glyphs in `02-screenshot-raw.png` and the values each surfaces at each pipeline
stage, sampled in an 11×11 window centered on the icon's aligned-space centroid. Numbers are
`mean / max / above-half %` of the channel-averaged luma in the stage's PNG (0-255). Where the
stage is a binary mask the `mean/max/aboveHalf%` columns are dominated by the binary itself; the
question is just "is this pixel inside the surviving mask?".

| Icon | aligned XY | bright px | Deviation | Deviation mask | Rim mask | Foreground | Morph-close | 07e Classify-png |
|---|---|---|---|---|---|---|---|---|
| A: upper-mid       | (327, 180) | 128 | 112 / 233 / 53 % | 162 / 255 / 64 % | 0 / 0 / 0 % | 255 / 255 / 100 % | 93  / 255 / 36 %  | Noise (8) + black (113) |
| B: upper-mid-right | (411, 185) |  27 |  87 / 192 / 44 % |  93 / 255 / 36 % | 0 / 0 / 0 % | 255 / 255 / 100 % | 162 / 255 / 64 %  | Structure (20) + black |
| C: upper-right     | (432, 202) |  27 |  88 / 179 / 45 % |   0 /   0 /  0 % | 0 / 0 / 0 % | 232 / 255 /  91 % | 240 / 255 / 94 %  | Structure (25) |
| D: middle          | (428, 257) |  27 |  82 / 179 / 40 % |  23 / 255 /  9 % | 0 / 0 / 0 % | 255 / 255 / 100 % | 232 / 255 / 91 %  | Noise (25) |
| E: lower-middle    | (375, 667) |  40 | 104 / 200 / 60 % |  93 / 255 / 36 % | 0 / 0 / 0 % | 255 / 255 / 100 % | 162 / 255 / 64 %  | Noise (20) + black |
| F: lower-mid-right | (500, 680) |  42 |  94 / 204 / 55 % | 116 / 255 / 45 % | 0 / 0 / 0 % | 255 / 255 / 100 % | 139 / 255 / 55 %  | **Icon (20)** + black |

For comparison, the same sampling on four floor-noise blobs (Icon-class blobs whose bbox contains no
real-icon glyph — i.e., the noise side of the 17:1 detection-recall failure):

| Floor blob | aligned XY | Deviation | Deviation mask | Morph-close | 07e Classify-png |
|---|---|---|---|---|---|
| blob 54  (24×23) | (703, 180) |  3 / 14 / 0 % |  0 /   0 /  0 % | 48 / 255 / 19 % | Icon + black |
| blob 75  (19×18) | (701, 205) |  2 / 14 / 0 % |  0 /   0 /  0 % | 34 / 255 / 13 % | Icon + black |
| blob 96  ( 8×10) | (297, 217) |  3 / 21 / 0 % | 116 / 255 / 45 % | 74 / 255 / 29 % | Icon + black |
| blob 158 (15×22) | (226, 297) | 53 / 72 / 0 % | 179 / 255 / 70 % | 76 / 255 / 30 % | Icon + black |

Read this table side by side with the real-icon table:

- **Deviation values at real-icon positions are 30–50× higher than at floor-noise positions.** The
  upstream deviation step IS doing its job — the signal-to-noise ratio is excellent.
- **Real icons survive the deviation mask** at 36–64 % above-half coverage (except IconC at 0 % —
  see "Per-icon failure modes" below); **floor noise also survives** (96, 158 above-half 45-70 %).
  The mask doesn't separate signal from noise; it just gates the alpha-boundary band.
- **Rim mask is 0 everywhere I sampled.** `rimPixelCount = 5` in `10c-blob-pipeline.json` for the
  whole 1127 × 1127 image — the rim mask removes 5 pixels out of 1.27 million. Not a recall factor.
- **Morph-close values at real icons are >2× the floor-noise values.** The icons are still
  separable here, after morph.
- **The classifier is the line.** Every real icon except F gets routed to a non-Icon class.

## Per-icon failure modes (canonical bundle)

For each missed icon, the blob whose bbox CONTAINS its aligned position, with the classifier verdict
explained against the production gates. Pulled from `10c-blob-pipeline.json` non-rotated records.
"Blob ord" is `blobOrdinal` — the same ordinal carried by `BlobTemplateScore.BlobOrdinal` and
`BlobClassification.BlobOrdinal` per #1121/#1123.

| Icon | Blob ord | bbox dims | area | solidity | aspect | peakDev | meanDev | Class | Failure mode |
|---|---|---|---|---|---|---|---|---|---|
| A | 58  | 36 × 7   |  231 | 0.92 | **5.14** | 1.00 | 0.87 | Noise | `aspect 5.14 > MaxAspect 2.5` |
| B | 40  | 48 × 54  | **1242** | 0.48 | 1.12 | 1.00 | 0.75 | **Structure** | `area 1242 > MaxIconArea 900`; large component engulfs B AND C |
| C | 40  | 48 × 54  | **1242** | 0.48 | 1.12 | 1.00 | 0.75 | **Structure** | same blob 40 as B — merged |
| D | 133 | 35 × 33  |  361 | **0.31** | 1.06 | 1.00 | 0.81 | Noise | `solidity 0.31 < MinSolidity 0.35` (margin 0.04) |
| E | 175 | 23 × 9   |  182 | 0.88 | **2.56** | 1.00 | 0.96 | Noise | `aspect 2.56 > MaxAspect 2.5` (margin **0.06**) |
| F | 176 | 13 × 23  |  152 | 0.51 | 1.77 | 1.00 | 0.90 | Icon  | passes all gates ✓ |

### IconA: 36 × 7 elongated blob

Blob 58 covers IconA's aligned position. Bbox is 36 × 7 — extreme aspect 5.14. The shape suggests
the icon's deviation halo has merged eastward along a line of floor noise. The icon glyph itself is
~7 × 7; the additional 29 px in X are floor-deviation noise that 8-connected through the morph-close
to the icon's halo.

This failure is upstream of the classifier: the **connected-components stage** is admitting too
much. Even if `MaxAspect` were relaxed to 6, the next bundle's elongation could be worse — the
audit found `asp 6.29` for the equivalent icon in bundle `06-12-235416`. The structural fix is
breaking the eastward connectivity, not chasing an aspect-ratio threshold.

### IconB + IconC: merged into one 1242-pixel Structure blob

The "upper-mid-right" and "upper-right" icons are 30 raw pixels apart (aligned (411, 185) vs (432,
202)). Both sit inside blob 40, which is 48 × 54 — a ~2600-pixel bbox with 1242 actual blob pixels
(solidity 0.48). The blob covers BOTH icons AND a quasi-rectangular region of floor between/around
them. Classifier verdict: `area > MaxIconArea (900)` and `meanDev 0.75 >= 0.6` → Structure.

This is the merge problem in its load-bearing form. Even if classifier gates were tuned to admit
1242-area blobs (which admits other Structure blobs as Icon class — a regression for typing), RANSAC
would get ONE detection where there are TWO icons. **The merge has to be broken upstream** —
in the deviation kernel or pre-classification morphology — for IconB and IconC to contribute two
distinct correspondences.

### IconD: solidity 0.31 < 0.35 by 0.04

Blob 133 contains IconD with area 361, aspect 1.06 (square), peakDev 1.00 (strong icon glyph), but
solidity 0.31 — 0.04 below the 0.35 floor. The bbox is 35 × 33 but only 361 of the 1155 enclosed
pixels are blob members — the icon's deviation halo is "sparse" inside its bbox. This is consistent
with a small bright glyph surrounded by a ring of mid-deviation halo: the bbox stretches to cover
the halo's outer edge but only the brightest pixels make the foreground mask.

Loosening `MinSolidity` to 0.30 admits this blob with no other ill effect visible in this bundle's
non-rotated set (the only sub-0.35 solidity blobs sitting in the icon-band of area are this one and
~~the elongated IconA blob (sol 0.92 — fine).~~ Looking at the broader set, blob 54 (the 24×23 floor
blob at (703, 180), aspect 4.00, peakDev 0.51) has solidity 0.30 and would be admitted if peakDev
gate passes — but it's blocked by `peakDev 0.51 < 0.7`. The peakDev gate handles the
floor-noise admission risk independently.

### IconE: aspect 2.56 > 2.5 by 0.06 — a systematic PG glyph

Blob 175 contains IconE with area 182, solidity 0.88 (excellent), peakDev 1.00, aspect 2.56. The
bbox is 23 × 9. **This exact aspect-2.56 / 23 × 9 bbox shows up in `Map_HogansKeepBasement-20260612-235416-091`
too**, on blob 144, with the same solidity 1.00 and peakDev 1.00. The 12-235416 bundle's IconE-equivalent
sits at aligned (396, 704) vs canonical's (375, 667) — a 21-pixel offset consistent with player
movement between captures. **It's the same in-game icon glyph** rendering at the same dimensions
across captures, and the production `MaxAspect = 2.5` rejects it by 0.06.

Bumping `MaxAspect` to 2.7 (a 0.2 margin above the observed 2.56) is the minimum-disturbance fix
for the most-reproducible Indoor recall miss across bundles.

## Cross-bundle confirmation

Same audit applied to two other Indoor Hogan's bundles + the GoblinDungeon sibling sub-zone. Pulled
in their entirety so the systematic-vs-bundle-specific patterns are visible.

### Bundle: `Map_HogansKeepBasement-20260612-235416-091-rejected-solve-insufficient-inliers`

Six visible real icons (per the bright-pixel scan). Locator: scale 1.16, origin (167, 86), aligned
1187 × 1187. Outcome: `2 inliers (need >= 4)`. Recall:

| Icon | Blob ord | bbox dims | area | solidity | aspect | peakDev | Class | Failure |
|---|---|---|---|---|---|---|---|---|
| A: upper-mid       | 14  | 44 × 7   |  264 | 0.86 | **6.29** | 1.00 | Noise | `aspect 6.29 > 2.5` |
| B: upper-mid-right |  8  | 104 × 57 | **2900** | 0.49 | 1.82 | 1.00 | **Structure** | `area > 900`; engulfs B + C |
| C: upper-right     |  8  | 104 × 57 | **2900** | 0.49 | 1.82 | 1.00 | **Structure** | same blob 8 as B |
| D: middle          | 29  | 72 × 55  | **1588** | 0.40 | 1.31 | 1.00 | **Structure** | `area > 900`; engulfs D + halo |
| E: lower-middle    | 144 | 23 × 9   |  207 | 1.00 | **2.56** | 1.00 | Noise | `aspect 2.56 > 2.5` (margin 0.06) |
| F: lower-mid-right | 147 | 12 × 22  |  160 | 0.61 | 1.83 | 1.00 | Icon ✓ | — |

Same systematic IconE aspect failure (2.56). Same B+C merge into one Structure (worse this time —
2900 area, engulfing more floor). Same IconF passes. Same dominant failure mode: **classifier
rejection of large or elongated icon-containing blobs**, with merge across adjacent floor noise as
the upstream cause for the large-blob cases.

### Bundle: `Map_HogansKeepBasement-20260610-091533-358-accepted`

The "accepted" cal the spec proposed as a recall-improvement comparison target. **Six visible real
icons, ZERO end up in Icon-class blobs.** The audit (bbox-containment + nearest-centroid) returns
the same blob for every icon — blob 5, area **119,655**, bbox 634 × 632, solidity 0.30. That single
blob covers the entire active map region of the aligned crop (~795 × 739 = 587k pixels; blob 5 owns
20 % of them as foreground).

The 4-inlier accept that produced this cal solved RANSAC against 27 Icon-class blobs (none of which
contain a real icon glyph) — confirmed by reading `10-detections.json`'s 18 emitted detections
against the bright-pixel scan: not one of the 18 detection anchors lands within 30 px of any real
icon. The accept was RANSAC fitting noise to a similarity transform.

This finding is consistent with spec §2.2's "synthesis-J `disagree: accept_to_reject`" and the
[#1116](https://github.com/moumantai-gg/mithril/issues/1116) cross-scene-leak hypothesis — but it
goes further: the 06-10 capture itself is structurally unrecoverable, because the underlying
deviation field is one giant connected component (the whole map view differs from the base texture
because of a global colour/lighting shift the audit didn't dig into). The recall-improvement Phase 2
fix CANNOT lift this bundle to a real cal; the design needs to (a) treat 06-10 as an out-of-scope
degenerate input, AND (b) ship the synthesis-J Shadow visibility that flags this kind of false-positive
before it reaches `refinements.json`.

**Spec §2.2's framing of 06-10 as a "comparison bundle" with better recall should be retracted.**

### Bundle: `Map_GoblinDungeon_TopFloor-20260610-095806-692-rejected-solve-insufficient-inliers`

Locator: scale 0.66, origin (230, 132), aligned 528 × 528. Outcome: `3 inliers (need >= 4)`.
**Only three real-icon glyphs visible in the capture** — the player view is small enough that the
RANSAC inlier floor of 4 is unreachable by signal alone. Of the three:

| Icon | Blob ord | bbox dims | area | peakDev | Class | Failure |
|---|---|---|---|---|---|---|
| Upper        |  — | — | — | — | NO BLOB | no Icon-class blob within 30 px; nearest is a 5-px below-area-min blob |
| Upper-near   | 17 | 2 × 3 |  5  | 0.53 | Noise | `area 5 < 12` |
| Middle       | 20 | 1 × 2 |  2  | 0.76 | Noise | `area 2 < 12` |

This bundle's failure isn't classifier shape gates — it's that the visible scene has too few icons
and the ones present render very small (areas 2, 5). Lowering `MinArea` to 4 admits the second two
into the icon-band of area, but they'd still need to pass `MinPeak 0.7`: the closer one (area 5)
has peakDev 0.53 (below floor) and the other (area 2) has peakDev 0.76 (passes peak but fails the
solidity-default for a 1×2 blob).

**GoblinDungeon-06-10 confirms that recall ≥ 4 is unreachable for some Indoor scenes regardless of
detector tuning** — the underlying signal isn't there. The Phase 2 success criterion has to be
per-bundle conditional on "≥ 4 real icons visible in the capture" rather than universal.

## Aggregate failure-mode tally

Across the four Indoor bundles audited, classifier-rejection reasons for icons whose deviation-mask
position survived into a blob:

| Failure mode | Canonical (06-13) | 12-235416 | 06-10 accept | GoblinDungeon | Notes |
|---|---|---|---|---|---|
| Aspect > 2.5 (icon-band) | 2 | 2 | 0 | 0 | Reproducible — IconE 2.56 is systematic |
| Solidity < 0.35 (icon-band) | 1 | 0 | 0 | 0 | IconD case |
| PeakDev < 0.7 (icon-band) | 0 | 0 | 0 | 0 | NOT a real recall failure mode here |
| Area > 900 → Structure | 2 | 3 | 6 | 0 | The merge problem |
| Area < 12 | 0 | 1 | 0 | 2 | GoblinDungeon scene size |
| No blob at icon position | 0 | 1 | 0 | 1 | Edge effect; small sample |
| **Reach Icon class ✓** | **1** | **1** | **0** | **0** | The lower-mid-right glyph, when present |

`LowNcc = 0.5` (the deviation threshold) doesn't fail any icon in this audit. Lowering it to 0.3–0.4
admits more floor-noise into the foreground without solving any of the real failure modes — the spec
called it out as a candidate but the audit data argues against touching it.

## Candidate tunables (ranked by audit evidence, not spec speculation)

The Phase 2 fix has to do **two things**: (1) admit blobs the classifier currently rejects, and
(2) break the upstream merging that produces ≥ 900-area blobs containing two icons. Either alone
recovers 1–3 icons per bundle but not the RANSAC floor of 4.

| # | Knob | Current | Proposed | Recovers | Risk |
|---|---|---|---|---|---|
| **T1** | `MaxAspect` | 2.5 | **2.7** | IconE (consistently 2.56 across bundles) | LOW — captures one repro icon; admits no obviously-noise blobs in audited bundles |
| **T2** | `MinSolidity` | 0.35 | **0.30** | IconD (sol 0.31 in canonical) | LOW — the only sub-0.35-icon-band noise blobs in the audit are blocked by other gates (peakDev 0.51 etc.) |
| **T3** | Split the B+C merged Structure blob | n/a | reduce deviation `win` from 11 to ~7, OR add morph-open before morph-close | IconB + IconC as two distinct icons (vs the current ONE Structure blob covering both) | MEDIUM — changes the upstream signal field; needs Outdoor regression battery |
| **T4** | `MaxIconArea` | 900 | leave at 900 (do NOT raise) | n/a | — Raising admits Structure blobs as Icon; T3 is the correct fix for the merge problem |
| **T5** | `MinPeak` | 0.7 | leave at 0.7 | n/a | All real icons in audit have peakDev = 1.00; the gate is doing its job blocking noise |
| **T6** | `LowNcc` | 0.5 | leave at 0.5 | n/a | Audit shows no icon dies at deviation step; spec's hypothesis falsified |
| **T7** | `BlobOpts.MinArea` | 12 | leave at 12 for Hogan's; possibly 4 for GoblinDungeon-class scenes | 2 icons in GoblinDungeon | LOW — but per-scene tuning further fragments the per-profile knob set; defer |
| T8 | morph `closeRadius` | 1 | leave at 1 | n/a | already minimal; reducing to 0 also won't break the merge — merge happens at the deviation level, not at morph |
| T9 | IconA's 5.14 / 6.29 aspect blob | n/a | NOT recoverable by classifier tuning alone | IconA — but only if T3 also breaks its eastward connectivity | HIGH — depends on T3 outcome |

### T1 + T2 + T3 sequenced

T1 and T2 are pure classifier-gate moves — single-line constant changes on the `BlobOptions` value
carried by the `SceneCalibrationProfile.Indoor` profile. They recover IconD + IconE in the canonical
bundle (2 of 4 needed) and the equivalent icons in 12-235416 (2 of 4 needed). They are necessary
but not sufficient.

T3 is the load-bearing fix. The merged 1242-pixel Structure blob in canonical (and 2900-pixel
equivalent in 12-235416) won't split by tuning classifier output — the connected-components labeller
sees them as one. Two upstream candidates the audit data supports:

- **Smaller deviation window (`win 11 → 7`).** The deviation kernel currently looks at an 11-pixel
  window around each pixel. Reducing to 7 tightens each icon's deviation halo and gives the
  inter-icon floor a chance to fall below `LowNcc = 0.5`, breaking the connectivity. Cost: the
  same icon's halo gets weaker by the same amount, possibly killing borderline real icons. Needs
  measurement.
- **Morph-open before morph-close (or instead of it).** A 1-pixel erosion before the 1-pixel
  dilation would erode the thin floor-noise bridges that connect adjacent icon halos, then the close
  re-fills the icons themselves. This is the textbook "open then close" pattern for separating
  touching objects.

The right T3 choice falls out of a measurement pass on the Indoor corpus — both candidates need to
be applied and the resulting `10c-blob-pipeline.json` blob counts compared. That work lives in the
Phase 2 sub-step 2 (implementation) PR, not this audit.

### T4 explicitly NOT proposed (why)

The spec lists "Per-profile `BlobOptions.MinArea` floor below 12" as a candidate and the parallel
plan §Phase 2 sub-step 2 mentions `MaxIconArea` adjustments. Raising `MaxIconArea` from 900 to
≥ 1500 admits the 1242-pixel B+C merged blob as Icon class — but it then produces one detection where
two icons exist. RANSAC's inlier search is detection-pair-based; one merged detection covering two
icons consumes one of the four needed inliers without delivering the second icon's geometric
constraint. The merge problem cannot be solved at the classifier; it has to be solved upstream.

Lowering `MinArea` from 12 helps the GoblinDungeon scene (areas 2, 5) but not the Hogan's family
where all missed icons have area ≥ 160. Defer — track as a follow-up if GoblinDungeon class scenes
become a priority.

## Implications for Phase 2 design micro-review

A. **The detector has a CLASSIFIER + CONNECTIVITY recall failure**, not a deviation/mask/morph
   failure. Per-profile tuning of `LowNcc` doesn't help. Per-profile tuning of `MinSolidity`,
   `MaxAspect` helps (T1 + T2). Per-profile change to the deviation kernel window or morph schedule
   helps (T3) — and is the LOAD-BEARING fix because it addresses the merge problem.

B. **The `SceneCalibrationProfile.Indoor` carrier needs three fields the spec hasn't named yet**:
   - `BlobOpts.MaxAspect` (currently in `BlobOptions` — confirmed needs per-profile override)
   - `BlobOpts.MinSolidity` (same)
   - Deviation kernel window size OR a morph-open radius (T3 — not in spec at all)

   The first two are constructor parameters of the existing `BlobOptions` record. The third needs a
   new field on `SceneCalibrationProfile` (or on `BlobOptions`) and a corresponding parameter on the
   deviation / morph code paths.

C. **Acceptance criterion for the Phase 2 fix has to be PER-BUNDLE**:
   - Canonical 06-13: ≥ 4 Icon-class blobs containing real-icon glyphs (per the `PeakLuma > 0.78,
     BrightPx ≥ 3` definition from this issue).
   - 12-235416: ≥ 4 Icon-class blobs containing real-icon glyphs.
   - GoblinDungeon-06-10: NOT a Phase 2 success target — only 3 real icons present. Track as a
     known-degenerate.
   - **06-10 "accepted": explicitly NOT a Phase 2 success target.** The fix can't lift it; the
     defensive change is synthesis-J Shadow visibility on the bundle (Phase 5) so future false
     positives surface.

D. **The original spec's untyped-detection direction (Phase 4) remains valid** but is independent of
   Phase 2 — once the classifier admits IconE-equivalents as Icon class, RANSAC's same-type pool
   constraint is the next failure point (per spec §2.1 "best score 0.7 with margin 0.01" for
   indoor-rendered icons). Phase 4's untyped path lets RANSAC type from geometric fit instead of
   per-blob NCC, which IS load-bearing once Phase 2 lifts the floor.

E. **Phase 3 (peak-luma pre-filter) composes cleanly with the above.** Real-icon blobs all have
   PeakLuma > 0.78 in their raw-BGRA bbox; floor-noise Icon-class blobs are at 0.22–0.40. Phase 3
   suppresses the residual noise the relaxed gates of T1+T2 would otherwise admit.

F. **Spec §6 verification owed items should be updated.** The spec's §6.f "Outdoor accept-rate
   regression — gates every Indoor-profile divergence PR" already covers the Outdoor side. Add a new
   §6.i: "Indoor real-icon-bbox detection rate ≥ 4 per non-degenerate bundle" — the Phase 2 success
   criterion at a doc level.

## Verification owed before the Phase 2 implementation PR

1. **T1 ceiling sensitivity.** Audit a wider Indoor corpus (any indoor bundle older than the four
   here, plus future captures) to confirm 2.7 is a sufficient `MaxAspect` for the systematic
   aspect-2.56 glyph. The 0.2-margin assumption is the audit's, not measurement.

2. **T3 candidate comparison.** Apply each of {deviation `win` 7, morph-open before morph-close,
   both} to the canonical bundle and report blob count + size distribution. The right T3 is the one
   that splits blob 40 (1242-area canonical) into two ≤ 900-area blobs each containing one of B/C
   without losing the IconF blob.

3. **Floor-noise sensitivity of T1 + T2.** With both gates relaxed (`MaxAspect 2.7, MinSolidity 0.30`),
   re-run the audit's nearest-blob lookup against ALL Icon-class blobs in the broader Indoor corpus
   and count how many gain Icon class versus how many additional REAL icons are admitted. Target:
   real-to-noise admission ratio > 0.5.

4. **Outdoor regression.** Per spec §6.f — Outdoor accept-rate byte-identical with the per-profile
   relaxed-gates applied to Indoor only. Already required by the plan; this audit doesn't change it.

The Phase 2 implementation PR landing the per-profile tuning + T3 fix should commit a new
measurement doc capturing #1-3 results.

## Tooling

Three throwaway PowerShell scripts produced this audit; the measurement DATA is durable, the
scripts aren't. Documented for reproducibility:

- `scan-bright-icons.ps1` — 8-connected component labelling on `R + G + B > 600` pixels in
  `02-screenshot-raw.png`; merges centroids within 8 px. Reports real-icon clusters in raw and
  aligned coords.
- `sample-stages.ps1` — 11×11 window sampling over the seven pipeline-stage PNGs at fixed
  aligned-coord points; reports `mean / max / aboveHalf%` per stage.
- `audit-bundle.ps1` — combines the bright-pixel scan, bbox-containment + nearest-centroid lookup
  against `10c-blob-pipeline.json`'s non-rotated blobs, and the production classifier gate
  reproduction. Per-icon failure-mode line + summary.

Scripts ran under PowerShell 7 against `$env:LOCALAPPDATA\Mithril\diagnostics\calibration\<bundle>\`
on a dev workstation. Bundle directories are dev-local (per the
`map_calibration_replay_fixtures_dev_local` project memory — PG art + 2-decimal zoom slider give
rule out contributor reproducibility), so the scripts can't run in CI. The durable output is this
markdown doc and the per-bundle tables it contains.

## Corpus extension — all 12 Indoor bundles classified

The initial audit sampled 4 of 12 available Indoor bundles. Running the same `audit-bundle.ps1`
across the remaining 8 reveals **two additional failure categories that aren't recall failures at
all** — locator mismaps and scene-degeneracy outcomes that the Phase 2 fix cannot help.

| Bundle | Outcome | Real icons visible | Locator-mapped crop covers icons? | Failure category |
|---|---|---|---|---|
| Map_HogansKeepBasement-20260613-230459-600 | rejected: 2 inliers (need 4) | 6 | yes | **Phase 2 target** ✓ |
| Map_HogansKeepBasement-20260612-235416-091 | rejected: 2 inliers (need 4) | 6 | yes | **Phase 2 target** ✓ |
| Map_HogansKeepBasement-20260610-091533-358 | accepted (suspected wrong, see §3) | 6 | yes (but degenerate) | Scene-degenerate |
| Map_HogansKeepBasement-20260610-154134-968 | rejected: no geom-consistent fit | 5 | yes (but degenerate) | Scene-degenerate |
| Map_HogansKeepBasement-20260610-154213-137 | rejected: no geom-consistent fit | 6 | yes (but degenerate) | Scene-degenerate |
| Map_HogansKeepBasement-20260610-154311-065 | rejected: no geom-consistent fit | 6 | **NO** — all icons at aligned (-599,-46) etc. | Locator mismap |
| Map_HogansKeepBasement-20260612-203727-499 | rejected: no geom-consistent fit | 6 | **NO** | Locator mismap |
| Map_HogansKeepBasement-20260612-203828-451 | rejected: no geom-consistent fit | (scanner returns 27, but mostly raw y > 1080 = UI/chat overlay) | n/a | **Excluded — scanner contamination** |
| Map_HogansKeepBasement-20260612-233006-375 | rejected: no geom-consistent fit | 6 | **NO** | Locator mismap |
| Map_HogansKeepBasement-20260612-235302-102 | rejected: no geom-consistent fit | (scanner returns 34, similarly UI-dominated) | n/a | **Excluded — scanner contamination** |
| Map_GoblinDungeon_TopFloor-20260610-095806-692 | rejected: 3 inliers (need 4) | 3 | yes | Insufficient-icon scene |
| Map_GoblinDungeon_TopFloor-20260610-095753-890 | rejected: no geom-consistent fit | 2 | yes | Insufficient-icon scene |

The **excluded** bundles have R+G+B > 600 bright-pixel clusters that the scanner can't distinguish
from in-game icons — most cluster at raw y > 1080 in a 1510 × 1313 screenshot, which is screen-bottom
UI/chat overlay territory, not in-game world icons. The scanner's bright-pixel test is sensitive to
white text in any overlay. Without an in-game-viewport mask there's no way to filter these out
mechanically, and the audit can't draw conclusions from contaminated input. Excluded from the corpus
analysis below; the captures are still useful for future audit work that has a viewport mask.

**Phase 2 fix helps 2 of 10 corpus bundles** (those classified "Phase 2 target"). The other 8 fail
upstream of the detection-recall step:

- **Scene-degenerate (3 bundles).** The locator succeeds but maps to a region where the deviation
  field is one giant connected component (areas 26,545 / 102,098 / 119,655 pixels). All visible icons
  sit inside one Structure-class blob. The recall fix can admit more Icon-class blobs in well-formed
  deviation fields; it cannot recover the missed icons when the whole field is a single component.

- **Locator mismap (3 bundles).** All "rejected-solve: no geometrically-consistent fit". The locator
  picked a small (143 × 143 / 281 × 281) cropped region of the screenshot that DOES NOT CONTAIN the
  real icons — all icons fall at negative or out-of-crop aligned coordinates. This is a locator
  failure, tracked separately from Phase 2 scope.

- **Insufficient-icon scene (2 bundles).** The locator mapped correctly but the captured scene has
  ≤ 3 real icons total — below the RANSAC inlier floor of 4 regardless of detector tuning.

### Recall-failure-modes hold across ALL Phase 2-target bundles

For the 2 Phase 2-target bundles (06-13 + 12-235416), the failure-mode tally I built on the canonical
bundle holds in 12-235416 too:
- IconE's aspect = 2.56 reproduces (same 23×9 bbox, same `MaxAspect 2.5` failure).
- IconA's `aspect > 2.5` reproduces (canonical 5.14, 12-235416 6.29 — bundle-dependent but always failing).
- The B+C merge reproduces (canonical 1242-area Structure, 12-235416 2900-area Structure — the merge is
  systematic at PG's indoor render scale, magnitude varies with player position).

n=2 is a smaller "Phase 2 target" corpus than I'd like, but the failure modes are reproducible
across both samples. T1 / T2 / T3 ranking holds.

### Implication for Phase 2 sub-step 3 (validation criteria)

The plan §Phase 2 verification calls for "Hogan's 06-13 bundle ≥ 4 Icon-class blobs containing real
icons; three other Indoor bundles measured to the same criterion." The corpus extension says
**three other Indoor "Phase 2 target" bundles don't currently exist** — 12-235416 is the only
sibling, and the other 10 bundles fail upstream of Phase 2.

Practical adjustment for the validation gate:
- Canonical 06-13: ≥ 4 real-icon Icon-class blobs (the load-bearing assertion).
- 12-235416: ≥ 4 real-icon Icon-class blobs (sibling confirmation).
- Future Indoor captures (specifically, "insufficient-inliers" outcomes with the locator-mapped crop
  containing real icons): same assertion, growing the corpus opportunistically as new captures land.
- Scene-degenerate, locator-mismap, and insufficient-icon-scene bundles are out-of-scope success
  targets but Phase 2 must not REGRESS them — verified by the Outdoor replay-fixture battery + a
  no-regression check that the existing 18 Icon-class noise blobs in canonical 06-13 (post-Phase 2)
  do not increase.

## What this audit deliberately doesn't do

- **Doesn't propose a specific fix.** T1 / T2 are obvious from the data; T3 needs measurement.
  The Phase 2 implementation PR owns the decision.
- **Doesn't measure Outdoor.** The Outdoor profile keeps today's constants per the spec; this
  audit's classifier-gate analysis is Indoor-only.
- **Doesn't fix the 06-10 "accepted" wrongness.** That's a synthesis-J Shadow (Phase 5) +
  `refinements.json` durability concern, not a recall problem.
- **Doesn't analyse the locator-mismap bundles.** Five of 12 corpus bundles fail because the locator
  picked the wrong region of the screenshot. That's a separate concern from Phase 2 (locator
  precision, not detection recall) and likely a follow-up sub-issue under #1116.
