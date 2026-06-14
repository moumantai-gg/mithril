# Indoor recall — T3 merge-fix candidate measurement

Phase 2 sub-step 2 measurement, driven by the
[`indoor-recall-stage-attribution.md`](indoor-recall-stage-attribution.md) audit's T3 question:

> Does varying the deviation kernel window (`win`) or the morph close-radius
> (`closeRadius`) split blob 40 — the production 1242-pixel `Structure` blob
> covering BOTH IconB (411, 185) and IconC (432, 202) — into two distinct
> components, each containing one icon?

Measured via [`IndoorRecallMergeTuningTests`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorRecallMergeTuningTests.cs)
on the canonical `Map_HogansKeepBasement-20260613-230459-600` bundle. Test
calls the production code path directly (`LocalNccDeviation.DeviationMap` +
`DeviationBlobDetector.DetectIconBlobs`) with each parameter combination, using
the bundle's `07a-deviation-mask.png` as the post-#1116 alpha+fog mask — so
the rig is **production-parity at the production parameters** (197 total
blobs, 18 Icon-class, blob 176 detected at IconF; asserted by the test).

## TL;DR — the audit's T3 hypothesis is FALSIFIED

**No combination of (`win`, `closeRadius`) splits the B+C merge.** Across the
8-point grid (`win ∈ {11, 9, 7, 5} × closeRadius ∈ {1, 0}`), IconB and IconC
end up in the same connected component every time. The merge survives the
narrowest tested window (5) AND morph-close disabled.

But the measurement DID find two adjacent results that move recall:

1. **Narrower `win` (≤9) tightens the deviation halo around individual icons,
   which drops IconE's blob aspect from 2.56 to 2.33 (just below the production
   `MaxAspect = 2.5` ceiling).** IconE recovers as Icon class with NO classifier
   gate change — purely from the upstream deviation kernel narrowing.
2. **`win=5, closeRadius=0` shrinks the B+C merged blob to 838 pixels** — below
   `MaxIconArea = 900`, so it drops from `Structure` to `Noise` instead. With T1
   (`MaxAspect 2.7`) + T2 (`MinSolidity 0.30`) — which the audit already
   recommended — that 838-pixel blob still wouldn't reach `Icon` (solidity 0.31,
   one notch under T2's 0.30 threshold). And it's still ONE detection covering
   two icons, so RANSAC only gets one correspondence.

So the path forward is **NOT** to break the merge with `win`/`closeRadius`. It's
to accept the merge while extracting better signal from elsewhere — specifically
IconE via narrower window, plus IconD/IconE via T1+T2 gate relaxation.

A second-pass measurement of `morph-open before close` (the audit's other T3
candidate) is filed as a follow-up — that intervention can't be plumbed through
`DetectIconBlobs` without a production code change, so it's deferred to the
implementation PR if needed.

## Measurement table

Production params (win=11, closeRadius=1) are bold. "RIC" = real-icon blobs
reaching `Icon` class (out of 6 visible). Container blob features at IconB's
aligned position (the merge probe — IconC sits inside the same blob in every
row).

| win | closeRadius | Total blobs | Icon-class | B+C blob area | B+C class | B+C aspect | B+C solidity | IconD class | IconE class | IconF class | RIC | B+C merged? |
|---:|---:|---:|---:|---:|---|---:|---:|---|---|---|---:|---|
| **11** | **1** | **197** | **18** | **1242** | **Structure** | **1.12** | **0.48** | **Noise(sol)** | **Noise(asp 2.56)** | **Icon** | **1** | **YES** |
| 11 | 0 |  351 |  21 | 1056 | Structure | 1.24 | 0.59 | Noise(sol) | Noise(asp 2.56) | Icon | 1 | YES |
| 9  | 1 |  243 |  40 | 1280 | Structure | 1.02 | 0.33 | Noise(sol)   | Icon (asp 2.33) | Icon | **2** | YES |
| 9  | 0 |  485 |  54 | 1042 | Structure | 1.25 | 0.43 | Noise(sol)   | Icon (asp 2.33) | Icon | **2** | YES |
| 7  | 1 |  344 |  55 | 1787 | Structure | 1.95 | 0.30 | (merged into B+C+D Structure) | Icon (asp 2.38) | Icon | 2 | YES |
| 7  | 0 |  693 | 101 | 1032 | Structure | 1.53 | 0.28 | Noise(sol)   | Icon (asp 2.38) | Icon | **2** | YES |
| 5  | 1 |  627 |  81 | 2338 | Structure | 1.21 | 0.34 | (merged into B+C+D Structure) | Icon (asp 2.12) | Icon | 2 | YES |
| 5  | 0 | 1312 | 162 |  838 | **Noise(sol)** | 1.08 | 0.31 | Icon (asp 2.07) | Icon (asp 2.12) | Icon | **3** | YES |

### What "RIC" counts

A real-icon blob "reaches Icon class" when:
- The blob's bbox contains the icon's aligned-space centroid, AND
- The blob's `BlobClass == Icon` per the production gates
  (`MinArea=12, MaxIconArea=900, MinSolidity=0.35, MaxAspect=2.5, MinPeak=0.7`).

IconA does NOT show "containing" blobs in this measurement because the audit's
single-point centroid (327, 180) sits just OUTSIDE the elongated blob 58 bbox
(307–342, 184–190). The production audit captured blob 58 via nearest-centroid
(d=7.6 px) rather than bbox containment; this measurement reports `NO blob
contains` for IconA, which is consistent. Recovering IconA requires either
(a) a less elongated blob (which means breaking the eastward floor-noise
connectivity — not addressed here) or (b) raising `MaxAspect` to ≥ 5.5 (rejected
by the audit as too permissive). Treat IconA as out-of-scope for the
`win`/`closeRadius` knobs.

## Per-finding analysis

### Finding 1 — B+C merge is structural, not parameter-tunable (within this knob set)

At every measured (`win`, `closeRadius`), IconB (aligned 411, 185) and IconC
(432, 202) sit in the same connected component. The B+C centroids are ~31 px
apart in aligned space and the floor texture between them produces a
deviation-NCC field that connects the two icon halos no matter how tight the
window. Even at `win=5` (the tightest the integral-image method supports) the
merged blob area is 627–838 pixels — enough to span both icons.

Splitting the merge needs a DIFFERENT mechanism. The audit listed
`morph-open before close` as the other T3 candidate. That isn't reachable from
the current `DetectIconBlobs` public surface — it would need an extra parameter
on the call OR a new pipeline branch. Filed as a follow-up; see "Open
follow-ups" below.

### Finding 2 — Narrower window recovers IconE via the aspect ceiling

IconE blob 175 (production: 23 × 9, aspect 2.56) is rejected at `MaxAspect =
2.5` by margin 0.06. At `win=9` the equivalent blob bbox shrinks to 21 × 9
(aspect 2.33); at `win=7` to 21 × 9 (aspect 2.38); at `win=5` to 17 × 8
(aspect 2.12). All three are below the production aspect ceiling.

This is a clean, single-parameter recovery: **drop `win` from 11 to 9 and IconE
reaches Icon class with NO classifier gate change.** The narrower window tightens
the deviation halo on the eastern/western sides of the icon glyph, pulling the
bbox in on the long axis without losing the icon's centre pixels (`peakDev=1.00`
throughout).

### Finding 3 — `win=5, closeRadius=0` drops the merged blob below `MaxIconArea`

At `win=5, closeRadius=0` the merged B+C blob has area 838 < 900, so the
classifier routes it to the icon-band branch instead of the Structure branch.
With production gates it still classifies as `Noise (solidity 0.31 < 0.35)` —
but the failure mode changed from "blob too big" to "blob too sparse".

Combined with the audit's T2 (`MinSolidity → 0.30`), the gates would NOT admit
838-area-blob-sol-0.31 either: 0.31 > 0.30 satisfies the relaxed gate, so it
WOULD become Icon. BUT — it's still **ONE detection covering two icons**. RANSAC
gets one correspondence where it needs two.

`win=5` is also expensive on the false-positive side: 162 Icon-class blobs (vs
production's 18) — most are floor noise that will get killed at the per-blob NCC
typing step, but the increased pool size raises the noise-floor of the typed
RANSAC inlier search (more candidates × per-pair tests). The doubling of
Icon-class-blob count from `win=11` to `win=9` (18 → 40) is more proportionate
than the doubling at `win=7` (55) or the 9× explosion at `win=5` (162).

### Finding 4 — `closeRadius=0` is the wrong knob for the recall question

Removing the morph close-step adds ~80 % more total blobs without changing the
classification outcome for any of the 6 real icons. The B+C merge persists
because their floor-noise bridge is already connected pre-morph; removing the
1-pixel dilation step doesn't disconnect them. The only place `closeRadius`
moves the needle is in the `win=5` row, where it drops the merged blob area
below the 900 ceiling — but as Finding 3 showed, that doesn't deliver two
detections.

## Recommendation for the Phase 2 implementation PR

The audit's three knobs (T1 / T2 / T3) need this refinement based on the
measurement:

- **T1 (`MaxAspect 2.5 → 2.7`).** STILL VALID. The IconE blob's aspect at
  production `win=11` is 2.56; T1 admits it without touching `win`. T1 alone
  recovers 1 additional real icon.
- **T2 (`MinSolidity 0.35 → 0.30`).** STILL VALID. The IconD blob's solidity at
  production `win=11` is 0.31; T2 admits it. T2 alone recovers 1 additional
  real icon.
- **T3 (`win 11 → 9` or smaller).** PARTIALLY VALID. Doesn't split the B+C merge
  as the audit hypothesized, but DOES recover IconE via the aspect side. Two
  paths in play:
  - **T3a: `win 11 → 9` ALONE.** Recovers IconE without T1. Modest blob-count
    increase (197 → 243). Marginally more noise to the RANSAC pool.
  - **T3a' do NOT touch `win`, use T1 instead.** Equivalent IconE recovery via
    classifier-gate relaxation, with zero upstream pipeline change. Strictly
    smaller blast radius. **PREFERRED** path for v1.

The recommended Phase 2 v1 profile divergence (Indoor):

```
BlobOptions {
  MinArea: 12              (unchanged)
  MaxIconArea: 900         (unchanged)
  MinSolidity: 0.30        (was 0.35 — T2; recovers IconD-class)
  MaxAspect: 2.7           (was 2.5 — T1; recovers IconE-class)
  MinPeak: 0.7             (unchanged)
}
LowNcc: 0.5                (unchanged)
DeviationKernelWin: 11     (unchanged — T3a's recovery is delivered by T1 alone)
MorphCloseRadius: 1        (unchanged)
```

Net recall lift on canonical 06-13: **+2 real icons reach Icon class** (IconD,
IconE join the production IconF). Total: 3/6 reachable via classifier gates.

That falls SHORT of the audit's "≥ 4 real-icon blobs (the RANSAC floor)"
acceptance criterion by one icon. The remaining icons are IconA (elongated
blob — needs different connectivity treatment, deferred) and IconB+C (merged
into one Structure blob — needs morph-open or chroma-aware deviation, deferred).

**Phase 2 v1 acceptance has to be revised down to "≥ 3 real-icon blobs", OR the
implementation PR pursues a Phase 2 v1.5 that lands morph-open as an additional
profile knob.** The latter is the cleaner architectural path: a `MorphOpenRadius`
field on `BlobOptions` / `SceneCalibrationProfile`, defaulting to 0 (Outdoor
behavior unchanged), set to 1 on Indoor. The measurement-test rig in this PR
extends naturally to add a morph-open parameter row.

## Open follow-ups

### Measure morph-open as a T3 alternative

The audit's "morph-open before close" candidate requires either a production
code change (an `openRadius` parameter on `DetectIconBlobs`, plus the
corresponding Erode→Dilate stage in the pipeline) OR replication of the entire
detector pipeline in the test. The first is a small production change that the
Phase 2 implementation PR can land alongside the profile; the second is the
right path if morph-open turns out to not help (no production change).

Recommended sequencing: the Phase 2 implementation PR adds the `MorphOpenRadius`
knob on `SceneCalibrationProfile` and a default-0 parameter on `DetectIconBlobs`,
then this measurement test gains a `morphOpenRadius ∈ {0, 1}` × `win ∈ {9, 11}`
sub-matrix that lands as a Phase 2.5 follow-up measurement.

### Recover IconA

IconA's blob (production 58: 36 × 7, aspect 5.14) is connected to floor noise
eastward. Neither `win` nor `closeRadius` breaks the connectivity. Candidates:

- **Chroma- or luma-aware deviation.** PG indoor icons are bright-white; the
  adjacent floor is mid-gray. A PRE-deviation luma threshold (e.g., only treat
  pixels with screenshot luma > 180 as deviation candidates) would isolate icon
  glyphs from floor texture and skip the noise side entirely. Different from the
  audit's "chroma pre-filter" idea — chroma doesn't separate (per
  [`indoor-chroma-threshold.md`](indoor-chroma-threshold.md)), but **luma might**.
- **A taller-than-wide / narrower-than-tall aspect ceiling that admits one
  orientation per icon type.** Would need icon-type-aware classification and
  raises the case complexity.

Both are larger structural changes; not v1 material.

### Outdoor regression battery

The recommended Indoor profile divergence (T1 + T2 only, `win` unchanged) is the
smallest possible blast-radius change. Outdoor profile keeps today's constants
verbatim. Replay-fixture battery on Serbule / Eltibule / Kur Mountains must
verify byte-identical solves under the per-profile dispatch — gates the Phase 2
implementation PR. Test already exists ([`ReplayFixtureTests`](../../../../tests/Mithril.MapCalibration.Tests/Detection/ReplayFixtureTests.cs)).

## Reproducibility

The measurement test ([`IndoorRecallMergeTuningTests`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorRecallMergeTuningTests.cs))
ships in this PR. Gated on the canonical bundle existing at
`%LOCALAPPDATA%\Mithril\diagnostics\calibration\Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers\`
(dev-local per the
[`map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md)
project memory — the bundle is PG art and a contributor can't reproduce the
2-decimal zoom-slider state). A dev with the bundle on disk runs:

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~IndoorRecallMergeTuningTests" --logger "console;verbosity=detailed"
```

— and reads the per-test `Standard Output Messages` block to see the per-icon
breakdown at each parameter combo. The parity guard at production parameters
(`win=11, closeRadius=1` asserts `Total=197, IconClass=18, IconF.Area=152,
IconF.Ordinal=176`) catches any drift in upstream code that would invalidate the
measurement.

A future Phase 2.5 measurement pass should re-run with the morph-open knob added
+ append a section to this doc.
