# Map auto-calibration: sparse-interior locate-stage fallback (mithril#1061)

**Issue:** [mithril#1061](https://github.com/moumantai-gg/mithril/issues/1061)
**Status:** active
**Depends on (shipped):** [#1009](https://github.com/moumantai-gg/mithril/issues/1009) (ORB+RANSAC locate-stage cutover), [#1041](https://github.com/moumantai-gg/mithril/issues/1041) (`MapSceneRef` per-scene keying)
**Composes with:** [#1036](https://github.com/moumantai-gg/mithril/issues/1036) (pin-anchor solver) — independent; both needed for end-to-end dungeon autocal but either can ship first.

## 1. Problem

`FeatureMatchingRefiner` — the ORB+Lowe locate stage shipped in #1009 — returns `MapRegionRefineResult.None` on sparse-interior maps (dungeons, basements, caves) because the captured frame has too few distinguishable ORB keypoints. Lowe's ratio test rejects most candidates as ambiguous (best vs. second-best are too close — corridor segments produce nearly identical binary descriptors).

Empirical evidence (issue body, 2026-06-03): three live `Map_GoblinDungeon` attempts produced 2, 3, 3 Lowe survivors against the gate's floor of 4. Engine sets `mapRect = null`, outcome `rejected-map-not-located`, user sees *"zoom the map all the way out and draw the box tightly"* — guidance that doesn't apply because the failure is intrinsic to sparse line-art inputs, not framing.

For outdoor maps with rich texture (Eltibule: 696 Lowe survivors, 645 RANSAC inliers on the same day) ORB+Lowe remains the right primary path. This spec adds a fallback for the regime where #1009's assumptions don't hold.

## 2. Converged algorithm (round-5 recommendation)

[Round-5 comment on #1061](https://github.com/moumantai-gg/mithril/issues/1061#issuecomment-4623244273) is the canonical algorithm reference. Summary — every element is load-bearing:

1. **Sobel gradient magnitude** on both capture and texture (3×3 kernel, CV_32F → 8U via min-max normalize). Continuous gradient response, no binary thresholding. Round-5 measured a consistent 1.5–2× NCC strengthening over Canny-edge binary across the corpus and recovers the correct basin on Eltibule full-frame where binary-edge was trapped at a small-scale corner.
2. **100 px zero padding on the capture's Sobel** (`BORDER_CONSTANT`, all four sides). `Cv2.MatchTemplate` requires `template ≤ image` at every test position, which silently caps the search space when the texture's display origin would put part of it past the original capture's edge. HogansKeep-223119 truth `(126, 35, 0.7227)` puts the template bottom 34 px past the capture's bottom; unpadded matchTemplate cannot reach the truth position. 100 px gives generous spillover headroom while keeping the pyramid's coarse stage cheap. The denominator-depression cost at the boundary is empirically small (5–10% NCC dip; peak position is preserved because in-capture overlap drives the numerator).
3. **3-level Gaussian pyramid** (`Cv2.PyrDown` × 2): full ladder at quarter resolution (level 2), narrow ladder at half (level 1) centred on the L2 winner ±2 steps, narrow ladder at full (level 0) centred on the L1 winner ±2 steps. Reduces runtime ~10× over a single full-resolution pass with bit-identical results on the corpus.
4. **Parabolic peak refinement** on the L0 ladder's scale axis: `refinedScale = winner + step * 0.5 * (y_{i-1} - y_{i+1}) / (y_{i-1} - 2*y_i + y_{i+1})`, gated on concave-down curvature. Re-match at refinedScale to recover the refined response map.
5. **Sub-pixel translation refinement** via 2D parabolic on the response map's 3×3 neighborhood around the integer peak (independent X/Y curves, clamped to ±1 px). Translate `(tx, ty)` back to original capture coords by subtracting the pad.

Ladder parameters: `ScaleMin = 0.20`, `ScaleMax = 2.00` (bumped from 1.20 by [mithril#1153](https://github.com/moumantai-gg/mithril/pull/1181) after the original ceiling was found to truncate at in-game zooms above 1.20×), `ScaleStep = 0.02`. Round 4 confirmed PG is empirically isotropic similarity — no anisotropic (`sx, sy`) search.

## 3. Dispatch

```
1. Try FeatureMatchingRefiner (ORB+Lowe — existing #1009 primary)
   - Outdoor with rich texture → ORB succeeds
   - Sub-scene interior → ORB returns None (survivors < 4) OR
     gate-rejects (inliers / ratio / rotation)

2. On primary's None-or-reject, run SobelPaddedPyramidRefiner

3. If fallback NCC < FallbackNccFloor (default 0.20)
   → return None; engine surfaces "couldn't locate the map confidently"
     reason (input pathology — try different zoom or explore more of the area)
```

**No texture-key prefix gate.** Per round-5: post-#1041 all captured bundles key as `Map_<X>` (pre-migration `Area<X>` is legacy). Fall-through ordering does the discrimination automatically — outdoor captures pass through ORB's rich-texture path; sub-scene interiors fall through to the new fallback. A prefix gate would re-introduce the `Area<X>` coupling that #1041 just retired.

## 4. Integration boundary

**New refiner alongside, composite dispatcher in front.** Three new types in `Mithril.MapCalibration.Detection`:

1. `SobelPaddedPyramidRefiner : IMapRegionRefiner` — clean rewrite of `TemplateMatchSobelPaddedPyramid3` (the round-5 production candidate in `tools/MapCalibrationFromScreenshot/SparseLocateSpike.cs`). The spike is throwaway diagnostic code; the production implementation should follow the algorithm faithfully but be re-implemented cleanly (no copy-paste, no diagnostic overlay rendering, no SpikeResult tuple).
2. `CompositeMapRegionRefiner : IMapRegionRefiner` — owns dispatch. Composes a primary `IMapRegionRefiner` (ORB) and a fallback `IMapRegionRefiner` (Sobel-padded-pyramid). Calls primary first; on `AcceptedRect is null` (whether "no fit" or "gate rejected"), calls fallback.
3. `IAreaContextualRefiner` (tiny marker interface) — `void SetAreaKey(string?)`. Implemented by `FeatureMatchingRefiner` (existing surface) and `CompositeMapRegionRefiner` (forwards to its inner refiners that implement it). `AutoCalibrationEngine.RunAttemptCoreAsync` / `CheckDriftAsync` change their runtime cast from `if (_refiner is FeatureMatchingRefiner fm)` to `if (_refiner is IAreaContextualRefiner ac)` — the same FM-specific cache pre-warm still fires, but the engine no longer hard-couples to a concrete refiner type.

**DI swap** in `DetectionServiceCollectionExtensions.AddMithrilMapCalibrationDetection`:

```csharp
// Before
services.AddSingleton<IMapRegionRefiner>(sp => new FeatureMatchingRefiner(...));

// After
services.AddSingleton<FeatureMatchingRefiner>(sp => new FeatureMatchingRefiner(...));
services.AddSingleton<SobelPaddedPyramidRefiner>(sp => new SobelPaddedPyramidRefiner(...));
services.AddSingleton<IMapRegionRefiner>(sp => new CompositeMapRegionRefiner(
    primary: sp.GetRequiredService<FeatureMatchingRefiner>(),
    fallback: sp.GetRequiredService<SobelPaddedPyramidRefiner>(),
    logger: ...));
```

Composite refiner exists for the *engine* (one `IMapRegionRefiner` it talks to). Concrete refiner types stay registered for tests + future direct consumers.

## 5. mapRect schema implications

**Keep `MapRect` int-valued; round at the refiner boundary.** The spike's `TemplateMatchSobelPaddedPyramid3` returns doubles (`tx 127.5`, `scale 0.720`); the production refiner rounds `originX = (int)Math.Round(tx)`, etc., before constructing the `MapRect`.

Rationale:
- `ImageOps.Crop` is integer-pixel.
- The solver downstream consumes `MapRect.OriginX/Y/Width/Height` as ints.
- Widening `MapRect` to `double` would touch ~20 sites for no measurable win — sub-pixel translation collapses to int crop immediately.
- The un-rounded `Scale`, `Tx`, `Ty` survive in `LocateMetrics` (already `double` fields) and flow into the bundle's `LocatorBestJson` for diagnostic triage.

`MapRect` schema is unchanged. **No `MapRect` schema version bump.**

## 6. LocateMetrics + bundle JSON additions

### `LocateMetrics` (additive)

Two new fields:

- `Provenance` — enum, two values: `OrbRansac` (existing) and `SobelPaddedPyramid` (new). Default value for legacy call sites is `OrbRansac`.
- `Confidence` — `double?`. NCC peak for the Sobel fallback; null for ORB (the gate already reads `InlierCount` / `InlierRatio` — those carry the ORB confidence signal).

For the Sobel fallback: `InlierCount = 0`, `CandidateCount = 0`, `InlierRatio = 0`, `RotationDegrees = 0`, `Mirror = false`, `ResidualPixels = 0`. Consumers route on `Provenance`.

### `LocatorBestJson` (SchemaVersion 1 → 2)

New optional fields, all `null` on a v1 ORB-only attempt:

- `Algorithm` — string, defaults `"orb-lowe"`. Set to `"sobel-padded-pyramid"` for fallback attempts.
- `FallbackNcc` — `double?`. NCC peak from the fallback's L0 refined re-match.
- `PadPx` — `int?`. Pad applied to the capture's Sobel (always 100 for v1, but recorded so future tuning is visible in old bundles).
- `LevelScales` — `double[]?`. The three pyramid winners `[L2, L1, refined]`, for triage when the pyramid lands at a wrong basin at L2 but the L0 NCC still satisfies the floor.

Bump `LocatorBestJson.SchemaVersion` to 2. Reader treats absence of new fields as v1 ORB-only. Sink writes v2 unconditionally; the new fields are null on ORB-primary success.

`AttemptJson.SchemaVersion` does not change — its shape is unchanged.

## 6.5. Tunable knobs via versioned settings store

Every magic number that affects locate-stage behaviour is promoted to a property on `MapCalibrationLocateOptions` and persisted as a versioned JSON file. The settings infrastructure is already canonical — `AddMithrilVersionedSettings<T>` (used today for `TelemetrySettings`, `ShellSettings`, etc.) wires `JsonSettingsStore<T>` + `IVersionedState<T>.Migrate` dispatch + `SettingsAutoSaver<T>` hosted service in one extension method.

### What goes in the JSON

All `MapCalibrationLocateOptions` properties — both pre-existing ORB knobs and new fallback knobs:

| Knob | Default | Owner |
|---|---|---|
| `InlierFloor` | 50 | ORB (existing) |
| `InlierRatioFloor` | 0.50 | ORB (existing) |
| `MaxRotationDegrees` | 0.5 | ORB (existing) |
| `OrbNFeatures` | 8000 | ORB (existing) |
| `LoweRatio` | 0.75 | ORB (existing) |
| `RansacReprojectionThresholdPx` | 3.0 | ORB (existing) |
| `FallbackNccFloor` | 0.20 | Sobel (new in this spec) |
| `FallbackPadPx` | 100 | Sobel (new in this spec) |
| `ScaleMin` | 0.20 | Sobel ladder bounds (promoted from `const`) |
| `ScaleMax` | 2.00 | Sobel ladder bounds (promoted from `const`; bumped 1.20 → 2.00 by [mithril#1153](https://github.com/moumantai-gg/mithril/pull/1181)) |
| `ScaleStep` | 0.02 | Sobel ladder bounds (promoted from `const`) |
| `MinScaledDim` | 20 | Sobel min template dim — full res (promoted from `const`) |
| `MinScaledDimHalf` | 10 | Sobel min template dim — half-res (promoted from `const`) |
| `MinScaledDimCoarse` | 5 | Sobel min template dim — quarter-res (promoted from `const`) |

Detect-pipeline constants in `AutoCalibrationEngine` (`RenderSizePx`, `LowNcc`, `TypeFloor`, `BlobOptions`) stay where they are — they belong to whatever issue actually moves the detect/solve tuning surface, not to #1061.

### File location + schema version

- Path: `%LocalAppData%/Mithril/map-calibration-locate.json` (sibling to `shell.json`, `telemetry.json`, etc. — the existing per-machine settings convention).
- Schema version: `MapCalibrationLocateOptions.Version = 1`. Fresh instance starts at `SchemaVersion = 1`; missing file → defaults from constructor; absent `schemaVersion` key in legacy file → defaults to `1` on deserialise (no pre-existing on-disk shape to migrate from for v1).
- File is auto-saved on every property change (debounced) via `SettingsAutoSaver<T>` already wired by `AddMithrilVersionedSettings`.

### Layering — where the wiring lives

Three projects involved:

- **`Mithril.MapCalibration.Detection`** (current home of `MapCalibrationLocateOptions`) — does NOT reference `Mithril.Shared`. Adds a one-line `ProjectReference` to `Mithril.Persistence` (zero-dependency project) so the options type can implement `IVersionedState<MapCalibrationLocateOptions>`. Adds the STJ source-gen context `MapCalibrationLocateOptionsJsonContext` here.
- **`Mithril.MapCalibration.Capture`** (already references `Mithril.Shared`) — its DI extension `AddMithrilMapCalibrationCapture` calls `services.AddMithrilVersionedSettings<MapCalibrationLocateOptions>(...)` with the settings path. This pre-registers the singleton; Detection's existing `services.TryAddSingleton<MapCalibrationLocateOptions>()` becomes a no-op fallback (semantics already correct).
- **`Mithril.Shell`** (composition root) — passes the settings directory into the Capture extension. Mirrors the existing `o.AssetCacheDir` flow.

This respects the established constraint that `Mithril.MapCalibration.Detection` doesn't reference `Mithril.Shared`.

### `Migrate` is a no-op stub for v1

```csharp
public static MapCalibrationLocateOptions Migrate(MapCalibrationLocateOptions loaded)
{
    if (loaded.SchemaVersion >= Version) return loaded;
    // v0 → v1: identity. No pre-existing on-disk shape — first persisted version.
    return loaded;
}
```

Future schema changes get a doc block above `SchemaVersion` describing each version delta, mirroring the `LegolasSettings.Migrate` convention.

### Why this scope (and not wider)

- **Detect-pipeline constants** (`RenderSizePx`, `LowNcc`, `TypeFloor`, `BlobOptions` in `AutoCalibrationEngine`) are a separate concern — they affect deviation-blob detection + the synthesis-J solve, not locate. Promoting them belongs to whatever future issue actually motivates tuning them.
- **A single mega-tuning file** would couple unrelated lifecycles (a detect-pipeline change should not bump a locate-stage schema). Keep them as separate stores when they materialise.

## 7. Confidence-floor reject path

Add `FallbackNccFloor` (default `0.20`) to `MapCalibrationLocateOptions`. When the fallback's refined NCC is below this floor, `SobelPaddedPyramidRefiner` returns `MapRegionRefineResult(AcceptedRect: null, RawFitRect: <fit>, Metrics: <metrics with Confidence>)` — the engine sees a no-fit, populates `attempt.LocatorRawFit` + `LocatorMetrics` (already happens), and surfaces a distinct outcome reason.

**New outcome category** in `OutcomeVocabulary`: `rejected-map-low-confidence`. User-facing reason:

> *"couldn't locate the map confidently — try a different zoom or explore more of the area first"*

This is distinct from `rejected-map-not-located` (the existing "no fit at all" outcome), so daily JSON + bundle viewers can tell input-pathology rejects from no-fit rejects.

`AutoCalibrationEngine.RunAttemptCoreAsync` branches on `refineResult.Metrics?.Provenance` to choose the reason text + outcome category. ORB rejection text is unchanged; fallback rejection gets the new copy.

## 8. Performance budget

Round-5 corpus (15 indoor bundles): **110–370 ms** per fallback attempt (median ~135 ms). Largest case: GoblinDungeon, texture 398×1024 → ~343–373 ms.

Cost is only paid on indoor maps; ORB-primary succeeds on outdoor in <200 ms. Cold ORB cache miss can already cost ~200 ms today, so worst-case adds the same order of magnitude. Hotkey-trigger latency budget (per existing engine instrumentation) is generous enough.

If a future precision lever is needed: 4-level pyramid is the next free win (round 5 didn't measure but cites it as plausible 50–80 ms territory).

## 9. Telemetry

Per `docs/perf-trace-schema.md` and `Mithril.Shared.Diagnostics.Telemetry`:

Two new spans on `MithrilActivitySources.MapCalibration`:

- `calibration.refine.primary` — wraps the FM primary call inside `CompositeMapRegionRefiner`. Tag: `outcome` ∈ `{accepted, rejected, no_fit}`.
- `calibration.refine.fallback` — wraps the Sobel-padded-pyramid call. Tags: `outcome` ∈ `{accepted, rejected_low_confidence, no_fit}`, `ncc` (double), `scale` (double), `pad_px` (int), `levels` (3).

These nest under the existing `calibration.refine` span (which itself nests under `calibration.attempt`) — three-deep waterfall in Seq shows primary-vs-fallback split per attempt.

Metric: `calibration.locate.algorithm` histogram or counter on `MithrilMeters.MapCalibration` keyed on `algorithm` tag ∈ `{orb_lowe, sobel_padded_pyramid}`. Lets the OTLP export surface a "what fraction of attempts hit fallback" graph without parsing logs.

Update `docs/perf-trace-schema.md` with the new span names, tags, and metric instrument when the implementation lands (per CLAUDE.md instrumentation convention).

## 10. Out of scope

Carried forward from round-5 — documented limitations, not v1 work:

- **WolfCave-223519 1% scale residual.** NCC peak is genuinely flat in the local neighborhood; sub-pixel refinement correctly bails to `(0, 0)`. Below the user-visible overlay threshold for an in-game UI placement. If a future user reports "off by X pixels at high-zoom WolfCave," the next precision lever is a renderer-blur-aware blur kernel on the template at low-to-mid scales.
- **HogansKeepBasement-223115 "center good, worse outward."** Same precision lever applies.
- **Eltibule cropped-to-mapRect 7–22% NCC drop under padding.** Small capture sizes (562–1006 px) make the 100 px zero-pad's denominator contribution disproportionate. Moot in production because ORB handles Eltibule with 645+ inliers and the fallback never runs. Worth knowing if the algorithm is ever wanted as a primary path.
- **Late-pixel-capture pathology.** Logout/transition screenshots produce high-confidence-looking results on near-uniform input. A frame-validity precheck (minimum Sobel-magnitude variance, or minimum Canny-edge count) before running the locate stage at all is a separate concern. Track as a follow-up issue when v1 ships.
- **AKAZE / Generalized-Hough / Borgefors / phase-correlate.** All explored in rounds 1–3; none survived. AKAZE-with-threshold-tuning could be a deferred sub-investigation if 110–370 ms ever turns out to be insufficient.
- **Anisotropic refinement / inverted-direction matchTemplate.** Explored and ruled out in rounds 4–5. PG is empirically isotropic; padding handles spillover better than direction inversion.
- **Pin-anchor solver behaviour on dungeons.** That is [#1036](https://github.com/moumantai-gg/mithril/issues/1036)'s territory. This spec only changes what `IMapRegionRefiner` returns; whatever the solver does with that rect downstream is independent.
- **Capture-box UI** ([#964](https://github.com/moumantai-gg/mithril/issues/964), [#965](https://github.com/moumantai-gg/mithril/issues/965), [#969](https://github.com/moumantai-gg/mithril/issues/969)) — independent.
- **Per-area solver-gate thresholds** ([#1002](https://github.com/moumantai-gg/mithril/issues/1002)) — independent.

## 11. Acceptance criteria

A correctness regression test recovers `HogansKeep-223119` truth `(126, 35, 0.7227)` within `(±2 px, ±2 px, ±0.005)` from the on-disk corpus bundle.

Three end-to-end criteria:

1. The three failed `Map_GoblinDungeon` bundles from 2026-06-03 (in the diagnostics calibration folder, captured in the issue body) now produce `AcceptedRect != null` from the composite refiner with `Algorithm = "sobel-padded-pyramid"` and `FallbackNcc ≥ 0.4`.
2. The Eltibule accepted control bundle from the same day still produces `Algorithm = "orb-lowe"` — ORB primary still wins on outdoor (no regression).
3. A bundle that fails both primary and fallback (synthetic near-uniform input simulating the late-pixel-capture pathology) surfaces `rejected-map-low-confidence` distinct from `rejected-map-not-located`.

Performance: median fallback runtime under 200 ms across the 15-bundle round-5 corpus; max under 400 ms.

Settings store (§6.5):

4. **Round-trip.** A freshly-deserialised options file produces identical knob values to the in-memory defaults when the file is absent; producing a file with `"scaleMin": 0.15` is honoured by the next attempt's fallback ladder.
5. **Migrate dispatch.** A file with `{ "schemaVersion": 0, "fallbackNccFloor": 0.30 }` loads, runs `Migrate`, and gets saved back with `"schemaVersion": 1`. No value loss; the customised `fallbackNccFloor` survives.
6. **Auto-save.** Mutating `options.FallbackNccFloor = 0.25` at runtime debounce-persists to disk via the wired `SettingsAutoSaver<MapCalibrationLocateOptions>` hosted service (mirrors the existing `TelemetrySettings` auto-save behaviour).

## 12. References

- [Round 5 — converged production candidate](https://github.com/moumantai-gg/mithril/issues/1061#issuecomment-4623244273) — algorithm + corpus data.
- [Round 4 — matchTemplate(edge) corpus tracking](https://github.com/moumantai-gg/mithril/issues/1061#issuecomment-4618463378) — superseded but explains scale-precision lever.
- [Round 2 — Borgefors recommendation](https://github.com/moumantai-gg/mithril/issues/1061#issuecomment-4616630818) — superseded; explains why Borgefors was ruled out (scale-floor bias).
- Spike reference: branch `claude/ecstatic-mccarthy-b6e5ad`, file `tools/MapCalibrationFromScreenshot/SparseLocateSpike.cs`, function `TemplateMatchSobelPaddedPyramid3` (lines ~787–908). Throwaway — delete with the rest of the spike harness once production is in.
- [docs/perf-trace-schema.md](../../perf-trace-schema.md) — telemetry shape contract.
- [#1009 closed](https://github.com/moumantai-gg/mithril/issues/1009) — the ORB+Lowe locate path this fallback complements.
- [#897 gate verdict](https://github.com/moumantai-gg/mithril/issues/897) — flagged texture-deviation local-NCC as the sparse-area direction; superseded by Sobel-magnitude-NCC here.
