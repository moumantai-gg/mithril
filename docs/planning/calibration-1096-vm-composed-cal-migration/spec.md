# VM projection paths → composed-cal migration — spec

**Issue:** [mithril#1096](https://github.com/moumantai-gg/mithril/issues/1096). **Status:** active. **Parent context:** [`calibration-logging-pass-1093/spec.md`](../calibration-logging-pass-1093/spec.md) §9 — this is the behaviour fix the logging pass intentionally deferred.

## 1. Problem

`IAreaCalibrationService.CurrentOverlayCalibration` returns null for scenes that have only a **texture-frame** record stored — even when that record can be projected onto the overlay surface via `WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` + `IMapTextureDimensions` (the post-#1081 composition mechanism). `OverlayWindowService.ResolveComposedOverlayCalibration` already does this composition for the marker-projection block; the VM-side projection paths still go through the non-composing `CurrentOverlayCalibration` getter and silently drop output for the same scenes.

Concretely: a user who runs AutoCalibrate and lands a texture-frame record (no overlay-frame record) sees:

- The overlay window's **markers** render correctly (because `OverlayWindowService` composes).
- The validation **pink dots** (ghosts), motherlode **markers + guidance ring**, the **survey "you-are-here" anchor**, the survey-pin drop, and the wizard **landmark ghosts** all render nothing — because the VM consumers read `CurrentOverlayCalibration` (returns null) and short-circuit through `LogCalibrationFallback("no_overlay_cal")`.

This is the underlying class of bug that triggered the [#1093](https://github.com/moumantai-gg/mithril/issues/1093) logging pass. The logging is now in place: the `mithril.legolas.calibration.projection.skipped` counter increments per consumer per area, and per-area Trace via `LogCalibrationFallback` names the call site. Production traces should make the migration target visible.

## 2. Scope

In scope — replace direct reads of `_areaCalibration.CurrentOverlayCalibration` in:

- `MapOverlayViewModel.RebuildCalibrationGhosts` (line ~640)
- `MapOverlayViewModel.MotherlodeMarkerPixels` (per-frame getter, line ~1407)
- `MapOverlayViewModel.MotherlodeGuidanceOverlay` (per-frame getter, line ~1455)
- `MapOverlayViewModel.RefreshSurveyPlayerAnchor` (line ~320)
- `MapOverlayViewModel.SetCalibrationValidation` toggle-log presence check (line ~590) — read-through only; the toggle log captures `overlayCalPresent` for triage, and "present" must now mean "present-OR-composable"
- `PlayerLogIngestionService.HandleMapTarget` (line ~210)
- `CalibrationSessionViewModel.ProjectLandmarks` (line ~538) — wizard canvas, separate surface dims

Plus the infrastructure additions that make the migration possible:

- New `IComposedOverlayCalibrationResolver` in `Mithril.Overlay` (lifts the composition logic out of `OverlayWindowService`).
- `IOverlayWindow.GetSurfaceSize()` so consumers can source the live overlay surface dims.
- `CalPath` enum promoted from `OverlayWindowService` internal to a public type.
- New `cal.path` tag value `composed_from_texture` registered in `LegolasCalibrationTagDescriptors` and documented in `docs/perf-trace-schema.md`.

Out of scope:

- The `RefreshCalibrationMarker` path (line ~1198). It's the in-flow Drop/Pair walkthrough — by design only runs while pairing, and the user is in the middle of solving a calibration. The texture-frame composition wouldn't change behaviour there (`IsPairing` gate fires before the cal lookup). Leave reading `CurrentOverlayCalibration` directly.
- Behaviour changes to the picker, the AutoCal engine, or the texture-frame store. This pass swaps the resolver consumers use; it does not change what gets stored or how the picker picks.
- `IAreaCalibrationService.CurrentOverlayCalibration` removal. The property stays (other consumers like the surface texture-only "is there ANY cal" semantics use it). It's no longer how the listed sites resolve.
- New diagnostics UI / settings surfaces.

## 3. Decision ledger

| # | Decision | Reasoning |
|---|---|---|
| D1 | **New shared composer service `IComposedOverlayCalibrationResolver` in `Mithril.Overlay`.** Method: `ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight)`. The `OverlayWindowService.ResolveComposedOverlayCalibrationForTest` pure helper moves into the new service as its implementation; `OverlayWindowService.ResolveComposedOverlayCalibration` becomes a thin pass-through to the new service with `(_window's surface dims)`. | User-selected during brainstorming. Promoting into `IAreaCalibrationService` couples a frame-typed service to `IMapTextureDimensions` + `IOverlayWindow` (WPF). Promoting into `IMapCalibrationService` pollutes the engine's contract with surface-dim concerns the engine doesn't have. A dedicated composer keeps each layer doing one thing. |
| D2 | **`IOverlayWindow` exposes surface dims via `(double Width, double Height) GetSurfaceSize()`.** Returns `(0, 0)` when the window/surface isn't realised — matches today's `OverlayWindowService.ResolveOverlaySurfaceSize` semantics. | User-selected. The 5 overlay-window consumers need ONE source of truth for surface dims. Mirroring `OverlayWindowService`'s internal helper as a public contract method avoids each consumer re-deriving "live overlay surface" through ad-hoc accessors. |
| D3 | **Wizard supplies its own placement-canvas dims via the EXISTING `_viewportW` / `_viewportH` fields on `CalibrationSessionViewModel`.** No new accessor / adapter / view code-behind change. The fields at [`CalibrationSessionViewModel.cs:664`](../../../src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs#L664) are populated by `SetViewport(width, height)` (line 666), which is called from [`CalibrationOverlayView.xaml.cs:Viewport_SizeChanged`](../../../src/Legolas.Module/Views/CalibrationOverlayView.xaml.cs#L45) — the precedent already exists. `ProjectLandmarks` just reads `(_viewportW, _viewportH)` and passes them to the composer. | User-selected. Codebase grounding (review pass) revealed the wizard VM ALREADY has its canvas dims plumbed via SetViewport — used today for offscreen-clamping survey pins. Adding a sibling `IWizardCanvasSize` interface would have been a redundant second source of truth. Test ctor leaves the fields at default 0 → composer's F2 (`unsized_surface`) path fires → existing "Solve a calibration first" warning still surfaces. |
| D4 | **Verification gate = composer unit tests + integration test on `RebuildCalibrationGhosts` + live shell verify.** Composer tests are table-driven (direct-overlay / texture-only-with-sha / texture-only-without-sha / unsized-surface / catalogue-miss / no-scene). Integration test drives `RebuildCalibrationGhosts` against a texture-frame-only `IMapCalibrationService` stub and asserts `CalibrationGhosts.Count > 0` and the activity tag `cal.path == composed_from_texture`. Manual: launch shell against a known AutoCal-only scene, toggle validation, confirm pink dots render. | User-selected. Per `verify_headline_behavior_through_full_render_chain` memory (PR #872): unit tests alone can be defeated by a second gate downstream; the test seam can hide it. The manual chain-trace stays mandatory. |
| D5 | **`ComposedCalResolution` carries a `MissReason` string when `Path == None`.** Lifted from `OverlayWindowService.ClassifyComposedMissReason`. Consumers feed the value into `LogCalibrationFallback(area, callSite, reason)` so the existing per-`(area, callSite, reason)` Trace dedup names the actual sub-case (`null_sha`, `unsized_surface`, `catalogue_miss`, `no_usable_calibration`) — actionable for the user (re-run AutoCalibrate / wait / resize). | The current call sites pass the literal string `"no_overlay_cal"` for every skip. Post-migration, "no usable cal" is one of several real sub-cases; if we collapse them the existing dedup helper has nothing to distinguish them by. The classifier already exists in `OverlayWindowService`; reusing it via the resolver gives consumers free triage. |
| D6 | **`CalPath` enum promoted from `internal` to a public type in `Mithril.Overlay`.** The post-#1093 `cal.path` ActivitySource tag is the public observable surface; the enum it encodes should be a public type, not redefined per consumer. | The tag values `direct_overlay` / `composed_from_texture` / `none` are the public surface. Cross-project producers need to emit them consistently. Cheapest way: one enum the producers share. |
| D7 | **`ProjectionSkipped` counter semantics change: only fires when `Path == None`, NOT when composition succeeds.** Today every null-`CurrentOverlayCalibration` increments the counter, including scenes where a texture-frame record exists. After migration, the counter measures real misses — composed-from-texture is a HIT, tagged `cal.path=composed_from_texture` on the span / hits the GhostsRebuildMs histogram normally. | The counter's promise is "how often does this consumer's projection drop because of missing cal." The right number to expose after migration is "drops because no usable cal at all." Otherwise the migration makes the counter incoherent (same scene drops one day, projects the next — driven by what the picker picked, not by what's available). |
| D8 | **`IAreaCalibrationService.CurrentOverlayCalibration` is not removed.** The property keeps its existing direct-overlay-only semantics. The 6 listed sites stop reading it; other consumers (test fixtures, in-flow `RefreshCalibrationMarker`, the existing `SetCalibrationValidation` `overlayCalPresent` log captures the "direct frame present" status which is still meaningful for triage — see §5) keep reading it. | Removing the property is a bigger blast radius. Other readers exist (and may grow); the post-migration semantic of `CurrentOverlayCalibration` ("there's a directly-stored overlay-frame record") is still a useful query for tooling and tests. The behaviour fix the issue asks for is at the consumer level, not the API level. |
| D9 | **One PR, TDD-task-ordered.** Same shape as #1093 (D2 there). Task 0 lands infrastructure (composer + enum promotion + `GetSurfaceSize` + descriptor); tasks 1–N migrate consumers one at a time; final task is docs (`perf-trace-schema.md` + the §9 follow-up acknowledged in #1093 spec gets a "shipped" link). | A reviewer reading the PR in task order sees the composer land before any consumer uses it; each consumer site is its own commit. Splitting risks shipping the composer with no users (no observable behaviour) or shipping consumer-side migrations against a missing API. |
| D10 | **No new defensive tests for "composer returns null on edge X" beyond the table-driven composer test.** The composer test is the assertion. Per #1093 D9 / CLAUDE.md "don't write speculative guards." | The composer is pure; one parameterised test covers the decision table. Adding per-consumer-site guard tests duplicates coverage. |

## 4. Components

### 4.1 New types in `Mithril.Overlay`

```csharp
namespace Mithril.Overlay;

/// <summary>How a usable <see cref="WorldToOverlayCalibration"/> was resolved
/// for the current scene. Surfaced as the <c>cal.path</c> tag on calibration
/// consumer spans (post-#1093). Public so cross-project producers (Legolas
/// VM paths, OverlayWindowService) emit the same vocabulary.</summary>
public enum CalPath
{
    /// <summary>No usable cal this frame (uncalibrated, null-sha cal,
    /// catalogue miss, or surface unsized). The companion
    /// <see cref="ComposedCalResolution.MissReason"/> names which sub-case.</summary>
    None,
    /// <summary>An overlay-frame record exists; consumed directly.</summary>
    DirectOverlay,
    /// <summary>Only a texture-frame record exists; composed onto the
    /// overlay surface via
    /// <see cref="WorldToTextureCalibration.ProjectThroughOverlay(MapRect)"/>.</summary>
    ComposedFromTexture,
}

/// <summary>Result of <see cref="IComposedOverlayCalibrationResolver.Resolve"/>.
/// On success, <see cref="Calibration"/> is non-null and <see cref="Path"/> says
/// how. On miss, <see cref="Path"/> is <see cref="CalPath.None"/> and
/// <see cref="MissReason"/> carries a stable, lowercase, snake_case reason
/// suitable for feeding into a log helper's dedup key
/// (<c>null_sha</c>, <c>unsized_surface</c>, <c>catalogue_miss</c>,
/// <c>no_usable_calibration</c>, <c>no_scene</c>).</summary>
public readonly record struct ComposedCalResolution(
    WorldToOverlayCalibration? Calibration,
    CalPath Path,
    string? MissReason);

/// <summary>Composes a <see cref="WorldToOverlayCalibration"/> for an
/// arbitrary surface size by reading <see cref="IMapCalibrationService"/>'s
/// frame-typed records: an overlay-frame record consumes directly; a
/// texture-frame record composes onto the surface rect via
/// <see cref="WorldToTextureCalibration.ProjectThroughOverlay(MapRect)"/>
/// with dims looked up from <see cref="IMapTextureDimensions"/>.
///
/// <para>Pure: the (scene, w, h) inputs fully determine the result given the
/// injected calibration + dim-catalogue state. The caller chooses the
/// surface (overlay window vs. wizard canvas vs. test).</para></summary>
public interface IComposedOverlayCalibrationResolver
{
    ComposedCalResolution Resolve(MapSceneRef? scene, double surfaceWidth, double surfaceHeight);
}
```

Internal default impl (`ComposedOverlayCalibrationResolver`) takes `IMapCalibrationService` + `IMapTextureDimensions` in its constructor. Lifts the body of `OverlayWindowService.ResolveComposedOverlayCalibrationForTest` and `OverlayWindowService.ClassifyComposedMissReason` verbatim, with the reason strings normalised to the snake_case vocabulary above.

DI registration: [`OverlayServiceCollectionExtensions.AddMithrilOverlay`](../../../src/Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs#L33) adds `services.TryAddSingleton<IComposedOverlayCalibrationResolver, ComposedOverlayCalibrationResolver>();`.

### 4.2 `IOverlayWindow` extension

```csharp
public interface IOverlayWindow
{
    Window Window { get; }
    bool IsReady { get; }
    // ... existing members ...

    /// <summary>The live D2D overlay surface's DIU size. Returns (0, 0) when
    /// the window or its surface isn't realised yet (mirrors the F2 fail-soft
    /// branch in <see cref="ComposedOverlayCalibrationResolver"/>). Callers
    /// thread the values into
    /// <see cref="IComposedOverlayCalibrationResolver.Resolve"/>.</summary>
    (double Width, double Height) GetSurfaceSize();
}
```

`OverlayWindowService.GetSurfaceSize` lifts `ResolveOverlaySurfaceSize` to a public method.

### 4.3 Wizard canvas dims — already plumbed (NO new infrastructure)

The grounding pass surfaced an existing channel: [`CalibrationSessionViewModel._viewportW` / `_viewportH`](../../../src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs#L664) are populated by `SetViewport(width, height)` (line 666), called from [`CalibrationOverlayView.xaml.cs:Viewport_SizeChanged`](../../../src/Legolas.Module/Views/CalibrationOverlayView.xaml.cs#L45):

```csharp
// Already present in the wizard codebehind:
private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
{
    if (DataContext is CalibrationSessionViewModel vm)
        vm.SetViewport(Viewport.ActualWidth, Viewport.ActualHeight);
}
```

`ProjectLandmarks` reads `_viewportW` / `_viewportH` directly when calling the composer. The fields are 0 when the viewport hasn't been sized yet (test ctor, view not laid out) — that falls into the composer's `unsized_surface` (F2) path, and the existing `"Solve a calibration first — nothing to project."` warning surfaces unchanged.

No new interface, no new adapter, no new view code-behind hooks.

## 5. Per-site migration

Each site replaces the `CurrentOverlayCalibration` read with:

```csharp
var (w, h) = _overlayWindow.GetSurfaceSize();        // or _wizardCanvasSize.GetSize() for the wizard
var r = _composer.Resolve(_areaCalibration?.CurrentScene, w, h);
if (r.Calibration is not { } cal)
{
    LogCalibrationFallback(area, "<callSite>", r.MissReason ?? "no_usable_calibration");
    MithrilMeters.LegolasCalibration.ProjectionSkipped.Add(1,
        new KeyValuePair<string, object?>("consumer", "<consumer>"),
        new KeyValuePair<string, object?>("area", area));
    act?.SetTag("cal.path", "none");
    return;       // or the existing equivalent (empty list / null / refused)
}
// happy path — use cal exactly as today (.ToOverlay / .ToLiveOverlay)
act?.SetTag("cal.path", r.Path switch
{
    CalPath.DirectOverlay => "direct_overlay",
    CalPath.ComposedFromTexture => "composed_from_texture",
    _ => "none",
});
```

| Site | New consumer-side change | Notes |
|---|---|---|
| `MapOverlayViewModel.RebuildCalibrationGhosts` | Replace `_areaCalibration?.CurrentOverlayCalibration is not { } cal` → composer call. `act?.SetTag("cal.path", ...)` already exists; switch from a hardcoded literal to the `r.Path` switch. | The `success` Information log keeps reading `_areaCalibration.CurrentCalibration?.Source` / `ResidualPixels` for the message — those come from the full `AreaCalibration` record and are still the right values to surface (the composer's `WorldToOverlayCalibration` doesn't carry them). When composing from texture, `CurrentCalibration` is the texture-frame record; `Source` / `ResidualPixels` reflect that record. |
| `MapOverlayViewModel.MotherlodeMarkerPixels` (per-frame getter) | Same swap. No success log (per-frame). `cal.path` not tagged (no span on getters). | Counter only on miss. |
| `MapOverlayViewModel.MotherlodeGuidanceOverlay` (per-frame getter) | Same swap. | Same as above. |
| `MapOverlayViewModel.RefreshSurveyPlayerAnchor` | Replace `var overlayCal = _areaCalibration?.CurrentOverlayCalibration;` → composer call; pass `overlayCal` value to `ResolveSurveyAnchor` unchanged otherwise. | Success log keeps its current shape. |
| `MapOverlayViewModel.SetCalibrationValidation` | The line `var overlayCalPresent = _areaCalibration?.CurrentOverlayCalibration is not null;` becomes `var overlayCalPresent = _composer.Resolve(scene, w, h).Calibration is not null;` — so the toggle log reports the post-migration "present-OR-composable" semantic. Rename property in the log message body to `overlayCalUsable` to make the semantic shift explicit. | The toggle is the lifecycle anchor (#1093 D7). Its log entry is the first thing a triager grep's for. Reporting "present" when composition was the actual yes-answer would mislead future investigations. |
| `PlayerLogIngestionService.HandleMapTarget` | Same swap. Existing `_session.LastLogEvent` strings unchanged on the miss path; on success the projection branch (`cal.ToLiveOverlay` / `cal.ToOverlay`) consumes the composed cal unchanged. | The service takes a new `IComposedOverlayCalibrationResolver` + `IOverlayWindow` ctor parameter. |
| `CalibrationSessionViewModel.ProjectLandmarks` | Replace `_service.CurrentOverlayCalibration is not { } c` → composer call with `(_viewportW, _viewportH)` (existing fields populated by `SetViewport`). | Wizard canvas dims of `(0, 0)` (test ctor / view not realised) cleanly fall into the existing "Solve a calibration first" warning via the composer's F2 path. |

## 6. Telemetry

### 6.1 New tag value: `cal.path = composed_from_texture`

The tag key `cal.path` is already declared by the #1093 pass at [`LegolasCalibrationTagDescriptors.cs:45`](../../../src/Legolas.Module/Diagnostics/LegolasCalibrationTagDescriptors.cs#L45) — the existing doc-comment ALREADY reads *"Projection path taken: direct_overlay | none (the composed-cal migration adds composed)"*, anticipating this migration. Task 0 updates the doc-comment vocabulary to `direct_overlay | composed_from_texture | none` (and adds a matching entry in `docs/perf-trace-schema.md`). The descriptor declares the KEY's classification, not its value set, so no structural change to the descriptor row is needed.

The tag value is **Safe** (fixed enum vocabulary, no PII).

### 6.2 `ProjectionSkipped` counter semantic change (D7)

Today the counter increments on every null-`CurrentOverlayCalibration` early-return. After migration: increments only when `composer.Resolve(...).Calibration is null`. A scene that resolves via the texture-frame composition is NOT counted as skipped — it hit the happy path and emits its usual span / histogram. This is a behaviour change to the meter, called out in `docs/perf-trace-schema.md`.

### 6.3 `MissReason` vocabulary on the skip log

The existing `LogCalibrationFallback(areaKey, callSite, reason)` helper dedups by `(area, callSite, reason)`. Today every consumer passes `reason = "no_overlay_cal"` for the calibration-null branch. Post-migration the reason carries the composer's `MissReason`:

| Reason | When |
|---|---|
| `no_usable_calibration` | Picker returned no record at all. |
| `null_sha` | Texture-frame record exists but `PixelSha256` is null (pre-#1081 record; user re-runs AutoCalibrate). |
| `unsized_surface` | Surface ActualWidth/Height ≤ 0 (window not yet realised; first frame after `Show()`; wizard not yet laid out). |
| `catalogue_miss` | Texture-frame sha doesn't match any entry in the bundled `CanonicalAssetHashes`. |
| `no_scene` | `CurrentScene` is null (no `MapAssetChanged` event yet). |

All Safe (fixed lowercase snake_case enums; no PII).

## 7. Test strategy

### 7.1 Composer unit tests

The existing [`tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs`](../../../tests/Mithril.Overlay.Tests/ResolveComposedOverlayCalibrationTests.cs) already covers 8 cases: `WizardOnly_ReturnsDirectOverlayCal`, `AutoCalOnly_ShaInCatalogue_ReturnsComposedFromTexture`, `AutoCalOnly_NullSha_ReturnsNone`, `AutoCalOnly_ShaNotInCatalogue_ReturnsNone`, `AutoCalOnly_UnsizedSurface_ReturnsNone`, `BothFramesPresent_PrefersDirectOverlay`, `Uncalibrated_ReturnsNone`, `NullScene_ReturnsNone`. The decision table is identical to what this spec needs.

Task 0 step:

1. Rename to `ComposedOverlayCalibrationResolverTests.cs`.
2. Replace the `OverlayWindowService.ResolveComposedOverlayCalibrationForTest(...)` calls with `new ComposedOverlayCalibrationResolver(stubMapCal, stubDims).Resolve(scene, w, h)` — the matrix of inputs / expected `(Cal, Path)` is preserved verbatim.
3. Extend each None-returning case with a `MissReason` assertion against the snake_case vocabulary in §6.3 — that's the post-migration addition the existing tests don't cover.

The underlying logic is unchanged because the helper body migrates verbatim (per §10 verification owed).

### 7.2 Integration test on `RebuildCalibrationGhosts`

Extend `tests/Legolas.Tests/ViewModels/MapOverlayCalibrationValidationTests.cs` (existing VM-construction fixture). New test:

- Wire a stub `IMapCalibrationService` that returns a texture-frame record (with sha) for the test scene, and a stub `IMapTextureDimensions` that resolves the sha to a known size.
- Wire a stub `IOverlayWindow.GetSurfaceSize()` returning `(800, 600)`.
- Wire 3 calibration references into `IAreaCalibrationService`.
- Capture `MapOverlayViewModel`'s `LegolasCalibration` activity source via the canonical capture pattern (the dedup test fixture demonstrates the `ILogger` capture pattern; mirror for `ActivityListener`).
- Drive `SelectScene` → `SetCalibrationValidation(true)`.
- Assert: `CalibrationGhosts.Count == 3`. Assert: one `calibration.ghosts.rebuild` activity emitted with `cal.path == "composed_from_texture"`.

### 7.3 Manual headline verify (gate)

Per the `verify_headline_behavior_through_full_render_chain` memory:

1. Launch Mithril against an area known to have only an AutoCal-produced texture-frame record (no wizard solve).
2. Toggle calibration validation on the Map tab.
3. **Confirm pink dots render on the overlay window** matching the area's calibration references.
4. Drop into Survey mode and place a `@me` map-pin; confirm the survey pin lands on the overlay.
5. Optional but worth doing: drop into Motherlode mode with at least one solved treasure; confirm the marker + guidance ring render.

If any of (3)–(5) fails, the migration didn't reach a downstream gate — a coordinator-level swap defeated by a renderer-level check is exactly the failure mode #872 caught.

Capture a perf-trace JSONL during the verify and inspect the `cal.path` tag on the relevant consumer spans — should see `composed_from_texture` on the previously-broken scene.

## 8. Phasing (D9)

One PR. Implementation plan tasks (deferred to `plan.md`) are ordered so each task is reviewable in isolation:

- **Task 0** — infrastructure: new `IComposedOverlayCalibrationResolver` + impl + tests; `CalPath` enum promoted to public; `ComposedCalResolution` record; `IOverlayWindow.GetSurfaceSize`; descriptor doc-comment for `composed_from_texture` value. **Behaviour-neutral** — no callers yet.
- **Task 1** — `OverlayWindowService` swaps to the new resolver. Local `ResolveComposedOverlayCalibration` / `ResolveComposedOverlayCalibrationForTest` / `ClassifyComposedMissReason` retire (the existing tests redirect at the new service). Still behaviour-neutral.
- **Task 2** — `MapOverlayViewModel` migration: 4 sites (`RebuildCalibrationGhosts`, `MotherlodeMarkerPixels`, `MotherlodeGuidanceOverlay`, `RefreshSurveyPlayerAnchor`) + the `SetCalibrationValidation` toggle-log update (`overlayCalPresent` → `overlayCalUsable`). Integration test from §7.2 lands here.
- **Task 3** — `PlayerLogIngestionService.HandleMapTarget` migration.
- **Task 4** — `CalibrationSessionViewModel.ProjectLandmarks` migration. Reads existing `_viewportW` / `_viewportH` fields (already populated by `SetViewport`, called from `CalibrationOverlayView.xaml.cs:Viewport_SizeChanged`) and passes them to the composer. No new interface, no view code-behind change.
- **Task 5** — `ProjectionSkipped` counter semantic change (D7) is implicit in tasks 2–4 (the counter only fires when the composer returns null); add a one-line comment at the counter declaration noting the post-#1096 semantic, and update `docs/perf-trace-schema.md`.
- **Task 6** — `docs/perf-trace-schema.md` update: new `cal.path` value, `MissReason` vocabulary, `ProjectionSkipped` semantic note. Plus flip the #1093 spec §9 row for this issue from "recommended new issue" to a shipped link.
- **Task 7** — manual headline verify per §7.3; capture perf-trace evidence in the PR description.

A reviewer reading the PR commit-by-commit sees the infrastructure land before any consumer uses it; each consumer site is its own commit (per `collaboration_style`).

## 9. Out-of-scope follow-ups

None expected from this pass — the parent #1093 spec already noted this migration as the §9 follow-up. The wizard's `_viewportW` / `_viewportH` channel was already in place pre-migration; Task 4 reuses it without adding new infrastructure.

If the manual verify uncovers a downstream gate (per the §7.3 memory note), the discovery becomes a sibling issue — not a §9 here.

## 10. Verification owed

| Claim | How to verify |
|---|---|
| `OverlayWindowService.ResolveComposedOverlayCalibrationForTest` body migrates verbatim into `ComposedOverlayCalibrationResolver` with no behaviour change. | Task 1 step 1: diff the bodies after the lift. Existing `OverlayWindowService` tests that hit `ResolveComposedOverlayCalibrationForTest` get redirected at the new service; they must still pass without changes to assertions. |
| `_viewportW` / `_viewportH` on `CalibrationSessionViewModel` are visible to `ProjectLandmarks` (private field, same type, so trivially yes — but worth a sanity check during Task 4 that the field semantics ARE "canvas pixels for projection," not "viewport for offscreen clamping in a different coordinate space"). | Task 4 step 1: read `CalibrationSessionViewModel.cs` around line 670–690 — `IsOffscreen` / `DisplayX` / `DisplayY` clamp logic already uses `_viewportW` / `_viewportH` as the canvas-pixel space the survey pins live in. Same surface, same units → `ProjectLandmarks` can reuse them directly. |
| The `MotherlodeMarkerPixels` / `MotherlodeGuidanceOverlay` per-frame getters call `_composer.Resolve(...)` once per frame — no allocation spike. | Task 2 step 3: confirm `ComposedCalResolution` is a `readonly record struct` (no heap alloc) and the composer's internal lookups are O(1). Optional: add a quick perf-trace observation over a 5-second motherlode session to confirm no histogram regression in `mithril.legolas.calibration.ghosts.rebuild_ms` or the overlay's `project` span. |
| The `SetCalibrationValidation` toggle log's `overlayCalUsable` semantic change doesn't break any test that asserts on `overlayCalPresent`. | Task 2 step 4: grep for the literal string `overlayCalPresent`; if any tests assert on it, update them. |
| `IComposedOverlayCalibrationResolver` registers correctly via `OverlayServiceCollectionExtensions` (no DI cycle through `IOverlayWindow`). | Task 0 step 2: the resolver takes `IMapCalibrationService` + `IMapTextureDimensions`. Neither pulls in `IOverlayWindow`. The DI registration is `TryAddSingleton<IComposedOverlayCalibrationResolver, ComposedOverlayCalibrationResolver>` — no `IOverlayWindow` dependency on the resolver itself; consumers (the VM, OverlayWindowService) inject both. Build + boot the shell; watch boot.log past `creating App` per the `di_cycle_invisible_to_unit_tests` memory. |
| Manual headline verify passes per §7.3. | Required pre-merge per D4 + the `verify_headline_behavior_through_full_render_chain` memory. PR description includes a screenshot or perf-trace JSONL excerpt showing `cal.path = composed_from_texture` on a previously-broken scene. |
