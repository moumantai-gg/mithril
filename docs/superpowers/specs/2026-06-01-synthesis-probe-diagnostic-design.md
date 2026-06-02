# Synthesis-Probe Diagnostic for Map Auto-Calibration

**Status:** design spec (output of brainstorming, 2026-06-01). Implementation plan to follow in a separate document / GitHub issue.

## Goal

Decide which of two auto-calibration redesigns to build, by measuring whether a continuous **icon-likelihood-field** objective can explain the in-game world-map screenshot well enough to recover the per-area `AreaCalibration` transform.

This spec describes only the **diagnostic tool**, not the production solver. The tool lives in `tools/MapCalibrationFromScreenshot` as a new `--phase synthesis-probe`. Its output is a CSV + a handful of PNGs that answer a single decision question:

> Does `J(T_truth)` dominate `J(T_anything_else)`, and is `J` a sharp peak around `T_truth`?

- If yes everywhere → **Proposal A (cold synthesis solver, no detector)**.
- If yes only when seeded near truth → **Proposal B (hybrid: RANSAC nominates, synthesis re-ranks)**.
- If neither → field/templates/deviation aren't informative enough; neither proposal ships in this form, and the diagnostic told us before we shipped a broken solver.

## Background

The current auto-calibration pipeline (full territory map in `scratch/auto-calibration-handoff.md`) is `detect → threshold → RANSAC → gate`. It fails on zoomed-out captures (the committed Frame 1 fixture) because:

1. Thresholding discards occluded icons — measured 0.3–0.66 when occluded, below the 0.80 floor; isolated they score 0.91–1.00.
2. Discrete correspondences are unreliable in clusters.
3. RANSAC's inlier-count objective is gameable by tightly clustered (spatially small) fits — we have a documented 6-inlier / 5.29 px / 63×26 px-span "garbage" solve that the gate accepted.

The problem is heavily over-constrained but solved with an under-constrained method. Unused priors:

- The 38 reference world positions for the area are known.
- The transform has only ~3 continuous DOF (`Scale`, `OriginX`, `OriginY`) plus discrete `{rotation ∈ {0, π}, MirrorNorth ∈ {0, 1}}`.
- Post-#978 ECC registration makes the screenshot sub-pixel-aligned to the base texture.

The continuous-evidence objective **`J(T) = Σᵣ L_{type(r)}(T · r)`** consumes all three priors at once. This diagnostic measures whether `J` is sharp and dominant enough at the true `T` to be the basis of a new solver.

## The math

For each of the 4 landmark types t (`Portal`, `MeditationPillar`, `TeleportationPlatform`, `Npc`):

- Compute deviation map `D = max(0, screenshot_crop − aligned_base_texture)` (additive-only).
- Build per-type score field `L_t[y, x] = LocalNCC(D, T_t)` over the whole crop, integral-image, `O(W·H)`.
- No threshold.

For a candidate transform `T = (scale, rot ∈ {0, π}, mirror ∈ {0, 1}, tx, ty)`:

```
J(T) = Σ_{r ∈ refs(area)}  L_{type(r)} [ bicubic( T · r ) ]
```

`T · r` is the world→texture-pixel projection (the same math `AreaCalibration.WorldToWindow` performs today), composed with a texture→crop affine derived from the `MapRect`. Refs that project outside the crop contribute 0.

Indicative cost (Frame 1, 847×841 crop, 4 templates at 16 px):

- Field build: ~1 GFLOP total, ~30 ms.
- Memory: 4 × 847 × 841 × 4 B ≈ 11 MB.
- Per-`T` `J` evaluation: 38 bicubic lookups, ns each → billions/sec achievable.

## Tool surface

**Existing host:** `tools/MapCalibrationFromScreenshot/` — already has decoders, asset extraction, ref-loading (`study/refs/<area>.json`), debug-PNG plumbing, and the CLI flag surface. The Frame 1 / Frame 2 fixtures live in `tests/Mithril.MapCalibration.Harness.Tests/Fixtures/` and are reachable directly.

**Added:** new `--phase synthesis-probe`. The phase is purely additive — every existing phase keeps working unchanged.

New flags (in addition to existing `--screenshot`, `--area`, `--map-rect`, `--debug --outdir`):

| Flag | Purpose |
|---|---|
| `--truth-cal scale,rot,ox,oy,mirror` | Known-correct calibration to evaluate `J` at. Frame 1's comes from the tool's existing 16-inlier solve; Frame 2's from production. |
| `--ransac-seeds-csv <path>` | CSV of candidate calibrations to evaluate (one row per candidate: `label,scale,rot,ox,oy,mirror`). Hand-fed for now (Proposal B's seed-locality test). |
| `--trace-console` | Emit OTel spans to stdout. |
| `--otlp <endpoint>` | Emit OTel spans via OTLP to the named endpoint (Aspire Dashboard / Seq / Jaeger). |

## Outputs

All written under `--outdir` (defaulting to `study/synthesis-probe/<area>/`):

- `field_<type>.png` — 16-bit grayscale of each per-type field `L_t`, normalized to [0, 65535].
- `synthesis_probe.csv` — one row per evaluated transform with columns `experiment, label, scale, rot, mirror, tx, ty, J, refs_above_0.5, dominance_vs_runner_up`.
- `grid_landscape_tx_ty.png` — 2D image of `J(tx, ty)` from experiment E2 (at truth scale/rot/mirror), colormapped.
- `grid_landscape_scale.png` — 1D plot of `J(scale)` from experiment E3, written as a PNG strip.
- OTel trace stream (destination per the `--trace-console` / `--otlp` flag).

## Experiments

All experiments run sequentially within one `--phase synthesis-probe` invocation. Each emits CSV rows and OTel spans.

| Tag | What | Evals | Pass criterion |
|---|---|---|---|
| **E1** | Score `J(T_truth)` once | 1 | `refs_above_0.5 ≥ 20` of 38; zero truth-projected refs land off-crop |
| **E2** | Sweep `(tx, ty)` over `±(2 × template_render_size_px) ≈ ±32 px` around truth, step 1 px | 65² = 4 225 | Truth is a sharp local max; FWHM ≤ template render size |
| **E3** | Sweep `scale` over `±25%` of truth scale, step `1%` of truth scale | 51 | Truth is a sharp peak; `J` monotonically decreasing away from truth in both directions |
| **E4** | Score each row of `--ransac-seeds-csv` (if provided) | K (~8) | `J(T_truth) ≥ 1.5 × max_k J(T_k)` — truth dominates the best wrong RANSAC candidate by ≥50% |
| **E5** | Cold coarse grid: `scale ∈ 16 log-spaced values over [0.1, 2.0] px/unit × tx, ty stepped by ≤ template_render_size_px over the full crop × {rot, mirror} ∈ 4` | ~50–110 k | At least one of the top-8 grid maxima by `J` is within 5 px of truth's `(tx, ty)` after a local LM refine |

**Sweep widths are tied to physical priors, not magic numbers:**

- **E2 translation window = `2 × template_render_size_px`.** Templates are pinned at ~16 px on screen, so the score field has its primary structure on that scale. ±32 px captures one full peak plus its falloff. A 1 px-wide truth spike, a 10 px-wide bell, and a flat plateau are all distinguishable inside that window.
- **E3 scale window = `±25%`.** The Frame 1 pathology is a cluster fit at `scale ≈ 0.06 × scale_truth` (a ~16× collapse). ±25% is wide enough to see whether the objective starts curving toward that pathology, while still being tight enough to sample the local peak densely. If `J(scale_truth × 0.75) ≈ J(scale_truth)`, the objective alone won't reject the cluster pathology and we need a scale-plausibility gate downstream.
- **E5 grid step = `template_render_size_px` in (tx, ty).** The refine stage pulls in to the nearest local maximum; step finer than ~half the template width and we duplicate work, step coarser than the template width and the refine can miss the truth peak entirely. The scale bracket `[0.1, 2.0] px/unit` is set from "any plausible PG area" rather than from truth so this experiment honestly simulates a cold solve where truth isn't known.

**Run on both fixtures** in this order:

1. `eltibule-frame2-accepted-7.61px.gray.png` — zoomed-in, **positive control** (RANSAC succeeds today; synthesis must too, or our objective is broken).
2. `eltibule-frame1-rejected-3inliers.gray.png` — zoomed-out, **the hard case**.

If Frame 2 fails E1–E5, the probe itself has a bug; stop and fix it before reading anything into Frame 1's behavior.

## OTel instrumentation

The tool lives outside `Mithril.slnx`, so it does **not** depend on `Mithril.Shared.Diagnostics.Telemetry`. Instead: tool-local `ActivitySource("Mithril.Tools.MapCalibrationSynthesisProbe")` + the OTel SDK directly.

Span hierarchy:

- `probe.attempt` (root) — tags: `area`, `screenshot`, `truth.scale`, `truth.rot`, `truth.mirror`, `crop.w`, `crop.h`
  - `field.build` ×4 — tags: `template.type`, `template.size_px`, `duration_ms`, `mean_L`, `max_L`
  - `experiment.E1` … `experiment.E5` — tags: `eval_count`, `J_best`, `J_truth`, `truth_in_topk` (E5 only), `dominance` (E4 only)
    - `J.eval` — **sampled 1 in 1000** (E5 alone is 65 k evals; per-eval spans would dominate the trace). Full per-eval data lives in the CSV; OTel keeps the waterfall readable.

Exporter selection:

- `--trace-console`: `AddConsoleExporter()`
- `--otlp <endpoint>`: `AddOtlpExporter(o => o.Endpoint = new Uri(endpoint))`
- Neither: no listener attached → zero cost (per project convention; producers don't guard with `if (active)`).

## Decision criteria

Read the synthesis_probe.csv across both fixtures. Outcome → architecture mapping:

| Signal | Decision |
|---|---|
| Frame 2 fails E1 | Stop. Diagnostic itself is broken. |
| Frame 1 E1 high AND E4 truth ≫ all RANSAC candidates AND E2 + E3 both sharp peaks at truth AND E5 top-8 contains a ≤5 px-of-truth entry | **Proposal A** (cold synthesis solver, no detector) confirmed. |
| Frame 1 E1 high AND E4 truth ≫ all RANSAC candidates AND E2 + E3 sharp at truth, but E5 misses (no near-truth in top-8) | **Proposal B** (RANSAC seeds + synthesis re-rank + refine). Synthesis can localize the truth given a seed but can't find it cold. |
| Frame 1 E1 low OR E2/E3 flat at truth | Neither proposal ships in this form. The diagnostic surfaces the failure rather than letting it lurk. Investigate why `L_t` isn't informative (template mismatch? deviation contamination? something we haven't anticipated). |

## Out of scope (v0)

- No production code changes. The `IconLikelihoodField` builder lives in the tool, not in `src/Mithril.MapCalibration`.
- No new committed fixtures — use Frame 1, Frame 2, and the existing AreaSerbule artifacts.
- No RANSAC top-K refactor. `--ransac-seeds-csv` is hand-fed from separate runs (or from harness output we dump locally).
- No live wiring. `AutoCalibrationEngine` untouched.
- Hard regression bar: must not change any existing `--phase` behavior.

## Open questions / verification owed

1. **Truth calibration for Frame 1.** The existing tool's 16-inlier solve produced it. Worth re-deriving it from a hand-clicked landmark set (Legolas-style) as a second source-of-truth so we're not begging the question.
2. **Rotation enumeration.** Production enumerates `{0, π}` only — Legolas calibration findings, world A. If a future PG patch ships a `π/2` map orientation for some area, this assumption breaks; out of scope here.
3. **Co-located refs.** Frame 1 has refs at ~2 px separation. The field-based objective doesn't separate them — both refs contribute additively to the same field peak. Expected behaviour, but the diagnostic surfaces it through E1's `refs_above_0.5` count: if it's low, co-located peaks may be getting credit only once and the objective is undercounting them.
4. **Field memory.** 11 MB at 4× the live engine's crop size — easily fits, but verify no GC pressure on the OTLP-export path if we end up running E5's 65 k evals with sampling-off for a debug session.
5. **Pivot semantics in projection.** Production uses `Sprite.m_Pivot = (0.5, 0.5)` for all four sprites (verified in `tools/MapCalibrationFromScreenshot/README.md`). The probe must consume the *same* pivot when computing `T · r` so the field lookup site matches the icon-anchor pixel; mis-pivoting would put us a half-template-width away from the field peak and look like a flat objective.

---

## Results & open questions — first integration runs (2026-06-01)

### What's built and landed on `claude/synthesis-probe-impl`

- 18 tasks complete (1–14 + 17 + 18), ~30 commits, 29 unit tests green, tool builds clean.
- `--phase synthesis-probe` runs end-to-end: produces `synthesis_probe.csv`, four `field_<type>.png`, `grid_landscape_translation.png`, and an OTel trace.
- `--aligned-base <path>` flag added in Task 18 so the auto-load+resize step can be overridden when ECC-aligned base inputs are available.

### Smoke-run findings

Smoke runs were attempted against `frame{1,2}-crop.png` + `frame{1,2}-texture-resampled.png` from `%LocalAppData%/Mithril/diagnostics/calibration/938-masks/`, and against the raw screenshot dumps from the parent dir. The numbers across runs:

- **E1 J(truth)** = −1.86 (frame 2) and +0.06 (frame 1) — only 1–2 of 38 refs above 0.5.
- **E3 scale sweep**: a local peak ~4–9% away from the computed crop-space truth-cal, J ≈ 2.5–3.2. Sharp but offset from the truth-cal value I supplied.
- **E5 cold-grid top-8**: consistently lands at scale ≈ 0.10 with J ≈ 10–11. **3–4× higher J than the real-scale local peak in E3.**

### What that meant — and what it didn't

The headline E5 finding (cold-grid finds clustered tiny-scale fits with J that *dominates* the real-scale truth) is **not synthesis-specific** — it surfaces in the existing offline `--phase full` CLI too. Running `--phase full` on the same `frame{1,2}-crop.png` inputs and on the raw dumps produces `scale ∈ [0.07, 0.11]` with 3–5 inliers — the same tiny-scale clustered-fit pathology, just expressed via RANSAC's inlier count instead of the synthesis cumulative-J. The production **live engine** sidesteps this in two ways the offline path doesn't:

1. **ECC-aligned inputs** (#978). Production registers the screenshot to the texture sub-pixel before deviation, so the deviation map is dominated by actual icons rather than terrain-mismatch noise; tiny-scale fits then have nothing to project onto.
2. **The 4-inlier acceptance gate.** A 3-inlier fit at scale 0.07 still gets rejected even if it materializes.

Neither of these is wired into the CLI's `--phase full` or into the synthesis probe today. So the smoke runs were testing a degraded version of the synthesis objective on un-aligned inputs.

### Open questions to pick up next

1. **`--aligned-deviation <path>` flag** — extend the probe to skip `IconLikelihoodField.Build`'s subtraction step and consume a pre-computed deviation map directly. Then point it at `frame{1,2}-aligned-1-deviation.png` (the live engine's post-ECC, post-subtraction deviation dump) and rerun E1–E5. This is the cleanest test of the synthesis math.
2. **Truth-cal extraction.** Live engine's `%LocalAppData%/Mithril/MapCalibration/refinements.json` has the recovered `AreaCalibration` (Eltibule: `scale=0.31536, rotation≈-π, origin=(1039.45, -36.38)`, residual 0.34) in **texture-pixel** space. Converting to screenshot-pixel space for the probe requires the ECC's sub-rect (texture region the screenshot covers), which is internal to the live engine. Two options:
   - Expose the sub-rect alongside the recovered calibration in `refinements.json`.
   - Or have the probe operate in texture-pixel space directly (resample screenshot UP into the texture sub-rect; build fields in texture coords).
3. **E5 cold-grid scale bracket.** Once truth-cal is rigorous, tighten `scaleBracket` from `[0.1, 2.0]` to a physically-plausible range around the expected `S_crop`. Excludes the tiny-scale degeneracy by construction rather than by post-hoc gate.
4. **Input pair semantics in 938-masks/.** The `frame{1,2}-texture-resampled.png` files are *bilinear resizes of the full texture to the crop's dimensions*, NOT the post-ECC warped textures. So `--aligned-base` on those is a no-op vs auto-load. The actual post-ECC artifacts are the `aligned-1-deviation.png` etc. intermediate dumps.
5. **The captured-screenshot pair to test.** Raw dumps confirmed by user:
   - `map-67x51-1257x1049-color-20260601-122726-226.png` — **zoomed-OUT**, production does NOT solve.
   - `map-67x51-1257x1049-color-20260601-123012-696.png` — **zoomed-IN**, production solves (`refinements.json` value above is presumed from this run).
   These should be the working test pair, with their corresponding live-engine ECC outputs.

### Architectural takeaway so far (provisional)

The synthesis objective `J(T) = Σ L_t(T·r)` is consistent with what we'd want — but it inherits the same data-ambiguity gap that already bites RANSAC: without ECC-aligned inputs and a physically-meaningful scale prior, both objectives can be dominated by tiny-scale clustered "fits" that project all refs into a small high-noise region. So whichever solver we eventually ship — Proposal A (cold synthesis), Proposal B (RANSAC seed + synthesis re-rank), or a third option — it MUST consume ECC-aligned inputs AND constrain scale within the physically-plausible range. Today's runs were below that bar; the open questions above are how to get above it.

---

## 2026-06-02 — bundle-driven runs

Three per-attempt calibration bundles produced by the live engine (Mithril.MapCalibration's `AutoCalibrationEngine`, post-#985) were fed to the probe via the new `--bundle-dir`/`--maprect-json`/`--recovered-cal-json`/`--aligned-deviation`/`--hand-truth-cal` flags. The bundles consume post-ECC, post-subtraction deviation maps directly — the cleanest synthesis input we can give the probe today.

All three bundles are AreaEltibule. Hand-truth-cal for B and C is the bundled-baseline entry (`map-calibration-baseline.json` — `scale=0.7632337, rotation=3.141276, origin=(2146.21, -202.47), mirror=false, residual=0.65 px, 5 refs`).

### Run summary

| Bundle | Production verdict | E1 J(truth) | E1 refs ≥ 0.5 | E2 J_best | E3 J_best | E5 J_best_refined | E5 truth in top-8? | E5 best-dist to truth |
|---|---|---|---|---|---|---|---|---|
| A (031105-069, accepted, 10 inliers, 0.79 px) | clean accept | **19.02** | **21 / 38** | 19.02 | 19.02 | 5.10 | **no** | 542 px |
| B (031130-122, wrong-fit accept, 4 inliers, 4.03 px) — hand-truth | wrong | **−2.76** | **0 / 38** | 11.33 | 4.99 | 6.84 | no | 335 px |
| B (031130-122) — production-recovered | wrong | +0.39 | 2 / 38 | 4.06 | 3.22 | 6.78 | no | 65 px |
| C (031004-908, rejected, only 3 inliers) — hand-truth | rejected | **13.96** | **13 / 38** | 15.39 | 13.96 | 5.12 | **no** | 290 px |

E2 sweeps ±32 px around truth at 1 px step (4 225 evals); E3 sweeps scale ±25 % at 1 % step (51 evals); E5 cold-grid is ~118 k–250 k evals depending on MapRect-bracketed scale span, with the top-8 hill-climbed after sampling.

### Bundle A — positive control: J(truth) dominates locally, but cold-grid misses globally

E1 J(truth)=19.02 with 21/38 refs above 0.5 clears the design-criterion threshold of "≥20 refs above 0.5". E2 J_best = J_truth → truth IS the local maximum in the ±32 px translation neighborhood. E3 J_best = J_truth → truth IS the maximum in the ±25 % scale neighborhood. E2 falls off rapidly: at +6.7 px from truth J drops to 6.06 (-68 %), at +9.5 px J drops to 5.45 (-71 %). Sharp peak, as required.

But E5 cold-grid + LM refine **misses** truth: top-8 refined J values are all in the 4.1–5.1 range (vs J_truth=19.02), the closest top-8 candidate is 542 px from truth, and none of the eight survive the ≤5 px design criterion. The MapRect-bracketed scale span did exclude the tiny-scale degeneracy (E5 scales are 0.27–0.37, within ±25 % of truth's 0.337) — what remains is the spacing trap the design spec called out: at 16 px grid stride no sample lands within 5 px of truth, and the LM refine basin doesn't reach truth from 8–11 px away because J falls below the local-search horizon within 6 px.

**Verdict on A:** synthesis correctly *scores* truth as the dominant peak when it's given. Cold synthesis (Proposal A) does NOT find that peak from a uniform grid + LM refine. **Proposal B** (RANSAC seed + synthesis re-rank/refine) is consistent with this data.

### Bundle B — degraded ECC alignment; synthesis CANNOT rescue it

Bundle B's deviation map (07-deviation.png) is visibly contaminated: terrain features and map borders fluoresce at the same intensity as icons, and icon peaks are blurred. Production's 4-inlier residual-4.03 px accept reflects this — the detector found just enough matches to clear the gate floor but pointed RANSAC at a geometrically wrong fit (scale=0.582, rotation=-2.046 rad, mirror=true, 117° off truth).

The probe's hand-truth-cal `J = −2.76` with **0 / 38 refs above 0.5** is the headline: at the geometrically-correct cal, no ref projects onto a deviation peak. Production's wrong-recovered cal scores higher (J=+0.39, 2 refs above 0.5) — only because its 4 inliers were picked to line up with *whatever the noisy deviation map happens to show*. The wrong-fit is consistent with the contaminated input.

Neither candidate dominates: E5 cold-grid finds an unrelated candidate at J=6.84 some 335 px from hand-truth and 65 px from production-recovered (i.e., synthesis re-rank would prefer the E5 candidate over either named "truth"). The cold-grid candidate is also wrong — it's just the highest peak in a noisy field.

The Bundle B failure is at the **ECC stage**, not the solver. The right fix isn't a better solver; it's either (i) rejecting captures with poor ECC alignment, or (ii) better ECC. Synthesis is downstream of that gate. (See follow-up §1 below.)

Also note: Bundle B's MapRect.json records `height=999`, but the deviation/aligned-screenshot/base-texture-resampled files are actually 1006 × **986** (the screenshot is 1274 × 1047, so origin.y=61 + height=999 = 1060 overflows by 13 px and the engine clamps to fit). The probe ran with an override MapRect `(143, 61, 1006, 986)` written to scratch. The bundle's recorded height is a small live-engine bug (see follow-up §2).

### Bundle C — production rejected, synthesis scores truth correctly

Bundle C is the **architecturally interesting case**: production rejected with "only 3 inliers (need ≥ 4)" because NPC detection starved on the more-zoomed-out 688 × 683 view (per the pre-flight investigation: 0 NPCs detected vs 3 in Bundle A; Eltibule has 17 NPCs = 45 % of the 38-ref pool, so losing them all crippled same-type RANSAC). The probe's E1 J(hand-truth) = **13.96** with **13 / 38 refs above 0.5** and `refs_off_crop=0` shows that synthesis evaluates truth correctly even on the smaller MapRect. E3 J_best = J_truth → truth is the scale peak. E2 J_best=15.39 slightly above J_truth, with the small offset consistent with the hand-truth-cal's published residual of 0.65 px.

So on the rejected-908 capture: **production rejected, synthesis would have ACCEPTED truth given a near-truth seed**. The same continuous-evidence objective that aggregates `L_t(T·r)` over all 38 refs without a per-type discrete threshold doesn't care that 0 NPCs cleared the NCC threshold — the weak NPC-field correlations still contribute positive J, and the 13 portals + meditation pillars carry the rest. This is exactly the architectural case for the redesign predicted in the pre-flight notes.

E5 cold-grid still misses (truth_in_topk=false, best-distance=290 px) — same spacing-vs-peak-width trap as Bundle A.

**Verdict on C:** synthesis re-rank/refine layered on top of *any* near-truth seed source (current RANSAC, a wider RANSAC, hand-clicked anchors, or an ECC-based seed) would rescue Bundle C. Pure cold synthesis would not.

### Architectural verdict: **Proposal B** (RANSAC seed + synthesis re-rank), with ECC quality as a prerequisite

Three independent data points, all from real bundles:

- **A:** synthesis scores truth as the dominant local peak (J=19, 21/38 refs); cold grid misses by 542 px. → Re-rank wins; cold doesn't.
- **B:** ECC alignment failed → deviation map degraded → J(truth)=−2.76. Synthesis CAN'T fix what RANSAC also can't fix here. → Out of solver scope; needs ECC quality gate.
- **C:** production rejected (insufficient inliers) but J(truth)=14 with 13/38 refs; cold grid misses by 290 px. → Re-rank rescues this rejection; cold doesn't.

The criterion table at the top of this spec said Proposal B was the verdict when "E1 high AND E4 truth ≫ all RANSAC candidates AND E2 + E3 sharp at truth, but E5 misses (no near-truth in top-8)". That's exactly the A and C signature. B is below the bar for either proposal — but the failure is upstream of where either solver intervenes.

What this means for an implementation plan:

1. **Keep RANSAC as the cheap seed generator.** It's not bad at producing near-truth candidates when the deviation is clean; A's production solve is proof. Synthesis isn't going to replace it cold.
2. **Replace the post-RANSAC inlier-count gate with a synthesis-J re-rank.** RANSAC nominates K candidates → score each via `J(T_k)` → pick top, LM-refine, accept/reject by `J ≥ J_min` and `refs_above_0.5 ≥ N_min`. This change alone would have rejected Bundle B's wrong-fit (J=0.39, 2 refs vs the A baseline of J=19, 21 refs) and would have accepted Bundle C (synthesis's J=14, 13 refs vs no current acceptance path at all).
3. **Add an ECC-quality gate upstream** so degraded captures don't reach any solver. Bundle B was correctly *captured* but poorly *aligned*; production accepted a wrong-fit largely because the gate floor is inlier-count, not deviation-map quality. (See follow-up §1.)

Cold synthesis (Proposal A) is not warranted by this evidence. The spec's E5 design criterion ("at least one of the top-8 grid maxima within 5 px of truth after a local LM refine") fails on every bundle, including the positive control. The fix isn't to widen the cold-grid scale span — that's already MapRect-bracketed and the result is unchanged — it's to seed it.

### Follow-ups (out of scope for this PR)

1. **Acceptance-gate monotonicity check (Mithril-engine-side).** Bundle A was a clean accept (0.79 px residual) at 03:11:05; Bundle B's wrong-fit was accepted at 03:11:30, *replacing* Bundle A's good calibration in `UserRefinements.json`. The acceptance gate should reject a new fit when its J (or its residual) is meaningfully worse than the currently-stored calibration's J for the same area. This is a small surgical fix in `AutoCalibrationEngine`'s accept path — file as a separate issue after this PR lands.

2. **Bundle MapRect height clamp.** Bundle B's `04-maprect.json` records `height=999` but the engine clamps to 986 to fit the 1047-tall screenshot, so the recorded MapRect is inconsistent with the 1006×986 deviation/aligned files. File as a small data-quality bug; the fix is to record the clamped height.

3. **Investigate rejected-908 (Bundle C) NPC NCC starvation.** The pre-flight investigation found 0/17 NPCs detected because the more-zoomed-out 688×683 view (vs Bundle A's 905×898) put NPC icons below the 0.90 same-type NCC threshold. That detection-side issue is real and would benefit from either (i) a zoom-aware NCC threshold, (ii) a smaller NPC template that survives the resize, or (iii) the synthesis re-rank above — which makes the NPC-NCC starvation moot because synthesis aggregates *weak* field correlations rather than requiring a per-template threshold. The synthesis re-rank in follow-up #2 (above) is the simplest dominant fix; the detection-side tweaks become optional.

4. **Cold-grid spacing-vs-peak-width.** Bundle A's E5 misses by 542 px not because the peak isn't there but because at 16 px grid stride no sample lands within 5 px of truth, and the LM refine basin is narrower than the half-stride. If we ever wanted to revisit Proposal A, the cold grid would need either (i) a finer stride (proportional to template size, not equal to it) or (ii) a more aggressive multi-restart refine that explores beyond the nearest local max. Not worth it now given the seed-and-rerank path is cleaner.
