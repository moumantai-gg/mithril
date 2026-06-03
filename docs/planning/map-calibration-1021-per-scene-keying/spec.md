# Spec — per-scene calibration keying (mithril#1021)

**Tracked in:** [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021).
**Brainstormed:** 2026-06-03 with @arthur-conde; decisions captured below in §3.
**Related (downstream consumer):** [mithril#914](https://github.com/moumantai-gg/mithril/issues/914) (map auto-calibration engine umbrella) — this work is upstream of #914 and lands independently.
**Related (resolved earlier):** [mithril#966](https://github.com/moumantai-gg/mithril/issues/966) (auto-calibration `Refine` perf stall, CLOSED — did not touch keying).
**Canonical references:**
- Wiki: [Player-Log-Signals → Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures) — log-line grammar + naming convention + 79-bundle inventory.
- Memory: `pg_map_asset_load_log_grammar` (user memory, not repo-resident) (pointer + corrections recorded during this brainstorm).

## 1. Problem

Mithril's `IMapCalibrationService` keys all calibration storage on a single `areaKey` string (e.g. `"AreaEltibule"`) sourced from Arda's `IAreaState.CurrentArea`, which in turn comes from the `!!! Initializing area! (<id>): <AreaX>` log line. The areas.json key is *one level too coarse* for PG's actual map-texture topology:

- The game ships **79 distinct `Map_<X>.png` bundles** (verified 2026-06-03 against `…\WindowsPlayer_Data\StreamingAssets\aa\StandaloneWindows64\`).
- Only **12** of those map one-to-one with `areas.json` `AreaX` keys (`Map_AreaSerbule` ↔ `AreaSerbule`, etc.).
- **~51** are **sub-zone scenes** with no `Area` infix and no own `areas.json` entry (`Map_HogansKeepBasement`, `Map_GoblinDungeon`, `Map_KurTower`, `Map_WinterNexus`, `Map_CarpalTunnels`, …).
- **16** are War Cache treasure-map *items* (`Map_WarCache_<Region><N>`) — out of scope here (see §7).

Six `AreaX` entries are **aggregators**: they cover multiple Unity scenes that share one `areas.json` registration. `AreaCave1`, `AreaCave2`, `AreaGazlukCaves`, `AreaKurCaves`, `AreaPovusCaves2`, `AreaTomb1`. The aggregator entries themselves **have no top-level `Map_<AreaX>.png` bundle at all** — PG ships only the sub-zone-named maps for those areas.

### 1.1 Concrete failure mode — autocal in Hogan's Basement

1. Player enters Hogan's Keep basement.
2. PG emits: `(502934): AreaCave1` → Arda's `Map.HandleInitializingArea` sets `IAreaState.CurrentArea = "AreaCave1"`.
3. PG also emits (live-verified 2026-06-03): `Downloading Map [44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 for area Hogan's Basement runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[Map_HogansKeepBasement]` — but Mithril has no handler for this line shape; the data is dropped.
4. User triggers autocal. `AutoCalibrationEngine.TryCalibrateCurrentAreaAsync` reads `var area = _areaState.CurrentArea ?? string.Empty;` → `"AreaCave1"`.
5. `_baseTextures.ResolveBaseTextureAsync("AreaCave1", …)` → `IBaseTextureProvider.TryGetBaseTexture("AreaCave1")` → cache miss → sidecar invoked with `--area AreaCave1` → sidecar globs for `maps_assets_assets_art_maps_map_areacave1.png_*.bundle` → **0 matches** (the bundle doesn't exist) → sidecar exits non-zero → autocal logs warning and safe-degrades.
6. Net: autocal **cannot calibrate any of the ~51 sub-zone scenes**, ever, regardless of how good the detector or solver is.

The bundled `map-calibration-baseline.json` ships anchors for `AreaSerbule`, `AreaEltibule`, `AreaKurMountains` (all directly-registered, all with a matching `Map_<AreaX>` bundle). For users in those three areas, autocal works today. For every other scene the player visits, autocal is silently inoperative.

## 2. Goal

Switch the calibration store's primary key from `areaKey` (areas.json) to the **literal per-scene map asset name** (e.g. `Map_HogansKeepBasement`, `Map_AreaSerbule`, prefix included — the verbatim Unity Texture2D name from the runtime-key bracket in the `Downloading Map` log line).

Downstream of that:
- Arda gains a new handler that parses the asset-loader line and surfaces the per-scene asset + sub-zone-level friendly name into the existing `IMapState` umbrella.
- Autocal switches DI from `IAreaState` to `IMapState`, reads `CurrentMapAsset`, and refuses calibration outright when it's null (vs. silently producing a misfit).
- The NPC reference filter gains a sub-zone-level scope so aggregator scenes resolve to the right NPC subset.
- The sidecar contract renames `--area <X>` → `--asset <Map_X>` to track the change at the seam.
- Persistence (`map-calibration-baseline.json` + the user-side `UserRefinementStore`) migrates to the new key shape, with the user-side migration running once at load time.

## 3. Ratified design decisions

Each row was litigated during the 2026-06-03 brainstorm; option letters reference the alternatives surfaced at that time. **No "Other" / open-ended outcomes** — every decision is closed.

| Decision | Choice | Rationale |
|---|---|---|
| **D1. State home** | Extend the existing `Arda.Contracts.State.Player.IMapState` umbrella. | `IMapState` already aggregates map-scoped state (area + position + weather + pins) by delegating to per-handler classes via the `MapScope` composite. The new fields fit the existing pattern; one new ctor param on `MapScope`; lowest plumbing cost. |
| **D2. Key form** | Literal `Map_<X>` end-to-end, sidecar CLI renames `--area <X>` → `--asset <Map_X>`. | The bracketed string in the log is the literal Unity Texture2D name (matches the PNG filename in the `Completed load` Dependencies line and what `tools/MapAssetSpike` already uses as `TargetTextureName`). Storing/keying anything else requires a normalization layer that fights the existing tooling. |
| **D3. Cold-start behaviour** | **Strict gate** — `CurrentMapAsset == null` → autocal returns `RejectReason: "Map asset not yet known — change zones once or restart while in this scene."`, never attempts. No fallback to `Map_<CurrentArea>` reconstruction. | Owner-ratified: *"Autocal is powerful enough without a best-effort lookup."* Single code path, no ambiguity, no special-cased fallback string. Mild cost (direct-area user on cold start has to zone once) is acceptable because zone changes are routine within a session. |
| **D4. Reference provider API** | `IAreaReferenceProvider.ForArea(string)` → `ForArea(MapSceneRef)` where `MapSceneRef = (ParentAreaKey, SceneFriendlyName?)`. | Single entry point, honest signature: every call-site is forced to think about both halves of the scene identity. `SceneFriendlyName == null` collapses to the existing area-only filter for back-compat. ~10 mechanical call-site rewrites in one PR, no overload footgun. |
| **D5. baseline.json migration** | Hand-edit + `schemaVersion` 1 → 2. | Bundled data, we own both shapes. No load-time migrator code; the loader reads keys from the `anchors` dict as-is. |
| **D6. UserRefinementStore migration** | Load-time code migrator: v1 (no `schemaVersion`) → v2 (`schemaVersion: 2`, keys prefixed with `Map_`). Persisted immediately, idempotent on subsequent boots. | Persisted user state; **never accept silent data loss** (memory rule). Single mechanical prefix rewrite; no `IVersionedState<T>`/Migrate-ladder needed since it's a one-step change to internal-only persistence. |
| **D7. War Cache scope** | Out of scope. No special-casing in the texture-resolution path. | Treasure-map *items*, not scenes the player visits via M. If one ever does fire a `Downloading Map` line at scene-load (verification owed), autocal will attempt it and fail-soft via the empty-references path — no new code needed. |
| **D8. #914 dependency** | Land independently. | #914 is downstream of this fix; the engine umbrella will consume the new state once it's in. PR-1 of #914 already merged, so this work is a patch to the existing autocal pipeline. |

## 4. Architecture overview

```
Player.log "Downloading Map [<GUID>] GUID <GUID> for area <Friendly> runtime key <GUID>[Map_<X>]"
       │
       ▼
Arda.Dispatch.VerbExtractor                              ← new prefix branch: "Downloading Map " → Verbs.DownloadingMap
       │  (emits Verbs.DownloadingMap, args = rest of line)
       ▼
Arda.World.Player.Internal.MapAssetLoader                ← NEW IFrameHandler
       │  (parses args, updates state, publishes MapAssetChanged)
       ▼
Arda.World.Player.Internal.MapScope                      ← extended (5th composed handler)
       │  (delegates CurrentMapAsset, CurrentSceneFriendlyName, MapAssetMeasuredAt)
       ▼
Arda.Contracts.State.Player.IMapState                    ← extended contract (3 new properties)
       │
       ▼
Mithril.MapCalibration.Capture.AutoCalibrationEngine     ← ctor swaps IAreaState for IMapState
       │  (strict gate on CurrentMapAsset; builds MapSceneRef from (CurrentArea, CurrentSceneFriendlyName))
       ▼
Mithril.MapCalibration.Capture.IAreaReferenceProvider    ← ForArea(string) → ForArea(MapSceneRef)
       │
       ▼
ReferenceDataAreaReferenceProvider                       ← NPC filter: AreaName == parent
       │                                                    && (sceneFriendly == null || AreaFriendlyName == sceneFriendly)
       │  Landmark filter: AreaName == parent (no sub-zone field on landmarks.json; partial coverage by design)
       ▼
Mithril.MapCalibration.Detection.IBaseTextureProvider    ← parameter rename "areaKey" → "mapAssetKey" (string-typed unchanged)
       │
       ▼
Mithril.AssetExtractor sidecar                           ← --area renamed to --asset; lowercase internally for bundle glob
       │
       ▼
map-calibration-baseline.json                            ← schemaVersion: 1 → 2, anchor keys hand-prefixed with "Map_"
%LocalAppData%\Mithril\MapCalibration\refinements.json   ← UserRefinementStore migrator: v1 keys prefixed with "Map_" on first v2 load
```

The literal `Map_<X>` string is the calibration key everywhere south of `IMapState`. NPCs filter on `(parent AreaName, sub-zone AreaFriendlyName)` when in an aggregator scene, on `AreaName` alone (sceneFriendlyName=null) when in a directly-registered area.

## 5. Layer-by-layer detail

### 5.1 Arda layer

**Verb registration** (`src/Arda/Arda.Dispatch/Verbs.cs`):
```csharp
/// <summary>Synthetic verb for the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_<X>]" asset-loader line.</summary>
public const string DownloadingMap = "DownloadingMap";
```

**VerbExtractor** (`src/Arda/Arda.Dispatch/VerbExtractor.cs`) — add a new prefix branch alongside the existing `"LOADING LEVEL "` / `"!!! Initializing area! "` cases:
```csharp
if (log.StartsWith("Downloading Map "))
    return new ParsedVerb(Verbs.DownloadingMap, log["Downloading Map ".Length..]);
```

**New event** (`src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs`) — mirrors `AreaChanged`:
```csharp
public readonly record struct MapAssetChanged(
    string? PreviousMapAsset,
    string? CurrentMapAsset,
    string? CurrentSceneFriendlyName,
    LogLineMetadata Metadata);
```
Carrying both `MapAsset` and `SceneFriendlyName` on the same event lets a subscriber pin both atomically (the two fields are always set together — every `Downloading Map` line carries both halves).

**New handler** (`src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`) implements `IFrameHandler`. Parses `args` of the shape `[<GUID>] GUID <GUID> for area <FriendlyAreaName> runtime key <GUID>[<AssetName>]`:

- `SceneFriendlyName` = substring between `for area ` and ` runtime key ` (defensive `IndexOf` after the `for area` boundary). The friendly name can contain apostrophes (`Hogan's Basement`) and spaces (`Caves Beneath Kur Mountains`); no further sanitization.
- `MapAsset` = substring inside the **last** `[…]` block — the runtime-key bracket. The earlier `[<GUID>]` at the head of args is also a bracket pair, so the implementation matches from the right (`LastIndexOf('[')` + `LastIndexOf(']')`).
- Malformed line (no `runtime key `, no trailing `]`, empty args, no `for area `): handler silently skips, no state mutation, no event published. The dispatch table doesn't care about return values; safe-degrade in the parser is the established pattern.
- On successful parse, updates state + publishes `MapAssetChanged` if either `MapAsset` or `SceneFriendlyName` actually changed (idempotent re-parse is a no-op event).

State surfaced by `MapAssetLoader`:
```csharp
public string? CurrentMapAsset { get; private set; }
public string? CurrentSceneFriendlyName { get; private set; }
public DateTimeOffset? MapAssetMeasuredAt { get; private set; }
```

**IMapState extension** (`src/Arda/Arda.Contracts/State/Player/IMapState.cs`) — three new properties, slotted between the existing `// --- Area ---` and `// --- Position ---` blocks. Doc comments updated to reference the new event and the wiki section:
```csharp
// --- Map asset (per-Unity-scene texture identity) ---

/// <summary>Literal Unity Texture2D name for the currently-displayed map texture (e.g. <c>"Map_HogansKeepBasement"</c>),
/// including the <c>Map_</c> prefix. Source: Player.log's <c>Downloading Map ... runtime key ...[Map_<X>]</c> line.
/// <c>null</c> until the first such line is observed in this session.</summary>
string? CurrentMapAsset { get; }

/// <summary>Sub-zone-level friendly area name from the same line (e.g. <c>"Hogan's Basement"</c>), which for
/// aggregator areas (<c>AreaCave1</c>, etc.) differs from <c>IMapState.CurrentArea</c>'s parent FriendlyName.
/// Matches the per-NPC <c>AreaFriendlyName</c> field in npcs.json.</summary>
string? CurrentSceneFriendlyName { get; }

/// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
DateTimeOffset? MapAssetMeasuredAt { get; }
```

**`MapScope`** (`src/Arda/Arda.World.Player/Internal/MapScope.cs`) — extended primary constructor + three delegating properties:
```csharp
internal sealed class MapScope(
    Map map, Position position, Weather weather, MapPins pins, MapAssetLoader mapAsset) : IMapState
{
    // ... existing delegations preserved verbatim ...
    public string? CurrentMapAsset => mapAsset.CurrentMapAsset;
    public string? CurrentSceneFriendlyName => mapAsset.CurrentSceneFriendlyName;
    public DateTimeOffset? MapAssetMeasuredAt => mapAsset.MapAssetMeasuredAt;
}
```

**DI registration** (`src/Arda/Arda.World.Player/PlayerWorldExtensions.cs`) — register `MapAssetLoader` as a singleton, register it as an `IFrameHandler` against `Verbs.DownloadingMap`, and append it to `MapScope`'s ctor wiring. Same pattern as the existing handlers.

**Explicitly out of scope at this layer:**
- We do **not** parse the `Completed load of [GUID]: …` follow-up. The `Downloading Map` line alone carries everything we need.
- We do **not** parse the `>> Map_<X> (UnityEngine.Texture2D) UnityEngine.Texture2D` third variant. Redundant signal.
- The `[<GUID>]` Addressables hash at the head of args is **not** surfaced as state. It changes per patch and isn't load-bearing for calibration keying (canonical-asset-hash gating is keyed off texture *bytes*, not the line's GUID).

### 5.2 Calibration layer

**New shared type** (`src/Mithril.MapCalibration/MapSceneRef.cs`) — composite key for the reference provider:
```csharp
/// <summary>Composite identifier for a single Unity scene's calibration scope.
/// <c>ParentAreaKey</c> is the areas.json key (always non-null in practice — Arda surfaces it from
/// <c>!!! Initializing area! </c>). <c>SceneFriendlyName</c> is the sub-zone-level npcs.json
/// <c>AreaFriendlyName</c>; <c>null</c> for directly-registered areas, set for aggregator-area sub-zones.</summary>
public readonly record struct MapSceneRef(
    string ParentAreaKey,
    string? SceneFriendlyName);
```

**Provider signature change** (`src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs`):
```csharp
// Before
IReadOnlyList<LandmarkReference> ForArea(string areaKey);
// After
IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef);
```

`ReferenceDataAreaReferenceProvider` keeps both halves of its filter:
```csharp
// Landmarks: landmarks.json has no sub-zone field — keep area-only filter, accept partial
// coverage for aggregator scenes (the solver's RANSAC tolerates mixed-scene landmarks; if a
// specific aggregator scene needs better landmark scope, a follow-up issue can add a per-scene
// world-coord bbox).
if (_refData.Landmarks.TryGetValue(sceneRef.ParentAreaKey, out var landmarks))
{
    foreach (var lm in landmarks) { /* existing emit, unchanged */ }
}

// NPCs: narrow to (AreaName, AreaFriendlyName) when SceneFriendlyName is set.
foreach (var npc in _refData.NpcsByInternalName.Values)
{
    if (!string.Equals(npc.AreaName, sceneRef.ParentAreaKey, StringComparison.Ordinal)) continue;
    if (sceneRef.SceneFriendlyName is { } sub
        && !string.Equals(npc.AreaFriendlyName, sub, StringComparison.Ordinal)) continue;
    // ... existing emit (Pos parsing, LandmarkReference construction), unchanged ...
}
```

For directly-registered areas (`SceneFriendlyName == null`), the NPC filter collapses to the existing `AreaName == ParentAreaKey` behaviour. Back-compat preserved at the implementation level.

**Texture-provider seam** (`src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs` — moved out of `Mithril.MapCalibration` proper by [#1028](https://github.com/moumantai-gg/mithril/pull/1028)'s project split, shipped 2026-06-02): signature stays string-typed; parameter renamed `areaKey` → `mapAssetKey` for honesty. The cache filename rolls naturally to `map-texture-Map_HogansKeepBasement.{json,bin}` because `CachedBaseTextureProvider` interpolates the key into the filename verbatim. Doc comment updated to reference the new identifier shape.

**Sidecar contract rename** (`tools/Mithril.AssetExtractor/`):
- `ExtractRequest.AreaKey` → `ExtractRequest.MapAssetName` (record field rename in `src/Mithril.MapCalibration/IAssetExtractor.cs`).
- CLI flag `--area <X>` → `--asset <Map_X>` (in `ProcessAssetExtractor.BuildStartInfo` + the sidecar `Program.cs` argument parser + the README).
- Sidecar internally still lowercases the asset name when matching against the bundle glob (`maps_assets_assets_art_maps_<lowercased>.png_*.bundle`) — case-preservation gotcha (wiki) is handled *inside* the sidecar; callers always pass the literal PascalCase form.

**AutoCalibrationEngine** read switch (`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`):
- Constructor: replace `IAreaState _areaState` with `IMapState _mapState`. Drop the IAreaState dep (the parent area key is read off `_mapState.CurrentArea`, which `IMapState` already surfaces from the same source).
- `TryCalibrateCurrentAreaAsync`:
  ```csharp
  // Strict gate — no fallback (D3).
  if (string.IsNullOrEmpty(_mapState.CurrentMapAsset))
  {
      return new AutoCalibrationOutcome(
          Persisted: false,
          AreaKey: _mapState.CurrentArea,   // context for logs/telemetry only, not used as a lookup key
          RejectReason: "Map asset not yet known — change zones once or restart while in this scene.");
  }
  var assetKey = _mapState.CurrentMapAsset;
  var sceneRef = new MapSceneRef(
      ParentAreaKey: _mapState.CurrentArea ?? string.Empty,
      SceneFriendlyName: _mapState.CurrentSceneFriendlyName);
  var attempt = new CalibrationAttemptContext(assetKey, DateTimeOffset.UtcNow);
  // ... existing flow, with assetKey replacing `area` in TryGetBaseTexture / ExtractRequest,
  //     sceneRef in _references.ForArea(...)
  ```

**Status formatter** (`src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` + `OutcomeVocabulary`): one new vocabulary entry `MapAssetNotYetKnown` mapped to the refusal string verbatim. Routed to the Palantir debug surface (existing `Outcome` column) and the autocal toast/status chip surfaced via the existing `IAutoCalibrationRunner.Status` event.

### 5.3 Persistence migration

**`src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`** — hand-edit, no code change:
```jsonc
// Before
{ "schemaVersion": 1, "anchors": { "AreaSerbule": {...}, "AreaEltibule": {...}, "AreaKurMountains": {...} } }
// After
{ "schemaVersion": 2, "anchors": { "Map_AreaSerbule": {...}, "Map_AreaEltibule": {...}, "Map_AreaKurMountains": {...} } }
```
Anchor values (Scale/Rotation/Origin/etc.) are byte-for-byte unchanged. The loader (`BundledBaselineLoader`) reads keys from the `anchors` dict as-is; bumping the version is forward-compat hygiene per the *any persisted JSON should carry a schema version* memory rule. No legacy-form acceptance — this file ships with the app, so a #1021-aware Mithril build by definition ships v2.

**`UserRefinementStore`** (`src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`) — load-time migrator, runs once per install on first boot of the #1021-aware build:

Adds a top-level `schemaVersion` field (currently absent → treated as v1):
```jsonc
// Before (v1, implicit)
{ "calibrations": { "AreaSerbule": { ...AreaCalibration... }, ... } }
// After (v2, explicit)
{ "schemaVersion": 2, "calibrations": { "Map_AreaSerbule": { ... }, ... } }
```

Migrator behaviour, inserted into the existing `Load()` walk:
1. Parse JSON. If root has `schemaVersion: 2`, no migration; proceed.
2. If root is missing `schemaVersion` (legacy v1 shape), walk each key in `calibrations`:
   - If the key already starts with `"Map_"` (defensive — shouldn't happen in v1, but harmless): keep as-is.
   - Otherwise: prefix with `"Map_"`.
3. After the in-memory dict is rewritten, **persist immediately** with `schemaVersion: 2` — same transactional `Persist()` path the existing `Save` uses (rollback on disk-write failure; the in-memory state cannot diverge from on-disk).
4. Log one `Information` line at migration: *"Migrated N user refinements to v2 (Map_<X> keying)."*

Migration is **idempotent at the load level** — on subsequent boots the v2 file is read straight through. The existing per-entry resilient parse (mithril#914 GATE-2 Fix A) carries over verbatim: a single poisoned entry doesn't drop the others, and the migration runs only on still-parseable entries.

**What this migration explicitly does not do:**
- **No best-effort lookup for v1 keys** at runtime. The migrator rewrites once at load; after that, every `TryGet`/`Save` uses the v2 key. No "try both forms" branch in the hot path.
- **No baseline.json legacy acceptance.** A #1021-aware build ships v2 baseline; nothing else ever appears.
- **No `IVersionedState<T>` / Migrate-ladder.** Internal-only persistence, single-step prefix rewrite — the full pattern is overkill.

The schemaVersion-on-individual-`AreaCalibration` field stays at 1 — only the *outer* file shape is changing, not the per-record shape (Scale/Rotation/Origin etc. are unchanged). A future change to the record itself (e.g. adding a per-scene world-coord bbox) gets its own per-record SchemaVersion bump.

### 5.4 Error handling + status surface

**One refusal point, one path** at the autocal entry: `CurrentMapAsset` null → return `AutoCalibrationOutcome(Persisted: false, RejectReason: "Map asset not yet known…")` without running detection, solver, or hitting the texture provider. No attempt bundle written; no `LoadMetrics`/`CalibrationAttemptContext` counter bumped. This is a **precondition gate**, modelled the same way `_areaState.CurrentArea` being null was previously handled (line 153's `?? string.Empty` was the implicit version — one that silently produced a downstream failure instead of an explicit refusal).

**Downstream failure modes stay in the existing fail-soft chain** — none of them change shape:

| Failure | Reached | Behaviour today | Behaviour under #1021 |
|---|---|---|---|
| Asset name correctly populated, texture not yet in sidecar cache | Live path | `IBaseTextureProvider` returns `null` → sidecar invoked → cache retry → if still null, `LogWarning + safe-degrade` | Unchanged. Sidecar receives `--asset Map_HogansKeepBasement` instead of `--area HogansKeepBasement`; rest of chain is identical. |
| Asset name OK, sidecar exits non-zero | Live | `LogWarning + safe-degrade + return null` | Unchanged. |
| Aggregator scene, no `Map_<AreaX>` bundle exists | Not reachable by construction — only emitted asset names reach this path. | n/a | n/a — `Map_<AreaX>` for aggregator parents never appears in the runtime-key bracket, so it can't end up in `CurrentMapAsset`. The earlier finding that "no `Map_AreaCave1` bundle exists" stops mattering because autocal never asks for it. |
| Solver got NPCs from the wrong sub-zone (filter bug) | Latent today | High-residual `AreaCalibration`; `CalibrationConfidenceGate` rejects on residual | Unchanged — the gate is the backstop; the new `(ParentAreaKey, SceneFriendlyName)` filter is the cause-side fix. |
| War Cache map fires `Downloading Map` line at scene-load (verification owed, unlikely) | Hypothetical | CurrentArea set to some `AreaWarCache*`; texture missing → fail-soft | CurrentMapAsset set to `Map_WarCache_Scorp1`; texture exists; NPC lookup likely returns 0 (war caches aren't visited areas) → solver returns no candidate → confidence gate rejects → safe-degrade. No special-casing. |

**Status surface** — `CalibrationStatusFormatter` + `OutcomeVocabulary` gain exactly one new entry: `MapAssetNotYetKnown` ↔ the refusal string. Surfaces verbatim in:
- Palantir → AutoCalibration → debug log (existing `Outcome` column gets a new row).
- The autocal toast (existing `IAutoCalibrationRunner.Status` event).

**Telemetry** — no new metrics. The existing `MithrilMeters.MapCalibration.Attempts` counter records each invocation; refusals via the new gate increment with a `outcome=map_asset_not_known` tag (lowercase-dotted per the canonical convention in [`docs/perf-trace-schema.md`](../../perf-trace-schema.md)). Tag declaration added to the relevant `MithrilMeters.MapCalibration` instrument's tag catalog (default-on Safe, no scrubbing concerns).

### 5.5 Testing strategy

xUnit + FluentAssertions per CLAUDE.md. Six surfaces, scoped per seam:

1. **Verb extraction** (`Mithril.Arda.Tests.Dispatch.VerbExtractorTests`): one new theory row for `"Downloading Map [hash] GUID hash for area X runtime key hash[Map_X]"` → `ParsedVerb(Verbs.DownloadingMap, args)`. Follows the `LoadingLevel` / `InitializingArea` test pattern.

2. **Handler parser** (`Arda.World.Player.Tests.Internal.MapAssetLoaderTests`):
   - Happy path with the live-verified Hogan's Basement line, byte-for-byte.
   - Friendly name with embedded apostrophe (`Hogan's Basement`) and embedded spaces (`Caves Beneath Kur Mountains`).
   - Last-bracket-wins rule: assert the `[GUID]` at args-head isn't mistakenly captured (multiple `[…]` blocks present).
   - Malformed forms (no `runtime key `, no trailing `]`, empty args, args without `for area `): silent skip, no state mutation, no event published.
   - Idempotent re-parse: same line twice → state changes once, `MapAssetChanged` fires once.
   - Transition: distinct line → both `PreviousMapAsset` and `CurrentMapAsset` populated on the event.

3. **Composite state surface** (`Arda.World.Player.Tests.Internal.MapScopeTests`): one delegation test per new property — assert `MapScope.CurrentMapAsset` reflects `MapAssetLoader.CurrentMapAsset` after a handler tick.

4. **Reference provider** (`Mithril.MapCalibration.Capture.Tests.AreaReferenceProviderTests`) — extend the existing fixture set:
   - All eight existing call-sites rewritten from `"AreaSerbule"` → `new MapSceneRef("AreaSerbule", null)` (mechanical).
   - Aggregator scene with `SceneFriendlyName` set → only NPCs matching `(AreaName, AreaFriendlyName)` are returned (fake `IReferenceDataService` seeded with two NPCs under `AreaCave1` with different `AreaFriendlyName` values).
   - Aggregator scene with non-matching `SceneFriendlyName` → empty NPC list; landmarks (no sub-zone field) still flow from the parent-area filter — knowingly over-broad, asserted as the documented behaviour.
   - `SceneFriendlyName: null` collapses to the existing area-only filter (back-compat assertion).

5. **Autocal gate** (`Mithril.MapCalibration.Capture.Tests.AutoCalibrationEngineTests`):
   - `CurrentMapAsset == null` → outcome is `Persisted: false`, `RejectReason: "Map asset not yet known…"`; fake `IBaseTextureProvider` / fake solver never invoked (call-count zero).
   - `CurrentMapAsset` set → fake `IBaseTextureProvider` receives the literal `Map_HogansKeepBasement` string; fake `IAreaReferenceProvider` receives `MapSceneRef(ParentAreaKey: "AreaCave1", SceneFriendlyName: "Hogan's Basement")`.
   - Existing test fakes (`EngineFakes.FakeAreaRefs`) update their `ForArea` signature to take `MapSceneRef`; `FakeMapState` / fixture wires the three new fields.

6. **Persistence migration** (`Mithril.MapCalibration.Tests.Internal.UserRefinementStoreMigrationTests`):
   - v1 file (no `schemaVersion`, bare keys) → load → keys become `Map_<X>` → file rewritten with `schemaVersion: 2`.
   - v2 file → load is a no-op rewrite; file unchanged byte-for-byte on disk.
   - v1 file with a defensive already-prefixed entry → not double-prefixed.
   - v1 file where `Persist` throws mid-migration → rolled back; on-disk file untouched (existing transactional pattern).
   - Empty / malformed JSON → no migration attempted, store loads empty without throwing.

7. **Bundled-baseline snapshot** (`Mithril.MapCalibration.Tests.Internal.BundledBaselineLoaderTests`): extend the existing fixture — assert `schemaVersion: 2` and the three anchor keys are `Map_Area…`. Catches an accidental hand-edit of `map-calibration-baseline.json` that forgets to bump the version or rename a key.

8. **Golden-fixture extension** (`tests/Mithril.Shared.Tests/Logging/Fixtures/per-rule/asset-loader-noise.log`): add a `Downloading Map` block (live-capture text, verbatim) alongside the existing `UnloadTime` / `Completed load` noise. The existing per-rule fixture test asserts classification; extending it surfaces a regression if the verb-extractor or rule pattern drifts.

9. **Replay-drain ordering** (a new test in `MapAssetLoaderTests` since this is single-source, not cross-source): feed a replay buffer with three `Downloading Map` lines (zone A → B → A) and assert `CurrentMapAsset` reflects the last one after drain completes. The replay-vs-live distinction the `IPlayerLogStream` memory flags applies symmetrically — this handler subscribes via the standard registration path, so late subscribers pay the drain cost but get the right end-state.

**Not in scope:**
- No end-to-end test through the real sidecar process (existing `ProcessAssetExtractor` tests cover CLI-arg construction; renaming `--area` to `--asset` is a one-line assertion edit).
- No War-Cache-specific tests (out of scope per §3 / §7; fail-soft behaviour already covered by the empty-refs case in surface 5).

## 6. Files touched (anticipated)

Production code (~16 files):

- `src/Arda/Arda.Dispatch/Verbs.cs` — add `DownloadingMap` const.
- `src/Arda/Arda.Dispatch/VerbExtractor.cs` — add prefix branch.
- `src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs` — NEW.
- `src/Arda/Arda.Contracts/State/Player/IMapState.cs` — extend with 3 properties.
- `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs` — NEW handler.
- `src/Arda/Arda.World.Player/Internal/MapScope.cs` — add 5th composed handler + 3 delegations.
- `src/Arda/Arda.World.Player/PlayerWorldExtensions.cs` — DI registration.
- `src/Mithril.MapCalibration/MapSceneRef.cs` — NEW record struct.
- `src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs` — signature change.
- `src/Mithril.MapCalibration.Capture/ReferenceDataAreaReferenceProvider.cs` — composite-key filter.
- `src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs` — param rename + doc. (Moved out of `Mithril.MapCalibration` by #1028.)
- `src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs` — param rename (only).
- `src/Mithril.MapCalibration/IAssetExtractor.cs` — `ExtractRequest.AreaKey` → `MapAssetName`. (Stayed in `Mithril.MapCalibration` per #1028's split — the contract is BCL-only.)
- `src/Mithril.MapCalibration.Detection/Internal/ProcessAssetExtractor.cs` — CLI flag `--area` → `--asset`. (Moved into `Mithril.MapCalibration.Detection.Internal` by #1028.)
- `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` — DI swap + gate + `MapSceneRef` build.
- `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs` + `OutcomeVocabulary.cs` — new `MapAssetNotYetKnown` entry.
- `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` — v1→v2 migrator in `Load`.
- `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` — add the v2 wrapper type if not already covered by `JsonDocument` walk.
- `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` — hand-edit.
- `tools/Mithril.AssetExtractor/Program.cs` — `--area` → `--asset` flag.
- `tools/Mithril.AssetExtractor/README.md` — flag rename.

Test code (~7 files):

- `tests/Mithril.Arda.Tests/Dispatch/VerbExtractorTests.cs` — new theory row.
- `tests/Arda.World.Player.Tests/Internal/MapAssetLoaderTests.cs` — NEW.
- `tests/Arda.World.Player.Tests/Internal/MapScopeTests.cs` — three delegation rows.
- `tests/Mithril.MapCalibration.Capture.Tests/AreaReferenceProviderTests.cs` — extend.
- `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` — gate + plumbed-key.
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` — `FakeAreaRefs` + `FakeMapState`.
- `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs` — NEW.
- `tests/Mithril.MapCalibration.Tests/Internal/BundledBaselineLoaderTests.cs` — extend.
- `tests/Mithril.Shared.Tests/Logging/Fixtures/per-rule/asset-loader-noise.log` — extend fixture.

## 7. Out of scope

- **War Cache maps** (16 `Map_WarCache_<Region><N>`) — quest-issued treasure-map *items*, not in-game scenes. If verification reveals one does fire a scene-load `Downloading Map` line, it falls through the existing fail-soft path (no NPC references → confidence gate rejects → safe-degrade). No special-casing here.
- **Aggregator-scene landmark scoping.** Landmarks.json has no per-NPC-style sub-zone field, so for aggregator scenes the landmark filter stays parent-area-wide (mixing sub-scenes). The solver's RANSAC tolerates this; a follow-up issue can add a per-scene world-coord bbox if a specific aggregator scene proves under-served by landmark coverage.
- **Sub-zone → aggregator mapping table.** A complete `Map_<X>` ↔ `AreaX` ↔ `AreaFriendlyName` triple isn't computable from bundled data alone (npcs.json gives partial coverage — 6 aggregators, 16 sub-zones — but NPC-less scenes are invisible). For #1021, the mapping is *consumed* runtime-by-runtime (each scene's pair arrives in its own log line); no bundled lookup table is built.
- **#914 engine plan changes.** This work lands as a standalone PR against the existing autocal pipeline. The downstream engine umbrella (#914) consumes the new state without itself needing to be in flight when this lands.
- **WPF manual-calibration tools.** `tools/MapCalibrationWpf/` and `tools/MapCalibrationFromScreenshot/` are research/legacy paths. The `IAreaReferenceProvider` interface change affects them at compile time (they consume `NpcsReader` / `LandmarksReader` directly via the `Mithril.MapCalibration.Tools.Common` package, NOT `IAreaReferenceProvider`), so they don't break — but they also don't gain the sub-zone-aware behaviour. If a user runs the legacy harness against an aggregator-area scene, the existing "couldn't find some maps" symptom persists. Deferred per D8 — these tools are slated for retirement, not for repair.

## 8. Verification owed

- **War Cache `Downloading Map` emission.** Confirm whether any of the 16 `Map_WarCache_*` bundles ever produce a `Downloading Map ... for area <X> runtime key ...[Map_WarCache_*]` line at scene-load (as opposed to only being loaded as an inventory-item image). Capture-required; not blocking this issue.
- **Replay timing edge.** For a player who has been in the same zone since before the current Player.log rotated, no `Downloading Map` line appears in the file replay. The strict gate (§5.4) handles this — autocal refuses with the *change zones once* hint. Worth confirming once in a real session that the hint actually unblocks the user (a single zone-change in PG does fire a fresh `Downloading Map` for the new zone — verified — but the case of *return to the same zone* may or may not re-emit; needs a session-level capture).
- **Minimap vs M-map gating.** Whether `Downloading Map` fires when only the corner minimap renders (always-on) vs only on full M-map open. Affects whether the signal can be repurposed as a "user opened the map" intent marker by a different consumer; not load-bearing here.
- **GUID stability across patches.** The `[<GUID>]` Addressables hash on each `Downloading Map` line may shift on patch. We don't persist it, but a future feature that wants to validate "the texture I solved against is still the same bytes" would want to know.

## 9. Cross-references

- Issue: [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021) — full discussion thread, including the brainstorm log captured as comments.
- Wiki (canonical grammar + inventory): [Player-Log-Signals → Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures).
- Memory pointer: `pg_map_asset_load_log_grammar` (user memory, not repo-resident).
- Downstream: [mithril#914](https://github.com/moumantai-gg/mithril/issues/914) — map auto-calibration engine umbrella (consumes the new state once landed).
- Related (closed): [mithril#966](https://github.com/moumantai-gg/mithril/issues/966) — `Refine` perf stall fix; did not touch keying.
- Existing infra: [`docs/perf-trace-schema.md`](../../perf-trace-schema.md) — telemetry tag conventions for the new `outcome=map_asset_not_known` metric tag.
- Architecture context: [`docs/cross-source-correlation.md`](../../cross-source-correlation.md) — note: this work is *single-source* (Player.log only), so the Tier-N decision tree doesn't apply.
