# §6.d — Indoor adaptive synthesis-J threshold

**Verdict: PARTIAL.** Outdoor `j` (16-23) vastly exceeds Indoor (3-4); static `jMin = 8` is correct for Outdoor and unreachable for Indoor.

**Blocking limitation:** the corpus contains **zero** ground-truth-good Indoor cals. The only Indoor "accept" we have is the historical Hogan's 06-10 cal that the spec hypothesizes is wrong (cross-scene leak per #1116 H1, residual 6.45 px). We can't derive a separating formula from rejected-only data.

**Recommendation:** Phase 4 ships with Indoor in `Shadow` mode for v1. Revisit once Phases 1-3 produce Indoor cals that can be ground-truthed (e.g., by spot-checking projection accuracy on known landmarks).

## Method

For every bundle under `%LocalAppData%/Mithril/diagnostics/calibration/Map_*`, read `01-attempt.json` and collect `synthesis.{j, refsAboveHalf, refsTotal, verdict, gateVerdict, disagree}`.

## Measurements

| Scene | Engine | Outcome | `j` | refsAbove | refsTotal | Synth verdict | Legacy verdict | Disagree |
|---|---|---|---|---|---|---|---|---|
| AreaEltibule (accept) | 3.0.0.81 | accepted | **16.53** | 15 | 38 | accept | accept | — |
| AreaSerbule (accept) | 3.0.0.81 | accepted | **23.45** | 24 | 46 | accept | accept | — |
| HogansKeepBasement (accept 06-10) | 3.0.0.81 | accepted | 3.25 | 5 | 11 | **reject** | accept | **accept→reject** |
| HogansKeepBasement (reject 06-12) | 3.0.0.88 | rej-insufficient-inliers | 3.85 | 5 | 11 | reject | reject | — |
| HogansKeepBasement (reject 06-13) | 3.0.0.91 | rej-insufficient-inliers | 3.01 | 3 | 11 | reject | reject | — |
| GoblinDungeon_TopFloor (reject) | 3.0.0.82 | rej-insufficient-inliers | 3.18 | 4 | 9 | reject | reject | — |
| (8 other Hogan's bundles) | various | rej-solve / rej-map-not-located | n/a | n/a | n/a | no_winner | reject | — |

## Findings

1. **Outdoor `j` ≫ Indoor `j`.** Outdoor cals score `j ∈ [16, 23]`; Indoor `j ∈ [3.0, 3.9]`. The gap is structural, not bundle-noise — for Indoor `jMin = 8` is unreachable, period.

2. **The one Indoor "accept" is the disputed cal.** The Hogan's 06-10 accept (`j = 3.25, gateVerdict = accept, synthVerdict = reject, disagree = accept→reject`) is exactly the cal the spec calls "structurally suspect" — it accepted at residual 6.45 px on 4 inliers, and the cross-scene leak hypothesis from #1116 strongly suggests it's a wrong cal. Synthesis-J would have rejected it. We don't know if `j = 3.25` represents "below-threshold-but-correct cal" or "below-threshold-and-correctly-rejected cal."

3. **All other Indoor bundles rejected by both gates.** No data point exists for "Indoor cal that's both legacy-accepted AND synthesis-J-accepted."

4. **Without ground-truth-good Indoor cals, no separating formula is derivable.** A formula like `jMin = 0.6 × refsTotal` (Hogan's → `jMin = 6.6`) rejects ALL current Indoor samples. A formula like `jMin = 0.25 × refsTotal` (Hogan's → `jMin = 2.75`) accepts ALL current Indoor samples, including ones that are likely wrong cals.

## Recommendation

**Phase 4 ships with `Indoor.SynthesisMode = Shadow` for v1.** This means:

- Outdoor profile: `Enforced` mode (or stays `Shadow` — whatever the existing default is post-#1117; spec says Shadow), static `jMin = 8 / nMin = 8`. No change.
- Indoor profile: `Shadow` mode — synthesis-J `j` is computed and logged in the bundle, but doesn't gate accept/reject. Legacy gate stays in charge.
- Phase 2 + Phase 3 produce Indoor cals (hopefully better than the disputed 06-10 cal).
- Once we have one or more known-good Indoor cals (manual verification by spot-checking landmark projection accuracy), re-derive the formula and re-spec Phase 4-v2 to enforce.

## Implication for spec

Spec §5.1 Indoor profile — `SynthesisMode = Enforced` → `SynthesisMode = Shadow` for v1. The `SynthesisJMinFn` / `SynthesisNMinFn` still land on the carrier for observability, but they don't drive accept/reject yet.

Spec §6.d — restate the verification as a deferred follow-up: "After Phases 1-3, collect Indoor accept candidates, manually validate, derive `jMin/nMin` formula, ship Phase 4-v2 with `Enforced` mode."

Plan Phase 4 — re-scope to "Indoor synthesis-J observability" (Shadow + bundle diagnostic + log mirror). The enforcement flip becomes a Phase 4-v2 follow-up issue.

## Outdoor sample size caveat

`n=2` Outdoor accepts (Serbule + Eltibule) is small. Both score `j` well above 8 — no signal to revise outdoor formula. KurMountains hasn't been auto-cal'd in the diagnostic corpus; if a future regression is observed, this measurement page should be updated.
