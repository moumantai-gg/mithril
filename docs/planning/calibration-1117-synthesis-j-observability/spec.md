# Shadow-mode synthesis-J observability — spec

**Issue:** [mithril#1117](https://github.com/moumantai-gg/mithril/issues/1117). **Status:** active. **Blocks:** path 1 of [mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) (synthesis-J as the auto-cal gate with per-area thresholds) — until this lands we cannot measure what synthesis-J actually scores sparse-interior cals at, and any threshold-tuning recommendation is speculation.

## 1. Problem

[`MapCalibrationSolveEngine.EmitSynthesisRerankTelemetry`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs) runs every auto-cal attempt (default `SynthesisRerankMode = Shadow` per [`MapCalibrationSolverOptions.cs:38`](../../../src/Mithril.MapCalibration/MapCalibrationSolverOptions.cs#L38)). It computes the synthesis-J score plus `RefsAboveHalf` / `RefsTotal` / `RefsOffCrop` for each top-K candidate, then emits to:

- `MapCalibrationDiagnostics.Meters.SynthesisJ` (Histogram) + `SynthesisRefsAboveThreshold` (Histogram) + `SynthesisDisagree` (Counter)
- `MapCalibrationDiagnostics.ActivitySource` span `calibration.synthesis_rerank` with tags `synth.j_best`, `synth.refs_above_0.5`, `synth.refs_total`, `synth.refs_off_crop`, `synth.j_min`, `synth.n_min`, `synth.verdict`, `gate.verdict`, `disagree`, `disagree.would_change`

**Neither of these lands in the two artifacts a real-world investigator actually has on disk after the fact:**

1. The per-day Serilog file under `%LocalAppData%/Mithril/Shell/logs/mithril-{yyyyMMdd}.json`. A `LogInformation` is only fired in `SynthesisRerankMode.Enabled` (lines 132 / 146-148 / 156 of `MapCalibrationSolveEngine.cs`). In `Shadow`, the score is invisible to grep.
2. The per-attempt diagnostic bundle directory under `%LocalAppData%/Mithril/diagnostics/calibration/<Map_X>-<timestamp>-<outcome>/`. The bundle's `01-attempt.json` carries `outcome`, `rejectReason`, `engineVersion`, `files`, `locatorBest` — and nothing else. No `synthesis` section, no top-K runner-up summary.

### Evidence

Sampled the 2026-06-08 Serilog (`mithril-20260608.json`) for category `Mithril.MapCalibration.Engine`: 35 lines across the day, spanning 4 outcomes (detect summary, locate accept/reject, solve accept/reject, inlier correspondences). **Zero lines containing `synthesis-J`, `synth.`, `J=`, or `RefsAboveHalf`.** Both the 19:37:14 Hogan's Basement accept (the [#1116](https://github.com/moumantai-gg/mithril/issues/1116) cal under investigation) and a clean comparison outdoor accept at 19:31:29 carry no synthesis-J information in the file the user can hand over.

Grepped the diagnostic bundle directory at `%LocalAppData%/Mithril/diagnostics/calibration/`: 6 Hogan's Basement bundles spanning all rejection / accept outcomes from `3.0.0.66+e87d9df6b7` and `3.0.0.103+f3161350bc`. The accepted bundle's `01-attempt.json` has the layout above. No synthesis section. Grepped the entire [`src/Mithril.MapCalibration.Capture/Diagnostics/`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/) folder for `synth|Synthesis|J_best|RefsAboveHalf`: zero matches.

### Why now

[mithril#1116](https://github.com/moumantai-gg/mithril/issues/1116) recommends promoting synthesis-J from `Shadow` to `Enabled` with per-area thresholds. The recommendation hinges on knowing what J / RefsAboveHalf the engine actually computes for sparse-interior cals. The hypothesis is that they score low and would have been rejected — but it's a hypothesis, not a measurement. Any per-area threshold scheme (e.g. `N_min = min(8, ⌈refs_total × 0.6⌉)`) needs data points from real attempts to calibrate against. A flip Shadow→Enabled with the wrong thresholds would silently reject every interior calibration and produce confusing "rejected" outcomes for users with no diagnostic signal in their log file.

## 2. Scope

In scope:

- **Diagnostic bundle** [`01-attempt.json`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs): new optional `synthesis` field carrying the per-attempt synthesis-J winner's scalars + the disagree-with-gate flag. Additive schema bump (`AttemptJson.SchemaVersion` 2 → 3, new `SynthesisJson` record at its own schema v1).
- **Serilog file** under `mithril-{yyyyMMdd}.json`: one Shadow-mode `Information` line per solve attempt with the same scalars, formatted for human grep and `mithril-logs` MCP queryability.
- **Engine surface** [`CalibrationSolveResult`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L499): new optional `Synthesis` field carrying a pure-data `SynthesisDiagnostics` record. Both surfaces consume from there — the engine populates once, the data is read twice.
- **Internal helper extraction in `MapCalibrationSolveEngine`**: lines 183-235 of [`MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L183) already compute the synth verdict, gate verdict, disagree flag, and `disagree.would_change` change-tag for the existing meter / span emit. Extract a `ComputeVerdicts(SynthesisOrientationWinner?, CalibrationSolveResult)` helper returning the four values so the new bundle population path, the new Serilog mirror, and the existing `EmitSynthesisRerankTelemetry` all use one definition. No behaviour change; refactor only.

Out of scope (called out in §6):

- Flipping `SynthesisRerankMode` from `Shadow` to `Enabled` — [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1 step (c), gated on the data this spec produces.
- Changing `SynthesisJMin` / `SynthesisNMin` defaults or adding per-area variants — same.
- Migrating the existing Meter / ActivitySource emissions to a different telemetry primitive. Those keep working for OTLP / perf-trace consumers; this spec *adds* a Serilog + bundle mirror, does not *replace* the existing emit.
- Per-ref likelihood values (the distribution behind `RefsAboveHalf`). Considered during brainstorm; rejected because retaining them requires plumbing through `ScoreOrientationCandidates` and is materially larger surgery. Revisit when threshold work (§ [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1) needs distribution shape.

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Two surfaces: bundle + Serilog mirror, not just one.** | The bundle is per-attempt, scoped, shareable as a unit (zip & attach to an issue), and schema-versioned — the natural place for the rich per-attempt record. The Serilog file is the natural surface for "did synthesis-J fire today and what verdicts did it produce?" greps and for `mithril-logs` MCP queries. Both are cheap; the synthesis data already exists at one point in the engine and is handed to both consumers via a single `SynthesisDiagnostics` field on `CalibrationSolveResult`. |
| D2 | **Summary only — no per-ref likelihood values.** | The investigation question driving this work is "what verdict would synthesis-J have rendered, and did it agree with the legacy gate?" The scalars on `SynthesisOrientationWinner` (`J`, `RefsAboveHalf`, `RefsOffCrop`, `RefsTotal`) answer that directly. Per-ref likelihoods would answer "what does the distribution look like below the `≥ 0.5` cutoff?" — useful for threshold tuning but not for the immediate observability question. Retaining them requires plumbing through `ScoreOrientationCandidates` (today they're aggregated into the count and dropped). Defer until [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1 step (b) collects enough summary-level data points to make the case for it. |
| D3 | **One emit per attempt — winner only, not per orientation.** | `MapCalibrationSolveEngine.Solve` runs rotate180 ∈ {false, true} and picks the cross-orientation winner. Logging both per-orientation candidates triples log volume per solve for a marginal gain — the per-orientation rejection cases are already captured by the `mithril.map_calibration.synthesis.j` Histogram in the perf-trace schema. The winner carries `Rotate180`, so the chosen orientation is preserved as a property on the single emit. Matches the cadence of the existing `Enabled`-mode synthesis line at [`MapCalibrationSolveEngine.cs:146-148`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L146). |
| D4 | **Verbose, threshold-bracketed Serilog message template.** | Template: `"Synthesis-J (shadow, rotate180={Rotate180}): J={J:0.00} (min {Jmin:0.00}), refs>=0.5 {Refs}/{Total} (min {Nmin}), off-crop {OffCrop}, would-{Verdict}, disagrees-with-gate={Disagree}."` The in-line `(min Jmin)` / `(min Nmin)` brackets let the reader instantly tell whether J/Refs are above or below the gate boundary without cross-referencing config. The `would-` prefix on `accept` / `reject` makes it unambiguous this is informational, not a decision. Including `disagrees-with-gate` surfaces the [#1116](https://github.com/moumantai-gg/mithril/issues/1116) failure mode (legacy accepts, synthesis-J would reject) directly. |
| D5 | **Bundle schema bump is purely additive (v2 → v3).** | New optional `Synthesis` field on `AttemptJson`, defaulted `null`. v2 readers ignore unknown fields (System.Text.Json default). v3 readers see `null` for pre-v3 bundles and treat that as "synthesis did not run, or pre-#1117 engine version". No migration needed; old bundles stay readable. |
| D6 | **`SynthesisDiagnostics` is a pure-data record in the Detection layer, mirrored to a wire-format `SynthesisJson` in the Capture layer.** | Same pattern the locator already uses (runtime `LocateMetrics` → wire `LocatorBestJson` at bundle-write time). Keeps the wire format colocated with the JSON source-gen context; keeps the runtime record free of `[JsonSerializable]` annotations that would force every consumer to know about the Capture-layer bundle. |
| D7 | **Engine-side log emit fires in Shadow mode specifically.** | The new line goes right after the existing accept/reject `LogInformation` at [`MapCalibrationSolveEngine.cs:117-125`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L117) so a log reader sees them paired. Gated on `mode == SynthesisRerankMode.Shadow && bestSynthesis is not null`. Skipped in `Off` (no synthesis was computed). Skipped in `Enabled` (the existing lines 146-148 / 156 already log J in their own message; double-logging would clutter). The bundle path, by contrast, populates `SynthesisDiagnostics` whenever synthesis ran — see D8. |
| D8 | **`SynthesisDiagnostics` is populated on `CalibrationSolveResult` whenever synthesis ran**, regardless of mode (Shadow OR Enabled). | Engineering-side: one assignment in one place. The bundle writer always reads `result.Synthesis` — it doesn't need to know about mode-gating, just "if present, serialise". This means the bundle surfaces synthesis even when running in `Enabled` (where the gate verdict already drove the outcome). Harmless; the bundle is per-attempt diagnostic state, more is better. |
| D9 | **No `docs/perf-trace-schema.md` edits.** | The perf-trace schema covers the `IPerfRecorder` JSON-lines file. The two new surfaces (bundle JSON and Serilog) are separate artifacts. The existing schema entry for `calibration_synthesis_rerank` (line 296 of [`docs/perf-trace-schema.md`](../../perf-trace-schema.md)) stays accurate — meters and span tags are unchanged. A future contributor reading the bundle schema doc will find synthesis there; a future contributor reading the Serilog cluster will find the line by grep. Cross-doc references add maintenance churn; skip unless a follow-up shows it's needed. |

## 4. Engine surface — `SynthesisDiagnostics`

New pure-data record in [`src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs) (or a sibling file in the same project — implementation choice):

```csharp
/// <summary>
/// Per-attempt diagnostic snapshot of the synthesis-J re-rank result. Populated
/// whenever synthesis ran (mode != Off), regardless of whether the legacy or
/// synthesis gate drove the outcome. Surfaced on <see cref="CalibrationSolveResult"/>
/// so both the diagnostic bundle (01-attempt.json synthesis section) and the
/// Shadow-mode Serilog mirror read from one source.
/// </summary>
public sealed record SynthesisDiagnostics(
    string Mode,              // "shadow" | "enabled"  (never "off" — record is null in that case)
    bool? Rotate180,          // null when no orientation produced a winner
    double? J,                // null when no winner
    double JMin,
    int? RefsAboveHalf,       // null when no winner
    int? RefsTotal,           // null when no winner
    int? RefsOffCrop,         // null when no winner
    int NMin,
    string Verdict,           // "accept" | "reject" | "no_winner"
    string GateVerdict,       // legacy gate verdict, "accept" | "reject"
    bool Disagree,            // synthesis verdict differs from legacy gate verdict
    string? DisagreeChange);  // "reject_to_accept" | "accept_to_reject" | null
```

Add a new init-only field to [`CalibrationSolveResult`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs#L499):

```csharp
public sealed record CalibrationSolveResult(
    AreaCalibration? Calibration,
    int InlierCount,
    string? RejectReason,
    IReadOnlyList<TypeAwareRansacSolver.AssignedReference>? Inliers = null)
{
    public IReadOnlyList<TypedDetection>? Detections { get; init; }
    public SynthesisDiagnostics? Synthesis { get; init; }   // NEW; null when mode == Off
}
```

Populated inside `MapCalibrationSolveEngine.Solve` right after `bestSynthesis` finalises (existing flow): translate `bestSynthesis` + `_options.SynthesisJMin` + `_options.SynthesisNMin` + the legacy gate's verdict into a `SynthesisDiagnostics` instance and assign to the result before returning. All scalars come from values `EmitSynthesisRerankTelemetry` already computes (lines 174-280 of `MapCalibrationSolveEngine.cs`); the construction is a pure restructuring.

## 5. Bundle surface — `01-attempt.json` v3

[`CalibrationBundleJson.cs`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/CalibrationBundleJson.cs) gains the wire-format record + the new field on `AttemptJson`:

```csharp
// AttemptJson — SchemaVersion bump 2 → 3, additive new field.
public sealed record AttemptJson(
    int SchemaVersion,
    string Area,
    string AttemptStartedUtc,
    string AttemptFinalizedUtc,
    string Outcome,
    string? RejectReason,
    string EngineVersion,
    AttemptFilesJson Files,
    LocatorBestJson? LocatorBest = null,
    SynthesisJson? Synthesis = null);    // NEW

/// <summary>
/// Bundle wire-format mirror of <see cref="SynthesisDiagnostics"/>. SchemaVersion 1 —
/// first persisted version. Null on AttemptJson when synthesis did not run
/// (SynthesisRerankMode == Off) or when the engine is pre-#1117 (v2 bundle).
/// </summary>
public sealed record SynthesisJson(
    int SchemaVersion,
    string Mode,
    bool? Rotate180,
    double? J,
    double JMin,
    int? RefsAboveHalf,
    int? RefsTotal,
    int? RefsOffCrop,
    int NMin,
    string Verdict,
    string GateVerdict,
    bool Disagree,
    string? DisagreeChange);
```

Register on the JSON source-gen context:

```csharp
[JsonSerializable(typeof(AttemptJson))]
[JsonSerializable(typeof(LocatorBestJson))]
[JsonSerializable(typeof(MapRectJson))]
[JsonSerializable(typeof(DetectionsJson))]
[JsonSerializable(typeof(RecoveredCalibrationJson))]
[JsonSerializable(typeof(SynthesisJson))]   // NEW
public partial class CalibrationBundleJsonContext : JsonSerializerContext;
```

The bundle sink ([`FilesystemCalibrationAttemptBundleSink`](../../../src/Mithril.MapCalibration.Capture/Diagnostics/FilesystemCalibrationAttemptBundleSink.cs)) reads `result.Synthesis` and translates to `SynthesisJson` field-by-field (no new helper needed; the records are isomorphic). Null in → null out.

## 6. Serilog surface — Shadow-mode mirror

In [`MapCalibrationSolveEngine.cs`](../../../src/Mithril.MapCalibration.Detection/MapCalibrationSolveEngine.cs) Solve's legacy branch (lines 109-127), after the existing accept/reject `LogInformation`:

```csharp
// Shadow-mode synthesis-J mirror. Fires only when synthesis ran and produced a
// winner, in Shadow mode (the legacy gate drove the outcome). In Enabled, the
// lines at 146-148 / 156 already log J as part of the accept/reject message; in
// Off no synthesis ran. See decision D7 of the #1117 spec.
if (mode == SynthesisRerankMode.Shadow && bestSynthesis is not null)
{
    var (synthVerdict, gateVerdict, disagree, _) = ComputeVerdicts(bestSynthesis, legacyResult);
    _logger?.LogInformation(
        "Synthesis-J (shadow, rotate180={Rotate180}): J={J:0.00} (min {Jmin:0.00}), "
        + "refs>=0.5 {Refs}/{Total} (min {Nmin}), off-crop {OffCrop}, "
        + "would-{Verdict}, disagrees-with-gate={Disagree}.",
        bestSynthesis.Rotate180,
        bestSynthesis.J, _options.SynthesisJMin,
        bestSynthesis.RefsAboveHalf, bestSynthesis.RefsTotal, _options.SynthesisNMin,
        bestSynthesis.RefsOffCrop,
        synthVerdict, disagree);
}
```

`ComputeVerdicts` is a small helper extracted from the existing duplicate logic in `EmitSynthesisRerankTelemetry` (lines 183-235 of `MapCalibrationSolveEngine.cs` already compute synth/gate verdict + disagree; the helper centralises it so the new emit + the existing meter emit + the populated `SynthesisDiagnostics` all use one definition).

Category: `Mithril.MapCalibration.Engine` (existing — same as the surrounding `Detect` and `Inlier correspondences` lines).

## 7. Tests

### 7.1 Engine logging tests

In [`tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs`](../../../tests/Mithril.MapCalibration.Tests/Detection/MapCalibrationSolveEngineLoggingTests.cs) (existing file):

- **`Shadow_mode_emits_synthesis_summary_line`** — fixture with mode = Shadow, synthesis produces a winner. Assert one `LogInformation` line on category `Mithril.MapCalibration.Engine` containing the `Synthesis-J (shadow` prefix and the expected `J`, `RefsAboveHalf`, `Total`, `Verdict`, `Disagree` properties.
- **`Off_mode_emits_no_synthesis_line`** — fixture with mode = Off, assert no synthesis-prefixed log line fires.
- **`Enabled_mode_does_not_double_log`** — fixture with mode = Enabled, assert the existing line at 146-148 / 156 fires (one line) and no separate `Synthesis-J (shadow` line is emitted.
- **`Disagree_true_when_synthesis_verdict_differs_from_legacy_gate`** — fixture where legacy gate accepts (4 inliers + 6 px residual ≤ 12) AND synthesis would reject (J = 2.0 < `JMin` = 8.0). Assert `Disagree=True` and `DisagreeChange="accept_to_reject"` in both the log line's properties and the populated `SynthesisDiagnostics` on the result.

### 7.2 Bundle JSON tests

In [`tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs`](../../../tests/Mithril.MapCalibration.Capture.Tests/Diagnostics/CalibrationAttemptBundleSinkTests.cs) (existing file):

- **`V3_bundle_has_synthesis_section_when_synthesis_ran`** — `result.Synthesis` populated, write bundle, parse `01-attempt.json`, assert `schemaVersion=3` and `synthesis` field present with all scalar properties round-tripped.
- **`V3_bundle_omits_synthesis_when_mode_is_off`** — `result.Synthesis = null`, write bundle, assert `synthesis` is null (or absent, depending on the source-gen's null handling) in the JSON.
- **`V3_code_reads_pre_v3_bundle_with_null_synthesis`** — Realistic forward-compat scenario: a bundle written by pre-#1117 code (schemaVersion=2, no `synthesis` field) is read by the post-#1117 v3 record type. Assert the deserialised `Synthesis` is null (the `SynthesisJson? Synthesis = null` default kicks in). Pins the contract that landing #1117 doesn't break user-collected diagnostic bundles already on disk.
- **`Disagree_change_serialises_correctly`** — Synthesis with `DisagreeChange="accept_to_reject"` survives round-trip.

### 7.3 Engine wiring test

One additional test (in either file — implementation choice) asserting `Solve` populates `result.Synthesis` correctly in Shadow + Enabled, and leaves it null in Off. Mirrors `D8`.

## 8. Verification owed

None. Synthesis-J has been live in Shadow mode since [#1022](https://github.com/moumantai-gg/mithril/issues/1022) shipped; the scalars on `SynthesisOrientationWinner` are stable and consumed by the existing Meter + ActivitySource emit. This spec is a pure additive read of an existing data source.

## 9. Out of scope

- **Per-area threshold scheme** — needs the data this spec produces; tracked as [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1 step (b).
- **Shadow → Enabled flip** — same; [#1116](https://github.com/moumantai-gg/mithril/issues/1116) path 1 step (c).
- **Per-ref likelihood retention** — defer until threshold work needs distribution shape (see §2 / D2).
- **A separate synthesis-J file in the bundle** (e.g. `12-synthesis.json`) — considered briefly; rejected. The data is small (a handful of scalars), the bundle has many image files already, and `01-attempt.json` is the conventional home for attempt-level scalars. A separate file would add bundle-sink wiring and an entry in `AttemptFilesJson` for no readability win.
- **mithril-logs MCP event-type registration** — the Serilog line is a regular `Mithril.MapCalibration.Engine` category message, queryable by `category` + `message_contains: "Synthesis-J"`. Promoting it to a named event type is a follow-up if querying patterns settle around it.

---

— drafted by Claude (Opus 4.7), posted by @arthur-conde
