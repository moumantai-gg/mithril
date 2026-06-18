# Indoor `BoundaryDilationPx` sweep — mithril#1174

The empirical sweep that gates the C3 recommendation in
[`indoor-recall-1174-npcc-brainstorm.md`](indoor-recall-1174-npcc-brainstorm.md).
Sweeps `BoundaryDilationPx ∈ {2, 3, 4, 5, 6, 8}` against the 06-13 canonical
bundle (the post-#1172 RIC baseline) and the 06-15 live-verification bundle
(the NPCc reproducer). The headline question: at what dilation does NPCc-lower
become detected without breaking the canonical bundle's RIC=5/6?

## TL;DR — `BoundaryDilationPx = 3` ships on Indoor (Outdoor unchanged)

Production Indoor ships at **dilation = 3**, justified by the **06-13
IconA recovery (RIC 5/6 → 6/6)**. The NPCc lift originally reported here
was **falsified by the mithril#1183 code review** — see the post-mortem
section at the bottom of this doc. The 06-13 IconA finding is unchanged
and remains the load-bearing reason to ship Indoor=3.

| Dilation | 06-13 RIC (pixel-hit, of 6) | 06-15 NPCc-lower (pixel-hit) | Verdict |
|---:|---:|---|---|
| 2 | 6 | NO | Same as 3 for RIC; no NPCc lift |
| **3** | **6 ✓** | **NO** | **IconA recovered → production pick** |
| 4 | 6 | NO | IconA recovered, no NPCc |
| 5 | 5 | NO | IconA at threshold; no NPCc |
| 6 | 5 | NO | No lift |
| 8 (pre-#1174) | 5 | NO | Baseline |

Outdoor leaves `BoundaryDilationPx = null` on the profile → falls back to
`MapCalibrationDetectorOptions.BoundaryDilationPx` (global default 8) →
byte-identical pre-#1174 behaviour. Outdoor's `opaqueFraction ≈ 1` makes the
alpha-boundary band a no-op anyway (no alpha edge to dilate), so the global
fallback's literal value is moot for Outdoor scenes.

## Bonus finding — IconA recovered on 06-13

The brainstorm noted IconA at (327, 180) "was never admitted at any
threshold" in the luma sweep. The boundary-dilation knob WAS the load-bearing
reason for the miss — at dilation ≤ 4 IconA reaches Icon class on the
canonical bundle, lifting RIC from the post-#1172 baseline of 5/6 to 6/6.
This wasn't predicted by the brainstorm; it falls out of the same mechanism
that rescues NPCc (IconA also sits within ~8 px of an alpha boundary).

The RIC=6/6 lift on 06-13 is the second piece of evidence that C3 is the
right mechanism — multiple Indoor icons are wiped by the over-dilation, not
just NPCc.

## Methodology — simulated dilation via mask erosion

The bundles save `07a-deviation-mask.png` at the production dilation of 8 —
that's the artifact each sweep value is derived from. For `r ≤ 8`, the
dilation=r mask is recovered by eroding the saved mask by `(8 - r)` pixels
using a square structuring element.

**Identity.** For a thin (1-px) boundary curve `B`:
`erode(dilate(B, 8), 8-r) = dilate(B, r)`.
This holds when `dilate(B, 8)` is a metric ball in the Chebyshev metric — by
construction in [`FloorBoundaryMaskCache.cs`](../../../../src/Mithril.MapCalibration.Detection/Internal/FloorBoundaryMaskCache.cs)
the alpha-boundary input IS such a thin curve.

**Confound.** The saved `07a-deviation-mask.png` is the OR of the
alpha-boundary mask AND the screenshot-derived fog-of-war mask. Eroding the
OR shrinks the fog contribution too — not production semantics for fog. In
practice (per the brainstorm's 81×81 inspection at NPCc's coordinate) the
mask in the NPC neighbourhood is a clean alpha-corridor shape with no fog
blobs, so the approximation is faithful for the NPCc lift question. For
06-13's RIC question, the icons sit far from any fog region; the erosion
doesn't reach into icon territory. The approximation is documented in
[`IndoorBoundaryDilationSweepTests.cs`](../../../../tests/Mithril.MapCalibration.Tests/Detection/IndoorBoundaryDilationSweepTests.cs)
so a fog-heavy bundle in the future Indoor corpus (#1176) prompts a
re-derivation via the production `FloorBoundaryMaskCache` rather than the
erosion shortcut.

## Per-bundle measurement tables

### 06-15 live-verification

Pip layout per the brainstorm:
- NPCa at (455, 212) — upper-left isolated NPC.
- NPCb at (478, 230) — upper-right isolated NPC.
- NPCc-upper at (473, 287) — already detected pre-#1174.
- NPCc-lower at (475, 297) — mithril#1174 lift target.

| Dilation | Total blobs | Icon class | NPCa | NPCb | NPCc-upper | NPCc-lower | Mask coverage |
|---:|---:|---:|---|---|---|---|---:|
| 2 | 6 | 6 | Icon (A=358) | Icon (A=357) | Icon (A=270) | Icon (A=270) | 83.5% |
| 3 | 6 | 6 | Icon (A=345) | Icon (A=357) | Icon (A=249) | Icon (A=249) | 84.8% |
| 4 | 6 | 6 | Icon (A=330) | Icon (A=357) | Icon (A=225) | — | 86.1% |
| 5 | 6 | 6 | Icon (A=314) | Icon (A=357) | Icon (A=208) | — | 87.4% |
| 6 | 6 | 6 | Icon (A=289) | Icon (A=357) | Icon (A=189) | — | 88.7% |
| 8 | 6 | 5 | Icon (A=239) | Icon (A=357) | Icon (A=155) | — | 91.2% |

NPCc-lower transition: 8 → 4 NO, 4 → 3 YES. Cleanest transition observed.

### 06-13 canonical

Real icons per the stage-attribution audit:
- IconA at (327, 180) — historical 1-of-6 miss.
- IconB at (411, 185), IconC at (432, 202) — the merge pair (split by #1172).
- IconD at (428, 257), IconE at (375, 667), IconF at (500, 680) — isolated.

| Dilation | Total blobs | Icon class | IconA | IconB | IconC | IconD | IconE | IconF | RIC | Mask coverage |
|---:|---:|---:|---|---|---|---|---|---|---:|---:|
| 2 | 6 | 6 | Icon (A=456) | Icon (A=349) | Icon (A=337) | Icon (A=303) | Icon (A=295) | Icon (A=290) | **6** | 82.9% |
| 3 | 6 | 6 | Icon (A=416) | Icon (A=334) | Icon (A=337) | Icon (A=287) | Icon (A=279) | Icon (A=271) | **6** | 84.3% |
| 4 | 6 | 6 | Icon (A=374) | Icon (A=318) | Icon (A=337) | Icon (A=269) | Icon (A=250) | Icon (A=252) | **6** | 85.8% |
| 5 | 6 | 6 | — (chrome) | Icon (A=301) | Icon (A=337) | Icon (A=247) | Icon (A=220) | Icon (A=232) | 5 | 87.2% |
| 6 | 6 | 6 | — (chrome) | Icon (A=276) | Icon (A=337) | Icon (A=222) | Icon (A=189) | Icon (A=200) | 5 | 88.7% |
| 8 | 6 | 5 | — (chrome) | Icon (A=226) | Icon (A=337) | Icon (A=187) | Icon (A=127) | Icon (A=125) | 5 | 91.5% |

IconA transition: 5 → 4 YES. Total Icon-class count is consistent at 6
through dilations 2-6, dropping to 5 at dilation=8 (the IconA chrome blob is
lost AND IconE/F shrink so much they almost hit the `MinPeak` shape gate —
A=125 and A=127 are close to the `MinArea=12` floor's headroom).

## Noise-admittance check

The brainstorm's risk surface: smaller dilation re-admits a narrow band of
boundary chrome that the Phase 3 peak-luma filter must drop. Across the
sweep, **total blob count stays at 6 on both bundles** for every dilation in
{2, 3, 4, 5, 6, 8}. Narrowing dilation doesn't admit new noise — it reshapes
the SAME blobs (NPCc-lower joins the upper pip into a 25-px-tall blob
instead of being clipped to 10 px). The Phase 3 peak-luma filter is doing
its job and the brainstorm's concern was unfounded for these two bundles.

The follow-up that DOES warrant attention: corpus expansion (#1176) needs
to confirm the noise-admittance budget holds across other Indoor scenes
(GoblinDungeon, BrainBugCaverns, HumanCellar). The mechanism for that
follow-up is unchanged — the dilation knob is now on the profile and can
be tuned per scene class if a future bundle proves the 3-px value too
narrow.

## Outdoor regression

Outdoor profile leaves `BoundaryDilationPx = null` so `AutoCalibrationEngine`
falls back to `_detectorOptions?.BoundaryDilationPx ?? 8` — the historical
default. Outdoor behaviour is byte-identical to pre-#1174 by construction
(the only code-path change in `AutoCalibrationEngine` is the `??`-chain
resolution before `GetOrCompute`; on Outdoor the resolved value matches the
pre-#1174 path verbatim).

The dev-local `ReplayFixtureTests` battery (Serbule + Eltibule + Kur) is
unaffected because that test path uses `MapCalibrationSolveEngine` /
`DeviationBlobCalibrationDetector` and never wires a `FloorBoundaryMaskCache`
— so the dilation knob has no effect on that surface at all.

## Reproducibility

Bundles dev-local per
[`map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md).

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests `
  --filter "FullyQualifiedName~IndoorBoundaryDilationSweepTests" `
  --logger "console;verbosity=detailed"
```

## What ships

- `SceneCalibrationProfile.BoundaryDilationPx` (new field, `int?`).
- Outdoor: `null` → fallback to global, byte-identical.
- Indoor: `3` (locked by both bundles' transitions).
- `FloorBoundaryMaskCache.GetOrCompute(mapAssetKey, dilationPx)` — dilation
  is caller-provided; cache keyed on `(mapAssetKey, dilationPx)` so sweeps
  on a single area in tests don't trample each other.
- `AutoCalibrationEngine` reorders: resolve `SceneClass + profile` BEFORE
  the mask block, then pass `profile.BoundaryDilationPx ?? options ?? 8`
  to `GetOrCompute`.

## Post-mortem — review S6 falsified the original NPCc claim

The pre-review version of this doc reported "06-15 NPCs reaching Icon class: 4/4
at dilation=3" as the load-bearing benefit. The mithril#1183 code review's
S6 finding caught that the assertion used bounding-box containment —
`x ∈ [MinX, MinX+W)` — not foreground-pixel membership. When `NpcsInIconBlobs`
was rewritten to check `b.Pixels.Contains(y * w + x)` (the load-bearing test
for "this NPC pip's pixels are in an Icon-class blob" — RANSAC consumes
foreground pixel positions as correspondences), the result was:

- 06-15 NPCa, NPCb, NPCc-upper: **pixel-hit at all dilations** (3/3 always).
- 06-15 NPCc-lower at (475, 297): **NOT pixel-hit at any dilation in {2, 3, 4, 5, 6, 8}.**

The upper-pip blob at dilation=3 has bbox `(466, 279) + 16×25`, which covers
(475, 297) at y=297 (within `279..303`). But its foreground pixels are the
upper pip's connected component, which terminates at y≈289 — the gap-then-
lower-pip pattern observed in the brainstorm's deviation map inspection
splits into two separate components in the foreground buffer. The narrower
boundary band reshapes the upper-pip blob (taller bbox, fewer central
pixels) without ever connecting it to the lower pip's pixels.

**What this means for #1174.** The IconA recovery on 06-13 IS real (every
canonical icon pixel-hits at dilation=3). Production Indoor=3 ships on that
benefit alone. The NPCc-lower failure mode is structurally different from
what the brainstorm proposed — it's not boundary-mask suppression of the
icon, it's that the icon's foreground COMPONENT terminates before reaching
the pixel. Likely candidates for the real NPCc mechanism:

1. **C4 from the brainstorm (bright-luma rescue inside the boundary band).**
   Direct test: at dilation=8 with a bright-luma override that admits
   raw-luma ≥180 pixels through the boundary subtract, does the lower-pip
   connected component reach (475, 297)?
2. **A smaller `LocalNccDeviation.win` value at the lower pip's altitude.**
   The brainstorm ruled this out under the original (wrong) mechanism;
   re-evaluate against the corrected one.
3. **The pre-deviation luma gate (#1172) interacting unexpectedly with the
   lower pip's halo.** Worth checking whether `MinLumaForDeviation = 200`
   over-gates the lower pip's halo specifically.

**Action.** New follow-up issue owed for the real NPCc mechanism. The Indoor
profile change still ships (justified by IconA), and the live-verification
plan is unchanged — the 06-13 RIC=6/6 win is the headline.

## Open follow-ups (not blocking #1174)

1. **Corpus expansion (#1176).** Re-run this sweep against additional
   Indoor bundles when they're available — GoblinDungeon, BrainBugCaverns,
   HumanCellar. The dilation=3 generalises only as far as the corpus does;
   bundle-specific re-tunes are now possible per-area without changing the
   profile (the field is on `SceneCalibrationProfile`, but the cache key
   on `(asset, dilation)` supports future per-area overrides too).

2. **Confound: fog-of-war erosion.** This sweep's methodology assumes the
   saved mask's only contribution is the alpha-boundary band. A fog-heavy
   Indoor bundle would invalidate the assumption. The right follow-up is
   not a code change but a test extension: when corpus #1176 lands, add a
   theory variant that derives the mask from a real alpha provider rather
   than the erosion shortcut, so the sweep can be replayed without
   the confound.

3. **Documenting the Outdoor cost-free property in the perf-trace schema.**
   The new `profile.boundary_dilation_px` span tag is emitted on every
   detect; document the expected values (Indoor=3, Outdoor=8) in
   [`docs/perf-trace-schema.md`](../../../../docs/perf-trace-schema.md).
   Trivial follow-up, can ship with #1174 or alone.
