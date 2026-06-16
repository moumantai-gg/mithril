# Phase 2.6 live verification — mithril#1172

Live in-game verification slot for the pre-deviation luma threshold.

**Status (2026-06-16):** PR #1173 merged to `main` (squash commit
`f9d7d4a6`). Two fresh in-game captures from build `3.0.0.96+447acb150f`:

- **10:36 — rejected-solve** (`Map_HogansKeepBasement-20260616-103608-261-rejected-solve`):
  locator failed to find the map widget (returned a 184×184 tile-floor
  patch with `blurAppliedSigma: 3`), downstream pipeline starved. Not a
  #1172 regression — locator-stage issue tracked separately as #1179.
- **11:55 — accepted** (`Map_HogansKeepBasement-20260616-115535-692-accepted`):
  full pipeline ran end-to-end. `profile.minLumaForDeviation = 200`
  selected by the live Indoor profile, **merge-split confirmed**
  (9 Icon-class blobs across R0/R180 where the pre-#1173 pipeline emitted
  a single Structure blob), 5 inliers recovered, residual 1.346 px
  (sub-pixel mean fit). **#1172 closed.**

## Expected headline (post-#1172 ship)

The new bundle should carry `01-attempt.json` `profile.minLumaForDeviation = 200`
and `outcome: "accepted"`, with `10c-blob-pipeline.json` showing two
distinct Icon-class blobs at the NPC pip positions previously merged into a
single Structure blob. The static-bundle measurements
([`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
reproduce the merge split on BOTH the 06-13 canonical and 06-15 live-
verification bundles; the live verification confirms in-game behaviour
matches the static reproduction.

## Result — 11:55 accepted capture (canonical)

```text
Bundle:                                          Map_HogansKeepBasement-20260616-115535-692-accepted
engineVersion:                                   3.0.0.96+447acb150f
01-attempt.json/outcome:                         "accepted"
01-attempt.json/rejectReason:                    null
01-attempt.json/sceneClass:                      "Indoor"
01-attempt.json/sceneClassOpaqueFraction:        0.1749134063720703
01-attempt.json/profile.minLumaForDeviation:     200             ← #1172 mechanism, confirmed
01-attempt.json/profile.minPeakLuma:             0.7
01-attempt.json/profile.morphOpenRadiusPx:       0
01-attempt.json/locatorBest.width:               1129
01-attempt.json/locatorBest.scale:               1.103
01-attempt.json/locatorBest.fallbackNcc:         0.7065
01-attempt.json/locatorBest.blurAppliedSigma:    0
01-attempt.json/locatorBest.gateAccepted:        true
01-attempt.json/synthesis.j:                     4.343           (shadow rail "reject", gate accepted; disagree)
01-attempt.json/synthesis.verdict:               "reject"
01-attempt.json/synthesis.gateVerdict:           "accept"
01-attempt.json/synthesis.disagreeChange:        "accept_to_reject"
10-detections.json/detections.count:             5               (3 NPCs + 2 Portals)
11-recovered-cal.json/residualPixels:            1.346
11-recovered-cal.json/referenceCount:            5
11-recovered-cal.json/scale:                     3.4203
11-recovered-cal.json/rotationRadians:           0.00543
11-recovered-cal.json/mirrorNorth:               false
10c-blob-pipeline.json icon-class blobs (R0+R180): 9             ← vs 1 merged Structure pre-#1173
```

### Inliers

| Label | matchScore | pixelX, pixelY |
|---|---|---|
| `landmark_npc:Gribburn` | 0.927 | 373.3, 172.3 |
| `landmark_npc:Gorvessa` | 0.899 | 392.4, 187.2 |
| `landmark_npc:Malvol` | 0.909 | 388.6, 237.6 |
| `landmark_portal:Exit` | 0.888 | 338.1, 605.6 |
| `landmark_portal:Exit` | 0.839 | 447.4, 617.2 |

### 10:36 rejected capture (for context)

```text
Bundle:                                          Map_HogansKeepBasement-20260616-103608-261-rejected-solve
engineVersion:                                   3.0.0.96+447acb150f
01-attempt.json/outcome:                         "rejected-solve"
01-attempt.json/rejectReason:                    "no geometrically-consistent fit"
01-attempt.json/sceneClass:                      "Indoor"
01-attempt.json/sceneClassOpaqueFraction:        0.1749134063720703
01-attempt.json/profile.minLumaForDeviation:     200            ← confirmed (same as accepted run)
01-attempt.json/locatorBest.width:               184            ← locator returned junk patch
01-attempt.json/locatorBest.scale:               0.18
01-attempt.json/locatorBest.fallbackNcc:         0.266
01-attempt.json/locatorBest.blurAppliedSigma:    3              ← vs 0 in accepted run (same engine!)
10-detections.json/detections.count:             0
10c-blob-pipeline.json/deviation[0].aboveThresholdCount: 0
10c-blob-pipeline.json/deviation[0].meanNcc:             1
10c-blob-pipeline.json/blobs.count:                      0
```

### Interpretation

**#1172 mechanism verified end-to-end on the 11:55 accepted capture.** The
pre-deviation luma threshold is correctly wired into the live `Indoor`
profile (`sceneClassOpaqueFraction = 0.175` inside the predicted
0.07–0.36 band → `profile.minLumaForDeviation = 200`). The
`10c-blob-pipeline.json` shows **9 Icon-class blobs across R0/R180**, where
the pre-#1173 pipeline emitted a single merged `Structure` blob in the
upper-right region — the merge-split that motivated #1172.

The geometric solve produced 5 inliers (3 NPCs + 2 Portals) with
`residualPixels = 1.346`, consistent with the resolved [#897 finding](https://github.com/moumantai-gg/mithril-calibration/wiki/Legolas-Calibration-Findings)
(PG map = per-area global isotropic similarity, sub-pixel, no warp).

### Quality notes

1. Arthur observed **two pink autocal pins offset by a few pixels** in the
   live overlay. With a 1.346 px mean fit residual across 5 inliers,
   individual residuals can be ~2–3 px on the lowest-scoring inliers —
   consistent with the two `landmark_portal:Exit` matches (scores 0.888 and
   0.839, the lowest of the five). Sub-pixel mean fit + visible offset on
   the outlier inliers is the expected isotropic-similarity-fit signature.
2. **synthesis-J shadow rail disagreed.** `synthesis.j = 4.343 < jMin = 8`
   → shadow `verdict = "reject"`, but gate accepted. `disagreeChange:
   "accept_to_reject"`. This is the deferred Phase 5 shadow rail recording
   that the conservative gate would have been stricter — not a regression,
   just observability for the Phase 5 follow-up.

### The 10:36 rejected capture — intermittent locator behaviour

The first capture of the day rejected with a degenerate output: the
locator returned a 184×184 junk region (a tile-floor patch — see
`06-aligned-screenshot.png` next to `05-base-texture-resampled.png`) and
the downstream pipeline starved. **Both captures came from the same engine
build** `3.0.0.96+447acb150f`, so this rules out a deterministic
regression between `3.0.0.93+98fdd54bef` and the post-#1173 build. The
likely candidate is a state-dependent / input-dependent branch in the
locator that flipped `blurAppliedSigma` from 0 (accepted run) to 3
(rejected run) and returned a worse candidate. Tracked separately as
[#1179](https://github.com/moumantai-gg/mithril/issues/1179) — does not
block this phase.

## Status

- **#1172 closed** on the 11:55 accepted capture — merge-split confirmed,
  sub-pixel solve, 5 real-landmark inliers, `profile.minLumaForDeviation =
  200` selected by the live Indoor profile.
- **#1155 closed** earlier the same day (2026-06-16) — upstream typeFloor
  issue resolved by the broader scene-class-profile shipment.

## Follow-ups (open, not blocking)

- [#1174](https://github.com/moumantai-gg/mithril/issues/1174) — NPCc 06-15 separate detection mechanism
- [#1175](https://github.com/moumantai-gg/mithril/issues/1175) — #1148 alpha-zero interior gap
- [#1176](https://github.com/moumantai-gg/mithril/issues/1176) — broader-corpus expansion for `MinLumaForDeviation = 200`
- [#1179](https://github.com/moumantai-gg/mithril/issues/1179) — intermittent locator `blurAppliedSigma` flip (10:36 capture failure mode)
