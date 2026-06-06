# Calibration-consumer logging pass — spec

**Issue:** [mithril#1093](https://github.com/moumantai-gg/mithril/issues/1093). **Status:** active. **Branch posture:** docs-only PR (`claude/calibration-logging-plan`) against `main`; the implementation lands in a follow-up PR this spec drives.

## 1. Problem

The calibration consumer chain — the path from "user toggles validation" through "pink dot lands on the overlay" — is largely unlogged. A regression investigation on Mithril `3.0.0.75+743fd96be3` (main as of [#1092](https://github.com/moumantai-gg/mithril/pull/1092)) hit a wall because every link in the chain emits nothing on the silent-failure path. Root cause (a texture-frame-only record returning null from `CurrentOverlayCalibration` for a scene that should resolve via the post-[#1081](https://github.com/moumantai-gg/mithril/issues/1081) composed-cal path) was diagnosable only by reading code. With the right log lines, it would have shown up in 30 seconds of trace inspection.

The reference standard is the auto-calibration engine. [`AutoCalibrationEngine`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) instruments every stage with `ILogger` (named-property message templates), per-stage `ActivitySource` spans (`calibration.attempt` → `calibration.capture` → `calibration.refine` → `calibration.solve`), and outcome tags. The consumer chain has nothing comparable.

## 2. Scope

In scope:

- **Picker:** `MapCalibrationService.PickByFrame` (the silent frame-typed dispatcher feeding `GetTextureCalibration` / `GetOverlayCalibration`).
- **AreaCalibrationService lifecycle:** `SelectScene`, `OnMapCalChanged`, `CalibrateCurrentArea`, `ClearCurrentAreaCalibration`.
- **VM-side projection paths** in `MapOverlayViewModel`: `RebuildCalibrationGhosts`, `MotherlodeMarkerPixels`, `MotherlodeGuidanceOverlay`, `OnCalibrationChanged`, `SetCalibrationValidation`, `RefreshSurveyPlayerAnchor` (and any other consumer of `_areaCalibration.CurrentOverlayCalibration`).
- **Survey-pin projection** in `PlayerLogIngestionService.HandleMapTarget` (the `ProcessMapFx` drop path).
- **Wizard projection** in `CalibrationSessionViewModel.ProjectLandmarks`.
- **Drawer state transitions** in `LegolasOverlaySceneDrawer.DrawCalibrationGhosts` (plumb `ILogger` through `LegolasOverlayDrawerHostedService`).

Out of scope (called out in §13):

- The auto-calibration engine itself — already well-instrumented; no changes.
- Behaviour rewrites — no migration of `CurrentOverlayCalibration` consumers to the composed-cal path, no marker/motherlode/ghost behavior changes. This pass is logging only.
- New diagnostics UI / settings surfaces — additions land in the existing log file + perf-trace harness.

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **Match the AutoCal pattern: logs + spans + meters.** | User-selected during brainstorming. AutoCal is the standard the brief points at; consumer chain should be triageable to the same depth. |
| D2 | **One PR.** | Six files touched, but they form one cohesive layer — splitting risks landing categories without their consumers, or vice versa, and the diff stays under ~500 LoC. The implementation plan is ordered TDD-task-by-task so a reviewer can read tasks 1→N as a coherent sequence. |
| D3 | **New category `Legolas.Calibration`** for `AreaCalibrationService`. Existing `Legolas.MapOverlay` keeps the VM projection paths. New `Legolas.Overlay.GhostDrawer` for the drawer's ghost pass. `Mithril.MapCalibration` (existing) keeps the picker. | Mirrors the existing category vocabulary — `Legolas.MapOverlay` already covers the VM (line 81 of [`MapOverlayViewModel.cs`](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs)), `Legolas.Overlay` is taken by `LegolasOverlayDrawerHostedService`. Splitting the ghost pass into its own category lets the diagnostics-UI filter throttle the ~60 Hz transition stream independently. |
| D4 | **Generalise `MapOverlayViewModel.LogCalibrationFallback(areaKey, reason)`** (line ~1140) for VM null-cal early-returns. The helper exists with the right dedup shape but its current message template is hardcoded for the wizard-marker call site (`"MapOverlayViewModel.RefreshCalibrationMarker fallback for area {AreaKey}: {Reason}"`). Add a `string callSite` parameter so the template can name the actual method, then call from every projection path. | The helper was added by #835 review iteration B2 for the wizard-marker path and uses the canonical `_projectionMissAreasLogged`-style `ConcurrentDictionary` dedup. The brief independently proposes "a shared helper that uniformly logs the null-skip reason" — the helper exists but needs a one-line generalisation, not a rewrite. |
| D5 | **No per-frame logging.** The drawer logs only state transitions (`ShowCalibrationGhosts` flips, `ghosts.Count` crosses 0, brush-null becomes brush-non-null). Frame-rate counts go to a `Histogram`/`Counter`, not `ILogger`. | Per CLAUDE.md "Instrumentation" bullet: logs answer "what happened", meters answer "how often / how shape". Per-frame `ILogger` would flood. |
| D6 | **Parent span = `calibration.attempt` (engine path) OR `calibration.consumer.<op>` (consumer path).** Consumer spans live on a new `MithrilActivitySources.LegolasCalibration` source, do NOT nest under engine spans (different driver thread, different lifetime). | The engine's `calibration.attempt` span covers one solve attempt; consumer projection happens asynchronously on the UI thread later. Forcing one span tree means correlation-id plumbing that costs more than it buys. |
| D7 | **Toggle is the lifecycle anchor.** `SetCalibrationValidation(true|false)` always logs at `Information` with `{Area, Scene, IsCalibrated, OverlayCalibrationPresent, GhostsBuilt}`. | The brief's load-bearing observation: when a user reports "I clicked validate and nothing happened," the toggle is the first log entry the triager grep's for. It must carry enough context to drive triage without further code reading. |
| D8 | **Drawer takes `ILogger?` via constructor.** Wired through `LegolasOverlayDrawerHostedService` (the existing host, already on category `Legolas.Overlay`). | The drawer is constructed in the host; threading the logger from there keeps DI changes localised and gives the host visibility into whether the drawer is being instantiated at all. |
| D9 | **No new defensive tests for null-cal log fires.** The logging is its own assertion — if it disappears, the diff is the test. Implementation tasks add ONE high-level test (a scene-switch wizard fake exercising `AreaCalibrationService.SelectScene` → `RebuildCalibrationGhosts` and asserting the `Legolas.MapOverlay` category emits the rebuild line) so the wiring doesn't silently break. | Per CLAUDE.md "don't write speculative guards." The brief raised this as "or is overkill?" — it is. The OTLP/perf-recorder exporter is the runtime check; one shape test is enough. |
| D10 | **Fix `PlayerLogIngestionService` DI wiring** as part of Task 5 (was: a side-note). Convert its ctor from `ILogger? logger = null` to `ILoggerFactory? loggerFactory = null` + `_logger = loggerFactory?.CreateLogger("Legolas.Ingestion")` inside. | Grounding the spec against the live registration at [`LegolasModule.cs:279`](../../../src/Legolas.Module/LegolasModule.cs#L279) (`AddHostedService<PlayerLogIngestionService>()`) revealed the existing `ILogger? _logger` is **always null in production today** — DI registers `ILogger<T>` and `ILoggerFactory`, never the non-generic `ILogger`. Today's `_logger?.LogInformation("Subscribed to Arda domain events")` at line 98 is dead code. Without this fix, every new log in Task 5 silently no-ops. |
| D11 | **Tag descriptors land in a NEW Legolas-owned provider**, not in `MithrilSharedTagDescriptors`. Create [`src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs`](../../../src/Legolas.Module/Diagnostics/) implementing `ITagDescriptorProvider`; register via DI in `LegolasModule.Register`. **Corrected during Task 0 implementation**: `TagCatalog` dedups by `TagDescriptor.Key` alone and **throws at startup** on any field difference (classification, subsystem, description) — see [`TagCatalog.cs:35-42`](../../../src/Mithril.Shared.Telemetry/Catalog/TagCatalog.cs#L35). Tag keys are **global** across the catalog; the `Subsystem` field is documentation/context, not a discriminator. Keys already declared by another provider (`outcome` by `MithrilSharedTagDescriptors` scoped to `Mithril.Reference`) are NOT re-declared — the existing row covers all producers whose use shares the classification. Original wording "every (key, scope) pair is its own row" was wrong. | `MithrilSharedTagDescriptors` lives in `Mithril.Shared`, which cannot reference `Legolas.Module` — layering violation. The Legolas provider declares each Legolas-specific key once. `outcome` is the documented intentional omission (covered by the existing `Mithril.Reference` row, same `Safe` classification). If a future producer needs a different classification or description for an already-declared key, that's a catalog-level conversation, not a Legolas-side patch. |

## 4. Telemetry catalog additions (the inventory)

### 4.1 `ILogger` categories

| Category | Owner | New / Existing |
|---|---|---|
| `Mithril.MapCalibration` | `MapCalibrationService` (incl. new `PickByFrame` log sites) | existing — no change |
| `Legolas.Calibration` | `AreaCalibrationService` | **new** |
| `Legolas.MapOverlay` | `MapOverlayViewModel` projection paths + toggle | existing — extended |
| `Legolas.Overlay.GhostDrawer` | `LegolasOverlaySceneDrawer.DrawCalibrationGhosts` (state transitions only) | **new** |
| `Legolas.Ingestion` | `PlayerLogIngestionService.HandleMapTarget` null-cal skip | **new** (distinct from existing `Legolas` / `Legolas.Motherlode` so the survey-pin drop path filters separately) |

DI wiring lives in [`LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs), [`MapCalibrationServiceCollectionExtensions.cs`](../../../src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs), and [`LegolasOverlayDrawerHostedService.cs`](../../../src/Legolas.Module/Rendering/LegolasOverlayDrawerHostedService.cs).

### 4.2 `ActivitySource` additions

In [`MithrilActivitySources`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs):

```csharp
/// <summary>Legolas calibration consumer chain — AreaCalibrationService lifecycle +
/// VM projection paths (#1093). Sibling-not-child of MapCalibration: consumer-side
/// projection runs on the UI thread asynchronously to the engine's solve attempt,
/// so consumer spans don't nest under calibration.attempt.</summary>
public static readonly ActivitySource LegolasCalibration = new("Mithril.Legolas.Calibration");
```

### 4.3 `Meter` + instruments

**Layering note (discovered during Task 1):** `Mithril.MapCalibration.csproj` deliberately doesn't reference `Mithril.Shared` (the long-standing pattern Arda follows too). The picker (`MapCalibrationService.PickByFrame`) therefore can't emit through `MithrilMeters.LegolasCalibration.PickerOutcomes` directly. Instead, the `PickerOutcomes` instrument is declared in [`Mithril.MapCalibration.Diagnostics.MapCalibrationDiagnostics.LegolasCalibrationPickerMeter`](../../../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs) with the **same `Meter` name** (`"Mithril.Legolas.Calibration"`) and the **same instrument name** (`"mithril.legolas.calibration.picker.outcomes"`). `MeterListener`s subscribing by name see both producers transparently. The consumer-side instruments below (`ProjectionSkipped`, `GhostDrawerTransitions`, `GhostsRebuildMs`) stay in `Mithril.Shared` because their producers live in `Legolas.Module` which DOES reference `Mithril.Shared`.

In [`MithrilMeters`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs):

```csharp
public static class LegolasCalibration
{
    public static readonly Meter Meter = new("Mithril.Legolas.Calibration");

    // PickerOutcomes lives in MapCalibrationDiagnostics due to layering (see note above).

    /// <summary>VM-side projection paths skipped because CurrentOverlayCalibration
    /// returned null. Tags: consumer ∈ {ghosts, motherlode_markers, motherlode_guidance,
    /// survey_pin, survey_anchor, wizard_landmarks}; area (scene MapAssetKey).</summary>
    public static readonly Counter<long> ProjectionSkipped =
        Meter.CreateCounter<long>("mithril.legolas.calibration.projection.skipped");

    /// <summary>Drawer ghost-pass state transitions. Tags: from, to ∈ {hidden, empty, drawing}.</summary>
    public static readonly Counter<long> GhostDrawerTransitions =
        Meter.CreateCounter<long>("mithril.legolas.calibration.ghost_drawer.transitions");

    /// <summary>RebuildCalibrationGhosts wall-clock. Tags: area, refs_count, ghosts_built.</summary>
    public static readonly Histogram<double> GhostsRebuildMs =
        Meter.CreateHistogram<double>("mithril.legolas.calibration.ghosts.rebuild_ms", unit: "ms");
}
```

### 4.4 Tag-key inventory + privacy

New tag keys introduced by this pass: `area`, `scene.asset_key`, `scene.parent_area_key`, `cal.source`, `cal.frame`, `cal.residual_px`, `cal.refs`, `cal.path` (already exists), `consumer`, `outcome`, `frame`, `refs_count`, `ghosts_built`, `from`, `to`, `placements` (added during Task 2 — `CalibrateCurrentArea` span). The `cal.source` value vocabulary is the live `AreaCalibration.Source` enum (`UserRefinement` / `AutoCapture` / `BundledBaseline` / `CommunitySync`), not the conceptual short-form. Update the descriptor description in Phase 3 docs.

All are **Safe** classification (no PII, no path-shaped strings). Descriptor entries are scope-keyed: each (tag key, source/meter scope) pair is its own row in an `ITagDescriptorProvider`. The existing `outcome` and `kind` entries in [`MithrilSharedTagDescriptors`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilSharedTagDescriptors.cs) are scoped to `Mithril.Reference` / `Mithril.Wpf` respectively — they are NOT global. Per D11, the new keys live in a new `LegolasCalibrationTagDescriptors` provider (Legolas-owned project) scoped to `"Mithril.Legolas.Calibration"`. Until they're promoted, the OTLP exporter drops them fail-closed and surfaces them in Settings "Newly seen" (per CLAUDE.md telemetry bullet). Task 0 creates the provider and registers it via DI.

## 5. Per-site call shapes

Each site lists: **category · level · message template · properties · companion span / meter** (when D1 applies).

### 5.1 `MapCalibrationService.PickByFrame` (the picker)

**Location:** [`MapCalibrationService.cs:140`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs#L140).

Today `GetCalibration` traces every pick with eligible/total + source + residual + refs (lines 62–72). `PickByFrame` is silent — it's the load-bearing path for #1077/#1081 cross-frame compose and every overlay-side consumer.

**Match the existing `GetCalibration` shape exactly:**

```csharp
// Hit (eligible >= 1)
_logger?.LogTrace(
    "PickByFrame({MapAssetKey}, frame={Frame}): {Eligible}/{Total} eligible, picked source={Source} residual={Residual:0.00}px refs={Refs}.",
    scene.MapAssetKey, frame, eligible.Count, candidates.Count, picked.Source, picked.ResidualPixels, picked.ReferenceCount);

// Below-floor fallback
_logger?.LogInformation(
    "PickByFrame({MapAssetKey}, frame={Frame}): no candidate cleared MinReferences={Floor}; returning best-source-precedence fallback (source={Source}, residual={Residual:0.00}px, refs={Refs}).",
    scene.MapAssetKey, frame, MinReferences, picked.Source, picked.ResidualPixels, picked.ReferenceCount);

// Miss (candidates.Count == 0) — NEW vs GetCalibration which silently returns null
_logger?.LogTrace(
    "PickByFrame({MapAssetKey}, frame={Frame}): no candidates (user-store {UserSlot}, baseline {BaselineFrame}).",
    scene.MapAssetKey, frame, userSlotState, baselineFrameState);
```

**Meter:** `PickerOutcomes.Add(1, { frame, outcome })` on every return — including miss. This is the "how often does PickByFrame return null in production" question the brief raises.

**No span.** `PickByFrame` is called O(many) times per session (every VM property that reads `CurrentOverlayCalibration`); a span per call would burn allocation. The meter counter covers shape.

### 5.2 `AreaCalibrationService` (lifecycle)

**Location:** [`AreaCalibrationService.cs`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs). Takes no `ILogger` today; constructor gains `ILogger? logger = null` parameter, wired via [`LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs) with category `"Legolas.Calibration"`.

| Method | Level | Template |
|---|---|---|
| `SelectScene` | Information | `"SelectScene → {MapAssetKey} (parent={ParentArea}, friendly={SceneFriendlyName}): {RefCount} refs, cal {CalState} (source={Source}, residual={Residual:0.00}px, frame={Frame})."` — `CalState` ∈ `none` / `applied`. |
| `OnMapCalChanged` (match) | Trace | `"OnMapCalChanged({MapAssetKey}): re-applied cal (source={Source}, residual={Residual:0.00}px, frame={Frame})."` |
| `OnMapCalChanged` (dropped — different scene) | Trace | `"OnMapCalChanged({PayloadKey}): dropped, current scene is {CurrentKey}."` (so a Changed-event flood with the wrong key is visible) |
| `CalibrateCurrentArea` (success) | Information | `"CalibrateCurrentArea({MapAssetKey}): solved {PlacementCount} placements at zoom={Zoom}; residual={Residual:0.00}px frame=Overlay refs={Refs}."` |
| `CalibrateCurrentArea` (refused) | Information | `"CalibrateCurrentArea: refused — no current scene or <2 placements ({PlacementCount} given)."` |
| `CalibrateCurrentArea` (solver returned null) | Warning | `"CalibrateCurrentArea({MapAssetKey}): solver returned no fit for {PlacementCount} placements."` |
| `ClearCurrentAreaCalibration` | Information | `"ClearCurrentAreaCalibration({MapAssetKey}): user requested clear; re-broadcast via mapCal.Changed."` |

**Span:** `LegolasCalibration.StartActivity("calibration.area.select_scene")` over `SelectScene` body. Tags: `scene.asset_key`, `scene.parent_area_key`, `refs_count`, `cal.applied` (bool), `cal.source`, `cal.residual_px`.

**Span:** `LegolasCalibration.StartActivity("calibration.area.calibrate_current")` over `CalibrateCurrentArea`. Tags: `scene.asset_key`, `placements`, `outcome` ∈ `solved|refused|no_fit`, `cal.residual_px` (on solve).

### 5.3 `MapOverlayViewModel` (VM projection paths)

**Category:** `Legolas.MapOverlay` (existing). Skip-path sites use the generalised `LogCalibrationFallback(areaKey, callSite, reason)` helper at [`MapOverlayViewModel.cs:1140`](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs#L1140) (per D4) for the per-`(area, reason)` first-time-Trace pattern, and `MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1, { consumer, area })` every call (the meter is the "how often" answer; the log gives one human-readable explanation per scene).

**Method frequency notes** — `MotherlodeMarkerPixels` and `MotherlodeGuidanceOverlay` are property getters consumed inside `LegolasOverlaySceneDrawer.BuildScene` (drawer line ~267), which runs every render tick (~60 Hz). Success-path logging would flood. `RebuildCalibrationGhosts` and `OnCalibrationChanged` are state-change frequency (fire on toggle, area-change, or recalibrate) — safe to log at Information.

| Method | Frequency | Success log | Skip path |
|---|---|---|---|
| `RebuildCalibrationGhosts` | state-change | **Information** — `"RebuildCalibrationGhosts({Area}): built {Ghosts} from {Refs} refs at zoom={Zoom} (cal source={Source}, residual={Residual:0.00}px)."` | `LogCalibrationFallback(area, "RebuildCalibrationGhosts", "no_overlay_cal")` + `ProjectionSkipped.Add(1, {consumer:"ghosts", area})` |
| `MotherlodeMarkerPixels` (getter) | per-frame | **none** — meter only | `LogCalibrationFallback(area, "MotherlodeMarkerPixels", "no_overlay_cal")` + `ProjectionSkipped.Add(1, {consumer:"motherlode_markers", area})` |
| `MotherlodeGuidanceOverlay` (getter) | per-frame | **none** — meter only | `LogCalibrationFallback(area, "MotherlodeGuidanceOverlay", "no_overlay_cal")` + `ProjectionSkipped.Add(1, {consumer:"motherlode_guidance", area})` |
| `OnCalibrationChanged` | state-change | **Information** — `"OnCalibrationChanged({Area}): IsCalibrated={IsCalibrated} ShowGhosts={ShowGhosts} → {Action}."` (`Action` ∈ `drop_validation|rebuild|noop`) | — |
| `SetCalibrationValidation` | user-action | **Information** (D7 anchor) — `"SetCalibrationValidation(on={On}, area={Area}, scene={Scene}, isCalibrated={IsCalibrated}, overlayCalPresent={OverlayCalPresent}): {Action} → ghostsBuilt={GhostsBuilt}."` | — |

**Span:** `LegolasCalibration.StartActivity("calibration.ghosts.rebuild")` over `RebuildCalibrationGhosts` body. Tags: `area`, `refs_count`, `ghosts_built`, `cal.source`, `cal.residual_px`, `cal.path` ∈ `direct_overlay|none`. Companion histogram `GhostsRebuildMs` records the wall-clock.

**No spans** on the per-frame getters — wrapping a getter in `StartActivity` 60×/sec is exactly the kind of per-frame cost the perf-recorder is designed to surface, not produce. The `ProjectionSkipped` counter is the "how often" answer for getter paths.

### 5.4 `LegolasOverlaySceneDrawer.DrawCalibrationGhosts` (the drawer)

**Location:** [`LegolasOverlaySceneDrawer.cs:147`](../../../src/Legolas.Module/Rendering/LegolasOverlaySceneDrawer.cs#L147). **Category:** new `Legolas.Overlay.GhostDrawer`.

Three early-return branches today: `ShowCalibrationGhosts==false`, `ghosts.Count==0`, `brush==null`. State machine:

```
hidden    ⇄ empty (ShowCalibrationGhosts toggles)
empty     ⇄ drawing (ghosts.Count crosses 0)
drawing  → brush_null (brush fetch returned null — degraded; rare; logged at Warning)
```

| Transition | Level | Template |
|---|---|---|
| `hidden → empty` (ShowCalibrationGhosts went true, ghosts.Count == 0) | Trace | `"DrawCalibrationGhosts: shown but empty — VM didn't rebuild any ghosts."` |
| `empty → drawing` (ghosts.Count crossed 0→N) | Trace | `"DrawCalibrationGhosts: drawing {Count} ghost(s)."` |
| `drawing → empty` | Trace | `"DrawCalibrationGhosts: ghost set cleared."` |
| `drawing → hidden` | Trace | `"DrawCalibrationGhosts: hidden by toggle."` |
| any → `brush_null` | Warning (throttled — first-occurrence per session) | `"DrawCalibrationGhosts: brush fetch returned null; ghost pass skipped this frame."` |

State is held in two `int?` fields on the drawer (`_lastShownState`, `_lastGhostCountBucket`). The transition check is integer compare on the hot path; no allocation. Per-frame counter `GhostDrawerTransitions.Add(1, { from, to })` on every transition.

### 5.5 `PlayerLogIngestionService.HandleMapTarget`

**Location:** [`PlayerLogIngestionService.cs:191`](../../../src/Legolas.Module/Services/PlayerLogIngestionService.cs#L191). **Category:** new `Legolas.Ingestion` (distinct from existing `"Legolas"` startup category so the survey-pin-drop signal is filterable).

| Path | Level | Template |
|---|---|---|
| Survey-pin placed | Information | `"HandleMapTarget {Name}@({X:0},{Z:0}): placed at overlay ({Px:0},{Py:0}) (cal source={Source}, residual={Residual:0.00}px)."` |
| Mode != Survey | Trace | `"HandleMapTarget {Name}@({X:0},{Z:0}): ignored, mode is {Mode}."` |
| Flow not Listening/Gathering | Trace | `"HandleMapTarget {Name}@({X:0},{Z:0}): ignored, flow is {Flow}."` |
| No overlay cal | Information | `"HandleMapTarget {Name}@({X:0},{Z:0}) {Area}: dropped — area not calibrated."` + meter `ProjectionSkipped.Add(1, { consumer: "survey_pin", area })` |

(`_session.LastLogEvent` already gets set in all paths today — the new logs are additive, not a UI-text change.)

### 5.6 `CalibrationSessionViewModel.ProjectLandmarks`

**Location:** [`CalibrationSessionViewModel.cs:533`](../../../src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs#L533). **Category:** existing `Legolas.CalibrationSession`.

| Path | Level | Template |
|---|---|---|
| Success | Information | `"ProjectLandmarks: projected {Count} ghost pins from {Refs} refs ({Skipped} already placed)."` |
| No cal | Information | `"ProjectLandmarks: refused — no overlay calibration; UI surfaced 'Solve a calibration first'."` + meter `ProjectionSkipped.Add(1, { consumer: "wizard_landmarks", area })` |

## 6. Privacy / OTLP scrubbing

Every new tag value above is:

- A typed enum / fixed-vocabulary string (`outcome`, `frame`, `consumer`, `from`/`to`, `cal.source`) — Safe.
- A scene/area key that's the literal Unity asset name (`Map_AreaSerbule`, `Map_HogansKeepBasement`) — Safe; no PII / no path.
- A numeric (residual, pixel, count, ms) — Safe.

No tag value carries `%USERPROFILE%`, `%LocalAppData%`, character name, server name, or user-typed strings. The `ValueRedactor` still runs but has nothing to redact.

**Task 0 (descriptor promotion):** add the new keys (`scene.asset_key`, `scene.parent_area_key`, `cal.source`, `cal.frame`, `cal.residual_px`, `cal.refs`, `consumer`, `outcome` for new contexts, `frame`, `refs_count`, `ghosts_built`, `from`, `to`) to the local tag-descriptor file for the `Mithril.Legolas.*` namespace. Existing globally-promoted keys (`area`) don't need re-declaring.

## 7. Test strategy (D9)

One **shape test** added to [`tests/Legolas.Tests/ViewModels/`](../../../tests/Legolas.Tests/ViewModels/) (the actual test project — not `Legolas.Module.Tests`). The existing [`MapOverlayCalibrationFallbackDedupTests.cs`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationFallbackDedupTests.cs) and [`MapOverlayCalibrationValidationTests.cs`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationValidationTests.cs) demonstrate the VM-construction fixture pattern — extend or sibling-from them. The 14-arg `MapOverlayViewModel` ctor is non-trivial, so reusing an existing fixture is load-bearing for keeping the test tractable.

- Wire `AreaCalibrationService` + a stub `MapCalibrationService` + a stub `MapOverlayViewModel` (or just exercise the helper directly).
- Drive `SelectScene` → assert one `Legolas.Calibration` Information-level record matching the template.
- Drive `RebuildCalibrationGhosts` with a null `CurrentOverlayCalibration` → assert one `Legolas.MapOverlay` Information record via `LogCalibrationFallback` with `reason == "ghosts.no_overlay_cal"`.
- Drive `RebuildCalibrationGhosts` with a non-null cal and 3 refs → assert one Information record with `ghosts.built == 3`.

Captured via whatever `ILogger` capture pattern [`MapOverlayCalibrationFallbackDedupTests`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationFallbackDedupTests.cs) already uses — that test asserts dedup behavior on the existing `LogCalibrationFallback` helper, so it must capture log records somehow. Task 1 reads that fixture first and reuses its pattern. (`FakeLogger` from `Microsoft.Extensions.Logging.Testing` is not in use anywhere under `tests/`; the dedup test is the canonical capture pattern.)

No per-site test. Per CLAUDE.md "don't write speculative guards": if a future change drops a log line, the next investigation re-files the issue, not the test suite catching it.

## 8. Phasing (D2)

**One PR.** Implementation plan tasks (see [`plan.md`](plan.md)) are ordered so each task is reviewable in isolation:

- Task 0: telemetry catalog (sources, meters, tag descriptors). No callers yet — zero behavior change.
- Tasks 1–5: per-site logging in the order picker → service → VM → drawer → ingestion/wizard. Each task touches one file and adds its tests.
- Task 6: documentation — append to [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) the new tag inventory + a one-paragraph "consumer chain" reading guide; update CLAUDE.md "Logging" bullet category list.

A reviewer reading the PR in task order sees the vocabulary land before any consumer uses it, and each consumer site lands with its own commit (per the project convention of frequent commits — [collaboration_style](../../../../memory/collaboration_style.md)).

## 9. Out-of-scope follow-ups (recommended new issues)

The brainstorming surfaced two cleanups that are NOT in this pass but should land as separate issues:

- **Migrate VM projection paths from `_areaCalibration.CurrentOverlayCalibration` to the composed-cal path used by [`OverlayWindowService`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs).** This is the structural fix for the symptom that triggered #1093 — texture-frame-only records currently return null from `CurrentOverlayCalibration` and silently drop projections, while the post-#1081 composed-cal path resolves them via `ProjectThroughOverlay(MapRect)` + `IMapTextureDimensions`. New issue title: *"Migrate VM projection paths to composed-cal (parity with OverlayWindowService post-#1081)."* This pass's telemetry will surface the gap in production traces — that data informs the cutover plan.
- **CLAUDE.md category list.** CLAUDE.md mentions `"Arda.Player"`, `"Reference"`, `"Samwise"` as examples — it does not maintain a comprehensive list. Add one if the project wants a canonical inventory. New issue title: *"CLAUDE.md: canonical ILogger category inventory."*

Neither blocks this pass.

## 10. Verification owed

| Claim | How to verify |
|---|---|
| `LogCalibrationFallback` helper at [`MapOverlayViewModel.cs:1140`](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs#L1140) is reusable as-is (signature matches the projection-path call sites). | Read the helper body during Task 3; if its dedup key includes a context the projection paths don't have, extend the helper or carve a sibling. |
| `MithrilSharedTagDescriptors` is the right place for these tag keys (confirmed via grep: `src/Mithril.Shared.Telemetry/` is the descriptor owner; `tests/Mithril.Shared.Tests/Telemetry/MithrilSharedTagDescriptorsTests.cs` is the parity test). Confirm the declaration shape matches by reading the descriptor file as Task 0 step 1. | Read the file as the first step of Task 0; the PR for #815 / #840 / #841 lays out the descriptor format. |
| `LogCalibrationFallback` generalisation (D4) doesn't break the existing `RefreshCalibrationMarker` caller — and the existing [`MapOverlayCalibrationFallbackDedupTests`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationFallbackDedupTests.cs) still passes after the signature change. | Task 3 step 1 updates the helper signature, the existing caller, the existing test, and adds a unit test for the new call sites. |
| `RefreshSurveyPlayerAnchor` (line ~326) calls `_areaCalibration?.CurrentOverlayCalibration` and is wired to `PlayerPositionChanged` + `_areaCalibration.Changed` + `_characterPin.Changed` subscriptions. Memory ([pg_log_timezones](../../../../memory/pg_log_timezones.md) etc.) and the PG signals wiki say `PlayerPositionChanged` is **sparse** (zone-in / teleport only) — not per-frame. So Information-level logging is safe here. | Confirm during Task 3 step 6 by reading the bus emission cadence — if positions fire more often than expected (rare zone, sticky character pin spam), demote to Trace + use `LogCalibrationFallback`. |
| The drawer's `_lastShownState`/`_lastGhostCountBucket` integer fields cost nothing on the hot path (Task 4). | Compile the drawer; check the IL / JITted code is two field-reads + one branch per frame. (Almost certainly free — but worth a quick look, since this is the only per-frame addition.) |
| **Phase 2 review carry-forward.** `RebuildCalibrationGhosts` + `RefreshSurveyPlayerAnchor` Information-log success paths fire from the `CurrentMapZoom` PropertyChanged handler (`MapOverlayViewModel.cs:215-237`) while `ShowCalibrationGhosts` is on. Dragging the zoom slider could produce many Information records per second. Spec §5.3 classifies these as "state-change frequency" (not per-frame), but real perf-trace recording during a slider-drag stress test should confirm — if chatty, demote to Trace or throttle. | Post-merge: run a perf-trace session, drag the zoom slider with ghosts visible for ~5 seconds, inspect the resulting `mithril-*.json` for Information log volume on those two categories. |
