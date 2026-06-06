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
├── AreaCalibration                       (record: drop CalibrationZoom field; schema v2 → v3)
├── Internal / AreaProjectionCore         (drop zoomFactor; layer-1 math only)
├── WorldToTextureCalibration             (drop currentZoom param; output is texture_px)
├── WorldToOverlayCalibration             (drop currentZoom param; compose tex→overlay via MapViewFix)
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
└── IOverlayCaptureSource                 (new — captures the overlay region as GrayImage)

Legolas.Module
├── ViewModels / MapOverlayViewModel      (delete slider + zoom-seed; subscribe to LiveMapViewService.Changed)
├── Hotkeys / RedetectMapViewHotkey       (new)
├── Controllers / OverlayController       (wires trigger sites + capture source)
└── Views / MapOverlay header             (slider removed; view-state badge added)
```

### 4.2 Math

**Layer 1** (`AreaProjectionCore.Project`):

```
pixel = origin + (R × world) × scale
```

where `R` is the rotation+mirror composition. No `zoomFactor`, no `calibrationZoom`, no `currentZoom`. The output `pixel` is in whichever frame the cal lives in — Texture for AutoCal / BundledBaseline cals, Overlay for wizard cals (until #1087 cross-frame composition normalizes through Texture).

**Layer 2** (composition in `WorldToOverlayCalibration.ToOverlay` and equivalents):

```
overlay_px = (texture_px − pan_tex) × viewScale
```

where `(pan_tex, viewScale)` come from a fresh `MapViewFix`. Texture-frame cals feed `texture_px` directly. Overlay-frame cals are routed through Texture-frame composition (`ProjectThroughOverlay`, #1087) before entering layer-2.

When no `MapViewFix` has ever been measured for the current area, `WorldToOverlayCalibration.ToOverlay` returns null and consumers refuse to render (status badge says "not measured"). When a prior fix exists but the latest re-detect failed, the prior fix stays in use and the badge surfaces the failure — the user sees stale-but-coherent markers plus a clear indicator that re-detection is owed.

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

**No silent fallback**: when no fix is available, marker collections are cleared and the badge surfaces the cause.

**Zoom-mismatch banner and `IsZoomMismatchWarningVisible`**: deleted. The mismatch was only meaningful under the broken math.

## 7. Schema migration

`AreaCalibration` record: remove the `CalibrationZoom` property in code.

- Existing user-cal JSON records that carry a non-default `calibrationZoom` field are silently ignored on load (`System.Text.Json`'s unknown-property handling already drops it). One `Information` log is emitted per such cal on first load: `"Migrated AreaCalibration {Area} — dropped CalibrationZoom={Value} (no longer load-bearing)."`. The "first load" gate avoids per-startup log spam.
- Bundled `map-calibration-baseline.json`: records there carry `calibrationZoom = 1.0` (default), which the existing `WhenWritingDefault` serializer rule already omits, so the file is unchanged. The file-wrapper `schemaVersion` is not bumped — the file format hasn't changed.
- `AreaCalibration.SchemaVersion` default is bumped from 1 to 3 (skipping 2 — the #1076 frame-typing bump — to make the no-CalibrationZoom invariant unambiguous from inspection).
- `AreaCalibration.Frame`: unchanged. Texture-frame cals remain the canonical runtime path. Overlay-frame wizard cals continue to round-trip through #1087's cross-frame composition.

## 8. Code-level changes

- `Mithril.MapCalibration/AreaCalibration.cs` — remove `CalibrationZoom`. Bump `SchemaVersion` default to 3.
- `Mithril.MapCalibration/Internal/AreaProjectionCore.cs` — remove `calibrationZoom`, `currentZoom` params; remove `ZoomFactor`. Update `Project`, `Unproject` signatures and bodies.
- `Mithril.MapCalibration/WorldToTextureCalibration.cs` — drop `currentZoom` from `ToTexture` signature.
- `Mithril.MapCalibration/WorldToOverlayCalibration.cs` — drop `currentZoom` from `ToOverlay`; compose via `MapViewFix` for Texture-frame inputs.
- `Mithril.MapCalibration/MapViewFix.cs` (new).
- `Mithril.MapCalibration/ILiveMapViewService.cs` (new).
- `Mithril.MapCalibration/LiveMapViewService.cs` (new).
- `Mithril.MapCalibration.Detection/IMapViewProbe.cs` (new).
- `Mithril.MapCalibration.Detection/CrossCorrelationMapViewProbe.cs` (new).
- `Mithril.Overlay/IOverlayCaptureSource.cs` (new) + concrete impl over the shared window.
- `Legolas.Module/ViewModels/MapOverlayViewModel.cs` — delete the slider, the zoom-seed logic at `OnCalibrationChanged`, `IsZoomMismatchWarningVisible`, `ZoomMismatchText`, `CalibrationZoomLabel`. Subscribe to `LiveMapViewService.Changed`. Update `RebuildCalibrationGhosts`, `MotherlodeMarkerPixels`, `MotherlodeGuidanceOverlay`, `RefreshSurveyPlayerAnchor` to read the current fix and refuse to render when null.
- `Legolas.Module/Services/PlayerLogIngestionService.cs` — `HandleMapTarget` reads the current fix.
- `Legolas.Module/Hotkeys/RedetectMapViewHotkey.cs` (new).
- `Legolas.Module/Controllers/OverlayController.cs` — wire `IOverlayCaptureSource` and `LiveMapViewService`; add status-badge slot.
- `Legolas.Module/Views/MapOverlay header` — slider removed; status badge added (XAML; mind [`docs/wpf-gotchas.md`](../../wpf-gotchas.md)).
- `Mithril.MapCalibration/BundledData/map-calibration-baseline.json` — unchanged at rest (`calibrationZoom` was never serialized for `1.0` defaults; file-wrapper schemaVersion stays at 2).
- DI wiring: `IMapViewProbe`, `ILiveMapViewService`, `IOverlayCaptureSource` registered in their owning modules' service-collection extensions.

The `OverlayWindowService.ResolveComposedOverlayCalibration` ([#1087](https://github.com/moumantai-gg/mithril/pull/1087)) picker stays. Layer-2 composition wraps its output before reaching consumers.

## 9. Testing

- **Math regression**: `AreaProjectionCore` tests verify world → texture-pixel projection for `BundledBaseline` Serbule, Eltibule, Kur Mountains records matches today's projection at the equivalent canonical zoom — confirms the math change doesn't shift canonical projections (only stops misprojecting at non-canonical).
- **Probe unit**: `CrossCorrelationMapViewProbe` against synthetic `GrayImage` scenarios — exact copy → `(pan=0, viewScale=1)`; scaled copy → expected scale; panned copy → expected pan; noise → null with confidence below gate. Plus a golden against a captured Serbule overlay + bundled base texture.
- **Service**: `LiveMapViewService` — concurrent `RefreshAsync(area)` deduped; per-area state isolated; `Changed` fires on UI thread; failed probe leaves the prior fix in place (markers stay rendered from last good measurement); the status badge surfaces the failure separately.
- **Migration**: v2 record with `calibrationZoom: 0.42` round-trips through load → save → load, ends with no field at rest, one Info log emitted.
- **VM**: `MapOverlayViewModel` does not render markers when `LiveMapViewService.GetCurrent(currentArea)` is null; renders correctly when fix is present.
- **E2E (manual)**: in PG on Serbule at off-cal zoom, toggle validation → detection completes → ghosts render at correct positions. Reproduces the #1095 trigger condition; closes it.

## 10. Out of scope

Filed as follow-up issues at PR-open time:

1. **Periodic background detection**: a low-rate refresh loop that catches PG pan/zoom without an explicit user gesture. Current design is gesture-driven (and a hotkey covers the resync case); periodic is a follow-up if it proves needed.
2. **PG-log-signal verification for pan/zoom**: spot-check whether PG emits anything observable on `Player.log` for world-map UI state changes. If yes, drive `LiveMapViewService` from the signal instead of (or alongside) screenshot probing.
3. **Wizard solving directly in Texture frame**: today's wizard solves in Overlay frame and rides cross-frame composition for runtime use. A wizard that solves directly in Texture frame would retire the Overlay frame from wizard solves and simplify the runtime path further. Independently scoped.

## 11. References

- Issue: [#1095](https://github.com/moumantai-gg/mithril/issues/1095)
- Logging counterpart (shipped): [#1093](https://github.com/moumantai-gg/mithril/issues/1093) · [calibration-logging-pass-1093](../calibration-logging-pass-1093/)
- Pixel-frame typing: [#1076](https://github.com/moumantai-gg/mithril/issues/1076) · [calibration-1076-pixel-frame-typing](../calibration-1076-pixel-frame-typing/)
- Cross-frame composition: [#1081](https://github.com/moumantai-gg/mithril/issues/1081) · [calibration-1081-overlay-cross-frame-composition](../calibration-1081-overlay-cross-frame-composition/)
- Sidecar / base-texture cache: [#931](https://github.com/moumantai-gg/mithril/pull/932)
- Overlay-out-of-Legolas: [#900 series](https://github.com/moumantai-gg/mithril/issues/900)
- Findings: [Legolas-Calibration-Findings](https://github.com/moumantai-gg/mithril/wiki/Legolas-Calibration-Findings) — sub-pixel isotropic similarity, no warp; perceived drift is operational.
- Player-log signals: [Player-Log-Signals § Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures)
