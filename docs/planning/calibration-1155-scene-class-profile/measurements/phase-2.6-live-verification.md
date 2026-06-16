# Phase 2.6 live verification — mithril#1172

Live in-game verification slot for the pre-deviation luma threshold.

**Status (2026-06-16):** PR #1173 merged to `main` (squash commit
`f9d7d4a6`). Fresh in-game capture taken from build
`3.0.0.96+447acb150f` (post-merge). The headline `profile.minLumaForDeviation
= 200` is confirmed wired into the live profile, but the attempt rejected at
the geometric-fit stage with a degenerate blob pipeline — a new failure mode
that did NOT appear on the 06-15 pre-merge baseline at the same area. See
"Result" below. #1172 stays open for the Phase 2.7 investigation; #1155
stays open behind it.

## Expected headline (post-#1172 ship)

The new bundle should carry `01-attempt.json` `profile.minLumaForDeviation = 200`
and `outcome: "accepted"`, with `10c-blob-pipeline.json` showing two
distinct Icon-class blobs at the NPC pip positions previously merged into a
single Structure blob. The static-bundle measurements
([`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
reproduce the merge split on BOTH the 06-13 canonical and 06-15 live-
verification bundles; the live verification confirms in-game behaviour
matches the static reproduction.

## Result

```text
Bundle:                                      Map_HogansKeepBasement-20260616-103608-261-rejected-solve
engineVersion:                               3.0.0.96+447acb150f
01-attempt.json/outcome:                     "rejected-solve"
01-attempt.json/rejectReason:                "no geometrically-consistent fit"
01-attempt.json/sceneClass:                  "Indoor"
01-attempt.json/sceneClassOpaqueFraction:    0.1749134063720703
01-attempt.json/profile.minLumaForDeviation: 200            ← confirmed
01-attempt.json/profile.minPeakLuma:         0.7
01-attempt.json/profile.morphOpenRadiusPx:   0
01-attempt.json/synthesis.j:                 null
01-attempt.json/synthesis.verdict:           "no_winner"
01-attempt.json/locatorBest.width:           184            ← vs 1246 on 06-15
01-attempt.json/locatorBest.scale:           0.18           ← vs 1.217 on 06-15
01-attempt.json/locatorBest.fallbackNcc:     0.266          ← vs 0.718 on 06-15
10-detections.json/detections.count:         0              ← expected ≥ 4
10c-blob-pipeline.json/deviation[0].aboveThresholdCount: 0  ← vs 149,483 on 06-15
10c-blob-pipeline.json/deviation[0].meanNcc:             1  ← degenerate (empty mask)
10c-blob-pipeline.json/rimMasks.blob_detection[0].rimPixelCount: 0
10c-blob-pipeline.json/morph[0].fgInputCount:            0
10c-blob-pipeline.json/blobs.count:                      0
```

### Interpretation

The pre-deviation luma threshold is correctly wired into the live profile
(scene class = `Indoor`, opaque fraction in the expected 0.07–0.36 band,
`profile.minLumaForDeviation = 200`), so the static-bundle measurement
generalises to the in-game profile selection.

The actual failure chain (sharpened after looking at the bundle images):

1. **The locator failed to find the map widget.** `02-screenshot-raw.png`
   shows the in-game map rendered large and well-lit with pips visible at
   near-native size — this is NOT a tiny-minimap operational state.
2. **`locatorBest` returned a junk 184×184 region.** `04-maprect.json`
   `origin (838, 430) / size 184×184` is identical to `locatorBest`, so the
   maprect that fed every downstream stage is whatever the locator picked.
   `06-aligned-screenshot.png` shows that picked region is a uniform dark
   tile-floor patch with no map content.
3. **Downstream pipelines starved on the junk region.** The 11×11 windowed
   NCC against the actual base texture (`05-base-texture-resampled.png`)
   degenerates to 1 because the chosen region has near-zero variance,
   making `aboveThresholdCount = 0`, no rim mask, no morph input, no
   blobs, no detections. The Phase 5 synthesis-J shadow rail likewise
   produced `verdict: "no_winner"` with `synthesis.j = null`. The
   geometric solver had nothing to fit and rejected with `"no
   geometrically-consistent fit"`.

Compounded by suspicious locator-stage stats:

| Field | 06-15 baseline | 06-16 fresh |
|---|---|---|
| `locatorBest.inlierCount` | 0 | 0 |
| `locatorBest.candidateCount` | 0 | 0 |
| `locatorBest.fallbackNcc` | 0.718 | **0.266** |
| `locatorBest.blurAppliedSigma` | 0 | **3** |
| `locatorBest.scale` | 1.217 | **0.18** |
| `locatorBest.width` | 1246 | **184** |

The `blurAppliedSigma` flipping from 0 to 3 between the two runs is a
non-trivial pre-filter behaviour change and a plausible proximate cause —
worth grepping the `MapCalibration` project for that field. The 06-16
attempt finalized 0.94 s after start (06-15 took 5.0 s), consistent with
the pipeline short-circuiting on an empty mask.

The pre-deviation luma threshold from #1172 itself is structurally fine:
it's only invoked after the locator has chosen a region, and this run the
locator never gave it a real map to gate. The merge-split demonstration
owed to #1172 still requires a non-degenerate capture.

## Follow-up

`profile.minLumaForDeviation = 200` is confirmed wired. The merge-split
demonstration on a real in-game capture is **not** confirmed because the
06-16 bundle has no blobs to count. Filed as Phase 2.7 follow-up
[#1179](https://github.com/moumantai-gg/mithril/issues/1179) — #1172 stays
open behind it; needs a second capture under matched conditions (or a fix
for the new starvation mode) before #1172 can close.

The reject reason does NOT match the
[`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md)
prediction (`"only 3 inliers"` after gate); the new bottleneck is upstream
of the gate itself. `10b-blob-template-scores.json` is null in this bundle
because there were no blob candidates to score.
