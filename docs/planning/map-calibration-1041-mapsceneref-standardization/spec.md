# Spec — `MapSceneRef` standardization + consumer migration (mithril#1041)

**Tracked in:** [mithril#1041](https://github.com/moumantai-gg/mithril/issues/1041).
**Brainstormed:** 2026-06-03 with @arthur-conde; decisions captured below in §3.
**Upstream:** [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021) (PR [#1040](https://github.com/moumantai-gg/mithril/pull/1040)) shipped per-scene calibration keying — baseline.json + `UserRefinementStore` migrated to `Map_<X>` Unity asset keys, `AutoCalibrationEngine` switched to `IMapState.CurrentMapAsset`. This work is the follow-up consumer migration: every remaining bare-area-key lookup gets retyped, and `MapSceneRef` is promoted from a `IAreaReferenceProvider` projection identifier into the universal calibration identity.
**Canonical references:**
- Spec: [`docs/planning/map-calibration-1021-per-scene-keying/spec.md`](../map-calibration-1021-per-scene-keying/spec.md) — D1–D8 ratified upstream; this spec does not re-litigate them.
- Wiki: [Player-Log-Signals → Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures) — log-line grammar.
- Memory: `pg_map_asset_load_log_grammar` — pointer.

## 1. Problem

#1040 (the #1021 cutover) migrated the **persistence + autocal engine** to per-scene `Map_<X>` keying. But three classes of consumer were left on bare-area-key lookups against `IMapCalibrationService`, because they sit outside the autocal/sidecar/persistence seam touched by the spec's anticipated-files list:

| Site | Source of bare key | Failure mode |
|---|---|---|
| [`Mithril.Overlay.OverlayWindowService`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs) — the WPF map-overlay renderer | `_areaState.CurrentArea` directly ([:276](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:276)) | 4 calls per frame: `IsCalibrated`/`WorldToWindow` ([:298](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:298), [:408](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:408), [:455](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:455), [:645](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:645)) miss against the now-`Map_<X>`-keyed store → uncalibrated render |
| [`Mithril.MapCalibration.Capture.AutoCalibrationTrigger`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs:126) | `AreaChanged.CurrentArea` | `_calibrationService.GetCalibration(area)` misses → skip-if-already-calibrated check fails → re-attempts autocal every zone-in to Serbule until a refinement lands under the bare key (which post-fix it never will) |
| [`Legolas.AreaCalibrationService`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs) — 4 sites at [:140](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:140), [:143](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:143), [:170](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:170), [:178-181](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:178) | `CurrentAreaKey` field (areas.json key) | Renderer projector applies stale/missing calibration; `OnMapCalChanged` equality check at `:180` compares engine-emitted `Map_<X>` against bare `CurrentAreaKey` → drops every change event |

### 1.1 Headline regression — existing `AreaSerbule` / `AreaEltibule` / `AreaKurMountains` users

`MapCalibrationService.GetCalibration(key)` is an exact-key dictionary lookup against `_baseline` (now `Map_<X>` keyed) and `_userStore` (migrated to `Map_<X>` on first v2 load by the migrator at [`UserRefinementStore.cs:Load`](../../../src/Mithril.MapCalibration/Internal/UserRefinementStore.cs:164)). When `OverlayWindowService` queries by bare `AreaSerbule`, both stores miss, and the renderer falls through to the "no calibration available" path. A user happily rendering against the Serbule baseline pre-#1040 sees uncalibrated rendering post-merge until autocal re-runs.

Autocal does recover on re-zone (it now writes under `Map_<X>` per `2f24c2b2`), so the regression window self-heals for autocal-running users. But:
- Cold-start render of an existing calibration (baseline or migrated user refinement) is broken until autocal re-runs.
- The auto-trigger's "skip if already calibrated" check misses, causing redundant autocal attempts on every zone-in to Serbule.
- For aggregator areas (`AreaCave1` etc.) where no `Map_<AreaX>` baseline exists, autocal can't recover either (Hogan's Basement has no shipped baseline) — but the per-scene asset name *can* be tracked through the cache (§5.3) for future calibrations.

### 1.2 Structural smell — three primitive strings flying together

Today, scene identity is split across three loose `string` fields on `IMapState` (`CurrentMapAsset`, `CurrentSceneFriendlyName`, plus the parent `CurrentArea`) and the analogous three on `MapAssetChanged`. `IMapCalibrationService` methods take a bare `string areaKey`. Every consumer has to remember *which* string to pass (asset key for the lookup, parent area for the picker), and the wrong string compiles fine. The type system isn't helping.

`MapSceneRef` already exists in `Mithril.MapCalibration` (added by #1040 for [`IAreaReferenceProvider.ForArea`](../../../src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs)), but it only carries two of the three fields (`ParentAreaKey`, `SceneFriendlyName?`). It's the obvious composite — it just needs the third field and a status promotion.

## 2. Goal

Promote `MapSceneRef` from "projection-time NPC-scope identifier" to **the universal calibration identity south of `IMapState`**. Retype every consumer of `IMapCalibrationService` to take a typed `MapSceneRef` parameter. Replace the three-primitive-string state on `IMapState` and `MapAssetChanged` with the one composite. Subscribe `Mithril.Overlay` and `Legolas.PlayerLogIngestionService` to `MapAssetChanged` so per-scene transitions inside aggregator areas drive renderer state directly.

Adjacent to that:

- Introduce a persisted `SceneAssetCache` that learns `(ParentAreaKey, SceneFriendlyName?) → MapAssetKey` from observed `MapAssetChanged` events and is pre-seeded at startup from the `baseline.json ∩ areas.json` intersection. Cold-start `OverlayWindowService` resolution (when `IMapState.CurrentMapScene == null` but `CurrentArea` is known) falls back to the cache before tripping the strict gate.
- Retire the #836 legacy parity loop: `LegolasAreaCalibrationMigration`, the `LegolasSettings.AreaCalibrations` dual-write/clear in `AreaCalibrationService`, and `IMapCalibrationService.ImportUserRefinements` are all deleted. `LegolasSettings.AreaCalibrations` itself stays one cycle marked `[Obsolete]` so on-disk legacy data isn't dropped from `LegolasSettings.json` mid-cycle. There is now **one write path** for any solved calibration: `SaveUserRefinement(MapSceneRef, AreaCalibration) → refinements.json`. Manual and auto solves share it (auto already did post-#914 PR-2; this PR retires the manual-side parity scaffold the auto path never used).

## 3. Ratified design decisions

Each row was litigated during the 2026-06-03 brainstorm; option letters reference alternatives surfaced at that time. **No "Other" / open-ended outcomes** — every decision is closed.

| # | Decision | Choice | Rationale |
|---|---|---|---|
| **D1** | How to disentangle "calibration lookup key" from "parent area key" inside `AreaCalibrationService` | `MapSceneRef` everywhere: extend the existing record with `MapAssetKey`, retype `IMapCalibrationService.*(string)` to take `MapSceneRef`, retype `IMapState.CurrentMapAsset` / `MapAssetChanged` payload to the composite. | Type-driven. Eliminates the "did I pass area or asset?" footgun at every call site. The composite is constructible only at the one site that has all three (the `MapAssetLoader` parser), so the rest of the system can't accidentally invent one. |
| **D2** | `IMapCalibrationService.Changed` event payload | `EventHandler<MapSceneRef>` (was `EventHandler<string>`). No `AreaCalibration?` payload addition. | Symmetric with retyped inbound parameters; the writer has the `MapSceneRef` in hand at every raise site (after D6 deletes `ImportUserRefinements`, all writers are `Save*`/`Clear*`). The single subscriber ([`AreaCalibrationService.OnMapCalChanged`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:178)) re-calls `GetCalibration` anyway, so bundling the `AreaCalibration?` into the payload is YAGNI. |
| **D3** | Cold-start renderer resolution when `IMapState.CurrentMapScene == null` | `SceneAssetCache` resolution helper: live `CurrentMapScene` → live; else cache hit on `(CurrentArea, null)` → synthesized `MapSceneRef`; else strict gate (uncalibrated chip). Cache pre-seeded from `baseline.json ∩ areas.json` intersection. | Live observation is the empirical authority; cache is the seeded-and-learned fallback (no "best-effort guess by prefix"); strict gate is the floor. Q3 evidence: the `merged-corpus.log` fixture captures one `Initializing area!` without a corresponding `Downloading Map`, so the strict-gate-without-cache cell is non-empty in practice. The cache makes hub-area cold-starts work on first launch. |
| **D4** | Renderer reaction to per-scene transitions | `OverlayWindowService` subscribes to `MapAssetChanged` directly via `IDomainEventSubscriber`, marshals to the WPF dispatcher, invalidates the next frame. | Sub-zone walks inside aggregator areas (motherlode hunts in caves) need scene-grain updates; without the subscription the renderer would wait for the next `OnSurfaceRender` tick to notice a fresh state read. |
| **D5** | `Legolas.PlayerLogIngestionService` area subscription | Drop the `AreaChanged` subscription. Subscribe to `MapAssetChanged` only. `SelectArea(string)` becomes `SelectScene(MapSceneRef)`. | Every `MapAssetChanged` carries the parent area name on the same log line, so the per-scene event is strictly-more-informative. The only loss is an `Initializing area!` without a subsequent `Downloading Map` — which is exactly the cold-start cell D3's cache + strict gate already handle. |
| **D6** | `LegolasSettings.AreaCalibrations` dual-write / legacy import (`#836` parity scaffold) | Retire all three: delete `LegolasAreaCalibrationMigration`, delete `IMapCalibrationService.ImportUserRefinements` + `UserRefinementStore.ImportFromLegacy`, delete the dual-write at `AreaCalibrationService.cs:223-225` and the dual-clear at `:263-264`. The field itself stays `[Obsolete]` for one release cycle so on-disk `LegolasSettings.json` data isn't dropped from existing installs; a follow-up PR removes the field after the cycle. | The model justification for treating manual fits as special (the ±10% affine ceiling, memory `legolas_calibration_findings`) has been ruled out — manual and auto solves produce identical-shape similarity transforms and already share `UserRefinementStore`. The parity loop is now ceremony. Every release since #836 has run the import; users with pre-lift `AreaCalibrations` entries already have them carried into the new store. The risk class (user installed pre-lift, skipped every release with the migration, lands directly on #1041) is approximately zero; `[Obsolete]` field preserves on-disk data either way for the next-cycle window. |
| **D7** | `UserRefinement*` naming (the API is misleading — both manual + auto solves persist through it) | **Defer** to a follow-up issue. Rename `SaveUserRefinement` → `SaveSolvedCalibration` and `UserRefinementStore` → `SolvedCalibrationStore` in a separate PR. | #1041 already changes ~15 file signatures + retires 5 paths. Bundling a ~10-file mechanical rename adds reviewer fatigue without changing semantics. |
| **D8** | Wizard sub-zone-aware area picker (consumes the new `SceneAssetCache`) | **Defer** to a follow-up issue. `CalibrationSessionViewModel`'s area picker stays `IReferenceDataService.Areas`-only in #1041. | The cache itself is the structural unlock; offering visited sub-zones in the picker is the UX consumer. Splitting keeps the #1041 PR contained. |
| **D9** | Single-PR landing vs. overload-and-evolve over multiple PRs | Single atomic PR with logically-ordered file groups in the diff. | The interface change concentrates in one type (`IMapCalibrationService`) + one event payload + ~15 call sites. Overload-and-evolve would multiply churn against a single 1-week review window. Plan-side TDD tasks may not produce buildable intermediate states; final-state test suite is the gate. |

## 4. Architecture overview

```
Player.log "Downloading Map ... runtime key ...[Map_<X>]"
       │
       ▼
Arda.World.Player.MapAssetLoader (already ships; #1021)
       │
       ▼   produces MapSceneRef(ParentArea, SceneFriendlyName?, MapAssetKey)
Arda.Contracts.State.Player.IMapState
       │      CHANGE: drop CurrentMapAsset / CurrentSceneFriendlyName / MapAssetMeasuredAt
       │      ADD:    CurrentMapScene : MapSceneRef?
       │              MapSceneMeasuredAt : DateTimeOffset?
       │
       ▼
Arda.World.Player.Events.MapAssetChanged
       │      CHANGE: (MapSceneRef? PreviousScene, MapSceneRef? CurrentScene, Metadata)
       │
       ├──────────────────────────────────────────────────────────┐
       │                                                          │
       │                            +++ NEW: SceneAssetCache +++  │
       │                  Subscribes to MapAssetChanged, records
       │                  (ParentArea, SceneFriendlyName?) → MapAssetKey.
       │                  Pre-seeded at startup from baseline.json ∩
       │                  areas.json (12 directly-registered areas).
       │                  Persisted to scene-asset-cache.json.
       │                                                          │
       ▼─────────┬────────────────────────────┬───────────────────┴──────┐
                 │                            │                          │
                 ▼                            ▼                          ▼
   Legolas.PlayerLogIngestionService    Mithril.Overlay.            Mithril.MapCalibration.Capture.
   (drives Legolas state)               OverlayWindowService        AutoCalibrationTrigger
   • subscribes MapAssetChanged         (the WPF renderer)           • subscribes MapAssetChanged
   • calls SelectScene(MapSceneRef)     • reads IMapState per frame    (alongside AreaChanged for
   • DROPS AreaChanged subscription     • subscribes MapAssetChanged    the zone-in heuristic)
   • cold-start: resolve via cache        for snappy re-render flip   • gate uses MapSceneRef
                                        • cold-start: resolve via cache  resolution
                 │                            │                          │
                 └─────────────┬──────────────┴──────────────────────────┘
                               │
                               ▼   all consumers pass MapSceneRef, never bare strings
                  Mithril.MapCalibration.IMapCalibrationService
                  • GetCalibration(MapSceneRef) / IsCalibrated(MapSceneRef)
                  • WorldToWindow(MapSceneRef, world, zoom)
                  • WindowToWorld(MapSceneRef, pixel, zoom)
                  • SaveUserRefinement(MapSceneRef, AreaCalibration)
                  • ClearUserRefinement(MapSceneRef)
                  • Changed: EventHandler<MapSceneRef> (payload = scene only; YAGNI)
                  • AllCalibrations: IReadOnlyDict<string, AreaCalibration>
                      (asset-keyed — persistence horizon, unchanged)
                  • ImportUserRefinements: DELETED (D6)
                               │
                               ▼   (unchanged — persistence is asset-keyed by string)
                  UserRefinementStore / map-calibration-baseline.json

Retired this PR (D6):
  ✗ LegolasAreaCalibrationMigration host service                  (delete entire file)
  ✗ LegolasSettings.AreaCalibrations dual-write/clear              (delete writer half;
                                                                    field stays [Obsolete])
  ✗ IMapCalibrationService.ImportUserRefinements                   (delete API)
  ✗ UserRefinementStore.ImportFromLegacy                           (delete impl)
  ✗ Legolas.PlayerLogIngestionService.OnAreaChanged + _lastArea    (replaced by
                                                                    OnMapAssetChanged + _lastScene)

Deferred to follow-up issues (D7, D8):
  → Wizard sub-zone-aware manual area picker (consumes SceneAssetCache)
  → UserRefinement* → Solved* rename across API + store + tests
```

### 4.1 The resolution cascade

One helper, three call sites. Pure function, no side effects:

```csharp
internal static MapSceneRef? ResolveCurrentScene(IMapState state, ISceneAssetCache cache)
{
    if (state.CurrentMapScene is { } live) return live;               // (a) live truth
    if (state.CurrentArea is { Length: > 0 } area &&
        cache.TryResolve(area, sceneFriendlyName: null) is { } cached)
        return cached;                                                 // (b) seeded or learned
    return null;                                                       // (c) strict gate
}
```

Branch (a) wins over (b) when both could fire — observation is authoritative, so a `Downloading Map` line that emits a fresh `MapAssetKey` for a `(parent, friendly)` the cache already knew under a stale `MapAssetKey` overwrites the cache via the `Record` write-through. This is the recovery path if PG renames an asset across patches.

Consumers:
- `OverlayWindowService.OnSurfaceRender` (per frame)
- `AutoCalibrationTrigger.OnAreaChangedAsync` (gate check)
- `AreaCalibrationService.SetCurrentScene` (cold-start bootstrap + on `MapAssetChanged`)

## 5. Layer-by-layer detail

### 5.1 Arda layer

**`MapSceneRef`** ([`src/Mithril.MapCalibration/MapSceneRef.cs`](../../../src/Mithril.MapCalibration/MapSceneRef.cs)) — extend the record:

```csharp
public readonly record struct MapSceneRef(
    string ParentAreaKey,            // "AreaCave1" — areas.json key
    string? SceneFriendlyName,       // "Hogan's Basement" — null for directly-registered areas
    string MapAssetKey);             // "Map_HogansKeepBasement" — literal Unity Texture2D name;
                                     // the calibration store key everywhere south of IMapState
```

Doc comment updated to call out the third field's role + the live-wins-over-cache invariant.

**`IMapState`** ([`src/Arda/Arda.Contracts/State/Player/IMapState.cs`](../../../src/Arda/Arda.Contracts/State/Player/IMapState.cs)) — remove the three `CurrentMapAsset` / `CurrentSceneFriendlyName` / `MapAssetMeasuredAt` properties, add:

```csharp
/// <summary>Composite map-scene identity (parent area + sub-zone friendly name + Unity asset key),
/// or <c>null</c> until the first <c>Downloading Map</c> line is observed this session.
/// Live truth — preferred over <see cref="Mithril.MapCalibration.ISceneAssetCache"/> resolution.</summary>
MapSceneRef? CurrentMapScene { get; }

/// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
DateTimeOffset? MapSceneMeasuredAt { get; }
```

**`MapAssetChanged`** ([`src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs`](../../../src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs)) — reshape payload:

```csharp
public readonly record struct MapAssetChanged(
    MapSceneRef? PreviousScene,
    MapSceneRef? CurrentScene,
    LogLineMetadata Metadata);
```

Subscribers can diff fields via record equality / `with`.

**`MapAssetLoader`** ([`src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`](../../../src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs)) — internal state collapses to `MapSceneRef? _currentScene`. Parser builds the composite atomically. Sub-zone-only transitions inside the same parent area use the `with`-expression: `_currentScene = _currentScene with { MapAssetKey = parsedAsset, SceneFriendlyName = parsedFriendly }`. Malformed lines still silently skip per existing pattern.

**`MapScope`** ([`src/Arda/Arda.World.Player/Internal/MapScope.cs`](../../../src/Arda/Arda.World.Player/Internal/MapScope.cs)) — delegations swap: `public MapSceneRef? CurrentMapScene => mapAsset.CurrentMapScene;` and the timestamp delegation.

**DI registration** ([`src/Arda/Arda.World.Player/PlayerWorldExtensions.cs`](../../../src/Arda/Arda.World.Player/PlayerWorldExtensions.cs)) — unchanged; `MapAssetLoader` is already registered against `Verbs.DownloadingMap`.

### 5.2 Calibration core layer

**`IMapCalibrationService`** ([`src/Mithril.MapCalibration/IMapCalibrationService.cs`](../../../src/Mithril.MapCalibration/IMapCalibrationService.cs)) — every method that took `string areaKey` now takes `MapSceneRef scene`. `Changed` event retypes to `EventHandler<MapSceneRef>?`. `ImportUserRefinements` is deleted entirely (D6). `AllCalibrations` stays `IReadOnlyDictionary<string, AreaCalibration>` — keys are asset-key strings, honestly reflecting the persistence horizon. The xmldoc on `AllCalibrations` is updated to point at `ISceneAssetCache` for parent-area resolution.

**`MapCalibrationService`** ([`src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationService.cs)) — each method extracts `scene.MapAssetKey` for the inner dictionary lookup. The store backing (`_userStore`, `_baseline`) stays `string`-keyed by asset key. `ImportUserRefinements` impl + xmldoc references removed.

**`UserRefinementStore`** ([`src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`](../../../src/Mithril.MapCalibration/Internal/UserRefinementStore.cs)) — `ImportFromLegacy` method deleted. The v1→v2 migrator at `Load()` stays verbatim — users still on v1 files at first #1041 boot still get migrated.

### 5.3 `SceneAssetCache` (new)

Four new files in `Mithril.MapCalibration`:

**`SceneAssetCache.cs`** — public service. In-memory `Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry>` where:
- `SceneAssetCacheKey = record (string ParentAreaKey, string? SceneFriendlyName)` with `StringComparer.Ordinal` semantics.
- `SceneAssetCacheEntry = record (string MapAssetKey, DateTimeOffset LastObservedAt)`.
- Public surface:
  - `MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName)` — composite-key strict lookup.
  - `void Record(MapSceneRef scene, DateTimeOffset observedAt)` — write-through, advances `LastObservedAt`; live observation wins over seeded entries; persists transactionally.
  - `event EventHandler<MapSceneRef>? Recorded` — optional, surfaced for debug surfaces only; not consumed by the three primary consumers.

**`Internal/SceneAssetCacheStore.cs`** — persistence wrapper mirroring `UserRefinementStore`'s shape:
- File: `%LocalAppData%/Mithril/MapCalibration/scene-asset-cache.json`.
- Schema version 1.
- Atomic temp-file + rename on persist; transactional rollback on `IOException` (in-memory restored to pre-write snapshot).
- Per-entry resilient parse on load — poisoned entries logged + skipped, others survive; whole-file structural failure → start empty + warn (mirrors [`UserRefinementStore.Load`](../../../src/Mithril.MapCalibration/Internal/UserRefinementStore.cs:164)'s pattern).

**`Internal/SceneAssetCacheSeeder.cs`** — one-shot startup helper. Walks bundled-baseline keys; for each `"Map_<X>"` checks if `X ∈ IReferenceDataService.Areas`. If yes, adds `(X, null) → "Map_X"` to the cache via `Record` with `lastObservedAt = DateTimeOffset.MinValue` (so any real observation wins). Idempotent — re-seeding a present entry that came from observation is a no-op (timestamp-based tiebreaker). With current shipped baseline (`Map_AreaSerbule`, `Map_AreaEltibule`, `Map_AreaKurMountains`), exactly 3 entries seed.

**`Internal/SceneAssetCacheRecorder.cs`** — `IHostedService` that subscribes to `MapAssetChanged` via `IDomainEventSubscriber` and calls `SceneAssetCache.Record(currentScene, metadata.Timestamp)` on every event. `CurrentScene == null` events are dropped without persisting. Replay metadata is honoured the same as live — the file replay is the cheapest learning signal we have; recording during replay populates the cache for cold-start resolution on first-boot.

**DI wiring** ([`src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`](../../../src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs)) — register `SceneAssetCache` (singleton), `SceneAssetCacheStore` (singleton), `SceneAssetCacheRecorder` (`IHostedService`). Seeder runs synchronously in `MapCalibrationService` construction (or in a small bootstrap helper invoked from the extension).

**JSON context** ([`src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`](../../../src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs)) — add `SceneAssetCacheFile` + entry record source-generated wrappers.

### 5.4 Calibration consumers

**`AutoCalibrationEngine`** ([`src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs)) — reads `IMapState.CurrentMapScene` directly instead of constructing a `MapSceneRef` from three loose fields. `GetCalibration(scene)` / `SaveUserRefinement(scene, ...)` take the typed composite. The strict-gate refusal at `TryCalibrateCurrentAreaAsync` reads `_mapState.CurrentMapScene` and falls back to `ResolveCurrentScene(_mapState, _sceneCache)` for the cache path before refusing.

**`AutoCalibrationTrigger`** ([`src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs`](../../../src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs)) — inject `IMapState` + `SceneAssetCache`. The existing `AreaChanged` subscription stays (the zone-in heuristic is genuinely area-shaped for the cross-area case). Also subscribes to `MapAssetChanged` so sub-zone transitions inside an aggregator area trigger a fresh attempt (today only `AreaChanged` fires, missing all sub-zone transitions entirely — Hogan's Basement → Goblin Dungeon within `AreaCave1` never triggered autocal pre-#1041). The `_persistedAreas` set is rekeyed to `_persistedScenes` (indexed on `MapAssetKey`), preserving its "scenes already attempted this session" role — a new scene isn't in the set yet, so the skip-check correctly allows the first attempt; the converged-by-MapAssetKey skip prevents re-attempts on sub-zone re-entry. The skip-if-already-calibrated check uses `ResolveCurrentScene` and passes the resolved `MapSceneRef` to `GetCalibration`.

**`IAreaReferenceProvider`** ([`src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs`](../../../src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs)) — already takes `MapSceneRef` post-#1040. No shape change. The new `MapAssetKey` field is along for the ride; `ReferenceDataAreaReferenceProvider` ignores it (NPC filter scope only uses `ParentAreaKey` + `SceneFriendlyName`). xmldoc updated to note the third field is calibration-identity, not provider-scope.

**`CalibrationStatusFormatter` + `OutcomeVocabulary`** — no change. The `MapAssetNotYetKnown` outcome from #1040 stays; the cache fallback just makes it less frequent.

### 5.5 Renderer (`Mithril.Overlay`)

**`OverlayWindowService`** ([`src/Mithril.Overlay/Internal/OverlayWindowService.cs`](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs)) — the headline fix:

- Constructor: inject `IMapState` and `SceneAssetCache` (alongside existing `IMapCalibrationService`).
- `OnSurfaceRender` ([:270+](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:270)): call `ResolveCurrentScene(_mapState, _sceneCache)` once per frame, capture the resolved `MapSceneRef?` into a local. All four bare `_areaState.CurrentArea`-derived lookups ([:298, :408, :455, :645](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:276)) consume the resolved scene instead.
- The inner `OverlaySceneContext.Project` ([:638-647](../../../src/Mithril.Overlay/Internal/OverlayWindowService.cs:638)) captures the resolved scene into its `BeginFrame` snapshot, so scene-drawer dispatch sees a stable per-frame value.
- New: subscribe to `MapAssetChanged` via `IDomainEventSubscriber` in `Initialize`. The handler marshals to the WPF dispatcher and invalidates the next frame so per-scene transitions don't wait for a zoom/marker tick to be visible.
- The "uncalibrated" chip + scene-drawer loop preserve the dissolved-#868 invariant from #872/#887 verbatim (scene drawers self-gate; pixel-native passes like calibration placement pins must render in uncalibrated areas).
- Telemetry tag `area` on `MithrilMeters.Overlay.ProjectionLatencyMs` etc. retypes to carry `MapAssetKey` (still string-shaped; just an honest naming clarification). No new instruments.

**`IOverlaySceneContext`** ([`src/Mithril.Overlay/IOverlaySceneContext.cs`](../../../src/Mithril.Overlay/IOverlaySceneContext.cs)) — `CurrentAreaKey` property renamed to `CurrentMapAssetKey` (string-typed; it's the asset key scene drawers index by). Doc updated to clarify it's the asset string, not the parent area.

**`IWorldOverlayMarkers`** ([`src/Mithril.Overlay/IWorldOverlayMarkers.cs`](../../../src/Mithril.Overlay/IWorldOverlayMarkers.cs)) — `CurrentArea` setter renamed correspondingly.

### 5.6 Legolas layer

**`IAreaCalibrationService`** ([`src/Legolas.Module/Services/AreaCalibrationService.cs`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs)) — interface surface:
- `CurrentAreaKey : string?` → `CurrentScene : MapSceneRef?`.
- `CurrentAreaFriendlyName : string?` stays — it's the parent area FriendlyName, a separate concept used by the wizard.
- `SelectArea(string)` → `SelectScene(MapSceneRef)`. The manual-picker path still feeds via a wrapper that constructs a `MapSceneRef(AreaEntry.Key, null, "Map_" + AreaEntry.Key)` for the 12 directly-registered areas (the picker is areas.json-shaped; the wrapper is a one-line bridge).

**`AreaCalibrationService`** impl changes:
- `OnMapCalChanged(object?, MapSceneRef payload)` compares `payload.MapAssetKey == _currentScene?.MapAssetKey` (was `string.Equals(areaKey, CurrentAreaKey, Ordinal)`). This fixes the bug at [`:180`](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:180) that drops every change event today.
- `CalibrateCurrentArea` writes through to `_mapCal.SaveUserRefinement(_currentScene, calibration)` only. The dual-write to `_settings.AreaCalibrations[key]` + `_saver.Touch()` at [:223-225](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:223) is **deleted** (D6).
- `ClearCurrentAreaCalibration` writes through to `_mapCal.ClearUserRefinement(_currentScene)` only. The dual-clear at [:263-264](../../../src/Legolas.Module/Services/AreaCalibrationService.cs:263) is **deleted**.
- Cold-start bootstrap in the constructor (or first call) consults `ResolveCurrentScene` to populate `_currentScene` from `IMapState` + cache if available.

**`PlayerLogIngestionService`** ([`src/Legolas.Module/Services/PlayerLogIngestionService.cs`](../../../src/Legolas.Module/Services/PlayerLogIngestionService.cs)):
- Drop `IAreaState` injection.
- Drop `_areaChangedSub` + `OnAreaChanged` + `_lastArea` dedup.
- Add `_mapAssetChangedSub` + `OnMapAssetChanged(MapAssetChanged evt)` + `_lastScene` dedup (compare `evt.CurrentScene?.MapAssetKey != _lastScene?.MapAssetKey`).
- `OnMapAssetChanged` calls `_areaCalibration.SelectScene(evt.CurrentScene)` when non-null.

**`LegolasAreaCalibrationMigration`** ([`src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs`](../../../src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs)) — **delete entire file**.

**`LegolasModule`** ([`src/Legolas.Module/LegolasModule.cs`](../../../src/Legolas.Module/LegolasModule.cs)) — remove `IHostedService` registration for `LegolasAreaCalibrationMigration`. Drop `IAreaState` injection into `PlayerLogIngestionService`'s factory.

**`LegolasSettings`** (the file housing `AreaCalibrations`) — mark the dict `[Obsolete]`:

```csharp
[Obsolete("Removed in a follow-up release; calibrations now live exclusively in IMapCalibrationService. " +
          "Field retained for one cycle to avoid downgrade-window data loss.")]
public Dictionary<string, AreaCalibration> AreaCalibrations { get; init; } = new();
```

The settings serializer (`SettingsAutoSaver<LegolasSettings>` / source-generated JSON context) preserves the field; only writers are removed.

**ViewModels:**
- `CalibrationSessionViewModel`: 5 sites switch `_service.CurrentAreaKey` → `_service.CurrentScene?.ParentAreaKey`. The picker comparison stays areas.json-shaped.
- `LegolasWizardViewModel:437`: `IsAreaKnown => _areaCalibration.CurrentAreaKey is not null` → `_areaCalibration.CurrentScene is not null`.
- `MapOverlayViewModel`: 3 sites reading `_areaCalibration.CurrentAreaKey` → switch to `CurrentScene?.ParentAreaKey`. The `_areaState?.CurrentArea` read at [:1089](../../../src/Legolas.Module/ViewModels/MapOverlayViewModel.cs:1089) → resolve via the shared helper.
- `PinCalibrationCoordinator:488`: log-string substitution `_service.CurrentAreaKey ?? "(unknown)"` → `_service.CurrentScene?.MapAssetKey ?? "(unknown)"`.
- `MotherlodeMeasurementCoordinator:329`: `_areaState?.CurrentArea` stays — parent area is the right axis for motherlode session scope, not the per-scene asset.

### 5.7 Persistence + retirements summary

| Path | Action | File |
|---|---|---|
| `%LocalAppData%/Mithril/MapCalibration/refinements.json` | Unchanged (asset-keyed since #1040). | — |
| `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json` | Unchanged (asset-keyed since #1040). | — |
| `%LocalAppData%/Mithril/MapCalibration/scene-asset-cache.json` | **NEW.** Schema 1, atomic temp+rename, per-entry resilient parse. | `SceneAssetCacheStore` |
| `%LocalAppData%/Mithril/Settings/LegolasSettings.json` | Field `AreaCalibrations` stays (marked `[Obsolete]` for one cycle). On-disk data is preserved across the upgrade; just not actively read. | `LegolasSettings` |

### 5.8 Testing strategy

xunit + FluentAssertions per CLAUDE.md. Per-surface, scoped to the seam each test catches.

**Arda layer:**
- `MapAssetLoaderTests` — composite-construction, idempotent re-parse, sub-zone-only `with`-transition (same parent area, different asset + friendly), malformed-line resilience.
- `MapScopeTests` — `CurrentMapScene` delegation, `MapSceneMeasuredAt` delegation.

**Calibration core:**
- `MapCalibrationServiceTests` — every method consumes `scene.MapAssetKey` for inner lookups; `Changed` payload carries the typed `MapSceneRef`. `AllCalibrations` keys remain raw asset strings (asymmetry assertion).
- `UserRefinementStoreTests` — delete every `ImportFromLegacy` test; preserve v1→v2 migrator tests verbatim.

**Cache (new — highest-value surface):**
- `SceneAssetCacheTests` — record/resolve roundtrip; second observation overwrites (PG-renamed-asset path); `(parent, null)` doesn't match an entry stored with non-null `SceneFriendlyName`.
- `SceneAssetCacheStoreTests` — roundtrip persistence; transactional rollback on `Persist` throw; per-entry resilient parse; whole-file corruption → empty + warn; missing file → empty + no warn.
- `SceneAssetCacheSeederTests` — given a synthetic `IReferenceDataService.Areas` + synthetic baseline, exactly 3 entries seed; non-matching areas don't seed; re-seeding on a non-empty cache is a no-op for observation-sourced entries.
- `SceneAssetCacheRecorderTests` — `MapAssetChanged` fires → `Record` called once with the typed scene. Null `CurrentScene` → no `Record`. Replay metadata is honoured (recording occurs).

**Consumer-side integration:**
- `AutoCalibrationEngineTests` — live `CurrentMapScene` path; cache-fallback path; both-null strict-gate path.
- `AutoCalibrationTriggerTests` — skip-if-calibrated uses resolved scene; `MapAssetChanged` invalidates `_persistedScenes` entry keyed on `MapAssetKey`.
- `OverlayWindowServiceTests` — live path engages calibrated render; cache-fallback engages calibrated render; both null → uncalibrated chip + scene-drawer loop still runs (preserves #872/#887 invariant); `MapAssetChanged` invalidates next frame.

**Headline regression-fix integration test** (`Legolas_PerSceneCalibration_IntegrationTests`): the test the #1041 brief called out as load-bearing.

Three test variants, all constructing the real `AreaCalibrationService` + `IMapCalibrationService` + `SceneAssetCache` (real `BundledBaselineLoader` against the actual `map-calibration-baseline.json`):

1. **Live truth.** `IMapState.CurrentMapScene = MapSceneRef("AreaSerbule", null, "Map_AreaSerbule")`. Assert: `_areaCalibration.CurrentScene` non-null, `CurrentCalibration` returns the baseline `AreaCalibration`, `IsCurrentAreaCalibrated == true`.
2. **Cache-fallback.** Same fixture but `CurrentMapScene = null`, only `CurrentArea = "AreaSerbule"` set; cache pre-seeded. Same three assertions pass.
3. **Strict gate.** Same fixture but `CurrentMapScene = null`, `CurrentArea = "AreaUnknownToCacheAndBaseline"`. Assert: `_areaCalibration.CurrentScene == null`, `IsCurrentAreaCalibrated == false`.

**Deletion-safety (build-failure-as-test):**
- Reintroducing a `string areaKey` parameter on `IMapCalibrationService` methods fails every consumer's compile.
- Reintroducing `LegolasAreaCalibrationMigration` lacks a DI registration.
- Reintroducing `_settings.AreaCalibrations[key] = ...` produces a `CS0618` warning (treated as error per `Directory.Build.targets`).

**Not in scope (deferred to manual smoke test in §8):**
- End-to-end against a live PG launch — covered by the §8 smoke checklist the owner runs in the worktree.
- Sidecar CLI argument construction — covered by #1040's existing tests; no shape change here.

## 6. Files touched (anticipated)

### Production (~20 files)

**Arda layer:**
- `src/Mithril.MapCalibration/MapSceneRef.cs` — add `MapAssetKey` field.
- `src/Arda/Arda.Contracts/State/Player/IMapState.cs` — swap 3 strings → `CurrentMapScene` + `MapSceneMeasuredAt`.
- `src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs` — payload reshape.
- `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs` — produce composites.
- `src/Arda/Arda.World.Player/Internal/MapScope.cs` — delegations.

**Calibration core:**
- `src/Mithril.MapCalibration/IMapCalibrationService.cs` — retype all params + Changed event; delete `ImportUserRefinements`.
- `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs` — match interface; delete `ImportUserRefinements` impl.
- `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs` — delete `ImportFromLegacy`.

**Cache (new):**
- `src/Mithril.MapCalibration/SceneAssetCache.cs` *(NEW)*
- `src/Mithril.MapCalibration/ISceneAssetCache.cs` *(NEW)* — public interface for DI.
- `src/Mithril.MapCalibration/Internal/SceneAssetCacheStore.cs` *(NEW)*
- `src/Mithril.MapCalibration/Internal/SceneAssetCacheSeeder.cs` *(NEW)*
- `src/Mithril.MapCalibration/Internal/SceneAssetCacheRecorder.cs` *(NEW, IHostedService)*
- `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` — add `SceneAssetCacheFile` + entry.
- `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs` — register cache + recorder.
- `src/Mithril.MapCalibration/Internal/SceneResolution.cs` *(NEW)* — `ResolveCurrentScene` helper.

**Calibration consumers:**
- `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs` — read `IMapState.CurrentMapScene`; cache fallback.
- `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs` — inject `IMapState` + cache; subscribe `MapAssetChanged`; rekey `_persistedScenes`.

**Renderer:**
- `src/Mithril.Overlay/Internal/OverlayWindowService.cs` — inject `IMapState` + cache; subscribe `MapAssetChanged`; all 4 calibration calls use `MapSceneRef`.
- `src/Mithril.Overlay/IOverlaySceneContext.cs` — rename `CurrentAreaKey` → `CurrentMapAssetKey`.
- `src/Mithril.Overlay/IWorldOverlayMarkers.cs` — rename `CurrentArea` setter.

**Legolas:**
- `src/Legolas.Module/Services/AreaCalibrationService.cs` — interface + impl: `CurrentScene`, `SelectScene`, retyped `OnMapCalChanged`; delete dual-write/clear.
- `src/Legolas.Module/Services/PlayerLogIngestionService.cs` — swap `AreaChanged` subscription for `MapAssetChanged`.
- `src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs` — **DELETE entire file**.
- `src/Legolas.Module/LegolasModule.cs` — remove migration registration; drop `IAreaState` from ingestion service.
- `src/Legolas.Module/LegolasSettings.cs` (wherever `AreaCalibrations` lives) — `[Obsolete]` annotation.
- `src/Legolas.Module/Services/PinCalibrationCoordinator.cs` — log-string update.
- `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs` — 5 sites.
- `src/Legolas.Module/ViewModels/LegolasWizardViewModel.cs` — 1 site.
- `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs` — 4 sites.

### Test (~10 files)

> Cohesion-verified paths. Test projects are `Legolas.Tests` (not `Legolas.Module.Tests`); existing tests in `tests/Mithril.MapCalibration.Tests/` and `tests/Arda.World.Player.Tests/` live at the project root, not under `Internal/`.

- `tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs` — extend (existing file).
- `tests/Arda.World.Player.Tests/MapScopeTests.cs` — NEW (or fold into existing `MapTests.cs`; check before adding).
- `tests/Mithril.MapCalibration.Tests/MapCalibrationServiceTests.cs` — retype + delete any `ImportUserRefinements` tests (existing file at root).
- `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs` — existing v1→v2 migrator tests stay verbatim.
- `tests/Mithril.MapCalibration.Tests/MapSceneRefTests.cs` *(NEW)* — small unit fixture for the 3-field record.
- `tests/Mithril.MapCalibration.Tests/SceneAssetCacheTests.cs` *(NEW)*
- `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheStoreTests.cs` *(NEW)*
- `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheSeederTests.cs` *(NEW)*
- `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheRecorderTests.cs` *(NEW)*
- `tests/Mithril.MapCalibration.Tests/SceneResolutionTests.cs` *(NEW)*
- `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs` — extend.
- `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs` — extend.
- `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs` — `FakeMapState` carries `CurrentMapScene`; `FakeSceneAssetCache` added.
- `tests/Mithril.Overlay.Tests/OverlayProjectionTests.cs` — extend with cache-fallback fixture (the existing test file covers the `ProjectMarkers` static; suitable for the cache-resolution path).
- `tests/Mithril.Overlay.Tests/OverlayWindowBindingTests.cs` — extend with the `MapAssetChanged` subscription assertion (or new test class if no existing fit).
- `tests/Legolas.Tests/Services/AreaCalibrationServiceTests.cs` — retype; delete dual-write assertion fixtures (existing file).
- `tests/Legolas.Tests/Services/PlayerLogIngestionServiceTests.cs` — retype to `MapAssetChanged` (verify file exists; may be new).
- `tests/Legolas.Tests/Integration/Legolas_PerSceneCalibration_IntegrationTests.cs` *(NEW, headline)* — three variants per §5.8.
- **DELETE:** `tests/Legolas.Tests/Services/LegolasAreaCalibrationMigrationTests.cs` (if it exists — check first).

## 7. Out of scope

- **`UserRefinement* → Solved*` API rename** (D7). Separate follow-up issue. Mechanical, ~10 files, no semantic change.
- **Wizard sub-zone-aware area picker** (D8). Separate follow-up issue. Consumes `SceneAssetCache.AllEntries` (an accessor we may also need to add to `ISceneAssetCache`, but only when the picker work lands).
- **Per-area landmark scoping for aggregator scenes.** Inherited from #1021 §7; unchanged.
- **War Cache maps.** Inherited from #1021 §7; unchanged.
- **Sub-zone → aggregator mapping table.** Inherited from #1021 §7; unchanged.
- **WPF manual-calibration tools** (`tools/MapCalibrationWpf/`, `tools/MapCalibrationFromScreenshot/`). Inherited from #1021 §7; unchanged — they don't reference `IMapCalibrationService`.

## 8. Verification owed

- **`Initializing area!` without subsequent `Downloading Map`.** The `merged-corpus.log` fixture exhibits exactly this shape (`AreaKurMountains` Initializing at line 28, no Downloading Map in the fixture). Verification needed on a real session: does PG always fire `Downloading Map` after `Initializing area!`, or are there legitimate cells (minimap-only render? loading-screen abort?) where the asset load is skipped? D3's cache + strict gate handle either case; the answer just calibrates how often the cache fallback is needed.
- **Manual smoke test (owner-verified outside the unit-test gate).** Three steps per §3 / Section 3 of the brainstorm:
  1. Launch Mithril with a Player.log containing `Initializing area! AreaSerbule` + `Downloading Map ... Map_AreaSerbule`. Open the overlay. Confirm baseline-anchored render (no "uncalibrated" chip, markers project correctly).
  2. Repeat with the same `Initializing` line but **no** `Downloading Map`. Confirm cache-fallback resolves via the seed (Serbule baseline render appears anyway).
  3. Launch with `Initializing area! AreaUnknown` (an unrecognized area not in baseline or cache). Confirm strict gate engages (uncalibrated chip surfaces).
  Per the `verify_headline_behavior_through_full_render_chain` memory: verified-green tests ≠ behavior-verified; the smoke test is the gate before merge.
- **PG asset rename across patches.** If a future PG patch renames `Map_AreaSerbule` → `Map_AreaSerbuleRefresh` (hypothetical), the seeded entry becomes stale and the live observation overwrites it on the user's first `Downloading Map`. The asymmetric live-wins invariant of `ResolveCurrentScene` handles this without additional code. Verification: capture a known rename event when one occurs and confirm the cache transitions through the recorder write-through.

## 9. Cross-references

- **Issue:** [mithril#1041](https://github.com/moumantai-gg/mithril/issues/1041) — original bug report + brainstorm transcript captured as comments.
- **Upstream spec:** [`docs/planning/map-calibration-1021-per-scene-keying/spec.md`](../map-calibration-1021-per-scene-keying/spec.md) — per-scene keying D1–D8 ratified there; this work is the consumer-migration follow-up.
- **Upstream PR:** [mithril#1040](https://github.com/moumantai-gg/mithril/pull/1040) — the cutover that introduced the regression this spec resolves.
- **Wiki (canonical grammar):** [Player-Log-Signals → Map asset loads](https://github.com/moumantai-gg/mithril/wiki/Player-Log-Signals#map-asset-loads-per-scene-map-textures).
- **Architecture context:** `docs/cross-source-correlation.md` — single-source, Tier-0 work; correlation tree doesn't apply.
- **Memory pointers:** `pg_map_asset_load_log_grammar`, `legolas_calibration_findings`, `verify_headline_behavior_through_full_render_chain`, `mithril_running_hook_misses_claude_worktrees`.
