# Perf-trace data schema

Reference for the JSON-lines files written by [`IPerfRecorder`](../src/Mithril.Shared/Diagnostics/Performance/IPerfRecorder.cs) / [`PerfFileExporter`](../src/Mithril.Shared/Diagnostics/Performance/PerfFileExporter.cs) — what each event means, what its properties carry, and how to read a trace.

Shipped in PR [#196](https://github.com/moumantai-gg/mithril/pull/196). Tracking issue: [#195](https://github.com/moumantai-gg/mithril/issues/195).

## What gets recorded and where

When a perf-trace session is active, each emitted event is written as one JSON object per line (Serilog `CompactJsonFormatter`) to:

```
%LocalAppData%\Mithril\Shell\perf\perf-{yyyyMMdd-HHmmss}.jsonl
```

The directory is created on first use. Older sessions are pruned to the most recent **30** files on each session start. Within a single long session, Serilog rolls at 50 MB to `…_001.jsonl`, `…_002.jsonl`, etc., keeping the last 5.

Off by default; enable via **Settings → Diagnostics** (or set `enablePerfTrace: true` in `%LocalAppData%\Mithril\Shell\shell.json`). Start/stop a session via the hotkey "Toggle perf-trace recording" (developer-only; ships unbound), or set `autoStartPerfTrace: true` to record from shell launch.

## File anatomy

Every line is a CompactJson record from Serilog. The base fields are:

| Field | Meaning |
|---|---|
| `@t` | UTC timestamp, ISO-8601 with sub-millisecond precision |
| `@l` | Serilog level (`Information`, `Warning`, `Verbose`, etc.) |
| `@mt` | Message template — the literal format string the producer used |
| `@m` | Rendered message (present when properties were interpolated) |
| _(per-event)_ | Structured properties, top-level. `Kind` discriminates the event. |

The discriminator is **`Kind`** — one of the constants in [`PerfEventKinds`](../src/Mithril.Shared/Diagnostics/Performance/PerfEventKinds.cs). All filtering should key off `Kind`.

## Producer model

Producers emit via `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` (BCL), using the canonical statics in [`Mithril.Shared.Diagnostics.Telemetry`](../src/Mithril.Shared/Diagnostics/Telemetry/) (and [`Arda.Abstractions.Diagnostics`](../src/Arda/Arda.Abstractions/Diagnostics/) for the Arda pipeline, which can't depend on `Mithril.Shared`). The recorder's internal listener — [`PerfFileExporter`](../src/Mithril.Shared/Diagnostics/Performance/PerfFileExporter.cs) — subscribes to every source/meter whose name starts with `"Mithril."` and maps emits to the JSON-lines schema below.

**Schema preservation is additive**, not byte-stable. Pre-existing record kinds keep all their pre-migration properties (so a jq recipe reading `.ModuleId` / `.File` / `.CacheHit` continues to work), but some records gained new properties:

- `ref_fetch` gained an `Outcome` property (`cdn`, `cdn_failed`, `cache`, `cache_failed`, `bundled`) — previously the record only fired on the CDN path and `CacheHit` was always `false`. Consumers that read specific properties are unaffected; consumers comparing entire records will see the new key.

If a downstream consumer (mithril-logs MCP, jq recipes) is sensitive to the full key set, treat this PR as an additive schema migration.

### Parallel exporters

Once `TelemetrySettings.EnableOtlpExport` is enabled (Settings → Diagnostics → Telemetry export (OTLP), restart required), the OTLP exporter from `Mithril.Shared.Telemetry` attaches as a *second* listener on the same producer surface as the perf-recorder file exporter. Producers don't change. Each exporter applies its own filtering before serialising — notably the OTLP exporter has the `AllowlistAndRedactionProcessor` tag scrubber (per mithril#815), which may drop tags the file exporter retains. If a JSON-lines record looks tag-rich but the same span in Seq/Honeycomb looks tag-sparse, that's the scrubber working as designed: check the tag's classification in `MithrilSharedTagDescriptors` / `ArdaTagDescriptors`, and the user's `TagExports` overrides in `%LocalAppData%/Mithril/Shell/telemetry.json`.

## What's instrumented today

| Kind | Producer | Status |
|---|---|---|
| `session_header` | [`PerfRecorder.Start`](../src/Mithril.Shared/Diagnostics/Performance/PerfRecorder.cs) | Always emitted on session start |
| `frame_summary` / `frame` / `stall` | [`PerfRecorderHostedService` + `CompositionTarget.Rendering`](../src/Mithril.Shared/Diagnostics/Performance/PerfRecorderHostedService.cs) | Attached while session active |
| `dispatcher` | `PerfRecorderHostedService` + `Dispatcher.Hooks` | Attached while session active |
| `counter` / `gc` | `PerfRecorderHostedService` + `System.Threading.Timer` (1Hz) | Running while session active |
| `binding_error` | [`BindingErrorTraceListener`](../src/Mithril.Shared/Diagnostics/Performance/BindingErrorTraceListener.cs) | Attached while session active |
| `input_latency` | `PerfRecorderHostedService` + `InputManager.PreProcessInput` | Attached while session active |
| `module_activated` | [`ShellViewModel.ActivateModule`](../src/Mithril.Shell/ViewModels/ShellViewModel.cs) | Fires on every activation (initial + tab clicks) |
| `gate_open` / `view_resolve` | `ShellViewModel.ActivateModule` (child spans) | Per activation; split tells which half of activation dominates |
| `module_discover` | [`ShellServiceCollectionExtensions.AddMithrilModules`](../src/Mithril.Shell/DependencyInjection/ShellServiceCollectionExtensions.cs) | Once at startup; carries `discovered_count` |
| `ref_fetch` | [`ReferenceDataService`](../src/Mithril.Shared/Reference/ReferenceDataService.cs) — CDN refresh + cache-hit + bundled-fallback | All three paths instrumented; `outcome` tag distinguishes |
| `arda_batch` | [`BatchProcessor.ProcessBatch`](../src/Arda/Arda.Ingest/Coordinator/BatchProcessor.cs) (L0/L1) | Per batch read from a log tailer |
| `arda_world_driver` | [`WorldDriver.RunAsync`](../src/Arda/Arda.Dispatch/WorldDriver.cs) (L2) | One long-running span per driver run (per source family) |
| `arda_dispatch` | [`DispatchTable.Dispatch`](../src/Arda/Arda.Dispatch/DispatchTable.cs) (L2) | Per dispatched line; gated on `Activity.Current` so zero-cost when no recording session |
| `arda_compose` | [`DomainEventBus.Publish`](../src/Arda/Arda.Dispatch/DomainEventBus.cs) (L4 and below) | One span per subscriber-callback per published event; gated on `ActivitySource.HasListeners()` |
| `overlay_project` | [`OverlayWindowService.OnSurfaceRender`](../src/Mithril.Overlay/Internal/OverlayWindowService.cs) (#835) | One span per per-tick projection; tag `area`, `marker_count`. Scaffold-only — fires only after a migration PR shows the overlay. |
| `calibration_synthesis_rerank` | Map auto-calibration Detection layer (synthesis-J re-rank) via [`MapCalibrationDiagnostics`](../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs) | One span per solve carrying the synthesis-J re-rank outcome. Tag list TBD per Task 16 of the synthesis-rerank plan. Scaffold-only — producer wiring lands in Task 16. |
| `calibration.drift_check` | [`AutoCalibrationEngine.CheckDriftAsync`](../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs) via `MithrilActivitySources.MapCalibration` (mithril#1046) | One span per hotkey-triggered drift check on a scene with a stored calibration; `outcome` tag distinguishes early-exit vs `Ok` vs `Drift`. |
| `calibration.refine.primary` / `calibration.refine.fallback` | [`CompositeMapRegionRefiner`](../src/Mithril.MapCalibration.Detection/CompositeMapRegionRefiner.cs) via [`MapCalibrationDiagnostics.ActivitySource`](../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs) (mithril#1061) | Per-branch spans from the locate dispatcher. Primary wraps `FeatureMatchingRefiner` (ORB+Lowe); fallback wraps `SobelPaddedPyramidRefiner`. Tags below. |
| `calibration.area.select_scene` / `calibration.area.calibrate_current` | [`AreaCalibrationService`](../src/Legolas.Module/Services/AreaCalibrationService.cs) via `MithrilActivitySources.LegolasCalibration` (mithril#1093) | Per AreaCalibrationService scene-handoff / per user-driven calibrate-current invocation. |
| `calibration.ghosts.rebuild` | [`MapOverlayViewModel.RebuildCalibrationGhosts`](../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs) via `MithrilActivitySources.LegolasCalibration` (mithril#1093) | Per VM ghost-pass rebuild; companion histogram `mithril.legolas.calibration.ghosts.rebuild_ms` records wall-clock. |
| `meter_counter` | All `Meter`-counter producers (Arda lines/verb-unmatched/grammar-break, reference fetch_outcome, domain-event-published, overlay projection.latency_ms + frame.markers, map-calibration synthesis-J histograms + disagree counter, Legolas calibration picker/projection/drawer/rebuild instruments) | Sums per (instrument, tag-set) flushed once per second |
| `scope` | _ad-hoc_ | Fallback record kind for any `Mithril.*` Activity that doesn't match a dedicated dispatch arm. Reaches via the `scope` arm until a dedicated `overlay_*` dispatch arm lands in `PerfFileExporter`. |

If you read a trace and a kind you expected is missing, check the producer column first.

## Event kinds

### `session_header`

First line of every trace file. Identifies the recording machine and build so a trace can be interpreted out-of-context.

| Property | Meaning |
|---|---|
| `Build` | `AssemblyInformationalVersion` of `Mithril.Shell` (e.g. `0.42.1+abcdef0`). |
| `Os` | `RuntimeInformation.OSDescription`. |
| `Gpu` | Adapter description — currently empty; reserved. |
| `RefreshRateHz` | Primary display refresh rate — currently `0`; infer from frame intervals if needed. |
| `Dpi` | Effective DPI of the main window (96 = unscaled). Layout cost scales with this. |
| `ActiveCharacter` / `ActiveServer` | Last-selected character at session start. |
| `Modules` | Array of module ids loaded at session start (e.g. `["samwise","pippin",…]`). |
| `RenderTier` | `RenderCapability.Tier >> 16`. `0` = software rendering (no GPU accel — any composition load *will* stall, that's the machine), `1` = partial hardware, `2` = full hardware. Read this first when stalls don't correlate with dispatcher activity. |
| `RenderMode` | `RenderOptions.ProcessRenderMode` — `"Default"` (use whatever the machine offers) or `"SoftwareOnly"` (forced software). The latter overrides `RenderTier` — even tier-2 will render in software. |
| `IsRemoteSession` | `SystemParameters.IsRemoteSession`. TS/RDP forces software composition independent of GPU capability, so a tier-2 machine over RDP behaves like tier-0. |

### `frame_summary`

Per-second aggregate of `CompositionTarget.Rendering` intervals. Emitted ~1×/sec while a UI window is visible.

| Property | Meaning |
|---|---|
| `Count` | Frames observed in the window (~60 on a healthy 60 Hz machine). |
| `MeanMs` | Mean inter-frame interval. |
| `P50Ms` / `P95Ms` | Percentile interval. P95 spiking above ~20 ms while P50 stays low ≈ occasional stutter. |
| `MaxMs` | Worst single frame in the window. |
| `StallCount` | Frames with interval > 33 ms (i.e. visibly dropped at 30 fps). |

### `frame` (with `Stall=true`)

Per-frame event emitted when a single render takes longer than 33 ms. The same shape, but `Stall=true` and `IntervalMs` is the bad sample. Raw `frame` events with `Stall=false` are emitted only when `verboseFrameEvents=true`.

| Property | Meaning |
|---|---|
| `IntervalMs` | Frame interval that triggered the event. |
| `Stall` | `true` for the >33 ms emit path; `false` only with verbose. |
| `CurrentOp` | Reserved for the in-flight dispatcher op label; currently empty string. |
| `Attribution` | Only present on stall events. `"dispatcher"` if cumulative `dispatcher.RunMs` over the 200 ms preceding the stall ≥ 20 ms (UI thread was busy — go look at the surrounding `dispatcher` events). `"non-dispatcher"` otherwise (UI thread was idle — the cause is elsewhere: GC, GPU/DWM, blocked native call, swap-chain hitch). Pair this with `session_header.RenderTier` to distinguish "no GPU" from "real transient render event." |

The 20 ms threshold and 200 ms window are defined in [`StallAttribution`](../src/Mithril.Shared/Diagnostics/Performance/StallAttribution.cs) — ops totaling less than ~20 ms can't realistically *cause* a >33 ms stall on their own, so anything below is classified as non-dispatcher.

### `dispatcher`

Fires from `Dispatcher.Hooks.OperationCompleted` — one event per UI-thread `DispatcherOperation` that ran while the session was active.

| Property | Meaning |
|---|---|
| `Priority` | `DispatcherPriority` of the op (e.g. `Render`, `DataBind`, `Background`). |
| `WaitMs` | Currently `0` (no public API for queued-at time). |
| `RunMs` | Wall-clock duration of `OperationStarted` → `OperationCompleted`. The number that matters. |
| `QueueDepthAtStart` | In-flight op count when this one began. Sustained depth > 1 means the UI thread is saturated. |

### `counter`

1 Hz process-state snapshot.

| Property | Meaning |
|---|---|
| `WorkingSetMB` | Process working set, MB. Trending up across a session ≈ leak suspect. |
| `Gen0` / `Gen1` / `Gen2` | Cumulative GC collection counts. Deltas tell you per-second collection pressure. |
| `Threads` | Process thread count. |
| `Handles` | OS handle count. Trending up = handle leak (file/window/event). |
| `DispatcherQueueDepth` | Current in-flight UI-thread ops (same counter `dispatcher` uses). |

### `gc`

Fires when `GC.CollectionCount(n)` increments between two `counter` ticks. Best-effort generation attribution — only the highest generation observed in the gap is reported.

| Property | Meaning |
|---|---|
| `Generation` | `0`, `1`, or `2`. Gen2 collections are the ones that cause visible frame stalls (compacting, blocking). |
| `DurationMs` | Currently `0` — the polling approach doesn't have start/stop ticks. |

### `binding_error`

WPF binding error captured via `PresentationTraceSources.DataBindingSource`. Throttled to **1 event per identical message per second** — a misnamed binding firing 60×/sec collapses to ~1/sec.

| Property | Meaning |
|---|---|
| `Message` | The raw `System.Windows.Data Error: nn : …` text. |

Binding errors are a classic silent perf sink — WPF retries failed bindings on every layout pass.

### `input_latency`

Time from the first observed `PreProcessInput` event to the next `CompositionTarget.Rendering` tick. Measures *perceived* lag, not just frame time.

| Property | Meaning |
|---|---|
| `InputKind` | `mouse` or `key`. |
| `LatencyMs` | Input → next-frame delta. Persistently > 50 ms = the UI feels unresponsive. |

Coalesced: multiple input events arriving in the same render frame produce one latency sample. Stylus/touch are ignored for now.

### `scope`

Manual timing scope — emitted on `using var s = perf.Scope("name", new { tag = value }).Dispose()`. The one extension point modules can adopt at suspected hot paths.

| Property | Meaning |
|---|---|
| `Name` | The string passed to `Scope`. Conventionally `module.operation` (e.g. `samwise.parse`). |
| `DurationMs` | Wall-clock from `Scope()` call to dispose. |
| `Tags` | Whatever anonymous object the caller passed (or null). |

> **No producers today.** See [What's instrumented today](#whats-instrumented-today). A trace with zero `scope` events is expected on a stock build.

### `module_activated`

Cost of the first activation of a module — the `_gates.For(...).Open()` plus the view-type DI resolution in `ShellViewModel.ActivateModule`.

| Property | Meaning |
|---|---|
| `ModuleId` | The module's `Id` (e.g. `samwise`). |
| `DurationMs` | Wall-clock for the activation block. Lazy modules pay their cost here on first click. |

### `ref_fetch`

Reference-data load in `ReferenceDataService`. All three load paths emit this kind — distinguish via `Outcome`.

| Property | Meaning |
|---|---|
| `File` | The reference data file name (`items`, `recipes`, `npcs`, …). |
| `Outcome` | `cdn` (HTTP fetch succeeded), `cdn_failed` (HTTP attempt threw — `Bytes=0`, `DurationMs` is the failing-attempt wall-clock), `cache` (loaded from on-disk cache file), or `bundled` (fell back to the bundled JSON). |
| `CacheHit` | `true` when `Outcome` is `cache`; `false` otherwise. Retained for backward-compat with pre-PR-B trace consumers that only branched on `CacheHit`. |
| `DurationMs` | Wall-clock for the path that emitted the record (HTTP fetch, cache read, or bundled read). |
| `Bytes` | Number of bytes read. `0` on the CDN-failure path (no body). |

### `gate_open`

Child of `module_activated`. Time spent in `ModuleGate.For(id).Open()` — the lazy-module wake-up half of activation.

| Property | Meaning |
|---|---|
| `ModuleId` | The module's `Id`. |
| `DurationMs` | Wall-clock for `Open()`. Cheap for eager modules (already open); for lazy modules this is when their hosted services start. |

### `view_resolve`

Child of `module_activated`. Time spent resolving the module's view from DI (`_services.GetRequiredService(ViewType)`).

| Property | Meaning |
|---|---|
| `ModuleId` | The module's `Id`. |
| `DurationMs` | Wall-clock for DI resolution + view construction. First-activation cost lives here for modules with eager `View` ctors. |

### `module_discover`

Module assembly scan in `AddMithrilModules`. One record per shell start.

| Property | Meaning |
|---|---|
| `DiscoveredCount` | Number of `IMithrilModule` implementations found in the `modules/` folder. |
| `DurationMs` | Wall-clock for the assembly load + reflection scan. Slow scans are usually I/O on the `modules/` directory. |

### `arda_batch`

Per batch read by `BatchProcessor.ProcessBatch` (Arda L0/L1) — covers the tailer→classification stage for one polling cycle.

| Property | Meaning |
|---|---|
| `Source` | The log file path being tailed (e.g. `…/Player.log`, `…/ChatLogs/Global.log`). |
| `LineCount` | Raw lines read from the tailer in this batch (pre-classification). |
| `ClassifiedCount` | Lines that survived classification and were forwarded to L2. The difference is uninteresting noise (chat-styling lines that aren't structured events). |
| `DurationMs` | Wall-clock for classify-the-whole-batch. Worth examining if a batch contains many lines — classification is per-line. |

### `arda_world_driver`

One long-running span per Arda driver run (typically one per session per source family — player + chat). Wraps the entire L2 dispatch loop.

| Property | Meaning |
|---|---|
| `SourceFamily` | `player` or `chat`. |
| `LineCount` | Total lines pulled through the loop before completion. |
| `Halted` | `true` if a grammar break stopped the loop early; otherwise the loop ran to source-exhaustion or cancellation. |
| `DurationMs` | Wall-clock for the loop. Mostly dominated by waiting for new lines from the tailer — interpret in combination with `LineCount` for throughput. |

### `arda_dispatch`

Per-line dispatch through `DispatchTable` (Arda L2). Only emitted when a recording session is attached — gated on `Activity.Current` being non-null so the no-listener path stays allocation-free.

| Property | Meaning |
|---|---|
| `Verb` | The verb token extracted from the line (e.g. `ProcessAddItem`). |
| `HandlerCount` | Number of handlers registered for this verb (1 for most, several for cross-cutting verbs). |
| `DurationMs` | Wall-clock for invoking all handlers. Slow dispatches with `HandlerCount > 1` warrant per-handler instrumentation (deferred from PR B — see [#818](https://github.com/moumantai-gg/mithril/issues/818)). |

### `arda_compose`

Per subscriber-callback invocation through `DomainEventBus.Publish` (Arda L4 and below). One record per (event, subscriber) pair — gated on `ActivitySource.HasListeners()` so the cost is one volatile read when no recording session is attached.

| Property | Meaning |
|---|---|
| `Composer` | The subscriber's target type name (e.g. `InventoryComposer`, `SessionComposer`, `NpcStateComposer`). |
| `EventType` | The event struct's name (e.g. `InventoryItemAdded`, `SessionEstablished`). |
| `DurationMs` | Wall-clock for the single subscriber invocation. Sum over a session per `Composer` to find which composer dominates. |

### `overlay_project` (#835 scaffold)

Per-tick world→pixel projection through the shared overlay (`Mithril.Overlay`). Emitted by [`OverlayWindowService.OnSurfaceRender`](../src/Mithril.Overlay/Internal/OverlayWindowService.cs) once per `CompositionTarget.Rendering` tick *while the overlay window is shown*. Surfaces under the fallback `scope` arm of `PerfFileExporter` until a dedicated dispatch arm lands — until then read this as `Kind="scope"` with `Name="project"` and source `Mithril.Overlay`.

| Property | Meaning |
|---|---|
| `area` | The Arda area key the projection ran for (e.g. `AreaEltibule`). Empty when the player is not in-world. |
| `marker_count` | Number of markers in the area filtered from the current `IWorldOverlayMarkers` snapshot. |
| `cal.path` | One of `direct_overlay` (overlay-frame record consumed directly), `composed_from_texture` (texture-frame record composed via `WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` — dims looked up from `IMapTextureDimensions` by `cal.PixelSha256`; see #1081), `none` (no usable calibration this frame: uncalibrated scene, null-sha cal, catalogue miss, or overlay surface not laid out yet). |
| `DurationMs` | Wall-clock for `MarkerSceneRenderer.Render` (the full per-tick draw dispatch). |

Companion `Mithril.Overlay` meter instruments emitted in `meter_counter` records (below):

- `mithril.overlay.projection.latency_ms` — histogram, ms. Tag: `area`.
- `mithril.overlay.frame.markers` — counter, per-tick marker count. Tag: `area`.
- `mithril.overlay.projection.misses` — counter, per-scene miss when `ResolveComposedOverlayCalibration` returns null even though the scene is otherwise calibrated (`isCalibrated=true`). Covers: uncalibrated scene (not reached), null-sha pre-#1081 record, catalogue miss, or overlay surface not yet sized. Tag: `area`. First-time-per-scene logged at Trace from `OverlayWindowService` with the specific sub-reason (re-run AutoCalibrate / wait for catalogue refresh / etc.).
- `mithril.overlay.dispatch.misses` — counter, per-marker drawer-dispatch miss (no drawer registered for the marker style type). Tag: `style_type`. First-time-per-type logged at Trace from `MarkerSceneRenderer`. A flood here means a consumer is producing markers before its drawer registration ran.
- `mithril.overlay.scene.exceptions` — counter, a registered scene-drawer callback (`IOverlayWindow.RegisterScene`) threw and was isolated from sibling drawers + the per-tick render loop (#835 step 6, review B1). Tag: `drawer_type` (the throwing callback's target type name, or `"unknown"`). Pairs with a `LogError` per occurrence on the `Mithril.Overlay` category. A flood here means a consumer's scene drawer is throwing every tick — the overlay keeps rendering siblings, but that consumer's layer is missing.

Scaffold-only — fires only after a Migration step 2+ PR registers Legolas drawers and shows the overlay. A clean scaffold-only build emits zero overlay records.

### `calibration_synthesis_rerank` (synthesis-J scaffold)

Per-solve span emitted from the Map auto-calibration Detection layer (`Mithril.MapCalibration.Detection` ActivitySource) carrying the synthesis-J re-rank outcome for a single solve. The catalog lives in [`Mithril.MapCalibration.Diagnostics.MapCalibrationDiagnostics`](../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs) — *local* to `Mithril.MapCalibration` rather than the Shared catalog because `Mithril.MapCalibration.csproj` deliberately doesn't reference `Mithril.Shared` (it must stay consumable by Shared peers without depending up the layering). This mirrors Arda's pattern with [`ArdaActivitySources`](../src/Arda/Arda.Abstractions/Diagnostics/ArdaActivitySources.cs) / [`ArdaMeters`](../src/Arda/Arda.Abstractions/Diagnostics/ArdaMeters.cs). Pointer comments in `MithrilActivitySources` + `MithrilMeters` document the split so future contributors don't reflexively add a duplicate `MapCalibration` static and create a vocabulary fork.

The parent `calibration.solve` span lives on the Capture-layer source [`MithrilActivitySources.MapCalibration`](../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs); the Detection-layer `calibration.synthesis_rerank` child nests under it when both sources are listened-to.

| Property | Meaning |
|---|---|
| _Tag list TBD per Task 16 of the synthesis-rerank plan._ | Producer wiring lands in Task 16; this section will be filled in then. Surfaces under the fallback `scope` arm of `PerfFileExporter` until a dedicated dispatch arm lands. |

Companion `Mithril.MapCalibration.Detection` meter instruments emitted in `meter_counter` records (below):

- `mithril.map_calibration.synthesis.j` — histogram (`double`). Winning candidate's `J(T_k)`. Tag: `verdict` ∈ {`accept`, `reject`} (per synthesis-J).
- `mithril.map_calibration.synthesis.refs_above_threshold` — histogram (`long`). Count of refs whose sampled `L_t(T·r) ≥ 0.5` for the winning candidate. Tag: `verdict`.
- `mithril.map_calibration.synthesis.disagree` — counter (`long`). Synthesis-J disagreed with the legacy inlier-count gate. Tag: `change` ∈ {`accept_to_reject`, `reject_to_accept`}.

Scaffold-only — fires only after Task 16 wires the producer. A clean scaffold-only build emits zero calibration-synthesis records.

The canonical map-calibration *attempt-outcome* string vocabulary lives in [`OutcomeVocabulary`](../src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs) — it's the per-attempt diagnostic-bundle subdir suffix today, and is the value set any future `mithril.map_calibration.attempts` counter's `outcome` tag will draw from (fixed-vocabulary string, Safe / no scrubbing). Known values:

- `accepted` — solve persisted a transform.
- `rejected-no-area` / `rejected-pg-not-foreground` / `rejected-no-bbox` — pre-capture refusals; no bundle written.
- `rejected-capture-failed` / `rejected-no-base-texture` / `rejected-map-not-located` / `rejected-clamp-degenerate` — capture / locate / clamp stage refusals.
- `rejected-solve` / `rejected-solve-no-detections` / `rejected-solve-insufficient-inliers` / `rejected-solve-residual` — solver-stage refusals (free-form `RejectReason` is mapped to a fixed sub-category by `OutcomeVocabulary.RejectSolveSubcategory`).
- `rejected-not-monotonic` — post-solve monotonicity gate refusal.
- `map_asset_not_known` — autocal invoked before any `Downloading Map` line was observed in this session (no per-scene asset key known). Added with #1021's per-scene keying; the user-facing hint is "change zones once or restart while in this scene."
- `rejected-map-low-confidence` — the Sobel-padded-pyramid fallback (mithril#1061) found a fit but the refined NCC fell below `MapCalibrationLocateOptions.FallbackNccFloor` (default 0.20). Distinct from `rejected-map-not-located` ("no fit at all"); the user-facing hint is "try a different zoom or explore more of the area first." Bundle-worthy so triage can read the low-confidence transform from `01-attempt.json.locatorBest`.
- `error` — unexpected exception in the attempt pipeline.

### `calibration.refine.primary` / `calibration.refine.fallback` (mithril#1061)

Per-branch spans from `CompositeMapRegionRefiner` — the locate-stage dispatcher that tries ORB+Lowe first and falls back to the Sobel-padded-pyramid refiner. Both spans live on [`MapCalibrationDiagnostics.ActivitySource`](../src/Mithril.MapCalibration/Diagnostics/MapCalibrationDiagnostics.cs) (`"Mithril.MapCalibration.Detection"`). The fallback span fires only when the primary returns no `AcceptedRect` (either "no fit at all" or "fit but gate rejected"), so its occurrence in a trace is itself the answer to "what fraction of attempts hit fallback."

| Span | Tag | Type | Notes |
|---|---|---|---|
| both | `outcome` | string | Primary: `"accepted"` / `"rejected"` / `"no_fit"`. Fallback: `"accepted"` / `"rejected_low_confidence"` / `"rejected"` / `"no_fit"`. |
| `calibration.refine.fallback` only | `ncc` | double | NCC peak from the L0 refined re-match (the floor-gated confidence number). Only emitted when `LocateMetrics.Confidence` is non-null. |
| `calibration.refine.fallback` only | `scale` | double | Recovered scale parameter (texture display size = texture-native × scale). |
| `calibration.refine.fallback` only | `blur.sigma` | double | σ (px) of the Gaussian blur applied to the Sobel template at the recovered scale in the fallback's full-resolution stage (mithril#1070). Zero when `RendererBlurEnabled = false` or when the σ-curve clamped to 0 at the recovered scale. Only emitted when `LocateMetrics.BlurAppliedSigma` is non-null — null on ORB primary success. |

```powershell
# Fallback hit-rate per area-attempt across a session
Get-Content $Path | jq -c 'select(.Kind=="scope" and .Name=="calibration.refine.fallback") | {outcome:.Tags.outcome, ncc:.Tags.ncc, scale:.Tags.scale}'
```

### `calibration.drift_check` (mithril#1046)

One span per `AutoCalibrationEngine.CheckDriftAsync` invocation. Emitted on every manual hotkey press over a scene that has a stored calibration (mithril#1046 §9.5). Lives on [`MithrilActivitySources.MapCalibration`](../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs).

| Tag | Type | Notes |
|---|---|---|
| `map.area` | string | `MapSceneRef.MapAssetKey` for the resolved scene (e.g. `"Map_AreaSerbule"`). |
| `refs.matched` | int | Count of reference landmarks that paired with a typed detection within the 20 px match gate. |
| `max_residual_px` | double | Worst predicted-vs-detected residual across the matched references. Compared against the threshold. |
| `threshold_px` | double | `DriftToleranceFactor × stored.ResidualPixels` (currently 3.0×). |
| `outcome` | string | One of `"NoStoredCalibration"`, `"CaptureFailed"`, `"MapNotLocated"`, `"NoIconDetections"`, `"Inconclusive"`, `"Ok"`, `"Drift"`. |

Early exits (`NoStoredCalibration`, `CaptureFailed`, `MapNotLocated`, `NoIconDetections`) set the `outcome` tag and stop before the residual comparison; `refs.matched`, `max_residual_px`, and `threshold_px` are not emitted on those paths.

```powershell
# Drift-check pass rate per area
Get-Content $Path | jq -c 'select(.Kind=="scope" and .Name=="calibration.drift_check") | {area:.Tags."map.area", outcome:.Tags.outcome}'
```

### Calibration consumer chain (mithril#1093)

The calibration *consumer* chain — picker → `AreaCalibrationService` lifecycle → VM projection paths → drawer ghost pass — is instrumented on a new `Mithril.Legolas.Calibration` `ActivitySource` and a same-named `Meter`. Consumer spans are siblings (not children) of the Capture-layer `calibration.attempt` span: consumer projection runs on the UI thread asynchronously to the engine's solve attempt, so a single span tree would cost more correlation-id plumbing than it would buy. All three consumer spans surface under the fallback `scope` arm of `PerfFileExporter` until a dedicated dispatch arm lands — read as `Kind="scope"` with the `Name` shown below.

#### `calibration.area.select_scene`

`AreaCalibrationService.SelectScene` body — fires when Arda hands a new scene to the calibration consumer layer.

| Tag | Type | Notes |
|---|---|---|
| `scene.asset_key` | string | Unity asset key of the new scene (e.g. `Map_AreaSerbule`). Safe. |
| `scene.parent_area_key` | string | Containing Arda area key (e.g. `AreaSerbule`). Safe. |
| `refs_count` | int | Count of reference landmarks available for the scene. |
| `cal.applied` | bool | `true` when a stored calibration was found and re-applied on entry; `false` for "new scene, no cal." |
| `cal.source` | string | `AreaCalibration.Source` enum verbatim: `UserRefinement` / `AutoCapture` / `BundledBaseline` / `CommunitySync`. Emitted only when `cal.applied=true`. |
| `cal.residual_px` | double | Stored calibration's residual in pixels. Emitted only when `cal.applied=true`. |

#### `calibration.area.calibrate_current`

`AreaCalibrationService.CalibrateCurrentArea` body — fires on every user-driven recalibrate.

| Tag | Type | Notes |
|---|---|---|
| `scene.asset_key` | string | Scene the placements were solved against. Safe. |
| `placements` | int | Number of user-supplied placements passed to the solver. |
| `outcome` | string | One of `solved` / `refused` / `no_fit`. `refused` = pre-solve refusal (no current scene or `placements < 2`); `no_fit` = solver returned null. |
| `cal.residual_px` | double | Residual of the resulting calibration. Emitted only when `outcome=solved`. |

#### `calibration.ghosts.rebuild`

`MapOverlayViewModel.RebuildCalibrationGhosts` body — fires when ghosts repopulate on validation-toggle, area-change, or recalibrate. Companion histogram below records wall-clock for the same call.

| Tag | Type | Notes |
|---|---|---|
| `area` | string | Scene asset key. Safe. |
| `refs_count` | int | Reference landmarks considered for ghost placement. |
| `ghosts_built` | int | Ghost dots actually placed (may be < `refs_count` if some refs project off-canvas). |
| `cal.source` | string | `AreaCalibration.Source` enum verbatim (`UserRefinement` / `AutoCapture` / `BundledBaseline` / `CommunitySync`). Emitted only when `cal.path=direct_overlay`. |
| `cal.residual_px` | double | Calibration residual. Emitted only when `cal.path=direct_overlay`. |
| `cal.path` | string | One of `direct_overlay` (overlay-frame calibration consumed via `CurrentOverlayCalibration`), `composed_from_texture` (texture-frame record composed onto the overlay surface via the shared `IComposedOverlayCalibrationResolver` — mithril#1096), or `none` (no usable calibration this frame — the ghosts list is empty regardless of `refs_count`). |

**`cal.path = composed_from_texture` (mithril#1096):** the producer resolved
the overlay-frame calibration by composing a texture-frame record onto the
target surface (overlay window or wizard canvas) via
`WorldToTextureCalibration.ProjectThroughOverlay(MapRect)` with dims from
`IMapTextureDimensions`. Indicates the post-#1081 AutoCal record path lit up
on this scene. The same vocabulary now appears on the VM-side consumer spans
(`calibration.ghosts.rebuild`, etc.) and on the `overlay_project` span — both
sides route through the shared `IComposedOverlayCalibrationResolver`.

**`ProjectionSkipped` counter semantic note (mithril#1096):** the counter
no longer increments when a texture-frame record successfully composes —
the scene hit the happy path via the composer. Only `Path == None`
outcomes increment the counter. A drop in this counter for a given
`area` between pre- and post-#1096 builds is the migration landing
correctly, not a regression.

**MissReason vocabulary on `LogCalibrationFallback` Trace records (mithril#1096):**
when the composer returns `Path == None`, the consumer feeds the resolver's
`MissReason` into the dedup helper:

| MissReason | Cause |
|---|---|
| `no_scene` | `CurrentScene` is null (no `MapAssetChanged` yet). |
| `no_usable_calibration` | Picker returned neither overlay nor texture record. |
| `no_overlay_cal` | Legacy fallback (composer not wired — should not appear in production builds). |

Pre-#1107 review fix also emitted `null_sha`, `unsized_surface`, and `catalogue_miss` —
those branches required surface-dim + catalogue-dim lookups for `MapRect`-based composition.
The post-review composer is a direct rebrand of the texture-frame transform (no catalogue
lookup, no surface dims), so those failure modes can't fire and the vocabulary is reduced
to the three above.

Companion `Mithril.Legolas.Calibration` meter instruments emitted in `meter_counter` records (below):

- `mithril.legolas.calibration.picker.outcomes` — counter, per `MapCalibrationService.PickByFrame` call (declared on the `Mithril.Legolas.Calibration` Meter from `Mithril.MapCalibration.Diagnostics.MapCalibrationDiagnostics` due to project layering — see [spec §4.3](planning/calibration-logging-pass-1093/spec.md#43-meter--instruments)). Tags: `frame` ∈ {`texture`, `overlay`}, `outcome` ∈ {`hit`, `miss`, `fallback_below_floor`}. `hit` = a candidate cleared the `MinReferences` floor; `fallback_below_floor` = no candidate cleared the floor but the picker returned the best-source-precedence fallback; `miss` = no candidates at all (the load-bearing "is `CurrentOverlayCalibration` returning null in production" answer).
- `mithril.legolas.calibration.projection.skipped` — counter, per VM-side projection path that skipped because `CurrentOverlayCalibration` returned null. Tags: `consumer` ∈ {`ghosts`, `motherlode_markers`, `motherlode_guidance`, `survey_pin`, `survey_anchor`, `wizard_landmarks`}, `area` (scene asset key). First-time-per-scene logged at Trace via `MapOverlayViewModel.LogCalibrationFallback` so the human-readable explanation exists once per scene; the counter is the "how often" answer for per-frame getter paths (`motherlode_markers`, `motherlode_guidance`).
- `mithril.legolas.calibration.ghost_drawer.transitions` — counter, per drawer ghost-pass state-machine transition. Tags: `from`, `to` ∈ {`hidden`, `empty`, `drawing`, `brush_null`}. The state machine is `hidden ⇄ empty ⇄ drawing` with a degraded `brush_null` sink. State held in two `int?` fields on `LegolasOverlaySceneDrawer` so the per-frame check is two field-reads + one branch.
- `mithril.legolas.calibration.ghosts.rebuild_ms` — histogram (`double`, unit `ms`), per `RebuildCalibrationGhosts` call. Tags: `area`, `refs_count`, `ghosts_built`. Pair with the `calibration.ghosts.rebuild` span tag set for distribution-over-time questions.

### `meter_counter`

Aggregated `System.Diagnostics.Metrics.Meter` counter sum, flushed once per second per (instrument, tag-set). Covers PR B's counters: Arda lines/verb-unmatched/grammar-break/verb-unhandled/domain-event-published, reference fetch-outcome, Mithril.Overlay projection latency + frame markers (#835), Mithril.MapCalibration synthesis-J histograms + disagree counter (synthesis-rerank plan Task 10), Mithril.Legolas.Calibration picker/projection/drawer/rebuild instruments (mithril#1093), and any future counters added via the `Mithril.*` Meter prefix.

| Property | Meaning |
|---|---|
| `Instrument` | The OTel instrument name (`mithril.arda.lines_parsed`, `mithril.reference.fetch_outcome`, `mithril.overlay.frame.markers`, …). |
| `Sum` | Sum of measurements within the 1-second flush window. |
| `Tags` | Object containing the tag-set the measurements share (e.g. `{"source":"player"}`, `{"outcome":"cache","file":"items"}`, `{"area":"AreaEltibule"}`). |

## Reading a trace

Every example below assumes the file is at `$Path`. On Windows PowerShell:

```powershell
$Path = "$env:LocalAppData\Mithril\Shell\perf\perf-20260511-143301.jsonl"
```

### Common false-negative traps

Before concluding "kind X is missing, the wiring is broken," rule out these analysis-side traps. Most "zero events" findings turn out to be one of them:

- **Capital-K `Kind`.** Serilog's CompactJsonFormatter preserves the case of the template placeholder. The trace JSON has `"Kind":"…"`, `"ModuleId":"…"`, `"DurationMs":…` — capital first letter on every property. A filter like `jq 'select(.kind=="module_activated")'` (lowercase `kind`) returns zero results even when events exist. Use `.Kind`. Same for all other properties.
- **The kind isn't instrumented yet.** Check [What's instrumented today](#whats-instrumented-today). `scope` is the fallback bucket for any `Mithril.*` Activity that doesn't match a dedicated dispatch arm — empty by default, populates only when a producer adopts a not-yet-mapped source/op pair.
- **The session didn't cover the event-generating window.** Hotkey-toggled sessions start *after* the shell is up — the initial `module_activated` fires during `ShellViewModel` construction and is therefore missed. Only subsequent tab clicks produce `module_activated` in that mode. Use `autoStartPerfTrace=true` if you want the first activation captured.
- **Autostart silently didn't fire.** If `enablePerfTrace=true` and `autoStartPerfTrace=true` aren't both in `shell.json` *before* the process starts, the autostart check at [`Program.cs`](../src/Mithril.Shell/Program.cs) skips. The regular diagnostics log will tell you — grep `%LocalAppData%\Mithril\Shell\logs\mithril-*.json` for `Session started:` dated to that run.
- **Sanity-check the file with a literal grep first.** Before reaching for jq, run a case-sensitive plain-text search for the kind string. If the literal string appears, the events exist and your jq filter has a bug. If it doesn't appear, the event isn't being produced (or wasn't produced during your session window).
  ```powershell
  Select-String -Path $Path -Pattern '"Kind":"module_activated"' -CaseSensitive
  ```

### What machine produced this trace?

```powershell
Get-Content $Path -TotalCount 1 | jq '{build:.Build, os:.Os, dpi:.Dpi, modules:.Modules}'
```

### Worst frame second across the trace

```powershell
Get-Content $Path | jq -c 'select(.Kind=="frame_summary") | {t:."@t", p95:.P95Ms, max:.MaxMs, stalls:.StallCount}' |
  Sort-Object { ($_ | ConvertFrom-Json).max } -Descending | Select-Object -First 5
```

### How many stalls and when

```powershell
Get-Content $Path | jq -c 'select(.Kind=="stall" or (.Kind=="frame" and .Stall==true)) | {t:."@t", ms:.IntervalMs, attr:.Attribution}'
```

### Stalls broken down by attribution

```powershell
Get-Content $Path | jq -r 'select(.Kind=="stall") | .Attribution' | Group-Object | Sort-Object Count -Descending
```

If `non-dispatcher` dominates, also check `session_header.RenderTier` — on tier-0/RDP machines that's the expected baseline, not a bug.

### Render-environment sanity check (read this first on any new trace)

```powershell
Get-Content $Path -TotalCount 1 | jq '{tier:.RenderTier, mode:.RenderMode, remote:.IsRemoteSession, refresh:.RefreshRateHz, dpi:.Dpi}'
```

### Which module is slowest to first-activate

```powershell
Get-Content $Path | jq 'select(.Kind=="module_activated") | "\(.ModuleId)\t\(.DurationMs)ms"'
```

### Slowest dispatcher operation

```powershell
Get-Content $Path | jq -c 'select(.Kind=="dispatcher") | {p:.Priority, run:.RunMs, depth:.QueueDepthAtStart}' |
  Sort-Object { ($_ | ConvertFrom-Json).run } -Descending | Select-Object -First 10
```

### Memory pressure across the session

```powershell
Get-Content $Path | jq -c 'select(.Kind=="counter") | {t:."@t", ws:.WorkingSetMB, gen2:.Gen2, q:.DispatcherQueueDepth}'
```

### Did GC cause a specific stall? Look for a `gc` event within ~1 s of the stall

```powershell
Get-Content $Path | jq -c 'select(.Kind=="gc" or .Kind=="stall") | {t:."@t", k:.Kind, gen:.Generation, ms:.IntervalMs}'
```

### Binding-error flood detection

```powershell
Get-Content $Path | jq -c 'select(.Kind=="binding_error") | .Message' | Sort-Object -Unique
```

A handful of distinct messages each emitting once a second indicates a persistent broken binding — they accumulate layout-pass cost on every frame.

## Diagnostic patterns

A few signatures that have been useful in practice. Most failure modes have a characteristic combination — looking at one event kind in isolation is rarely enough.

**Stall on a software-rendered machine.** Read `session_header.RenderTier` *first* — if it's `0`, or `RenderMode == "SoftwareOnly"`, or `IsRemoteSession == true`, that user has no GPU acceleration. Any meaningful composition load *will* hitch and there's no fix on the app side; the answer is "this is the machine, not the build." Distinguishing this from a real transient GPU event used to require knowing the user's hardware; the three header fields now make it a one-line jq.

**Stall with `Attribution = "dispatcher"`.** UI thread was the cause. Filter `dispatcher` events around the stall timestamp and find the long-running op by `RunMs`. Look for `Priority=Render` or `DataBind` ops with high `RunMs` — that's where the time went.

**Stall with `Attribution = "non-dispatcher"`.** UI thread was idle (cumulative `dispatcher.RunMs` < 20 ms over the preceding 200 ms). Cause lives elsewhere: GC pause (look for a `gc` event with `Generation=2` within ~1 s), GPU/DWM hitch (especially on tier-0 or `IsRemoteSession`), blocked native call, or a swap-chain stutter. The trace doesn't name the offender but rules out the dispatcher cleanly so you stop digging in the wrong place.

**Gen-2 GC pause masquerading as "the app froze."** Look for a `gc` with `Generation=2` adjacent (≤1 s) to a `stall` event — and the stall should carry `Attribution = "non-dispatcher"` (GC pauses block the UI thread but don't show as dispatcher work). Working-set in the surrounding `counter` events will usually be at its session-high right before. Action: profile allocations on the hot path; the trace doesn't name the offender but `counter.WorkingSetMB` over time narrows the suspect window.

**UI thread saturated.** `counter.DispatcherQueueDepth` stays > 0 across many samples and `dispatcher.RunMs` p95 is elevated. Usually a hosted service is dispatching too eagerly to the UI thread, or a `RelayCommand` is doing work it shouldn't. Filter `dispatcher` by `Priority` to find which queue is backed up.

**Slow first-render of a lazy module.** Single large `module_activated` event for that module id. The work is the gate-open broadcast plus the view-type resolution; usually means the view's constructor is doing too much (loading data, building grids). Move the work behind `Loaded` or off the UI thread.

**CDN fetch on the UI thread.** `ref_fetch` events with significant `DurationMs` show CDN behavior is fine, but if they appear interleaved with elevated `frame_summary.P95` it means the awaiter resumed on the dispatcher and burned a frame budget on JSON parse. Reference data should be loaded off the UI thread.

**Input lag without obvious render cost.** `input_latency` consistently > 50 ms but `frame_summary` looks healthy. Means the input event itself isn't slow — something between the input firing and the next frame is. Usually a binding update or a `Dispatcher.Invoke` that runs at `DataBind`/`Render` priority. Filter `dispatcher` by timestamp surrounding the lagging input event.

**Binding-error flood.** Many unique `binding_error` messages, or one message that re-emits every second for the whole session. WPF retries failed bindings on every layout pass — even with no visual change, each pass costs CPU. The throttle in [`BindingErrorTraceListener`](../src/Mithril.Shared/Diagnostics/Performance/BindingErrorTraceListener.cs) collapses 60×/sec floods to ~1×/sec for storage, but the underlying app cost is still there.

**Calibration validation shows no ghosts.** Grep the trace for the toggle anchor first: `SetCalibrationValidation(on=True, …)` on category `Legolas.MapOverlay`. The next link is `RebuildCalibrationGhosts(<area>): built N from M refs …` (success) or `MapOverlayViewModel.RebuildCalibrationGhosts fallback for area <X>: no_overlay_cal` (skip). If the rebuild succeeded but no dots show, walk to the drawer's `Legolas.Overlay.GhostDrawer` transitions — `drawing` bucket reached means the draw pass executed; `empty` or `brush_null` names the broken link. The gap between toggle and any rebuild line names the missing wire.

## What the harness doesn't capture

- Startup time **before** the WPF `Application` exists — Velopack hooks, mutex acquisition, settings JSON load, `Host.Build`, eager-module gate opens. [`Program.Boot()`](../src/Mithril.Shell/Program.cs) markers in `boot.log` cover those.
- Render-thread work below the dispatcher level (WPF composition / DWM). `frame_summary` reflects the *user-visible* result but doesn't decompose into measure/arrange/render phases.
- GPU-side cost. `frame_summary` accounts for the full frame including GPU work, but doesn't attribute it.
- Code on threadpool threads that doesn't reach the dispatcher. `scope` markers cover this if instrumented manually.

## Extending the harness

Adding a new event kind:

1. Add the string constant to [`PerfEventKinds.cs`](../src/Mithril.Shared/Diagnostics/Performance/PerfEventKinds.cs).
2. Pick a canonical `ActivitySource` for the producer. If a cross-cutting source already fits in [`MithrilActivitySources`](../src/Mithril.Shared/Diagnostics/Telemetry/MithrilActivitySources.cs) (`Wpf`, `ShellModules`, `Reference`) or the Arda-side [`ArdaActivitySources`](../src/Arda/Arda.Abstractions/Diagnostics/ArdaActivitySources.cs), reuse it. Add a new entry to one of those catalogs if needed — never `new ActivitySource(...)` at a call site.
3. Emit at the producer with `using var act = MithrilActivitySources.X.StartActivity("op.name"); act?.SetTag(...)`. No `IsActive` check — `StartActivity` returns null when no listener is attached, so producers pay one volatile read in the off case.
4. Add a dispatch arm in [`PerfFileExporter.OnActivityStopped`](../src/Mithril.Shared/Diagnostics/Performance/PerfFileExporter.cs) keyed on `(source.Name, op)`, mapping tags to the JSON-lines record shape via the Serilog message template. Property names should be `CamelCase` (Serilog preserves placeholder case).
5. For UI-thread hooks, wire/unwire in [`PerfRecorderHostedService.AttachHooks`](../src/Mithril.Shared/Diagnostics/Performance/PerfRecorderHostedService.cs).
6. Document the new `Kind` in this file under **Event kinds** — include the property table.

For a `Meter` counter instead of a span: add the instrument to the relevant catalog ([`MithrilMeters`](../src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs) or [`ArdaMeters`](../src/Arda/Arda.Abstractions/Diagnostics/ArdaMeters.cs)), emit with `Counter.Add(1, tag)`. `PerfFileExporter.AccumulateCounter` aggregates per `(instrument, tag-set)` and flushes as `meter_counter` records once per second — no new exporter code needed unless you want a custom record shape. For hot paths where tag values are expensive to compute (e.g. `parsed.Verb.ToString()`), gate on `Counter.Enabled` first.
