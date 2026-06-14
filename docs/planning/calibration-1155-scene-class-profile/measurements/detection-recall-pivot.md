# NEW finding — Mode-B has the wrong load-bearing direction

**Discovered during the Phase 0 spike, outside the §6 plan.**

The spec's chosen direction (candidate E, untyped detection + RANSAC discrimination) assumes the detector **finds real-icon blobs and mis-types them.** The spike data shows that's wrong for Indoor: the detector **doesn't find most real-icon blobs at all.**

## The data

Canonical bundle: `Map_HogansKeepBasement-20260613-230459-600-rejected-solve-insufficient-inliers`.

**Visible real icons in raw screenshot (`02-screenshot-raw.png`)**, identified by scanning for bright-pixel clusters (RGB > 200):

| Cluster center (raw XY) | Aligned XY | Bright px |
|---|---|---|
| (521, 304) | (324, 187) | 37 |
| (521, 295) | (324, 178) | 36 |
| (528, 302) | (331, 185) | 14 |
| (526, 295) | (329, 178) | 11 |
| (608, 302) | (411, 185) | 16 |
| (696, 799) | (499, 682) | 12 |
| (571, 786) | (374, 669) | 10 |
| (625, 373) | (428, 256) | 10 |
| (628, 318) | (431, 201) | 7 |
| (630, 318) | (433, 201) | 6 |

→ The screenshot contains **~5-6 distinct icon glyphs** (some clusters are adjacent and likely the same icon; some are clearly separate).

**Detected Icon-class blobs (non-rotated pass)**, from `10c-blob-pipeline.json`, with each blob's raw-screenshot `PeakLuma` + `BrightPx`:

| Blob | Aligned XY | Dim | PeakLuma | BrightPx | Real-icon? |
|---|---|---|---|---|---|
| 176 | (488, 668) | 13×23 | **0.91** | **30** | **YES** (lower-middle cluster (499, 682)) |
| 158 | (226, 297) | 15×22 | 0.40 | 0 | NO |
| 73 | (353, 204) | 7×11 | 0.31 | 0 | NO |
| 170 | (353, 331) | 7×7 | 0.31 | 0 | NO |
| 160 | (353, 299) | 7×8 | 0.31 | 0 | NO |
| 68 | (297, 194) | 8×18 | 0.31 | 0 | NO |
| 96 | (297, 217) | 8×10 | 0.28 | 0 | NO |
| 75 | (701, 205) | 19×18 | 0.28 | 0 | NO |
| 54 | (703, 180) | 24×23 | 0.27 | 0 | NO |
| 37 | (563, 172) | 23×31 | 0.27 | 0 | NO |
| 20 | (546, 138) | 40×23 | 0.26 | 0 | NO |
| 104 | (804, 223) | 21×16 | 0.26 | 0 | NO |
| 79 | (786, 208) | 27×12 | 0.25 | 0 | NO |
| 23 | (522, 145) | 18×18 | 0.25 | 0 | NO |
| 152 | (804, 280) | 12×10 | 0.25 | 0 | NO |
| 181 | (1005, 870) | 4×8 | 0.25 | 0 | NO |
| 151 | (354, 279) | 6×5 | 0.24 | 0 | NO |
| 189 | (965, 912) | 3×6 | 0.22 | 0 | NO |

**Of 18 Icon-class blobs, only 1 contains a real icon glyph.** The other 17 are pure floor-texture noise.

**Of 5-6 visible icons in the screenshot, 4-5 are not detected as blobs at all** (no Icon-class blob covers their position). The visible icons at aligned (324, 178-187), (411, 185), (428, 256), (374, 669), (431, 201) have no overlapping blob within 30 px in the detection output.

## Why this changes the spec

The spec's candidate menu (§4) and chosen direction (§5) implicitly assume:

> "Real-pip blobs ARE detected; the per-blob NCC step mis-types them; untyped detection + RANSAC type-from-geometry fixes the typing failure."

The spike data falsifies the antecedent. **Most real-pip blobs aren't detected at all.** Untyped RANSAC operating on a pool of 17 noise + 1 signal won't find 4 inliers regardless of how cleverly it types them.

## What the actual failure is

The detection pipeline's deviation → rim → morph → classify cascade misses real Indoor icons because:

- Indoor icons are **light gray glyphs on stone-grey floor** — low-contrast against the baseline texture
- The deviation step (`LocalNccDeviation`) sets a `LowNcc = 0.5` floor on per-pixel deviation; low-contrast icon pixels may fall below
- The morph-close + rim-mask cascade may then connect remaining icon-fragments to nearby floor-noise, collapsing them into "Noise" or "Structure" class
- The single Icon-class blob that DID survive (blob 176) is the one at the highest-contrast region — it's also the only one with `solidity = 0.51` (lower than typical Icon-class — possibly because the icon-glyph + adjacent floor-pixels merged into one blob)

The fix has to live upstream of typing — in the deviation / mask / morph / classify pipeline — not in the RANSAC step.

## Implication for spec + plan — proposed re-sequence

Rather than rewriting the spec in this measurements PR (the spec is on `main` already), file a follow-up sub-issue under #1155 titled **"Indoor icon-blob recall (Mode-B v1 root cause)"** with the scope:

1. **Investigate the deviation/mask/morph/classify stages on the Indoor corpus.** For each visible-but-undetected icon, trace where in the pipeline it gets lost. Output: a stage-attribution table per icon, per bundle.
2. **Propose tunable fixes.** Candidates: lower `LowNcc` for Indoor profile; chroma-aware deviation kernel (compare colour channels independently — even though indoor icons are grayscale, their luma profile against the floor MIGHT differ from floor-vs-floor); shape-aware morph; per-class min-area adjustment.
3. **Validate against the corpus.** Each tunable fix must:
   - Lift Indoor real-icon-detection rate to ≥ 4 blobs per bundle (the RANSAC floor)
   - Not regress Outdoor accept rate

After that lands, the original spec's Phase 2 (untyped detection) becomes a useful tier-2 improvement — *once we have enough real-icon blobs that RANSAC has correspondences to find.*

Re-sequence:

| Original phase | New role |
|---|---|
| Phase 1 — Scaffolding | Unchanged |
| Phase 2 — Indoor untyped detection | **DEMOTED** — becomes Phase 4 |
| Phase 3 — chroma pre-filter | **REPLACED** by peak-luma pre-filter (per [`indoor-chroma-threshold.md`](indoor-chroma-threshold.md)) |
| Phase 4 — adaptive synthesis-J | **DEFERRED** — becomes Shadow-only for v1 (per [`indoor-synthesis-j-threshold.md`](indoor-synthesis-j-threshold.md)) |
| **NEW Phase 2 — Indoor icon-blob recall** | The actual load-bearing fix. Scope per "Implication" section above. |
| **NEW Phase 3 — Peak-luma pre-filter** | Defence-in-depth noise suppression. |

## Caveat — this is one bundle

`n=1` bundle. The pattern (only 1 of 18 Icon-class blobs contains a real glyph) is striking, but the broader Indoor corpus should be checked before this lands as a finalized spec revision. The follow-up "Indoor icon-blob recall" sub-issue should include corpus expansion as its first task.

In particular, the HogansKeepBasement-06-10 accepted bundle DID solve with 4 inliers — so it had ≥ 4 real-icon blobs. Why that bundle achieves better detection-recall than the 06-13 bundle is itself a question worth answering: was it a different player position, different alpha-zero coverage, different ambient lighting? The answer informs the v1 recall fix.

## Why the spike caught this

The original spec was written from a reading of `10b-blob-template-scores.json` (per-template NCC scores) without grounding against pixel-level reality in the screenshot. The score-distribution analysis suggested "real pips score 0.7, noise scores 0.86" — but the spike's pixel-level scan revealed the "real pips" hypothesis was wrong; those 0.7-scoring blobs are also noise, just at a different region of the same floor.

This is exactly the failure mode `verify_headline_behavior_through_full_render_chain` (project memory) warns against — verified-green at the score-distribution layer does NOT mean behavior-verified at the pixel layer. The spike caught it before code shipped.
