# calibration-1095 — live-view detector

**Issue:** [#1095](https://github.com/moumantai-gg/mithril/issues/1095)
**Status:** active

## 1. Problem

Marker projection (validation ghosts, motherlode pins, motherlode guidance, `ProcessMapFx` survey pins, survey player anchor) desyncs from PG's live world-map view whenever the user pans or zooms PG's map. Today's only input is a manual zoom slider; pan isn't tracked. When the slider drifts — or when the seed picker writes the wrong cal record's `CalibrationZoom` to it — markers silently render at off-screen positions and the user sees nothing.

The root mechanism is in [`AreaProjectionCore.Project`](../../../src/Mithril.MapCalibration/Internal/AreaProjectionCore.cs):

```csharp
var effScale = scale * ZoomFactor(currentZoom, calibrationZoom);
return (originX + effScale * rotE, originY - effScale * rotN);
```

`scale` scales by zoom factor but `origin` doesn't. For a cal whose `origin` lands far from world(0,0) — e.g. Serbule's wizard `originY≈1124` — that produces an enormous global pixel offset at off-cal zoom. Tracked, latent, just made visible by the marker-rendering moves of [#1077](https://github.com/moumantai-gg/mithril/pull/1077) / [#1081](https://github.com/moumantai-gg/mithril/pull/1081) / [#1087](https://github.com/moumantai-gg/mithril/pull/1087).

## 2. Reframe

The bug is conceptual, not arithmetic. The current code conflates two layers:

1. **Layer 1 (durable, ships as canonical data):** the world → base-texture-pixel similarity transform. This is purely a property of PG's area asset; the wizard / AutoCal solve it once, it ships as a `BundledBaseline` (or a user/community-contributed) cal record, and it does not change unless PG re-cuts its map assets (`pixelSha256` gate invalidates on drift).
2. **Layer 2 (lightweight, ephemeral runtime measurement):** the base-texture-pixel → overlay-pixel mapping. This depends on PG's current `(pan, zoom)` UI state. PG emits no log signal for either dimension — they must be **measured**, not asked of the user.

Today's slider is a fake layer-2: the user types a number that's supposed to mean "current zoom," and the code multiplies layer-1 scale by it. That conflation is the bug class. Removing the slider — and the `CalibrationZoom` field on the cal record that anchors it — eliminates the entire failure mode.

PG's world-map UX makes layer-2 cheap to measure: zooming pivots on the current pan point, and the map re-centers on the player after movement (until then the user can pan freely). At the canonical "entire map visible" state pan is pinned. Cross-correlating a screen capture of the overlay region against the cached base texture for the area yields `(pan, viewScale)` in a single sub-1s probe — no landmark identification needed; the entire base texture is the template.

## 3. User model

- The cal record is durable. It encodes `world → texture_px` at a canonical solve state. Pan and zoom never invalidate it.
- Mithril measures `(pan, viewScale)` on demand by cross-correlating a screen capture against the cached base texture for the current area. The user never inputs a zoom value.
- Detection runs automatically on overlay-marker-enable user gestures (toggle validation, enable motherlode overlay, enable survey overlay) and on a manual re-detect hotkey for resyncs after the user pans/zooms PG mid-session.
- Detection failure is loud: markers refuse to render and a status badge explains. There is no silent fallback to "guess canonical" that would render wrongly.
- The zoom slider is removed entirely. End users never type or drag a number.

The contributor model is unchanged: the wizard remains end-user accessible, so anyone can solve an area they have access to and contribute the cal upstream. Every PG map asset is present in the local install (no expansion gating), so the bundled-baseline coverage grows monotonically.

## 4. Architecture

### 4.1 Component map

```
Mithril.MapCalibration
├── AreaCalibration                       (record: drop CalibrationZoom property; bump SchemaVersion default)
├── WorldToTextureCalibration             (drop CalibrationZoom struct field; drop currentZoom params)
├── WorldToOverlayCalibration             (drop CalibrationZoom struct field; drop currentZoom params)
├── Internal / AreaProjectionCore         (drop calibrationZoom + currentZoom params; drop ZoomFactor)
├── MapViewFix                            (new struct)
├── ILiveMapViewService                   (new — per-area MapViewFix + Refresh + Changed)
│   └── LiveMapViewService                (new impl)
└── BundledData/map-calibration-baseline.json  (already shaped correctly — no edit needed)

Mithril.MapCalibration.Detection
├── IBaseTextureProvider                  (exists)
├── IMapViewProbe                         (new)
│   └── CrossCorrelationMapViewProbe      (new — screenshot × baseTexture → MapViewFix)
└── (existing detection primitives reused: ImageOps, GrayImage, CalibrationConfidenceGate)

Mithril.Overlay
├── IOverlayZoomSource                    (DELETE — fake layer-2; replaced by ILiveMapViewService)
├── FixedOverlayZoomSource                (DELETE — the platform default for the deleted interface)
├── IOverlayCaptureSource                 (new — captures the overlay region as GrayImage)
└── Internal / OverlayWindowService       (drop IOverlayZoomSource ctor dep; consume ILiveMapViewService.GetCurrent for layer-2)

Legolas.Module
├── ViewModels / MapOverlayViewModel      (delete slider + zoom-seed + IsZoomMismatchWarningVisible + ZoomMismatchText + CalibrationZoomLabel; subscribe to LiveMapViewService.Changed)
├── ViewModels / SessionState             (delete CurrentMapZoom + OnCurrentMapZoomChanged)
├── Rendering / LegolasOverlayZoomSource  (DELETE — Legolas-side IOverlayZoomSource adapter)
├── Hotkeys / OverlayController           (wire ILiveMapViewService + IOverlayCaptureSource; remove IOverlayZoomSource override registration in LegolasModule.cs)
├── Hotkeys / RedetectMapViewHotkey       (new)
├── Views / MapOverlayView.xaml           (zoom strip removed — lines 87–92 binding to Session.CurrentMapZoom; view-state badge added)
└── Views / WizardView.xaml               (zoom strip at lines 35–53 + 730 — wizard solves at canonical only, so the strip is no longer needed; remove or replace with a "fully zoom out, then click here" affordance)
```

### 4.2 Math

**Layer 1** (`AreaProjectionCore.Project`):

```
pixel = origin + (R × world) × scale
```

where `R` is the rotation+mirror composition. No `zoomFactor`, no `calibrationZoom`, no `currentZoom`. The output `pixel` is in whichever frame the cal lives in — Texture for AutoCal / BundledBaseline cals, Overlay for wizard cals. (#1087's `WorldToTextureCalibration.ProjectThroughOverlay` is unchanged in its math role — it composes a Texture-frame cal with a `MapRect` placement to yield a Texture-frame → Overlay-frame composition. Its caller chain — `OverlayWindowService.ResolveComposedOverlayCalibration` — feeds layer-2 in the new model, replacing the `IOverlayZoomSource`-driven zoom path it uses today.)

**Layer 2** (composition site is the marker-projection path inside `OverlayWindowService` and the VM-side projections; not on `WorldToOverlayCalibration` directly):

```
overlay_px = (texture_px − pan_tex) × viewScale
```

where `(pan_tex, viewScale)` come from a fresh `MapViewFix`. Texture-frame cals feed `texture_px` directly. Overlay-frame wizard cals are an open compatibility question — they project to canonical-overlay pixel, not to texture pixel, so `MapViewFix` cannot be applied to them directly. Two viable paths: (a) at runtime, convert Overlay-frame cals to Texture-frame using base-texture dims (inverse of `ProjectThroughOverlay`), then apply layer-2; or (b) Overlay-frame cals work only at canonical view (no layer-2) and are a transitional form. (a) is preferred and is small; the inverse-composition math is closed-form and the base-texture dims are already accessible via `IMapTextureDimensions`. Spec'd at implementation time; the plan can decide which path to take in PR-1.

When no `MapViewFix` has ever been measured for the current area, the live-overlay composition path returns null and consumers refuse to render (status badge says "not measured"). When a prior fix exists but the latest re-detect failed, the prior fix stays in use and the badge surfaces the failure — the user sees stale-but-coherent markers plus a clear indicator that re-detection is owed.

### 4.3 Data flow

User gesture (toggle validation, enable motherlode/survey overlay, manual re-detect hotkey)
  → `LiveMapViewService.RefreshAsync(mapAssetKey)`
  → background thread: `IOverlayCaptureSource.Capture()` + `IBaseTextureProvider.TryGetBaseTexture(mapAssetKey)` + `IMapViewProbe.TryProbe(...)`
  → UI thread: store `MapViewFix?` for the area + raise `Changed(mapAssetKey, fix)`
  → consumers (`RebuildCalibrationGhosts`, motherlode marker/guidance projection, `HandleMapTarget`, `RefreshSurveyPlayerAnchor`) re-project against the new fix.

Concurrent `RefreshAsync(area)` calls for the same area are deduped: the second caller awaits the in-flight probe's result.

### 4.4 Service location

`ILiveMapViewService` lives in `Mithril.MapCalibration` alongside `MapCalibrationService` / `AreaCalibrationService`. Detection lives in `Mithril.MapCalibration.Detection`. The screen-capture seam (`IOverlayCaptureSource`) is the only platform dependency and lives in `Mithril.Overlay` — `Mithril.MapCalibration` consumes the interface, not the implementation.

This decouples cleanly from the #900 overlay-out-of-Legolas refactor: when the Legolas-side controller / VM ownership shifts further, `ILiveMapViewService` remains stable and the consumer wiring is the only Legolas concern.

## 5. Detector algorithm (sketch)

`CrossCorrelationMapViewProbe.TryProbe`:

1. **Coarse pass**: candidate scales geometrically spaced over `[0.25, 4.0]` (≈8 candidates). For each, downsample the screenshot to the candidate's effective resolution; FFT cross-correlate against the base texture; record peak `(pan, score)`.
2. **Refine**: golden-section over a narrow scale window around the best coarse candidate; finer correlation. Optional sub-pixel parabolic peak fit.
3. **Confidence gate**: accept if `peakScore > absoluteThreshold` AND `peakScore / secondPeakScore > ratioThreshold`. Otherwise return null.

Cost target: ≤ 1s wall on a typical machine. Approximate budget — 2048×2048 FFT ≈ 80M complex ops per scale × 8 scales = ~640M ops total, comfortably inside budget. Coarse-to-fine confines memory.

Rotation is held at the cal's value (PG's world-map view doesn't independently rotate within an area). Mirror likewise.

Failure modes (return null + diagnostic for the status badge):
- No base texture cached for the area.
- Screenshot is mostly UI (e.g. inventory dialog open over the map).
- Confidence gate fails (correlation peak too low, or ambiguous).
- Capture itself fails (overlay not visible).

## 6. UX surface

**Status badge** replaces the slider on the overlay header:

| State | Badge text |
|-------|------------|
| Fresh fix | `View: detected (HH:MM:SS) — 0.65×` |
| Never measured | `View: not measured — press <hotkey> on the world map` |
| Detecting | `View: detecting…` |
| Failed | `View: detection failed — couldn't match base texture` |

**Manual re-detect**: a new `IHotkeyCommand` (`RedetectMapViewHotkey`) — default unassigned, user-configurable via the Hotkeys settings.

**Automatic re-detect**: triggered on `SetCalibrationValidation(true)`, motherlode-overlay enable, survey-overlay enable. The same `LiveMapViewService.RefreshAsync` path; results fan out via `Changed`.

**No silent fallback**: when the area has *never* been measured, marker collections are cleared and the badge says "not measured." When a prior fix exists but the latest re-detect failed, the prior fix stays in use (markers don't blank) and the badge surfaces the failure separately. Either way, no path renders markers through a guessed-or-stale layer-2.

**Zoom-mismatch banner and `IsZoomMismatchWarningVisible`**: deleted. The mismatch was only meaningful under the broken math.

## 7. Schema migration

`AreaCalibration` record: remove the `CalibrationZoom` property in code.

- Existing user-cal JSON records that carry a non-default `calibrationZoom` field are silently ignored on load (`System.Text.Json`'s unknown-property handling already drops it). One `Information` log is emitted per such cal on first load: `"Migrated AreaCalibration {Area} — dropped CalibrationZoom={Value} (no longer load-bearing)."`. The "first load" gate avoids per-startup log spam.
- Bundled `map-calibration-baseline.json`: records there carry `calibrationZoom = 1.0` (default), which the existing `WhenWritingDefault` serializer rule already omits, so the file is unchanged. The file-wrapper `schemaVersion` is not bumped — the file format hasn't changed.
- `AreaCalibration.SchemaVersion` default is bumped from 1 to 3 (skipping 2 — the #1076 frame-typing bump — to make the no-CalibrationZoom invariant unambiguous from inspection).
- `AreaCalibration.Frame`: unchanged. Texture-frame cals remain the canonical runtime path. Overlay-frame wizard cals continue to round-trip through #1087's cross-frame composition.

## 8. Code-level changes

**Cal records and projection math:**
- `Mithril.MapCalibration/AreaCalibration.cs` — remove the `CalibrationZoom` property. Bump `SchemaVersion` default from 1 to 3 (skip 2 to make the no-CalibrationZoom invariant unambiguous from inspection).
- `Mithril.MapCalibration/WorldToTextureCalibration.cs` — drop `CalibrationZoom` from the record-struct primary constructor; drop the `double currentZoom` parameter from `ToTexture` / `FromTexture`; drop the 1-arg overloads that default `currentZoom = CalibrationZoom`. Update `ProjectThroughOverlay` to compose without the `CalibrationZoom` thread-through.
- `Mithril.MapCalibration/WorldToOverlayCalibration.cs` — drop `CalibrationZoom` from the record-struct primary constructor; drop the `double currentZoom` parameter from `ToOverlay` / `FromOverlay`; drop the 1-arg overloads. Compose with `MapViewFix` for Texture-frame inputs (helper method, e.g. `ToLiveOverlay(WorldCoord, MapViewFix)`).
- `Mithril.MapCalibration/Internal/AreaProjectionCore.cs` — drop `calibrationZoom`, `currentZoom` params on `Project` / `Unproject`; delete the private `ZoomFactor` helper.

**New types in Mithril.MapCalibration / Detection:**
- `Mithril.MapCalibration/MapViewFix.cs` (new — `record struct`).
- `Mithril.MapCalibration/ILiveMapViewService.cs` (new).
- `Mithril.MapCalibration/LiveMapViewService.cs` (new).
- `Mithril.MapCalibration.Detection/IMapViewProbe.cs` (new).
- `Mithril.MapCalibration.Detection/CrossCorrelationMapViewProbe.cs` (new).

**Mithril.Overlay surface:**
- `Mithril.Overlay/IOverlayZoomSource.cs` — DELETE the file (interface + `FixedOverlayZoomSource`). It's the fake-layer-2 abstraction; `ILiveMapViewService` replaces it.
- `Mithril.Overlay/IOverlayCaptureSource.cs` (new) + concrete impl over the shared window.
- `Mithril.Overlay/Internal/OverlayWindowService.cs` — drop the `IOverlayZoomSource _zoomSource` field + ctor dep (line 80); replace the per-tick zoom read in projection driver with a `ILiveMapViewService.GetCurrent(currentArea)` read that flows through layer-2 composition. Update tests via the `ResolveComposedOverlayCalibrationForTest` seam.
- `Mithril.Overlay/DependencyInjection/OverlayServiceCollectionExtensions.cs:55` — remove the `IOverlayZoomSource` registration; add `IMapViewProbe` + `ILiveMapViewService` + `IOverlayCaptureSource` registrations.

**Legolas.Module consumer churn (namespace `Legolas.*`, NOT `Legolas.Module.*`):**
- `Legolas.Module/ViewModels/SessionState.cs` — delete `CurrentMapZoom` (and its `OnCurrentMapZoomChanged` clamp partial method at line 147).
- `Legolas.Module/ViewModels/MapOverlayViewModel.cs` — delete: the `SessionState.CurrentMapZoom` PropertyChanged subscription (line 215); the zoom-seed logic at `OnCalibrationChanged` (lines 809–815); `IsZoomMismatchWarningVisible`, `ZoomMismatchText`, `CalibrationZoomLabel`, `IsCalibrationZoomLabelVisible`. Subscribe to `ILiveMapViewService.Changed`. Update `RebuildCalibrationGhosts`, `MotherlodeMarkerPixels`, `MotherlodeGuidanceOverlay`, `RefreshSurveyPlayerAnchor` to compose through the current fix; refuse to render when none has ever been measured for the area.
- `Legolas.Module/Services/PlayerLogIngestionService.cs` — `HandleMapTarget` composes through the current fix.
- `Legolas.Module/Rendering/LegolasOverlaySceneDrawer.cs` — drop the `currentZoom` parameter from the calibration-ghost draw site (it no longer threads through `AreaProjectionCore`).
- `Legolas.Module/Rendering/LegolasOverlayZoomSource.cs` — DELETE the file (the Legolas-side adapter for the deleted `IOverlayZoomSource`).
- `Legolas.Module/LegolasModule.cs` (around lines 227–234) — remove the `IOverlayZoomSource` override registration that pointed at `LegolasOverlayZoomSource`.
- `Legolas.Module/Hotkeys/OverlayController.cs` (namespace `Legolas.Hotkeys`) — wire the trigger sites for `LiveMapViewService.RefreshAsync` (validation toggle, motherlode overlay enable, survey overlay enable); inject `IOverlayCaptureSource`.
- `Legolas.Module/Hotkeys/RedetectMapViewHotkey.cs` (new) — manual re-detect via a `IHotkeyCommand`.
- `Legolas.Module/Views/MapOverlayView.xaml` — delete the zoom strip (lines 87–92 binding to `Session.CurrentMapZoom`); add view-state badge. Mind [`docs/wpf-gotchas.md`](../../wpf-gotchas.md).
- `Legolas.Module/Views/WizardView.xaml` — wizard solves at canonical only (PG enforces "fully zoomed out = no pan"), so the zoom strip at lines 35–53 + 730 is dropped; the wizard prompts the user to "zoom fully out, then click the first landmark."

**Bundled data and DI:**
- `Mithril.MapCalibration/BundledData/map-calibration-baseline.json` — unchanged at rest (`calibrationZoom` was never serialized for `1.0` defaults; file-wrapper `schemaVersion` stays at 2 — this PR doesn't reshape the file format).
- DI wiring: `IMapViewProbe`, `ILiveMapViewService`, `IOverlayCaptureSource` registered via the Mithril.MapCalibration + Mithril.Overlay service-collection extensions; Legolas drops its `IOverlayZoomSource` override.

**Composition path (not a picker):**
- `Mithril.Overlay/Internal/OverlayWindowService.ResolveComposedOverlayCalibration` (internal helper added by [#1087](https://github.com/moumantai-gg/mithril/pull/1087); not a picker, a Texture→Overlay composition using `MapRect`). Stays in place. Its caller chain wraps the result through `MapViewFix` layer-2 before reaching marker projection. The actual picker is `MapCalibrationService.PickByFrame` ([Internal/MapCalibrationService.cs:142](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs:142)) — unchanged here; the picker-mismatch concern from #1095 disappears because the `_session.CurrentMapZoom` seed step (which was reading from the union-picker `GetCalibration` while projection used `PickByFrame`) is deleted along with the field.

## 9. Testing

- **Math regression**: `AreaProjectionCore` tests verify world → texture-pixel projection for `BundledBaseline` Serbule, Eltibule, Kur Mountains records matches today's projection at the equivalent canonical zoom — confirms the math change doesn't shift canonical projections (only stops misprojecting at non-canonical).
- **Probe unit**: `CrossCorrelationMapViewProbe` against synthetic `GrayImage` scenarios — exact copy → `(pan=0, viewScale=1)`; scaled copy → expected scale; panned copy → expected pan; noise → null with confidence below gate. Plus a golden against a captured Serbule overlay + bundled base texture.
- **Service**: `LiveMapViewService` — concurrent `RefreshAsync(area)` deduped; per-area state isolated; `Changed` fires on UI thread; failed probe leaves the prior fix in place (markers stay rendered from last good measurement); the status badge surfaces the failure separately.
- **Migration**: an `AreaCalibration` JSON blob carrying `calibrationZoom: 0.42` (a wizard-style record) deserializes cleanly (unknown-property ignored), emits the one-shot Info log, round-trips through save → reload with no `calibrationZoom` field at rest.
- **VM**: `MapOverlayViewModel` does not render markers when `LiveMapViewService.GetCurrent(currentArea)` is null; renders correctly when fix is present.
- **E2E (manual)**: in PG on Serbule at off-cal zoom, toggle validation → detection completes → ghosts render at correct positions. Reproduces the #1095 trigger condition; closes it.

## 10. Out of scope

Filed as follow-up issues at PR-open time:

1. **Periodic background detection**: a low-rate refresh loop that catches PG pan/zoom without an explicit user gesture. Current design is gesture-driven (and a hotkey covers the resync case); periodic is a follow-up if it proves needed.
2. **PG-log-signal verification for pan/zoom**: spot-check whether PG emits anything observable on `Player.log` for world-map UI state changes. If yes, drive `LiveMapViewService` from the signal instead of (or alongside) screenshot probing.
3. **Wizard solving directly in Texture frame**: today's wizard solves in Overlay frame and rides cross-frame composition for runtime use. A wizard that solves directly in Texture frame would retire the Overlay frame from wizard solves and simplify the runtime path further. Independently scoped.
4. **Overlay-frame cal → Texture-frame conversion** (if §4.2 path (a) isn't selected in PR-1): a one-shot migrator that walks user-stored Overlay-frame wizard cals, inverse-composes them through `IMapTextureDimensions`, and re-saves them as Texture-frame so they get layer-2 detection support without re-solving.

## 11. References

- Issue: [#1095](https://github.com/moumantai-gg/mithril/issues/1095)
- Logging counterpart (shipped): [#1093](https://github.com/moumantai-gg/mithril/issues/1093) · [calibration-logging-pass-1093](../calibration-logging-pass-1093/)
- Pixel-frame typing: [#1076](https://github.com/moumantai-gg/mithril/issues/1076) · [calibration-1076-pixel-frame-typing](../calibration-1076-pixel-frame-typing/)
- Cross-frame composition: [#1081](https://github.com/moumantai-gg/mithril/issues/1081) · [calibration-1081-overlay-cross-frame-composition](../calibration-1081-overlay-cross-frame-composition/)
- Sidecar / base-texture cache: [#931](https://github.com/moumantai-gg/mithril/pull/932)
- Overlay-out-of-Legolas: [#900 series](https://github.com/moumantai-gg/mithril/issues/900)
- Findings: [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) — sub-pixel isotropic similarity, no warp; perceived drift is operational.
- Player-log signals: [Player-Log-Signals § Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures)
