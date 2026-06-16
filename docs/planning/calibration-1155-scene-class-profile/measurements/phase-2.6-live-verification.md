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
generalises to the in-game profile selection. However the entire
blob-detection pipeline emitted zero output: deviation has
`aboveThresholdCount = 0`, `meanNcc = 1` (degenerate empty-mask state),
morph input count = 0, blobs = 0, detections = 0. The Phase 5 synthesis-J
shadow rail likewise produced `verdict: "no_winner"` with `synthesis.j =
null`. The geometric solver had nothing to fit and rejected with
`"no geometrically-consistent fit"`. The 06-15 baseline at the same area
produced `aboveThresholdCount = 149,483` and 119+ blobs, so this is a new
failure mode.

The locator candidate also collapsed to a 184×184 / scale-0.18 region vs
1246×1246 / scale-1.217 on 06-15, with `fallbackNcc` dropping from 0.718 to
0.266 — the locator picked a very different candidate this run. Two
candidate root-cause families: (1) the in-game camera zoom / map-window
state on this capture differed materially from the 06-15 baseline and the
locator settled on a poor candidate that then starved the deviation
pipeline; (2) something in the locator / deviation path regressed between
engine `3.0.0.93+98fdd54bef` (06-15) and `3.0.0.96+447acb150f` (06-16). The
06-16 attempt finalized 0.94 s after start (06-15 took 5.0 s), consistent
with the pipeline short-circuiting on an empty mask.

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
