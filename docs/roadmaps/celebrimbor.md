# Celebrimbor · Crafting Planner — roadmap

> **Active backlog:** [Mithril Roadmap Project — `Module: Celebrimbor`](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Celebrimbor%22).

What shipped in v1, what shipped after, and the rationale for what's still deferred. Per-item *task tracking* lives in Issues; this doc keeps the design narrative — *why* each piece was scoped the way it was, and the architectural rules that keep new prefixes additive.

## Context

Celebrimbor is the crafting planner. Users pick recipes and per-recipe quantities in a two-step wizard: **Step 1 — Pick Recipes** (search, filter, add), then **Step 2 — Shopping List**, which shows every item needed across the selection in a crafting-step ladder (raw materials → intermediates → final targets), grouped by item keyword within each step. On-hand counts from the active character's storage export cross-reference each row, a manual override beats detected stock, and progress bars + auto-collapse tell the user how close they are to being able to start crafting.

## What shipped in v1

### Module scaffolding
- `CelebrimborModule` entry point — lazy activation, custom `celebrimbor.ico` wired via `IconUri`, Lucide `Hammer` fallback, `SortOrder 450`, settings pane registered.
- Persistence via `JsonSettingsStore<CelebrimborSettings>` + `SettingsAutoSaver<CelebrimborSettings>` at `%LOCALAPPDATA%\Mithril\Celebrimbor\settings.json`. Settings surface: filter toggles, expansion depth, tooltip delay, craft list, manual on-hand overrides.

### Pure domain / services
- `RecipeAggregator` — target-centric aggregation: the craft-list entries seed the demand dict with each recipe's *output* item (`ResultItems` first, `ProtoResultItems` fallback), so targets, intermediates, and raw materials all flow through a single code path. Expansion depth now reads intuitively (`0` = targets only, `1` = targets + direct ingredients, `N` = full chain to raw). Cycle-safe via a visited-set; shortfall-only expansion respects both detected on-hand and manual overrides; `ChanceToConsume < 1` handled as expected-value math ceiled at display. Computes per-item dependency **depth** (memoised DFS over a producer lookup) for step grouping.
- `OnHandInventoryQuery` — reads `IActiveCharacterService.ActiveStorageContents` and projects to per-item counts + location chips via `StorageReportLoader.NormalizeLocation`.
- `CraftListFormat` — plain-text share format (`RecipeInternalName x Qty`, `#` comments, both `x` and `×` separators). Lenient parse: unknown recipes and invalid quantities produce warnings, never throw. Merge-append sums duplicates.
- `RecipeSearchIndex` — case-insensitive substring over name / internal name / skill, rebuilds on `IReferenceDataService.FileUpdated`.
- Extended `Mithril.Shared.Reference.RecipeEntry` with `ProtoResultItems` (optional tail param, back-compat with Elrond tests). Reference deserialiser populates it. Celebrimbor uses it as the fallback output source for crafted-equipment recipes.

### Picker (Step 1)
- `MithrilDataGrid` with `MithrilQueryBox` — bare substring search *or* expression queries (`IsKnown AND SkillLevelReq < 30`, `Skill = "Cooking"`).
- Per-row **+** button adds a recipe to the craft list; clicking again increments the quantity. One-click-one-add — no selection model, no multi-select toolbar. Cells are `Focusable=False` + a `SelectionChanged → UnselectAll` hook so the system-highlight never paints.
- Rows flagged gold when already in the craft list; a pill in the "In list" column shows the current quantity.
- Two filter toggles (Known only, Meets skill level); degrade gracefully when no active character.
- Rich row tooltip (Elrond-style): icon + name + skill/level header, Ingredients list with `ChanceToConsume %` when present, Yields list, `Known / Unknown` and `Skill met / Under-skilled` badges.
- Clipboard import/export toolbar (Copy list, Paste list with Append/Replace prompt, Clear).
- Bottom-docked **drawer** surfaces the active craft list: one editable-qty chip per recipe with a × remove button, `N recipes · M batches` summary, and the **Finalize** button. The drawer hides when the list is empty.

### Shell / wizard
- Shared `WizardStepIndicator`: two clickable step "pills" (Pick Recipes / Shopping List) with rounded corners, accent-colour active state, disabled until the craft list has content. Chromeless button template — no default WPF button chrome bleeding through.

### Shopping list (Step 2)
- **Making** context anchor at the top: icon + name chips with quantity pills, read-only. Always visible — doubles as intent reminder and tooltip host (shared `RecipeCardTemplate`).
- **Crafting-step ladder** as the main surface: outer groups keyed by dependency depth ("Step 1 · Raw materials", "Step N · Intermediate crafts", "Step N · Ready to craft"), inner groups keyed by item `PrimaryTag`. Each level has its own progress bar (gold for steps, green for tag groups), "Complete" badge, click-to-collapse chevron, auto-collapse on completion with a user-pinned latch so a manual expand persists. Single-group steps hide their inner header (redundant with the step header).
- Item rows: shared column layout (Item / Needed / On hand / pin / Override / Remaining). Override is an always-editable `TextBox` — committing fires `Rebuild()` so raw-ingredient shortfalls update live when the user types a count on an intermediate. Replaced `DataGrid` for the row surface with a plain `ItemsControl` so there's no selection model to fight.
- Location pin column opens a tooltip card listing every storage location holding the item, styled like the Elrond card (sourced from the same `RecipeCardTemplate` idiom).
- Header status reports which character's export is backing the on-hand counts.

### Settings pane
- Toggles for Known Only and Meets Skill Level.
- Numeric input for sub-recipe **Expansion Depth** (0–10).
- Slider + `{N} ms` readout for **Tooltip Delay** (0–2000 ms, default 200). Bound to `ToolTipService.InitialShowDelay` across every Celebrimbor tooltip surface via ancestor-`UserControl` binding.

### Testing
- 21 unit tests in `Celebrimbor.Tests`:
  - 14 cover `RecipeAggregator`: target-as-row semantics at depth 0/1/2, shared ingredients across targets, `ChanceToConsume` expected-value math, cycle termination, missing-ingredient tolerance, `Misc` fallback tag, zero/negative-qty skip, unknown-recipe skip, on-hand + override propagation, override-reduces-intermediate-shortfall.
  - 7 cover `CraftListFormat`: round-trip, comment / blank handling, separator variants, unknown / negative-qty warnings, merge-append semantics.

---

## What shipped after v1

### Shareable craft-list deep links

`mithril://list/<base64url>` carries a gzipped copy of the plain-text share format. The picker's "Copy share link" button (`CopyShareLinkCommand` in [RecipePickerViewModel.cs](../src/Celebrimbor.Module/ViewModels/RecipePickerViewModel.cs)) round-trips through `CraftListFormat.EncodeShareLink` / `DecodeShareLink`. The shell's `DeepLinkRouter` ([Mithril.Shared/Modules/DeepLinkRouter.cs](../src/Mithril.Shared/Modules/DeepLinkRouter.cs)) routes `list/` URIs to whichever module implements `ICraftListImportTarget` — Celebrimbor's `CraftListImportTarget` brings the tab to the foreground and prompts append-vs-replace. Registration is opt-in through the About settings toggle (HKCU only, no elevation).

### ResultEffects-driven output preview

**100% of `recipes.json` ResultEffects produce a preview** through one of 15 typed `Parse*` methods, the generic identifier-shape fallback, or the deliberate silent allow-list. Phase 6 (April 2026) introduced the strongly-typed preview pipeline (`AugmentPreview` for `AddItemTSysPower` plus the `EffectDescsRenderer` that resolves `{TOKEN}{value}` placeholders against `attributes.json`). Phase 7 extended it with `CraftedGearPreview` (the `TSysCraftedEquipment` / `GiveTSysItem` / `CraftSimpleTSysItem` chip), `TaughtRecipePreview` (`BestowRecipeIfNotKnown`), `WaxItemPreview` (`CraftWaxItem`), `AugmentPoolPreview` + the dedicated `AugmentPoolView`, and `EffectTagPreview` for the calligraphy / meditation tag families. The April 2026 coverage push (commits `c75aa46` → `e19eae8`) closed the long tail — adding `ResearchProgressPreview` / `XpGrantPreview` / `WordOfPowerPreview` / `LearnedAbilityPreview` (knowledge & progression), `ItemProducingPreview` (brews / surveys / treasure maps), `EquipBonusPreview` + `CraftingEnhancePreview` (equipment-property), `RecipeCooldownPreview` (`AdjustRecipeReuseTime`), `UnpreviewableExtractionPreview` (split out of `AugmentPoolPreview` for `ExtractTSysPower` since its outcome depends on the player-provided cube), the `EffectTagPreview` table-driven dispatch, and a generic identifier-shape fallback. Every typed preview renders inline on the recipe card and tooltip; clicking through opens `ItemDetailWindow` with the full per-effect breakdown — and the bundled `recipes.json` now hits 100% coverage with a standing gate test.

The eligibility model the pool viewer's pre-fill query uses (gear-level bracket × rolled-rarity floor × form/skill gate × `power.Slots ∋ template.EquipSlot`) is documented in [treasure-system.md](treasure-system.md). The slot clause closed [issue #8](https://github.com/moumantai-gg/mithril/issues/8) — without it the headline `OptionCount` over-counted by however many slot-incompatible powers lived in the profile.

### Reference — `ResultEffects` parser surface

The parser surface in [src/Mithril.Shared/Reference/ResultEffectsParser.cs](../src/Mithril.Shared/Reference/ResultEffectsParser.cs) projects every recipe's `ResultEffects` array into one of these typed preview shapes (each renders as its own collapsible section in `ItemDetailWindow`):

| Preview type | Prefix(es) | Notes |
|---|---|---|
| `CraftedGearPreview` | `TSysCraftedEquipment`, `GiveTSysItem`, `CraftSimpleTSysItem` | Deterministic crafted gear. |
| `AugmentPreview` | `AddItemTSysPower(power,tier)` | Effect descriptions resolved via `EffectDescsRenderer`. |
| `WaxItemPreview` | `CraftWaxItem(wax,power,tier,durability)` | Tuneup-kit crafts. |
| `WaxAugmentPreview` | `AddItemTSysPowerWax(power,tier,durability)` | Finite-use augment application. |
| `AugmentPoolPreview` | `TSysCraftedEquipment` enchantment-source form | Static pool — Browse-pool button opens the pool viewer. |
| `UnpreviewableExtractionPreview` | `ExtractTSysPower(cube,skill,minTier,maxTier)` | Outcome depends on player-provided cube; renders honest "preview not available" affordance. |
| `TaughtRecipePreview` | `BestowRecipeIfNotKnown(recipe)` | Recipe-teaching crafts. |
| `ResearchProgressPreview` | `Research{Topic}{Level}` | WeatherWitching / FireMagic / IceMagic / ExoticFireWalls. |
| `XpGrantPreview` | `Give{Skill}Xp` | Today only `GiveTeleportationXp`; future-proof shape. |
| `WordOfPowerPreview` | `DiscoverWordOfPower{N}` | |
| `LearnedAbilityPreview` | `LearnAbility(internalName)` | Falls back to humanised internal name (no abilities lookup table). |
| `ItemProducingPreview` | `BrewItem`, `SummonPlant`, `CreateMiningSurvey*`, `CreateGeologySurvey*`, `Create*TreasureMap*`, `CreateNecroFuel`, `GiveNonMagicalLootProfile` | Shared shape — display name, icon, qualifier, resolved item handle when args reduce to a known item. |
| `EquipBonusPreview` | `BoostItemEquipAdvancementTable(table)` | Equipped → permanent skill XP. |
| `CraftingEnhancePreview` | `CraftingEnhanceItem{Element}Mod`, `CraftingEnhanceItemArmor/Pockets`, `RepairItemDurability`, `CraftingResetItem`, `TransmogItemAppearance` | Uniform `(Property, Detail)` shape; per-prefix detail formatting. |
| `RecipeCooldownPreview` | `AdjustRecipeReuseTime(deltaSeconds, condition)` | Pre-formatted as `"Reduces cooldown by 1d on Quarter Moon"`. |
| `EffectTagPreview` | All zero/one-arg behavioural tags + the generic fallback | See below for breakdown. |

**`EffectTagPreview` covers the long tail through three mechanisms in `TryParseEffectTag`:**

1. **`ExactTagLines` dispatch table** — fixed display lines for ~30 zero-arg tags (mushroom / teleport / portal verbs, status effects, cosmetic / fairy / drink, survey / metadata, fishing / utility one-offs).
2. **`SuffixedTierFamilies` table** — prefix + integer suffix families: 15 Meditation tiers, 6 typed Calligraphy sub-families (Slash / Rage / FirstAid / ArmorRepair / Piercing / SlashingFlat), Whittling (+KnifeBuff), Augury, plus the bound-circle / portal numbered tags.
3. **Bespoke handlers** for the parametrised cases: `MeditationWithDaily[(combo)]`, `Calligraphy{N}{Slot}`, `CalligraphyCombo{N}[Letter]`, `SpawnPremonition_*`, `PermanentlyRaiseMaxTempestEnergy(N)`, the per-slot `Decompose*ItemIntoAugmentResources` family, the non-augment `Decompose{Source}Into{Substance}` family (Phlogiston / CrystalIce / FairyDust / Essence), `HelpMsg_*`, `StorageCrateDruid{N}Items`, `Teleport(area,name)`, `DeltaCurFairyEnergy(N)`, `ConsumeItemUses(template,N)`.

**Silent allow-list:** `Particle_*` is recognised and intentionally suppressed — these are display markers carrying no player-meaningful semantics.

**Generic fallback:** Any unrecognised PascalCase / `Identifier(args)` shape that isn't claimed by a typed parser or the silent allow-list flows through `TryHumanizeAsFallback` and renders as a humanised `EffectTagPreview` line. This is the future-proofing path: a brand-new prefix in a future game patch shows up as *something* informative rather than dropping silently. Prefixes that have typed parsers (or prefix-match families like `Research*`, `Give*Xp`, `CreateMiningSurvey*`, `CreateGeologySurvey*`, `Create*TreasureMap*`, `CraftingEnhanceItem*`) are explicitly excluded from the fallback so they never double-render.

**Coverage gate:** [tests/Mithril.Shared.Tests/Reference/ResultEffectsCoverageTests.cs](../tests/Mithril.Shared.Tests/Reference/ResultEffectsCoverageTests.cs) loads the bundled `recipes.json` and asserts every `ResultEffects` entry either produces a preview through one of the 15 `Parse*` methods or matches the silent allow-list. The only effects allow-listed at gate level are `Particle_*` and the documented `TSysCraftedEquipment` silent-skip cases (template lacks `TSysProfile` or profile not in `tsysprofiles.json` — 3 corresponding tests in `AugmentPoolParserTests`). New prefixes introduced by future game patches that don't fit existing typed shapes will surface through the generic fallback; if a future patch ever introduces a structured prefix worth a typed shape, the gate keeps passing while the typed parser gets added.

**Architectural rules for adding new prefixes:**
- New typed preview → add the record under `Mithril.Shared/Reference/`, add the `Parse*` method, wire it through `ItemDetailContext` + `ItemDetailViewModel` + `ItemDetailWindow.xaml`, and add the prefix to `ExcludedFallbackPrefixes` in `ResultEffectsParser.cs` so it doesn't double-render.
- New behavioural tag with a fixed display string → add to `ExactTagLines`.
- New numbered family (`PrefixN`) with a "Tier N" rendering → add a `(Prefix, "{format} Tier {0}")` row to `SuffixedTierFamilies`. Listed prefix-longest-first so longer prefixes win over shorter ones.
- Confirm coverage with `dotnet test tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj --filter "FullyQualifiedName~ResultEffectsCoverageTests"`.

### Keyword-matched recipe ingredients (`ItemKeys`)

Every `*E` enchanted-equipment recipe (~1,645 of them) carries an "auxiliary crystal" slot encoded as `{ "Desc": "Auxiliary Crystal", "ItemKeys": ["Crystal"], "StackSize": 1 }` — any item whose `Keywords` includes every listed tag satisfies the slot. Previously the parser dropped these entries (`ItemCode`-only filter), so the shopping list undercounted and the picker tooltip silently lost the slot.

The schema now models ingredients as a sealed `RecipeIngredient` hierarchy ([RecipeIngredient.cs](../src/Mithril.Shared/Reference/RecipeIngredient.cs)) — `RecipeItemIngredient` (by `ItemCode`) plus `RecipeKeywordIngredient` (by `ItemKeys` + optional `Desc`). Results stay as `RecipeItemRef`, mirroring the data asymmetry. A new catalog-side `ItemKeywordIndex` ([ItemKeywordIndex.cs](../src/Mithril.Shared/Reference/ItemKeywordIndex.cs)) inverts the keyword → items lookup; `OnHandInventoryQuery` adds a per-keyword owned-set so the aggregator answers single-key on-hand in O(1) and AND-matches multi-key sets via set intersection.

`RecipeAggregator` dedupes keyword rows under a synthetic `"#keys:..."` key (so two recipes citing `["Crystal"]` collapse to one shopping-list row) and renders the slot as `1× Auxiliary Crystal (any Crystal)` in both the picker tooltip and shopping list. Bilbo's `CraftableRecipeCalculator` and Elrond's `SkillAdvisorEngine` light up against the same data — Bilbo sums on-hand across matching items, Elrond shows `Any Crystal x1` in skill-advisor tooltips.

### Shopping-by-source pop-out window

The location-pin tooltip on shopping-list rows used to render `Locations` as a flat list of `Label × Quantity`. For keyword rows that union locations across many items at the same chest, this turned into a 30-line wall of `Serbule Community Chest × 47` repeated for items the row couldn't tell apart. The pin is now a `Button` that opens [`IngredientSourcesWindow`](../src/Mithril.Shared.Wpf/IngredientSourcesWindow.xaml) (in `Mithril.Shared.Wpf` so other modules can reuse it).

`IngredientLocation` ([Mithril.Shared/Storage/IngredientLocation.cs](../src/Mithril.Shared/Storage/IngredientLocation.cs)) carries per-item identity (`ItemInternalName`, `DisplayName`, `IconId`) so the window can group by location with a per-item breakdown sorted by quantity. The window has two tabs — **On hand** (default when stock exists) and **Sources** — populated by [`IngredientSourcesViewModel.Build`](../src/Mithril.Shared.Wpf/IngredientSourcesViewModel.cs) from a module-agnostic `IngredientSourcesInput`.

**Title-bar use-gate hint:** `Item.SkillReqs` is rendered as a sub-line under the item title (`Requires Gardening 10 to use`, or comma-joined for multi-skill `Requires Alchemy 35, Werewolf 35 to use`). Distinct from acquisition gates because it filters *use* (planting a seedling, casting a scroll), not *purchase*. Skipped for keyword rows (no single item to gate).

**Sources tab — `IReferenceDataService.ItemSources` resolution with gate enrichment:**

- `Vendor` / `Barter` / `NpcGift` → `Npcs[npc].Name` + `Area`. Vendor and Barter sources additionally surface `NpcService.MinFavorTier` as a `Requires <Tier> or higher` sub-line (Vendor routes to the NPC's `Store` service, Barter to its `Barter` service). NpcGift skips the per-NPC line because gift acceptance is gated per-preference (`NpcPreference.RequiredFavorTier`), not per-NPC.
- `Recipe` → resolved to the recipe's display name + `Requires <Skill> <Level>` and a `prereq: <recipe>` detail when `PrereqRecipe` is set. The parser ([`ResolveSourceContext`](../src/Mithril.Shared/Reference/ReferenceDataService.cs)) maps the JSON's numeric `recipeId` to the recipe's `InternalName` at parse time so the consumer doesn't need to know about the id form.
- `Quest` → `Quest reward` with the raw `questId` as detail. The bundled `quests.json` exists but isn't parsed yet (see deferred §3).
- `Monster` / `Drop` / `Angling` / `HangOut` → label + area resolved through the new `AreaCatalog` service.

`AreaCatalog` ([AreaEntry.cs](../src/Mithril.Shared/Reference/AreaEntry.cs)) parses `Reference/BundledData/areas.json` (previously unparsed) into `IReferenceDataService.Areas` — area code → friendly + short-friendly names, falling back to the long form when the JSON omits the short variant.

For keyword rows, the Sources tab shows a placeholder ("not aggregated yet") — surfacing a deduped union of vendor/drop/quest sources across 20+ items at once is its own UX problem worth a future pass (see deferred §4).

---

## Still deferred

### 1. Multi-character inventory aggregation

**Why deferred:** Keeps v1 focused on the shortest useful loop. `IActiveCharacterService.ActiveStorageContents` is already parsed and cached; scope grew cleanly from "active character only." Multi-character wants to iterate every export, parse each on a background thread, prefix locations with the character name, and cache by `(path, lastModifiedUtc)`. That's a modest but non-trivial feature with its own reliability surface (file-lock handling, stale-cache invalidation, memory ceiling).

**Likely approach for v2:**
- Extract `IInventoryQueryService` into `Mithril.Shared` — the shared abstraction both Celebrimbor and Bilbo would consume. The service iterates `IActiveCharacterService.StorageReports`, parses each via `StorageReportLoader.Load`, and exposes `QueryByInternalName(string) → IReadOnlyList<(Character, Location, Quantity)>`.
- Gate the background parsing behind `IModuleGate` so it doesn't run before the shell is ready.
- Location chips become `"{Character} · {Normalized Location}"`.
- Bilbo migrates to consume the same service; its `StorageRowMapper` stays where it is but starts pulling from the shared parse cache.

### 2. Recipe prereq chain visualization

**Why deferred:** Not core to the shopping-list goal; users who already have the list in hand have committed to the recipes.

**Likely approach for vNext:**
- Resolve `RecipeEntry.PrereqRecipe` transitively into a chain.
- Render a mini-chain in the picker's row detail pane on hover/select.
- Flag "you cannot learn this yet" when an upstream prereq is missing from `CharacterSnapshot.RecipeCompletions`.

### 3. Quest source resolution

**Why deferred:** `Reference/BundledData/quests.json` exists and is rich (`Name`, `InternalName`, `Description`, `DisplayedLocation`, `Objectives`, `Rewards`, `Rewards_Items`), but `IReferenceDataService` doesn't parse it yet. The Sources window currently renders quest sources as `Quest reward (45016)` — informational only.

**Likely approach:** Parser side, mirror the npcs / areas pattern — add `RawQuest` + `QuestEntry` records, register `Dictionary<string, RawQuest>` in `ReferenceJsonContext`, add `LoadQuests` + `ParseAndSwapQuests` to `ReferenceDataService`, expose `IReadOnlyDictionary<string, QuestEntry> Quests` on `IReferenceDataService`. Window side, the Quest case in `BuildSources` looks up `refData.Quests[$"quest_{questId}"]` and renders `Quest: <Name> · <DisplayedLocation>` with optional objectives or reward summary as detail. Touches the existing 17 test fakes (`Quests` property addition).

### 4. Sources tab for keyword rows

**Why deferred:** v1 keyword rows show a placeholder in the Sources tab. Aggregating vendor/drop/quest sources across 20+ items at once needs its own filtering/grouping UX (otherwise it floods the window). Likely needs a "common across all" vs "per item" toggle.

### 5. Active-character gate evaluation

**Why deferred:** The shipped Sources-tab enrichments (favor tier, recipe skill, item use-gate) all render the requirement text but don't yet check it against the active character. A natural next step is colouring met / unmet gates against `CharacterSnapshot.Skills` (recipe + use-gate skill comparisons) and `CharacterSnapshot.NpcFavor` (favor-tier comparisons), with a "you cannot learn this yet" badge against `CharacterSnapshot.RecipeCompletions` for `PrereqRecipe`.

**Likely approach:** Extend `AcquisitionSource` (and add a similar field to the title-bar hint) with `RequirementMetByActiveCharacter: bool?` populated when an active character is loaded. The window XAML adds a green/red trigger on the requirement line.

### 6. Tighter persistence of UI state

**Why deferred:** v1 persists the craft list, overrides, filter toggles, expansion depth, and tooltip delay. It does not persist DataGrid column layout, expanded-state of step / group headers across sessions, or recent search queries.

**Likely approach for vNext:**
- Wire `DataGridStateBinder.Bind(...)` on the picker grid for column-width and sort persistence.
- Persist `IsExpanded` per group / step so the user's mental model of what's "done" survives restarts.
- Recent search queries — drop-down history on the query box.

### 7. Variable-yield planning

**Why deferred:** `RecipeItemRef` currently exposes `ChanceToConsume` only; there's no `PercentChance` on result items in the schema. If the schema grows to include bonus-output probabilities (certain recipes do produce bonus copies at low probability), the aggregator's expected-value pipeline is the right slot.

**Likely approach if the schema grows:**
- Model `RecipeItemRef.PercentChance` for result rows.
- Add an "assume bonus yield" settings toggle that divides planned batch count by `1 + Σ percentChance * yieldMultiplier` when enabled (off by default — pessimistic planning avoids unhappy surprises).
- Surface the raw chance in a tooltip regardless of toggle state.

---

## Blocked on upstream data

Some enrichments aren't reachable today because the underlying CDN data doesn't include the fields. These would either need a new upstream file or out-of-band data sources to make progress.

### Monster name / level on `Monster` and `Angling` sources

`sources_items.json` `Monster` and `Angling` entries carry only `{ "type": "Monster" }` — no monster identifier, no level, no area. The Sources window can render them as `Monster: Drop` with no further detail. The bundled data simply doesn't tell us *which* monster drops the item, only that some monster does.

**Unblocks:** Either an upstream change to `sources_items.json` that includes monster ids, or a separate `monsters.json` + drop-table file. Bestiary-style data would also enable Bilbo and Smaug to surface drop hints.
