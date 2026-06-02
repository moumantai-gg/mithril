# Synthesis-J Re-Rank for Map Auto-Calibration

**Status:** design spec (output of brainstorming, 2026-06-02). Architectural verdict (Proposal B) is settled — see the [synthesis-probe diagnostic spec](2026-06-01-synthesis-probe-diagnostic-design.md) and PR #986. This spec covers **how to ship Proposal B in production**: where the math lands in `src/`, the settings/toggle surface, the shadow-mode telemetry contract, and the threshold-calibration plan.

## Goal

Replace [`AutoCalibrationEngine`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)'s post-RANSAC acceptance gate with a synthesis-J re-rank:

- **Today:** [`CalibrationConfidenceGate`](../../../src/Mithril.MapCalibration/Detection/CalibrationConfidenceGate.cs) accepts when `inlierCount >= 4 AND residualPixels <= 12.0`.
- **Proposed:** [`MapCalibrationSolveEngine`](../../../src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs) gathers RANSAC's top-K candidates; for each, score `J(T) = Σ L_t(T·r)` over the area's references; accept by `J ≥ J_min AND refs_above_0.5 ≥ N_min`. Inlier-count and residual remain informational.

The synthesis-probe diagnostic ran this objective on three real bundles and produced four independent data points, post-rim-mask (PR #993):

| Bundle | Production verdict | J | refs ≥ 0.5 | Synthesis-J would |
|---|---|---|---|---|
| A | accepted (10 inliers, 0.79 px) | 19.02 | 21 / 38 | accept (agrees) |
| C | rejected (3 inliers, NPC starvation) | 13.96 | 13 / 38 | accept (rescues false reject) |
| B truth (hand-derived) | n/a — production accepted a *different* fit | **15.55** | **16 / 38** | accept |
| B production-recovered (wrong fit, 117° off truth) | accepted (4 inliers, 4.03 px) | **2.54** | **4 / 38** | reject (catches false accept) |

The two B rows are PR #993's reframing: the earlier "Bundle B is degraded ECC, no solver can rescue" diagnosis was substantially wrong — it was the rim's NCC contribution spilling into nearby interior cells via the windowed integral-image computation. With rim-masking applied (PR #992), Bundle B's truth scores correctly *and* the wrong-recovered fit scores far below the truth. **Synthesis-J + rim mask is a sufficient quality signal on its own; no separate ECC-quality gate prerequisite.** This spec turns that verdict into a wired-up production change.

## Current state — the inlier-count gate's failure modes

The gate today lives in [`CalibrationConfidenceGate.Accept`](../../../src/Mithril.MapCalibration/Detection/CalibrationConfidenceGate.cs):

```
bool Accept(AreaCalibration solve, int inlierCount, out string? rejectReason)
{
    if (inlierCount < _inlierFloor) return false;          // floor = 4
    if (solve.ResidualPixels > _goodResidualThresholdPx)   // threshold = 12.0
        return false;
    return true;
}
```

Inlier-count is the proximate failure shape. The 4-inlier floor is **load-bearing** for sparse zones (we measured that a 3-inlier "clean residual" fit can be a wildly wrong transform — Eltibule frame1 produced a 3-inlier solve at scale=0.45 vs truth=0.76 with mirror flipped). But the floor is also **gameable**: PR #986's Bundle B is a 4-inlier accept at 4.03 px residual whose fit is 117° off truth (scale=0.582, rotation=-2.046 rad, mirror=true). Inlier-count says "✓ four matches, geometrically consistent"; the underlying detection pool was contaminated, so RANSAC found four positions consistent with the noisy field rather than four landmarks consistent with each other.

The objective `J(T) = Σ_{r ∈ refs} L_{type(r)}(T · r)` doesn't have this failure mode by construction: it scores against the **whole 38-ref pool** rather than the small subset RANSAC happened to find consistent. With rim-masking applied, Bundle B's wrong-fit scores `J=2.54` with 4/38 refs above 0.5 (vs the hand-truth's `J=15.55` with 16/38). The hand-truth dominates by a factor of 6 in J — no projected ref of the wrong fit lands on enough deviation peaks because the fit is wrong relative to the reference layout, independent of how clustered the detections were.

The same objective also **rescues Bundle C** (rejected with 3 inliers from NPC-NCC starvation): `J(truth)=13.96` with 13/38 refs above 0.5, because the continuous-evidence sum doesn't need a per-template NCC threshold to clear — weak field correlations from refs whose template missed the floor still contribute positive `J`.

## Proposed change — the synthesis-J gate

### Where the `L_t` fields get built

Synthesis-J needs four per-type `L_t` fields (one per `Portal`/`MeditationPillar`/`TeleportationPlatform`/`Npc`) plus the per-ref bicubic sampler. The inputs are all already in the live [`CalibrationAttemptContext`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationAttemptContext.cs):

| Field need | Source in `CalibrationAttemptContext` |
|---|---|
| Aligned screenshot (or deviation) | `AlignedCrop` + `AlignedTexture` (compute `D = max(0, crop − texture)` inline), OR pass deviation through directly once production produces it |
| Per-type templates | resolved via existing `IIconTemplateProvider` (already in-engine per [`EnsureIconTemplatesAsync`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L429)) |
| MapRect (texture↔crop conversion) | `MapRect` (already populated) |
| Refs | `References` (already populated by `IAreaReferenceProvider.ForArea`) |
| Top-K RANSAC candidates | new — `TypeAwareRansacSolver` today returns the single best; PR-1 below adds a top-K variant |

Re-rank flow ([`MapCalibrationSolveEngine.Solve`](../../../src/Mithril.MapCalibration/Detection/MapCalibrationSolveEngine.cs)) — the existing outer loop over orientations `{0, π}` is preserved; the new logic is inside each iteration:

```
for orientation in {0, π}:
    1. detect (unchanged)
    2. RANSAC → top-K candidates (K=8 default; was K=1)
    3. build 4× L_t fields from the deviation (rim-mask via DeviationFloodRimMask)
    4. for each candidate T_k, compute J(T_k) + refs_above_0.5(T_k)
    5. LM-refine the highest-J candidate via existing LocalRefine
    6. record per-orientation winner: (J, refs_above_0.5, refined candidate)

cross-orientation selector: pick the higher-J winner (was: lower-residual winner)
accept iff selected.J ≥ J_min AND selected.refs_above_0.5 ≥ N_min
```

Step 3's rim-masking goes through the already-shipped [`DeviationFloodRimMask.Build`](../../../src/Mithril.MapCalibration/Detection/DeviationFloodRimMask.cs) helper from PR #992 — same input the probe applies via `IconLikelihoodField.LoadDeviationAsField` so probe-measured `J` and production `J` are computed on identical masked input. The `L_t` fields are built once per orientation and reused across all K candidates; the per-candidate cost is just the 38-ref bicubic-sample loop.

### Q1 — Where in `src/` does the infra move?

**Position: move the math, not the bundle adapter.**

| Today's file (`tools/MapCalibrationFromScreenshot/SynthesisProbe/`) | New home | Notes |
|---|---|---|
| `IconLikelihoodField.cs` (`Build`, `LoadDeviationAsField`, `Sample`, `ScoreAll`) | [`src/Mithril.MapCalibration/Detection/IconLikelihoodField.cs`](../../../src/Mithril.MapCalibration/Detection/) | BCL-only; deps are `GrayImage` + `IconTemplate` already there. `LoadDeviationAsField` already calls `DeviationFloodRimMask` post-#992. |
| `JEvaluator.cs` | `src/Mithril.MapCalibration/Detection/JEvaluator.cs` | BCL-only; pure math. |
| `CandidateTransform.cs` | `src/Mithril.MapCalibration/Detection/CandidateTransform.cs` | BCL-only record. |
| `LocalRefine.cs` | `src/Mithril.MapCalibration/Detection/LocalRefine.cs` | BCL-only; hill-climbing in `(Tx, Ty, Scale)`. |
| `Bundle/MapRectConversion.cs` (math part: `AreaCalibration` + `MapRect` → `CandidateTransform` in aligned-pair-pixel space, geom-mean isotropic scale, anisotropy out-param) | `src/Mithril.MapCalibration/Detection/CandidateTransformFromCalibration.cs` (or a static method on `CandidateTransform` — naming TBD by the implementer) | Re-target onto the in-memory `AreaCalibration` instead of the probe-side `RecoveredCalibrationJson` DTO. The tool retains a thin adapter that loads the DTO and calls the shared math. |
| `Bundle/BundleJsonDtos.cs`, `Bundle/BundleLoader.cs`, `Bundle/BundleArgsResolver.cs`, `Bundle/PngHeader.cs` | **Stays in `tools/`.** | Production constructs the live data inline from `CalibrationAttemptContext`; it never reads its own bundles back. The probe loads bundles from disk; production does not. |

The probe keeps consuming `LoadDeviationAsField`/`JEvaluator`/`LocalRefine` from the new `src/` home via ProjectReference (the tool already references `Mithril.MapCalibration.csproj`). Two consumers, one piece of math.

**Decoder-free invariant.** All moved files are BCL-only. The [`ShippedGraphDecoderFreeTests`](../../../tests/Mithril.MapCalibration.Tests/) guard stays green. No new project required; the existing `src/Mithril.MapCalibration` core absorbs the additions.

**The two surfaces match by construction.** Production builds `L_t` from `CalibrationAttemptContext.AlignedCrop` / `.AlignedTexture` (subtraction inline) → `DeviationFloodRimMask` → `IconLikelihoodField.ScoreAll`. The probe builds `L_t` from the bundle's `07-deviation.png` → `IconLikelihoodField.LoadDeviationAsField` (same `DeviationFloodRimMask` call inside). The two paths converge at `ScoreAll`. Differences then can only come from `D = max(0, crop − texture)` (production-only) vs the bundle's pre-written deviation (which the live engine wrote from the same computation). Equivalent by construction; testable by feeding production's `AlignedCrop`/`AlignedTexture` to `LoadDeviationAsField` and asserting equality.

### Q2 — Settings, toggle, and shadow-mode shape

**Position: new POCO, three-state enum, runtime-flippable, mirrors `CaptureDiagnosticsOptions` pattern.**

#### The POCO

New `MapCalibrationSolverOptions` in `src/Mithril.MapCalibration/`:

```csharp
public sealed class MapCalibrationSolverOptions : INotifyPropertyChanged
{
    public SynthesisRerankMode SynthesisRerankMode { get; set; } = SynthesisRerankMode.Shadow;
    public double SynthesisJMin   { get; set; } = 8.0;   // placeholder; recalibrate per Q3
    public int    SynthesisNMin   { get; set; } = 8;     // placeholder; recalibrate per Q3
    public int    RansacTopK      { get; set; } = 8;     // candidates the re-rank scores
    // INotifyPropertyChanged plumbing...
}

public enum SynthesisRerankMode { Off, Shadow, Enabled }
```

DI singleton, threaded through `AddMithrilMapCalibrationEngine`, runtime-flippable (no graph re-resolve) — same shape as the existing [`CaptureDiagnosticsOptions`](../../../src/Mithril.MapCalibration.Capture/CaptureDiagnosticsOptions.cs).

**Why a new POCO rather than a flag on `CaptureDiagnosticsOptions`.** Capture-diagnostics is the bundle-dumping toggle, semantically "off in normal use, on for debugging." Solver behaviour is core production logic, semantically "always on, in one of three modes." Keeping them separate avoids the "is this a diagnostic or a solver setting?" coupling.

**Why a three-state enum rather than `bool RerankEnabled` + `bool ShadowMode`.** Two bools can express four states; only three are valid; the enum eliminates the invalid combination by construction. Naming: `Off` (no `L_t` build, zero-cost), `Shadow` (compute J, log telemetry, keep legacy gate), `Enabled` (J is the gate).

**Why `Shadow` is the default.** The threshold defaults (`SynthesisJMin=8.0`, `SynthesisNMin=8`) are anchored to PR #993's post-rim 4-bundle dataset (Bundle A=19.02/21, B-truth=15.55/16, C=13.96/13 all accept; B-wrong-fit=2.54/4 rejects). That's a comfortable margin on a tiny dataset — three accepts and one reject — but it's still only four data points from one area. Real-world play will introduce zoom + area + ECC-residual variance the dataset doesn't capture. Shadow mode lets that variance reveal itself in telemetry before the legacy gate stops being the source of truth. See Q3 for the Shadow → Enabled flip criteria.

#### Mode semantics

| Mode | `L_t` built? | Production gate | Telemetry emitted? |
|---|---|---|---|
| `Off` | no | inlier-count (legacy) | no |
| `Shadow` | yes (per-orientation, on the top-K RANSAC candidates) | inlier-count (legacy) — the persistence decision (whether to call `SaveUserRefinement`) follows the *legacy* verdict | yes (full disagreement record) |
| `Enabled` | yes | synthesis-J — accept iff `J ≥ J_min AND refs_above_0.5 ≥ N_min` | yes |

In `Shadow`, the legacy gate is still the source of truth for `IMapCalibrationService.SaveUserRefinement`. The synthesis-J computation runs to completion and emits its verdict-vs-legacy comparison, but never persists. This means a Shadow-mode session can be deployed safely; the worst case is "we paid the cost of L_t builds we didn't use." Cost: one rim-mask + 4× `ScoreAll` per accepted attempt (≈30 ms at the live crop size per the diagnostic spec's indicative cost), plus K bicubic-sample loops over 38 refs (cheap).

#### Telemetry contract

Reuses the existing [`MithrilActivitySources.MapCalibration`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs) source (`"Mithril.MapCalibration.Capture"`). Adds a `MithrilMeters.MapCalibration` static that fills the documented placeholder slot in [`MithrilMeters`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs).

**New span:** `calibration.synthesis_rerank` nested under the existing `calibration.solve` span. Tags:

| Tag | Type | Meaning |
|---|---|---|
| `synth.mode` | string | `off` / `shadow` / `enabled` (mirrors the enum, lowercase) |
| `synth.j_best` | double | winning candidate's `J(T_k)` |
| `synth.refs_above_0.5` | int | refs whose sampled `L_t(T·r) ≥ 0.5` |
| `synth.refs_total` | int | references for the area (denominator; usually constant per area) |
| `synth.refs_off_crop` | int | refs whose projected position fell outside the L_t field (contributed 0) |
| `synth.j_min` | double | active `J_min` at decision time (so log-search can correlate disagreements with threshold changes) |
| `synth.n_min` | int | active `N_min` |
| `synth.verdict` | string | `accept` / `reject` per synthesis-J |
| `gate.verdict` | string | `accept` / `reject` per legacy inlier-count gate |
| `gate.inliers` | int | inlier count from RANSAC |
| `gate.residual_px` | double | RANSAC fit residual (null/unset when no calibration) |
| `disagree` | bool | `synth.verdict != gate.verdict` |
| `disagree.would_change` | string | `none` / `accept_to_reject` / `reject_to_accept` — practical impact if we were in Enabled mode |

**New meters** (`MithrilMeters.MapCalibration` static):

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `mithril.map_calibration.synthesis.j` | Histogram\<double\> | (unitless) | `verdict` ∈ `{accept, reject}` |
| `mithril.map_calibration.synthesis.refs_above_threshold` | Histogram\<long\> | (unitless) | `verdict` |
| `mithril.map_calibration.synthesis.disagree` | Counter\<long\> | (count) | `change` ∈ `{accept_to_reject, reject_to_accept}` |

The `disagree` counter is the key telemetry: in shadow mode it accumulates the rate at which synthesis-J would have changed the legacy gate's verdict. Below a learnt rate (per Q3), the flip to Enabled is safe.

**Off-mode cost.** When `SynthesisRerankMode = Off`, the engine skips the `L_t` build and the span/meters fall back to the zero-cost no-listener path (per the producer-unconditional convention documented in CLAUDE.md). Off mode is exactly today's behaviour modulo one enum dispatch.

#### Acceptance criteria for the `Shadow → Enabled` default flip

Manual review of telemetry — automation would over-constrain a small dataset. Criteria:

1. **Coverage:** ≥ 50 attempts logged across ≥ 3 distinct areas in `Shadow`.
2. **No false-rejects on clean accepts:** every Bundle-A-pattern attempt (high-inlier-count clean residual) must score `synth.verdict = accept`. If even one clean accept comes back as `synth.verdict = reject`, do not flip — re-tune `J_min` downward first.
3. **≥ 1 confirmed `accept_to_reject`:** a wrong-fit the legacy gate accepted that synthesis-J correctly rejected (Bundle B pattern). Confirms the re-rank catches the failure mode that motivated this work.
4. **≥ 1 confirmed `reject_to_accept`:** a near-truth fit the legacy gate rejected (NPC-starvation / sparse-zone) that synthesis-J would have accepted (Bundle C pattern). Confirms the re-rank rescues the failure modes that the inlier floor is over-rejecting today.

#### Out of scope for this spec

- A user-facing settings UI for the three thresholds. The POCO is wired and writable; a Settings view is a follow-up. Power users + telemetry-driven recalibration suffice for Phase A/B.
- Per-area thresholds. Single global `J_min`/`N_min` for now. If Phase B telemetry shows per-area variance the threshold can't span, file a follow-up.

### Q3 — `J_min` / `N_min` threshold calibration plan

**Position: probe-based calibration first, shadow-mode telemetry second, default flip third.**

#### Chronology

| Phase | What | Status as of this spec |
|---|---|---|
| 0 | `DeviationFloodRimMask` helper shipped + probe applies rim-mask by default | **DONE** in PR #992 |
| A | Re-run probe over the 3 existing Eltibule bundles (A/B/C) with rim-masking enabled; record post-mask `J` / `refs_above_0.5`. Pick conservative `J_min` / `N_min` defaults consistent with: rejects Bundle B's wrong fit, accepts Bundles A + B-truth + C with margin. | **DONE** in PR #993 |
| B | Land synthesis re-rank PR-1 (math moves into `src/`) + PR-2 (`MapCalibrationSolverOptions` + shadow-mode wiring + telemetry). Default ships as `SynthesisRerankMode.Shadow`. | depends on this spec |
| C | User plays normally; shadow-mode telemetry accumulates. Per-area + per-zoom J distributions become visible via the new meters. Recalibrate thresholds against the real distribution. | depends on Phase B + real play time |
| D | Once acceptance criteria in Q2 are met (manual review of telemetry), land PR-3 changing default to `SynthesisRerankMode.Enabled`. | depends on Phase C |

**Why Phase A actually happened up-front (rather than being pending at spec time).** When this spec was being drafted, Phase A was a "pending — user-owned post-merge task" carried by PR #992. PR #993 closed it within the same brainstorming window. The post-rim numbers are what's pinned in the `MapCalibrationSolverOptions` defaults above. Without PR #993's data the initial defaults would have been anchored to pre-rim values, which PR #993's reframing of Bundle B (`J: -2.76 → 15.55`) shows would have been substantially wrong: a `J_min` picked from `B=-2.76` would have been tuned around a deeply incorrect baseline and would have over-accepted low-J wrong fits. Phase-A-before-PR-2-defaults is the load-bearing chronology.

#### Why both Phase A and Phase B (rather than just one)

- **Phase A alone is fast but undersampled.** Four data points, all Eltibule, all one player's hardware/zoom. A J_min picked from four points won't generalize across areas. But Phase A pins defensible "safe initial defaults" that beat shipping `J_min=0` (accept everything). Done in PR #993.
- **Phase B alone is slow but representative.** Real-world telemetry will surface zoom/area variance Phase A can't see. But Phase B without Phase A means the *initial* `Shadow` mode ships with arbitrary defaults; if the user happens to flip to `Enabled` before Phase B has data, they get a worse production gate than the legacy one. Phase A's conservative defaults make `Shadow` safe to ship by construction.

Sequence: Phase A pinned the initial defaults; Phase B re-tunes them via real data.

#### Why not skip Phase B and just flip on Phase A's thresholds

Four bundles aren't enough to know what `J_min` should be. PR #993's reframing of Bundle B (J: -2.76 → 15.55) is the cautionary tale — a confident pre-rim Phase-A conclusion was substantially wrong, and the failure mode (NCC integral-image spillover from rim contamination) wasn't visible from any one bundle alone. A hypothetical Bundle D from a different area might genuinely score J=7 with refs=6 and be correct. Without telemetry from real play, we can't distinguish "J=7 means wrong fit" from "J=7 is normal for low-ref-count areas." Shadow mode produces the disagreement data; without it we're guessing.

## Risks

| Risk | Mitigation | Caught by Shadow mode? |
|---|---|---|
| Synthesis-J false-rejects a clean accept (Q2 criterion #2 violated) | Conservative initial thresholds; Phase B re-tune | Yes |
| Synthesis-J over-tolerates wrong fits the inlier gate would have rejected | Threshold floor on `refs_above_0.5` is the backstop | Yes |
| `L_t` build cost is unacceptable on low-end hardware | Diagnostic spec measured ~30 ms at the live crop size; budget the per-attempt cost in PR-1 testing | Partial — perf cost shows up in `field.build` span timing |
| A future Bundle-D-like attempt fails at the ECC stage in a way rim-masking can't rescue | PR #993 retracted the "Bundle B was such a case" interpretation — Bundle B's interior noise was rim spillover, fixed by rim-masking. A genuinely ECC-degraded capture might still defeat synthesis-J. Shadow mode will surface attempts where `synth.j_best` is low + ECC residual is high (correlation visible in the telemetry tags). Routes to issue #991 (ECC quality investigation). | Yes |
| `Shadow` mode disagreement-rate stays high indefinitely (never converges to safe-to-flip) | Don't flip. Filing follow-ups on the disagreement causes is preferable to lowering the bar. | N/A — this *is* the bar |
| Production's in-memory `MapRect` math doesn't match the probe-side bundle math byte-for-byte | The shared `CandidateTransformFromCalibration` helper is the single math path; the conversion-equivalence test pins it. | Pre-deploy (unit test) |

## Open questions

1. **`TypeAwareRansacSolver` top-K plumbing.** The current solver returns the single best candidate. The re-rank needs the top K (default K=8 per `MapCalibrationSolverOptions.RansacTopK`). Two implementation options: (a) thread a `topK` param through `Solve` and return `IReadOnlyList<(AreaCalibration, IReadOnlyList<AssignedReference>)>`; (b) keep the public surface as-is and add a new method `SolveTopK`. (a) is one call site change; (b) preserves the existing test surface. Implementer's call.
2. **Should `Shadow` mode persist a per-attempt synthesis verdict to the bundle JSON?** PR #985's bundle currently writes `01-attempt.json` (legacy outcome). A `08-synthesis-verdict.json` sibling would let after-the-fact bundle-replay studies recover the synthesis-J reasoning. Probably yes, but it's a `CalibrationAttemptContext` shape change and an `AttemptFilesJson` field add — small but a separate review concern.
3. **Per-area threshold elaboration.** Single global thresholds may fail on areas with significantly fewer references. If a 14-ref area is normal-noisy enough that `refs_above_0.5 = 5` is "expected for a correct fit," `N_min=8` over-rejects there. Phase B telemetry will reveal this; the response is either to add per-area thresholds (config dict on the POCO) or to scale `N_min` to `area.RefCount` (e.g. `floor(refs_total × 0.2)`). Don't pre-solve; let the data decide.
4. **Interaction with [#988 acceptance-gate monotonicity](https://github.com/moumantai-gg/mithril/issues/988).** Once #988 ships, the engine compares a new fit's quality against the currently-stored calibration's quality. The natural extension is "compare J values when synthesis-J is the gate." #988's spec already anticipates this ("When synthesis-J ships per the umbrella synthesis-rerank issue: also reject if new J << existing J"). The order of work matters: if #988 lands first, it uses residual; if this umbrella lands first, #988 should consume J directly. Either order works; flag in the umbrella that #988's accept-comparison logic is a downstream consumer.

## Out of scope (filed separately)

These four sibling follow-ups are referenced as related context but explicitly NOT claimed by this umbrella:

| Issue | Title | Why it's out of scope |
|---|---|---|
| [#988](https://github.com/moumantai-gg/mithril/issues/988) | Map calibration: don't replace a good calibration with a worse one | Small surgical fix in `AutoCalibrationEngine`'s accept path. Independent of synthesis-J; both gates benefit from "don't downgrade an existing good calibration." |
| [#989](https://github.com/moumantai-gg/mithril/issues/989) | Map calibration: per-attempt bundle's 04-maprect.json records the unclamped height | Bundle-writing data-quality fix. Probe already works around it via PR #987's `BundleArgsResolver`. Production wiring doesn't read its own bundles back, so the synthesis re-rank doesn't depend on this. |
| [#990](https://github.com/moumantai-gg/mithril/issues/990) | Map calibration: investigate why NPC NCC drops below threshold on zoomed-out captures | Detection-side investigation. Synthesis-J **routes around** the NPC-NCC starvation by aggregating weak field correlations without a per-type threshold, but doesn't fix the root cause. If #990 lands a template/threshold improvement, both gates benefit. |
| [#991](https://github.com/moumantai-gg/mithril/issues/991) | Map calibration: investigate ECC alignment quality at high base-texture downsample ratios | ECC-quality investigation. Synthesis-J is **more robust** to a noisy deviation than RANSAC (Bundle C's J=14 vs RANSAC's reject; Bundle B's post-rim J=15.55 vs the contaminated pre-rim J=−2.76). PR #993 partially *retracted* the "Bundle B is an ECC failure" framing — much of what looked like ECC noise was rim spillover via the windowed integral-image NCC, fixed by rim-masking. Remaining ECC concerns cap the J ceiling on aggressive-downsample captures but no longer block synthesis-J adoption. |

This umbrella consumes their work as it lands but does not block on any of them; each is independently shippable.

## Milestones

| PR | Scope | Depends on |
|---|---|---|
| **PR-1** | Move `IconLikelihoodField` / `JEvaluator` / `CandidateTransform` / `LocalRefine` from `tools/MapCalibrationFromScreenshot/SynthesisProbe/` into `src/Mithril.MapCalibration/Detection/`. Add `CandidateTransformFromCalibration` (in-memory `AreaCalibration` + `MapRect` → `CandidateTransform`). Tool's `Bundle/MapRectConversion.cs` calls the shared math via a thin adapter. Top-K plumbing in `TypeAwareRansacSolver`. **No production behaviour change.** | This spec |
| **PR-2** | `MapCalibrationSolverOptions` POCO + `SynthesisRerankMode` enum. Wire `MapCalibrationSolveEngine` to build `L_t` from top-K candidates per orientation when mode ≠ `Off`; cross-orientation selector chooses higher J. Telemetry contract (new span + meters). Default `SynthesisRerankMode.Shadow` with `J_min`/`N_min` pinned to PR #993's post-rim values. **No accept-path behaviour change** (Shadow keeps the legacy gate as the source of truth). | PR-1 |
| **PR-3** | Once Q2 acceptance criteria met via real-world telemetry: flip default to `SynthesisRerankMode.Enabled`. Re-tune `J_min`/`N_min` per Phase C data. Document the criteria-met record. | PR-2 + ≥ 50 telemetry attempts across ≥ 3 areas |

## Verification owed

- **Conversion-equivalence unit test** between the production `CandidateTransformFromCalibration(AreaCalibration, MapRect)` and the existing tool-side `MapRectConversion.FromRecoveredCalibration(RecoveredCalibrationJson, MapRect)` — feed the same data through both paths, assert byte-equivalent `CandidateTransform` (modulo the `RecoveredCalibrationJson` → `AreaCalibration` round-trip).
- **Production-vs-probe `L_t` equality test:** feed an aligned crop + aligned texture into both the live production path and the probe's `LoadDeviationAsField`; assert the resulting `L_t` fields are byte-equivalent. Closes the door on "the two surfaces drifted apart silently."

*(Phase A recalibration was owed at draft time but completed in PR #993; defaults are pinned to those numbers.)*
