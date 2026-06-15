# Phase 3 live verification — Hogan's Keep Basement (post-#1169)

**Verdict: PHASE 3 MECHANISM VERIFIED. CALIBRATION STILL REJECTED.** Phase 3 fires correctly end-to-end on a fresh in-game capture, drops 93 % of floor-noise blobs at the 0° orientation, and leaves three semantically-real detections. RANSAC finds 3 inliers; the gate is ≥ 4. The remaining 1-inlier gap is the recall ceiling identified in the Phase 2 audit — `IconB + IconC` merge plus `IconA` bbox/centroid mismatch — that the plan's **Phase 2.5 (morph-open)** is scoped to address.

> **Scope of this verification (review #1170-r2 finding #2).** This doc captures the LIVE in-game state at engine `3.0.0.93+98fdd54bef` — the post-#1169 squash merge, which is also the base of PR #1170. The peak-luma mechanism (Phase 3) IS field-verified by this doc. The PR #1170 log-format changes (`(rotate180={Rotate180})` template prefix on the kept/dropped/rejected-all Trace lines, the 180°-pass Trace demotion, the per-orientation tagging on the sibling Deviation/RimMask/DeviationMask/Morph/BlobClassification Trace lines) are NOT visible in the log excerpts quoted below — those quote the pre-#1170 log format. The #1170 changes are pinned by the test battery (`PeakLumaFilterTests`), not by a fresh live run. A future re-verification on engine `3.0.0.94+` (post-#1170 merge) would close that gap; for the immediate "stop false-positive 180° Warning" goal, the test-side pin is sufficient.

## Setup

| | |
|---|---|
| Engine version | `3.0.0.93+98fdd54bef` (post-#1169 squash merge `98fdd54b`) |
| Test bundle | `Map_HogansKeepBasement-20260615-012510-030-rejected-solve-insufficient-inliers` |
| Scene | Hogan's Keep Basement (`Map_HogansKeepBasement` / `AreaCave1`) |
| Capture time | 2026-06-15 01:25:10 UTC |
| Sibling Outdoor check | `Map_AreaEltibule` drift check 01:25:45 UTC |

## Phase 3 mechanism — end-to-end verification

### Bundle JSON carries `MinPeakLuma` (review #1169 finding #3)

`01-attempt.json` `profile` block (verbatim):

```json
"profile": {
  "minArea": 12,
  "maxIconArea": 900,
  "minSolidity": 0.3,
  "maxAspect": 2.7,
  "minPeak": 0.7,
  "minPeakLuma": 0.7
}
```

`sceneClass: "Indoor"` with `sceneClassOpaqueFraction: 0.175`. The added `minPeakLuma` field surfaces in production exactly as the post-review fix intended — a triager reading the bundle alone can now tell that Phase 3 was active for this attempt.

### Filter fires on the 0° pass — drops 92.7 % of Icon-class blobs

From `mithril-20260615.json` at the attempt's trace id (`2039821bdfedebfaeb39e02a8aeca7ae`):

```
{Category}=Mithril.MapCalibration.Detection
{Message}=PeakLumaFilter: kept 3/41 Icon-class blobs (threshold 0.70, dropped 38).
```

That's the per-orientation 0° pass. Out of 41 classified Icon-class blobs (post-T1+T2 relaxed gates), 38 were dropped as floor-noise and 3 survived to the per-template-NCC typing step.

`10c-blob-pipeline.json` breakdown by orientation:

| Orientation | Total blobs | Icon | Noise | Fog | Structure |
|---|---:|---:|---:|---:|---:|
| `rotate180=False` (0°) | 250 | 41 | 203 | 2 | 4 |
| `rotate180=True` (180°) | 126 | 40 | 66 | 0 | 20 |

> Review #1170-r2 finding #14: PR #1170 emits the .NET-default `bool.ToString()` form (`True` / `False`) for the `{Rotate180}` MEL template. The table headers above were originally written in C-style lowercase; corrected so a triager copy-pasting the table value to grep the log gets a match.

The 81 Icon-class total across orientations matches the per-bundle log lines (`kept 3/41` at 0° + `rejected ALL 40` at 180°).

### Engine dispatches Indoor `MinPeakLuma=0.7` (review #1169 finding #7 wiring)

```
{Category}=Mithril.MapCalibration.Capture.Engine
{Message}=Auto-calibration AreaCave1: scene class Indoor (opaqueFraction=0.1749134063720703);
BlobOptions = BlobOptions { MinArea = 12, MaxIconArea = 900, MinSolidity = 0.3, MaxAspect = 2.7,
MinPeak = 0.7, MinPeakLuma = 0.7 }.
```

The engine-layer dispatch trace shows the full BlobOptions including the new field. The Outdoor sibling (next section) shows `MinPeakLuma = ` (empty) for the byte-identical case.

## Final detections

`10-detections.json` after per-template NCC + spatial dedup:

| Type | Score | Anchor (x, y) | Notes |
|---|---:|---|---|
| `Npc` | 0.917 | (473, 292) | High-confidence NPC pip, upper-middle |
| `Portal` | 0.865 | (412, 737) | Portal/transition, lower |
| `Portal` | 0.870 | (550, 751) | Portal/transition, lower |

All three detections look semantically real — NPC pip at clearly-visible head-and-shoulder glyph position, two portals at the basement exits. Scores are clean (0.86 – 0.92) rather than the noise-ceiling 0.70 – 0.80 the canonical 06-13 bundle produced.

`rejectReason: "only 3 inliers (need >= 4)"` — RANSAC found all 3 detections fit a consistent transform, but the engine's 4-inlier gate didn't accept.

## Comparison to canonical 06-13 bundle

| Metric | Canonical 06-13 (pre-#1163) | Live 06-15 (post-#1169) | Δ |
|---|---|---|---|
| Engine version | `3.0.0.91+304a3d9` (pre-Phase-1/2/3) | `3.0.0.93+98fdd54b` (post-Phase-3) | +2 phases |
| Total Icon-class blobs | 18 (0°) | 41 (0°) | +23 — relaxed T1+T2 admits more |
| Real-icon blobs admitted to Icon-class | 1 (IconF only) | 3 (IconD + IconE + IconF) | +2 (Phase 2) |
| Floor-noise blobs admitted | 17 | 38 | +21 — but Phase 3 catches them |
| Phase 3 filter survivors | n/a | 3 of 41 | (the 3 real-icon blobs) |
| Final typed detections | 2 (both `Portal`, both noise) | **3 (1 `Npc` + 2 `Portal`, all real)** | +1, all real |
| RANSAC inliers | 2 | **3** | **+1** |
| Calibration accepts? | No (`only 2 inliers`) | No (`only 3 inliers`) | Still no — 1 short |

**Detection quality is now genuinely real** rather than the floor-vs-template false-positive pattern the spec §2.1 documented for the canonical bundle. The remaining 3 → 4 gap is the recall ceiling, not a precision problem.

## Outdoor regression — byte-identical confirmed

The same in-game session triggered Eltibule drift checks (the engine's per-second drift cadence on a stored cal). From the log:

```
{Category}=Mithril.MapCalibration.Capture.Engine
{Message}=Drift check Map_AreaEltibule: scene class Outdoor (cache_wired=True);
BlobOptions = BlobOptions { MinArea = 12, MaxIconArea = 900, MinSolidity = 0.35, MaxAspect = 2.5,
MinPeak = 0.7, MinPeakLuma =  }.

{Message}=Drift check Map_AreaEltibule: OK (10 refs matched, max residual 1.36px, threshold 1.95px).
No recalibration needed.
```

`MinPeakLuma = ` (empty / null) on Outdoor dispatch ✓. 10 references matched the stored Eltibule cal at 1.36 px residual, well under the 1.95 px threshold — the existing pre-#1169 cal is still valid. No fresh capture was triggered; the Outdoor byte-identical invariant holds in production.

## One follow-up identified by the live run

The post-review **100%-drop LogWarning** I added in DeviationBlobDetector fires on the 180° orientation pass in addition to the 0° pass. Excerpt from the log:

```
{@l}=Warning
{Message}=PeakLumaFilter: rejected ALL 40 Icon-class blobs (threshold 0.70). Indoor calibration
will fail downstream with 'no detections'; check for upstream BGRA-dim drift, an unexpectedly dim
capture, or a misaligned crop.
```

The 180° pass legitimately produces zero survivors on non-mirrored scenes (the texture orientation doesn't match the screenshot), and the 0° pass produced 3 real detections that the engine used. So the Warning's "Indoor calibration will fail downstream" framing is structurally wrong at the per-orientation level — it should fire only when the union of both orientations produced no detections, or be tagged so triagers can filter out the expected 180° fail. Fix in the same PR as this doc.

## Headline finding — what's needed to ACCEPT

We are **1 inlier short** of the 4-inlier gate. Two paths:

1. **Phase 2.5 morph-open** (audit-recommended). The `indoor-recall-stage-attribution.md` audit identified the `IconB + IconC` merge into a single 1242-area Structure blob as the load-bearing failure mode beyond T1+T2. The follow-up audit (`indoor-recall-merge-fix-candidates.md`) ruled out deviation-window narrowing and morph-close-radius zeroing as solutions for the merge, and named **morph-open before morph-close** as the Phase 2.5 candidate. Splitting B+C would lift RIC from 3/6 to 5/6 and clear the gate.

2. **Lower the Indoor inlier gate from 4 to 3** (smaller scope, higher risk). 3 high-NCC detections (0.85+) on geometrically-consistent positions IS a valid similarity-transform fit, but the 4-inlier gate is what defends against collinear-noise edge cases producing wrong cals that then ship via `refinements.json`. Mechanically simpler but the Indoor "accept" set grows in ways that may surface in non-Hogan's scenes.

Phase 2.5 fixes the cause; gate-lowering treats the symptom. Recommend Phase 2.5 next.

## Files referenced

- `01-attempt.json` — bundle metadata + outcome + profile
- `10-detections.json` — final 3 typed detections
- `10c-blob-pipeline.json` — per-orientation blob classifications (250 + 126)
- `mithril-20260615.json` — engine + detector logs at trace id `2039821bdfedebfaeb39e02a8aeca7ae`
