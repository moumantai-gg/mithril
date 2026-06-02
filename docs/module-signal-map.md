# Module signal map

> **Vocabulary:** see [`docs/glossary.md`](glossary.md) for definitions of the world-sim terminology used in this doc.

Snapshot of every component's signal topology — what flows in, what closed mini-FSMs the component owns, what flows out. The inverse of [`module-charters.md`](module-charters.md): charters say *what each module is responsible for*; this doc says *what each module actually consumes and emits today.*

## Why this exists

This is the foundation for the [world-simulator design](world-simulator.md). Before the worlds' dispatch graphs can be drawn, the dependency topology of state holders has to be visible. It's the read-side companion to [`gamestate-services-gap-audit.md`](gamestate-services-gap-audit.md), which catalogues *missing* GameState services — this doc inventories the ones that exist and the modules that consume them.

Companion docs:
- [`world-simulator.md`](world-simulator.md) — the converged architecture this topology feeds into. Three layers (sources → worlds → views → modules); two independent worlds (PlayerWorld + ChatWorld) with sealed output boundaries; cross-source composition lives in views above both worlds.
- [`world-sim-migration-audit.md`](world-sim-migration-audit.md) — line-by-line audit of every state-holder against the architecture. 15 components; 5 need migration; 3 sleeper blockers. Where to start when planning a concrete migration step.

**Update this doc when:**
- a new GameState service ships
- a new module-level state machine is added
- a module's input set changes (new pipe / new GameState dep / new ref data / new persisted file)
- an existing component's outputs change (new event surface, new persisted file, new UI binding shape)

## How to read each entry

- **Inputs** — signals flowing in. Grouped: log pipes / GameState services / reference data / settings / user input / other.
- **State machines** — closed mini-FSMs the component owns. Name + one-line description of what it tracks. Transition tables are intentionally omitted; the source is canonical, and [`cross-source-correlation.md`](cross-source-correlation.md) covers cross-source patterns.
- **Outputs** — what flows out. Grouped: GameState-style `Subscribe` / `Current` surfaces / events / persisted JSON / UI bindings.
- **⚠️ markers** flag topology facts worth knowing (cross-FSM peeks, wall-clock-gated transitions, second event sources). Not value judgements — just things a world-sim refactor designer needs to see.

A component classified as "no state machines" still appears if it consumes or exposes signals — pure projectors and reactors are first-class topology nodes.

## Conventions

- Log-derived signals are consumed via Arda domain events (`IDomainEventSubscriber`). The legacy L0/L0.5/L1 pipeline (`IPlayerLogStream`, `IChatLogStream`, `ILogStreamDriver`, `PlayerLogPipeSplitter`) has been retired; all modules consume Arda's typed events.
- GameState services are named by interface (`IInventoryView` etc.). Modules subscribe to **these**, not raw log lines — the architectural commitment per #587.
- "Wall-clock TTL" means a transition guarded by `_time.GetUtcNow()` deltas. Distinguished from event-time deltas (`envelope.Payload.Timestamp` arithmetic) which are part of the log stream and replay-deterministic.

### Scope vocabulary

Every state holder has a **scope** — the partition along which its state replicates. Three kinds:

- **`reference`** — data that's immutable for the lifetime of a Mithril attach (modulo CDN refresh). Same across every character and server. Items, recipes, skills, NPCs, quest definitions, the server catalog.
- **`world` (per-server)** — properties of the simulated PG world, populated from any character's observations on that server. PG has multiple parallel servers (Laeth / Miraverre / Strekios / Dreva / Arisetsu visible in current `clientconfig.json`); world state is partitioned **per-server**, not global across all of PG. Weather, celestial state, area-existence ledger, the session ledger.
- **`character` (per-server-per-character)** — properties of an individual character's relationship to its server's world. Transitively per-server via its character. Skills, recipes, inventory, quests, favor, position, pins, effects, garden plots, words of power.

Two streams (Player.log and chat) each **self-scope** via their own intra-source banners — Player.log via `EVENT(Ok): connected, url=…` + the Player.log `LoginBanner`, chat via its `**** Logged In As X. Server Y. Timezone Offset Z.` banner. No cross-source coupling for scope determination. Cross-source correlators (Tier 1 / Tier 2 per [`cross-source-correlation.md`](cross-source-correlation.md)) should additionally **scope-check** that both sides are in the same `(Server, Character)` before firing, otherwise they're matching across a session boundary.

### State-holder scope at a glance

| Service | Scope |
|---|---|
| `IReferenceDataService` | reference |
| `IServerCatalogService` (planned) | reference |
| `Mithril.GameReports` (shipped #612, see [`world-simulator.md`](world-simulator.md) §Three categories) | reference (external shared data; per-character snapshot files, externally sourced) |
| `IGameSessionService` | world (session ledger; evolving from "current session" to a per-server ledger) |
| `IPlayerSkillStateService` | character |
| `IPlayerRecipeStateService` | character |
| `IPlayerEffectsStateService` | character |
| `IPlayerCelestialStateService` | world |
| `IPlayerWeatherTracker` | world (per-area, per-server; `Player` prefix is a misnomer) |
| `IPlayerAreaTracker` | world (area-existence ledger) + character (current area) |
| `IPlayerPositionTracker` | character |
| `IPlayerPinTracker` | character (user pins) |
| `IInventoryView` | character |
| `IPlayerQuestJournalState` | character |

The `Player*` naming on the world-scope services (`IPlayerWeatherTracker`, `IPlayerCelestialStateService`, parts of `IPlayerAreaTracker`) is misleading under this partition — they're actually world-scope readings of "what the active character can observe of the world." Rename target if/when the world-sim refactor lands; not worth churning ahead of that.

---

## Foundation: `Mithril.Reference`

### `IReferenceDataService`

**Inputs**
- HTTP: `cdn.projectgorgon.com/{version}/data/{file}.json` (versioned, version auto-detected via `CdnVersionDetector` parsing the redirect meta tag)
- Bundled fallback: `Mithril.Shared/Reference/BundledData/*.json` (ships with each Mithril build for offline-start)
- Background refresh: periodic re-fetch + persist

**State machines**
- Per-file fold — each known data file (items / recipes / skills / NPCs / quests / abilities / effects / areas / landmarks / lorebooks / titles / storage vaults / TSys / strings_all / xp tables) is its own keyed dictionary, hot-swapped on successful fetch

**Outputs**
- Per-file accessors: `Items`, `ItemsByInternalName`, `Recipes`, `Skills`, `Npcs`, `Quests`, etc.
- `FileUpdated` event per data file (`"items"`, `"recipes"`, `"npcs"`, …) — fires when a fetch (CDN or fallback) replaces the in-memory dictionary
- Icon URL builder: `https://cdn.projectgorgon.com/{version}/icons/icon_{IconId}.png`

Foundational — every module that displays an item, recipe, NPC, ability, or skill consumes this. The `FileUpdated` event is a *re-projection trigger*, not a world frame: modules re-render derived state on refresh, but the change is orthogonal to the Player.log frame stream (and deliberately so — see [`world-simulator.md`](world-simulator.md) for the "ref-data updates are not world inputs" decision).

---

## Foundation: `Mithril.GameState` services

These are the canonical state holders. Modules subscribe to *these* surfaces; new modules should not subscribe to raw log pipes.

### `IGameSessionService`

**Inputs**
- Log: `SystemSignalLogLine` filtered to `LoginBanner` (`[HH:MM:SS] Logged in as character X. Time UTC=…. Timezone Offset Z.`)
- *(Planned)* Log: `EVENT(Ok): connected, url=<url>, port=<port>` — per-session server URL, parsed by a planned `ConnectionEventParser`. Joins against `IServerCatalogService` to resolve `url` → friendly server name. See [In-scope future signals](#in-scope-future-signals).

**State machines**
- Session-boundary fold — emits `SessionStarted` per parsed login banner. Today emits the most recent session only; under per-server scope this evolves into a session ledger keyed by `(Server, Character)`.

**Outputs**
- `Current : GameSession?` — today carries `(SessionId, CharacterName, LoggedInUtc, TimezoneOffset)`; planned to gain a `Server` field once the catalog + connect-event parsers ship
- `Subscribe(Action<GameSession>)`
- `SessionStarted` event
- Pushes session identity into `SessionAnchor` (a `Mithril.Shared` leaf — used to stamp records that need session correlation)

⚠️ Today's `LoginBannerParser` does NOT capture server identity — `Logged in as character X. Time UTC=…` carries no server field. The chat-side banner (`Logged In As X. Server Y. Timezone Offset Z.`) does, but that's chat's own intra-source self-scope, not Player.log's. Player.log's server identity comes from the connect-event line (planned parser).

---

### `IPlayerSkillStateService`

**Inputs**
- Log: `LocalPlayer` pipe (skill XP / cap events)

**State machines**
- Skill-snapshot fold — applies `SkillProgress` / `SkillReset` events; idempotent re-emit suppression

**Outputs**
- `Current : PlayerSkillSnapshot`
- `Subscribe(Action<PlayerSkillSnapshot>)` (whole-snapshot stream)
- `SubscribeChanges(Action<SkillChange>)` (delta stream)

Design notes: [`player-skill-state-service.md`](player-skill-state-service.md).

---

### `IPlayerRecipeStateService`

**Inputs**
- Log: `LocalPlayer` pipe (`ProcessLoadRecipes`, `ProcessUpdateRecipe`)

**State machines**
- Recipe-snapshot fold — structural twin of skill state; `ProcessUpdateRecipe` is **absolute, not delta**

**Outputs**
- `Current : PlayerRecipeSnapshot`
- `Subscribe(Action<PlayerRecipeSnapshot>)`
- `SubscribeChanges(Action<RecipeChange>)`

---

### `IPlayerEffectsStateService`

**Inputs**
- Log: `LocalPlayer` pipe (`ProcessAddEffects`, `ProcessUpdateEffectName`, `ProcessRemoveEffect`)

**State machines**
- Effect-list fold — stack-based intra-handler correlation pairs adds with their resolved names

**Outputs**
- `TryGet`, `ActiveEffects`
- `Subscribe(Action<EffectEvent>)`

---

### `IPlayerCelestialStateService`

**Inputs**
- Log: `LocalPlayer` pipe (celestial / moon-phase events)

**State machines**
- Celestial-snapshot fold

**Outputs**
- `Current : CelestialInfo?`
- `Subscribe`

⚠️ Per memory `celestial_moonphase_producer`: quarter-phase tokens `FirstQuarter` / `ThirdQuarter` are **unconfirmed vs the live log** — capture-pending verification.

---

### `IPlayerWeatherTracker`

**Inputs**
- Log: unified classified pipe (`IClassifiedPlayerLogStream`) — folds both weather and `AreaLoading` events locally

**State machines**
- Weather-per-area fold — **owns its own area-tracking state locally** to dodge the pre-#556 cross-pump race; does not depend on `IPlayerAreaTracker.CurrentArea`

**Outputs**
- `CurrentArea`, `Current : WeatherState?`
- `Subscribe`

⚠️ Weather grammar pointer: wiki Player-Log-Signals §Weather. Sun-damage is tier-driven (Vampirism), not weather-name driven.

---

### `IPlayerAreaTracker`

**Inputs**
- Log: `SystemSignalLogLine` filtered to `AreaLoading`
- Legacy push-in: `Observe(line, ts)` — used by Legolas + Gandalf for pre-session reverse-scan bootstrap

**State machines**
- Current-area fold

**Outputs**
- `CurrentArea : string?`

⚠️ No canonical area-changed event surface today — three trackers (Pin / Weather / Position) deliberately self-derive `AreaLoading` from the unified pipe instead of subscribing here. The legacy `Observe` push-in surface is the one cross-cutting wart remaining.

---

### `IPlayerPositionTracker`

**Inputs**
- Log: unified classified pipe (`[Status]` lines parsed as relative offsets)

**State machines**
- Position fold — applies offsets; **stale mid-zone** (per memory `gamestate_owns_player_position`)

**Outputs**
- `Current : PlayerPosition?`
- `Subscribe`

⚠️ `Current` is the last-observed position, NOT live — movement silently invalidates the projector between `[Status]` emissions. Consumers that need live position have to assume staleness.

---

### `IPlayerPinTracker`

**Inputs**
- Log: unified classified pipe (`ProcessMapPinAdd` / `ProcessMapPinRemove` + `AreaLoading`)

**State machines**
- Pin-per-area fold — owns its own area-tracking state locally (#556 race avoidance, parallel to weather)

**Outputs**
- `CurrentArea`, `CurrentAreaPins`
- `Subscribe(Action<PinSetChanged>)`

Design notes: [`player-pin-service.md`](player-pin-service.md). Pin grammar: no edit verb (rename = Remove + Add).

---

### `IInventoryView`

> **Note:** the description below mirrors the pre-#602 monolithic
> `IInventoryService` topology at the *composed* level — the full per-half
> decomposition (`IPlayerInventoryState` (PlayerWorld) + `IChatInventoryState`
> (ChatWorld) + `IInventoryView` (composing layer) per #602; `Subscribe` shim
> retired per #659; `Items` Bind channel added per #729) is tracked under
> the broader signal-map reshape in
> [#665](https://github.com/moumantai-gg/mithril/issues/665).

**Inputs**
- Log: `LocalPlayer` pipe (`ProcessAddItem` / `ProcessRemoveItem`) — folded by `IPlayerInventoryState` post-#602
- Chat: `IChatInventoryState` consumes `[Status] X xN added to inventory.` frames via ChatWorld (no direct `IChatLogStream` tail post-#602)

**State machines**
- Instance-id ledger — `InstanceId → InternalName` per character (PlayerWorld half) + name-keyed stack-size observations (ChatWorld half)
- **Player.log + chat correlator** (canonical Tier 1) — `PendingCorrelator<string, *>` keyed by InternalName, 5s **wall-clock** TTL; lives inside `IInventoryView` composition (cross-source join above both worlds per principle 4)

**Outputs**
- **Query** — `TryResolve(instanceId)` / `TryGetStackSize(instanceId)` (snapshot reads — consumed by Arwen, plus the post-#659 PlayerWorld-direct consumers retain blueprint access)
- **React** — `Bus.Subscribe<InventoryItemAdded>` / `InventoryItemRemoved` / `InventoryStackChanged` (typed-frame surface; replaces the pre-#659 union-shaped `Subscribe(Action<InventoryEvent>)` shim)
- **Bind** — `Items: IReadOnlyObservableCollection<InventoryItem>` per #729 — consumed by Palantir's `LiveInventoryViewModel` per #745
- Character-scoped state

⚠️ The correlator TTL is the only wall-clock-gated **transition** in `Mithril.GameState`; everything else there is event-time-gated. Pattern reference: [`cross-source-correlation.md`](cross-source-correlation.md) §Tier 1.

---

### `IPlayerQuestJournalState`

**Inputs**
- Log: `LocalPlayer` pipe (`QuestAccepted`, `QuestCompleted`, `QuestJournalLoad`)

**State machines**
- Active-quest fold — active character's currently-in-journal set, derived from Player.log events only

**Outputs**
- `Subscribe(Action<PlayerQuestEvent>)` — discriminated subtypes `PlayerQuestAccepted` / `PlayerQuestAbandoned` / `PlayerQuestCompleted` (#657 / #718)
- No persistence (#718) — `ProcessLoadQuests` re-fires per session, so the active set rebuilds from each session's replay; cross-session completion anchors live in module-side ledgers (Gandalf's `DerivedTimerProgressService`).

⚠️ `PlayerQuestCompleted` subscribers must be past-anchored / idempotent on `Timestamp`. The post-#718 in-memory `_completedThisSession` dedupe map resets on every Mithril restart, so a duplicate `(InternalName, Timestamp)` pair may fire across a Mithril restart that lands within a single PG session (each fresh attach re-replays the current session's `ProcessCompleteQuest` lines). `QuestSource.AnchorCompletionCooldown` meets the contract by overwriting `DerivedTimerProgress.StartedAt` with the carried log-line timestamp — an overwrite-with-same-value is a no-op. Side-effect-emitting subscribers (toasts, audio alarms) should additionally gate on `Mode == Live` once worlds land per [`world-simulator.md`](world-simulator.md), and may want a per-session dedupe on the receiving side. See #736 + the `Subscribe` XML doc on `IPlayerQuestJournalState`.

✅ Extracted via #607 (world-sim migration item #6): the legacy `IQuestService` is gone. Reference data (what is quest X?) lives in `IReferenceDataService.Quests`; this folder owns only state (am I on quest X?). Consumers join the two surfaces explicitly. The previous `PerCharacterView<…>.CurrentChanged` listener and its `_time.GetUtcNow()`-stamped synthetic `Abandoned`/`Accepted`/`Completed` events retired with the extraction — character switch is now a UI binding swap, not a state mutation.

✅ Reshape under #718: the prior `CompletionHistory` map / `TryGetCompletion` API / per-character `quests.json` persistence retired. The folder now has the same shape as `IPlayerSkillState` / `IPlayerRecipeState` — single source, fold-to-snapshot, no persistence, log-derivable (principle 13). Gandalf imports any pre-existing `quests.json` into `DerivedTimerProgressService` on startup (`QuestCompletionImportService`) and deletes the source file once imported.

ℹ️ The `HandleJournalLoaded` log-derived `Abandoned`-inference (deriving `Abandoned` for quests missing from a `ProcessLoadQuests` snapshot) **stays** — that's a necessary inference from a real log event, stamped on the log line's timestamp, not on wall-clock.

---

## Modules

### Samwise (gardening)

**Charter:** garden / crop tracking + ripeness alerts ([`module-charters.md`](module-charters.md)).

**Inputs**
- Log: `LocalPlayer` pipe via `GardenIngestionService` — garden parser events (`SetPetOwner`, `UpdateDescription`, `StartInteraction`, `AddItem`, `DeleteItem`, `UpdateItemCode`, `GardeningXp`, `PlantingCapReached`, `ScreenTextError`)
- PlayerWorld: `IPlayerWorld.Bus.Subscribe<PlayerInventoryAdded>` / `<PlayerInventoryRemoved>` direct (post-#725 — migrated off the legacy `IInventoryService.Subscribe` shim that re-shaped `Added` / `Deleted` into `AddItem` / `DeleteItem` inside the ingestion service; the shim retired in #659)
- Reference: `IReferenceDataService` items.json (`FileUpdated`)
- Identity: `IActiveCharacterService`
- Settings: `SamwiseSettings`
- User input: snooze, dismiss, manual plot edits

**State machines**
- `GardenStateMachine` — per-character plot ledger; pairs gardening signals into plot transitions (empty → planted → growing → ripe → withered)
- `AlarmService` — reactor over `PlotChanged`; fires audio + window flash when a plot reaches Ripe; supports per-plot snooze
- `GardenIngestionService` — fan-in (Player.log pipe + `IPlayerWorld.Bus.Subscribe<PlayerInventoryAdded/Removed>` direct, post-#725; pre-#725/#659 the inventory leg was the union-shaped `IInventoryService.Subscribe` shim); not a real FSM, just a forwarder with dispatcher marshaling

**Outputs**
- `GardenStateMachine.PlotChanged` event
- Persisted: per-character garden JSON
- UI: garden grid (`PerCharacterView`-bound)

⚠️ `AlarmService.IsLikelyGarbageCollected` is a wall-clock-backed read into garden state. `GardenStateMachine.PruneWithered` is wall-clock TTL eviction. Both are transition-gating, not record-stamping.

---

### Pippin (food consumption)

**Charter:** food / recipe tracking via `FoodsConsumedReport` ([`module-charters.md`](module-charters.md)).

**Inputs**
- Log: `LocalPlayer` pipe (`FoodsConsumedReport`) via `GourmandIngestionService`
- Reference: `FoodCatalog` (snapshot) + `IReferenceDataService`

**State machines**
- `GourmandStateMachine` — single-event idempotent fold; tracks consumed-foods set per session

**Outputs**
- VM-bound state (no events surface today)
- Persisted: per-character food log

The simplest module — one input, one fold, no peeks, no wall-clock.

---

### Arwen (NPC favor)

**Charter:** NPC favor + gift-rate calibration ([`module-charters.md`](module-charters.md)).

**Inputs**
- Log: `LocalPlayer` pipe via `FavorIngestionService` — one event type parsed by `FavorLogParser`:
  - `FavorUpdate` — `ProcessStartInteraction` marker; absolute favor snapshot for the active NPC
- GameState: `IGiftSignalService` (Tier-2 signal service in `Mithril.GameState.Gifting`) — React-channel `Subscribe` for `GiftAccepted`. The signal service owns a single L1 subscription with its own `ProcessAddItem`-fed `instanceId → InternalName` map, correlates the `ProcessStartInteraction` / `ProcessDeleteItem` / `ProcessDeltaFavor` verb triple on its own pump, and emits a fully-resolved `GiftAccepted` with `InternalName` baked in. The React channel atomically replays the in-session event log to late subscribers (#585 contract).
- GameState: `IInventoryView.TryGetStackSize` (post-#753 — pre-#753 this was `IInventoryService.TryGetStackSize`, retired with the legacy interface) — same-source read against the view's composed map (sim-coherent under Player.log dispatch order, encapsulated inside the view layer per principle 4).
- GameState: `IGameSessionService` (SessionId for record stamps + dedup key)
- Reference: `IReferenceDataService` items.json, NPC data, gift preferences
- Settings: `ArwenSettings`, `CalibrationSettings`
- Other: `ICommunityCalibrationService` (CDN-fetched community gift rates)
- User input: confirm / discard pending gift quantity, observation editor

**State machines**
- `FavorStateService` — per-character NPC favor snapshots; `SetExactFavor` is an absolute (not delta) last-write-wins upsert
- `CalibrationService` — calibration aggregator. Production gift events arrive via `OnGiftAccepted(GiftAccepted)` from the `IGiftSignalService` React channel — already resolved (`InternalName` + `NpcKey` + delta), so this entry point goes straight to `RecordObservation`. Persists observations; derives per-(NPC,item) / per-(NPC,signature) / per-NPC / per-keyword rates. (Legacy hand-rolled 2-slot correlator FSM — `_pendingDeletedItem` ⊕ `_pendingDelta` — remains only to exercise pre-#608 transitions in unit tests; the production path bypasses it.)
- `PendingGiftObservation` queue — TTL-bounded list of gifts awaiting user confirmation of stack size

**Outputs**
- `FavorStateService.OnFavorUpdated` event
- `CalibrationService.DataChanged` / `PendingChanged` events
- Persisted: per-character favor JSON, `calibration.json` (aggregates), `observations.json` (source-of-truth observations)
- Community export: sanitized rate aggregates only (no per-observation timestamps / NPC favor snapshots)
- UI: favor dashboard, gift scanner, observations editor, calibration tab

**Cross-source posture.** Resolved post-#608 — the historical `IInventoryService.TryResolve` cross-FSM peek (legacy, pre-#602; the canonical Rule-1 violation cited in earlier revisions of the world-simulator design notes) was eliminated by lifting the gift-detection FSM into the Tier-2 `IGiftSignalService`. The signal service does all three verbs on a single L1 pump and emits resolved events; Arwen consumes only same-source signals (its own L1 favor pipe + the React channel). No cross-pump race on subscribe-late. See [`world-sim-migration-audit.md`](world-sim-migration-audit.md) §Arwen for the full narrative.

---

### Saruman (Words of Power)

**Charter:** Words-of-Power codebook tracking. (Not in [`module-charters.md`](module-charters.md)'s table; charter pending — flag.)

**Inputs**
- Log: `LocalPlayer` pipe via `SarumanDiscoveryIngestionService` — Player.log discovery side. Parses `ProcessBook("You discovered a word of power!", "…<sel>CODE</sel>…<b><size=125%>Word of Power: <effect></size></b>…")` lines. Replay: `FromSessionStart`.
- Log: chat pipe via `SarumanChatIngestionService` (through `ILogStreamDriver.Subscribe<RawLogLine>` on the chat path, NOT a direct `IChatLogStream` tail). Parses the chat-side word-of-power spoken-word announcement → marks the matching code as `Spent`. Replay: `LiveOnly`.
- PerCharacterView: `PerCharacterView<SarumanState>` (per-character codebook).

**State machines**
- `SarumanCodebookService` — codebook fold; per-character `Code → KnownWord` ledger. Supports rediscovery (bump `DiscoveryCount`, flip `Spent → Known`). Persists per mutation.
- `SarumanDiscoveryIngestionService` — Player.log fan-in; calls `RecordDiscovery` with the originating `LocalPlayerLogLine.Sequence` for high-water tracking.
- `SarumanChatIngestionService` — chat fan-in; calls `MarkSpent` on the matched code.

**Outputs**
- `SarumanCodebookService.CodebookChanged` event
- Persisted: per-character JSON
- UI: codebook view (known / spent words, discovery counts)

⚠️ **Saruman is the second cross-source consumer in the codebase** (alongside `IInventoryService` (legacy, pre-#602; now `IInventoryView` composing the post-split halves)). Its cross-source pattern is structurally different from the existing Tier 1 / 2 references: the two sides aren't *paired in time* — discovery on Player.log and the matching `Spent` on chat may be hours or days apart. Both sides write to the same record keyed by **word code**; no TTL, no correlator. This is closer to "Tier 1-without-pairing" than to any cataloged tier. Under the per-character sim scope, this stays well-defined as long as both ingestion sides see frames in matching `(Server, Character)` scope — same caveat as the Inventory correlator.

⚠️ Saruman is not in CLAUDE.md's module table — update CLAUDE.md when this entry lands so future audits don't miss it.

---

### Legolas (surveying + map overlay)

**Charter:** surveying, route optimization, map overlay ([`module-charters.md`](module-charters.md); architecture in [`legolas-overview.md`](legolas-overview.md)).

**Inputs**
- Log: `LocalPlayer` pipe via `PlayerLogIngestionService` (`ProcessMapFx`,
  `ProcessDoDelayLoop`, `ProcessScreenText(ImportantInfo, …)` for motherlode
  distance) + `ItemCollectionTracker` (`ProcessScreenText(ImportantInfo, …)`
  for `<Mineral> collected!`). Post-#606 Legolas has **zero direct chat
  consumers** — the module is PlayerWorld-resident.
- GameState: `IInventoryView.Bus.Subscribe<InventoryItemAdded>` /
  `Subscribe<InventoryStackChanged>` (via `ItemCollectionTracker`, post-#606
  replacement for the retired chat `[Status] X xN added to inventory.`
  consumer); `IPlayerPositionTracker.Subscribe`,
  `IPlayerPinTracker.Subscribe`, `IPlayerAreaTracker.CurrentArea`
  (synchronous read).
- Reference: `IReferenceDataService` (display-name resolution for the
  inventory→collect correlator key).
- Settings: `LegolasSettings`, `ICharacterPinAnchor`
- Other: `ICommunityCalibrationService`
- User input: route plan editor, overlay controls, calibration UI

**State machines**
- `PlayerLogIngestionService` — Player.log fan-in; **synchronous peeks** into `PlayerAreaTracker`, `AreaCalibrationService`, `SurveyFlowController`, `SessionState` mid-handler.
- `ItemCollectionTracker` — Tier-1 Add↔Collect correlator (#606); Add side via `IInventoryView.Bus`, Collect side via the new `PlayerLogParser.ItemCollectedRx`. Intra-module (no longer cross-source).
- `MotherlodeMeasurementCoordinator` — single-source PlayerWorld SM post-#604 (`ProcessDoDelayLoop` ↔ `ProcessScreenText(ImportantInfo, …)`).
- `SurveyFlowController` — survey-session lifecycle (idle → active → all-collected)
- `MotherlodeFlowController` — motherlode-session lifecycle
- `PinCalibrationCoordinator` — pin-set delta reactor for projector calibration
- `AreaCalibrationService` — per-area projector calibration state
- `AutoOverlayCoordinator` — overlay visibility from session + flow state

**Outputs**
- `SessionState` (observable session-scoped surveys + collected items)
- Calibration data (per-area projector params)
- Map overlay UI (window above game)
- Share-card export
- Persisted: per-character JSON (calibration, route plans)

---

### Elrond (skill leveling advisor)

**Charter:** recipe-anchored skill leveling advisor ([`module-charters.md`](module-charters.md)).

**Inputs**
- Reference: `IReferenceDataService` (recipes, skills, items, XP tables)
- Character snapshot: today imported from Bilbo storage report; post-migration reads directly from `Mithril.GameReports` (per world-simulator.md migration item 11). Live `IPlayerSkillStateService` integration is tracked separately in #224–228 chain.
- Settings: `ElrondSettings`
- User input: skill picker, recipe selector, complexity/difficulty editor

**State machines**
- None — pure projector. `SkillAdvisorEngine` computes XP/hr and crafting plans from reference data + character snapshot.

**Outputs**
- Crafting plan UI
- Persisted: per-character skill targets

---

### Gandalf (timers + shift alarms)

**Charter:** user-created timers, derived loot-cooldown timers, in-game-time shift alarms ([`module-charters.md`](module-charters.md); detailed in [`gandalf-timer-model-redesign.md`](gandalf-timer-model-redesign.md)).

**Inputs**
- Log: `LocalPlayer` pipe via `LootIngestionService` (chest interactions, boss kill credit, defeat cooldowns)
- GameState: `IPlayerQuestJournalState.Subscribe` (quest cooldowns), `IPlayerAreaTracker.CurrentArea` (chest area stamping)
- Settings: `GandalfSettings`, `GandalfShiftSettings`, user-defined timer definitions, `IShiftCatalog`
- Reference: `ICommunityCalibrationService` (chest cooldown community data)
- Wall-clock: `IGameClock` (PG's in-game time-of-day for shift alarms), `TimeProvider` for timer expiration
- User input: timer creation, snooze, dismiss

**State machines**
- `LootBracketTracker` — pure synchronous SM that brackets loot lines after a chest interaction (2s `SoftTimeout`, **event-time** gated — replay-safe)
- `LootIngestionService` — Player.log fan-in; dispatches to `LootBracketTracker`
- `LootSource` — projects bracketed loot into chest cooldown timers
- `QuestSource` — projects `IPlayerQuestJournalState` events into quest cooldown timers
- `UserTimerSource` — projects user-created timer definitions into timer rows
- `DerivedTimerProgressService` — composes the heterogeneous timer sources
- `TimerProgressService` — **wall-clock-driven** expiration; transitions timers between Armed / Firing / Expired / Recurring
- `TimerExpirationScheduler` — wakes the app at next `FiringAt` via `DispatcherTimer`; injects expiration into `TimerProgressService`
- `TimerDisplayScheduler` — 1Hz progress-bar animation; **pure display, not state**
- `ShiftAlarmService` — wakes at PG in-game time-of-day boundaries; fires shift transition alarms
- `TimerAlarmService` — audio + visual alarm reactor
- `TimerSourceBinder` — binds heterogeneous `ITimerSource` implementations into one UI list

**Outputs**
- Timer UI (active + upcoming list)
- Audio + window flash alarms
- Persisted: timer definitions, per-character progress

⚠️ Three wall-clock-driven wakeup schedulers (`TimerExpirationScheduler`, `ShiftAlarmService`, `TimerProgressService.CheckExpirations`) drive transitions today. These are the most clock-dependent state machines in the codebase.

⚠️ **Under the [world-sim model](world-simulator.md), all three scheduler-services collapse**: Gandalf subscribes to PlayerWorld's `CalendarTimeAdvanced` + `TimeOfDayShift` domain events (principle 13), compares each event's timestamp against its module-side timer ledger, fires alarms gated on `Mode == Live` (principle 12). No `DispatcherTimer`-based scheduling; no `IGameClock` dependency (PG in-game time is derived from world calendar events via the existing formula); no `TimerExpirationScheduler` / `ShiftAlarmService` wake-injection. Module-side state for the timer definitions stays; the wakeup machinery doesn't.

---

### Bilbo (storage / craftability)

**Charter:** storage / inventory aggregation across characters; recipe craftability projector ([`module-charters.md`](module-charters.md)).

**Inputs**
- Filesystem: character storage report JSON (read at module init / on file change) — owned today by `StorageReportLoader` inside Bilbo; **migrates to `Mithril.GameReports`** (shared service in foundation layer) per the [world-simulator design](world-simulator.md)'s "Three categories of data" section. Vault contents come from here — they're the canonical example of data only available in reports, not in worlds.
- Reference: `IReferenceDataService` (recipes, items)
- User input: craft selection, filter / search

**State machines**
- None — pure projector. Reads from `Mithril.GameReports` (post-migration; today: `StorageReportLoader`); `CraftableRecipeCalculator` computes craftability from inventory + recipes.

**Outputs**
- Storage grid UI, craftable-recipe UI
- (Post-migration: character snapshot is in `Mithril.GameReports`, consumed by Elrond directly from there — not exported by Bilbo.)

---

### Silmarillion (reference data browser)

**Charter:** read-only reference data browser; the canonical "what Mithril knows about X" display surface ([`module-charters.md`](module-charters.md); detailed in [`roadmaps/silmarillion.md`](roadmaps/silmarillion.md)).

**Inputs**
- Reference: `IReferenceDataService.FileUpdated` per data file (items / recipes / skills / NPCs / quests / abilities / effects / areas / landmarks / lorebooks / titles / storage vaults / TSys)
- User input: tab navigation, search, sort, filter

**State machines**
- None — pure projector. Each tab VM subscribes to `FileUpdated` and rebuilds its list.

**Outputs**
- Browser UI (one tab per reference data type)
- "Open entity" link-out events to other modules (cross-tab navigation)

---

### Celebrimbor (crafting / leveling planner)

**Charter:** multi-step crafting plans + leveling plan documents ([`module-charters.md`](module-charters.md); detailed in [`roadmaps/celebrimbor.md`](roadmaps/celebrimbor.md)).

**Inputs**
- Reference: `IReferenceDataService`
- Filesystem: leveling plan JSON via `LevelingPlanStore`
- User input: plan editor, step navigation, result-effects display

**State machines**
- None — pure projector. `LevelingPlanStore` loads/saves JSON; VM-local craft-step progress.

**Outputs**
- Plan editor UI
- Persisted: leveling plan JSON

---

## Cross-cutting observations

A handful of patterns are worth seeing all at once after the per-component scan. These reflect the **current state** of the codebase — the [world-simulator design](world-simulator.md) migration plan addresses each one.

- **Cross-FSM peeks live in three places.** `Arwen.CalibrationService.OnItemDeleted` → `IInventoryView.TryResolve` (post-#753 — pre-#753 this was `IInventoryService.TryResolve`). `Legolas.PlayerLogIngestionService` → `PlayerAreaTracker.CurrentArea` + `AreaCalibrationService.CurrentCalibration` + `SurveyFlowController.CurrentState`. `Samwise.AlarmService` → `GardenStateMachine.IsLikelyGarbageCollected`. None inside `Mithril.GameState` itself — the foundation layer is peek-free post-#556 / #587. Under the clocked-sim model these peeks become legal (declared dispatch order makes them coherent); the cross-source one (Arwen → Inventory) moves through the view layer instead.
- **Cross-source services spanned both Player.log and chat in two places pre-migration.** `IInventoryService` (foundation; fused Player.log `ProcessAddItem` with chat `[Status] added` via Tier-1 `PendingCorrelator`) and `SarumanCodebookService` (module-level; mutated from both `SarumanDiscoveryIngestionService` and `SarumanChatIngestionService`). Both **were split** per the [world-simulator rule](world-simulator.md) — Player.log half stays in PlayerWorld, chat half stays in ChatWorld, an `IInventoryView` / `IWordOfPowerView` composes at the view layer. The inventory split shipped in #602; the Saruman split is tracked in #603. (Pre-migration framing retained for the historical context — the broader signal-map reshape lives in [#665](https://github.com/moumantai-gg/mithril/issues/665).)
- **Chat-input surface post-migration.** After #602 (inventory split), #603 (Saruman split), #604 / #605 / #606 (Legolas retirements), there are **zero direct module-level `IChatLogStream` consumers**. The chat stream is consumed only inside `ChatWorld` (the `IChatInventoryState` folder + the `IChatWordOfPowerState` folder, both fed by the chat-channel classifier). Modules consume the composed view surfaces (`IInventoryView`, `IWordOfPowerView`) instead. Legolas — the last holdout — retired its chat tail in #606; every chat verb it consumed has a same-source Player.log equivalent (mapped in the wiki Player-Log-Signals page).
- **Both streams self-scope independently.** Player.log identifies its `(Server, Character)` from its own intra-source signals (`Servers:` catalog + `EVENT(Ok): connected, url=…` + `LoginBanner` — the latter two planned). Chat identifies its `(Server, Character)` from its own `**** Logged In As X. Server Y. Timezone Offset Z.` banner. No cross-source coupling for scope determination. Cross-source agreement (both banners' character names match) is verification, not derivation.
- **View-layer correlators must scope-check.** Once the splits land and cross-source correlation lives in the view layer, every cross-world join must assert both sides are in the same `(Server, Character)` before firing — otherwise it's pairing across a session boundary. In single-server play this is implicitly safe; under multi-server use it's a latent correctness gap that needs the scope check to land alongside the connect-event / catalog parsers.
- **Wall-clock-gated transitions live in five places.** `IInventoryView` correlator TTL (5s, the only foundation-layer wall-clock gate; pre-#602 this lived in the monolithic `IInventoryService`); `Samwise.GardenStateMachine.PruneWithered` + `AlarmService.IsLikelyGarbageCollected`; `Gandalf.TimerProgressService.CheckExpirations` + `TimerExpirationScheduler` + `ShiftAlarmService`; `Legolas.MotherlodeMeasurementCoordinator` (event-time but compared against a fresh foreign-FSM timestamp, which is wall-clock-shaped). Everything else uses event-time arithmetic on payload timestamps. **Under the worlds**, all of these migrate from `_time.GetUtcNow()` to `IWorldClock.Now` and become replay-deterministic — a mechanical search-replace.
- **Second event sources (non-log inputs that mutate state).** Pre-#602 `IInventoryService` carried a `FileSystemWatcher` for export-reconcile (retired with the split); `Gandalf` three wakeup schedulers; user-input commands across every UI-bearing module. (The legacy `IQuestService` `PerCharacterView.CurrentChanged` character-switch reload was a second event source until #607 — see the `IPlayerQuestJournalState` entry — and is now retired under per-character world scope.) Under the worlds these become **timestamped producer-emitted frames** for a world's merger (wake-at-T) or move out of world scope entirely (user input mutates adjacent state directly).
- **Module → GameState dependency graph is shallow.** No module depends on more than four GameState services. The deepest consumer (`Legolas.MotherlodeMeasurementCoordinator` — 4 services) is *today* the only Tier-2 SM in the repo; post-migration (item 3 in the world-simulator design plan) it becomes single-source and the Tier-2 reference vanishes. Arwen depends on 2 (`IInventoryView`, `IGameSessionService`). Samwise on 1 (`IInventoryView`). Saruman on 0 GameState services (consumes reference + per-character view only, despite being a cross-source consumer). Pippin / Bilbo / Silmarillion / Celebrimbor / Elrond on 0.
- **Five of ten modules are pure projectors** (Pippin, Elrond, Bilbo, Silmarillion, Celebrimbor) — no log subscriptions, no GameState subscriptions for state mutation. They consume reference data + persisted JSON + user input only.

---

## In-scope future signals

Forward-tense companion to the topology snapshot above. Lists signal sources we've decided are worth modeling but haven't yet built. Each entry follows the same `Inputs / State machines / Outputs` shape so the topology slot is ready when a consumer asks for it.

This is **not a roadmap** — prioritisation (target version, effort, status) is tracked separately, not in this doc. This section reserves topology shapes only. Signals PG emits but no module consumes (combat ticks beyond attributes, group membership, ability-cooldown applies, vendor / trade, etc.) deliberately stay off this list until a consumer asks.

### `IServerCatalogService` (planned)

**Inputs**
- Log: `LocalPlayer` pipe (or `SystemSignal` — TBD by parser fit) — the `Servers: [ { … } ]` JSON line emitted at startup after `Downloading config file https://client.projectgorgon.com/clientconfig.json…`

**State machines**
- Catalog fold — once-per-attach parse of the JSON array into a `Url → ServerEntry` dictionary. `ServerEntry` carries `ID` (`"s0"`–`"s4"`), `Name` (`"Arisetsu"`–`"Laeth"`), `Url`, `Port`, `Description`.

**Outputs**
- `Get(url) : ServerEntry?` lookup
- `All : IReadOnlyCollection<ServerEntry>`
- Could alternatively live as a regular `Mithril.Reference` entry (the data shape fits) — design choice deferred to implementation

Notes: reference-scope (immutable per Mithril attach). The catalog itself is the join target for connection events; not a world-state-mutating producer.

### `ConnectionEventParser` + `IGameSessionService.Server` field (planned)

**Inputs**
- Log: `LocalPlayer` pipe — `EVENT(Ok): connected, url=<url>, port=<port>` lines emitted per session

**State machines**
- Connect-event fold — pairs with the subsequent `LoginBanner` to populate `GameSession.Server = catalog[url].Name`. Connect line lands first (visible in current logs ~17 minutes ahead of the login banner due to character-select / area-load latency); banner lands second; pairing is by adjacency-within-session, not timestamp-correlation.

**Outputs**
- Augments existing `IGameSessionService.Current : GameSession` with a `Server` field
- Augments `SessionStarted` event payload accordingly

Notes: world-scope. Same-source — no chat dependency. The chat-side banner (`Logged In As X. Server Y.`) carries server identity too but for chat's own intra-source scope; the cross-source agreement check is verification only, not derivation.

### `IPlayerAttributeStateService` (planned)

**Inputs**
- Log: `LocalPlayer` pipe (attribute-change events — HP / Power / Armor / etc.; hot signal, fires on every combat tick)
- GameState: `IPlayerEffectsStateService` (active modifiers — buffs / debuffs that adjust effective values)

**State machines**
- Attribute-snapshot fold — base attribute values from log events
- Effective-attribute projector — composes base ⊕ active-effect-modifiers (design choice deferred: eager fold at frame N vs lazy compose at read time)

**Outputs**
- `Current : PlayerAttributeSnapshot`
- `SubscribeChanges(Action<AttributeChange>)`

Notes: the textbook case for declared dispatch order under the world model — Effects applies at frame N → PlayerAttribute reads coherent frame-N effect state. Also the first place dispatch-graph load characteristics become observable, since attribute updates fire on every combat tick.

### `IPlayerDeathStateService` (planned)

**Inputs**
- Log: `LocalPlayer` pipe (death + respawn events)

**State machines**
- Death-event fold — emits structured death-with-cause records; tracks current alive / dead / respawning state

**Outputs**
- `Current : DeathState`
- `Subscribe(Action<DeathEvent>)`

Notes: feeds any future Dying-skill XP tracker (Dying is a PG skill levelled by dying with side effects active). No GameState dependencies — pure log-driven leaf.

### `IPlayerCurrencyStateService` (planned)

**Inputs**
- Log: `LocalPlayer` pipe (currency-change events)
- Possibly chat: balance announcements (Tier-1 correlator if needed for confirmation)

**State machines**
- Balance fold — current gold / councils / other currencies
- Ledger — frame-stamped time-series of changes; each entry carries `(Now, Frame)` per the dual-clock convention

**Outputs**
- `Current : CurrencyBalance`
- `SubscribeChanges(Action<CurrencyChange>)`
- Persisted: per-character ledger JSON (append-only time-series)

Notes: min/max consumers want gold-over-time analysis, so the ledger is a first-class output rather than just a snapshot. Early customer for the dual-clock `(Now, Frame)` pair-stamp convention — both axes matter (gold per hour wants `Now`; "did this transaction precede that one" wants `Frame`).

### `Mithril.GameReports` (shipped, #612)

**Replaces** the earlier "Storage-export as synthetic-frame producer" entry — the world-simulator design's [Three categories of data](world-simulator.md) section reframed this as a **shared service in the foundation layer**, not a producer of frames into a world. Reports are point-in-time records (PG's `/exportchar` output), not world events.

**Inputs**
- Filesystem: per-character export JSON files in `Reports/` (`items_X.json`, plus skills / recipes / quests / vault data). Loaded via `FileSystemWatcher`; debounced per file.

**State machines**
- Per-file fold — each known report file is its own keyed dictionary, hot-swapped on file change. No event-stream semantics; consumers read the current snapshot.

**Outputs**
- Per-file accessors (current snapshot of items / skills / recipes / vault contents / etc.)
- `Updated` event per file when a fresh export is detected
- Per-character scope; queried by the active session's `(Server, Character)`

**Consumers** (post-migration):
- Bilbo — storage view, craftability projection
- Elrond — character snapshot input for skill advisor
- Future: any module needing "what does this character have access to" (vault + bag composed at a view layer above worlds + reports)

Notes: vault contents are the canonical case requiring this service — the worlds can't observe vault items, only reports include them. The previously-flagged "FileSystemWatcher reconcile retires under chat replay" framing was wrong: chat replay covers pre-attach inventory adds; vault/snapshot data requires GameReports; the two concerns separate cleanly. See `world-simulator.md` migration item 11.
