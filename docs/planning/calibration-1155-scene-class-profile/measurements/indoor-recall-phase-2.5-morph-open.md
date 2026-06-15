# Indoor recall — Phase 2.5 morph-open measurement

Phase 2.5 of the mithril#1155 scene-class slug. Implements the audit's other T3
candidate — **square-element morphological OPEN (erode-then-dilate) applied to
the foreground buffer BEFORE morph-close** — and sweeps it against the canonical
Hogan's 06-13 bundle to test whether it splits the load-bearing IconB+IconC
merge that the [`indoor-recall-stage-attribution.md`](indoor-recall-stage-attribution.md)
audit identified and the [`indoor-recall-merge-fix-candidates.md`](indoor-recall-merge-fix-candidates.md)
T3 sweep deferred.

## TL;DR — morph-open hypothesis FALSIFIED on the canonical bundle

**No combination of `(openRadius ∈ {0, 1, 2, 3}, closeRadius ∈ {0, 1})` splits
IconB (411, 185) and IconC (432, 202) into two distinct connected components,
each classified as `BlobClass.Icon`.** The B+C connecting region in the
deviation map is a substantial bridge — not a thin spike — and survives every
erosion-then-dilation kernel tested.

Worse, every non-zero `openRadius` value DEGRADES Real-Icon-Class (RIC) recall
on this bundle: at `openRadius=2`, IconE and IconF collapse below `MinArea` or
above the icon-band aspect ceiling (they're narrow halos that the erosion
shrinks asymmetrically and the dilation doesn't reconstitute back to their
original shape).

The path forward is therefore NOT to enable morph-open on Indoor. Phase 2.5
ships the **carrier infrastructure** (`Morphology.Open`, `openRadius` parameter
on `DetectIconBlobs`, `MorphSnapshot.OpenRadius`, `SceneCalibrationProfile.MorphOpenRadiusPx`,
`DetectionRequest.MorphOpenRadiusPx`, `SceneCalibrationProfileJson.MorphOpenRadiusPx`)
with both Outdoor and Indoor profiles at `MorphOpenRadiusPx = 0` — byte-identical
to pre-#1155 behaviour. A future investigator can flip the Indoor value once a
different audit angle (chroma-aware deviation, watershed split, alternative
connectivity treatment) lifts the underlying bridge structure.

## Measurement table

Production deviation kernel held at `win=11`. Indoor profile T1+T2 gates
applied (`MaxAspect=2.7, MinSolidity=0.30, MinPeak=0.7`); peak-luma pre-filter
disabled so the table reports the raw classifier output. "RIC" = real-icon
blobs reaching `BlobClass.Icon` (out of 6 visible). "B+C blob" features at
IconB's aligned position (the merge probe — IconC sits in the same blob in
every row).

| openRadius | closeRadius | Total blobs | Icon-class | B+C blob area | B+C class | B+C aspect | B+C solidity | IconD class | IconE class | IconF class | RIC | B+C split? |
|---:|---:|---:|---:|---:|---|---:|---:|---|---|---|---:|---|
| **0** | **1** (prod) |  197 |  20 | 1242 | Structure | 1.12 | 0.48 | **Icon** | **Icon** | **Icon** | **3** | NO |
| 0  | 0  |  351 |  23 | 1056 | Structure | 1.24 | 0.59 | Icon       | Icon       | Icon       | 3   | NO |
| 1  | 1  |   58 |  25 | 1059 | Structure | 1.22 | 0.64 | Icon       | Noise(asp) | Icon       | 2   | NO |
| 1  | 0  |   74 |  27 | 1025 | Structure | 1.22 | 0.62 | Icon       | Noise(asp) | Icon       | 2   | NO |
| 2  | 1  |   24 |  12 |  996 | Structure | 1.22 | 0.60 | Icon       | Noise(asp) | Noise(asp) | 1   | NO |
| 2  | 0  |   25 |  13 |  990 | Structure | 1.22 | 0.59 | Icon       | Noise(asp) | Noise(asp) | 1   | NO |
| 3  | 1  |   14 |   7 |  974 | Structure | 1.22 | 0.61 | Icon       | Icon       | (gone)     | 2   | NO |
| 3  | 0  |   14 |   7 |  970 | Structure | 1.22 | 0.61 | Icon       | Icon       | (gone)     | 2   | NO |

Reproduced via [`IndoorRecallMergeTuningTests.Measure_morph_open_pipeline`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorRecallMergeTuningTests.cs):

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests `
  --filter "FullyQualifiedName~IndoorRecallMergeTuningTests.Measure_morph_open_pipeline" `
  --logger "console;verbosity=detailed"
```

(Bundle gate per the existing convention — bundle dev-local per
[`map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md).)

## Per-finding analysis

### Finding 1 — The B+C bridge is structurally not severable by morph-open

At every measured `openRadius`, IconB and IconC sit in the same connected
component. The B+C blob bbox stays at roughly the same dimensions (~36×44 to
~38×47 pixels) across `openRadius ∈ {1, 2, 3}`; only the *area inside* the
bbox shrinks as the erosion peels foreground pixels off the boundary. The
bbox shape itself doesn't separate into two — meaning the connecting bridge
is a substantial interior region, not a 1-px-thin connector that erosion
could sever.

This rules out morph-open as a viable B+C split mechanism. The mental model
"the bridge is a 1-px-thin filament between two well-defined glyph halos" was
wrong; the reality is closer to "the two glyph halos in the deviation map
overlap substantially because the icons are only 31 px apart in aligned space
and the local-NCC kernel's spatial smearing fills the gap".

### Finding 2 — Every non-zero `openRadius` DEGRADES Real-Icon-Class recall

`openRadius=0`: 3/6 RIC (IconD + IconE + IconF) — Phase 2's T1+T2 baseline.

`openRadius=1`: 2/6 — IconE drops from Icon (aspect 2.56 with T1=2.7) to
Noise(aspect=2.88 over T1). The erosion shrinks IconE's already-narrow halo
(23×9 pre-open → ~23×8 after erode-dilate cycle); since the halo is asymmetric
the dilation regrows the long axis more than the short axis, pushing aspect
over the ceiling.

`openRadius=2`: 1/6 — IconE drops via aspect (now 2.75); IconF drops via
aspect (now 3.67, well over). Only IconD survives (which sits in a denser
deviation cluster that holds together through the erode-then-dilate cycle).

`openRadius=3`: 2/6 — IconF disappears entirely from the blob set; IconE
recovers because its halo collapses below `MinArea` during erosion and then
the dilation builds it back into a compact (aspect 2.62) blob — but IconF's
narrow halo never reconstitutes.

This degradation pattern is unsurprising in hindsight: the icons that
classifier-tuning recovers (IconD+E+F) are the ones whose deviation halos sit
at the edge of being narrow / sparse / borderline. Morph-open is exactly the
operation that punishes narrow/sparse blobs by eroding them out of existence.
The same kernel that *could* split a thin bridge erases narrow icon halos.

### Finding 3 — `closeRadius` is independent of `openRadius` on this bundle

For every fixed `openRadius`, changing `closeRadius` between 0 and 1 changes
the total blob count by ~10–80 % but doesn't change any real-icon's
classification outcome and doesn't split the B+C merge. The close stage's
job — bridging fragmented icon pixels — is orthogonal to the open stage's
job — severing thin connecting bridges. Confirms the sequencing (open BEFORE
close) is correct in concept; the issue is that the open step doesn't sever
*this particular* bridge.

### Finding 4 — `MaxIconArea = 900` traps the B+C merge into Structure regardless

At `openRadius ≥ 2`, the merged B+C blob area drops to 970–996 — just over
the 900 ceiling. This is the same trap the previous merge-fix-candidates
measurement noted at `win=5, closeRadius=0` (B+C area = 838, dropped to
Noise, but still ONE blob spanning two icons). Even if `openRadius=2` HAD
shrunk B+C below 900, the result would be "one Icon-class blob covering two
icons" — RANSAC still gets one correspondence where it needs two.

The merge is structurally one connected component on this bundle. Splitting
it requires breaking the connectivity, not shrinking the area below the
ceiling.

## Ship path — Phase 2.5 carrier, both profiles disabled

The PR ships the morph-open infrastructure with `MorphOpenRadiusPx = 0` on
both Outdoor and Indoor profiles in v1:

```text
SceneCalibrationProfile.Outdoor.MorphOpenRadiusPx = 0   (byte-identical to pre-#1155)
SceneCalibrationProfile.Indoor.MorphOpenRadiusPx  = 0   (per the negative result above)
```

Carrier components landed:

- `Morphology.Open(bool[], w, h, r)` — sibling to `Morphology.Close`, runs
  erode-then-dilate at radius `r`. Used by `DetectIconBlobs` when `openRadius
  > 0`.
- `DetectIconBlobs(..., int openRadius = 0)` — new positional default-0
  parameter. When > 0, runs `Morphology.Open` BEFORE the existing morph-close
  stage. Default 0 preserves the byte-identical pre-#1155 fast path.
- `MorphSnapshot.OpenRadius` (init-only, default 0) — surfaces the kernel
  radius for the orientation pass in `10c-blob-pipeline.json` via
  `MorphSectionJson` (TBD if needed) and the per-orientation Trace lines.
- `SceneCalibrationProfile.MorphOpenRadiusPx` (positional, default 0) — the
  profile-level knob. Outdoor and Indoor both ship 0 in v1.
- `DetectionRequest.MorphOpenRadiusPx` (init-only, default 0) — request-level
  carrier. `AutoCalibrationEngine` sources from `profile.MorphOpenRadiusPx`
  for both the main calibration path and drift-check path.
- `SceneCalibrationProfileJson.MorphOpenRadiusPx` — JSON wire format. Always
  emitted (no nullable) so jq filters and downstream tooling can read it
  unconditionally; both profiles emit 0 in v1.

The Outdoor replay-fixture battery is byte-identical by construction:
`openRadius=0` skips the entire open branch (no allocation, no buffer
mutation, no log line) so the pipeline reaches `Morphology.Close` with the
identical fg buffer it produced before this PR.

## Open follow-ups for a future Phase 2.5-v2 / Phase 2.6

The negative result above falsifies the audit's morph-open T3 candidate
specifically. The B+C merge problem itself is still open. Alternative angles
worth investigating in a follow-up issue under #1155:

1. **Pre-deviation luma threshold.** Per the
   [`indoor-recall-merge-fix-candidates.md`](indoor-recall-merge-fix-candidates.md)
   "Recover IconA" follow-up — PG indoor icons are bright-white (PeakLuma
   > 0.78); the adjacent floor connecting B+C is mid-gray (luma ~120–140 /
   255). A PRE-deviation luma threshold (only treat pixels with screenshot
   luma > 180 as deviation candidates) would suppress the floor-noise bridge
   while keeping both icon glyphs intact. Different mechanism than the
   post-classification peak-luma filter Phase 3 ships — this would change
   what the deviation map sees, not what survives classification.

2. **Watershed / distance-transform split.** Replace the binary
   connected-components labeller with a watershed-style split that breaks a
   single component into multiple at narrow waist points. More complex code
   but doesn't assume the bridge is "thin" in pixel terms — operates on the
   deviation magnitude gradient instead.

3. **Per-blob centroid bbox-vs-content disambiguation.** When a single Icon
   /Structure-class blob's bbox contains TWO known-good-real-icon centroid
   candidates (per per-template NCC peaks), split the bbox into two halves
   and emit two TypedDetection records. This is a post-classifier mechanism;
   doesn't change blob detection but changes how RANSAC sees correspondences.

4. **Lower `RansacInlierPx` gate from 4 to 3 for Indoor.** Treat-the-symptom
   path — 3 high-confidence detections geometrically consistent IS a valid
   similarity fit. Risk: the 4-inlier gate defends against collinear-noise
   edge cases; relaxing it for Indoor admits more wrong cals into
   `refinements.json`. Not recommended without a sibling gate (e.g.
   synthesis-J Enforced) to compensate.

Recommend filing as a single follow-up issue under #1155 with the data above,
and gathering additional Indoor bundles before committing to a direction —
the canonical 06-13 bundle is one data point and the alternative-angle
choices benefit from a broader corpus.

## Reproducibility

Same dev-local convention as the predecessor measurement: bundle must be
present at
`%LOCALAPPDATA%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers/`.
Production-parity guard at `(openRadius=0, closeRadius=1, Indoor T1+T2)`
asserts 3/6 RIC so an upstream pipeline change that drifts this baseline
fails the measurement test loudly.

## Outdoor regression

Verified byte-identical by construction: `Outdoor.MorphOpenRadiusPx = 0`
means `request.MorphOpenRadiusPx = 0` arrives at `DetectIconBlobs`, which
short-circuits the `openRadius > 0` branch. Full Outdoor replay battery
(Serbule / Eltibule / Kur) continues to solve unchanged.

The full slnx test battery (`dotnet test Mithril.slnx --no-build`) ran
clean apart from two unrelated parallel-collection flakes
(`SynthesisRerankShadowModeTests.Mode_Off_emits_no_synthesis_span` once, and
`GameReportsServiceTests.FileSystemWatcher_DebouncesAndFires_OnNewExport`
once across two separate runs) — neither involves morph-open or
calibration-profile state.
