# Silmarillion: Effects tab (#244)

**Tracked in:** #244 — `module:silmarillion` / `area:ui` / `type:feature`.

**Companion docs:**
- [silmarillion-tab-cookbook.md](../silmarillion-tab-cookbook.md) — the scaffolding pattern. Read this first; this handoff only covers the *Effects-specific* decisions.
- [roadmaps/silmarillion.md](../roadmaps/silmarillion.md) — Bucket B, sequenced after Abilities (which shipped via #293). The Effects tab is the natural rendering destination for the conditional-rule sub-tables plumbed by #288/#296 (`AbilityDynamicDots`, `AbilityDynamicSpecialValues`).

## ⚠️ Critical — the issue body has four wrong assumptions

The original #244 issue body was drafted pre-data-shape-verification and pre-#239 (the kind-target registry refactor). **Don't follow it literally; it contains category errors of the same class as #243/#288.** Three concern the issue body itself; the fourth is a misuse of the existing `EntityRef.Effect` factory by pre-existing call sites, which this PR fixes alongside the tab work. Specifically:

### Wrong assumption 1 — "AbilitiesApplyingEffect" reverse lookup

Abilities **do not reference effects by InternalName**. They reference them by *keyword*:
- [`Ability.EffectKeywordReqs`](../../src/Mithril.Reference/Models/Abilities/Ability.cs#L98) — `IReadOnlyList<string>?`. "Required keyword to be present on an active effect."
- [`Ability.EffectKeywordsIndicatingEnabled`](../../src/Mithril.Reference/Models/Abilities/Ability.cs#L73) — same shape.
- [`Ability.TargetEffectKeywordReq`](../../src/Mithril.Reference/Models/Abilities/Ability.cs#L92) — singleton string variant.

Plus three POCOs with effect-keyword predicates already known via `AbilityDetailViewModel` chip-stub work:
- `EffectKeywordUnsetAbilityRequirement.EffectKeyword` (an `AbilitySpecialCasterRequirement` subclass)
- `HasEffectKeywordAbilityRequirement.EffectKeyword` (same)
- `AbilityConditionalKeyword.EffectKeywordMustExist` / `EffectKeywordMustNotExist`

The reverse lookup is therefore **keyword-set intersection**, not InternalName foreign-key. Naming: `AbilitiesByEffectKeyword` (`string → IReadOnlyList<Ability>`) keyed by individual keyword tag, queried by intersecting the *target effect's* `Effect.Keywords` against it.

### Wrong assumption 2 — "ItemsApplyingEffect" reverse lookup

Items do not reference effects at all. [`Item.EffectDescs`](../../src/Mithril.Reference/Models/Items/Item.cs#L50) is a list of **procedural placeholder strings** (e.g. `"{MAX_ARMOR}{49}"`) resolved at render time via `attributes.json`. The strings are *not* foreign keys to `effects.json`. There is no clean data-shape join from effect to item.

**Drop this section from v1.** A possible future direction: parse `EffectDescs` placeholder tokens, cross-reference against the `Attribute` table, and surface "items that boost attributes referenced by this effect's keyword rules" — but that's at least one tab-PR worth of work, several heuristics deep, and belongs in a follow-up issue.

### Wrong assumption 3 — "reuse the Celebrimbor ResultEffects renderer"

[`ResultEffectsParser`](../../src/Mithril.Shared/Reference/ResultEffectsParser.cs) parses **recipe** outputs (`Recipe.ResultEffects` — method-call-style strings like `"GiveTSysItem(GoblinSword1)"`). It has no overlap with `effects.json` entries; "Effect" in the name refers to recipe-result effects, not the Effect entity. **Do not import it into the Effects tab.**

The natural rendering precedents for Effect detail are:
- [`AbilityDetailViewModel`](../../src/Silmarillion.Module/ViewModels/AbilityDetailViewModel.cs) for the wide-POCO row pattern.
- [`QuestDetailProjector`](../../src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs) for chip projections.

### Wrong assumption 4 — `EntityRef.Effect` is currently misused by call sites that pass a keyword

[`QuestDetailProjector.cs:264`](../../src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs#L264) and [`tests/Mithril.Shared.Tests/Reference/ReferenceDataEntityNameResolverTests.cs:229`](../../tests/Mithril.Shared.Tests/Reference/ReferenceDataEntityNameResolverTests.cs#L229) both construct `EntityRef.Effect(keyword)` — passing a **keyword** (e.g. `"FrostShard"`) as the `InternalName` payload. This was a stub anchor written knowing the Effects tab wasn't yet shipped; the call site even has a comment saying so.

But the Effects tab's natural entity-anchor is the **envelope key** (`effect_10003`), not a keyword. `Effect.Keywords` is many-to-many — `"Buff"` alone matches thousands of entries. A keyword cannot select a row; it can only filter the tab.

**Resolution: split into two kinds**, mirroring the `RecipeIngredientKeyword` / `Recipe` and `ItemKeyword` / `Item` splits that already exist:

| Kind | Payload | Target |
|---|---|---|
| `EntityKind.Effect` (existing, repurposed) | envelope key (e.g. `effect_10003`) | Effects tab row-select |
| `EntityKind.EffectKeyword` (new synthetic) | keyword tag (e.g. `FrostShard`) | Effects tab filtered to `Keywords CONTAINS "<tag>"` |

This means the **existing call site at `QuestDetailProjector.cs:264` migrates to `EntityRef.EffectKeyword(keyword)`** as part of this PR, not a follow-up. Same for the resolver test fallback line.

## Effect POCO shape — what's available

[`Mithril.Reference.Models.Effects.Effect`](../../src/Mithril.Reference/Models/Effects/Effect.cs):

```csharp
public sealed class Effect
{
    public string? Desc { get; set; }           // long-form description; may contain <i>/<b> markup
    public string? DisplayMode { get; set; }    // e.g. "Effect"
    public int IconId { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }  // chip set, predicate target for #296 rules
    public string? Name { get; set; }            // friendly name, not unique
    public string? Duration { get; set; }        // int seconds OR "Permanent" — STJ-converted to string

    /// <summary>
    /// Gates which source-ability keyword set is eligible to *trigger* this effect's
    /// procs. Verified pattern (bundled JSON): entries shaped `"AbilityKeywords":["Attack"]`
    /// pair with Desc text like "5% of your attacks deal +50% damage". Semantically:
    /// "this effect's behavior fires only when the triggering ability matches one of
    /// these keywords." NOT the same axis as <see cref="Keywords"/>, which describes
    /// the effect itself. Render on Effect detail as a small "Procs from abilities with
    /// keyword: [chip]" row using <c>EntityRef.AbilityByKeyword(tag)</c> if/when that
    /// synthetic kind exists, else <c>EffectKeyword</c>-style filter-on-Abilities-tab.
    /// </summary>
    public IReadOnlyList<string>? AbilityKeywords { get; set; }

    public int? StackingPriority { get; set; }
    public string? StackingType { get; set; }    // group key
    public string? Particle { get; set; }
    public string? SpewText { get; set; }        // combat float text; see § SpewText, below
}
```

### `InternalName` lift — architectural amendment to the issue body

The Effect POCO ships without an `InternalName` field today. Every other Silmarillion-tabbed kind has one (true field on the POCO, or lifted from the envelope key by the deserializer like [`Item.Id`](../../src/Mithril.Reference/Models/Items/Item.cs#L23)). Leaving Effects out creates structural asymmetry in `EntityRef`, kind targets, and the deep-link grammar.

**Add `string? InternalName` to `Effect.cs` and lift the envelope key onto it in `ReferenceDeserializer.ParseEffects`** — mirror the lift in [`ParseItems`](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L85-L110) but storing the raw envelope key (e.g. `"effect_10003"`) since there's no human-form name to extract:

```csharp
foreach (var pair in result)
    pair.Value.InternalName = pair.Key;
```

After this lift, Effects look like every other entity:
- `EntityRef.Effect("effect_10003")` carries an `InternalName` like any other ref.
- `IReferenceDataService.EffectsByInternalName` is a sibling to `Effects` (envelope-keyed) — pattern matches Item / Quest / Ability.
- Kind target `TrySelectByInternalName` reads from `EffectsByInternalName` against the bound `AllEffects` collection.
- Deep-link URL `mithril://silmarillion/effect/effect_10003` flows through `SilmarillionDeepLinkHandler` with no special-casing — the handler uses `Enum.TryParse<EntityKind>` + `new EntityRef(...)` and is target-agnostic.

The `effect/effect_10003` URL is cosmetically redundant but **architecturally unavoidable**: PG's envelope grammar uses `effect_NNNN` as the unique identifier and there's no human-form name to substitute. This is no worse than a hypothetical quest with `InternalName = "Quest_Foo"` — a property of the source data, not a structural quirk of the route.

Bundled file is ~23,069 entries — second-largest after `items.json`. Same virtualization considerations as the Abilities tab; the [`VirtualizingWrapPanel`](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) pattern AbilitiesTabView uses applies here too.

## Scope

### 1. Service-layer plumbing on `IReferenceDataService`

Add these properties, all with empty-default interface fallbacks per the cookbook *Test scaffolding → non-rippling default* pattern:

```csharp
/// <summary>Effect envelope key (e.g. "effect_10003") → Effect POCO.</summary>
IReadOnlyDictionary<string, Effect> Effects => EmptyEffectMap;

/// <summary>Effect.InternalName → Effect POCO. Same shape as
/// <see cref="ItemsByInternalName"/> / <see cref="AbilitiesByInternalName"/>.
/// After the InternalName lift (see "InternalName lift" section), this is the
/// primary lookup driving kind-target row-select.</summary>
IReadOnlyDictionary<string, Effect> EffectsByInternalName => EmptyEffectMap;

/// <summary>Effect.Keywords flat tag → effects carrying that tag. Powers the
/// EffectKeyword synthetic kind's filter (and the on-detail "Other effects with
/// this keyword" sidebar if you decide to surface it).</summary>
IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByKeyword => EmptyEffectIndex;

/// <summary>Effect.StackingType → all effects sharing that stacking group.
/// Powers the on-detail "Stacks with" / "Overridden by" sections.</summary>
IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByStackingType => EmptyEffectIndex;

/// <summary>Effect keyword → abilities whose EffectKeywordReqs ∪
/// EffectKeywordsIndicatingEnabled ∪ TargetEffectKeywordReq contains the keyword.
/// <b>Excludes</b> abilities with <c>InternalAbility == true</c> (engine-internal
/// scaffolding — mob skills, mount transitions; no player-facing display name and
/// pollutes the chip cluster). Powers the on-detail "Required by abilities"
/// section. Built whenever abilities.json or effects.json reloads.</summary>
IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesByEffectKeyword => EmptyAbilityIndex;

/// <summary>Ability keyword → effects whose <see cref="Effect.AbilityKeywords"/>
/// list contains it. Powers the on-detail "Procs from abilities with keyword"
/// section (which abilities can trigger this effect's procs). Reverse of
/// <see cref="AbilitiesByEffectKeyword"/> — that one says "abilities that gate
/// on having this effect," this one says "abilities that trigger this effect's
/// behavior." Built whenever effects.json reloads.</summary>
IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByTriggeringAbilityKeyword => EmptyEffectIndex;
```

The reverse-index rebuild trigger matrix (extend the cookbook table):

| Index | Triggers rebuild from |
|---|---|
| `EffectsByKeyword`, `EffectsByStackingType`, `EffectsByTriggeringAbilityKeyword` | `effects.json` |
| `AbilitiesByEffectKeyword` | `abilities.json`, `effects.json` |

Implementation lives in `ReferenceDataService.ParseAndSwapEffects` + a `BuildEffectAbilityCrossLinkIndices` helper called from both `ParseAndSwapEffects` and `ParseAndSwapAbilities`. Mirror the existing `BuildRecipeCrossLinkIndices` / `BuildAbilityCrossLinkIndices` shape.

`effects.json` parser already exists ([`ReferenceDeserializer.ParseEffects`](../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L250-L258)). Wire it into `LoadEffects` / `ParseAndSwapEffects` / `RefreshAsync` switch / `RefreshAllAsync` / `Keys` list / `GetSnapshot` switch — same checklist as #288/#296 followed for the rule files, just with the cross-link index step added.

### 2. Synthetic `EntityKind.EffectKeyword` + kind target

Append to [`EntityRef.cs`](../../src/Mithril.Shared/Reference/EntityRef.cs):

```csharp
EffectKeyword,
```

Plus the factory:

```csharp
public static EntityRef EffectKeyword(string keyword) => new(EntityKind.EffectKeyword, keyword);
```

New file `src/Silmarillion.Module/Navigation/EffectKeywordKindTarget.cs`, mirroring [`ItemKeywordKindTarget`](../../src/Silmarillion.Module/Navigation/ItemKeywordKindTarget.cs) almost exactly:

- `Kind => EntityKind.EffectKeyword`
- `TabIndex => 5` (Effects tab — see *Tab order*, below)
- `TrySelectByInternalName(string keyword)`: clear `SelectedEffect`, set `_vm.QueryText = $"Keywords CONTAINS \"{keyword}\""`. Return `true` if the keyword is in `_refData.EffectsByKeyword`, `false` otherwise.
- `TryOpenInWindow() => false`.

No `ItemKeys+`-joined composite form is needed — quest/ability effect-keyword references are always singleton tags, never composite slots.

### 3. `EntityKind.Effect` kind target (entity row-select)

New file `src/Silmarillion.Module/Navigation/EffectsKindTarget.cs`, mirroring [`AbilityKindTarget`](../../src/Silmarillion.Module/Navigation/AbilityKindTarget.cs):

- `Kind => EntityKind.Effect`
- `TabIndex => 5`
- `TrySelectByInternalName(string envelopeKey)`: look up against the tab VM's `AllEffects` bound collection (per the cookbook's *Pattern walkthrough → ItemsKindTarget.cs:33-39* caveat — don't resolve against `_refData.Effects` directly because background refreshes swap instances). Clear `QueryText` first to avoid filtering the target row out.
- `TryOpenInWindow()`: open `EffectDetailWindow` against the current `DetailViewModel`.

#### Selection contract

After the InternalName lift (see *Effect POCO shape*, above), the kind target's `TrySelectByInternalName(string)` parameter is the **lifted InternalName** for Effect — which equals the envelope key (`"effect_10003"`). Document the contract on the factory **and add a Debug.Assert guard** so the misuse pattern (passing a keyword or Name) is caught at construction site, not at first navigation attempt:

```csharp
/// <summary>
/// Effect InternalName — equal to the envelope key (e.g. "effect_10003"), lifted from
/// effects.json by the deserializer (no human-form name exists in source data). NOT a
/// keyword tag and NOT Effect.Name. For keyword-based deep-link filtering use
/// <see cref="EffectKeyword"/> instead.
/// </summary>
public static EntityRef Effect(string internalName)
{
    Debug.Assert(
        internalName.StartsWith("effect_", StringComparison.Ordinal),
        $"EntityRef.Effect expects an envelope-key InternalName like 'effect_10003', got '{internalName}'. " +
        "If you have a keyword tag, use EntityRef.EffectKeyword(...) instead.");
    return new(EntityKind.Effect, internalName);
}
```

Cross-reference: when a chip wants to point at "the effect for this gameplay-loop event," it almost always means "filter the Effects tab to this keyword" — use `EffectKeyword`. Only when the consumer holds a specific `effect_NNNN` row (e.g. a future per-effect link from another entity) does `Effect` apply.

### 4. Tab VM + view + detail VM + view

Standard cookbook scaffolding (steps 1-6 of the cookbook checklist). The mechanical bits:

#### `EffectsTabViewModel.cs` (mirror `AbilitiesTabViewModel`)

```csharp
public sealed partial class EffectsTabViewModel : ObservableObject, ITabViewModel
{
    public string TabHeader => "Effects";
    public int TabOrder => 5;

    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(ColumnBindingHelper.BuildFromProperties(typeof(EffectListRow)));
    // ...
}
```

Subscribe `FileUpdated` to `"effects"` (rebuilds `AllEffects` on the UI thread, preserves selection by envelope key) and `"abilities"` (rebuilds the on-detail "Required by abilities" cross-link). Selection preservation key is the envelope key, not `Effect.Name` — names collide (multiple `"Riposte!"` effects exist).

#### `EffectListRow` row record

The reflected-schema row exposed to `MithrilQueryBox`. Minimum fields:

```csharp
public sealed record EffectListRow(
    string EnvelopeKey,                    // "effect_10003" — selection key
    string DisplayName,                    // Effect.Name, fallback to envelope key
    int IconId,
    string? StackingType,
    string? Duration,
    IReadOnlyList<EffectKeywordValue> Keywords);  // IQueryStringValue wrapper for CONTAINS
```

Wrap individual keyword strings in an `EffectKeywordValue` record implementing `IQueryStringValue` (mirror [`IngredientKeywordValue`](../../src/Silmarillion.Module/ViewModels/IngredientKeywordValue.cs)) so the query box supports `Keywords CONTAINS "Buff"`.

#### `EffectDetailViewModel` sections (in order)

1. **Header** — Icon, Name, envelope key in mono small footer (per the *Detail-view internal-name footer convention* memory).
2. **Description** — `Effect.Desc` rendered through the `FormattedText` attached property (the cookbook warns to grep for plain `Text="{Binding Description}"` consumers — Effect descs may carry `<i>...</i>` markup).
3. **Metadata strip** — Duration (special-case `"Permanent"` string vs int seconds → "30 seconds" / "5 minutes" / "1 hour" via simple bucketing), Stacking type chip, Display mode chip when non-default. Filter out default values per the cookbook *Default-value noise filtering* rule (`DisplayMode == "Effect"` is the universal default; null it out so the chip hides).
4. **Keywords** — chip row, each chip is `EntityChipVm` with `Reference = EntityRef.EffectKeyword(tag)` and `IsNavigable = _navigator.CanOpen(reference)`. Resolves to "open Effects tab filtered to this keyword" via the synthetic kind. Apply `KeywordDisplayOverrides`-style friendly-name resolution if you discover collisions; otherwise CamelCase-split the raw tag for display (mirror [`KeywordDisplayOverrides`](../../src/Silmarillion.Module/ViewModels/KeywordDisplayOverrides.cs) shape from #267).
5. **Conditional rules that fire on this effect** — feed from #296's plumbed `AbilityDynamicDots` and `AbilityDynamicSpecialValues`. Filter via `AbilityRulePredicate.Matches(rule.ReqEffectKeywords, effect.Keywords)`. Two sub-sections:
   - "DoT effects layered on by abilities" — list each matching `AbilityDynamicDot`'s `DamagePerTick × NumTicks ticks of DamageType, gated by [ReqAbilityKeywords]`.
   - "Tooltip values surfaced by abilities" — list each `AbilityDynamicSpecialValue`'s `"<Label> <Value> <Suffix>" gated by [ReqAbilityKeywords]`.
   These are predicate-rule rows, not entity links — render as plain projected text inside an `ItemsControl`. Skip when both lists are empty.
6. **Stacks with** — other effects sharing `StackingType` (excluding self). Renders as `EntityChip` rows with `EntityRef.Effect(otherInternalName)`. Skip when count ≤ 1.
7. **Required by abilities** — for each keyword in `effect.Keywords`, look up `AbilitiesByEffectKeyword[keyword]`, union, dedupe by `InternalName`, sort by `Skill` then `Level`. The index already excludes `InternalAbility == true` rows (engine scaffolding — see the index doc comment), so the chip cluster carries player-facing entries only. Cap at `SilmarillionSettings.UsedInChipCap` (default 12) with a `+{N-cap} more →` overflow pill that deep-links via a new synthetic `EntityKind` (see *Open question 1*, below) or stays plain text in v1.

   This is **the section the issue body called "AbilitiesApplyingEffect"** — same intent, different join mechanic.

8. **Procs from abilities with keyword** — for each tag in `effect.AbilityKeywords` (e.g. `"Attack"` on `effect_12229` Augury), render an `EntityChipVm` row. Skip when `AbilityKeywords` is null/empty (the common case — only ~few hundred effects carry this).
9. **Spew text** — `Effect.SpewText` rendered as small italic floating-text prose at the bottom when present. Verified against bundled data: SpewText values are user-facing combat float text (`"%NAME% is Off-Guard!"`, `"REGENERATION"`, `"+EVASION"`, `"+DAMAGE!"`, `"BURST REPLY"`), not engine keys. `%NAME%` is a placeholder for actor name — leave literal in detail rendering (no live actor context here) and document it as `<i>"%NAME% is Off-Guard!"</i>` style.

#### `EffectsTabView.xaml` + `EffectDetailView.xaml`

Mirror [`AbilitiesTabView.xaml`](../../src/Silmarillion.Module/Views/AbilitiesTabView.xaml) line-for-line for the list-pane structure (`MithrilQueryBox` top, virtualized `ListBox` left, `ScrollViewer` right with detail). Detail view's section layout follows [`AbilityDetailView.xaml`](../../src/Silmarillion.Module/Views/AbilityDetailView.xaml)'s `DockPanel` + footer pattern.

### 5. DI registration in `SilmarillionModule.Register`

Three new lines, in the order the cookbook prescribes:

```csharp
services.AddSingleton<EffectsTabViewModel>();
// ... existing AddSingleton<SilmarillionViewModel>() stays ...
services.AddSingleton<ITabViewModel>(sp => sp.GetRequiredService<EffectsTabViewModel>());
services.AddSingleton<IReferenceKindTarget>(sp => new EffectsKindTarget(
    sp.GetRequiredService<EffectsTabViewModel>(),
    sp.GetService<IDiagnosticsSink>()));
services.AddSingleton<IReferenceKindTarget>(sp => new EffectKeywordKindTarget(
    sp.GetRequiredService<EffectsTabViewModel>(),
    sp.GetService<IDiagnosticsSink>()));
```

Per cookbook step 4: add `<DataTemplate DataType="{x:Type vm:EffectsTabViewModel}"><local:EffectsTabView/></DataTemplate>` to [`SilmarillionView.xaml`](../../src/Silmarillion.Module/Views/SilmarillionView.xaml)'s `UserControl.Resources`.

The `SilmarillionViewModel` ctor doesn't change — `ITabViewModel` enumerable composition is the post-#293 default. `Tabs` recomposes itself from DI; tab strip ordering follows `TabOrder`.

#### Tab order

| Idx | Tab | TabOrder |
|---|---|---|
| 0 | Items | 0 |
| 1 | Recipes | 1 |
| 2 | NPCs | 2 |
| 3 | Quests | 3 |
| 4 | Abilities | 4 |
| 5 | **Effects** (this PR) | 5 |

`EntityRef.Skill`'s resolver case is wired in but has no tab today — that lands when (and if) a Skill tab is added later, separate issue.

### 6. Audit pre-existing surfaces (per cookbook *Cross-link chips → audit existing surfaces*)

Before opening the PR, grep for all stale call sites and migrate them:

| Call site | Current | Migrate to |
|---|---|---|
| [`QuestDetailProjector.cs:264`](../../src/Silmarillion.Module/ViewModels/QuestDetailProjector.cs#L264) — `HasEffectKeywordRequirement` chip | `EntityRef.Effect(h.Keyword!)` + comment "registered tab `(#244)` not present" | `EntityRef.EffectKeyword(h.Keyword!)`; delete the wait-for-#244 comment |
| [`AbilityDetailViewModel.cs:93`](../../src/Silmarillion.Module/ViewModels/AbilityDetailViewModel.cs#L93) — `EffectKeywordReqsDisplay` plain-string `string.Join(", ", ...)` | plain text | Convert to `IReadOnlyList<EntityChipVm>` with `EntityRef.EffectKeyword(tag)` per entry. Wire the chip ItemsControl in [`AbilityDetailView.xaml:268`](../../src/Silmarillion.Module/Views/AbilityDetailView.xaml#L268). |
| `AbilityDetailViewModel.cs:347` — `TargetEffectKeywordReq` "Target effect keyword" flag row | plain string | Same upgrade: chip with `EntityRef.EffectKeyword(...)`. |
| `AbilityDetailViewModel.cs:271-273` — `EffectKeywordMustExist` / `EffectKeywordMustNotExist` conditional descriptions | plain string `$"When effect keyword present: {c.EffectKeywordMustExist}"` | Migrate in the same PR. Once `EntityKind.EffectKeyword` exists, these are ~4-line changes each. Leaving them text creates an orphaned half-migration where every other effect-keyword surface is a chip and these two stay text. Do them. |
| [`tests/Mithril.Shared.Tests/Reference/ReferenceDataEntityNameResolverTests.cs:229`](../../tests/Mithril.Shared.Tests/Reference/ReferenceDataEntityNameResolverTests.cs#L229) | `EntityRef.Effect("FrostShard")` (asserts raw-passthrough fallback) | Update to `EntityRef.Effect("effect_10003")` against bundled-fixture data, OR keep the existing assertion semantics with `EntityRef.EffectKeyword("FrostShard")` — whichever reads cleaner. |

Add `EntityKind.Effect` to [`ReferenceDataEntityNameResolver.cs`](../../src/Mithril.Shared/Reference/ReferenceDataEntityNameResolver.cs) — `_refData.Effects.TryGetValue(envelopeKey, out var effect)`, fall back to envelope key. `EffectKeyword` doesn't need a resolver case (it's a payload-as-display kind; the synthetic kind target reads it as a tag).

### 7. `mithril://silmarillion/effect/<envelope-key>` deep-link route

The [`SilmarillionDeepLinkHandler`](../../src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs) (introduced via #229) is target-agnostic — it parses `(kind, name)` and calls `_navigator.Open(new EntityRef(kind, name))`. The new kind picks up automatically as long as `EntityKind.Effect` parses from the URL segment `"effect"`. Verify by running through the existing path-parser test; no handler code changes expected. Add a deep-link round-trip test under [`tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs`](../../tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs) for the `effect_NNNN` form.

`effectkeyword` is **not** a routable deep-link target by default — keywords are filter pivots, not URL-stable entities. If you want one anyway, add it explicitly in the handler's `IsKnownKind` set with an explanatory comment. Defer unless there's a concrete need.

## Tests — the standard trio plus the cross-link index test

Per cookbook:

1. **`tests/Silmarillion.Tests/ViewModels/EffectsTabViewModelTests.cs`** — list construction, sort order, keyword-CONTAINS filter, `FileUpdated` re-bind preserves selection by envelope key, detail-VM build cross-link projection. Stub `IReferenceDataService` via `StubReferenceData` (extend it with `Effects` / `EffectsByKeyword` / `EffectsByStackingType` / `AbilitiesByEffectKeyword` properties — the interface defaults mean other tests don't ripple).

2. **`tests/Silmarillion.Tests/Navigation/EffectsKindTargetTests.cs`** — `Kind` / `TabIndex` properties, `TrySelectByInternalName` (hit by envelope key, miss → returns false), `TryOpenInWindow` (with and without current detail). Mirror [`AbilityKindTargetTests`](../../tests/Silmarillion.Tests/Navigation/AbilityKindTargetTests.cs).

3. **`tests/Silmarillion.Tests/Navigation/EffectKeywordKindTargetTests.cs`** — `Kind` / `TabIndex` / `TrySelectByInternalName` (mutates `QueryText`, returns true for known keyword), `TryOpenInWindow() => false`. Mirror [`ItemKeywordKindTargetTests`](../../tests/Silmarillion.Tests/Navigation/ItemKeywordKindTargetTests.cs).

4. **Extend `SilmarillionReferenceNavigatorTests`** — verify the duplicate-registration guard still trips with both new kinds in the mix. Add `Open_Effect_SwitchesToEffectsTab_AndSelects` and `Open_EffectKeyword_SwitchesToEffectsTab_AndSetsQueryText` tests mirroring the existing Ability + ItemKeyword test pairs.

5. **`tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceEffectCrossLinkIndexTests.cs`** (new) — synthetic-fixture round-trip for the five new index properties. Canonical precedent: [`ReferenceDataServiceAbilityCrossLinkIndexTests`](../../tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceAbilityCrossLinkIndexTests.cs) — that file uses an in-test temp `_bundledDir` and writes JSON fixtures inline (not real bundled data), so no `[SkippableFact]` is needed. Tests:
   - `EffectsByKeyword_GroupsByEachKeywordTag` — an effect with `Keywords = ["Buff", "OrranInv"]` appears under both keys.
   - `EffectsByStackingType_GroupsByStackingType` — entries sharing `StackingType = "WordOfPowerInventory"` co-locate.
   - `AbilitiesByEffectKeyword_IndexesEffectKeywordReqs` — union of `EffectKeywordReqs`, `EffectKeywordsIndicatingEnabled`, `TargetEffectKeywordReq` against a synthesised ability fixture.
   - `AbilitiesByEffectKeyword_ExcludesInternalAbilities` — an ability with `InternalAbility = true` and a matching `EffectKeywordReqs` is omitted from the index.
   - `EffectsByTriggeringAbilityKeyword_IndexesEffectAbilityKeywords` — Effect with `AbilityKeywords = ["Attack"]` appears under `"Attack"`.
   - `RefreshAbilitiesOrEffects_RebuildsIndex` — proves the cross-trigger matrix.

6. **Real-data sanity walk test** (cookbook *Verification ladder → step 4*) — `RealBundledEffects_KnownEntry_ProjectsSensibly`. **This one DOES need `[SkippableFact]`** — it loads `src/Mithril.Shared/Reference/BundledData/effects.json` directly, which isn't co-located on some CI shapes. Skip via `Skip.IfNot(File.Exists(bundledPath))`. Look up `"effect_10003"` (Sticky!) and 2-3 other well-known entries, assert text shape: name non-empty, keyword chips populated, no `(unknown)` sentinels, expected section buckets present.

7. **Drift detector** — extend the existing `BundledDataValidationTests` walk to cover effects (it should already; verify). Plus a coverage test that every distinct `Effect.Keywords` tag appearing in `>50` effects has at least one `KeywordDisplayOverrides` entry if you ship overrides (parallels the `KeywordDisplayOverrides` drift test from #267).

## Out of scope

- **Items ↔ Effects join.** No clean data shape; deferred to a hypothetical follow-up issue if the heuristic-based projection becomes worth a tab-PR.
- **`AbilityKeywordRules` rendering on effects.** Per the #288 handoff: those rules gate on `MustHaveAbilityKeywords`, not `ReqEffectKeywords`. They render on Ability detail (not yet wired) or in a hypothetical "Combat math" augment tab. Not relevant here.
- **`Recipe.ResultEffects` → effect cross-link.** Procedural method-call strings, not foreign keys. Renderer reuse explicitly out of scope (see *Wrong assumption 3*).
- **Per-effect deep-link by Name.** Many effects share `Name` (e.g. multiple `"Riposte!"`). The route stays InternalName-only (and InternalName = envelope key per the lift).
- **Skill tab.** Roadmap Bucket B but separate issue; resolver already has `EntityKind.Skill` wired for rendering only.

## Open questions worth flagging in the PR description

1. **"Required by abilities" overflow.** Capped at 12 per `UsedInChipCap` with `+N more` pill. Does the pill need a synthetic deep-link target (`EntityKind.AbilityByEffectKeyword`?) or can it just plain-text in v1? The Items tab's overflow chips use `EntityKind.RecipeIngredientItem` to deep-link to a filtered Recipes tab. Effects-to-Abilities is the inverse direction; consider whether the Abilities tab's query box can express `EffectKeywordReqs CONTAINS "<tag>"` cleanly. If the Abilities row schema exposes that column (it should — check `AbilityListRow`), then yes, mirror the pattern with a new `EntityKind.AbilityByEffectKeyword`. If not, plain text is fine for v1 and you file the follow-up.

2. **Duration normalization.** `Effect.Duration` is "int seconds | Permanent". The cookbook *Default-value noise filtering* rule would null out the universal default — but here there isn't one; both representations are meaningful. Decide whether `-1` / `-2` sentinels (visible in `effect_10003` Sticky!: `Duration: -2`) get special rendering ("Until cleansed" / "Until logout"?) or plain "-2 seconds" leak through. The drift-risk is moderate; document whatever you pick in the detail VM's projector comment.

3. ~~**Keyword chip cardinality.**~~ **Resolved during review.** Effects have small keyword lists (~1-3 tags typical), but `"Buff"` alone appears on thousands of effects — clicking the chip yields a large filtered list. **v1 commits to: ship it, no per-keyword cap.** The cardinality is on the *filter result* side, not the chip-fan-out side that #259 solved — filtering N rows is the user's problem to refine further with `AND` clauses, exactly how the existing Items / Recipes tabs handle their high-cardinality keyword filters. Revisit only if user feedback explicitly says the wide-keyword filter results are unusable; not pre-emptive scope.

## Verification ladder

Standard cookbook ladder; specific checks for #244:

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test tests/Silmarillion.Tests` — new test files + extended navigator tests pass.
3. `dotnet test Mithril.slnx` — full suite. Pay attention to `BundledDataValidationTests` (now walks the Effects POCO too) and any `ReferenceDataEntityNameResolverTests` regressions from the Effect-case wiring.
4. **Real-data sanity walk before manual smoke**: 2-3 known effects (Sticky! / Luck of the Legendary Lemon / one with a populated `StackingType`) project legibly.
5. `dotnet run --project src/Mithril.Shell` — manual:
   - Effects tab appears in the strip (gold IsSelected underline confirms `MithrilTabItemStyle` is being applied — confirms `ItemsSource` wiring per #272/#293).
   - Type `Keywords CONTAINS "Buff"` in the query box → list filters; completion popup opens on the column name (`Schema` binding sanity).
   - Pick an effect → detail pane shows description (italic markup rendered), keyword chips, stacking peers, required-by-abilities chips.
   - Click a keyword chip → tab stays on Effects, query box auto-populates `Keywords CONTAINS "<tag>"`.
   - Click an ability chip → switches to Abilities tab and selects the ability.
   - Open a quest with `HasEffectKeywordRequirement` (search the catalog — there are several) → the "Has effect:" chip is now navigable (validates the `QuestDetailProjector.cs:264` migration).
   - Open an ability with `EffectKeywordReqs` populated → the chips render and are clickable (validates the `AbilityDetailViewModel.cs:93` migration).
   - Deep-link: `start mithril://silmarillion/effect/effect_10003` → Effects tab opens, Sticky! selected.
   - Background refresh: leave the Effects tab open, trigger Settings → Refresh All; confirm the list rebuilds without dropping selection.

## Workflow

1. Branch from `origin/main`: `feat/244-effects-tab`. Branch policy forbids direct commits to main.
2. Commit slicing: (a) service-layer plumbing + tests, (b) `EntityKind.EffectKeyword` + factory + audit-pass migrations, (c) tab VM + view + kind targets + DI, (d) detail-pane sections + cross-link rendering, (e) deep-link test + manual-smoke fixes. Land as a single PR.
3. PR title: `feat(silmarillion): Effects tab — #244`.
4. PR body should:
   - Cite #244 + #288/#296 (the rule-data dependency) + #293 (`ITabViewModel` refactor)
   - Note the four wrong assumptions and how each was resolved (especially the `EntityRef.Effect` misuse → `EffectKeyword` split — that's the architecturally interesting one)
   - Note the `QuestDetailProjector.cs:264` migration (a small *correctness fix* riding alongside the feature)
   - Note the `Effect.InternalName` lift in `ReferenceDeserializer.ParseEffects` as a contained architectural cleanup
   - Call out the two remaining *Open questions* under their own section so review can land each independently
5. Expect ~500-800 LoC, in line with Abilities tab #293's size.

## Related

- **#203** — Reference-DB epic umbrella.
- **#244** — this issue.
- **#288 / #296** — ability conditional-rule plumbing; the consumer for `AbilityDynamicDots` / `AbilityDynamicSpecialValues` lands on this tab's detail pane.
- **#293** — Abilities tab (predecessor, established the `ITabViewModel` enumerable-composition pattern).
- **#282** — `IEntityNameResolver`; the place to add the `EntityKind.Effect` case.
- **#229** — `SilmarillionDeepLinkHandler`; the new `effect` URL segment goes through it for free.
- **#259 / #270 / #273** — synthetic-kind precedents (`RecipeIngredientKeyword`, `ItemKeyword`, `RecipeIngredientItem`) for the `EffectKeyword` design.
- **#285** — drift detection umbrella; the Effect POCO field-coverage walk is a Channel-B fit.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-14.*
