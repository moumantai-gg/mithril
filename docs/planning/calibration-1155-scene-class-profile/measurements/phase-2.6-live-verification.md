# Phase 2.6 live verification — mithril#1172

Live in-game verification slot for the pre-deviation luma threshold. Updated
in-place after Arthur captures a fresh Hogan's Basement attempt from this
branch.

## Expected headline (post-#1172 ship)

The new bundle should carry `01-attempt.json` `profile.minLumaForDeviation = 200`
and `outcome: "accepted"`, with `10c-blob-pipeline.json` showing two
distinct Icon-class blobs at the NPC pip positions previously merged into a
single Structure blob. The static-bundle measurements
([`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
reproduce the merge split on BOTH the 06-13 canonical and 06-15 live-
verification bundles; the live verification confirms in-game behaviour
matches the static reproduction.

## To populate

1. Pull `feat/calibration-1172-pre-deviation-luma` and rebuild.
2. Capture a fresh attempt in Hogan's Keep Basement.
3. The bundle lands at
   `%LOCALAPPDATA%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-<ts>-<outcome>/`.
4. Replace this section with the bundle name + the headline JSON fields
   below.

```text
Bundle:                              <TBD>
01-attempt.json/outcome:             <TBD — expected "accepted">
01-attempt.json/profile.minLumaForDeviation: <TBD — expected 200>
10-detections.json/count:            <TBD — expected ≥ 4>
```

## If the bundle still rejects

The `01-attempt.json` `rejectReason` field tells which downstream stage is
the new bottleneck. A `"only 3 inliers (need >=4)"` after the gate is
sufficient evidence the merge-split worked (Icon blobs exist) but a different
stage is now the limit — investigate via `10b-blob-template-scores.json` for
template-match scores at the new candidates. Anything else is a Phase 2.6
follow-up issue.
