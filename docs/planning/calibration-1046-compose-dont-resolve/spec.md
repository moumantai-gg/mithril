# Spec — Compose-don't-resolve for runtime projection + best-fit picker (mithril#1046)

**Tracked in:** [mithril#1046](https://github.com/moumantai-gg/mithril/issues/1046).
**Brainstormed:** 2026-06-03 with @arthur-conde; decisions captured in §3.
**Upstream:**
- [mithril#1041](https://github.com/moumantai-gg/mithril/issues/1041) (PR [#1048](https://github.com/moumantai-gg/mithril/pull/1048)) shipped `MapSceneRef` as the universal calibration identity; this spec assumes `IMapCalibrationService.GetCalibration(MapSceneRef)` and `GetAllSources(MapSceneRef)` as the consumer-facing surface.
- [mithril#988](https://github.com/moumantai-gg/mithril/issues/988) shipped the monotonicity gate; this spec retires it.
- [mithril#1005](https://github.com/moumantai-gg/mithril/issues/1005) shipped the scale-regime guard + `AreaCalibration.LocatorScale` field; this spec retires both.

**Canonical references:**
- Issue body: <https://github.com/moumantai-gg/mithril/issues/1046> — verbatim source for the architectural shift.
- Code surface: [`IMapCalibrationService`](../../../src/Mithril.MapCalibration/IMapCalibrationService.cs), [`MapCalibrationService`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs), [`AutoCalibrationEngine`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs), [`AutoCalibrationTrigger`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs).
- Telemetry shape: [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) — extended with `calibration.drift_check`.

## 1. Problem

`AreaCalibration` is a similarity transform from world coords to **base texture pixels**. The transform is **intrinsic to the area** — the texture is fixed, world coords are fixed, the relationship doesn't change with PG's zoom slider, doesn't drift with player movement, doesn't depend on capture state.

Once solved (manually via [`LandmarkCalibrationSolver`](../../../src/Mithril.MapCalibration/LandmarkCalibrationSolver.cs), from community sync, or from auto-calibration), `AreaCalibration.WorldToWindow(world)` answers "where on the texture does this world coord land?" forever, for the same scene.

The current auto-calibration engine re-fits world→texture from captured icons on the manual hotkey path and (until #1046) on every bundled→auto upgrade attempt. The cost is real (per [#966](https://github.com/moumantai-gg/mithril/issues/966), brute-force full-res NCC in `Refine` dominates the engine's wall-clock — minutes per attempt under the legacy refiner; sub-second under the [`FeatureMatchingRefiner`](../../../src/Mithril.MapCalibration.Detection/FeatureMatchingRefiner.cs), but the silent-re-solve risk to a converged fit is unchanged).

Two further structural smells survive into the post-#1041 codebase:

1. **`MapCalibrationService.GetCalibration` picks the wrong fit when N is small.** The current picker prefers a `UserRefinement` with `ResidualPixels ≤ CalibrationGoodResidualPx` over a `BundledBaseline`. But a closed-form similarity solve is exactly-determined at N=2 (residual ≈ 0 by construction); a 0.3 px residual from a 2-ref wizard fit shouldn't beat a 0.9 px residual from an 8-ref baseline. The picker is dimensional-unaware.
2. **The #988 monotonicity gate and #1005 regime guard exist only to defend silent re-solves.** Once the architecture stops re-solving silently, both gates become unreachable. They were correct patches against the architecture as built; they become unnecessary once the architecture changes.

## 2. Goal

Solve world→texture once per scene; compose at runtime; never re-solve over a converged calibration without explicit user consent.

Concretely:

1. **Replace `GetCalibration`'s picker logic** so candidate calibrations are compared on `ResidualPixels` ordering, gated by a `ReferenceCount` floor, with source-precedence as a tiebreak.
2. **Manual `CaptureCalibrateCommand` hotkey becomes verify-and-warn** when a converged fit already exists for the scene: run the locator, project known landmarks via `stored ∘ texture→screen`, compare to icon-detector output, and surface drift (or its absence) to the user. Recalibration only happens on explicit user re-press within a short arming window.
3. **`AutoCalibrationTrigger`'s pre-flight is decoupled from the picker.** It asks the store directly via `GetAllSources` whether any `AutoCapture`/`UserRefinement` record exists; if so, skip. The trigger's contract is "one cold solve per scene per install," not "re-solve whenever the picker prefers a different source."
4. **Delete the now-unreachable #988 gate, #1005 regime guard, and `AreaCalibration.LocatorScale` field**, plus the test surface that exercised them.

The live-overlay path (Legolas drawing on PG's live in-game map view) is codified as `world → texture (AreaCalibration) → screen (IMapRegionRefiner output)`. Per-frame locator efficiency and pan-recovery on featureless areas remain out of scope (separate issue when the live-overlay rewrite lands).

## 3. Ratified design decisions

Each row was litigated during the 2026-06-03 brainstorm; option letters reference alternatives surfaced at that time. **No "Other" / open-ended outcomes** — every decision is closed.

| # | Decision | Choice | Rationale |
|---|---|---|---|
| **D1** | Verify-and-warn drift policy on the manual hotkey | **A** — Trigger-on-suspicion only. Hotkey runs the locator + projection check; on Ok, chip says so and no-op; on Drift, chip says so and arms re-press-to-confirm; background never runs. | Smallest v1 that's honest about what the architecture allows. Doesn't depend on the live-overlay rewrite. Matches the issue's own instinct. Periodic spot-check (B) and per-frame piggyback (C) can land later if telemetry shows users miss real drift. |
| **D2** | `GetBestCalibration` sibling vs in-place body change to `GetCalibration` | **B** — `GetCalibration`'s body adopts the new picker rule. No sibling method. | One surface. Every consumer (overlay, Legolas service, Silmarillion, Gwaihir, autocal trigger) gets the picker on next build, no per-site migration. `GetAllSources` already exists for debug surfaces that need to see every candidate; a future `GetCalibrationFrom*` sibling can land later if a source-specific lookup case appears. |
| **D3** | `MinReferences` floor | **A** — `MinReferences = 4`. | Matches the autocal inlier floor (the gate already requires ≥4 inliers to accept). First reference count where residual is statistically meaningful (under-determined at N=2, barely informative at N=3). Tuneable via a single `const`. |
| **D4** | Where drift surfaces in the verify-and-warn flow | **A** — Status chip on both outcomes; second hotkey press within an arming window (10 s) confirms recalibrate. | Reuses the only channel the autocal system already owns. Keeps in-game focus intact. Future direction: the planned inbox feature is the natural home for persistent, dismissible drift signals — when inbox lands, the chip arming becomes an inbox item with a "Recalibrate" action; the hotkey-re-press idiom remains as the fast path. Out of scope for v1. |
| **D5** | Dead-code removal: in this PR or follow-up | **A** — Atomic PR deletes #988 monotonicity gate, #1005 regime guard, and `AreaCalibration.LocatorScale` field. No `SchemaVersion` bump (old JSON loads with the unknown property silently ignored). Tests for both gates go with them. **Correction (post-A3):** `GameConfig.CalibrationGoodResidualPx` does NOT become dead — it has two non-picker consumers: `PinCalibrationCoordinator.IsResidualGood` (the Legolas calibration wizard's "Confirm" gate) and `CaptureServiceCollectionExtensions.BuildConfidenceGate` (the auto-capture confidence gate). The picker-side cleanup (drop the `_goodResidualThresholdPx` ctor param + DI wiring on `MapCalibrationService`) is still right; the property itself stays alive, unmarked. | Matches #1041's "single atomic PR" precedent. The gate + regime guard + `LocatorScale` field exist *only* to support multi-attempt re-solve; compose-don't-resolve makes that path nonexistent. The `CalibrationGoodResidualPx` correction was found during A3 implementation — initial spec claim that "the threshold was the old picker's gate" was wrong; it's also the wizard's UX gate + autocal's confidence threshold. |
| **D6** | `AutoCalibrationTrigger` pre-flight check after the picker changes | **A** — Switch from `GetCalibration(scene).Source != BundledBaseline` to `GetAllSources(scene).Any(s => s.Source is UserRefinement or AutoCapture)`. | Decouples the trigger's "have we ever solved this scene?" question from the picker's "which candidate wins for runtime projection?" question. Without this change, the new picker could pick a `BundledBaseline` over a lower-quality stored `AutoCapture` and re-arm the trigger on every zone-in — a regression. The trigger's `_persistedScenes` set stays as an in-session in-flight dedup guard. |
| **D7** | Logging contract for the verify-and-warn flow | **Explicit** — every decision point on the drift-check + arming path emits an `ILogger` line at Information/Warning level; per-reference detail at Trace; a `calibration.drift_check` span feeds OTLP. | The user reports the overlay status chip may not render reliably; logging is the primary observability surface until a separate chip-render triage resolves that. See §9. |

## 4. Architecture overview

```
   stored AreaCalibration (intrinsic to scene; persistent in MapCalibrationService stores)
                              │
                              │  world → texture-pixel
                              │  (AreaCalibration.WorldToWindow)
                              ▼
                   ┌──────────┴──────────┐
                   │                     │
                   ▼                     ▼
       Texture-canvas consumers     Live-overlay consumer
       (Silmarillion area page,     (Legolas drawing on PG's
        Gwaihir POI authoring on    live in-game map view)
        the base texture)               │
           │                            │  texture → screen
           │  texture → control         │  (IMapRegionRefiner.Refine →
           │  (WPF view transform)      │   LocateMetrics.Scale + Tx/Ty)
           ▼                            ▼
        rendered pin                  rendered pin
```

Three legitimate re-solve triggers remain. Every other path becomes verify-only.

| Trigger | Path | Re-solve? |
|---|---|---|
| Cold scene (no `AutoCapture`/`UserRefinement` in store) | `AutoCalibrationTrigger` on zone-in | Yes — bundled→auto upgrade or first-ever calibration |
| Manual hotkey, drift check Ok | `CaptureCalibrateCommand` → engine `CheckDriftAsync` → Ok | No — chip says so, no-op |
| Manual hotkey, drift detected, user confirms via re-press | `CheckDriftAsync` → Drift → arm → re-press within window → `TryCalibrateCurrentAreaAsync` | Yes — user-confirmed recalibrate |
| Manual hotkey, drift detected, no re-press within window | `CheckDriftAsync` → Drift → arm → window expires | No — arming dropped, no-op until next press |
| Background scene-changed event into already-solved scene | `AutoCalibrationTrigger.OnSceneChangedAsync` → `GetAllSources` returns converged record → skip | No — store-backed one-shot promise |

## 5. Picker — `MapCalibrationService.GetCalibration`

The body of [`MapCalibrationService.GetCalibration(MapSceneRef)`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs:45) is replaced. The `IMapCalibrationService` interface signature is unchanged; `IsCalibrated`, `WorldToWindow`, `WindowToWorld` continue to delegate; `GetAllSources` is unchanged.

### 5.1 New rule

```csharp
public AreaCalibration? GetCalibration(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

    var candidates = new List<AreaCalibration>(capacity: 2);
    if (_userStore.TryGet(scene.MapAssetKey, out var user)) candidates.Add(user);
    if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) candidates.Add(baseline);
    // CommunitySync slot reserved.

    if (candidates.Count == 0) return null;

    var eligible = candidates.Where(c => c.ReferenceCount >= MinReferences).ToList();

    // Fallback: every candidate is below the floor — return the
    // highest-source-precedence one (better than nothing for the
    // consumer's degradation UX).
    if (eligible.Count == 0)
        return candidates.OrderByDescending(SourceRank).First();

    return eligible
        .OrderBy(c => c.ResidualPixels)
        .ThenByDescending(SourceRank)
        .First();
}

internal const int MinReferences = 4;

private static int SourceRank(AreaCalibration c) => c.Source switch
{
    CalibrationSource.UserRefinement  => 4,
    CalibrationSource.AutoCapture     => 3,
    CalibrationSource.CommunitySync   => 2,
    CalibrationSource.BundledBaseline => 1,
    _ => 0,
};
```

### 5.2 What goes away

- The `_goodResidualThresholdPx` constructor parameter on `MapCalibrationService` and its DI wiring (the `goodResidualThresholdPx` parameter on `AddMithrilMapCalibration`). The field, ctor param, and DI parameter are removed in the same PR.
- `GameConfig.CalibrationGoodResidualPx` **stays alive and unmarked** (no `[Obsolete]`). It has two non-picker consumers that still need it: `PinCalibrationCoordinator.IsResidualGood` (the Legolas calibration wizard's "Confirm" gate) and `CaptureServiceCollectionExtensions.BuildConfidenceGate` (the auto-capture `CalibrationConfidenceGate` accept threshold). The picker-side wiring is severed; the property itself is not dead.
- The old precedence-based body of `GetCalibration` (lines 45–66 of `MapCalibrationService.cs`).

### 5.3 What's preserved

- `AllCalibrations` property — unchanged in shape; iterates union of asset keys and reads the (new) `GetCalibration` per scene.
- `GetAllSources(MapSceneRef)` — unchanged; consumers needing every candidate (debug surfaces, the trigger's pre-flight per §7) read here.
- `Changed` event semantics — unchanged.

## 6. Verify-and-warn manual flow

### 6.1 New engine method

```csharp
// In IAutoCalibrationRunner — new sibling to TryCalibrateCurrentAreaAsync.
Task<DriftCheckOutcome> CheckDriftAsync(CancellationToken ct);

public abstract record DriftCheckOutcome
{
    public sealed record NoStoredCalibration : DriftCheckOutcome;
    public sealed record CaptureFailed(string Reason) : DriftCheckOutcome;
    public sealed record MapNotLocated(string Reason) : DriftCheckOutcome;
    public sealed record NoIconDetections : DriftCheckOutcome;
    public sealed record Inconclusive(string Reason, int MatchedReferences) : DriftCheckOutcome;
    public sealed record Ok(double MaxResidualPx, int MatchedReferences) : DriftCheckOutcome;
    public sealed record Drift(double MaxResidualPx, int MatchedReferences, double ThresholdPx) : DriftCheckOutcome;
}
```

### 6.2 Engine pipeline

`CheckDriftAsync` reuses the existing `RunAttemptCoreAsync` infrastructure up through `IMapRegionRefiner.Refine` and the typed icon detector, then branches:

1. Resolve the scene via `SceneResolution.ResolveCurrentScene(_mapState, _sceneCache)`. Null → cold path (`NoStoredCalibration`).
2. Look up `stored = _calibrationService.GetCalibration(sceneRef)`. Null → `NoStoredCalibration` (caller falls through to cold solve).
3. Capture the framed bbox under the overlay-blank guard (existing path).
4. Run `IMapRegionRefiner.Refine`. Null `AcceptedRect` → `MapNotLocated(reason)`.
5. Build `references = _references.ForArea(sceneRef)` (same set the cold solve uses).
6. Run the typed icon detector on the captured frame (same `DetectionRequest` shape, minus the solver invocation).
7. For each reference:
   - `predictedTexture = stored.WorldToWindow(refWorld, currentZoom: 1.0)`
   - `predictedScreen = predictedTexture * LocateMetrics.Scale + (LocateMetrics.Tx, LocateMetrics.Ty)` (composed via `LocateMetrics.Scale`, `LocateMetrics.Tx`, `LocateMetrics.Ty`)
   - Find the nearest `TypedDetection` within 20 px of `predictedScreen`. If none, drop this reference.
   - Otherwise record `|predicted − actual|`.
8. Aggregate:
   - Matched < 3: `Inconclusive("too few visible landmarks", matched)`.
   - Otherwise `max = max(residuals)`, `threshold = DriftToleranceFactor × stored.ResidualPixels` (where `DriftToleranceFactor = 3.0`):
     - `max > threshold` → `Drift(max, matched, threshold)`.
     - Else → `Ok(max, matched)`.

### 6.3 New consts (engine)

```csharp
private const double DriftToleranceFactor = 3.0;
private const double DriftMatchGatePx = 20.0;
private const int DriftMinMatchedReferences = 3;
```

### 6.4 Hotkey coordination

The arming state + window live in a small new `ManualCalibrationCoordinator` (DI-singleton, owned by `Mithril.MapCalibration.Capture`) that:

- Exposes `Task HandleHotkeyAsync(CancellationToken ct)` consumed by `CaptureCalibrateCommand` (the hotkey command keeps its public shape; the body becomes a one-line delegate).
- Owns `_armedUntil : DateTimeOffset?` and the 10-second arming window (`ArmingWindow = TimeSpan.FromSeconds(10)`).
- Decision tree on each hotkey press:
  1. Resolve scene via the existing cascade. Bbox/foreground gates checked the same way `TryCalibrateCurrentAreaAsync` checks them — surface a reject chip via `CalibrationStatusFormatter` on failure.
  2. If `_armedUntil` is set and `TimeProvider.GetUtcNow() < _armedUntil`: armed re-press → call `_runner.TryCalibrateCurrentAreaAsync(ct)`, surface its outcome via `CalibrationStatusFormatter`, clear `_armedUntil`.
  3. Otherwise, clear any stale `_armedUntil` and look up `stored = _calibrationService.GetCalibration(scene)`:
     - Null: cold path. Call `_runner.TryCalibrateCurrentAreaAsync(ct)` (existing behavior).
     - Non-null: drift-check path. Call `_runner.CheckDriftAsync(ct)`:
       - `Ok` → chip `"Calibration check OK — no drift detected."` Do not arm.
       - `Inconclusive` → chip `"Drift check inconclusive — {reason}."` Do not arm.
       - `Drift(max, matched, threshold)` → set `_armedUntil = TimeProvider.GetUtcNow() + ArmingWindow`, chip `"Drift detected (~{max:0.0}px). Press calibrate hotkey again within {seconds}s to recalibrate."`
       - `CaptureFailed`/`MapNotLocated`/`NoIconDetections` → surface the actionable reject reason via `CalibrationStatusFormatter`. Do not arm.
       - `NoStoredCalibration` → fall back to `TryCalibrateCurrentAreaAsync` (cold path; coordinator's drift-check pre-check is purely a fast path).

The arming state is in-process only. A Mithril restart disarms.

### 6.5 New chip messages

Added to [`CalibrationStatusFormatter`](../../../src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs):

| Trigger | Text |
|---|---|
| DriftCheck.Ok | `"Calibration check OK — no drift detected."` |
| DriftCheck.Inconclusive | `"Drift check inconclusive — {reason}."` |
| DriftCheck.Drift (arms) | `"Drift detected (~{max:0.0}px). Press calibrate hotkey again within {seconds}s to recalibrate."` |
| Armed re-press → Persisted | `"Recalibrated successfully."` |
| Armed re-press → not-Persisted | Existing `OutcomeCategory`-routed messages |

The `AutoCalibrationTrigger` (background bundled-upgrade path) is **not** routed through `CheckDriftAsync` — it only fires when no `AutoCapture`/`UserRefinement` exists in the store (§7), so there's nothing to verify against.

## 7. `AutoCalibrationTrigger` pre-flight update

One change in [`AutoCalibrationTrigger.OnSceneChangedAsync`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs:135), at the existing pre-flight check at lines 157–161.

**Today:**

```csharp
var existing = _calibrationService.GetCalibration(scene);
if (existing is not null && existing.Source != CalibrationSource.BundledBaseline)
    return;
```

**After:**

```csharp
// Skip if the store has any UserRefinement or AutoCapture record for this
// scene. Decoupled from GetCalibration's picker: the picker may return a
// BundledBaseline when its residual+ref-count beats a stored AutoCapture,
// but the trigger's promise is "one cold solve per scene per install" and
// is store-backed, not picker-backed.
var sources = _calibrationService.GetAllSources(scene);
if (sources.Any(s => s.Source is CalibrationSource.UserRefinement
                              or CalibrationSource.AutoCapture))
    return;
```

The in-process `_persistedScenes` set stays as an in-session in-flight dedup guard — it's redundant against the store-backed check post-restart, but it eliminates the redundant `GetAllSources` call on every burst of scene-changed events within one session. `_inFlightScenes` is unchanged.

## 8. Dead-code removal

In the same PR:

| Target | Location |
|---|---|
| `AutoCalibrationEngine.CheckMonotonicAccept` method body | [`AutoCalibrationEngine.cs:676–688`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L676) |
| `MonotonicResidualRatio`, `MonotonicInlierDelta` consts | [`AutoCalibrationEngine.cs:69–70`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L69) |
| `AutoCalibrationEngine.IsSameScaleRegime` method | [`AutoCalibrationEngine.cs:656–661`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L656) |
| `ScaleRegimeRelTolerance` const | [`AutoCalibrationEngine.cs:79`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L79) |
| Existing-fit lookup + regime guard + gate body block | [`AutoCalibrationEngine.cs:465–482`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L465) (kept only the gate-accept persist at line 484+) |
| `LocatorScale = refineResult.Metrics?.Scale` stamp at accept site | [`AutoCalibrationEngine.cs:450–454`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs#L450) |
| `AreaCalibration.LocatorScale` property | [`AreaCalibration.cs:80`](../../../src/Mithril.MapCalibration/AreaCalibration.cs#L80) |
| `LocatorScale` JSON round-trip support | wherever `AreaCalibration` is serialised (Source-gen JsonSerializerContext; field disappears from the JSON model automatically when the property is removed) |
| `AutoCalibrationEngineZoomChangeRegressionTests` (entire file) | `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineZoomChangeRegressionTests.cs` |
| Monotonicity test cases in `AutoCalibrationEngineTests` | `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` (cases named `*Monotonic*` / `*RegimeGuard*`) |

JSON schema impact: `AreaCalibration.LocatorScale` is an additive nullable on a non-`SchemaVersion`-bumped record. `System.Text.Json` source-gen context silently ignores unknown JSON properties on load, so pre-#1046 user-refinement JSON files round-trip cleanly: the unknown `"LocatorScale"` property is dropped on first save, no whole-record loss. No `SchemaVersion` bump.

## 9. Logging contract

The user reports the overlay status chip may not render reliably. Until that's independently triaged, logging is the primary observability surface for the verify-and-warn flow.

All logs use the existing `ILogger` instances on `AutoCalibrationEngine` and `ManualCalibrationCoordinator`. Logger categories follow the existing `"Mithril.MapCalibration"` / `"Mithril.MapCalibration.Capture"` convention.

### 9.1 Picker (`MapCalibrationService.GetCalibration`)

| Event | Level | Template |
|---|---|---|
| Per-call decision | Trace | `"GetCalibration({MapAssetKey}): {Eligible}/{Total} eligible, picked source={Source} residual={Residual:0.00}px refs={Refs}"` |
| Fallback (no eligible) | Information | `"GetCalibration({MapAssetKey}): no candidate cleared MinReferences={Floor}; returning best-source-precedence fallback (source={Source}, residual={Residual:0.00}px, refs={Refs})"` |

Trace level on the per-call path because consumers (overlay) may call per frame.

### 9.2 DriftCheck (`AutoCalibrationEngine.CheckDriftAsync`)

| Event | Level | Template |
|---|---|---|
| Started | Information | `"Drift check starting for {MapAssetKey}: {Refs} references, tolerance factor {Factor}× of stored {Residual:0.00}px"` |
| Locator metrics | Information | `"Drift check {MapAssetKey}: locator scale={Scale:0.000}, rotation={Rot:0.00}°, inliers={Inliers}/{Cand}, locator residual={LocResid:0.00}px"` |
| Per-reference | Trace | `"Drift check {MapAssetKey}: ref '{Name}' predicted=({Px:0.0},{Py:0.0}), nearest detection=({Dx:0.0},{Dy:0.0}) at {Dist:0.00}px"` |
| Outcome — Ok | Information | `"Drift check {MapAssetKey}: OK ({Matched} refs matched, max residual {MaxResid:0.00}px, threshold {Threshold:0.00}px). No recalibration needed."` |
| Outcome — Drift | Warning | `"Drift check {MapAssetKey}: DRIFT detected ({Matched} refs matched, max residual {MaxResid:0.00}px exceeds threshold {Threshold:0.00}px). Hotkey armed for {ArmingSeconds}s — re-press to recalibrate."` |
| Outcome — Inconclusive | Information | `"Drift check {MapAssetKey}: inconclusive — {Reason} ({Matched} refs matched, need ≥{Min}). No arming."` |
| Outcome — locator/capture failure | Information | `"Drift check {MapAssetKey}: {Failure} ({Reason}). No arming; chip shows actionable reason."` |

### 9.3 `ManualCalibrationCoordinator`

| Event | Level | Template |
|---|---|---|
| Hotkey fired | Information | `"Manual calibrate hotkey: scene={MapAssetKey}, armed={IsArmed}, storedSource={Source}, storedResidualPx={Residual:0.00}, storedRefs={Refs}"` |
| Arming window expired | Information | `"Manual calibrate hotkey: drift arming window expired for {MapAssetKey} ({ArmingSeconds}s)."` |
| Confirmed recalibrate firing | Information | `"Manual calibrate hotkey: armed re-press confirmed for {MapAssetKey}; running full solve."` |

### 9.4 `AutoCalibrationTrigger`

| Event | Level | Template |
|---|---|---|
| Skipped — store has converged solve | Information | `"Auto-trigger skipped for {MapAssetKey}: store has {Source} record (residual {Residual:0.00}px, refs {Refs}). One-shot-per-install respected."` |
| Picker/store disagreement | Information | `"Auto-trigger skipped for {MapAssetKey}: store has converged solve (source={StoredSource}) but picker returned {PickedSource}. Picker chose better-quality record; trigger respects store."` |
| Firing — cold scene | Information | `"Auto-trigger firing for {MapAssetKey}: no converged solve in store; attempting cold solve (existing source: {Source})."` |

The picker/store-disagreement log is informational telemetry — useful to see how often the picker prefers a baseline over a stored auto-capture once #1046 lands; never an error.

### 9.5 Telemetry spans

Extend [`MithrilActivitySources.MapCalibration`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs) with:

- `calibration.drift_check` — wraps `CheckDriftAsync`. Tags: `map.area` (string), `refs.matched` (int), `max_residual_px` (double), `threshold_px` (double), `outcome` (string — one of `"Ok"`, `"Drift"`, `"Inconclusive"`, `"CaptureFailed"`, `"MapNotLocated"`, `"NoIconDetections"`, `"NoStoredCalibration"`).

Existing `calibration.attempt` span is unchanged. Both the cold-solve and confirmed-recalibrate solve paths emit it as today.

Update [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) with the new span shape.

## 10. Test plan

xunit + FluentAssertions per project convention. New test classes named per existing convention (`*Tests.cs`, one class per behaviour group).

### 10.1 Picker — `MapCalibrationServiceTests`

| Case | Setup | Assertion |
|---|---|---|
| `Picker_HighRefCountBaselineBeatsLowRefUserFit` | baseline(refs=8, residual=0.9px) + user(refs=2, residual=0.3px) | Returns baseline (user dropped by floor) |
| `Picker_PrefersLowerResidualAcrossSources` | baseline(refs=8, residual=2.1px) + auto(refs=5, residual=0.6px) | Returns auto |
| `Picker_TiebreaksBySourcePrecedence_UserOverAuto` | user(refs=6, residual=0.8px) + auto(refs=6, residual=0.8px) | Returns user |
| `Picker_TiebreaksBySourcePrecedence_AutoOverBaseline` | auto(refs=6, residual=0.8px) + baseline(refs=6, residual=0.8px) | Returns auto |
| `Picker_BelowFloorAcrossAll_FallsBackToSourcePrecedence` | user(refs=2, residual=0.3px) + baseline(refs=3, residual=0.5px) | Returns user (fallback path, highest source) |
| `Picker_NoCandidates_ReturnsNull` | empty store, no baseline | Returns null |
| `Picker_OnlyBaseline_ReturnsBaseline` | baseline(refs=6, residual=2.1px), no user | Returns baseline |
| `Picker_OnlyUserBelowFloor_ReturnsIt` | user(refs=2, residual=0.3px), no baseline | Returns user (fallback path, only candidate) |
| `Picker_LogsTraceOnPickAndInfoOnFallback` | FakeLogger; covers a normal pick and a fallback case | Trace logged on every pick; Information logged only on fallback |

### 10.2 DriftCheck — `AutoCalibrationEngineDriftCheckTests`

| Case | Setup | Assertion |
|---|---|---|
| `DriftCheck_NoStoredCalibration_ReturnsNoStoredCalibration` | empty store for the scene | Outcome = `NoStoredCalibration` |
| `DriftCheck_PredictedMatchesDetections_ReturnsOk` | stored(residual=0.7px), 6 fake detections placed at predicted positions ±0.5px | Outcome = `Ok`, matched=6, maxResidualPx ≈ 0.5 |
| `DriftCheck_PredictedMissesDetections_ReturnsDrift` | stored(residual=0.7px), 6 fake detections offset by 5px from predicted | Outcome = `Drift`, exceeds 3.0 × 0.7 = 2.1px |
| `DriftCheck_FewerThan3Matched_ReturnsInconclusive` | stored(residual=0.7px), only 2 detections within 20px gate | Outcome = `Inconclusive("too few visible landmarks", 2)` |
| `DriftCheck_LocatorFails_ReturnsMapNotLocated` | fake refiner returns no AcceptedRect | Outcome = `MapNotLocated(reason)` |
| `DriftCheck_CaptureFails_ReturnsCaptureFailed` | fake capture returns null Gray | Outcome = `CaptureFailed(reason)` |
| `DriftCheck_LogsExpectedSequence` | FakeLogger; an Ok case | Captures: started, locator metrics, ≥1 per-ref trace, Ok outcome |
| `DriftCheck_EmitsCalibrationDriftCheckSpan` | TestActivityListener on `MithrilActivitySources.MapCalibration` | One `calibration.drift_check` span with `outcome="Ok"` and expected tags |

### 10.3 Manual hotkey arming — `ManualCalibrationCoordinatorTests`

`TimeProvider` is the wall-clock seam (`IGameClock` is PG in-game time-of-day, not what we want for the arming window). The coordinator takes a `TimeProvider` in its ctor; tests inject the existing `FakeClock : TimeProvider` pattern used in `Mithril.Shared.Tests`, `Legolas.Tests`, and `ThrottledWarnTests`.

| Case | Setup | Assertion |
|---|---|---|
| `Hotkey_NoStoredCalibration_RunsFullSolve` | empty store | `IAutoCalibrationRunner.TryCalibrateCurrentAreaAsync` called; `CheckDriftAsync` not called |
| `Hotkey_DriftOk_DoesNotArmDoesNotSolve` | stored cal, fake DriftCheck → Ok | Neither arming nor solve; chip set to OK message |
| `Hotkey_Drift_ArmsAndSetsChip` | stored cal, fake DriftCheck → Drift | Coordinator armed; chip set to drift message |
| `Hotkey_ArmedRePressWithinWindow_RunsFullSolve` | armed; clock advances 5s; re-press | `TryCalibrateCurrentAreaAsync` called; coordinator disarmed |
| `Hotkey_ArmedRePressAfterWindow_RunsDriftCheckAgain` | armed; clock advances 11s; re-press | `CheckDriftAsync` called (not the solve); prior arming dropped; new check evaluated |
| `Hotkey_LogsArmedAndExpired` | FakeLogger; drift → wait → expire → next press | Captures Drift arming log + expiration log + next-press's fresh check |
| `Hotkey_NoBboxOrPgNotForeground_SurfacesActionableChip` | bbox unset | Chip set to existing "no bbox" reject reason; no DriftCheck call |

### 10.4 `AutoCalibrationTrigger` — extend `AutoCalibrationTriggerTests`

| Case | Setup | Assertion |
|---|---|---|
| `Trigger_StoreHasUserRefinement_Skips` | `GetAllSources` returns one `UserRefinement` | Engine not invoked; "store has UserRefinement record" log emitted |
| `Trigger_StoreHasAutoCapture_Skips` | `GetAllSources` returns one `AutoCapture` | Engine not invoked; "store has AutoCapture record" log emitted |
| `Trigger_StoreOnlyHasBundledBaseline_Fires` | `GetAllSources` returns only `BundledBaseline` | Engine invoked |
| `Trigger_StoreEmpty_Fires` | `GetAllSources` returns empty | Engine invoked |
| `Trigger_PickerReturnsBaselineButStoreHasAuto_Skips` | `GetAllSources` returns both auto + baseline; picker prefers baseline | Engine not invoked; the picker/store-disagreement log emitted |

### 10.5 Deletion verification

- Build: solution compiles with `AutoCalibrationEngine.CheckMonotonicAccept`, `IsSameScaleRegime`, `MonotonicResidualRatio`, `MonotonicInlierDelta`, `ScaleRegimeRelTolerance`, `AreaCalibration.LocatorScale` all removed.
- JSON round-trip: existing `UserRefinementStore` round-trip test re-runs against a fixture containing a pre-#1046 record with `"LocatorScale": 0.762` to confirm the unknown property is silently ignored on load and not re-emitted on save.
- `AutoCalibrationEngineZoomChangeRegressionTests` and `*Monotonic*` / `*RegimeGuard*` cases in `AutoCalibrationEngineTests` are removed in the same PR.

## 11. Out of scope

- Bootstrap autocal mechanics (#914 owns).
- Per-frame locator efficiency / pan-recovery on featureless areas (separate issue when the live-overlay rewrite lands).
- New community-sync ingest semantics (#871 territory).
- Periodic background drift spot-check (D1.B): land after telemetry from D1.A shows whether users miss real drift in practice.
- Continuous per-frame drift piggyback (D1.C): depends on the live-overlay rewrite.
- Inbox-driven drift surfacing: depends on the planned inbox feature; the chip + arming idiom in §6 is the v1 surface.
- Overlay status chip render reliability: separate triage issue (verification owed below).

## 12. Verification owed

| Item | Why |
|---|---|
| Overlay status chip render reliability | User-reported during 2026-06-03 brainstorm: chips may not appear on screen consistently. The logging contract in §9 is the primary observability surface for the verify-and-warn flow until this is triaged. Filing a separate issue to triage the chip's render path. |
| Picker telemetry on real corpora | Once the picker change lands, the picker/store-disagreement log line (§9.4) is the canary for "how often does the picker prefer a baseline over a stored auto-capture?" Useful for tuning `MinReferences` if the floor turns out wrong. |
| Drift-tolerance factor calibration | `DriftToleranceFactor = 3.0` is a defensible starting guess (drift is rare; false-positive drift chip is benign because user re-presses). Tune after telemetry from §9.2's Warning-level drift log shows the false-positive rate in practice. |

## 13. Adjacent issues

- [#914](https://github.com/moumantai-gg/mithril/issues/914) — autocal engine umbrella; this spec re-scopes its runtime ambitions.
- [#1041](https://github.com/moumantai-gg/mithril/issues/1041) — calibration consumer migration; this spec assumes the `MapSceneRef` surface it shipped.
- [#988](https://github.com/moumantai-gg/mithril/issues/988) — monotonicity gate (retired by this spec).
- [#1005](https://github.com/moumantai-gg/mithril/issues/1005) — `LocatorScale` regime guard (retired by this spec).
- [#871](https://github.com/moumantai-gg/mithril/issues/871) — calibration store lift; the picker change is consumer-visible on that surface.
- [#830](https://github.com/moumantai-gg/mithril/issues/830) — `Mithril.MapAssets` substrate; MapView consumer is the companion issue, consuming the same stored `AreaCalibration` per §4.

— drafted by Claude (Opus 4.7), posted by @arthur-conde
