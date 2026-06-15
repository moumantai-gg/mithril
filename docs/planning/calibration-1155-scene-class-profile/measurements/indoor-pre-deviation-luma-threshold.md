# Indoor pre-deviation luma threshold sweep — mithril#1172 Phase 2.6

The load-bearing pick measurement for the
[`#1172`](https://github.com/moumantai-gg/mithril/issues/1172) pre-deviation
luma threshold. Companion to
[`indoor-pre-deviation-luma-distribution.md`](indoor-pre-deviation-luma-distribution.md)
which confirmed the bimodal mechanism. This doc determines the actual
threshold value to ship on the Indoor profile.

## TL;DR — `MinLumaForDeviation = 200` ships on Indoor

With production `closeRadius = 1` and the screenshot-only zeroing
implementation (see Finding 1 below), threshold `200` is the unique value in
the swept range that splits BOTH canonical bundles' merged NPC pair into TWO
Icon-class blobs AND lifts Real-Icon-Class (RIC) recall from the Phase 3
baseline of 3/6 to **5/6 on 06-13** (IconB and IconC newly reach Icon-class)
and from 0/3 to **2/3 on 06-15** (NPCa and NPCb newly reach Icon-class).
The third 06-15 NPC (middle-right at (473, 291)) is undetected at every
threshold — that pip is too faint regardless and likely needs a different
mechanism.

Lower thresholds (140, 160, 180) split 06-15 but the production morph-close
at radius 1 re-bridges the more aggressively-gated 06-13 halos back into one
merged Icon-class blob (still classified Icon but counted as one
correspondence, not two — failing the split criterion). Higher thresholds
would over-gate the icon cores themselves; the sweep stopped at 200 because
the Phase 3 corpus measurement showed icon glyph peak luma reaches > 220 in
every bundle, so 200 leaves ~20+ luma-byte headroom above the gate before
the icon's own brightest pixels would start dropping.

## Implementation note — screenshot-only zeroing

The issue body specified "AND-gate both screenshot and base-texture
buffers". The literal implementation (zero floor pixels on BOTH sides
before the integral image) **collapsed real-icon recall to 0/6 at every
non-zero threshold** on both bundles. Mechanism: zeroing floor pixels on
both `a` and `b` aligns the non-zero spatial pattern across the two
buffers at icon positions, so covariance spikes proportionally to the
icon's bright signal — `cov / sqrt(va * vb) ≈ 1` and the addedOnly
deviation signal disappears.

The working implementation zeros ONLY the screenshot buffer (`a`). The
texture buffer (`b`) stays untouched. At floor windows where `a` is now
mostly-zero but `b` is still textured, the existing OBSCURED branch fires
(`va < flatVar && vb ≥ flatVar` with `addedOnly = true` → `ncc = 1`) and
correctly emits zero deviation. At icon windows, the bright icon spike in
`a` (with surrounding floor zeroed) has high `va`; the untouched texture
in `b` has its normal variance; the covariance between "bright spike in a
sea of zeros" and "smooth texture" is low → high deviation → icon
detected.

The asymmetric implementation matches the issue's STATED intent ("only
treat pixels with screenshot luma > threshold as deviation candidates")
even though it disagrees with the body's "AND-gate both" wording.
[`LocalNccDeviation.cs`](../../../../src/Mithril.MapCalibration.Detection/LocalNccDeviation.cs)
documents this in the parameter's XML doc.

## Measurement table — 06-13 canonical

`win=11` deviation kernel. Indoor profile T1+T2 shape gates (MaxAspect 2.7,
MinSolidity 0.30); MinPeakLuma post-filter disabled so the verdict reflects
the upstream merge-only outcome.

| minLumaForDeviation | closeRadius | Total blobs | Icon-class | B+C blob area | B+C class | B+C split? | RIC |
|---:|---:|---:|---:|---:|---|---|---:|
| **0** | **1** (prod) | 197 | 20 | 1242 | Structure | NO | **3** |
| 0   | 0 | 351 | 23 | 1056 | Structure | NO | 3 |
| 140 | 0 | 26  | 13 |  896 | Icon (merged) | NO | 5 |
| 140 | 1 | 26  | 13 |  919 | Structure | NO | 3 |
| 160 | 0 |  - | -  |  867 | Icon (merged) | NO | 5 |
| 160 | 1 |  - | -  |  871 | Icon (merged) | NO | 5 |
| 180 | 0 |  - | -  | (B=226, C=337) | Icon / Icon | **YES** ✓ | 5 |
| 180 | 1 |  - | -  |  766 | Icon (merged) | NO | 5 |
| **200** | **1** (prod) | - | - | **(B=226, C=337)** | **Icon / Icon** | **YES ✓** | **5** |
| 200 | 0 |  - | -  | (B=226, C=337) | Icon / Icon | YES ✓ | 5 |

Note: at `minLumaForDeviation ∈ {160, 180}, closeRadius=1`, B+C are
"merged Icon" — they sit in the same connected component but the merged
area is now below `MaxIconArea = 900`, so the algorithm reports the merged
blob as one Icon-class entry. That's still **one** RANSAC correspondence
from two real icons; the split criterion requires two distinct connected
components.

## Measurement table — 06-15 live verification

| minLumaForDeviation | closeRadius | Total blobs | Icon-class | (a)+(b) area | (a)+(b) class | (a)+(b) split? | NPCs reaching Icon |
|---:|---:|---:|---:|---:|---|---|---:|
| **0** | **1** (prod) | 250 | 41 | 1016 | Structure | NO | **0** |
| 0   | 0 | 525 | 63 | 999 | Structure | NO | 0 |
| 140 | 0 | 26  | 7  | (a=347, b=565) | Icon / Icon | **YES** ✓ | 2 |
| 140 | 1 | 26  | 7  | 920 | Structure | NO | 0 |
| 160 | 0 |  - | -  | (a=334, b=549) | Icon / Icon | YES ✓ | 2 |
| 160 | 1 |  - | -  | 890 | Icon (merged) | NO | 2 |
| 180 | 0 |  - | -  | (a=318, b=449) | Icon / Icon | YES ✓ | 2 |
| 180 | 1 |  - | -  | (a=318, b=449) | Icon / Icon | YES ✓ | 2 |
| **200** | **1** (prod) | - | - | **(a=239, b=357)** | **Icon / Icon** | **YES ✓** | **2** |
| 200 | 0 |  - | -  | (a=239, b=357) | Icon / Icon | YES ✓ | 2 |

The 06-15 bundle's NPC-pair spacing is 29 px (vs 27 px on 06-13), so its
merge is structurally weaker — the gate splits it at lower thresholds.
NPCc (middle-right, 473,291) goes undetected at every threshold; that pip's
issue is unrelated to the merge mechanism.

## Sweet-spot summary (production `closeRadius = 1`)

| Threshold | Splits 06-13 B+C? | Splits 06-15 (a)+(b)? | 06-13 RIC | 06-15 Icon count | Acceptable? |
|---:|---|---|---:|---:|---|
| 0 (no gate)   | NO | NO | 3 (baseline) | 0 (baseline) | NO |
| 140 | NO | NO | 3 | 0 | NO |
| 160 | NO | NO | 5 | 2 | partial (06-15 only via NPC class change) |
| 180 | NO | YES | 5 | 2 | NO (06-13 still merged) |
| **200** | **YES** ✓ | **YES** ✓ | **5** ✓ | **2** ✓ | **YES** ✓ |

The only threshold that splits BOTH bundles at production `closeRadius = 1`
is `200`. The simpler shipping path is to keep production `closeRadius`
unchanged on Indoor (no new profile knob) and set `MinLumaForDeviation =
200`.

Reproduced via:

```pwsh
dotnet test tests/Mithril.MapCalibration.Tests `
  --filter "FullyQualifiedName~Measure_pre_deviation_luma_pipeline" `
  --logger "console;verbosity=detailed"
```

## Per-finding analysis

### Finding 1 — Screenshot-only zeroing was the load-bearing fix

The issue's literal "AND-gate both buffers" reading was tried first
(`a[i] = 0; b[i] = 0` at sub-threshold pixels). It produced 0/6 RIC on
the canonical bundle at every non-zero threshold because zeroing both
sides aligns the non-zero pattern → high covariance → low deviation even
at icon spikes. The asymmetric implementation (zero `a` only) preserved
the `addedOnly` branch's OBSCURED path, suppressing floor windows
correctly without destroying icon evidence. See the `LocalNccDeviation.cs`
XML doc on `minLumaForDeviation` for the full mechanism.

### Finding 2 — Phase 2.5 morph-open was the wrong layer

The Phase 2.5 morph-open measurement
([`indoor-recall-phase-2.5-morph-open.md`](indoor-recall-phase-2.5-morph-open.md))
confirmed the bridge isn't a thin filament. Phase 2.6's pre-deviation
gate operates UPSTREAM of the deviation map and severs the bridge BEFORE
the NCC kernel can smear it together. Finding 5 of that measurement
predicted this would be the load-bearing mechanism; the sweep here
confirms.

### Finding 3 — RIC LIFTS from 3/6 to 5/6 — bonus effect

At threshold 200, IconB and IconC (the previously-merged Structure
blob) become two Icon-class blobs both at the real icon centers. RIC
counts them as 2 of the 5/6 admitted. IconD/E/F survive byte-identically.
IconA (327, 180) was never admitted at any threshold — its bbox/centroid
relationship is different (see the
[`indoor-recall-merge-fix-candidates.md`](indoor-recall-merge-fix-candidates.md)
"What 'RIC' counts" appendix).

This is a meaningful improvement on top of the merge fix. Pre-#1172,
Indoor RIC was 3/6; post-#1172, it's 5/6. The same mechanism that splits
the merge also lifts the merged-component-as-Icon classification by
gating the structural cobblestone noise that was inflating B+C's bbox
area above the MaxIconArea ceiling.

### Finding 4 — NPCc (06-15) is unrelated

The third NPC at (473, 291) on 06-15 goes undetected at every threshold.
Its raw screenshot luma values aren't gated out (they're above any
threshold in the sweep), so the gate isn't the cause. The pip's deviation
signal is just below the BlobOptions floor, likely because its position
sits in a denser cobblestone region whose own deviation noise raises the
local floor. Solving NPCc would need a separate mechanism (`LowNcc`
tuning, per-region adaptive threshold, etc.); the #1172 scope is the
merge fix, not catching every visible pip.

### Finding 5 — Cross-bundle generalisation

Both bundles' acceptance regions (the (threshold, closeRadius) pairs that
yield split-both-Icon) overlap precisely at `(200, 1)` — the production
`closeRadius`. The 06-15 bundle accepts a wider threshold range because
its merge is structurally weaker; the 06-13 bundle is the tighter
constraint. As long as a future Indoor bundle's NPC-pair spacing
constraint sits within the 27–29 px range observed here, threshold 200
should generalise. Broader-corpus validation (Phase 4 sweep over other
Indoor scenes once their dev-local bundles exist) is a recommended
follow-up.

## Outdoor regression

Outdoor profile ships `MinLumaForDeviation = 0` (unchanged). The
zero-threshold path inside `LocalNccDeviation.DeviationMap` short-circuits
the pre-scan entirely, so the Outdoor pipeline is byte-identical to
pre-#1172. The Outdoor replay battery (Serbule / Eltibule / Kur) runs in
the post-implementation verification phase as a gate on the PR.

## Reproducibility

Bundles dev-local per
[`map_calibration_replay_fixtures_dev_local`](../../../../C:/Users/arthu/.claude/projects/I--src-project-gorgon/memory/map_calibration_replay_fixtures_dev_local.md).
Both canonical bundles need to be present under
`%LOCALAPPDATA%/Mithril/diagnostics/calibration/` for the theory to emit
data; absent bundles SKIP the corresponding theory rows.
