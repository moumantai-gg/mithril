# Calibration-consumer logging pass — plan

**Spec:** [`spec.md`](spec.md). **Issue:** [mithril#1093](https://github.com/moumantai-gg/mithril/issues/1093). **Branch:** `claude/calibration-logging` (new feature branch — distinct from the docs-only branch this plan lands on).

One PR, seven tasks, ordered so each commit reads independently. Vocabulary lands first; consumer sites land in pipeline order (entry → service → VM → drawer → ingestion → wizard); docs land last.

## Task 0 — Telemetry catalog + tag descriptors

**Files:** [`src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs), [`MithrilMeters.cs`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs), NEW [`src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs`](../../../src/Legolas.Module/Diagnostics/), [`src/Legolas.Module/LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs).

**Steps:**

1. **Read [`MithrilSharedTagDescriptors`](../../../src/Mithril.Shared/Diagnostics/Telemetry/MithrilSharedTagDescriptors.cs) for the format.** It's an `ITagDescriptorProvider` with `(tag_key, PiiClassification, source_or_meter_scope, description)` rows — scope-keyed, NOT global.
2. Add `LegolasCalibration` source to `MithrilActivitySources` per spec §4.2.
3. Add `MithrilMeters.LegolasCalibration` static (one `Counter<long>` for `PickerOutcomes`, one `Counter<long>` for `ProjectionSkipped`, one `Counter<long>` for `GhostDrawerTransitions`, one `Histogram<double>` for `GhostsRebuildMs`).
4. **Create new descriptor file** [`src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs`](../../../src/Legolas.Module/Diagnostics/) implementing `ITagDescriptorProvider`. Add rows for the new keys scoped to `"Mithril.Legolas.Calibration"`: `area`, `scene.asset_key`, `scene.parent_area_key`, `cal.source`, `cal.frame`, `cal.residual_px`, `cal.refs`, `cal.path`, `consumer`, `outcome`, `frame`, `refs_count`, `ghosts_built`, `from`, `to`. All `PiiClassification.Safe`. **Note:** every (key, scope) pair is its own row — the existing `outcome` on `Mithril.Reference` is NOT reused; we ship a fresh row scoped to our source. Same for `area` (the overlay catalog scopes it to `Mithril.Overlay`).
5. Register the new descriptor via DI in [`LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs): `services.AddSingleton<ITagDescriptorProvider, LegolasCalibrationTagDescriptors>()` (or whatever the existing wiring shape is — confirm by reading where `MithrilSharedTagDescriptors` is registered).

**Tests:** If a `LegolasCalibrationTagDescriptorsTests` doesn't exist yet, sibling one onto `MithrilSharedTagDescriptorsTests` ([`tests/Mithril.Shared.Tests/Telemetry/MithrilSharedTagDescriptorsTests.cs`](../../../tests/Mithril.Shared.Tests/Telemetry/MithrilSharedTagDescriptorsTests.cs)) asserting every key/scope pair is declared. Existing catalog/parity tests should still pass.

**Acceptance:** Build green. No consumer references the new statics yet — zero behavioural diff. Descriptor test passes against the new provider.

---

## Task 1 — Picker telemetry (`MapCalibrationService.PickByFrame`)

**Files:** [`src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs).

**Steps:**

1. Add the three log calls per spec §5.1 (hit-Trace, below-floor-Information, miss-Trace).
2. Add `MithrilMeters.LegolasCalibration.PickerOutcomes.Add(1, ...)` on every return path including null.
3. Verify category is `"Mithril.MapCalibration"` (existing — no DI change).

**Tests:** Extend `MapCalibrationServiceTests` (or sibling) — new test "PickByFrame logs eligible/total on hit." Use `FakeLogger`. Assert one record per call with the expected template + properties. Add a second test for the miss path.

**Acceptance:** `dotnet test tests/Mithril.MapCalibration.Tests` passes. `GetCalibration` traces unchanged (regression check on existing tests).

---

## Task 2 — `AreaCalibrationService` lifecycle

**Files:** [`src/Legolas.Module/Services/AreaCalibrationService.cs`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs), [`src/Legolas.Module/LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs).

**Steps:**

1. Add `ILogger? logger = null` parameter to `AreaCalibrationService` ctor; store on a private field.
2. In `LegolasModule`, wire the logger via `loggerFactory?.CreateLogger("Legolas.Calibration")` at the service registration site.
3. Add log calls per spec §5.2 in `SelectScene`, `OnMapCalChanged` (both branches), `CalibrateCurrentArea` (all three branches), `ClearCurrentAreaCalibration`.
4. Wrap `SelectScene` and `CalibrateCurrentArea` in `LegolasCalibration.StartActivity(...)` spans per §5.2.

**Tests:** Extend [`tests/Legolas.Tests/Services/AreaCalibrationServiceTests.cs`](../../../tests/Legolas.Tests/Services/AreaCalibrationServiceTests.cs) — three small tests covering `SelectScene` log shape, `CalibrateCurrentArea` solved-vs-refused, and `OnMapCalChanged` re-apply vs drop. Existing tests in that file continue to pass (no behavioural change).

**Acceptance:** `dotnet test tests/Legolas.Tests` passes. Manual smoke (Mithril.Shell, zone change, watch `mithril-*.json` log): `SelectScene` line fires on each `MapAssetChanged`.

---

## Task 3 — VM projection paths (`MapOverlayViewModel`)

**Files:** [`src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs).

**Steps:**

1. **Generalise `LogCalibrationFallback`** (D4) — add a `string callSite` parameter, update the message template to `"MapOverlayViewModel.{CallSite} fallback for area {AreaKey}: {Reason}"`, update the existing `RefreshCalibrationMarker` caller to pass `"RefreshCalibrationMarker"`. Dedup key becomes `areaKey + "|" + callSite + "|" + reason` so different call sites in the same area each get one first-time-Trace.
2. Add success-path Information log + `LegolasCalibration.StartActivity("calibration.ghosts.rebuild")` + `GhostsRebuildMs` histogram to `RebuildCalibrationGhosts`. Skip-path uses `LogCalibrationFallback(area, "RebuildCalibrationGhosts", "no_overlay_cal")` + `ProjectionSkipped.Add(1, {consumer:"ghosts", area})`.
3. **Per-frame getters: meter + skip-only log.** For `MotherlodeMarkerPixels` and `MotherlodeGuidanceOverlay`, add NO success log (per spec §5.3 frequency notes — these are called every render tick). Skip path: `LogCalibrationFallback(area, "MotherlodeMarkerPixels", "no_overlay_cal")` + `ProjectionSkipped.Add(1, ...)`. Same for guidance.
4. Add Information log to `OnCalibrationChanged` reporting the chosen `Action`.
5. **D7 anchor** — add Information log to `SetCalibrationValidation` with the full property bag (area, scene, isCalibrated, overlayCalPresent, action, ghostsBuilt).
6. Find any other reader of `_areaCalibration.CurrentOverlayCalibration` in this file and apply the same pattern. (`RefreshSurveyPlayerAnchor` is a candidate — verify during this task.)

**Tests:** Per spec §7, one shape test sibling-to [`tests/Legolas.Tests/ViewModels/MapOverlayCalibrationValidationTests.cs`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationValidationTests.cs) — reuses that file's 14-arg VM-construction fixture. Drive `SetCalibrationValidation(true)` with a calibrated stub area + 3 refs, assert one Information record + ghosts built; drive `SetCalibrationValidation(true)` with null `CurrentOverlayCalibration`, assert `LogCalibrationFallback` fires with `"no_overlay_cal"`. **Also extend [`MapOverlayCalibrationFallbackDedupTests`](../../../tests/Legolas.Tests/ViewModels/MapOverlayCalibrationFallbackDedupTests.cs)** to cover the new `callSite` parameter — confirms D4 didn't break dedup. ILogger capture: reuse whatever pattern the dedup test already uses (it must capture log records to assert dedup).

**Acceptance:** Tests pass. Manual: toggle the validation crosshair in a calibrated area → log shows the toggle entry + the rebuild line; toggle in an uncalibrated area → log shows the toggle entry + the `no_overlay_cal` skip.

---

## Task 4 — Drawer state-transition logging

**Files:** [`src/Legolas.Module/Rendering/LegolasOverlaySceneDrawer.cs`](../../../src/Legolas.Module/Rendering/LegolasOverlaySceneDrawer.cs), [`src/Legolas.Module/Rendering/LegolasOverlayDrawerHostedService.cs`](../../../src/Legolas.Module/Rendering/LegolasOverlayDrawerHostedService.cs).

**Steps:**

1. Add `ILogger? logger = null` parameter to `LegolasOverlaySceneDrawer` ctor; store on a private field.
2. In `LegolasOverlayDrawerHostedService`, pass `loggerFactory?.CreateLogger("Legolas.Overlay.GhostDrawer")` to the drawer ctor.
3. Add two `private int?` fields on the drawer for state-transition tracking: `_lastShownBucket` (0=hidden, 1=empty, 2=drawing, 3=brush_null) and `_lastTransitionFrame` (for throttling — optional, if a transition could flicker rapidly).
4. In `DrawCalibrationGhosts`, compute `currentBucket` at top, compare to `_lastShownBucket`, emit Trace log + `GhostDrawerTransitions.Add(1, {from, to})` on change, then update `_lastShownBucket`.
5. On `brush == null`, emit `LogWarning` once per session (gate with a `bool _brushNullWarned`).

**Tests:** Extend the existing `LegolasOverlaySceneDrawerGhostTests` (referenced in code at line ~189). Drive 4 calls: hidden, shown-but-empty, shown-with-2-ghosts, shown-with-2-ghosts-again. Assert exactly 2 transition logs (the no-change repeat doesn't log).

**Acceptance:** Tests pass. Manual: toggle validation on an empty-cal area, then on a populated area — the drawer log shows `hidden → empty → drawing` over the two toggles.

---

## Task 5 — Ingestion + wizard projection (includes the DI fix per D10)

**Files:** [`src/Legolas.Module/Services/PlayerLogIngestionService.cs`](../../../src/Legolas.Module/Services/PlayerLogIngestionService.cs), [`src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs`](../../../src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs). **Note:** does NOT touch [`LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs:279) — the existing `AddHostedService<PlayerLogIngestionService>()` registration stays as-is; the fix is on the ctor side.

**Steps:**

1. **D10 fix — wire the logger properly.** `PlayerLogIngestionService` today takes `ILogger? logger = null` (line 75) and is registered via `AddHostedService<T>()` at [`LegolasModule.cs:279`](../../../src/Legolas.Module/LegolasModule.cs#L279). DI registers `ILogger<T>` and `ILoggerFactory`, NOT non-generic `ILogger` — so today's `_logger` is always null and the existing `_logger?.LogInformation("Subscribed to Arda domain events")` at line 98 is dead code. Fix: change the ctor parameter from `ILogger? logger = null` to `ILoggerFactory? loggerFactory = null`, and inside, set `_logger = loggerFactory?.CreateLogger("Legolas.Ingestion")`. Matches the pattern in [`LegolasOverlayDrawerHostedService.cs:50-55`](../../../src/Legolas.Module/Rendering/LegolasOverlayDrawerHostedService.cs#L50). Existing tests passing `null` keep building.
2. **Ingestion logs.** Add the 4 log paths from spec §5.5 to `HandleMapTarget`. Increment `ProjectionSkipped` on the no-cal path. The existing `"Subscribed to Arda domain events"` line at 98 now actually fires (no behavior change to the service itself, just it's no longer dead).
3. **Wizard:** `CalibrationSessionViewModel._logger` is already correctly wired via factory lambda at [`LegolasModule.cs:173`](../../../src/Legolas.Module/LegolasModule.cs#L173) on `"Legolas.CalibrationSession"` — no DI change needed. Add success + skip log to `ProjectLandmarks` per spec §5.6. Increment `ProjectionSkipped` on skip.

**Tests:** D10's DI change is testable: add one `PlayerLogIngestionServiceLoggingTests` that constructs the service with a `LoggerFactory` and asserts the "Subscribed to Arda domain events" line emits at StartAsync — proves the DI wiring actually flows. No unit test for `HandleMapTarget` log shape (D9 says one shape test for the whole pass is enough). Extend the existing `CalibrationSessionViewModelTests` (verify the test file exists during this task; if not, add a focused test for the no-cal log).

**Acceptance:** Tests pass. Manual: launch Mithril; first-run log shows `Legolas.Ingestion: Subscribed to Arda domain events` (proves the dead code is now live). In Survey mode, walk into an uncalibrated area, trigger a `ProcessMapFx` line (complete a survey) → log shows the drop with area + reason.

---

## Task 6 — Docs

**Files:** [`docs/perf-trace-schema.md`](../../perf-trace-schema.md), [`CLAUDE.md`](../../../CLAUDE.md) (logging bullet).

**Steps:**

1. Append a section to `perf-trace-schema.md` under "What's instrumented today" listing the new `Mithril.Legolas.Calibration` ActivitySource + its three meter instruments + one histogram. Document each tag's vocabulary (`outcome`, `consumer`, `frame`, `from`/`to`).
2. Add a "Calibration consumer chain" paragraph to the doc's diagnostic-patterns section: "If validation shows no ghosts, grep the trace for `SetCalibrationValidation` → `RebuildCalibrationGhosts` → drawer transitions; the gap names the broken link."
3. Update CLAUDE.md's logging bullet category list to mention the new categories (`Legolas.Calibration`, `Legolas.Overlay.GhostDrawer`, `Legolas.Ingestion`) — or, if the follow-up issue from spec §9 lands first, skip this and let the canonical-list issue cover it.

**Tests:** None — docs only.

**Acceptance:** PR review reads the doc additions and confirms the example diagnostic walkthrough matches the implementation.

---

## Wrap-up

After Task 6:

1. Verify build + full test suite green.
2. Manual smoke per acceptance criteria of each task.
3. Squash-merge per project convention; close #1093 in the merge commit.
4. File the two follow-up issues from spec §9 (composed-cal VM migration; CLAUDE.md canonical category list).

## Estimated diff size

- Code: ~280 LoC across 8 files (drawer ~40, VM ~60, AreaCalibrationService ~50, MapCalibrationService ~30, ingestion + wizard ~30, telemetry catalog ~40, new `LegolasCalibrationTagDescriptors` ~30).
- Tests: ~180 LoC across 4 test files (descriptor test, DI-wiring test, VM diagnostics test, extensions to fallback-dedup + AreaCalibrationServiceTests).
- Docs: ~80 LoC.

Total ~540 LoC. Reviewable in one PR.
