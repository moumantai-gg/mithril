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
