# §6.c (Phase 3) — Broader-corpus Indoor peak-luma threshold

**Verdict: CONFIRMED.** The spike's single-bundle PeakLuma separation reproduces across the broader Indoor bundle corpus. `MinPeakLuma = 0.7` cleanly partitions real-icon blobs (PeakLuma ≥ 0.78, n = 5/130 in the corpus) from floor-noise blobs (PeakLuma ≤ 0.55, n = 125/130) with a 0.23-wide separation band that contains zero blobs.

## What this expands on

The Phase 0 spike ([`indoor-chroma-threshold.md`](indoor-chroma-threshold.md)) sampled 7 of 18 Icon-class blobs in ONE bundle (canonical Hogan's 06-13) and found PeakLuma 0.91 for the lone real-icon blob vs 0.22–0.40 for floor-noise. The spec §6.c-replacement flagged "the peak-luma threshold is derived from ONE bundle — verification owed against the broader Indoor corpus."

This doc captures that broader corpus measurement.

## Method

Ran [`Measure_peak_luma_distribution_across_indoor_corpus`](../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorRecallMergeTuningTests.cs) on every Indoor bundle present under `%LOCALAPPDATA%/Mithril/diagnostics/calibration/`:

- 11 bundles total — Hogan's (8) + GoblinDungeon (3).
- Each bundle's `06-aligned-screenshot.png` (gray) + `05-base-texture-resampled.png` + `07a-deviation-mask.png` loaded via the existing test fixture infrastructure.
- Indoor profile applied (`MaxAspect = 2.7, MinSolidity = 0.30`), with the peak-luma filter DISABLED so all Icon-class blobs surface.
- For each Icon-class blob, `PeakLumaFilter.PeakLuma(blob, rawBgra, width, height)` computed over the connected-component pixel list against the BGRA-loaded screenshot.

**BGRA caveat (acknowledged).** `06-aligned-screenshot.png` is saved as `Gray8` by `FilesystemCalibrationAttemptBundleSink`. The WIC `LoadBgra` helper produces R=G=B=gray, so per-pixel BT.601 luma collapses to the gray value. This is a PROXY for the production raw BGRA (true multi-channel via GetDIBits), but the §6.c chroma measurement already established that PG's Indoor scenes are essentially grayscale (icons + floor both desaturated). The proxy is equivalent for the populations being measured.

## Per-bundle results

| Bundle | Total Icon-class | < 0.40 | [0.40, 0.78) | ≥ 0.78 | ≥ 0.70 (filter passes) | Range | Median |
|---|---:|---:|---:|---:|---:|---|---:|
| `Map_GoblinDungeon-…095904-227-rejected-solve` | 5 | 0 | 5 | 0 | 0 | [0.51, 0.55] | 0.55 |
| `Map_GoblinDungeon_TopFloor-…095753-890-rejected-solve` | 5 | 0 | 5 | 0 | 0 | [0.42, 0.53] | 0.51 |
| `Map_GoblinDungeon_TopFloor-…095806-692-rejected-solve-insufficient-inliers` | 15 | 1 | 14 | 0 | 0 | [0.21, 0.55] | 0.51 |
| `Map_HogansKeepBasement-…091533-358-accepted` | 32 | 0 | 32 | 0 | 0 | [0.43, 0.55] | 0.50 |
| `Map_HogansKeepBasement-…154134-968-rejected-solve` | 0 | — | — | — | — | — | — |
| `Map_HogansKeepBasement-…154213-137-rejected-solve` | 5 | 0 | 5 | 0 | 0 | [0.48, 0.55] | 0.54 |
| `Map_HogansKeepBasement-…154311-065-rejected-solve` | 1 | 1 | 0 | 0 | 0 | [0.25, 0.25] | 0.25 |
| `Map_HogansKeepBasement-…203727-499-rejected-solve` | 4 | 3 | 1 | 0 | 0 | [0.23, 0.47] | 0.37 |
| `Map_HogansKeepBasement-…233006-375-rejected-solve` | 3 | 3 | 0 | 0 | 0 | [0.23, 0.37] | 0.30 |
| `Map_HogansKeepBasement-…235416-091-rejected-solve-insufficient-inliers` | 40 | 38 | 0 | 2 | 2 | [0.22, 0.92] | 0.26 |
| `Map_HogansKeepBasement-…230459-600-rejected-solve-insufficient-inliers` (canonical) | 20 | 16 | 1 | 3 | 3 | [0.22, 0.93] | 0.27 |

## Corpus aggregate

| Metric | Value |
|---|---:|
| Bundles with Indoor-classified Icon-class blobs | 10 |
| Total Icon-class blobs | **130** |
| PeakLuma < 0.40 (floor noise) | 62 (48 %) |
| PeakLuma in [0.40, 0.78) (mid-band) | 63 (48 %) |
| PeakLuma ≥ 0.78 (real-icon range per §E) | **5** (4 %) |
| PeakLuma ≥ 0.70 (Phase 3 threshold) | **5** (4 %) |
| Blobs in [0.55, 0.78) (the threshold's safety band) | **0** |

## Interpretation

### 1. The 0.7 threshold is structurally safe

Across 130 Icon-class blobs spanning 10 bundles, **zero** sit in the `[0.55, 0.78)` band. The 5 blobs ≥ 0.70 are the same 5 blobs ≥ 0.78. The threshold has a 0.23-wide cushion — no blob is "close to the cliff."

The maximum PeakLuma observed in any non-real-icon blob (across all 125 non-≥0.78 blobs) is **0.55** (multiple bundles cap at this value). 0.7 is 0.15 above this ceiling. The threshold is robust.

### 2. The §E hypothesis is partially correct

§E predicted "real-icon blobs all have PeakLuma > 0.78; floor-noise Icon-class blobs are at 0.22–0.40." The first half (≥ 0.78 = real icon) holds across the corpus. The second half is **wrong**: 63 of 130 floor-noise blobs (48 %) sit in the [0.40, 0.78) mid-band, mostly clustered around 0.50–0.55.

This doesn't break Phase 3 — the threshold sits above the mid-band's ceiling, not below it. But future spec revisions of §E should drop the "≤ 0.40" claim. The accurate framing is "real-icon blobs at PeakLuma ≥ 0.78, all floor-noise blobs at PeakLuma ≤ 0.55, with a 0.23-wide separation band."

### 3. The 06-10 "accepted" bundle is a known false positive

`Map_HogansKeepBasement-20260610-091533-358-accepted` has 32 Icon-class blobs all in [0.43, 0.55]. **Zero are above 0.7.** Applying the Phase 3 filter to this bundle drops every Icon-class blob → no inliers → reject.

This is the documented outcome per the stage-attribution audit:

> "06-10 'accepted': explicitly NOT a Phase 2 success target. The fix can't lift it; the defensive change is synthesis-J Shadow visibility on the bundle (Phase 5) so future false positives surface."

And the spec §2.2:

> "Synthesis-J shadow: j: 3.25, refsAboveHalf: 5, jMin: 8. gateVerdict: accept, verdict: reject, disagree: true, disagreeChange: accept_to_reject. The cal currently live in users' refinements.json is structurally fragile."

Phase 3 catches this false positive structurally (peak-luma rejects floor-noise typing) instead of waiting for Phase 5 (synthesis-J enforcement) to flag it. Both gates point at the same cal as wrong; one ships now.

### 4. The canonical 06-13 bundle exhibits the §6.c spike's claim, plus 2 more real icons

Canonical Hogan's 06-13: 20 Icon-class blobs (16 below 0.40, 1 in [0.40, 0.78), 3 ≥ 0.78). The spike measured 1 real icon (blob 176, PeakLuma 0.91). The full Indoor profile (T1+T2) admits 2 more Icon-class blobs containing IconD + IconE — which sit at PeakLuma ≥ 0.78 too. The 3 ≥ 0.78 blobs map to the 3 admitted real icons. Cross-check with [`Indoor_profile_with_peak_luma_filter_drops_noise_blobs_and_keeps_real_icons`](../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorRecallMergeTuningTests.cs) confirms this: post-filter Indoor returns 3 blobs, all 3 cover the IconD/E/F centroids.

### 5. Where the [0.40, 0.78) mid-band comes from

48 % of Icon-class blobs sit in this band. They're not real icons (would be ≥ 0.78) and not "deep dark floor noise" (would be < 0.40). They are PG floor textures at mid-gray brightness — cobble stone, lit by ambient light, no glyph overlay. The bundles where the mid-band dominates (091533, 095904, 095753, 154213) all have higher average screen brightness; the dim-screen bundles (203727, 233006, 235416, 230459) push more blobs below 0.40. Brightness shifts the noise band but the real-icon band (≥ 0.78) is invariant to ambient lighting because the icons themselves render at saturated white.

## Decision

**Ship Indoor profile with `MinPeakLuma = 0.7`.** The corpus-wide separation (max noise PeakLuma = 0.55; min real-icon PeakLuma = 0.78) is wider than the spike measured and the threshold sits in the middle of the gap.

**No deferred sub-issue.** The spec §6.c-replacement said "if no separating threshold exists in the broader corpus, Phase 3 ships disabled and we file a deferred sibling." That condition is not met — the threshold separates cleanly.

## Sample-size caveats

- 10 bundles with data; total real-icon-containing blobs n=5. Statistical confidence on the upper-band cluster is limited by how few Indoor bundles contain real icons reaching Icon class. The Phase 2 measurement document already addresses recall-side improvement; as Phase 2 lifts recall, more Indoor bundles will surface real-icon blobs and re-running this measurement will tighten the upper-band statistics.
- All bundles are Hogan's Keep Basement + GoblinDungeon. Other Indoor scenes (no captures yet) may exhibit different floor-luma distributions. The structural argument (PG icons render at saturated white; floor textures cap at mid-gray) generalises to any Indoor scene that follows PG's overlay/texture convention.
- The Gray8 → BGRA proxy is exact for grayscale Indoor scenes (per §6.c chroma measurement). If PG ever ships a colourful Indoor scene the measurement would need re-running against the production raw BGRA from `captureResult.Color.Bgra`.

## Wire to the spec

- Spec §5.1 — Indoor `BlobOpts.MinPeakLuma = ~0.7` resolves to **0.7** (broader-corpus measurement).
- Spec §6.c-replacement — verification owed: ✅ CONFIRMED.
- Spec §6.f (Outdoor regression) — peak-luma is no-op outdoors (`MinPeakLuma = null` in Outdoor profile), so byte-identical-by-construction.
- Spec §6.h (#1163 Indoor icon-blob recall) — composes cleanly with Phase 3. Phase 2 admits +2 real-icon blobs (IconD, IconE); Phase 3 keeps all 3 (D+E+F) and drops the 17 surviving floor-noise blobs in the canonical bundle.
