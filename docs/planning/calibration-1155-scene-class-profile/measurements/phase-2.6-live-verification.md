# Phase 2.6 live verification — mithril#1172

Placeholder for the live in-game verification of the pre-deviation luma
threshold knob. The static-bundle measurements
([`indoor-pre-deviation-luma-distribution.md`](indoor-pre-deviation-luma-distribution.md)
+ [`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
confirm the mechanism + the threshold value works on the existing 06-13
canonical and 06-15 live-verification bundles. This doc captures the
headline live-acceptance result.

## Expected result (post-#1172 ship)

Arthur runs Mithril from `feat/calibration-1172-pre-deviation-luma`,
captures a fresh auto-cal attempt in Hogan's Keep Basement (same scene
as the 06-13 and 06-15 bundles), and the new bundle should show:

- **`01-attempt.json` `profile.minLumaForDeviation` = 200** — the
  resolved Indoor profile value surfaces in the bundle.
- **`01-attempt.json` `profile.sceneClass` = "Indoor"** — alpha-coverage
  classifier resolves correctly (unchanged from Phase 3).
- **`10c-blob-pipeline.json`** — two distinct Icon-class blobs at the
  NPC pip positions previously merged into a single Structure blob. The
  exact aligned-coordinates depend on the new capture's framing (the
  06-13 and 06-15 bundles had different in-game zoom and player
  positions); a triager can verify the split visually against the
  bundle's `07e-blob-classification.png` PNG.
- **`10-detections.json`** — 4 or more typed detections (2 NPC pips
  newly typed individually + the previously-detected NPC + 2 portals).
- **`01-attempt.json` `outcome: "accepted"`** — the headline win for
  #1155 if the typed detections land in geometric agreement with the
  reference landmarks. The 4-inlier RANSAC gate becomes reachable for
  the first time on an Indoor scene.

## How to capture

1. Pull `feat/calibration-1172-pre-deviation-luma` and rebuild Mithril.
2. Launch Project Gorgon, enter Hogan's Keep Basement.
3. Open the in-game map and zoom out fully.
4. Use the draw-map-bbox hotkey to set the map capture region.
5. Use the auto-cal hotkey to trigger an attempt.
6. The bundle lands at
   `%LOCALAPPDATA%/Mithril/diagnostics/calibration/Map_HogansKeepBasement-<ts>-<outcome>/`.

## How to update this doc

After the live capture lands, update the bundle path placeholder below
with the actual `Map_HogansKeepBasement-<timestamp>-<outcome>` directory
name and inline the headline values from `01-attempt.json`.

```text
Bundle: Map_HogansKeepBasement-<TBD>-<outcome>

01-attempt.json (headline fields):
  outcome:                <TBD — expected "accepted">
  sceneClass:             Indoor
  sceneClassOpaqueFraction: <TBD — expected 0.07-0.36>
  profile.minLumaForDeviation: 200
  profile.minPeakLuma:    0.7
  profile.maxAspect:      2.7
  profile.minSolidity:    0.30

10-detections.json:
  Number of detections: <TBD — expected ≥ 4>
  Types: [<TBD>]

10c-blob-pipeline.json:
  Total blobs:        <TBD>
  Icon-class blobs:   <TBD>
  Structure-class:    <TBD>
```

## Fallback — if `outcome != "accepted"`

If the bundle still rejects despite the merge being split into two
Icon-class blobs (per `10c`), the reject reason from `01-attempt.json`
tells us which downstream stage is the new bottleneck:

- **`only N inliers (need >=4)` with `N=3`** — the merge split but
  RANSAC didn't find geometric agreement among 4+ detections. Possible
  causes: NPC pip mis-typing by the per-blob NCC step, or one of the
  newly-admitted blobs being a real-but-not-in-reference-data entity.
  Investigate by reading `10b-blob-template-scores.json` for the
  per-template scores at each newly-admitted blob.
- **`only N inliers (need >=4)` with `N>=4` but residual > 12 px** —
  RANSAC found 4+ correspondences but the residual exceeds the legacy
  gate. The fit is geometrically over-determined; investigate whether
  the scale ladder picked the right scale. Possibly a Phase 5 synthesis-J
  Shadow-mode follow-up; not in #1172 scope.
- **Some other reject reason** — escalate as a Phase 2.6 follow-up
  issue.

In any of these cases, the static measurements still establish that the
gate produced the expected merge split — the live-verification fallback
clarifies *which downstream bottleneck* is the next problem.

## Static-measurement evidence backing the expected result

The static-bundle threshold-sweep measurement
([`indoor-pre-deviation-luma-threshold.md`](indoor-pre-deviation-luma-threshold.md))
established that at `MinLumaForDeviation = 200` with production
`closeRadius = 1`:

| Bundle | B+C (or a+b) split? | RIC after gate |
|---|---|---|
| 06-13 canonical | YES (Icon/Icon) | 5/6 (was 3/6) |
| 06-15 live | YES (Icon/Icon) | 2/3 NPCs reach Icon (was 0/3) |

The mechanism cleanly reproduces across the two existing live bundles;
the cross-bundle generalisation hypothesis is that a fresh Hogan's
capture will reproduce the same pattern. The live verification confirms
in-game behaviour matches the static reproduction.
