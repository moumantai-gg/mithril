# §6.c — Indoor chroma pre-filter

**Verdict: NEGATIVE.** Chroma doesn't separate real-icon blobs from floor-noise blobs in Indoor scenes. PG indoor icons are grayscale (white/cream glyphs on grayscale stone floor), not saturated.

**Recovery:** A different feature — **peak luma** — DOES separate cleanly. See "Peak luma as the alternative" below.

## What the spec assumed

> "PG icons are saturated white/cyan/red; floor / off-texture noise is desaturated. Require min-chroma on blob pixels before NCC fires."  
> — spec §4 candidate F + §5.1 Indoor profile + §6.c

## What the data says

For 7 blobs in the canonical Hogan's bundle, raw-screenshot pixel statistics over the blob bbox:

| Blob | Aligned XY | Dim | MeanChroma | MaxChroma | MeanLuma | PeakLuma | BrightPx |
|---|---|---|---|---|---|---|---|
| 96 (emit Portal 0.86) | (297, 217) | 8×10 | 0.04 | 0.07 | 0.23 | 0.28 | 0 |
| 176 (emit Portal 0.83) | (488, 668) | 13×23 | 0.04 | **0.12** | 0.35 | **0.91** | **30** |
| 68 (sub Portal 0.76) | (297, 194) | 8×18 | 0.03 | 0.06 | 0.22 | 0.31 | 0 |
| 37 (sub Portal 0.75) | (563, 172) | 23×31 | 0.01 | 0.04 | 0.19 | 0.27 | 0 |
| 20 (sub Portal 0.75) | (546, 138) | 40×23 | 0.01 | 0.04 | 0.19 | 0.26 | 0 |
| 54 (sub Medi 0.70) | (703, 180) | 24×23 | 0.01 | 0.04 | 0.19 | 0.27 | 0 |
| 75 (sub Medi 0.70) | (701, 205) | 19×18 | 0.01 | 0.04 | 0.20 | 0.28 | 0 |

Chroma is essentially **0.01-0.04** across the board — including the one blob (176) that we now know contains a real icon glyph (per peak-luma + bright-pixel-count, established in [`detection-recall-pivot.md`](detection-recall-pivot.md)). PG indoor map icons render as light gray-on-darker-gray, not colourful, so chroma can't discriminate.

## Peak luma as the alternative

The same scan that killed the chroma hypothesis revealed that **peak luma cleanly separates** real-icon-containing blobs from floor-noise blobs:

| Blob | PeakLuma | BrightPx (>0.78 luma) | Real icon? |
|---|---|---|---|
| 176 | **0.91** | **30** | **YES** (only one in the bundle) |
| 96 | 0.28 | 0 | NO (floor noise) |
| 68 | 0.31 | 0 | NO |
| 37 | 0.27 | 0 | NO |
| 20 | 0.26 | 0 | NO |
| 54 | 0.27 | 0 | NO |
| 75 | 0.28 | 0 | NO |
| 158 | 0.40 | 0 | NO |
| 73 | 0.31 | 0 | NO |
| 170 | 0.31 | 0 | NO |
| 160 | 0.31 | 0 | NO |
| ... (10 more) | 0.22-0.31 | 0 | NO |

Across all 18 non-rotated Icon-class blobs in the canonical bundle:

- The one blob containing a real icon glyph: `PeakLuma = 0.91`, `BrightPx = 30`.
- The 17 other blobs (floor noise): `PeakLuma ∈ [0.22, 0.40]`, `BrightPx = 0`.

**Massive separation.** A threshold of either `PeakLuma > 0.7` OR `BrightPx ≥ 3` would catch the real-icon blob and reject all 17 noise blobs.

## Implication for spec

- Spec §5.1 Indoor profile — `MinChroma` clause: remove. Replace with `MinPeakLuma` (or equivalent — name TBD by spec revision).
- Spec §4 candidate F — chroma framing was wrong; rewrite as "luma pre-filter."
- Plan Phase 3 — re-scope from "chroma pre-filter" to "peak-luma pre-filter." File-level breakdown stays nearly identical (the implementation is "compute max luma per blob region before NCC fires"); just the feature name changes.

**Caveat:** the peak-luma threshold is derived from ONE bundle. Spec §6.c-replacement should re-state verification owed against the broader Indoor corpus before the threshold ships.

## Sample-size caveat

This analysis ran against 18 blobs from a single bundle. The single real-icon blob (176) is `n=1` ground truth. Other bundles likely contain more real-icon blobs (the accepted Hogan's 06-10 cal had 4 inliers, implying ≥ 4 real-icon blobs in that bundle). Re-running the peak-luma analysis against those bundles is the natural next step and should land before Phase 3 ships.
