# Silmarillion: recipe-detail keyword chip → Items tab keyword filter

**Tracked in:** #270

**Companion to:** PR #267 / #266 (merged 2026-05-14, recipe-side keyword chip display names + reverse direction). Closes the *forward* symmetry — the inverse direction is already shipped: clicking a keyword chip on an item-detail's "Used as" section filters the Recipes tab by `IngredientKeywords CONTAINS "<keyword>"`.

## Context

The Silmarillion item-detail "Used as" section now has navigable keyword chips. Clicking a chip on Massive Tourmaline's detail page (e.g. "Crystal") flips to the Recipes tab and pre-populates `QueryText = IngredientKeywords CONTAINS "Crystal"`, leveraging #260/#261's collection-`CONTAINS`-via-`IQueryStringValue` plumbing.

The **forward direction is still asymmetric**: when a user opens a recipe's detail page and sees a keyword ingredient chip (e.g. the "Crystal" slot on an enchanting recipe), the chip is rendered but **not clickable** — it carries a sentinel `EntityRef` and `IsNavigable: false`. From [src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs:184-188](../../../src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs#L184-L188):

```csharp
private static EntityChipVm BuildKeywordChip(RecipeKeywordIngredient kwIng)
{
    var label = kwIng.Desc ?? $"any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}";
    return new EntityChipVm(label, IconId: 0, Reference: KeywordSentinelRef, IsNavigable: false);
}
```

The comment that gates this — *"keyword-set ingredients don't point at a single entity, so the chip is non-navigable"* — was correct at the time (pre-#260/#261) but is now obsolete. The collection-CONTAINS path lets a keyword chip mean *"filter the Items tab to items carrying this keyword"* without having to point at a single item.

This plan closes that gap.

## Approach

Mirror the existing keyword-chip → filtered-tab pattern (the one PR #267 introduced for the reverse direction), applied to the inverse:

1. New `EntityKind.ItemKeyword` variant on `EntityRef` (alongside the existing `RecipeIngredientKeyword`). Carries the slot's `ItemKeys` list `+`-joined into `InternalName` (no `ItemKey` value contains `+`, verified against bundled `recipes.json`). Two factories: `EntityRef.ItemKeyword(string)` for singleton callers, `EntityRef.ItemKeyword(IReadOnlyList<string>)` for the composite case.
2. New `ItemKeywordQueryMapper` static helper. **All-or-nothing translation**: takes the slot's `ItemKeys`, returns a query fragment iff *every* key can be mapped to an Items-tab query clause. Mapping rules for v1:
   - Bare tag (no `:`) → `Keywords CONTAINS "<tag>"`
   - `EquipmentSlot:X` → `EquipSlot = "X"` (lossless — `Item.EquipSlot` is a direct queryable string property)
   - Anything else (`MinTSysPrereq:*`, `MaxTSysPrereq:*`, `SkillPrereq:*`, …) → fail the whole slot; chip stays non-navigable
   - Multiple mapped fragments joined with ` AND `
3. New `ItemKeywordKindTarget` (sibling of `RecipeIngredientKeywordKindTarget`). Dispatches to the Items tab (`TabIndex = 0`); calls the mapper, sets `ItemsTabViewModel.QueryText`; clears `SelectedItem` (the navigation expresses a filter, not a row pick). Returns `false` from `TrySelectByInternalName` if the mapper fails (kept as a defensive belt — the chip-builder is the gate).
4. Update `RecipesTabViewModel.BuildKeywordChip` to call the mapper to determine `IsNavigable`, then emit `EntityRef.ItemKeyword(itemKeys)`. Display label preserved (`kwIng.Desc ?? $"any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}"`).
5. Register the new kind target in `SilmarillionModule.cs` DI.

The end state:
- "Crystal" slot chip → clickable → Items tab with `Keywords CONTAINS "Crystal"`.
- "Main-Hand Item" slot chip (`[EquipmentSlot:MainHand, MinTSysPrereq:0]`) → **non-navigable** (because `MinTSysPrereq:0` has no item-side mapping). Honest UX: if a chip can't faithfully express its slot constraint, it stays inert. Future expansion can promote it as item-side analogues land.
- Hypothetical singleton `[EquipmentSlot:MainHand]` chip (no prereq) → clickable → `EquipSlot = "MainHand"`. Not present in today's catalog but supported by the mapping.

## Why this works under PR #267's plumbing

- `Item.Keywords` is `IReadOnlyList<ItemKeyword>`, and `ItemKeyword(string Tag, int Quality)` already implements `IQueryStringValue` via `Tag` (per `Mithril.Reference/Models/Items/ItemKeyword.cs`). The query engine's collection-CONTAINS path (`QueryCompiler` in `Mithril.Shared.Wpf.Query`) picks this up automatically.
- The kind-target pattern is well-established. `RecipeIngredientKeywordKindTarget` ([src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs](../../../src/Silmarillion.Module/Navigation/RecipeIngredientKeywordKindTarget.cs)) is the template — copy and invert.
- `ItemsKindTarget` already exists for the entity (specific item) direction. The new target is its filter-action sibling.

## Files to touch

| Path | Change |
|---|---|
| `src/Mithril.Shared/Reference/EntityRef.cs` | Add `EntityKind.ItemKeyword` (append to the end of the enum to preserve ordinals). Two factories: `ItemKeyword(string)` and `ItemKeyword(IReadOnlyList<string>)` (`+`-joins for the latter). |
| `src/Silmarillion.Module/Navigation/ItemKeywordQueryMapper.cs` (new) | Static helper. `TryBuildQuery(IReadOnlyList<string> itemKeys, out string? query)`. All-or-nothing semantics described above. |
| `src/Silmarillion.Module/Navigation/ItemKeywordKindTarget.cs` (new) | Sibling of `RecipeIngredientKeywordKindTarget`. `Kind => EntityKind.ItemKeyword`, `TabIndex => 0`. `TrySelectByInternalName` splits the `+`-encoded `internalName`, calls the mapper; on success sets `_vm.SelectedItem = null` then `_vm.QueryText = <query>`; returns false otherwise. `TryOpenInWindow() => false`. |
| `src/Silmarillion.Module/SilmarillionModule.cs` | Register the new target alongside the existing `IReferenceKindTarget` registrations (mirror the `RecipeIngredientKeywordKindTarget` factory-lambda style). |
| `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs` | `BuildKeywordChip` becomes instance method (needs the mapper + navigator to compute `IsNavigable`). Always emits `EntityRef.ItemKeyword(ItemKeys)`; `IsNavigable` = mapper succeeded AND `_navigator.CanOpen(ref)`. Display label preserved as today (`kwIng.Desc ?? $"any {ItemKeywordIndex.Humanise(kwIng.ItemKeys)}"`). |
| `tests/Mithril.Shared.Tests/Reference/EntityRefTests.cs` | Test both `EntityRef.ItemKeyword` overloads (singleton + list). |
| `tests/Silmarillion.Tests/Navigation/ItemKeywordQueryMapperTests.cs` (new) | Mapper coverage: bare-tag, EquipmentSlot map, unmappable token → fail whole slot, multi-key AND-join, empty slot. |
| `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` | Tests mirroring the existing `Open_RecipeIngredientKeyword_*` pair: `Open_ItemKeyword_SwitchesToItemsTab_AndSetsQueryText` (singleton), `Open_ItemKeyword_ClearsResidualSelectedItem`, plus `Open_ItemKeyword_CompositeSlotWithEquipmentSlot_SetsAndJoinedQuery`. |
| `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs` | Cover: (a) singleton keyword slot produces a navigable chip with `EntityRef.ItemKeyword(tag)`; (b) composite slot of all-mappable keys produces a navigable chip; (c) composite tuple containing an unmappable key (`MinTSysPrereq:*`) stays non-navigable; (d) chip display label unchanged from pre-existing behaviour. |

## Resolved design question — composite-tuple slots

**Decision (2026-05-14, during plan refinement):** v1 *does* navigate composite slots when every key is translatable. The plan's original "out of scope" stance was overcautious — it assumed virtual keys couldn't be expressed in the Items query, but `Item.EquipSlot` is a direct queryable string property, so `EquipmentSlot:X` maps cleanly onto `EquipSlot = "X"`.

The all-or-nothing rule (any unmappable key → entire chip non-navigable) keeps the UX honest:

| Slot pattern | Result |
|---|---|
| `["Crystal"]` (singleton bare tag) | navigable → `Keywords CONTAINS "Crystal"` |
| `["EquipmentSlot:MainHand"]` (hypothetical singleton) | navigable → `EquipSlot = "MainHand"` |
| `["Crystal", "Tier3"]` (composite all-bare, hypothetical) | navigable → `Keywords CONTAINS "Crystal" AND Keywords CONTAINS "Tier3"` |
| `["EquipmentSlot:MainHand", "MinTSysPrereq:0"]` (real catalog) | **non-navigable** — `MinTSysPrereq:N` has no item-side analogue |

`MinTSysPrereq` / `MaxTSysPrereq` are item-level concepts (per-item TSys tier prereq) but **not currently exposed on `Item`**. When/if they're surfaced, the mapper can be extended; chips that are inert today will quietly become clickable with no chip-builder change.

`SkillPrereq:X` is also unmappable for v1 (`Item.SkillReqs` is a dictionary, no simple equality clause). Treated the same way: chip stays inert.

## Test plan

1. Build clean: `dotnet build Mithril.slnx` (warnings-as-errors on).
2. Targeted tests: `dotnet test tests/Silmarillion.Tests`.
3. Full suite: `dotnet test Mithril.slnx`.
4. Manual:
   - `dotnet run --project src/Mithril.Shell`.
   - Silmarillion → Recipes tab → open an enchanting recipe (e.g. "Enchant Boots" or any `*E`/`*Max` recipe).
   - The "Crystal" ingredient chip should now render as clickable (hover highlight, hand cursor).
   - Click it → Items tab opens, query box reads `Keywords CONTAINS "Crystal"`, filtered list shows every Crystal item.
   - Refine query (`AND Skill = "..."` etc.) — composes naturally.
   - Clear query box → full Items list returns.
   - Verify composite slots that include unmappable tokens (e.g. the "Main-Hand Item" slot on `Decompose Main-Hand Weapon` / `recipe_10501`, ItemKeys `[EquipmentSlot:MainHand, MinTSysPrereq:0]`) stay non-clickable — `MinTSysPrereq:0` blocks the mapper. The chip still renders its `Desc` ("Main-Hand Item") as text.

## Workflow

1. **File the issue first** (`gh issue create`) with `type:feature` + `module:silmarillion` + `area:ui` labels. Title suggestion: `Silmarillion recipe-detail keyword chip should filter Items tab (symmetric to PR #267)`. Body summarises the asymmetry, links to PR #267 and this plan.
2. Branch from main: `feat/<issue-number>-recipe-keyword-chip-to-items-filter`. Branch policy forbids direct commits to main — always PR.
3. TDD as the existing kind-target tests do: write the failing test first, then implement, then run.
4. Land as a single PR. Reasonable size (~150 LoC across new files + edits).

## Related memories and references

- Branch policy: `~/.claude/projects/i--src-project-gorgon/memory/branch_policy_no_direct_commits.md`
- Collaboration style (frequent commits, present trade-offs before building): `collaboration_style.md`
- WPF gotchas worth remembering: `wpf_run_text_default_twoway.md`, `wpf_virtualizing_wrap_panel.md`, `wpf_combobox_displaymember_quirk.md`
- Query system three surfaces: `query_system_overview.md` (pointer to `docs/query-system.md`)

## Out of scope

- Mapping additional virtual-key prefixes (`SkillPrereq:*`, `MinValue:*`, `MinRarity:*`, `MinTSysPrereq:*`, `MaxTSysPrereq:*`). The mapper's `EquipmentSlot:` mapping is the only one with a clean item-side analogue today. The others would need either new `Item` columns or schema-level synthesized-keyword exposure — separate feature.
- The "chip display name vs query token" UX wrinkle is tracked separately in **#268** (the chip might show "Crystal" but the query box would read `Keywords CONTAINS "Crystal"`; for keywords like "MetalArmor" the chip says "Metal Armor" but the query has the raw `MetalArmor` — same issue as #268 for the inverse direction). Don't address here.
- Typed `StringRef` pattern (broader infra, low urgency) — tracked in **#265**.

## What got us here

This session (2026-05-13 → 2026-05-14):

- PR #259 (merged) — shipped the "Used as" keyword-chip section on the item-detail view. Used singleton-slot Descs + strings_all + CamelCaseSplit fallback for display names.
- PR #267 (merged) — built on #259 with: hardcoded `KeywordDisplayOverrides` table for cases where slot Descs are misleading (`Crystal` → "Auxiliary Crystal" is a slot role, not a keyword name); most-common-Desc-wins resolution; drift-detector unit test against real bundled data; kind-target navigation-state clearance so direct links don't land selection on filtered-out rows.
- Issues filed: #265 (typed StringRef pattern, low urgency), #266 (closed by #267), #268 (chip display vs query token wrinkle, low urgency).

This plan closes the natural symmetry that emerged at the end of PR #267's review.
