# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

Mithril is a modular WPF desktop companion app for the MMORPG *Project Gorgon*. It tails the game's log files in real time, parses events, and provides modules for gardening, surveying, food tracking, NPC favor, skill advising, timers, and storage management.

## Build & Test

```bash
# Build everything (requires .NET 10 SDK)
dotnet build Mithril.slnx

# Run all tests
dotnet test Mithril.slnx

# Run a single test project
dotnet test tests/Samwise.Tests

# Run a single test by name
dotnet test tests/Samwise.Tests --filter "FullyQualifiedName~GardenStateMachineTests.Tier1_StartInteraction"

# Run the app
dotnet run --project src/Mithril.Shell
```

Module DLLs are auto-copied to `src/Mithril.Shell/{config}/modules/` on build (see `Directory.Build.targets`). No manual copy step needed.

## Build Configuration

- **.NET 10**, `net10.0-windows`, C# latest, nullable enabled, warnings-as-errors (except CS1591)
- Central package management via `Directory.Packages.props`
- `VSTHRD002` (sync-over-async) is enforced as error; other VSTHRD rules are suppressed (no VS JoinableTaskFactory context)
- Test framework: **xunit** + **FluentAssertions**

## Architecture

### Module System

Every module is a class library whose folder name ends with `.Module`, implementing `IMithrilModule` (in `Mithril.Shared/Modules/`). The interface requires:

- `Id`, `DisplayName`, `Icon` (Lucide), `SortOrder`, `DefaultActivation` (Eager/Lazy)
- `ViewType` (main UI), optional `SettingsViewType`
- `Register(IServiceCollection)` for DI setup

Modules are discovered at runtime via reflection from the `modules/` folder (`ShellServiceCollectionExtensions.AddMithrilModules`). Lazy modules are gated by `ModuleGate` — a `TaskCompletionSource`-based latch opened on first tab selection.

### Current Modules

| Module | Id | Purpose | Activation |
|---|---|---|---|
| Samwise | samwise | Garden/crop tracking, ripeness alarms | Eager |
| Pippin | pippin | Food consumption & recipe tracking | Lazy |
| Legolas | legolas | Surveying, route optimization, map overlay | Lazy |
| Arwen | arwen | NPC favor & gift tracking | Lazy |
| Elrond | elrond | Skill leveling advisor | Lazy |
| Gandalf | gandalf | User-created timers with alarms | Eager |
| Bilbo | bilbo | Storage/inventory management | Lazy |

This table is purpose-only and non-exhaustive (Silmarillion and Celebrimbor also ship). **Before proposing or building work for a module, read [docs/module-charters.md](docs/module-charters.md)** — it records each module's responsibility *boundaries* (what it explicitly does **not** own, and why). A data-availability gap is not a feature unless it serves the module's charter.

### Arda Pipeline (sole log-processing engine)

Arda is a deterministic log-replay and live world-state tracking engine organised in five layers:

| Layer | Project(s) | Responsibility |
|---|---|---|
| L0 | `Arda.Ingest` | Tails `Player.log` + `ChatLogs/*.log` via `ILogLineSource` |
| L1 | `Arda.Ingest` | Span-based zero-alloc line parsing, string interning |
| L2 | `Arda.Dispatch` | `VerbExtractor` + `FrozenDictionary` dispatch table |
| L3 | `Arda.World.Player`, `Arda.World.Chat` | Stateful `IFrameHandler` implementations; emit domain events via `IDomainEventPublisher` |
| L4 | `Arda.Composition` | Cross-source composers (session fusion, inventory correlation, word-of-power) |

`Arda.Hosting` bootstraps the pipeline and exposes `ArdaOptions` for DI. `Arda.Contracts` holds the public domain events, state interfaces (`ISessionState`, `IAreaState`, `IPlayerState`, `IChatSessionState`), and subscriber/publisher contracts (`IDomainEventSubscriber`, `IDomainEventPublisher`).

Modules consume Arda via `IDomainEventSubscriber` and the read-only state interfaces — they never reference the internal handler or dispatch types.

### Shell Bootstrap (Program.cs)

Single-instance mutex guard &rarr; game root detection &rarr; settings load &rarr; `IHost` build &rarr; eager module gates opened &rarr; WPF `App.Run()`. Second-instance attempts raise the existing window via `EventWaitHandle`.

### Shared Infrastructure (Mithril.Shared)

DI is composed via extension methods in `Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs`:

- **Game services**: `IGameClock`, `IShiftCatalog`, `IGameReportsService`, `IActiveCharacterService` — game clocks, character snapshots
- **Reference data**: `IReferenceDataService` — fetches JSON (items, recipes, skills, NPCs, XP tables) from `cdn.projectgorgon.com` with bundled fallback and background refresh
- **Settings**: `ISettingsStore<T>` / `JsonSettingsStore<T>` with `System.Text.Json` source-generated contexts; `SettingsAutoSaver<T>` for periodic persistence
- **Hotkeys**: OS-level Win32 hotkey registration; modules provide `IHotkeyCommand` implementations; `HotkeyConflictDetector` validates uniqueness
- **Diagnostics**: `ILogger` via `DiagnosticsLoggerProvider` (ring buffer, Rx live stream, Serilog compact-JSON file)
- **Logging**: inject `ILoggerFactory.CreateLogger("Subsystem")` (e.g. `"Arda.Player"`, `"Reference"`, `"Samwise"`) so diagnostics UI category filters stay stable. Use MEL message templates with named properties (`LogInformation("Loaded {FileName} from cache ({CdnVersion})", …)`), not `$"…"` interpolation. Levels: `Trace` = per-event/hot-path; `Information` = lifecycle milestones; `Warning` = recovered/degraded; `Error` = failure + exception. Use [`ThrottledWarn`](src/Mithril.Shared/Diagnostics/ThrottledWarn.cs) on hot ingest paths. Require `ILogger` on `BackgroundService` / `IHostedService` and Arda pipeline types; `ILogger?` only for optional WPF/import targets.
- **Telemetry (traces + metrics)**: emit via `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` using the canonical instances in [`Mithril.Shared.Diagnostics.Telemetry`](src/Mithril.Shared/Diagnostics/Telemetry/) — `MithrilActivitySources` (per-subsystem sources, names mirror the logger categories: `"Mithril.Arda.Player"`, `"Mithril.Reference"`, `"Mithril.Shell.Modules"`, …) and `MithrilMeters` (one `Meter` per top-level subsystem, instruments centrally defined). Never `new ActivitySource(...)` / `new Meter(...)` at a call site — add it to the relevant `MithrilActivitySources` / `MithrilMeters` static so the vocabulary stays in one file. Recording is opt-in: when no `PerfRecorder` session is active, `StartActivity` returns null and `Meter.Record` is a no-op, so producers emit unconditionally without `if (active)` guards. Use a span (`using var act = MithrilActivitySources.X.StartActivity("name"); act?.SetTag(…)`) for discrete events with attributes (module activate, ref fetch, dispatcher op); use a `Histogram` for high-frequency duration/distribution measurements (frame interval, input latency); use a `Counter`/observable gauge for rates and snapshots. Tag keys are lowercase dotted (`module.id`, `cache_hit`, `priority`); when a new tag/instrument lands, update the shape contract in [docs/perf-trace-schema.md](docs/perf-trace-schema.md) and the byte-parity tests in [`PerfTracerTests`](tests/Mithril.Shared.Tests/PerfTracerTests.cs).
- **OTLP export (opt-in)**: when `TelemetrySettings.EnableOtlpExport` is `true`, `Mithril.Shared.Telemetry` wires an OTLP exporter through `OpenTelemetry.Extensions.Hosting`. Producer surface is unchanged — the OTLP exporter is a parallel listener on the same `ActivitySource`/`Meter`/`ILogger` catalogs that the perf-recorder file exporter consumes. The same three-layer scrubbing model runs on all three surfaces (mithril#841 — spans via `AllowlistAndRedactionProcessor`, log records via `LogScrubbingProcessor`, metric dimensions via the `MetricTagAllowlistView` view filter): producer-declared `TagCatalog` (default-on for Safe/Identifying, default-off for Sensitive); user-editable allowlist in `TelemetrySettings.TagExports`; `ValueRedactor` substring-scrubs `%USERPROFILE%` / `%LocalAppData%` paths + active character name from passing string values (and the formatted log body for the logs surface). Unknown tag keys are dropped fail-closed and surfaced in the settings UI's "Newly seen" panel for the user to promote. Spans + logs re-read `TagExports` per record so chip-cloud toggles are live; the metric view captures TagExports once per instrument observation, so metric-only toggles are restart-required for v1. `TelemetrySettings.TrustEndpoint = true` symmetrically bypasses the allowlist gate on all three surfaces while still running `ValueRedactor`. Off by default (zero `HasListeners()` cost when disabled). See mithril#815, mithril#840, mithril#841.
- **Query system**: SQL-like filtering over data models — `MithrilDataGrid`/`MithrilQueryBox` (tabular UI), `QueryFilter` attached behaviour (any `ItemsControl`), `QueryableSource<T>` (VM-side, headless). See [docs/query-system.md](docs/query-system.md) before adding new filter UI.

### Patterns to Follow

- **MVVM with CommunityToolkit.Mvvm** source generators (`[ObservableProperty]`, `[RelayCommand]`)
- **Settings classes** implement `INotifyPropertyChanged` with source-generated JSON serialization contexts (not reflection)
- **Log parsing**: Arda L3 handlers implement `IFrameHandler.Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)` with span-based zero-alloc parsing and emit domain events via `IDomainEventPublisher`. Module-level consumers subscribe via `IDomainEventSubscriber`
- **HostedServices** for background work; gated behind `ModuleGate.WaitAsync()` for lazy modules
- **Instrumentation**: when adding a new lifecycle event (module activate, gate open, service start), outbound IO (HTTP fetch, file load, CDN refresh), discrete cross-source event (composer step, dispatch table miss), or any duration/distribution measurement (parse latency, queue depth, batch size), emit a span or instrument via the canonical statics in `Mithril.Shared.Diagnostics.Telemetry` rather than relying on `ILogger.LogInformation` alone — logs answer "what happened", spans/metrics answer "how often / how long / what shape". See the **Telemetry** bullet above for the API and naming convention. Producer cost is zero when no perf-recording session is attached, so guards like `if (recorder.IsActive)` are not needed.
- **WPF resources** shared via `Mithril.Shared.Wpf/Resources.xaml`; icons from MahApps Lucide icon pack
- **Before editing any `*.xaml` or writing a new view, read [docs/wpf-gotchas.md](docs/wpf-gotchas.md)** — catalogues runtime-only WPF traps (hit-testing, null-leak templates, binding-mode defaults, `ItemContainerStyle` rules, etc.) that build green + tests green but break the UI silently.
- **For C# work touching >1 type, load the LSP tool first** (`csharp-lsp` plugin is enabled; `ToolSearch query: "select:LSP"` to fetch its schema, then use it for go-to-def / find-refs / type info). Grep alone misses partial classes, source-generated members (`[ObservableProperty]` setters, JSON contexts), and overload signatures, so a "no callers" or "no implementations" claim from text search alone is not load-bearing.
- **Cross-source correlation** — before wiring a new consumer that fuses Player.log + chat (or any two streams), read [docs/cross-source-correlation.md](docs/cross-source-correlation.md). It defines the Tier 1/2/3/4 decision tree and points at the canonical references (`PendingCorrelator<TKey,TReq>` for Tier 1, `MotherlodeMeasurementCoordinator` for Tier 2). Skip a tier and you'll reinvent the pre-#541 "credit at least 1 if the add never arrived" folk fallback.

### Game Data Paths

The app reads from `%LocalAppData%Low/Elder Game/Project Gorgon/`:
- `Player.log` — primary event source (garden actions, combat, items)
- `ChatLogs/` — chat message logs
- `Reports/` — character data exports

App settings persist to `%LocalAppData%/Mithril/`.

### CDN Reference Data

`ReferenceDataService` fetches versioned JSON from `https://cdn.projectgorgon.com/{version}/data/{file}.json`. Version is auto-detected by `CdnVersionDetector` (parses redirect meta tag). Bundled copies under `Mithril.Shared/Reference/BundledData/` serve as fallback. Item icons are available at `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`. Full file inventory + schema notes: [wiki: CDN Reference Data](https://github.com/moumantai-gg/mithril/wiki/CDN-Reference-Data).

## Where does new content go?

Project knowledge is split across four tiers. Route new content by what it is:

| If you're writing… | Put it… |
|---|---|
| A pending unit of work (bug, feature, chore) | A GitHub Issue. Use the bug/feature template; the dropdowns auto-apply `module:*` and `area:*` labels. For behavior that was working and broke as a consequence of a known change, also add the `regression` label (the template doesn't surface it yet — apply manually). The issue body can be a brief description that refers to the relevant spec/plan in `docs/planning/<slug>/`. |
| Roadmap / prioritisation state | `docs/roadmaps/` in this repo (one file per module/area). The [**Mithril Roadmap** Project](https://github.com/orgs/moumantai-gg/projects/1) (org-level, replaced the legacy user-level board 2026-05-21) is still the queryable board; custom fields: `Status`, `Priority`, `Module`. Don't add inline checklists to roadmap docs — the doc holds *why*, the issue holds *what*. |
| Stable reference, process, how-to, user guide | The [wiki](https://github.com/moumantai-gg/mithril/wiki). Stable content; doesn't co-evolve with code. |
| Design rationale that co-evolves with code | `docs/` in this repo. Architecture decisions, design notebooks, cross-cutting concerns. |
| **Spec or plan for a feature / agent task** | `docs/planning/<human-readable-slug>/` in this repo. Specs and plans for the same effort live side by side (e.g. `docs/planning/gwaihir-v1.0/spec.md` + `plan.md`). Every new slug folder must be appended to `docs/planning/INDEX.md` with status + linked issue/PR. |

**Workflow rules:**

1. **Backlog item → Issue first.** Don't add a checkbox to a roadmap doc. Issues are queryable, have state, and surface on the Project board.
2. **Issue references plan, plan/roadmap doesn't list issues inline.** Each issue body links to the relevant `docs/planning/<slug>/` or `docs/roadmaps/<file>` for context. Roadmap docs link to the *Project* (which lists the issues), not to individual issues, so docs don't rot when issues close.
3. **Anything load-bearing-but-unverified gets a "Verification owed" marker** in the design notebook. Filing an issue for the spot-check is the *task side*; the doc entry stays for context.
4. **Specs and plans are durable artifacts under `docs/planning/<slug>/`.** They are NOT scratch and are NOT deleted when implementation lands — the `INDEX.md` row gets its status flipped (e.g. `active` → `shipped`) instead. A cold/spawned session reads the issue, the issue links to the slug folder, and the folder is self-contained from there.
5. **Append to the index every time.** `docs/planning/INDEX.md` is the agent-readable directory. Every new slug folder MUST add a row: `slug | status | issue/PR | one-line description`. Status values: `active`, `shipped`, `deferred`, `abandoned`.
6. **Scratch is `.claude/plans/` and tempfiles, NOT `docs/`.** Pre-commit thinking, throwaway analysis, and one-shot drafts don't belong in `docs/planning/` — that directory is for content worth keeping. Use `.claude/plans/` or `$env:TEMP` for true scratch and delete it.
