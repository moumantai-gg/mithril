# Phase 2.6 live verification — mithril#1172

Live in-game verification slot for the pre-deviation luma threshold.

**Status (2026-06-16):** PR #1173 merged to `main` (squash commit
`f9d7d4a6`). Awaiting Arthur's in-game capture from a build off `main`;
this file gets the bundle name + JSON values filled in below after capture.
#1172 stays open until then; #1155 stays open until #1172 closes.

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

1. Pull `main` (post-PR #1173) and rebuild.
2. Capture a fresh attempt in Hogan's Keep Basement.
3. The bundle lands at
   `%LOCALAPPDATA%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-<ts>-<outcome>/`.
4. Replace the "To populate" block below with the bundle name + the
   headline JSON fields.

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
