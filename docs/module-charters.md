# Module charters

> **Why this exists.** A module's *purpose* (one line in CLAUDE.md's table) doesn't
> prevent scope errors — its *boundaries* do. This doc records, per module, what it
> **owns**, what it **explicitly does not own and why**, and which reference data it is
> responsible for sourcing. The negative space is the load-bearing part.
>
> **The filter this enforces.** A data-availability gap is **not** a feature backlog.
> "Module X *could* read reference data Y" is only a gap if Y serves X's *Owns*. A
> "what can modules consume now" pass that ranks by data-reachability will manufacture
> non-gaps (it did: it proposed Gandalf adjudicate quest eligibility — a direct charter
> violation). Before proposing work for a module, check its charter: does it serve what
> the module owns? If not, it belongs to a different module or to no module.

## How to read confidence markers

Each **Does NOT own** entry is tagged:

- **✅ confirmed** — backed by the module owner, an existing design doc, or an
  established design decision. Treat as binding.
- **⚠️ inferred** — drafted from code behaviour by Claude, *not* owner-confirmed.
  A wrong charter is worse than none (it gets cited as authority), so these are
  **proposals pending sign-off**, not yet binding. Owner: confirm, correct, or delete.

`Owns` lines are grounded in current code and are lower-risk; `Reference data` lists
what the module legitimately consumes within its charter (not everything it *could*).

A **Does NOT own** entry is one of two kinds, and the distinction matters:

- **Hard (authority):** another module or the game engine is the source of truth;
  doing it here would duplicate/contradict authority. Example: Gandalf vs. quest
  eligibility. *Must not.*
- **Soft (empty):** on-charter-adjacent and not prohibited, but a deliberate
  non-feature — the underlying mechanic is trivial or the data doesn't exist, so there
  is nothing worth building. Example: Samwise vs. crop-selection advice. *Won't,
  because empty.* These are still binding decisions, just for a different reason.

---

## Cross-cutting ownership (confirmed)

**GameState owns the emulated game world; modules project subsets for UX. ✅
owner-confirmed 2026-05-21.** This is the strategic principle the tactical
rules below follow from. [#511](https://github.com/moumantai-gg/mithril/issues/511)'s
layered log pipeline (L0 → L0.5 → L1 → L2) exists to rebuild the emulated
game — the player, the NPCs, the world — from `Player.log`. The GameState
services that have emerged organically through the project's arc —
[`IPlayerPositionTracker`](../src/Mithril.GameState/Movement/IPlayerPositionTracker.cs)
([#454](https://github.com/moumantai-gg/mithril/pull/454)),
[`IPlayerSkillState`](../src/Mithril.GameState/Skills/IPlayerSkillState.cs)
([#462](https://github.com/moumantai-gg/mithril/issues/462)/[#465](https://github.com/moumantai-gg/mithril/pull/465)),
[`IPlayerPinTracker`](../src/Mithril.GameState/Pins/IPlayerPinTracker.cs)
([#468](https://github.com/moumantai-gg/mithril/issues/468)),
[`IInventoryView`](../src/Mithril.GameState/Inventory/IInventoryView.cs)
([#602](https://github.com/moumantai-gg/mithril/issues/602) — composes
[`IPlayerInventoryState`](../src/Mithril.GameState/Inventory/IPlayerInventoryState.cs) +
[`IChatInventoryState`](../src/Mithril.GameState/Inventory/IChatInventoryState.cs)),
[`IPlayerWeatherTracker`](../src/Mithril.GameState/Weather/IPlayerWeatherTracker.cs),
[`IPlayerCelestialState`](../src/Mithril.GameState/Celestial/IPlayerCelestialState.cs)
([#490](https://github.com/moumantai-gg/mithril/pull/490)),
[`IPlayerRecipeState`](../src/Mithril.GameState/Recipes/IPlayerRecipeState.cs)
([#475](https://github.com/moumantai-gg/mithril/pull/475)),
[`IGameSessionService`](../src/Mithril.GameState/Sessions/IGameSessionService.cs),
[`IPlayerQuestJournalState`](../src/Mithril.GameState/Quests/IPlayerQuestJournalState.cs),
[`PlayerAreaTracker`](../src/Mithril.GameState/Areas/PlayerAreaTracker.cs),
and [`INpcStateTracker`](https://github.com/moumantai-gg/mithril/issues/552)
(#552, in flight) — are **not** parallel abstractions of their domains;
they **are** the one canonical model of the emulated game. Modules
**project subsets** of that emulated game with module-specific UX
(Samwise → garden subset; Smaug → vendor-economics subset; Arwen → NPC-
favor subset; Gandalf → timer/cooldown subset; Legolas → surveying
subset; etc.). A module is a *surface* over GameState, never a parallel
emulation.

The two tactical rules below — *modules consume the service surface
(not raw logs)* (added 2026-05-21 via
[#578](https://github.com/moumantai-gg/mithril/pull/578)) and *services
translate logs into a developer-facing domain model with three
channels: query, react, bind* (added 2026-05-21 via
[#584](https://github.com/moumantai-gg/mithril/pull/584)) — are
**derivations** of this strategic principle, not separate commitments.
The underlying direction has been in place since #511 shipped the
layered pipeline; this paragraph articulates it explicitly so future
module/service design questions can be answered from the principle
directly without re-deriving.

Applies to *every* module; owner-confirmed 2026-05-16 (section
structure + bullets 1-2), 2026-05-21 (consumption-side rule, three-
channel rule, and this strategic principle):

- **Recipe / crafting → Elrond or Celebrimbor.** Anything recipe- or crafting-related
  — items as crafting ingredients, crafting *use* of an item, craft planning, leveling
  *via* recipes — is owned by **Elrond** (leveling advice) or **Celebrimbor** (planning
  / execution), never by the module that happens to produce or hold the item. Cited by
  Samwise (crops), Pippin (food), and Bilbo (craftables) below.
  **Carve-out:** *evaluating which recipes the player's current inv/storage already
  satisfies* is a read-only state query owned by **Bilbo** (its data domain), not
  planning/leveling. This rule governs crafting-*as-activity* (plan / level / execute),
  not "what is makeable from what I hold right now."
- **Two layers: the *data* (a shared single-source service) and the *browse/evaluate
  surface* (exactly one owning module). Rendering elsewhere is not turf.** Plumbing
  verified against code 2026-05-16:

  | Domain | Data owner — shared single source of truth | Surface owner — module |
  |---|---|---|
  | Reference (CDN) data | `IReferenceDataService` (Mithril.Shared) | **Silmarillion** |
  | Static storage *export* | `IActiveCharacterService` / `StorageReport` (Mithril.Shared) | **Bilbo** (+ immediate craftability) |
  | Live inventory simulation | `IInventoryView` (**Mithril.GameState**, #602) — composes `IPlayerInventoryState` (tails `IPlayerLogStream` `ProcessAddItem/…`) with `IChatInventoryState` (tails chat `[Status]` for stack sizes) | **Palantir** (debug surface — `LiveInventoryView`) |
  | Eaten-food state | in-game report (dumped to `Player.log`) | **Pippin** (Gourmand) |
  | Progression state | character export | **Elrond** (as leveling constraints) |
  | NPC store state | **Smaug** mines it itself (no shared service) | **Smaug** — the one domain where a single module owns *both* layers |
  | Words-of-Power known-state | player logs (learn + utterance) | **Saruman** |

  Owning a *surface* ≠ owning the *data*: the shared service is the single source of
  truth; modules `Subscribe`/query it (this is *why* it's centralised — the service
  docstring calls out the late-subscribe race a per-module rebuild would hit).
  "Renders some data" ≠ "owns the surface," and "owns the surface" ≠ "owns the data."
  *Recollection correction, verified:* the live-inventory sim is in **Mithril.GameState**,
  not Mithril.Shared; Mithril.Shared owns the static *export* only.

- **Modules `Subscribe` to the GameState service surface; they do not subscribe to
  raw logs for cross-cutting state. ✅ owner-confirmed 2026-05-21.** Each GameState
  service ([`IInventoryView`](../src/Mithril.GameState/Inventory/IInventoryView.cs),
  [`IPlayerPinTracker`](../src/Mithril.GameState/Pins/IPlayerPinTracker.cs),
  [`IPlayerWeatherTracker`](../src/Mithril.GameState/Weather/IPlayerWeatherTracker.cs),
  [`IPlayerPositionTracker`](../src/Mithril.GameState/Movement/IPlayerPositionTracker.cs),
  [`IPlayerSkillState`](../src/Mithril.GameState/Skills/IPlayerSkillState.cs), and the
  shipped Recipe / Celestial / Area trackers) owns its L1 subscription internally and
  exposes `Subscribe(Action<TEvent>)` with atomic replay-then-live. Modules consume the
  service surface; they do **not** `Subscribe<LocalPlayerLogLine>` /
  `IChatLogStream` / `IClassifiedPlayerLogLine` for any data domain that has a GameState
  service. Direct log subscription is reserved for (a) the GameState services
  themselves; (b) module-specific domains the module owns end-to-end (Samwise/Garden,
  Pippin/Gourmand, Arwen/Favor business logic, Gandalf/Loot business logic, etc.).

  *Why.* Real, hard-to-reproduce bugs from unclear ownership when multiple modules each
  rebuilt their own view of the same underlying state from the raw log stream. Two
  state machines on the same signal with subtly different framing produced symptoms
  that didn't replay deterministically. Centralising the canonical view in a GameState
  service — with the atomic replay-then-live `Subscribe` contract — retired that whole
  bug class. The rule is the *consumption-side* complement of the data-owner-vs-surface
  bullet above: that bullet says shared services *exist*; this one says modules
  *consume them*, not the underlying log.

  *Anti-pattern (flag immediately).* A module subscribing to `_driver.Subscribe<LocalPlayerLogLine>`
  /`IPlayerLogStream` / `IChatLogStream` / `IClassifiedPlayerLogLine` and matching a
  cross-cutting verb (`ProcessAddItem`, `ProcessDeleteItem`, `ProcessUpdateItemCode`,
  `ProcessRemoveFromStorageVault`, `ProcessLoadSkills`, `ProcessUpdateSkill`,
  `ProcessLoadRecipes`, `ProcessUpdateRecipe`, `ProcessSetWeather`, `ProcessMapPin*`,
  `ProcessNewPosition`, area-transition verbs, celestial / moon-phase verbs), or
  holding its own `[GeneratedRegex]` against one. The fix is "consume the corresponding
  GameState service's `Subscribe(...)`," not "write your own state machine on the same
  signal."

  *Known anti-pattern instances — audit landed as
  [#579](https://github.com/moumantai-gg/mithril/issues/579) on 2026-05-21,
  surfacing four Class A migrations (Gandalf `LootBracketTracker.AddItemRx`,
  Smaug CivicPride, Samwise GardeningXp, Arwen `ItemDeleted` — Arwen
  resolved via [#608](https://github.com/moumantai-gg/mithril/pull/608) by
  lifting the gift-detection FSM into the Tier-2 `IGiftSignalService`; see
  [`world-sim-migration-audit.md`](world-sim-migration-audit.md) §Arwen)
  plus one Class C design discussion (Samwise `ProcessUpdateItemCode` needs
  a new `InventoryEvent.CodeChanged` event kind on `IInventoryService`
  — described pre-#602; the post-split equivalent would land on
  `IPlayerInventoryState` or `IInventoryView`).
  Each filed as its own retiring-PR issue.

- **GameState services translate log events into a developer-facing domain
  model with three channels: query, react, and bind. ✅ owner-confirmed
  2026-05-21.** A consumer asking *"what's in inventory right now?"*,
  *"react when a skill XP gain fires,"* or *"bind a UI to the live pin set"*
  gets three different APIs — each shaped for the question — never one
  channel trying to serve all three. The GameState layer exists so consumers
  develop against a stable domain model (`InventoryEvent`, `PinSetChanged`,
  `SkillChange`), not the raw log events the service translates from.
  Companion rule to the consumption-side bullet above: that one tells
  modules what to consume; this one tells services what to expose.

  **The three channels:**

  - **Query** — synchronous state lookup at moment of call.
    `IInventoryView.TryResolve(instanceId, out internalName)`,
    `IPlayerWeatherTracker.Current`, `IPlayerPinTracker.CurrentAreaPins`,
    etc. Returns the current value; no side effects.
  - **React** — event stream, default `ReplayMode.FromSessionStart`.
    Replays every in-session event atomically before live events start,
    so a late-attaching consumer sees the full sequence — not just
    live-from-now. Consumers pass `ReplayMode.LiveOnly` explicitly when
    they genuinely want live-only semantics (e.g. a UI showing
    *"changes since the panel opened"*). The handler runs on the
    service's ingestion thread; consumers marshal off-thread for
    non-trivial work.
  - **Bind** — for WPF data-binding. Collection-shaped services expose
    `IReadOnlyObservableCollection<T>`; single-value services expose
    `INotifyPropertyChanged` properties. Mutations are marshaled to the
    UI dispatcher inside the service, so UI consumers bind directly in
    XAML without consumer-side dispatching. Headless / test contexts
    where `Application.Current?.Dispatcher` is null fall back to inline
    mutation (mirrors the existing
    [`VendorIngestionService` pattern](../src/Smaug.Module/State/VendorIngestionService.cs)).

  **Why three channels.** Each maps to a structurally different question:
  *Query* = "what's the current state?" (point-in-time); *React* =
  "what's happening?" (ordered event log); *Bind* = "how does this view
  stay in sync?" (declarative, UI-safe). Conflating them cost concretely.
  Pre-#602/#659, `IInventoryService.Subscribe` tried to serve both
  *current-state-replay* (synthesizing `Added` events for live items) and
  *event-log-replay* (live events forward) through one API — and silently
  lost pre-attach `Deleted` and `StackChanged` events. Three REACT consumers
  silently degraded as a result:

  - [`Samwise.GardenIngestionService.OnInventoryEvent`](../src/Samwise.Module/State/GardenIngestionService.cs)
    lost `_itemIdToCrop` map entries for items added-then-deleted before
    Mithril attached.
  - [Arwen's gift calibration](../src/Arwen.Module/Domain/CalibrationService.cs)
    would have lost every gift made before Mithril attached in the current PG
    session — surfaced in [#582](https://github.com/moumantai-gg/mithril/issues/582)'s
    replay-contract analysis.
  - [`Legolas.MotherlodeMeasurementCoordinator`](../src/Legolas.Module/Services/MotherlodeMeasurementCoordinator.cs)
    silently missed dig-completion signals for digs completing before
    Mithril attached — the inline comment cited it consumes "the
    IInventoryService Deleted event" (legacy, pre-#602) as the
    authoritative completion signal.

  Three sharp channels with distinct semantics retired the bug class
  structurally (delivered via the #602 split + #659 shim retirement —
  the post-split surfaces today are `IPlayerInventoryState`,
  `IChatInventoryState`, and the composing `IInventoryView`).

  **Anti-pattern (flag immediately).** A consumer manually building
  observable state from a GameState service's event stream
  (`_sub = service.Subscribe(OnEvent); ... _items.Add(...)`) is doing
  the service's job. If the service exposes a domain model, the model
  should be observable; consumers shouldn't reinvent the state
  assembly. Today this pattern repeats in
  [`Palantir.LiveInventoryViewModel`](../src/Palantir.Module/ViewModels/LiveInventoryViewModel.cs),
  [`Palantir.WorldStateViewModel`](../src/Palantir.Module/ViewModels/WorldStateViewModel.cs),
  and several Legolas ViewModels — each a candidate for a Bind-surface
  migration once the relevant service grows the channel. Filing those
  migrations rather than appending to this charter.

  **What this implies for L2 / log-parsing internals.** L2 recognizers,
  L1 envelopes, L0 timestamps — all internal to GameState services.
  Modules never see them. The service surface is the abstraction
  boundary; the GameState layer earns its keep by translating raw log
  flux into a stable domain model on the other side. This shapes the
  L2 spec direction ([#574](https://github.com/moumantai-gg/mithril/issues/574)):
  cross-cutting recognizers + event types live in `Mithril.GameState`
  alongside the service that owns them; module consumers consume the
  service surface, never the L2 dispatch directly.

---

> **Coverage:** all 12 `*.Module` projects are charactered (owner-confirmed
> 2026-05-16). `Mithril.Shared` / `Mithril.GameState` / `Mithril.Shell` are shared
> libraries, not modules — they appear in the data-domain table as *data owners*, not
> as charter entries.

## Samwise — garden tracker

- **Owns: ✅ confirmed (owner, 2026-05-16)** — Samwise is *first and foremost a garden
  tracker*. It interprets `Player.log` to track what the player has planted, tracks
  crop state transitions, and raises alarms **so plants don't die**. The authority on
  *what is planted* and *where each plot is in its lifecycle*, derived from the log.
  Alarms are loss-prevention, not merely a ripeness/harvest-ready notification.
  (See `samwise-parser` memory: identification trade-offs, alias-learning convergence.)
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16) — soft/empty** — *Crop-selection / yield
    optimization.* Not prohibited (Samwise *could* suggest what to plant) but a
    deliberate non-feature: PG gardening is intentionally trivial (plant → water when
    thirsty → fertilize when hungry → collect when ready), so there is nothing to
    optimize.
  - **✅ confirmed (owner, 2026-05-16)** — *Anything recipe/crafting* (crops as
    ingredients, cooking with crops, **and Gardening crafting recipes** — see data
    note) → Elrond/Celebrimbor, per the cross-cutting rule.
- **Reference data:** `Items` (seed→crop identity via `ItemsByInternalName`).
- **Data note (verified v470, 2026-05-16; refined by owner):** Gardening's **primary**
  XP source is the planting loop (plant → water → fertilize → harvest) — owner-confirmed
  it grants XP. A **secondary** source is a handful of Gardening *crafting* recipes
  (`BasicFertilizer1/2/3`, …), which are in `recipes.json` with full
  `RewardSkillXp`/first-time/drop-off and so fall to Elrond via the cross-cutting rule
  like any craft skill. Structural consequence: Elrond is recipe-anchored, so it sees
  *only the secondary slice* — Gardening's primary progression sits outside both
  Samwise's charter (tracker, not advisor) and Elrond's (recipe-anchored). **Verification
  owed:** the planting-loop per-action XP values do not appear to be in reference data
  (owner's observation; not exhaustively checked). If confirmed absent, no data-feasible
  XP-driven gardening advisor is possible for the primary source — which is the
  data-level reason crop-selection optimization stays a soft non-feature, not a
  temporary gap.

## Pippin — Gourmand support (food-variety + provenance)

- **Owns: ✅ confirmed (owner, 2026-05-16)** — supplementing PG's **Gourmand** skill,
  which levels from eating foods *not previously eaten*. Pippin tracks the per-character
  set of foods already eaten against the food catalog and surfaces the not-yet-eaten
  foods so the player can progress Gourmand. **Food provenance is in scope** — *where*
  a not-yet-eaten food can be obtained is part of Pippin's responsibility. (This is
  food-specific provenance in service of Gourmand; it does not collide with
  Silmarillion's *generic* reference browsing — different surface, different purpose.)
  About food *novelty/variety*, not buffs.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Food buffs.* Pippin tracks nothing about
    buff effects or buff uptime. Gourmand novelty only.
  - **✅ confirmed** — *Crafting food itself* → Celebrimbor, per the cross-cutting
    recipe/crafting rule. (Pippin may *point at* where a food comes from, including
    "it's crafted"; it does not own the crafting.)
- **Primary data source: ✅ owner-stated (2026-05-16)** — the **in-game reporting
  tool**'s character report (the eaten-food record). That report is *dumped into*
  `Player.log` — where Pippin reads it today — and is *also* written as a standalone
  raw text file that Pippin does **not** currently consume (a potentially cleaner
  input). Not the CDN. (An earlier draft's flat "*not* `Player.log`" was wrong —
  corrected: the report content lands in `Player.log`.)
- **Reference data:** CDN `Items` (`FoodDesc`) for the food catalog/identity — used to
  compute not-yet-eaten and to resolve where un-eaten foods come from.
- **Endorsed opportunity → Tracked: #348** — combine the two: "what haven't I eaten
  *and where do I find it*." Owner-endorsed (2026-05-16) as good value; filed as #348.
  (Charter carries the stable pointer; the issue holds the spec.)

## Legolas — surveying & route optimization

- **Owns: ✅ confirmed (owner, 2026-05-16; narrowed 2026-05-28)** — the Survey
  FSM, the Motherlode FSM (with its motherlode-specific position anchor +
  `CoordinateProjector.Refit` for converting `[Status]` relative offsets to
  world coords), survey-run route optimization, and the survey/motherlode UX
  (cold-start calibration wizard, survey-run line, motherlode dig flow). See
  `legolas-overview` doc + `legolas_position_anchor_constraint` memory:
  surveying produces inventory items.
  - **Update — #454 (2026-05-18):** survey placement is now **absolute**
    (`ProcessMapFx`); the relative-offset model, the position anchor, and
    `CoordinateProjector.Refit` are retired *for Survey*. The
    position-anchor/projector survives **Motherlode-only**. Cold-start
    calibration becomes a wizard `Calibrating` step over the map overlay
    (#460). The "movement invalidates the projector" caveat no longer
    applies to Survey.
  - **Update — 2026-05-28 (owner-confirmed):** the **DirectX overlay surface**,
    the **world→screen projection** that drives it, and the **per-area map
    calibration data + transform** all lift out of Legolas into shared infra
    (`Mithril.Overlay`, `Mithril.MapCalibration` — see the *Cross-module shared
    infra* section below). The lifts retire Legolas's status as the de facto
    home for "anything map/projection-shaped" and free Gwaihir (issue #830 §3a/§3b)
    and Silmarillion (issue #830 §3c) to render world coords on either surface
    without taking a Legolas dependency. Legolas becomes the first consumer of
    the `Mithril.Overlay` platform: its Survey/Motherlode scene — markers, route
    line, guidance ring, pixel nudges, calibration placement — draws through the
    overlay's **scene hook** (relocating today's `PinSceneRenderer` pass), rather
    than owning the surface; the simpler world-coord **marker registry** is the
    convenience path Gwaihir uses. Camera-state predecessor: **resolved** via
    closed [#832](https://github.com/moumantai-gg/mithril/issues/832) (Decision
    C) — the projection is map-aligned (not 3D) and consumes `IPositionState` +
    `IAreaState` directly per the consumption-side rule; no `IPlayerCameraState`
    producer is needed (see the `Mithril.Overlay` entry below for the full
    reasoning). The Motherlode-only `CoordinateProjector` / position
    anchor (Status-line offsets → world coords) is a **separate** projection
    from world→screen and **stays Legolas's**.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *General geographic reference* (where
    places/landmarks are). Areas/landmarks browsing is Silmarillion.
  - **✅ confirmed (owner, 2026-05-16)** — *Item acquisition guidance.*
  - **✅ confirmed (owner, 2026-05-28)** — *The DirectX overlay surface +
    windowing layer.* Lifted to `Mithril.Overlay` (shared infra): the
    transparent-window host (click-through, foreground gating, layout, resize,
    DPI) *and* the D2D drawing surface. Legolas *draws on* it via the scene hook;
    it does not own the substrate.
  - **✅ confirmed (owner, 2026-05-28)** — *World→screen projection.* The
    map-aligned transform lives in `Mithril.MapCalibration` and is surfaced to
    overlay consumers through the scene hook's `Project(...)` helper. Legolas
    draws its scene through the hook; the projection math is shared infra.
    (Motherlode's Status-line-offsets-to-world-coords `CoordinateProjector` is a
    different projection and stays Legolas's per the Owns line above.)
  - **✅ confirmed (owner, 2026-05-28; extended 2026-05-29)** — *Per-area map
    calibration data + transform — and the calibration store + session.* Lifted
    to `Mithril.MapCalibration` (shared infra). Legolas's accumulated per-area
    anchor observations become one of several anchor sources the shared service
    stacks (bundled baseline + per-user refinement + future community-sync);
    Legolas no longer owns the catalogue, the `WorldToWindow` / `WindowToWorld`
    surface, the calibration store, or the headless solve *session*. What Legolas
    **keeps** is the overlay-flavored calibration *placement UX* (the Drop/Pair
    click-through walkthrough), which feeds the shared session — a different
    surface (the static texture) supplies its own placement UX.
- **Reference data:** `Items` (survey items).

## Arwen — NPC favor & gift tracking

- **Owns: ✅ confirmed (owner, 2026-05-16)** — per-NPC favor state, gift-rate
  calibration, and gift-outcome tracking.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *NPC location/services browsing.* The NPC
    reference card is Silmarillion.
  - **✅ confirmed (owner, 2026-05-16)** — *Quest-giving / favor-quest adjudication.*
- **Reference data:** `Npcs` (preferences), `Items` (gift identity).

## Elrond — skill leveling advisor

- **Owns: ✅ confirmed (owner, 2026-05-16)** — (1) the **player's progression state**
  — learned skills, skill progress, and known recipes — which is the *constraint set*
  the leveling calculator runs against; (2) per-recipe leveling math (effective XP,
  first-time bonuses, drop-off) and the optimal grind path *within a skill*; (3) the
  cookbook view. Scope of "player state" here is the **progression facet only** —
  inventory/storage state is Bilbo's, raw character-data parsing is shared
  (`ICharacterDataService`); Elrond owns the progression model the calculator
  constrains on.
- **Does NOT own:**
  - **✅ confirmed** — *Non-recipe skills.* Recipe-anchored by design: skills without
    recipes (combat/gathering/etc.) intentionally never appear; Elrond cannot advise
    on them. (Design doc + `elrond_recipe_anchored_design` memory.)
  - **✅ confirmed (by entailment, 2026-05-16)** — *Multi-recipe shopping / inventory.*
    Celebrimbor's Owns is owner-confirmed as exactly this; Elrond not owning it is the
    direct complement.
  - **✅ confirmed (owner, 2026-05-16) — provisional/future** — *The skill-XP **math**,
    post-#225.* Owner-confirmed technically correct: the math is slated to lift into
    shared `Mithril.Leveling`; after #225 Elrond *consumes* that engine, not owns the
    math. Clean split: **math → shared `Mithril.Leveling`; the player-progression-state
    constraints it runs against stay Elrond's** (see Owns). Contingent on #225 landing.
- **Reference data:** `Recipes`, `Skills`, `XpTables`.

## Gandalf — timers & repeatable-quest cooldowns

- **Owns: time.** When you completed a repeatable, when it is re-attemptable, and
  user-defined timers/alarms. The authority on *temporal bookkeeping* only.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Quest eligibility / "can you do this now".*
    The game engine is the source of truth for requirements; Gandalf never adjudicates
    them from `Quest.Requirements`. Recomputing eligibility would duplicate authority it
    deliberately disclaims and risk disagreeing with the engine. This is the canonical
    example of why charters exist.
- **Reference data:** `Quests` (reuse-time fields for cooldown anchoring only),
  `Strings`/`Areas` (display resolution).

## Bilbo — storage/inventory management

- **Owns: ✅ confirmed (owner, 2026-05-16)** — the **browse/evaluate *surface* over
  the static storage export**: (1) present/query the export, location chips; (2) a
  read-only **craftability evaluator** — "what is craftable *right now* given current
  export state." Bilbo owns the *surface*, **not the data**: the export is
  `IActiveCharacterService` / `StorageReport` (Mithril.Shared, single source of truth);
  Bilbo consumes it.
- **Does NOT own:**
  - **✅ confirmed (verified 2026-05-16; refreshed 2026-05-23 post-#602/#753)** —
    *Live inventory simulation.* The live `instanceId→item` + stack-size model
    is `IInventoryView` in **Mithril.GameState** — composes `IPlayerInventoryState`
    (tails `ProcessAddItem/…`) with `IChatInventoryState` (tails chat `[Status]`);
    its user-facing surface is **Palantir** (`LiveInventoryView`), not Bilbo.
    Bilbo is export-*snapshot*, not live.
  - **✅ confirmed** — *Craft planning* (shopping lists, what-to-acquire, quantity
    math) → Celebrimbor. "What can I make from what I hold *now*" is Bilbo (a state
    property); "what do I need to make N of X" is Celebrimbor (a plan). See the
    recipe/crafting carve-out above.
  - **✅ confirmed** — *Leveling advice* → Elrond (cross-cutting rule).
- **Reference data:** `Items`, `Recipes`, keyword index — joined against the export
  state to compute immediate craftability.

## Silmarillion — reference-data browser

- **Owns: ✅ confirmed (owner, 2026-05-16)** — *Silmarillion is **the** reference-data
  browser*: the dedicated, authoritative surface for browsing and cross-linking **all**
  reference data (master-detail, navigator, provenance popups). Other modules may
  render reference data incidentally to their own purpose, but "being the browser" is
  Silmarillion's charter alone. Field-level scope:
  [silmarillion-field-coverage.md](silmarillion-field-coverage.md); tab scope:
  [roadmaps/silmarillion.md](roadmaps/silmarillion.md).
- **In scope — owner-ratified (2026-05-17; supersedes the 2026-05-16 "popup"
  framing):** Silmarillion owns a dedicated **Treasure System tab** (**#412**) — a
  browse/query surface over `tsysclientinfo` (the **Power** entity: skill-tagged,
  tiered) + `tsysprofiles` (40 named **Profile** pools). PG's own term is "treasure
  system"; the tab uses it (a reference browser mirrors source vocabulary). Recipe
  detail (**#214**, rescoped) **deep-links into** this tab — the tab is the *single
  render site* for TSys; recipe detail does **not** re-render the pool inline (no
  divergence surface). Silmarillion is the **master** view; **Celebrimbor is the
  planning-narrowed slice** of the *same* `ResultEffectsParser`/pool data (the pool
  for recipes in your craft plan).
  - *Data shape — verified v470 (`tsysclientinfo`/`tsysprofiles`, 2026-05-17):* both
    sources are a faceted entity table + named-set table — **card-shaped and
    crystal-free**. The earlier roadmap Bucket-D "calculator-shaped, not a Silmarillion
    tab" verdict reasoned from the raw-JSON label, not the contents; **overturned**
    (recorded in [roadmaps/silmarillion.md](roadmaps/silmarillion.md) + History below).
  - *Pool/recipe linkage — verified `recipes.json`/`items.json`/`ResultEffectsParser`
    2026-05-16:* the `*E` *"(enchanted)"* recipe's `TSysCraftedEquipment(template)`
    ResultEffect → `TryBuildCraftedEquipmentPool` → the crafted-equipment template's
    `Item.TSysProfile` → `IReferenceDataService.Profiles`. #214's deep-link prefills
    the tab's query to that profile.
  - *Crystal scoping is a recipe-crafting-path concern — **not part of the tab
    browse**.* Retained because it is settled and still true *for enchanted crafting*:
    each `Crystal`-keyword item carries the enchantment family on its own item record
    (`Item.DynamicCraftingSummary` *"…<family> enchantments"*; an `Item.Description`
    `"Associated Primary Skill: <X>"` line on *some* crystals — Moonstone, LapisLazuli,
    Tsavorite — but **not** others like Rubywall, and the family term is often prose,
    not a `skills.json` entity — *"survival-related"* has no `Survival` skill). It
    degrades to display text per the chip-degradation rule; **the Treasure System tab
    itself never touches crystals** (`tsysclientinfo`/`tsysprofiles` contain none).
  - *Data-availability ceiling (unchanged, owner 2026-05-16):* roll **resolution** —
    probabilities, the precise rolled power — is **not in CDN data at all** (same class
    as Samwise's gardening-XP absence). The tab shows what the system is *composed of*,
    never how a roll resolves. A data ceiling, not a charter bar. Relates to #214/#412.
- **Does NOT own:**
  - **✅ confirmed** — *Computation/simulation.* It is a browser, not a calculator.
    Calculators are Elrond/Celebrimbor; TSys/power **roll calculation** (resolution
    math — probabilities, the rolled power) stays Celebrimbor's territory.
    **Updated 2026-05-17:** the roadmap's *"not a Silmarillion **tab**"* clause is
    **overturned** — the *browse* surface (#412) is Silmarillion's; only the
    *calculation* is Celebrimbor's. **Carve-out (owner 2026-05-16, realised 2026-05-17):**
    *displaying* parsed treasure data / pools via the shared `ResultEffectsParser` and
    browsing `tsysclientinfo`/`tsysprofiles` is browsing, not calculation — the
    original #214 carve-out, now a dedicated tab (#412). "Does not calculate" ≠
    "cannot render parser output or browse TSys data."
- **Reference data:** effectively all sources (it is the browser), per the bucketing
  rule in the roadmap.

## Celebrimbor — crafting planner

- **Owns: ✅ confirmed (owner, 2026-05-16)** — build a shopping list and craft a given
  set of targets, accounting for what is on hand. Scope is *exactly* "what is needed to
  make N units of X": multi-recipe selection, shopping-list aggregation, on-hand
  cross-reference, the craft-step ladder. **It accounts for no logic beyond the
  quantity math** — no optimization, no strategy, no decision about *what* or *how
  much* to make. (See [roadmaps/celebrimbor.md](roadmaps/celebrimbor.md).)
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Any logic beyond "make N of X".* Leveling
    optimization, what-to-craft strategy, ROI — all out. Celebrimbor only ever
    *consumes* a target list; whatever decides that list (Elrond / the #227 cross-skill
    planner) is upstream. #228's "plan-aware craft list" means Celebrimbor *receives* a
    computed plan as targets — it does not compute it.
  - **✅ confirmed (owner, 2026-05-16)** — *Reference browsing.* Silmarillion is **the**
    reference browser; Celebrimbor rendering recipe/effect data is **incidental to
    planning, not a co-owned browsing role** — "just another place the data is
    displayed." It shows a recipe in service of a craft plan and previews its treasure
    (ResultEffects) outcomes — including the TSys augment **pool** for that recipe.
    Celebrimbor's view is the **planning-narrowed slice** of the master catalogue
    Silmarillion browses: the *same* shared `ResultEffectsParser` pool surface, scoped
    to the recipes in the plan rather than the full list. The capability is **shared
    infra**, owned by neither; Silmarillion does not consume it yet — #214 (*gap, not
    boundary*; owner-endorsed, see Silmarillion's *In scope*). (Displaying data ≠
    owning the browser role — see the data-display cross-cutting rule above.)
- **Reference data:** `Recipes`, `Items`, `ResultEffectsParser` previews, `Areas`
  (source resolution), inventory.

## Palantir — debug / dev tools

- **Owns: ✅ confirmed (owner, 2026-05-16)** — a **debug module** (not player-facing):
  the browser/inspector over **Mithril.GameState** state (incl. the live-inventory
  view, `LiveInventoryView` over `IInventoryView`), plus dev tools — currently a
  **badge tester**. A development/diagnostic surface.
- **Does NOT own:**
  - **✅ confirmed (owner, 2026-05-16)** — *Any player-facing feature.* Debug-only by
    charter: exposes engine/GameState internals for development, never a user workflow.
    (Hard boundary, by the module's nature.)
- **Data:** reads Mithril.GameState services (`IInventoryView`, …) as a debug
  surface — owns no player data domain of its own.

## Smaug — NPC stores & sale economics

- **Owns: ✅ confirmed (owner, 2026-05-16)** — viewing NPC stores; the **gold value of
  the player's inv/storage**; **mining store states** (capturing NPC store
  contents/state); **sale min/maxing** (optimising what/where to sell).
- **Does NOT own:**
  - **✅ confirmed** — *NPC favor / gifts* → Arwen. Smaug is NPC *economics*
    (buy/sell, store contents, gold) — a distinct purpose from Arwen's relationship/favor.
  - **✅ confirmed** — *Inv/storage browsing & craftability* → Bilbo. Smaug *consumes*
    inv/storage state to value it in gold / optimise sales (a distinct purpose-surface
    over the same shared data); it does not own the inventory browser.
  - ⚠️ *NPC gold tracking* — owner-noted as a **possible future**, not current scope;
    provisional until built (cf. the Elrond/#225 line).
- **Data:** NPC store state — **Smaug mines it itself** (owns both acquisition and
  surface for this domain). Inv/storage from the shared owners
  (`IActiveCharacterService`/`StorageReport`) + `Item.Value` for gold valuation.

## Saruman — Words of Power

- **Owns: ✅ confirmed (owner, 2026-05-16)** — the **Words of Power** mechanic: a WoP
  is an RNG'd string gained via a recipe or item; typing it into chat activates
  effects. Saruman **monitors the logs** to detect when a WoP is *learned* and tracks
  the known set, and detects when a known word is *uttered* while logged in and
  **strikes it from the record** (consumed). Owns the player's WoP known-state
  lifecycle.
- **Does NOT own:**
  - **✅ confirmed** — *The recipe/item as a WoP source in reference data* — that's
    `ResultEffectsParser` `WordOfPowerPreview` (`DiscoverWordOfPower{N}`), browsable in
    Silmarillion. Saruman owns the *player's* learned/known/consumed state, not the
    reference datum "this recipe grants WoP N." (Same data-owner-vs-surface split.)
- **Data source: ✅ owner-stated (2026-05-16)** — log monitoring: learn-detection +
  utterance/consumption while logged in. ⚠️ Channel split inferred (not owner-stated):
  learn likely `Player.log` (recipe/item), utterance the chat log (typed into chat) —
  verify when the module is next touched.

## Radagast — environment & world-state (player-facing)

> **(Owner-confirmed 2026-05-19 — binding. Radagast is a *proposed* module, not
> yet a `*.Module` project, so it does not count against the "all 12 projects
> charactered" coverage statement above.)**

- **Owns: ✅ confirmed (owner, 2026-05-19)** — the
  **player-facing surface** for server-keyed environment state: moon phase,
  per-map weather, and the server-keyed gardening buff. Two halves: **display**
  the live state, and the **community consume→display** path — surface the
  aggregated community observations so a player sees current server state *before
  their own client observes it*. Owns the **environment *slice*** of the
  community-data pipeline — the `"radagast"` key, its payload schema (what an
  environment observation is, server/shard-keyed), and the
  publish-trigger/consume-render policy — parallel to how Samwise/Arwen/Smaug/
  Gandalf own *their* calibration keys. Owns *what is shared and when*, not the
  wire.
- **Does NOT own:**
  - **✅ (cross-cutting data-owner rule) — the producers.**
    `IPlayerCelestialState` and `IPlayerWeatherTracker` live in
    **Mithril.GameState** and are the single source of truth; Radagast
    *subscribes*, it does not parse `Player.log`. A **gardening-buff producer
    does not yet exist** — when built it belongs in Mithril.GameState (parser →
    `IPlayerGardeningBuffState` → hosted service, parallel to Celestial/Weather),
    **never in Radagast**. Radagast owns the *surface* + *community sync*, not
    the log-parsing/producer.
  - **✅ (cross-cutting data-owner rule) — the client/server community-sync
    infra.** The serverless publish + consume *transport* (the
    raw.githubusercontent.com aggregation pipeline — `CommunityCalibrationService`
    / `MithrilRepository` and its future publish half) is a **shared Mithril
    dependency**, owned by *neither* Radagast nor any consuming module — the
    single source of truth all calibration keys ride on. Radagast defines and
    consumes its `"radagast"` slice; it does not own the wire, the aggregation,
    or the publish mechanism. Same split as the GameState producers above:
    shared transport = data owner, Radagast = surface + payload slice.
  - **✅ (hard) vs Palantir** — Palantir's `WorldStateView` already renders
    moon/weather, but Palantir is **debug-only by charter** (a GameState/engine
    inspector, not a user workflow). Radagast is the **player-facing** surface
    over the *same* GameState producers. Same data owner, two surfaces, different
    purpose — the data-owner-vs-surface rule, not turf. Radagast does not
    own/replace Palantir's debug inspector.
  - **✅ (hard) vs Samwise** — Samwise owns *what is planted and each plot's
    lifecycle* (loss-prevention alarms). Radagast owns *server-wide environment
    state*; the gardening buff is a server-keyed growth modifier, **not** plot
    state. Samwise may **consume** Radagast's buff state to inform growth-rate
    prediction/calibration; it never senses or displays the buff. Radagast never
    tracks plots.
  - **soft/empty** — *Environment-derived advice* ("plant now, the buff is
    active"). Radagast surfaces state; it does not optimize gardening (mirrors
    Samwise's soft non-feature — PG gardening is intentionally trivial).
- **Data sources:** Mithril.GameState producers (`IPlayerCelestialState`,
  `IPlayerWeatherTracker`, future `IPlayerGardeningBuffState`); the community
  aggregate via the existing `CommunityCalibrationService` / `MithrilRepository`
  raw.githubusercontent.com pattern (new `"radagast"` key + payload schema,
  server/shard-keyed). No CDN reference data.
- **Open (for the build issue, not charter):** the serverless *publish* half is
  unbuilt — and is **shared-infra work** (extending `CommunityCalibrationService`
  / `MithrilRepository`), not Radagast's, though Radagast is its first publishing
  consumer; the community payload must be **PG-server/shard-keyed** so
  cross-server observations aren't blended; the gardening-buff log grammar is not
  yet decoded.

## Gwaihir — community POI / pin sharing (player-facing)

> **(Owner-confirmed 2026-05-28 — binding. Gwaihir is a *proposed* module, not
> yet a `*.Module` project, so it does not count against the "all 12 projects
> charactered" coverage statement above.)**

- **Owns: ✅ confirmed (owner, 2026-05-28)** — the **player-facing surface** for
  shared points-of-interest in the game world. Two halves: **community pin
  repo** (issue [#830](https://github.com/moumantai-gg/mithril/issues/830) §3a —
  serverless community-aggregated POI dataset parallel to `mithril-calibration`,
  shape `(areaKey, worldX, worldZ, label, author, tags)`) and **`mithril://`
  POI URLs** (issue #830 §3b — stateless URL encoding
  `(areaKey, worldX, worldZ, label)` for peer-to-peer pin handoff, no hosting
  infra needed). Owns the **POI *slice*** of the community-data pipeline — the
  `"gwaihir"` key, its payload schema (server/shard-keyed pin record), and the
  publish-trigger / consume-render policy — parallel to how Samwise / Arwen /
  Smaug / Gandalf / Radagast own *their* community-sync keys. Owns *what is
  shared and when*, not the wire and not the renderer.
- **Does NOT own:**
  - **✅ (cross-cutting shared-infra rule) — the rendering substrate.**
    `Mithril.MapAssets` (PNG extraction, #830 Tier 1), `Mithril.MapCalibration`
    (per-area world ↔ map-pixel transform), and `Mithril.Overlay` (DirectX
    surface + windowing layer + scene hook + world-coord marker registry) are
    shared infra owned by *no module*. Gwaihir hands `(areaKey, worldX, worldZ, …)`
    to either rendering surface — the static map-view control (MapAssets +
    MapCalibration) or the live in-game overlay (Mithril.Overlay) — and the
    right thing happens. World coord is the universal POI currency; rendering
    surface is a downstream choice the user makes per pin. Owning the POI
    dataset slice ≠ owning the renderer. Gwaihir consumes whichever surface's
    draw path fits (the overlay's world-coord **marker registry** for the simple
    POI-pin case; the static map-view control for the texture) and — when a
    target area isn't calibrated yet — the **shared calibration session**
    (`Mithril.MapCalibration`), supplying its own surface-appropriate placement
    UX. It implements none of these itself.
  - **✅ (cross-cutting data-owner rule) — the community-sync transport.** The
    serverless publish + consume *transport* (the raw.githubusercontent.com
    aggregation pipeline — `CommunityCalibrationService` / `MithrilRepository`
    and its future publish half) is **shared Mithril infra**, owned by
    *neither* Gwaihir nor any consuming module. Gwaihir defines and consumes
    its `"gwaihir"` slice; it does not own the wire, the aggregation, or the
    publish mechanism. Same split as Radagast.
  - **✅ (hard) vs Silmarillion (#830 §3c — area-page map background).**
    Silmarillion is **the** reference browser; an area-page map rendering
    landmarks / NPCs / quest-givers over the extracted PNG is a Silmarillion
    surface that consumes the *same* shared infra (MapAssets + MapCalibration
    + the map-view control). Gwaihir's surface is the community-POI dataset;
    Silmarillion's is the reference catalogue. Same rendering substrate, two
    surfaces, different data domain — the data-owner-vs-surface rule.
  - **✅ (hard) vs Legolas.** Legolas's in-game survey/motherlode markers,
    survey-run route line, motherlode wizard, and Motherlode-only
    `CoordinateProjector` are Legolas-specific consumers of `Mithril.Overlay`
    on top of Legolas-specific FSMs. Gwaihir is a separate consumer of the
    same overlay marker API for a different purpose (community POIs). Both
    render via shared infra; neither owns the other's UX.
  - **✅ (hard) — position-data ceiling.** Gwaihir does **not** track *which*
    player is *where* — there is no live player position stream (see issue
    #830's position-data ceiling discussion). Pins are static world coords
    authored at click-time (`Mithril.MapCalibration.WindowToWorld` on a map
    click, or accepted from a `mithril://` URL) and rendered at static world
    coords on either surface. "Live following player marker" is foreclosed by
    data, not charter.
  - **soft/empty** — *Editing / curating the community repo from inside
    Mithril.* Author-side mutation is out-of-band (PR to the community repo,
    parallel to `mithril-calibration`); Gwaihir is read + publish-trigger only,
    mirroring Samwise / Arwen / Smaug / Gandalf / Radagast calibration-key
    patterns.
- **Data sources:** the community aggregate via the existing
  `CommunityCalibrationService` / `MithrilRepository`
  raw.githubusercontent.com pattern (new `"gwaihir"` key + payload schema,
  server/shard-keyed); shared rendering infra (`Mithril.MapAssets` /
  `Mithril.MapCalibration` / `Mithril.Overlay`). No CDN reference data.
- **Open (for the build issue, not charter):** the three shared-infra lifts
  (`Mithril.MapAssets` substrate per #830 Tier 1; `Mithril.MapCalibration`
  lifted from Legolas's per-area anchor catalogue; `Mithril.Overlay` lifted
  from Legolas's DirectX surface + projection) are **sequenced predecessors**.
  The `Mithril.Overlay` projection lift in turn requires an `IPlayerCameraState`
  GameState producer first (player position, view direction, FOV) so the
  projection consumes the camera signal via the GameState consumption-side
  rule rather than re-parsing raw logs. The community-sync publish half
  (shared infra, currently unbuilt; Radagast is its first publishing consumer)
  must also land before community-pin publishing works — Gwaihir adds a second
  publishing consumer. The community payload must be **PG-server/shard-keyed**.
  The per-pin accuracy ceiling (`legolas_calibration_findings`,
  [PR #449](https://github.com/moumantai-gg/mithril/pull/449)) is **detection
  precision + zoom handling**, not any renderer warp (the old "±10% non-affine"
  band was disproven — it was an operational Survey-pipeline artifact, and the
  renderer is a clean isotropic similarity). "approximate location" UX is still
  the honest default for community/derived pins, and `Mithril.MapCalibration`
  should expose the residual estimate so Gwaihir's UX degrades honestly.

---

## Cross-module shared infra (charter-adjacent)

Some responsibilities are deliberately being lifted *out* of modules into shared
libraries; the charter follows the code:

- **`ResultEffectsParser` (Mithril.Shared) — shipped, shared.** Parses recipe
  treasure-effect strings into typed previews. Owned by *neither* recipe-displaying
  module: consumed by Celebrimbor today, by Silmarillion per #214. The canonical
  "shared infra, not module turf" case — effect *display* is appropriate in any
  recipe surface; the parser is the single source of truth both lean on.
- **`Mithril.Leveling` (#225)** — skill-XP **math**, lifted from Elrond; future owner
  of the math both Elrond and the #227 planner consume. **Player-progression state
  (learned skills, progress, known recipes) does *not* lift — it stays Elrond's**, fed
  into the calculator as its constraint set (owner-confirmed 2026-05-16).
- **Shared demand-driven recipe expander (#226, supersedes #121)** — generalises
  Celebrimbor's aggregator; future shared consumer set = Bilbo + Elrond + #227 planner.
- **`PrereqRecipe` resolver (latent, unfiled)** — three surfaces want the same
  prereq-chain modelling: #341 (Silmarillion *displays*), Celebrimbor §2 (*visualises*),
  #227 (*gates on*). If the planner work lands first, downstream surfaces should consume
  its resolver rather than re-derive.
- **Community-sync transport (`CommunityCalibrationService` /
  `MithrilRepository`, Mithril.Shared) — consume half shipped, publish half
  unbuilt.** The serverless raw.githubusercontent.com aggregation pipeline that
  every calibration key (`samwise`/`arwen`/`smaug`/`gandalf`, and proposed
  `radagast`/`gwaihir`) rides on. Owned by *no module*: modules own their key +
  payload slice and `Subscribe`/publish; the transport, aggregation, and
  (future) publish mechanism are the shared single source of truth. The publish
  half is the unbuilt extension — owned here, not by Radagast (its first
  publishing consumer) or Gwaihir (its second). Parallel to the
  data-owner-vs-surface rule for live state.
- **`Mithril.MapAssets` — proposed substrate
  (issue [#830](https://github.com/moumantai-gg/mithril/issues/830) Tier 1; owner-confirmed 2026-05-28).**
  Extracts per-area map PNGs from the user's local Steam PG install via Unity
  AssetBundle parsing (`AssetsTools.NET`); mtime-keyed cache under
  `%LocalAppData%/Mithril/maps/`. Surface:
  `IMapAssetService.TryGetMapPngPathAsync(addressableKey, ct) → string?`. Owned
  by *no module* — every map-rendering consumer (the WPF map-view control,
  Silmarillion's area page per #830 §3c, Gwaihir's static-pin surface) pulls
  the PNG through this service. Steam-only by substrate; fails informatively
  when PG isn't a Steam install. See #830 for the Tier 1 build spec.
- **`Mithril.MapCalibration` — proposed shared transform
  (lift from Legolas; owner-confirmed 2026-05-28).** Per-area world ↔
  map-PNG-pixel transform, bidirectional. Surface:
  `IMapCalibrationService.WorldToWindow(areaKey, (x, z)) → Point?` +
  `WindowToWorld(areaKey, point) → (x, z)?` + `IsCalibrated(areaKey) → bool`.
  Anchor sources stack: bundled baseline (~36-area hand-authored JSON,
  #830 Tier 2a) → per-user refinement from accumulated calibration data
  (Legolas's existing anchor catalogue moves here) → potentially community-sync
  down the road (parallel to existing calibration keys). Surfaces the residual
  estimate so consumers can degrade UX honestly ("approximate location" chip)
  per the real accuracy ceiling — detection precision + zoom handling, not a
  renderer warp (the "±10% non-affine" band was disproven)
  (`legolas_calibration_findings`,
  [PR #449](https://github.com/moumantai-gg/mithril/pull/449)). Owned by
  *no module* — Legolas, Silmarillion (#830 §3c), Gwaihir (#830 §3a/§3b), and
  any future map-rendering consumer share the catalogue.
  **The transform (`WorldToWindow` / `WindowToWorld`) + `LandmarkCalibrationSolver`
  shipped in #836 (closed).** Still owed (reframed 2026-05-29): the
  surface-agnostic **calibration store** (per-area get/save, baseline → per-user
  refinement → community layering) and a headless **calibration *session*
  protocol** (accumulate `(pixel, worldLandmark)` pairs → `LandmarkCalibrationSolver`
  → commit to the store) lift here from Legolas's `AreaCalibrationService`.
  These belong in `MapCalibration`, **not** `Mithril.Overlay`, because they
  serve the static 2D-texture surface (Silmarillion area pages, Gwaihir static
  pins) as well as the live overlay — calibration is upstream of *both*
  rendering targets. What stays **per-surface** is the calibration *placement
  UX*: the overlay's `PinCalibrationCoordinator` Drop/Pair phase split exists
  only because a transparent window's click-through is all-or-nothing, a
  constraint the in-app texture control doesn't have — so each surface
  implements its own placement interaction feeding the shared session, and live
  current-area tracking (from Arda) stays overlay-side. Tracked as a follow-up
  issue (see the *Mithril Roadmap* Project).
- **`Mithril.Overlay` — proposed shared overlay *platform*
  (lift from Legolas; owner-confirmed 2026-05-28; reframed 2026-05-29).** Not a
  registry — a three-layer platform for "any module spins up an in-game overlay
  and draws on it." Owned by *no module*. The layers:
  1. **Windowing-behavior layer** — the transparent topmost window host:
     click-through (`ClickThrough` / `PartialClickThrough`), foreground-focus
     gating (show/hide as the user alt-tabs between game and desktop —
     `ForegroundFocusGate` + `User32Focus`), window layout persistence
     (`WindowLayoutBinder`), resize chrome (`ResizeGrips`), and the
     per-monitor-DPI / `Stretch.Fill` invariant. This hosts **D2D *and*
     plain-WPF overlay content** — Legolas already runs two windows on it today
     (the D2D map overlay *and* the WPF inventory overlay), which is why it's a
     separate layer from the drawing surface, not welded to it.
  2. **D2D drawing surface + scene hook** — the `D3DImage`-backed Direct2D
     surface plus a per-tick **scene-drawer hook** that hands the consumer a raw
     render target + factory + brush cache **and a `Project(areaKey, worldX,
     worldZ) → pixel?` helper** (backed by `Mithril.MapCalibration`). This is
     the *primary* draw API: the consumer draws its whole scene with full
     freedom — world-projected geometry, polylines, pixel-anchored glyphs,
     animation — exactly as today's `PinSceneRenderer` does off the `Render`
     event. `FrameTimeLogger` (generic frame-interval capture) lifts here so any
     consumer can perf-profile the shared surface.
  3. **`IWorldOverlayMarkers`** — a *thin convenience* over the scene hook for
     the dumb-simple "just plot these world points with this style" case;
     consumers hand `(areaKey, worldX, worldZ, style)` and the platform projects
     + draws. Built **on top of** layer 2, not instead of it.
  Consumers pick their layer: Legolas draws its full survey/motherlode scene via
  the hook (layer 2); Gwaihir uses the registry (layer 3) for community POI pins.
  **Calibration is *not* here** — the world→pixel transform, the per-area
  calibration store, and the headless calibration *session* live in
  `Mithril.MapCalibration` (see its entry) because they serve the static
  2D-texture surface too, not just the overlay. The overlay only *consumes* a
  calibration (via the `Project` helper) and *renders* a surface's calibration
  placement pins through the scene hook.
  **Camera-state consumption — `IPositionState` + `IAreaState` from Arda
  (resolved 2026-05-28 via closed
  [#832](https://github.com/moumantai-gg/mithril/issues/832); supersedes the
  initial `IPlayerCameraState` predecessor framing).** The world→screen
  projection used by today's Legolas overlay is map-aligned, not 3D —
  `AreaCalibration.WorldToWindow` (`Mithril.MapCalibration`; lifted in #836) is a per-area 2D similarity transform that
  needs only `(currentArea, playerPosition)`. Both signals already exist in
  Arda as `IAreaState` and `IPositionState`; `Mithril.Overlay` consumes them
  directly per the consumption-side rule. No new GameState producer is
  needed. Two reasons closed the original `IPlayerCameraState` proposal: (1)
  view direction + FOV aren't carried by Player.log
  (`Arda.Dispatch.Verbs.cs` declares no camera-orientation verb), and the
  only off-log channels available — memory hooks, DirectX presentation
  hooks — are foreclosed by PG's anti-injector stance (see
  [[pg-anti-injector-stance]]: PG ships `[ACTk] Injection Detector` by
  intent, today's Mono build runs it inert but a future IL2CPP migration
  silently turns detection on); (2) a position-only `IPlayerCameraState`
  whose only delta from `IPositionState` is stamping the area key was a
  YAGNI seam for a migration we may never do. Build sequence:
  `Mithril.MapCalibration` lift (or in-parallel with stub) → projection +
  DirectX surface lift into `Mithril.Overlay` consuming
  `IPositionState`/`IAreaState` directly → `Mithril.Overlay` published as
  shared infra. Legolas becomes the first consumer: it draws its full
  survey/motherlode scene — pins, route polyline, pixel nudges, the guidance
  ring, calibration-placement pins — through the **scene hook** (layer 2),
  essentially relocating today's `PinSceneRenderer` draw pass onto the shared
  surface, rather than owning the surface. Gwaihir becomes the second consumer
  and uses the **marker registry** (layer 3) for community POI pins. Because
  Legolas draws freeform, the route-polyline, pixel-nudge, and
  pre-calibration-pixel cases — filed as
  [#866](https://github.com/moumantai-gg/mithril/issues/866) /
  [#867](https://github.com/moumantai-gg/mithril/issues/867) /
  [#868](https://github.com/moumantai-gg/mithril/issues/868) while the public
  API was registry-only — need **no** pixel-space marker API; they dissolve into
  the scene hook (route + nudge) and per-surface calibration placement (#868).

## History

- **2026-05-28** — **`Mithril.Overlay` sequenced-predecessor framing revised:
  `IPlayerCameraState` dropped; consume `IPositionState` + `IAreaState` directly
  (✅ owner-confirmed 2026-05-28).** When [#831](https://github.com/moumantai-gg/mithril/pull/831)
  landed earlier today the *Cross-module shared infra → `Mithril.Overlay`* entry
  named `IPlayerCameraState` as a sequenced predecessor (player position + view
  direction + FOV). A planning pass to file the predecessor issue
  ([#832](https://github.com/moumantai-gg/mithril/issues/832), closed without
  code) surfaced two facts: (1) Player.log carries no camera-orientation verb
  — `Arda.Dispatch.Verbs.cs` declares position only — and the off-log channels
  available (memory hook, DirectX presentation hook) are foreclosed by PG's
  anti-injector stance (PG ships ACTk's Injection Detector, currently inert on
  Mono builds but loud about intent — see [[pg-anti-injector-stance]]); (2) the
  remaining position-only flavour of `IPlayerCameraState` was a YAGNI seam over
  the existing `IPositionState` + `IAreaState`. Owner picked the C resolution:
  `Mithril.Overlay` consumes the two existing services directly. The lift's
  "world→screen projection" reduces to `IMapCalibrationService.WorldToWindow`
  — the renderer holds no projection math of its own. Build sequence revised
  to drop the producer step; Legolas's overlay being map-aligned (not 3D)
  makes this both correct and minimal. Surfaced during the issue-filing
  follow-up to #831.
- **2026-05-28** — **Gwaihir charter added + Legolas narrowed + three shared-infra
  lifts proposed (✅ owner-confirmed 2026-05-28).** Working out a module name for
  issue [#830](https://github.com/moumantai-gg/mithril/issues/830) §3a (community
  pin repo) and §3b (`mithril://` POI URLs) opened a broader architectural pass.
  Conclusion: world coord is the universal POI currency; two rendering surfaces
  exist (static map via extracted PNG, live in-game DirectX overlay); any module
  with a world coord should reach either surface without taking a per-module
  dependency. Three shared-infra lifts land in the same charter pass —
  **`Mithril.MapAssets`** (PNG substrate, #830 Tier 1), **`Mithril.MapCalibration`**
  (per-area world ↔ pixel transform, lifted from Legolas's calibration catalogue),
  and **`Mithril.Overlay`** (DirectX surface + world→screen projection + marker
  API, lifted from Legolas's overlay infrastructure). Legolas narrows
  correspondingly: Survey FSM, Motherlode FSM with motherlode-only
  `CoordinateProjector` for Status-offset→world-coord (a *different* projection
  from world→screen, retained), survey-run route optimization, and the
  survey/motherlode UX stay; the DirectX surface, world→screen projection math,
  and per-area calibration *data* all lift. The world→screen projection lift
  surfaces a sequenced predecessor: an `IPlayerCameraState` GameState producer
  (player position + view direction + FOV) must land first so `Mithril.Overlay`
  consumes the camera signal via the GameState consumption-side rule rather than
  re-parsing raw logs in shared infra. Build sequence: `IPlayerCameraState`
  producer → projection + DirectX surface lift into `Mithril.Overlay` →
  calibration lift into `Mithril.MapCalibration` → `Mithril.MapAssets`
  substrate → Gwaihir module. Gwaihir entry follows the Radagast pattern
  (proposed module, not yet a `*.Module` project; does not count against the
  12-project coverage statement). Charter pass disambiguates a naming question
  along the way: the natural Tolkien-metaphor pick **Palantír** was already
  claimed by the debug-tools module; **Gwaihir** (eagle-lord, messenger between
  distant points) lands as the chosen name and slots into the existing
  character-name pattern. The conversation also confirmed Celebrimbor is a
  character (Noldorin smith) — Silmarillion is the lone book/jewel-name outlier,
  not a character/place split. Issue #830 Tier 2 anchor authoring re-targets
  from `Mithril.Reference/BundledData/area-anchors.json` to `Mithril.MapCalibration`;
  worth a comment on #830 once the module name is settled. Issue #830's
  "position-data ceiling" section also undersells the overlay path: *live-following
  the player* remains foreclosed (no live position stream), but *static world
  coords rendered live on the DirectX overlay* is fully supported — the overlay
  refreshes at game framerate and the projection is solved, so Gwaihir pins
  *stay put in world space as the camera moves*. Worth amending #830 separately.
- **2026-05-21** — **Strategic principle made explicit: GameState owns the emulated
  game; modules project subsets for UX (✅ owner-confirmed 2026-05-21).** The two
  tactical rules previously landed today ([#578](https://github.com/moumantai-gg/mithril/pull/578)
  consumption-side, [#584](https://github.com/moumantai-gg/mithril/pull/584) three-channel
  service-design) are **derivations** of this principle, not separate commitments.
  The underlying direction has been in place since [#511](https://github.com/moumantai-gg/mithril/issues/511)
  shipped the layered log pipeline (L0/L0.5/L1/L2): #511's mission is rebuilding the
  emulated game from logs, and the GameState services that emerged through the project's
  arc (`IPlayerPositionTracker` #454, `IPlayerSkillState` #462/#465, `IPlayerPinTracker`
  #468, `IInventoryService` (legacy; split into `IPlayerInventoryState` + `IChatInventoryState` + `IInventoryView` per #602),
  `IPlayerWeatherTracker`, `IPlayerCelestialState` #490,
  `IPlayerRecipeState` #475, `IGameSessionService`, `IPlayerQuestJournalState` #607, `PlayerAreaTracker`,
  and `INpcStateTracker` #552 in flight) are the canonical model of that emulated game.
  Articulating the principle explicitly clarifies that modules are *projections* of
  subsets for UX — they consume the canonical model, they don't parallel-emulate it.
  Future design questions ("does this thing belong in GameState or in a module?") have a
  direct answer from the principle rather than re-derivation. Surfaced during the
  discussion thread arising from the L2 spec review chain
  ([#574](https://github.com/moumantai-gg/mithril/issues/574)) and the audit umbrella
  ([#579](https://github.com/moumantai-gg/mithril/issues/579)) — specifically the
  realisation that the audit's "Class A migration" classification was too coarse for
  consumers using cross-cutting verbs as *temporal anchors for correlation* (Arwen
  gift attribution, Gandalf bracket discrimination) versus consumers *rebuilding
  cross-cutting state* (Samwise garden inventory map, Smaug CivicPride). Both share
  the symptom (a module parsing a cross-cutting verb) but the right disposition
  differs — the strategic principle disambiguates them.
- **2026-05-21** — **GameState service-design rule added: three channels (query, react,
  bind) over a developer-facing domain model (✅ owner-confirmed 2026-05-21).** Companion
  rule to the consumption-side bullet landed earlier the same day in
  [#578](https://github.com/moumantai-gg/mithril/pull/578). Together: modules consume the
  service surface (consumption-side); services translate log events into a domain model
  with three channels — Query for "what's the current state?", React for "what's
  happening?", Bind for "how does this view stay in sync?". Grounded in the inventory
  channel-conflation bug class: `IInventoryService.Subscribe` (legacy, pre-#602/#659)
  tried to serve both current-state-replay and event-log-replay through one API, with
  the result that three REACT consumers silently missed pre-attach
  `Deleted` / `StackChanged` events —
  Samwise's plant-resolution map, Arwen's gift calibration (surfaced in
  [#582](https://github.com/moumantai-gg/mithril/issues/582)), and Legolas's
  Motherlode dig-completion signal. Three sharp channels with distinct semantics retire
  the bug class structurally. Also notes the Bind-surface pattern (currently repeated
  in 5–6 places across Palantir + Legolas ViewModels) as a candidate for service-side
  lift. Establishes the service-design template future GameState services follow when
  they're built. Surfaced during the L2 spec review chain
  ([#574](https://github.com/moumantai-gg/mithril/issues/574)) and the post-#578
  consumer audit ([#579](https://github.com/moumantai-gg/mithril/issues/579)).
- **2026-05-21** — **Consumption-side rule added to cross-cutting ownership (✅ owner-confirmed
  2026-05-21).** The existing "data + surface" bullet stated shared services exist
  and are the single source of truth, with modules `Subscribe`/query them. The new
  bullet closes the consumption-side gap: modules **never** subscribe to raw logs
  (`IPlayerLogStream` / `IChatLogStream` / `IClassifiedPlayerLogLine`) for any
  cross-cutting domain that has a GameState service. Direct log subscription is
  reserved for the GameState services themselves and for module-specific domains.
  Grounded in real, hard-to-reproduce bugs from earlier in the project's history
  where multiple modules each rebuilt overlapping state from raw logs and produced
  symptoms that didn't replay deterministically. Lists the five HEAD-existent
  GameState services (`IInventoryService` (legacy; now `IInventoryView` post-#602),
  `IPlayerPinTracker`, `IPlayerWeatherTracker`, `IPlayerPositionTracker`,
  `IPlayerSkillState`) plus recipes/celestial/area trackers; names a verb list that
  signals the anti-pattern; records `LootBracketTracker.AddItemRx` as a known
  instance to retire by switching the tracker to `IInventoryService.Subscribe`
  (the as-of-date migration target; the actual landing later went via the post-#602
  PlayerWorld-direct route). Surfaced during the mithril#574
  L2 spec review chain. A full repo audit for additional anti-pattern instances is
  pending and will be filed as its own issue.
- **2026-05-19** — **Radagast charter sketch added (⚠️ Claude draft).** A proposed
  module for server-keyed environment state — moon phase, per-map weather,
  server-keyed gardening buff: sense → player-facing display → serverless
  community publish/consume. Name chosen by owner (the Brown Wizard — nature &
  weather; sits beside Gandalf/Saruman). Charter records boundaries vs.
  **Palantir** (debug-only inspector vs. Radagast's player-facing surface over the
  *same* GameState producers — data-owner-vs-surface rule) and **Samwise**
  (plot lifecycle vs. server-wide environment state; Samwise *consumes* the buff,
  never senses it). Producers stay in Mithril.GameState (Celestial/Weather exist;
  gardening-buff producer is unbuilt GameState work). **Owner refinement
  (same date):** the client/server community-sync *transport* (publish + consume)
  is **shared Mithril infra**, owned by no module — Radagast owns only its
  `"radagast"` key + payload slice. Added a "Cross-module shared infra" entry for
  the community-sync pipeline and a does-not-own bullet for it. **Owner-confirmed
  2026-05-19** ("charter statement matches my mental model") — entry promoted
  ⚠️→✅ and now binding; Radagast is not yet a `*.Module` project so the
  12-project coverage statement is unaffected.
- **2026-05-17** — **Silmarillion TSys: popup → tab (owner-ratified).** The
  2026-05-16 "show possible TSys rolls in a *popup*" framing is superseded by a
  dedicated **Treasure System tab** (#412). Verified v470: `tsysclientinfo` (Power
  entity, skill-tagged, tiered) + `tsysprofiles` (40 Profile pools) are card-shaped
  and **crystal-free** — the roadmap's Bucket-D "calculator-shaped, not a Silmarillion
  tab" verdict reasoned from the raw-JSON label, not the contents, and is **overturned**
  (roadmap doc updated in the same PR). #214 rescoped from "re-render pool inline" to
  "deep-link into the tab" (single render site; no divergence surface). Only roll
  *resolution* calculation stays Celebrimbor's; the *browse* is Silmarillion's.
  Disambiguated a naming collision: `Create{Region}TreasureMap{Quality}` is the
  **`TreasureCartography`** skill (buried-treasure maps, verified `recipe_21201` →
  `RewardSkill: "TreasureCartography"`), an unrelated system — explicitly out of scope
  for the TSys tab. Updated Silmarillion's *In scope* (popup→tab, v470 verification,
  crystal scoped to the recipe-craft path only) and the *Computation* does-not-own line
  (the "not a tab" clause overturned; calculation boundary retained).
- **2026-05-16** — Final three modules charactered by owner, closing the set (all 12
  `*.Module` projects now covered): **Palantir** (debug module — browser over
  Mithril.GameState + badge tester; not player-facing), **Smaug** (NPC stores, inv/
  storage gold valuation, store-state mining, sale min/maxing; NPC-gold tracking ⚠️
  future), **Saruman** (Words-of-Power known-state lifecycle from log monitoring).
  Added NPC-store-state and WoP-known-state rows to the data-domain table (Smaug is
  the one domain where a single module owns both acquisition and surface). "Not yet
  charactered" note replaced with a coverage statement.
- **2026-05-16** — Doc created. `Owns` grounded in current code; ✅ entries are
  owner/design-doc-confirmed (Gandalf eligibility confirmed by owner this date; Elrond
  recipe-anchoring and Silmarillion browser-not-calculator from existing design docs).
  All ⚠️ entries are unconfirmed Claude drafts pending owner sign-off.
- **2026-05-16** — Samwise charter confirmed by owner: first and foremost a garden
  tracker — interprets `Player.log` to track plantings + state transitions and alarms
  so plants don't die (loss-prevention, not just ripeness). Owns promoted to ✅
  confirmed.
- **2026-05-16** — Celebrimbor↔Silmarillion ⚠️ resolved by owner (the last genuine
  open charter ruling): recipe *display* deliberately overlaps — both show recipes,
  differentiated by purpose — and Celebrimbor additionally previews treasure effects
  while Silmarillion currently does not (#214: gap, not boundary). Added a carve-out
  to Silmarillion's "no computation" line so #214 can't be misread as a charter
  violation; listed `ResultEffectsParser` as shipped shared infra owned by neither.
- **2026-05-16** — Bilbo reframed by owner: at root **the browser/evaluator for the
  player's inv/storage export** (parallel to Silmarillion for reference data) + a
  read-only "craftable from on-hand now" evaluator. Generalised the four
  session-confirmed single-owner facts (Silmarillion/Bilbo/Pippin/Elrond) into one
  cross-cutting rule: each data domain has exactly one owning browser/evaluator. Added
  a recipe/crafting carve-out so "what's makeable from what I hold now" stays Bilbo's
  (state query) vs. Celebrimbor's planning.
- **2026-05-16** — Owner endorsed Silmarillion showing **possible TSys rolls** via a
  popup (the `AugmentPoolPreview` surface, shared `ResultEffectsParser`) — promoted
  from "#214 gap" to explicit in-scope. Sharpened the Silmarillion↔Celebrimbor
  relationship: Silmarillion = master list / full pool; Celebrimbor = planning-narrowed
  slice of the same data.
- **2026-05-16** — TSys-pool shape **verified** against `recipes.json` +
  `ResultEffectsParser`: confirmed the popup is data-supported (`*E` "(enchanted)" →
  `Crystal` keyword slots + `TSysCraftedEquipment(template)` → `AugmentPoolPreview`
  from `template.TSysProfile`/`Profiles`).
- **2026-05-16** — **Self-correction reverted.** The above check also concluded
  "crystal-independent / engine-runtime only" and flagged the owner's mechanic as
  unsupported. That was wrong — the check traced only recipe→parser and missed the
  crystal *item* record. Owner pointed at `Moonstone` (`item_18038`): `Description`
  *"Associated Primary Skill: Lycanthropy"* + `DynamicCraftingSummary` *"…Lycanthropy
  enchantments"* (siblings: LapisLazuli→Priest, Tsavorite→Ice Magic). The crystal→
  enchantment-family linkage **is** in `items.json` — owner was right, the correction
  was the error. Charter rewritten: two-piece data-backed mechanic; Silmarillion *can*
  cross-link recipe→crystal→skill (browsing). Lesson: verifying one path and
  generalising to "not in data" is itself an unverified assertion — scope the check to
  the claim, not the first path that comes to hand.
- **2026-05-16** — Owner settled the boundary: crystals **do** influence enchanted
  crafts; the crystal→enchantment-family *association* is in CDN data, but the
  treasure-system *specifics* (roll resolution/probabilities) are **not in CDN data at
  all**. Reframed the "what's out" from "calculation Silmarillion is barred from" to a
  **data-availability ceiling** (same class as gardening-XP absence) — Silmarillion
  surfaces the data-backed association; roll resolution is unshowable because it's
  absent upstream, not because it'd be calculation.
- **2026-05-16** — Resolvability precision (owner pointed at Rubywall Crystal,
  `item_1002`): the crystal enchantment-family signal is **prose/category, often not a
  `skills.json` entity**, and **heterogeneous** — `DynamicCraftingSummary` is the only
  consistent field; the `Description` "Associated Primary Skill" line is present on
  some crystals (Moonstone/LapisLazuli/Tsavorite) but absent on others (Rubywall), and
  *"survival-related"* maps to no `Survival` skill. Corrected the charter's
  overclaimed "cross-link to the associated skill" → surface as **text, degrading per
  the chip-degradation rule**, not a guaranteed Skill cross-link. This is the settled
  version.
- **2026-05-16** — Layering corrected after code verification: split the single-owner
  rule into *data owner (shared service)* vs *surface owner (module)*. `IInventoryService`
  (legacy; split into `IPlayerInventoryState` + `IChatInventoryState` + `IInventoryView`
  per #602) is in **Mithril.GameState** (not Mithril.Shared as recalled) — live sim from
  `ProcessAddItem/…` + chat `[Status]`; its surface is **Palantir**, not Bilbo. Bilbo
  scoped to the static-*export* surface (data = Mithril.Shared `IActiveCharacterService`/
  `StorageReport`). Dropped the stale "candidate for shared extraction" note (the
  shared inventory service already exists). Added a "not yet charactered" note
  (Palantir/Smaug/Saruman).
- **2026-05-16** — Elrond Owns expanded by owner: Elrond owns the **player's
  progression state** (learned skills, progress, known recipes) as the leveling
  calculator's constraint set — scoped to the progression facet (distinct from Bilbo's
  inventory state and shared character-data parsing). Post-#225 split clarified: math
  lifts to `Mithril.Leveling`, progression-state constraints stay Elrond's. The #225
  line owner-confirmed technically correct — retagged ✅ provisional/future, no longer
  bare ⚠️.
- **2026-05-16** — Owner sharpened the asymmetry: it is *not* symmetric overlap —
  **Silmarillion is *the* reference browser; Celebrimbor is just another place the
  data is displayed.** Generalised into a cross-cutting rule ("displaying data is not
  turf; being the browser is"), parallel to the shared-infra rule. Silmarillion Owns
  + the Celebrimbor browsing line reworded from "deliberate overlap" to this
  asymmetric framing.
- **2026-05-16** — Pippin data source corrected: the in-game report is *dumped into*
  `Player.log` (Pippin reads it there today) and *also* exists as an un-consumed
  standalone raw text file — the prior flat "not `Player.log`" was wrong. Endorsed
  food-provenance opportunity filed as **#348**; charter now carries the pointer
  instead of "untracked".
- **2026-05-16** — Pippin corrected again by owner: **food provenance IS in scope**
  — the prior ⚠️ "→ Silmarillion" was a wrong inference, removed. Primary data source
  clarified as the in-game reporting tool's character report (not `Player.log`/CDN; the
  earlier "from the log" Owns wording was also wrong). Owner endorsed an integrated
  "what haven't I eaten + where to find it" feature — recorded as an untracked
  opportunity (charter doesn't list issues; filing offered separately).
- **2026-05-16** — Celebrimbor charter confirmed & tightened by owner: scope is exactly
  "what is needed to make N units of X" + on-hand, no logic beyond the quantity math.
  Owns + the no-extra-logic boundary → ✅; Elrond's reciprocal "multi-recipe shopping →
  Celebrimbor" → ✅ by entailment. Celebrimbor↔Silmarillion left ⚠️ (strongly implied,
  not explicitly ruled).
- **2026-05-16** — Samwise gardening note refined by owner: planting loop is the
  *primary* Gardening XP source (grants XP — confirmed); recipes are secondary and in
  ref data. Recorded that Elrond (recipe-anchored) sees only the secondary slice;
  primary-loop XP presence in ref data remains Verification owed.
- **2026-05-16** — Legolas & Arwen sections confirmed accurate by owner; their ⚠️
  entries promoted to ✅.
- **2026-05-16** — Samwise gardening-XP claim corrected after a v470 data check: the
  asserted "gardening XP not in reference data" was **false** — `Gardening` is a skill
  and `BasicFertilizer1/2/3` recipes carry full Gardening XP, so recipe-based Gardening
  leveling is Elrond's via the cross-cutting rule. Crop-*lifecycle* XP availability
  recorded as explicit Verification owed. Both the prior draft and the owner's
  assumption were off; checking the data resolved it.
- **2026-05-16** — Samwise boundaries refined by owner: crop-selection/yield advice is
  a *soft* non-feature (PG gardening trivial; gardening XP absent from ref data), not a
  prohibition — promoted to ✅. Recipe/crafting confirmed as Elrond/Celebrimbor
  territory and lifted into a new "Cross-cutting ownership (confirmed)" section
  (recurs for Pippin/Bilbo). Added the hard-vs-soft does-not-own distinction to the
  reading guide.
- **2026-05-16** — Pippin charter corrected by owner: it supplements the **Gourmand**
  skill (food-novelty tracking — foods *not yet eaten*), not buff/consumption tracking.
  Owns + the no-buffs boundary promoted to ✅ confirmed. The earlier draft's
  "recommending what to eat" non-ownership was deleted — it contradicted the real
  charter (surfacing un-eaten foods *is* Pippin's job).
