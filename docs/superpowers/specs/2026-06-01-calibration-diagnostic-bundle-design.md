# Per-attempt Calibration Diagnostic Bundle

**Status:** design spec (output of brainstorming, 2026-06-01). Approved end-to-end. Implementation plan to follow in a GitHub issue body.

## Goal

Replace today's flat capture-frame dump (a single PNG per capture written from `CaptureService`) with a structured **per-attempt diagnostic bundle** written from `AutoCalibrationEngine`. Each bundle is a self-describing subdirectory under `%LocalAppData%/Mithril/diagnostics/calibration/` containing the captured screenshot, intermediate ECC-aligned artifacts, the deviation map, the detection set, the recovered calibration, three annotated visualizations, and a JSON header that enumerates what's present.

The bundle exists so a separate diagnostic tool (the synthesis-probe — `tools/MapCalibrationFromScreenshot` on the `claude/synthesis-probe-impl` branch) can consume `04-maprect.json`, `07-deviation.png`, `11-recovered-cal.json`, and the rest as inputs to its `--aligned-deviation`, `--maprect-json`, and `--truth-cal` flags. Cleanly producing this bundle from the live engine unblocks the synthesis-probe's open questions documented in `docs/superpowers/specs/2026-06-01-synthesis-probe-diagnostic-design.md` §"Open questions to pick up next".

Lineage: extends [#966](https://github.com/moumantai-gg/mithril/issues/966) (original `CaptureFrameDumper` toggle) and [#978](https://github.com/moumantai-gg/mithril/issues/978) (ECC sub-pixel registration).

## Bundle layout

**Dump root** (unchanged): `%LocalAppData%/Mithril/diagnostics/calibration/`

**Per-attempt subdir name:** `<area>-<yyyyMMdd>-<HHmmss>-<fff>-<outcome>` (UTC timestamp via `DateTimeOffset.UtcNow` + `CultureInfo.InvariantCulture`).

Example tree:

```
%LocalAppData%/Mithril/diagnostics/calibration/
  AreaEltibule-20260601-123012-696-accepted/
    01-attempt.json                    # header — outcome, timestamps, file inventory
    02-screenshot-raw.png              # BGRA32 captured frame
    03-screenshot-gray.png             # Gray8 derivation
    04-maprect.json                    # ECC-refined sub-rect + texture dims
    05-base-texture-resampled.png      # base texture resized to crop dims
    06-aligned-screenshot.png          # gray screenshot cropped to ECC rect
    07-deviation.png                   # max(0, 06 − 05), Gray8
    08-detections.png                  # detections overlay (parity w/ tool --debug-image)
    09-projection-overlay.png          # refs projected via recovered cal (parity w/ --projection-overlay)
    10-detections.json                 # typed detections in structured form
    11-recovered-cal.json              # AreaCalibration + inlier list
  AreaEltibule-20260601-122726-226-rejected-solve-insufficient-inliers/
    01-attempt.json
    02-screenshot-raw.png
    03-screenshot-gray.png
    04-maprect.json
    05-base-texture-resampled.png
    06-aligned-screenshot.png
    07-deviation.png
    08-detections.png
    10-detections.json
    # 09 and 11 absent — calibration was rejected
```

Numbered file prefixes ensure pipeline-order display in any file browser. **Write-what-you-have policy:** a file is written only when the data needed to produce it exists; absent files don't get placeholders. The `files` block in `01-attempt.json` enumerates which files are present.

### Outcome vocabulary

| Outcome | Trigger | Bundle written? |
|---|---|---|
| `accepted` | `Result.Calibration` is not null (gate accepted, persisted via `IMapCalibrationService.SaveUserRefinement`) | Yes |
| `rejected-no-area` | `_areaState.CurrentArea` is null/empty | **No** (nothing captured) |
| `rejected-pg-not-foreground` | `_windowLocator.Locate()` returned null | **No** (nothing captured) |
| `rejected-no-bbox` | `_region.Current` is null | **No** (nothing captured) |
| `rejected-capture-failed` | `_capture.CaptureMapAsync` returned null | Yes (`01-attempt.json` only) |
| `rejected-no-base-texture` | `ResolveBaseTextureAsync` returned null after sidecar retry | Yes |
| `rejected-map-not-located` | `_refiner.Refine` returned null | Yes |
| `rejected-clamp-degenerate` | `ClampToFrame` returned null | Yes |
| `rejected-solve-no-detections` | `result.RejectReason` matches "no detections" category | Yes |
| `rejected-solve-insufficient-inliers` | `result.RejectReason` matches "insufficient inliers" category | Yes |
| `rejected-solve-residual` | `result.RejectReason` matches "residual" category | Yes |
| `rejected-solve` | `result.Calibration is null` with any other / unmapped reject reason | Yes |
| `error` | Engine threw an unhandled exception inside the pipeline | Yes (whatever was captured before throw) |

The three pre-capture rejects (`no-area`, `pg-not-foreground`, `no-bbox`) intentionally do not produce a bundle: there is no data to dump and writing a near-empty subdir on every keypress would clutter the dir. The full `RejectReason` string is preserved verbatim in `01-attempt.json`; the dir-name suffix is a small fixed category set for at-a-glance browsing.

## Architecture

### Approach: explicit-write context (Approach E)

A plain mutable **`CalibrationAttemptContext`** is constructed at the top of `AutoCalibrationEngine.TryCalibrateCurrentAreaAsync`. As each pipeline stage produces its bytes/metadata, the engine assigns a property. A `try { … } finally { _sink.Write(attempt); }` wraps the pipeline body. On every exit path — success, gate-reject, exception, cancellation — the sink writes whatever was assigned.

Why this shape:

- **Property-bag context** decouples the dumper from pipeline-stage ordering. Reorder freely.
- **Explicit `Write` in `finally`** makes the IO call site visible (vs hidden in `Dispose`) without losing partial-write-on-exception semantics.
- **Single owner of the lifecycle** (the public entry method) makes "forgetting the finally" effectively impossible: the public method is ~5 lines wrapping a private pipeline body.

Trade-offs vs alternatives considered:

- vs **inlining the dump code in `AutoCalibrationEngine`**: would add ~150 lines of dump bookkeeping to a 421-line file that already mixes orchestration concerns. Rejected.
- vs a **stateful dumper with `Record*` methods**: implicit method-order coupling + a forgettable `Finalize` obligation. The approved approach collapses both via property-bag + `try/finally`.
- vs **one-shot `Write(snapshot)` with engine-local threading**: forces every `Fail()` exit (6 paths today) to assemble a snapshot or thread ~8 nullable locals. Rejected.
- vs **`IDisposable` context with IO in `Dispose`**: equivalent semantics but hides the IO call site. Rejected — `finally + Write` is structurally identical and readable.

### New types

All in `Mithril.MapCalibration.Capture`:

- `CalibrationAttemptContext` — plain data carrier. Constructed with `(area, startUtc)`. Nullable mutable properties for each pipeline-stage output: `RawCapture`, `GrayCapture`, `BaseTextureResampled`, `MapRect`, `AlignedCrop`, `AlignedTexture`, `Detections`, `Result`, `References`, `Outcome`, `ExceptionInfo`. `FinalizedUtc` is set by the sink at write time.
- `ICalibrationAttemptBundleSink` — interface: `void Write(CalibrationAttemptContext context)`. Single method, fail-soft by contract — no exception propagates.
- `FilesystemCalibrationAttemptBundleSink` — concrete sink. Owns the dump dir, derives the subdir name, encodes PNGs, serializes JSONs via source-generated context.
- `NullCalibrationAttemptBundleSink` — no-op. Used when `CaptureDiagnosticsOptions.DumpCalibrationBundles` is false.
- `AttemptBundleVisualizer` — internal helper for the three annotated PNGs (`08-detections.png`, `09-projection-overlay.png`) and the deviation byte-math (`07-deviation.png`). WPF `DrawingVisual` + `RenderTargetBitmap` only — no `System.Drawing` (#921 guard).
- `CalibrationBundleJsonContext : JsonSerializerContext` — source-generated serializer context for the four JSON shapes.

### Changed types

- `AutoCalibrationEngine` — refactored so `TryCalibrateCurrentAreaAsync` is a thin `try/finally` wrapper around a private `RunAttemptCoreAsync` that runs the pipeline. Gains one constructor dep: a sink factory (or a direct `ICalibrationAttemptBundleSink` resolved per attempt by a small selector class so the toggle can flip at runtime).
- `CaptureService` — drops the two `if (_diagnostics.DumpCaptureFrames)` blocks at [CaptureService.cs:65-76](../../../src/Mithril.MapCalibration.Capture/CaptureService.cs:65). No longer holds a `CaptureFrameDumper`.
- `CaptureDiagnosticsOptions` — `DumpCaptureFrames` renamed to `DumpCalibrationBundles`; `DumpGrayFrames` removed (now folded into the bundle unconditionally). Mirrored from `ShellSettings.DumpCalibrationBundles` via the existing `CaptureDiagnosticsMirror` machinery.
- `CalibrationSolveResult` — gains `IReadOnlyList<TypedDetection>? Detections` init-only property after the existing `Inliers` (same shape — non-positional, default null, non-breaking).
- `MapRect` — gains `TextureToScreenshot(double, double)` sibling of the existing `ScreenshotToTexture`. One-liner; needed by the projection-overlay renderer.
- `ShellSettings` — `DumpCalibrationCaptureFrames` renamed to `DumpCalibrationBundles`; `DumpCalibrationGrayFrames` removed. The existing convention is "purely additive fields → no schema bump (missing key defaults false on load)"; **a rename + a removal IS bump-worthy** because old-shape JSON with `DumpCalibrationCaptureFrames=true` would otherwise silently load with `DumpCalibrationBundles=false`. So `ShellSettings.Version` increments, and `Migrate` translates the old key. Pattern per project memory `settings_schema_migration_pattern`.
- `DiagnosticsSettingsViewModel` — gains `[RelayCommand] OpenCalibrationDumpDirectory()` — sibling of `OpenLogDirectory` ([DiagnosticsSettingsViewModel.cs:135](../../../src/Mithril.Shell/ViewModels/DiagnosticsSettingsViewModel.cs:135)).
- `DiagnosticsSettingsView.xaml` — "Open folder" button next to the existing `CalibrationDumpDirectoryHint` text block; remove the `DumpCalibrationGrayFrames` checkbox at line 66.

### Retired

- `CaptureFrameDumper` as a separately-wired service. Its PNG-encoding logic stays alive as a low-level static helper inside the sink (or is absorbed entirely into `FilesystemCalibrationAttemptBundleSink`). The public static `DumpDirectory` property migrates to the sink so the settings-UI hint string still has a single source of truth.

### Wiring

DI registration (in `CaptureServiceCollectionExtensions.AddMithrilMapCalibrationCapture`):

- `CalibrationAttemptBundleSinkSelector` singleton — resolves the live sink at attempt-construction time by reading `CaptureDiagnosticsOptions.DumpCalibrationBundles`. Lets users flip the toggle in settings without an app restart.
- `AutoCalibrationEngine` constructor gets the selector.

Engine flow (illustrative):

```csharp
public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
{
    var attempt = new CalibrationAttemptContext(_areaState.CurrentArea ?? "", DateTimeOffset.UtcNow);
    var sink = _sinkSelector.Resolve();
    try
    {
        return await RunAttemptCoreAsync(attempt, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        attempt.Outcome = "error";
        attempt.ExceptionInfo = $"{ex.GetType().Name}: {ex.Message}";
        throw;
    }
    finally
    {
        sink.Write(attempt);   // fail-soft inside Write — never throws into the engine
    }
}
```

`RunAttemptCoreAsync` is the existing pipeline body with property assignments inserted at each stage. The three pre-capture rejects (`no-area`, `pg-not-foreground`, `no-bbox`) set `attempt.Outcome` to the matching short string; the sink reads `Outcome` and writes nothing when it matches one of the no-write outcomes.

## JSON schemas

All roots carry `"schemaVersion": 1`. Property names are camelCase. Source-generated via `CalibrationBundleJsonContext`.

### `01-attempt.json` — header, always present

```json
{
  "schemaVersion": 1,
  "area": "AreaEltibule",
  "attemptStartedUtc": "2026-06-01T12:30:12.696Z",
  "attemptFinalizedUtc": "2026-06-01T12:30:14.812Z",
  "outcome": "accepted",
  "rejectReason": null,
  "engineVersion": "0.5.0+e56477fa",
  "files": {
    "rawScreenshot": "02-screenshot-raw.png",
    "grayScreenshot": "03-screenshot-gray.png",
    "mapRect": "04-maprect.json",
    "baseTextureResampled": "05-base-texture-resampled.png",
    "alignedScreenshot": "06-aligned-screenshot.png",
    "deviation": "07-deviation.png",
    "detectionsImage": "08-detections.png",
    "projectionOverlay": "09-projection-overlay.png",
    "detections": "10-detections.json",
    "recoveredCalibration": "11-recovered-cal.json"
  }
}
```

- `outcome`: stable string from the outcome vocabulary table above.
- `rejectReason`: raw `result.RejectReason` (or `"{ExceptionType}: {Message}"` for `error`). Null on accept.
- `engineVersion`: assembly informational version of `Mithril.MapCalibration.Capture` (includes the git sha) — so a synthesis-probe replay can know which engine produced the bundle.
- `files`: each value is the relative filename, or `null` when that file wasn't written. A consumer reads this to know what's present without globbing.

### `04-maprect.json`

```json
{
  "schemaVersion": 1,
  "originX": 12,
  "originY": 18,
  "width": 1192,
  "height": 1020,
  "textureWidth": 4096,
  "textureHeight": 4096,
  "autoDetectScore": 0.847,
  "sourceScaleFactor": null
}
```

1:1 with the `MapRect` record. Synthesis-probe consumes via a future `--maprect-json <path>` flag.

### `10-detections.json`

```json
{
  "schemaVersion": 1,
  "renderSizePx": 16,
  "detections": [
    {
      "landmarkType": "Portal",
      "iconName": "landmark_portal",
      "anchorX": 412.7,
      "anchorY": 588.3,
      "score": 0.94
    }
  ]
}
```

1:1 with `TypedDetection`. `renderSizePx` is the icon template size in effect so a diagnostic can reconstruct the visual rects.

### `11-recovered-cal.json`

```json
{
  "schemaVersion": 1,
  "scale": 0.31536,
  "rotationRadians": -3.14159,
  "originX": 1039.45,
  "originY": -36.38,
  "mirrorNorth": false,
  "calibrationZoom": 1.0,
  "residualPixels": 0.34,
  "referenceCount": 8,
  "source": "AutoCapture",
  "inliers": [
    {
      "label": "Portal:Eltibule→Serbule",
      "worldX": 234.1,
      "worldZ": -78.5,
      "pixelX": 612.3,
      "pixelY": 488.7,
      "matchScore": 0.94
    }
  ]
}
```

Calibration fields 1:1 with `AreaCalibration`. `inliers` is `result.Inliers` verbatim. Synthesis-probe truth-cal source.

### Versioning

Schema bumps follow project memory `settings_schema_migration_pattern`: any shape change increments `schemaVersion`. Consumer policy is read-and-ignore-unknown for forward compat (synthesis-probe just reads the fields it cares about). No migration code in this PR — only writers exist in `Mithril.slnx`; nothing reads bundles back.

## Annotated visualizations

All three heavy PNGs use WPF `DrawingVisual` + `DrawingContext` + `RenderTargetBitmap` → `PngBitmapEncoder`. No `System.Drawing` (#921 guard).

### `07-deviation.png`

Pure pixel math, no drawing. For each `(x, y)`:

```
byte d = (byte)Math.Max(0, alignedCrop[x, y] - alignedTexture[x, y]);
```

Encoded as Gray8 via `BitmapSource.Create` + `PngBitmapEncoder`. ~20 lines.

### `08-detections.png`

Overlay on a color render of `03-screenshot-gray.png`. For each entry in `Result.Detections`:

- `Pen(Brushes.Cyan, 1)` rectangle of `RenderSizePx × RenderSizePx` centered on `(AnchorX, AnchorY)`.
- `Pen(Brushes.Red, 1)` cross-mark at the anchor (two 4 px line segments).
- Small text label `Score:0.00` in cyan, positioned top-right of the rect.

`RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32)` → `DrawImage(grayBitmapSource, …)` for the background, then `DrawRectangle` / `DrawLine` / `DrawText` per detection. ~80 lines.

### `09-projection-overlay.png`

Overlay on the raw color screenshot. For each ref in `References` (the area's full reference set):

- Project via `Result.Calibration.WorldToWindow(ref.World, currentZoom: 1.0)` into texture pixels, then map back to screenshot pixels via the new `MapRect.TextureToScreenshot`. Yellow cross at each projected point.
- For each entry in `Result.Inliers`, a green outline rect of `RenderSizePx × RenderSizePx` centered on the inlier's `(PixelX, PixelY)`, also mapped to screenshot pixels.

Color background (matches the offline tool's `--projection-overlay`). ~100 lines plus ~10 for the `MapRect.TextureToScreenshot` extension. Produced only when `Result.Calibration` is non-null.

### Parity goal

Approximate visual parity with `tools/MapCalibrationFromScreenshot`'s `--debug-image` / `--projection-overlay` outputs: same colors, same stroke widths, same intent — not pixel-exact. Pixel-exact would mean re-using the tool's drawing code which lives outside `Mithril.slnx`. Approximate parity is achievable + maintainable in WPF and good enough that a developer can side-by-side a tool run with a production bundle without confusion.

## Toggle, defaults, and settings migration

Single setting, off by default: `CaptureDiagnosticsOptions.DumpCalibrationBundles : bool`. Surfaced in the diagnostics settings UI as a checkbox labelled approximately "Save calibration diagnostics on each attempt".

Settings JSON migration (per project memory `settings_schema_migration_pattern`):

- Bump `ShellSettings.SchemaVersion` (or whatever the existing key is named).
- In `Migrate`: rename `DumpCalibrationFrames` → `DumpCalibrationBundles` (preserving the boolean value); drop `DumpCalibrationGrayFrames` silently (its functionality is now unconditional inside the bundle).
- Test asserts an old-shape JSON with `DumpCalibrationFrames=true` loads as `DumpCalibrationBundles=true`.

## Failure-mode discipline

Project memory `instrument_generated_code_with_diagnostics`, `consumerless_service_verify_via_diagnostics`, and the existing `CaptureFrameDumper` fail-soft contract carry forward:

- `Write` swallows every exception, logs a warning via `ILogger`, returns. No exception propagates into the engine on any path.
- The visualizer methods similarly swallow + log; a render failure for one PNG does not block the others.
- The settings UI button "Open calibration diagnostics folder" creates the dir if absent (idempotent `Directory.CreateDirectory`), then `Process.Start` with `UseShellExecute = true`. Mirrors the existing `OpenLogDirectory` shape.

## Test surface

### `tests/Mithril.MapCalibration.Capture.Tests/CalibrationAttemptBundleSinkTests.cs` (renamed from `CaptureFrameDumperTests.cs`)

| Test | Asserts |
|---|---|
| `Sink_writes_per_attempt_subdir_with_expected_name` | Outcome → dir-name format: UTC timestamp, area key, outcome suffix. |
| `Sink_writes_all_11_files_on_accepted_attempt` | Fully-populated context → every file present, `01-attempt.json` `files` block matches actuals. |
| `Sink_writes_only_populated_artifacts_on_solve_rejection` | Context with `Result.Calibration is null` → no `09`, no `11`; `01-attempt.json` `files` has `null` for those slots. |
| `Sink_writes_only_header_on_capture_failed` | Context with `RawCapture is null` and `Outcome = "rejected-capture-failed"` → only `01-attempt.json` exists. |
| `Sink_skips_write_on_pre_capture_outcomes` | Context with `Outcome ∈ {rejected-no-area, rejected-pg-not-foreground, rejected-no-bbox}` → no subdir created. |
| `Sink_swallows_exceptions_and_logs` | Forced exception inside the sink (e.g., stub visualizer that throws) → no exception propagates; warning logged. |
| `NullSink_no_ops_on_populated_context` | The null-sink variant accepts a fully-populated context and writes nothing. |
| `Sink_json_round_trips_through_source_gen_context` | Serialize-then-deserialize each of the four JSON shapes via the generated context; assert all fields preserved. |
| `OutcomeNaming_maps_reject_reasons_to_subcategories` | The reject-reason → outcome-suffix table covers each branch (`no-detections`, `insufficient-inliers`, `residual`, fallback). |
| `Visualizer_renders_deviation_with_expected_byte_pattern` | 4×4 fixture with known max-positive diffs; per-pixel parity. |
| `Visualizer_renders_detections_overlay_with_expected_dims` | Synthetic 32×32 gray + 3 detections; output is a non-null `BitmapSource` of expected dims (no per-pixel parity — `DrawingContext` is OS-dependent). |
| `Visualizer_renders_projection_overlay_only_when_calibration_present` | Null `Result.Calibration` → no overlay produced. |

### `tests/Mithril.MapCalibration.Tests/Detection/MapRectTests.cs` (new or extend existing)

| Test | Asserts |
|---|---|
| `TextureToScreenshot_inverts_ScreenshotToTexture` | For any rect, `TextureToScreenshot(ScreenshotToTexture(p)) ≈ p`. |

### `tests/Mithril.Shell.Tests/`

| Test | Asserts |
|---|---|
| `OpenCalibrationDumpDirectoryCommand_creates_dump_directory_if_missing` | Invoke the command on a VM whose dump dir has been wiped; assert `Directory.Exists(CaptureFrameDumper.DumpDirectory)` after. (The `Process.Start` side-effect is not directly testable without an abstraction over the process launcher; explicitly out of scope — manual verification covers the open-in-Explorer behavior.) |
| `ShellSettings_migrates_DumpCalibrationCaptureFrames_to_DumpCalibrationBundles` | Old-shape JSON with `dumpCalibrationCaptureFrames: true` deserialized via the new shape → `DumpCalibrationBundles == true`; absent `dumpCalibrationGrayFrames` doesn't throw. |
| `CaptureDiagnosticsMirrorTests` (existing, updated) | Asserts the new single toggle is mirrored end-to-end; old `DumpGrayFrames` assertions removed. |

### Engine-level

`AutoCalibrationEngineTests` (whatever the existing file is named): one new assertion per outcome path that the engine handed a context to the sink with the expected populated slots. Uses a counting test-double sink, not filesystem.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Settings migration mis-flips on existing user installs (silent loss of "I had it on") | `IVersionedState<T>.Migrate` with explicit version bump + unit test that loads an old-shape JSON and asserts the new field is set true. |
| Renaming `CaptureFrameDumper` → sink helpers breaks third-party callers | Internal class; only consumer is `CaptureService`, which loses the call. No public API break. |
| `CalibrationSolveResult.Detections` as a new positional property would be breaking | Added as init-only property after `Inliers` (same pattern `Inliers` already follows). Default null. Non-breaking. |
| Bundle write hits disk on every attempt → user surprised by `%LocalAppData%` growth | Hint text under the toggle documents the behavior. Manual-prune policy explicit in spec + XAML hint. |
| Per-attempt WPF rendering for visualizations holds GC pressure (`RenderTargetBitmap` is heavy) | Visualizations only run when the toggle is on. Off → null-sink, no rendering. |
| Schema-versioned JSONs become stale (synthesis-probe expects v1 forever) | Read-and-ignore-unknown forward-compat in the consumer; schema bumps only on shape change. |

## Definition of done

- [ ] `CalibrationAttemptContext`, `ICalibrationAttemptBundleSink`, `FilesystemCalibrationAttemptBundleSink`, `NullCalibrationAttemptBundleSink`, `CalibrationAttemptBundleSinkSelector`, `AttemptBundleVisualizer`, and `CalibrationBundleJsonContext` shipped under `Mithril.MapCalibration.Capture`.
- [ ] `AutoCalibrationEngine.TryCalibrateCurrentAreaAsync` refactored into a public `try/finally` wrapper around a private `RunAttemptCoreAsync`; sink wired via the selector.
- [ ] `CaptureService` no longer dumps; `CaptureFrameDumper` is removed as a wired service (helpers migrate into the sink); `CaptureDiagnosticsOptions.DumpGrayFrames` removed; `CaptureDiagnosticsOptions.DumpCaptureFrames` renamed to `DumpCalibrationBundles`; `ShellSettings.DumpCalibrationCaptureFrames` renamed to `DumpCalibrationBundles` with a schema-version bump + `Migrate` translating the old key; `ShellSettings.DumpCalibrationGrayFrames` removed.
- [ ] `CalibrationSolveResult.Detections` (`IReadOnlyList<TypedDetection>?`) surfaced from the solver.
- [ ] `MapRect.TextureToScreenshot(double, double)` shipped.
- [ ] `DiagnosticsSettingsViewModel.OpenCalibrationDumpDirectoryCommand` + matching button in `DiagnosticsSettingsView.xaml`; old `DumpCalibrationGrayFrames` checkbox removed.
- [ ] All tests in the test-surface table shipped + green.
- [ ] **Manual verification:** a live in-game capture produces an `Area<name>-…-accepted/` subdir under `%LocalAppData%/Mithril/diagnostics/calibration/` with all 11 expected files; one `*-rejected-*` subdir also produced by capturing somewhere that doesn't solve cleanly.
- [ ] PR opened with `area:map-calibration` label, referencing #966 and #978 for lineage. NOT merged.

## Out of scope

- Synthesis-probe-side wiring of `--aligned-deviation` / `--maprect-json` / `--truth-cal` flags. Separate PR on the `claude/synthesis-probe-impl` branch.
- `08-mask.png` border-mask diagnostic. Would require refactoring `DeviationBlobDetector` to expose intermediate state; deferred to a follow-up.
- Retention / auto-prune of accumulated bundles. Manual prune only in v1.
- Top-level `index.json`. Glob by subdir name is sufficient for the synthesis-probe.
- Migrating the legacy `938-masks/` subdir under the dump root. That was a one-off external artifact, not the engine's output.
- Live wiring of the synthesis-probe in the production engine. The probe stays an offline tool; the production engine reads its `AreaCalibration` from `IMapCalibrationService` as today.
