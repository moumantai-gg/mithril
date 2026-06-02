# Silmarillion · Tab Cookbook

> **Companion doc:** [roadmaps/silmarillion.md](roadmaps/silmarillion.md) — *why* each tab exists. This doc covers *how* to build one.

How to add a new master-detail tab to Silmarillion, after the Items + Recipes v1 shipped (PR #236), the navigator was refactored to a kind-target registry (#239), and the tab-strip moved to MVVM `ItemsSource` binding via [`ModuleTab`](../src/Mithril.Shared.Wpf/ModuleTab.cs) (#272 / #233). Every Bucket B tab follows the same scaffold; this doc is the scaffold. Per-tab handoffs in [`docs/planning/`](planning/) (e.g. `silmarillion-244-effects-tab/`, `silmarillion-249-storagevaults-tab/`) own the entity-specific decisions.

## What's in scope vs. what your handoff still owns

**The cookbook covers** (don't repeat in handoffs):
- File layout, naming, folder structure
- DI registration shape and the Func<> cycle break
- Master-detail VM skeleton (lazy detail VM, FileUpdated subscription, selection preservation)
- `IReferenceKindTarget` adapter shape
- TabControl wiring + the `SilmarillionViewModel` constructor parameter
- Cross-link chip conventions and degradation pattern
- The standard test trio (tab VM, kind target, navigator integration)
- Verification ladder

**Your handoff still owns**:
- Which `IReferenceDataService` source dictionary to read. **If a slim `<X>Entry` projection already exists for the kind** (Arwen consumes `NpcEntry`, etc.), decide explicitly whether to add a sibling `<X>sByInternalName` property exposing the full `Mithril.Reference.Models.<X>s.<X>` POCO (path 1, smallest blast radius — recommended default) or migrate the existing projection (path 2). For kinds with both a POCO and a slim projection, expect to alias `using <Kind>Poco = Mithril.Reference.Models.<Kind>s.<Kind>;` in the tab VM and tests to disambiguate. **Sizing path 2: look at field reads, not consumer file count.** A 12-file consumer list where everyone reads a narrow subset of fields that already exist on the POCO is mostly a mechanical type-swap + one or two field renames. The slim type's projected-only fields (e.g. `QuestEntry.SkillRewards` flattening polymorphic `Rewards.SkillXp`) are where the real migration cost lives; if no consumer reads those, path 2 is cheap. **Inversion case (#245):** when the slim projection is *wider* than the POCO — `AreaEntry` lifts the envelope key onto a `Key` field while the underlying `Area` POCO only carries `FriendlyName` / `ShortFriendlyName` — neither path applies. The slim type IS the canonical surface; consume it directly as the tab's row type, no sibling `<X>sByInternalName` needed. Document the inversion at the tab VM so future readers don't reach for Path 1 by reflex.
- The master-list row shape and filter facets. **Card secondary-line format**: plain `'<primary> <secondary>'` separated by a space — no unit-letter prefixes (`'L'` for level, `'x'` for count, etc.) and no parenthetical decoration. Match the v1 precedent: Recipes is `'Cooking 30'`, NPCs is the area name, Quests is location-or-favor-NPC, Abilities is `'Sword 7'`. Unit context is conveyed by the primary token without spelling it out per row.
- Detail-pane sections — which fields surface, in what order. For polymorphic entities (NPC `Services`, quest `QuestRequirement`), don't render every subclass field via `Concat(...)` into one bullet list — group by subclass *intent* and label each group, or real game data will read as undifferentiated slop. Worked example: [`QuestDetailProjector.ClassifyRequirement`](../src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs) collapses 42 `QuestRequirement` subclasses into 8 player-facing buckets (Story / Skill gates / Favor / Identity / Inventory / Time / Location / Combat / Composite / Flags / Drift). **For wide POCOs with multiple sub-list shapes** (Ability has `Costs`, `ConditionalKeywords`, `AmmoKeywords`, `DoTs`, …): define one row-record per shape (`<Kind><Sub>Row`, e.g. `AbilityCostRow`, `AbilityDoTRow`) co-located with the detail VM. Bind XAML against the row record, not the raw POCO sub-type — this lets display-only computed properties (`AbilityDoTRow.Display`, `AbilitySpecialValueRow.Display`, formatted DoT lines, value+suffix strings) live in the row record rather than `<Run StringFormat=...>` gymnastics in XAML.
- Reverse-lookup plumbing on `IReferenceDataService` (if the detail pane needs new cross-links)
- Cross-link chip kinds (which `EntityKind` values to anchor to)
- Any bundled JSON / refresh ordering caveats for the entity's source file
- **Default-value noise filtering.** When a chip / badge surfaces an enum-like value, identify whether one value is the universal default (e.g. `Favor: Despised` on every NPC service, "one-time quest" for most quests) and null it out in the projection so the XAML hides the chip — every persistent chip should carry information.
- **Editorial duplication suppression.** Distinct from default-value noise: when a POCO has both a `Description` field that PG editorialises (free-form prose) *and* a sibling display field (e.g. `AmmoDescription`, caster-requirements text, effect tooltip) that the prose verbatim contains, suppress the sibling line — or fold it into a chip label. Verify quantitatively against bundled data before locking in the suppression: the substring check should hide the duplicate in the common case but leave the line visible when the prose is generic and the sibling carries unique tier-specific info. AcidBolt1 (and ~76% of ammo-using abilities) was the cautionary case in #243 — "Fire an acid-covered bolt that destroys armor. Uses a Beginner's Dense Arrow." rendered alongside a redundant `AmmoDescription` row.
- **Disambiguators for non-unique display names.** Items have unique `InternalName`, so their display names don't collide. NPCs, recipes, and (likely) future kinds do — multiple NPCs share first names; recipes share skill+level. When surfacing an entity's friendly name in a context where collisions could confuse (favor lines, gift-target chips, recipe lists), annotate with a disambiguator: `AreaFriendlyName` for NPCs, `Skill + SkillLevelReq` for recipes. Build the annotated form as a helper on the projector, not inline at every call site.
- **Heterogeneous string arrays — partition by detector at VM time.** PG sometimes packs values with different semantics into the same string-array field. `Ability.ItemKeywordReqs` carries both real item keywords (`"EmptyHand"`, `"Sword"`) and caster-state form-gates (`"form:Cow"`, `"Werewolf"`, `"Beast"`); routing the gates as Items-tab keyword chips makes dead-link chips (#303). When you spot this, partition at VM build time into two output fields — real chips and gate labels — and verify the detector against the full corpus before committing (look at the sibling `*ErrorMessage` field for the semantic tell: "Must be in X Form" is the form-gate signal, "One Hand Must Be Empty" is the item-keyword signal). The error message itself is also the cleanest label source when it's player-facing and always-populated in the corpus.

## Drafting handoffs (when you're the planner)

Before recommending a cross-file facet or cross-link in a handoff, **verify the join key exists in the source data**:

- If you propose "filter quests by `IsGuidedObjective` sourced from `directedgoals.json`", grep `directedgoals.json` and confirm it contains quest `InternalName` references — not just a thematic relationship. (The #242 handoff proposed exactly this; `directedgoals.json` carried no quest keys and the facet had to be dropped mid-execution.)
- Each proposed facet should cite the **exact field on the join file** that contains the foreign key, by name. "Foreign-keys via `<file>.<field>`" — not "logically related to."
- When citing subclass counts for polymorphic kinds, **reference the POCO file path** as the source of truth. The #242 handoff cited "25 QuestRequirement subclasses" against an actual 42; six newer subclasses would have silently fallen through to the default branch if the executing agent hadn't re-verified. Better: pair the count with a test fixture that asserts the projector's switch arms match the count of concrete subclasses, so adding a new subclass surfaces as a test failure.
- **Verify the data is keyed by what the consumer expects.** Before proposing "fold reference file X into entity Y's detail pane," confirm X is actually keyed by Y's primary envelope key, not by a *predicate over Y's fields*. If the file is an array of rule rows with `Req*` / `MustHave*` fields rather than a dictionary keyed by entity InternalName, the natural consumer is whichever tab represents the predicate's domain — not the entity. The three `abilityXXX.json` files (#288) look like ability sub-tables but are conditional rules keyed by keyword predicates; their natural consumer is the Effects tab (#244), not Abilities. Symptom of misclassification: the answer to "what keywords does ability X have?" is `Ability.Keywords` directly, so the rule file adds nothing on the entity side.
- **Audit POCO field coverage when extending a Reference model.** Newtonsoft silently drops unknown JSON fields, so a POCO modelling N of M fields round-trips lossy and never warns. Before proposing work that reads a POCO's surface, grep the bundled JSON for the candidate field-set and diff against the POCO's properties: `grep -oh '"<Prefix>[A-Za-z]*"' src/Mithril.Shared/Reference/BundledData/<file>.json | sort -u`. Pair the POCO extension with a test that walks bundled data and asserts each modelled property is populated on at least one entry — catches a later refactor that renames the property and silently breaks the JSON binding. (PR #296 added five missing `AttributesThat*` fields to `AbilityKeyword` that had been round-tripping lossy since the POCO was written.) Drift detection for a *new field name shipped by Elder Game* is the daily CDN drift workflow's lane (see #285) — don't conflate the two.

## Scaffolding checklist

For tab `X` (e.g. `Npcs`, `Quests`, `Areas`):

1. **New files**
   - `src/Silmarillion.Module/Views/<X>TabView.xaml` (+ `.xaml.cs`)
   - `src/Silmarillion.Module/ViewModels/<X>TabViewModel.cs` — must implement [`ITabViewModel`](../src/Silmarillion.Module/ViewModels/ITabViewModel.cs) so `SilmarillionViewModel` can compose the tab from `IEnumerable<ITabViewModel>`. Two properties: `TabHeader` (the string shown in the TabControl chrome) and `TabOrder` (sort key — pick the next free integer; current ordering is Items=0, Recipes=1, NPCs=2, Quests=3, Abilities=4). **`TabOrder` must match your `IReferenceKindTarget.TabIndex`** for any entity kinds dispatched to this tab — `SilmarillionViewModel.OnNavigated` walks the same ordering.
   - `src/Silmarillion.Module/ViewModels/<X>DetailViewModel.cs` *(or reuse an existing shared detail VM if the entity already has one — e.g. items use `Mithril.Shared.Wpf.ItemDetailViewModel`)*
   - `src/Silmarillion.Module/Navigation/<X>KindTarget.cs`
   - **No new resolver class.** Friendly-name resolution lives in a shared DI-registered `IEntityNameResolver` that switches on `EntityKind` (one place to extend, one DI seam to mock in tests). To add support for your new kind, add a case to the resolver's switch with the kind's POCO-name fallback chain (raw POCO `Name` field, then internal name with prefix-stripping if the kind uses `<Prefix>_<Name>` envelope keys like `NPC_Joeh`). Consume the resolver in your master-list row projection, detail header, and any pre-existing chip-builder on a different tab that surfaces the kind's friendly name — see *Cross-link chips → audit existing surfaces*, below. Inlining `_refData.<X>sByInternalName[name]?.Name ?? name` in three places is the smell you're avoiding.
2. **`SilmarillionModule.Register`** ([src/Silmarillion.Module/SilmarillionModule.cs](../src/Silmarillion.Module/SilmarillionModule.cs)) — three `AddSingleton` lines, **in this order** so DI can resolve:
   ```csharp
   services.AddSingleton<XTabViewModel>();
   // Forward to ITabViewModel so SilmarillionViewModel composes its Tabs collection from
   // IEnumerable<ITabViewModel> — adding a future tab is just this pair of registrations
   // here, no SilmarillionViewModel ctor change (refactor #243).
   services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<XTabViewModel>());
   services.AddSingleton<IReferenceKindTarget>(sp => new XKindTarget(
       sp.GetRequiredService<XTabViewModel>(),
       sp.GetService<IDiagnosticsSink>()));
   ```
3. **`SilmarillionViewModel`** — **no changes required.** The constructor takes `IEnumerable<ITabViewModel>` and orders by `TabOrder` (post-#293 refactor); your tab VM's `ITabViewModel` registration above wires it up. `OnNavigated` and `OpenInWindow` dispatch via the kind-target registry — registry-driven for kinds, enumerable-driven for tabs. Confirm `EntityKind.<X>` exists in [src/Mithril.Shared/Reference/EntityRef.cs](../src/Mithril.Shared/Reference/EntityRef.cs); it should — all entity kinds were enumerated in v1. Every synthetic *fan-out* kind (`AbilityByEffectKeyword`, `RecipeIngredientItem` #273, `RecipeIngredientKeyword` #259, `ItemKeyword` #270, `NpcByArea`) was deleted in #318 slice 4 — no synthetic fan-out kinds remain. The surviving *filter-pivot* kinds (`EffectKeyword`, `EffectByStackingType`, `ItemByKeyword` #327) are legitimate single-value 1:1 pivots, not fan-outs — see *1:1 vs 1:N — the chip-vs-popup rule*, below.
4. **`SilmarillionView.xaml`** ([src/Silmarillion.Module/Views/SilmarillionView.xaml](../src/Silmarillion.Module/Views/SilmarillionView.xaml)) — **the `TabControl` itself doesn't change** (it binds `ItemsSource="{Binding Tabs}"` since the #272 / #233 refactor). Add a `<DataTemplate DataType="{x:Type vm:XTabViewModel}"><local:XTabView/></DataTemplate>` entry to `UserControl.Resources` so the TabControl's `ContentTemplate` can resolve the new VM type to its view. Header rendering and the gold IsSelected underline come from `ModuleTabHeaderTemplate` + `MithrilTabItemStyle` — already wired.
5. **Wire `IReferenceDataService.FileUpdated`** in the tab VM constructor — subscribe to the file name your data source reads (e.g. `"npcs"`, `"quests"`). Rebuild the master list on the UI thread; preserve selection by `InternalName`. **Don't skip this** — without it, a background CDN refresh leaves WPF's ListBox bound to a stale collection and selections set via the navigator silently fall out of the list.
6. **Expose `public static IReadOnlyList<ColumnSchema> SchemaSnapshot`** on the tab VM, reflected from your row type via `ColumnBindingHelper.BuildFromProperties(typeof(<Row>)).ToSchema()` — see [ItemsTabViewModel.cs:34](../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs#L34) for the canonical shape. Bind it from XAML as `<wpf:MithrilQueryBox Schema="{x:Static vm:XTabViewModel.SchemaSnapshot}" .../>` (#264 / #262). Without this binding the completion popup silently never opens and column-name highlighting falls back to unknown-column red. The reflected surface must match what the row type exposes, since the same reflection drives the `QueryFilter` parser at attach time.

That's it. No edits to `SilmarillionReferenceNavigator`, no `V1TabbedKinds` (it was retired in #239), no `OnNavigated` switch.

## Pattern walkthrough

The Items tab is the cleanest reference. Read these in order:

- [ItemsTabViewModel.cs](../src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs) — `_allItems` list, lazy `DetailViewModel` build on `OnSelectedItemChanged`, `FileUpdated` re-bind preserving selection (lines 30-37 ctor, ~80-110 selection handler, ~140-170 cross-link builders).
- [ItemsTabView.xaml](../src/Silmarillion.Module/Views/ItemsTabView.xaml) — `MithrilQueryBox` top, virtualized `ListBox` left (~320px), `ScrollViewer` right with detail pane.
- [ItemsKindTarget.cs](../src/Silmarillion.Module/Navigation/ItemsKindTarget.cs) — the four-property adapter. Note the critical comment at lines 33-39: resolve `TrySelectByInternalName` against the tab VM's bound collection, **not** against `IReferenceDataService` directly. A background refresh swaps instances, and a refData lookup hands you a record WPF can't match by `Equals`.

The Recipes tab does the same dance with a row-projection wrapper (`RecipeListRow`) because raw `Recipe` records don't carry resolved skill display names. If your entity needs cross-cuts (resolved skill, joined area, computed icon fallback), follow that pattern instead of `Item`'s plain-record model.

## Cross-link chips

Two parallel chip vocabularies, both rendered through reusable WPF UserControls in `Mithril.Shared.Wpf`:

**Entity-anchored** — use [`EntityChipVm`](../src/Mithril.Shared.Wpf/EntityChipVm.cs) `(DisplayName, IconId, EntityRef Reference, bool IsNavigable)` rendered through `<c:EntityChip .../>`. Set `IsNavigable = _navigator.CanOpen(reference)` — this returns `true` iff a kind target is registered for `reference.Kind`. Chips to kinds without a tab degrade to plain text automatically; they flip to clickable the moment that kind's tab ships. **Don't gate on the kind manually** — let `CanOpen` decide.

**Source-anchored** — use [`ItemSourceChipVm`](../src/Mithril.Shared.Wpf/EntityChipVm.cs) `(DisplayName, Detail, IconId, EntityRef? EntityReference, bool IsNavigable)` rendered through `<c:ItemSourceChip ClickCommand="{Binding DataContext.OpenEntityCommand, RelativeSource={...}}" .../>`. The `EntityReference` is nullable for true non-entity sources (monster drop, barter table). The chip auto-degrades to plain text in a transparent frame when `IsNavigable=false`. Don't introduce a parallel chip type per entity kind; reuse this one.

### Audit existing surfaces when shipping a new EntityKind

The "let `CanOpen` decide" rule only protects chips that *follow* it. Pre-existing chip-builders that hardcoded `IsNavigable: false` or `EntityReference: null` (because the kind wasn't tabbed when they were written) **stay stale** when the new kind ships. Symptoms: a chip on some *other* tab still renders plain text after your tab is live.

Before shipping, grep for:

- Any `EntityRef.<NewKind>(...)` call site — confirm its consumer sets `IsNavigable = _navigator.CanOpen(...)` and not a hardcoded `false`.
- Any `ItemSourceChipVm(..., EntityReference: null, IsNavigable: false)` literal — if the source `Type` could match the new kind (e.g. `"NpcGift"` for NPCs), wire it through `EntityRef.<NewKind>(s.Npc)` and `_navigator.CanOpen(...)`.
- Any `<TextBlock Text="{Binding ...}"/>` rendering source-style data with a `TODO(stub:#…)` nearby — it's probably an `ItemSourceChip` waiting to happen.

Replace the friendly display name in those builders with `_nameResolver.Resolve(EntityRef.<NewKind>(internalName))` from the shared `IEntityNameResolver` (per scaffolding step 1).

### EntityRef factory normalisation

Before constructing `EntityRef.<Kind>(input)`, check whether `input` comes from a data file whose reference form matches that kind's primary envelope-key form. If they diverge — Quest fields reference NPCs as `AreaSerbule2/NPC_DurstinTallow` slugs while `npcs.json` keys NPCs bare as `NPC_DurstinTallow` — **normalise inside the factory, not at every call site**. There will always be a new call site you forget. The `EntityRef.Npc` factory now strips everything before the last `/` (confirmed unambiguous against npcs.json envelope keys); follow the same pattern when introducing new kinds with slug-form references in source data. Worth grepping existing factories on every new tab.

### Keyword-vs-Item slug discrimination

A JSON field whose value reads like a tier-suffixed item slug (`'DenseArrow1'`, `'SporeBomb3'`, `'Crystal2'`) is **almost always an item *keyword***, not an item InternalName. Use [`EntityRef.ItemByKeyword(...)`](../src/Mithril.Shared/Reference/EntityRef.cs) (the single-keyword 1:1 filter-pivot kind, #327 — *not* the retired #270 `ItemKeyword` fan-out factory), not `EntityRef.Item(...)` — it deep-links to the Items tab filtered by `Keywords CONTAINS "<tag>"` rather than selecting a single row. Heuristic: if the value matches multiple items in `ItemsByInternalName.Keywords` rather than appearing as a single `Item.InternalName`, it's a keyword. Field-name clues: `ItemKeyword`, `<X>Keyword`, `ReqKeyword`, `KeywordRef`, `*Keywords` are all keyword-typed. Surfaced in #243's `AbilityAmmoKeyword.ItemKeyword` rendering — first-pass shipped as plain text, review-corrected to deep-link chips; degraded to plain text in #326 when the double-duty `ItemKeyword` kind was retired for its fan-out use, then restored to `EntityRef.ItemByKeyword(...)` chips in #327.

### Chip-stub coverage grid (for tabs with parallel projection paths)

When a tab has symmetric projection paths — requirements vs rewards, ingredients vs results, gives vs receives — enumerate the entity-shape transforms as a **grid** (kind × path) during chip-stub design, not a flat per-method list:

| Entity kind | Requirement side | Reward side |
|---|---|---|
| NPC (favor) | `MinFavorLevel` ✓ chip | `DeltaNpcFavor` ✗→✓ (#242 caught this) |
| Quest | `QuestCompleted` ✓ chip | (n/a) |
| Item | `InventoryItem` ✓ chip | (via reward chips) |
| Ability | `AbilityKnown` text only | `LearnAbility` ✓ chip |

A flat per-method list hides the mismatched cell; the grid surfaces it. **And:** synthetic tests that assert chip *Text* should also assert `ChipName` / `Reference` / `Prefix` when a chip is intended, not just the prose — text-only assertions accept a regression where the chip half drops to null.

### Identifier fallback splitter

When a chip falls back to a CamelCase-split of an identifier (kind not registered, no resolver hit), the splitter must handle **all separator chars present in the source data**, not just camel-case boundaries. Catalog identifiers like `LiveEvent_Crafting`, `LiveNpc_Orran`, `LiveEvent_Kalrod_Done` use `_` as semantic separators; some data files also use `.` (e.g. `Skill.Bard`). Add a regression test with a real-data identifier the first time the helper is wired up — `_` between non-uppercase chars is exactly the case clean-input synthetic tests miss.

### Pitfalls

- ❌ **Right-aligned arrow button next to plain text rows** to signal "this is navigable". Too small, wrong visual weight, doesn't read as an affordance. The user-feedback verdict on #242's first pass: "did you introduce this instead of chips?"
- ❌ **Hyperlink-style underlined text inside a `Run`.** Same problem — too subtle, conflicts with WPF's default `Hyperlink` rendering on some themes.
- ✅ **`EntityChip` for self-contained entity references.** The chip *is* the affordance.
- ✅ **`EntityChip` + label prefix when the row needs context.** `"Completed:" + [chip]` carries the prefix as a `TextBlock` next to the chip — don't wrap the chip in a button.

## 1:1 vs 1:N — the chip-vs-popup rule (#318)

This supersedes the earlier "navigable summary chip" / synthetic-kind-deep-link pattern. One rule, all tabs:

| Relationship | Surface |
|---|---|
| **1:1 direct reference** (this recipe, this NPC, this area, *one* keyword filter pivot) | navigable `EntityChip`, `EntityRef.X(internalName)` — unchanged |
| **1:N reverse-lookup / fan-out** (abilities required by this effect, recipes consuming this item, NPCs in this area) | **provenance popup** ([`ProvenancePopupWindow`](../src/Mithril.Shared.Wpf/ProvenancePopupWindow.xaml)), fed the source index collection **directly** |

### The invariant (read this before building any 1:N surface)

> The set is materialized exactly once, in the index, **and the index retains why each member qualified**. The popup renders membership *and provenance*. The optional per-section **"To Query"** button emits a labeled, explicitly-lossy projection into the destination tab's query box — and is **never** the source of a displayed count or membership claim.

If a future popup ever populates itself by re-running a query, the second derivation is back and so is the bug. The invariant — not the word "popup" — is the review target.

**Why this replaced the summary-chip + synthetic-kind pattern.** The retired pattern had a *dual-derivation* fault: the set was computed once by an index union in `ReferenceDataService`, but the "View all N →" chip deep-linked through a synthetic `EntityKind` whose kind target **re-derived** the set as a query string over *one* of the unioned fields. Two independent derivations of the same set; they diverged silently. Quantified on bundled data for effect→abilities: of 24 distinct effect-keyword tags, the queried field carried 1; the index union carried all 24 — so **23 of 24** "View all N" chips deep-linked to an empty filtered list. The popup-from-index has no query language between the set and the screen, so the expressiveness mismatch that *caused* the bug is simply absent. It is also strictly better: a browse tab can only show *which* rows matched; the popup sections by *why* ("6 **require** it, 7 are **enabled by** it, 2 **target** it").

### How to build a 1:N surface

1. **Provenance-retaining index.** The reverse-lookup index value must carry, per member, *why* it qualified — a `[Flags]` reason enum + a `(<Entity>, Reason)` match record (see [`EffectAbilityMatch`](../src/Mithril.Shared/Reference/EffectAbilityMatch.cs) and `IReferenceDataService.AbilitiesByEffectKeyword`). A member qualifying via multiple fields is carried **once** with OR'd reason flags, so a distinct-member count equals the displayed "View all N".
2. **Build a [`ProvenancePopupViewModel`](../src/Mithril.Shared.Wpf/ProvenancePopupViewModel.cs)** in the detail VM from that index directly: a title, an *ordered* list of `(sectionLabel, IReadOnlyList<EntityChipVm>)`, and an optional per-section `ToQueryCommand`. Rows are navigable `EntityChip`s (1:1 — the popup is *composed of* direct references, consistent with the rule). One reason ⇒ the VM collapses to a flat list (a single trivial reason is noise — the *Discipline* rule). The section lists are virtualized (recycling `VirtualizingStackPanel`; high-cardinality precedent is the #259 Massive Tourmaline ~547-row case).
3. **Open it as a window**, not a navigation. The detail VM exposes a `Show…PopupCommand` whose default opener does `new ProvenancePopupWindow{…}.Show()`. **Opening the popup must not push navigator back/forward history** — it mirrors `IReferenceKindTarget.TryOpenInWindow`'s non-navigating contract (#229). Inject the opener (static `Action`) so tests assert the VM without spawning a window. The reference implementation is [`EffectDetailViewModel`](../src/Silmarillion.Module/ViewModels/EffectDetailViewModel.cs)'s `BuildRequiredByAbilities` + `ShowRequiredByAbilitiesPopupCommand`.
4. **"To Query" is reserved but not load-bearing.** `ProvenancePopupViewModel.ToQueryCommand` exists so a host *can* offer a labeled, explicitly-lossy push into the destination tab's query box. It is post-hoc and user-initiated; it never sources a count or membership claim. effect→abilities ships it **unset** (no button rendered) — the projection logic is a deliberate fast-follow; the popup-from-index is already the count-bearing surface. `ActionChip` may survive as the eventual "To Query" affordance.

### State of the migration

**Migrated (#318 slices 2+3): effect→abilities** — Effects-tab "Required by abilities" "View all N →" opens the provenance popup; the `AbilityByEffectKeyword` synthetic `EntityKind` + its kind target + its `mithril://silmarillion/AbilityByEffectKeyword/…` route were deleted.

**Migrated (#318 slice 4): RecipeIngredientItem (#273)** — Items-tab "Used in" "View all N →" opens the provenance popup fed `IReferenceDataService.RecipesByIngredientItemWithReason` directly; the `RecipeIngredientItem` synthetic `EntityKind`, its `EntityRef` factory, `RecipeIngredientItemKindTarget` (+ DI registration), and its `mithril://silmarillion/RecipeIngredientItem/…` route were deleted. Single-reason relationship (`DirectIngredient`) ⇒ the popup collapses to a flat list (#318 Discipline).

**Migrated (#318 slice 4): RecipeIngredientKeyword (#259)** — item-detail "Used as" "View all N →" opens the provenance popup fed `IReferenceDataService.RecipesByIngredientKeywordWithReason` directly (the recipes consuming this item via a keyword-ingredient slot, unioned across the item's keyword tags and deduped by recipe); the `RecipeIngredientKeyword` synthetic `EntityKind`, its `EntityRef` factory, `RecipeIngredientKeywordKindTarget` (+ DI registration), and its `mithril://silmarillion/RecipeIngredientKeyword/…` route were deleted. Single-reason relationship (`KeywordIngredientSlot`) ⇒ the popup collapses to a flat list (#318 Discipline).

**Migrated (#318 slice 4): NpcByArea** — Areas-tab "NPCs in this area" "View all N →" opens the provenance popup fed `IReferenceDataService.NpcsByAreaWithReason` directly (derived from the same accumulation as `NpcsByArea` so the two cannot diverge); the `NpcByArea` synthetic `EntityKind`, its `EntityRef.NpcByArea` factory, `NpcByAreaKindTarget` (+ DI registration), and its `mithril://silmarillion/NpcByArea/…` route were deleted. Single-reason relationship (`InArea`) ⇒ flat list (#318 Discipline). **#311 folded in & closed:** Area-detail landmark groups now render through the shared virtualizing `ProvenancePopupWindow`/`ProvenancePopupViewModel` (recycling `VirtualizingStackPanel`, the ~547-row #259 precedent) — there is no separate non-virtualized list path for the full landmark set. Landmark groups are a *provenance-sectioned* popup (one section per landmark `Type`, since Type is genuine provenance — "which kind of landmark"), deliberately **not** flat (contrast the NPCs single-trivial-reason case).

**Migrated (#318 slice 4): ItemKeyword (#270) — fan-out only.** Recipe-detail keyword slots ("any Crystal", "Main-Hand Item") now surface as the "Keyword ingredients" section, each a "View all N →" provenance popup fed `IReferenceDataService.ItemsByRecipeKeywordSlotWithReason` directly (materialized once via the canonical `ItemKeywordIndex` — no query re-derivation, so the count cannot diverge; this dissolved the exact dual-derivation fault where `ItemKeywordQueryMapper` failed whole-slot on `MinTSysPrereq:`/`SkillPrereq:` keys the keyword index nonetheless matched). The `ItemKeyword` synthetic *fan-out* `EntityKind`, its `EntityRef` factories, `ItemKeywordKindTarget` (+ DI registration), `ItemKeywordQueryMapper`, and the `mithril://silmarillion/ItemKeyword/…` route were deleted. Single-reason relationship (`KeywordMatch`) ⇒ the popup collapses to a flat list (#318 Discipline). The `ItemKeyword` kind was *double-duty* — it also backed two legitimate single-keyword 1:1 Items filter-pivots (Abilities/NPCs tabs), which #326 intentionally degraded to non-navigable plain-text chips to stay scoped.

**Restored (#327): the single-keyword Items filter-pivot.** The Ability-detail `ItemKeywordReqs` + ammo-keyword chips and the NPCs-tab Store-cap / Consignment keyword chips are navigable again — re-wired to the new `EntityKind.ItemByKeyword` (`EntityRef.ItemByKeyword`, `ItemByKeywordKindTarget`, `mithril://silmarillion/ItemByKeyword/…`), a single-keyword 1:1 *filter pivot* (one tag → Items tab `Keywords CONTAINS "<tag>"`). This is the symmetric Items-side twin of the surviving `EffectKeyword` kind and a deliberately **distinct** name from the retired fan-out `ItemKeyword` (so the #326 route-rejection lock stays valid). It is **not** a reintroduced synthetic fan-out kind: no `*WithReason` index, no `'+'`-joined composite slot key, no query re-derivation, no popup. `TryOpenInWindow()` returns false — navigation flips the tab + query and pushes no window history. Pre-existing dead-pivot caveat: the hand-state pseudo-keywords `HasHands`/`Unarmed` in `Ability.ItemKeywordReqs` match zero items, so restoring navigability re-exposes that defect; partitioning them out of the chip is tracked separately in **#332** (data-classification, not navigation-mechanism — out of scope for #327).

**Fully migrated.** Every synthetic fan-out / 1:N reverse-lookup `EntityKind` — `AbilityByEffectKeyword`, `RecipeIngredientItem`, `RecipeIngredientKeyword`, `ItemKeyword`, `NpcByArea` — is now deleted; no synthetic-kind 1:N surfaces remain. **Do not add new synthetic-kind 1:N surfaces** — build them on the popup-from-index rule above from day one. A single-keyword *filter pivot* (one tag → "open the tab filtered to this concept") remains a legitimate 1:1 `EntityChip` target; the rule is about *fan-out / reverse-lookup* sets, not every query-shaped navigation. The surviving pivot kinds are `EffectKeyword`, `EffectByStackingType`, and `ItemByKeyword` (#327) — note the deleted *fan-out* `ItemKeyword` (#270) and the live *pivot* `ItemByKeyword` are deliberately distinct names; do not conflate or merge them.

**Tooling for collection-CONTAINS filtering** (still relevant for the surviving keyword pivots and any "To Query" projection): wrap the tag/value in a small record implementing `IQueryStringValue` (e.g. [`IngredientKeywordValue.cs`](../src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs)), expose it on your master-list row as `IReadOnlyList<TValue>`, and the `MithrilQueryBox` parser handles `CONTAINS` for free (#261). Make sure the row type's reflected schema (step 6 above) includes the collection field — without it, autocomplete won't suggest it.

## Shared.Wpf helpers worth knowing about

Several reusable WPF helpers live in [`src/Mithril.Shared.Wpf/`](../src/Mithril.Shared.Wpf/) and should be the first choice over rolling per-tab equivalents:

- **`EntityChip` / `EntityChipVm`** — navigable entity chips (see *Cross-link chips* above). Solid border, optional icon, "open this specific entity" semantics.
- **`ProvenancePopupWindow` / `ProvenancePopupViewModel`** — the shared 1:N surface (see *1:1 vs 1:N — the chip-vs-popup rule* above). Renders a reverse-lookup set fed the source index directly, sectioned by inclusion reason, single-reason collapsing to flat. Opened via `Window.Show()` — pushes no navigator history. **This is the first choice for any fan-out / reverse-lookup relationship**; do not reach for a synthetic-kind summary chip.
- **`ActionChip`** — ghost / outline variant of `EntityChip` for "navigate to a filtered view" semantics (transparent fill, subdued border, leading `ListFilter` icon). Same `EntityChipVm` DataContext + `ClickCommand` contract as `EntityChip`; picking between the two is a visual-affordance call. **Legitimate use today:** a *single-keyword filter-pivot* `EntityChip`/`ActionChip` (one tag → "open the tab filtered to this concept") is 1:1 and stays a chip. It is **no longer** the surface for fan-out reverse-lookup clusters — those are popups. `ActionChip` is also the reserved rendering for a future `ProvenancePopupViewModel.ToQueryCommand` affordance.
- **`ItemSourceChip` / `ItemSourceChipVm`** — source-style rows (see same).
- **`MithrilQueryBox`** — query box + completion popup (see step 6).
- **`FormattedText` attached property** — parses PG's inline `<i>...</i>` / `<b>...</b>` markup in long-form text. Quest descriptions use this as speaker prefixes (`<i>Zhia Lian:</i> ...`); 880 italic + 124 bold pairs across the quest catalogue alone. Bind as `<TextBlock c:FormattedText.Text="{Binding Description}"/>` anywhere long-form text shows. **Audit:** Items v1 and Recipes v1 shipped before this helper existed and may currently render literal `<i>` tags on description bindings — worth a grep for plain `Text="{Binding Description}"` consumers on every tab PR.

## Reverse-lookup index rebuild triggers

When your detail pane needs cross-link sections fed by reverse lookups (`RecipesTaughtByNpc`, `ItemsSoldByNpc`, etc.), build them in a `Build<X>CrossLinkIndices()` method on `ReferenceDataService`, mirroring the existing [`BuildRecipeCrossLinkIndices`](../src/Mithril.Shared/Reference/ReferenceDataService.cs). Call it from the end of the `ParseAndSwap*` method for **every** input file the index depends on, so a refresh of any one rebuilds the index. Concrete trigger matrix:

| Index | Triggers rebuild from |
| --- | --- |
| `RecipesByProducedItem`, `RecipesByIngredientItem` | `items.json`, `recipes.json` |
| `RecipesTaughtByNpc` | `recipes.json`, `sources_recipes.json` |
| `ItemsSoldByNpc` | `items.json`, `sources_items.json` |

Same pattern for whatever index your tab adds. Missing a trigger means a CDN refresh of the dependent file leaves the index pointed at stale POCO instances.

## DI cycle break — why the Func<> wrappers exist

[SilmarillionModule.cs:36-39](../src/Silmarillion.Module/SilmarillionModule.cs#L36-L39):

```csharp
services.AddSingleton<IReferenceNavigator>(sp => new SilmarillionReferenceNavigator(
    () => sp.GetServices<IReferenceKindTarget>(),
    () => sp.GetService<IModuleActivator>(),
    sp.GetService<IDiagnosticsSink>()));
```

Kind targets depend on tab VMs (via constructor). Tab VMs depend on `IReferenceNavigator` (for `CanOpen` calls inside chip builders). The navigator depends on kind targets (for `CanOpen` to mean anything). The `Func<>` wrapper defers `GetServices` until the navigator's first `CanOpen` call — by which point the container is fully built. **Keep the Func<> shape when modifying registration.** Eager resolution will cycle.

Tests should use the eager `SilmarillionReferenceNavigator(IEnumerable<IReferenceKindTarget>)` constructor with stubs; production code is the lazy path.

## Test scaffolding

Three test files per tab, mirroring the v1 trio:

1. `tests/Silmarillion.Tests/ViewModels/<X>TabViewModelTests.cs` — list construction, sort order, skill / area resolution, `FileUpdated` re-bind preserves selection, detail-VM build cross-link projection.
2. `tests/Silmarillion.Tests/Navigation/<X>KindTargetTests.cs` — `Kind` / `TabIndex` properties, `TrySelectByInternalName` (hit and miss), `TryOpenInWindow` (with and without current detail).
3. `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` and `SilmarillionViewModelTests.cs` — **extend existing tests**, don't create per-tab navigator tests. Verify the duplicate-registration guard still trips with the new kind in the mix.

Use the `NavFactory.WithKinds(...)` helper from [RecipesTabViewModelTests.cs](../tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs) to construct stub navigators with a specific kind subset registered — this is how you write `IsNavigable=true|false` assertions deterministically. Extend `StubReferenceData` with new source dictionaries as needed; consumer fakes in other modules use the interface default for any property they don't care about, so adding source data doesn't ripple across the test suite.

**The non-rippling default depends on you giving the new property an interface-level default value.** Pattern for every new `IReferenceDataService` property:

```csharp
IReadOnlyDictionary<string, Npc> NpcsByInternalName => EmptyNpcMap;

private static readonly IReadOnlyDictionary<string, Npc> EmptyNpcMap
    = new Dictionary<string, Npc>(StringComparer.Ordinal);
```

Without the `=> Empty<X>` default, every test fake across every module needs a `NpcsByInternalName` getter — a many-file cascade you don't want.

**When your test fake exposes a reverse-lookup index for kind X and a chip-builder resolves entries against `IEntityNameResolver`, also surface `<X>ByInternalName` on the same fake** so the resolver can project `InternalName → Name`. Deriving it from the index values is fine — e.g. `AbilitiesByInternalName => AbilitiesTaughtByNpcMap.Values.SelectMany(v => v).ToDictionary(a => a.InternalName!, a => a)`. Without this, the resolver falls through to the raw InternalName and a synthetic test asserting `DisplayName == "Wounding Shot"` silently becomes an `InternalName == "WoundingShot"` assertion that passes for the wrong reason.

## Verification ladder

Every Bucket B PR should pass these in order:

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test tests/Silmarillion.Tests` — the three new test files + existing tests pass.
3. `dotnet test Mithril.slnx` — no regressions, especially in consumers of `IReferenceDataService` (Celebrimbor, Bilbo, Elrond, Arwen).
4. **Real-data sanity check before manual smoke.** Walk 2-3 real entries from the bundled JSON for the kind, and confirm each polymorphic subclass / variant renders legibly with real data. Synthetic test fixtures verify *projection correctness*; only real data exposes whether the *rendering* is parseable. Categories the real-data walk catches that synthetic tests systematically miss:
   - **Cross-file foreign-key form mismatches** (slug `AreaX/NPC_Y` vs bare envelope key `NPC_Y`).
   - **In-data markup the rendering layer doesn't parse** (literal `<i>` tags showing through).
   - **Affordance discoverability** (right-aligned arrows, hyperlink-styled runs, etc. — see *Cross-link chips → Pitfalls*).
   - **Information hierarchy** (gameplay-loop-affecting metadata buried in the footer — repeatability/daily-vs-one-shot belongs in the header, not below the chips).
   - **Section ordering** (does the layout match the player's read order? Description above or below the chip grid?).
   Promote the walk to an automated test where the data permits: `RealBundled<EntityKind>_<SpecificEntry>_ProjectsSensibly()` loads the real bundled JSON for 2–3 known entries and asserts text shape (no `(unknown)` sentinels, every text non-empty, expected buckets present). Skip when bundled data isn't co-located so CI shapes that strip it stay green. Locks correctness in code; lets human smoke focus on rendering quality. Canonical example of the bundled-load plumbing (throwing-`HttpClient` + `bundledDir:` arg + existence-check skip): [`ReferenceDataServiceRecipeCrossLinkIndexTests`](../tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeCrossLinkIndexTests.cs).
5. **Build-time XamlResourceLint** runs as part of `dotnet build` and catches dangling `{StaticResource X}` references that would otherwise blow up at runtime when the view is opened. When you add a new converter or `DataTemplate`, either declare it locally in the consuming view's `UserControl.Resources` or explicitly merge the module's `Resources.xaml`. Lint errors point at the canonical fix pattern; trust them.
6. `dotnet run --project src/Mithril.Shell` — manual checks. **Close Mithril between rebuilds during iterative fix cycles** — the post-build copy-to-`Mithril.Shell/modules/` cascades to MSB3027 file-lock errors when the running app holds the module DLL open. Symptom: a clean Module build fails 10× retries before erroring; test DLLs build cleanly because tests don't copy to `modules/`.
   - Open Silmarillion → new tab appears in the strip with the gold IsSelected underline (confirms `MithrilTabItemStyle` is being applied — only happens when the tab comes through `ItemsSource`, not direct `TabControl.Items.Add`).
   - Pick an entity → detail pane populates.
   - Type a column name in the query box → completion popup opens, known names highlight in column-gold (confirms `SchemaSnapshot` is wired per step 6).
   - Cross-link chips render (navigable to tabbed kinds, plain text otherwise).
   - Click a chip that points to your new kind from an *existing* tab → tab switches and entity selects.
   - Click a chip on a *different* tab that anchors your new kind (e.g. NPC chips on recipe-detail's "Taught by" section after shipping NPCs) — confirms the audit pass from *Cross-link chips → audit existing surfaces* caught everything.
   - Open-in-window button on the header works for the new kind.
   - Background refresh check: leave the tab open, force a CDN refresh, confirm the list rebuilds without losing the current selection.

## Four things that go wrong

1. **The tab VM is registered but the kind target isn't.** The tab renders but cross-link chips to its kind stay plain-text, and the navigator's `CanOpen` returns false for the new kind. Symptom: clicking a chip silently does nothing. Cause: missed the second `AddSingleton<IReferenceKindTarget>` line. Fix: register the target.
2. **The tab VM lookup happens against `IReferenceDataService` instead of the bound `All<X>` collection.** Symptom: post-CDN-refresh navigation appears to succeed but the detail pane goes blank. Cause: WPF's ListBox can't match the new refData instance to its `ItemsSource`. Fix: resolve in `TrySelectByInternalName` against the tab VM's bound collection, per the `ItemsKindTarget` comment.
3. **`MithrilQueryBox.Schema` isn't bound.** Symptom: typing in the query box never opens the completion popup, and known column names render as unknown-column red. Cause: missed the `Schema="{x:Static vm:XTabViewModel.SchemaSnapshot}"` binding (or never exposed `SchemaSnapshot`). The query *parser* still works for hand-typed expressions, so this fails silently in tests — only manual smoke catches it. Fix: per step 6 above.
4. **The new kind's tab ships, but pre-existing chip-builders on *other* tabs are stale.** Symptom: clicking a chip on a tab *other* than the one being shipped doesn't navigate, even though the kind target is registered and the lookup is correct. Cause: a chip-builder elsewhere hardcoded `IsNavigable: false` / `EntityReference: null`, or rendered through a plain `<TextBlock/>` with a `TODO(stub:#…)` rather than `EntityChip` / `ItemSourceChip`. Fix: the *Audit existing surfaces* grep pass under *Cross-link chips* — replace hardcoded falsy flags with `_navigator.CanOpen(reference)` and upgrade stub `TextBlock`s to `<c:ItemSourceChip .../>`.

## When the cookbook doesn't apply

- **Single-pane tabs** (no master-detail). Not in scope for any Bucket B entry; lorebooks are still master-detail with a long-form body in the detail pane. Revisit this doc if a future tab needs a different shape.
- **Calculator-shaped surfaces** (e.g. a hypothetical Powers / TSys tab). Per [roadmaps/silmarillion.md](roadmaps/silmarillion.md) these are out of scope for Silmarillion; their natural home is Celebrimbor.
- **Cross-module dependencies** (e.g. NPCs cross-links from Arwen's favor service). Pure read access to other modules' DI-exposed services is fine; if a new tab needs to *push* data back, that's a design question outside the cookbook — file an issue first.
