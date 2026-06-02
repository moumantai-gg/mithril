# Mithril.Reference design notebook

A running log of two kinds of decisions made while authoring POCO models for
the BundledData files:

- **Shape quirks** — JSON oddities the deserializer copes with, including any
  unverified semantic gambles that might bite a future consumer.
- **Design notes** — non-obvious modelling choices (e.g. when to share a base
  type across files, when to keep hierarchies separate). The "why" matters
  more than the "what" — the code records the choice; this file records the
  reasoning so future-Arthur can re-evaluate when context shifts.

This file is the place future-Arthur should look first if a downstream
consumer (quest-eligibility evaluator, recipe planner, cross-file query layer)
starts producing wrong answers — the bugs are likely to be in the
*interpretation* of these models rather than in the parse.

---

## Shape quirks

### Quests: nested-array `Requirements` (interpretation: flatten as AND)

**Where:** `quests.json`, applies to a small number of quests including the
vampire-themed quest near line 40605 of the bundled `quests.json`. The
shape is `Requirements` whose array elements are themselves arrays:

```json
"Requirements": [
    { "Quest": "Grasuul", "T": "QuestCompleted" },
    [
        { "T": "IsVampire" },
        { "DaysAllowed": ["Monday", "Thursday"], "T": "DayOfWeek" }
    ]
]
```

That is: an outer array of two elements where element [0] is a plain
requirement and element [1] is itself an array of requirement objects.
Surfaced during the Phase 1 validation run as a `JsonReaderException` —
`DiscriminatedUnionConverter` expected `StartObject`, found `StartArray`.

**How the deserializer copes:** `SingleOrArrayConverter<T>` recursively
flattens nested arrays into the parent list. The example above parses to a
flat 3-element `IReadOnlyList<QuestRequirement>` containing
`QuestCompletedRequirement`, `IsVampireRequirement`, and
`DayOfWeekRequirement`. See [SingleOrArrayConverter.cs](../src/Mithril.Reference/Serialization/Converters/SingleOrArrayConverter.cs).

**Semantic gamble:** the bundled JSON allows three plausible interpretations
of nesting:

1. **Flat AND** — `[A, [B, C]]` means "A AND B AND C". Nested-ness is a
   hand-editing artifact; the data designer visually grouped some
   requirements but didn't intend distinct semantics.
2. **Implicit nested AND** — `[A, [B, C]]` means "A AND (B AND C)".
   Semantically equivalent to (1).
3. **Implicit nested OR** — `[A, [B, C]]` means "A AND (B OR C)". Nested
   arrays carry hidden OR semantics.

We chose interpretation (1) because:

- Project Gorgon already has an explicit `OrRequirement` (JSON `"T": "Or"`
  with a `List` field). If a quest designer wanted OR-grouping, they would
  use `Or` rather than implicit nesting.
- The affected quests' surrounding context (vampire + day-of-week, etc.)
  reads as a simple AND ("must be a vampire, must be Monday or Thursday")
  rather than as an OR.
- Flattening preserves all entries — no data loss in either direction.

**Risk:** if interpretation (3) is actually correct, flattening silently
drops a constraint. A quest meant to require "A AND (B OR C)" would parse
as "A AND B AND C", over-restricting eligibility in a future quest-eligibility
evaluator. The validation harness cannot detect this — the data parses
successfully under any interpretation. Only behavioural comparison against
the live game would catch the mistake.

**Verification owed:** before any consumer evaluates quest eligibility from
the parsed `Quest` model, identify every quest in `quests.json` with
nested-array `Requirements`, look up their gating logic on the Project
Gorgon wiki or in-game, and confirm the AND interpretation. ~15 minutes of
spot-checking, deferred until a real consumer needs the certainty.

### NPCs: `LevelRange` is dash-separated strings, not int pairs

**Where:** `npcs.json`, `Services` entries with `Type: "InstallAugments"`
carry a `LevelRange` field. The shape is a list-of-strings, not list-of-ints
as the field name suggests:

```json
{ "Favor": "Friends", "LevelRange": ["0-60"], "Type": "InstallAugments" }
```

Surfaced during the Phase 2 NPC validation run as `JsonReaderException:
Could not convert string to integer: 0-60` after I initially modelled
`LevelRange` as `IReadOnlyList<int>?`.

**How the deserializer copes:** `InstallAugmentsService.LevelRange` is
`IReadOnlyList<string>?`. Consumers split each entry on `-` and parse the
two halves at use time. No converter needed; the raw shape is preserved.

**Why flag this:** the pattern of "dash-separated range as a single string"
is likely to recur across BundledData (skill brackets, level requirements,
power tier ranges). Future POCO authors who see a *Range* or *Bracket* field
name should expect string-range shapes by default and only model as int when
the data verifies.

---

## Design notes

### Three deliberate deviations on `Item` from "JSON shape exactly"

**Where:** [Models/Items/Item.cs](../src/Mithril.Reference/Models/Items/Item.cs).
`items.json` is the highest-traffic file in the Mithril codebase — every
gameplay module reads it. Three properties deviate from the literal JSON
shape because every consumer wanted the lifted form:

1. **`Id` (long)** — *lifted from the JSON envelope key.* `items.json`
   ships entries keyed by `"item_5010"` with no `Id` field on the entry
   itself. `IReferenceDataService.Items` is keyed by the numeric id, and
   GiftMatch / PriceObservation persist the id, so the deserializer parses
   it out of the key at load time and writes it onto the POCO.
2. **`Value: decimal` (was `double` in the raw shape)** — *flipped for
   monetary correctness.* Vendor sale value. JSON ships int for 10624
   entries and float for 106; `decimal` is .NET's monetary type and matches
   what Smaug's `PriceObservation.BaseValue` already used. The handful of
   `(double)item.Value` cast sites compile unchanged.
3. **`Keywords: IReadOnlyList<ItemKeyword>?` (was `IReadOnlyList<string>?`)** —
   *parsed at deserialize.* JSON ships each entry as a flat string shaped
   like `"VegetarianDish=84"` (tag=quality) or just `"Loot"`.
   `ItemKeywordListConverter` does the split once at parse time. The
   matching-machinery virtuals (`EquipmentSlot:`, `SkillPrereq:`,
   `MinValue:`, `MinRarity:*`) are *not* on the Item — they live in
   `Mithril.Shared.Reference.ItemKeywordSynthesis` since they're not a
   property of the JSON shape, they're matching logic layered on top.

**Why these and not others:** the rule is "match JSON exactly unless every
consumer pays a tax to undo the shape." For these three, every consumer
*was* paying the tax (every call site cast `(double)item.Value` to decimal
for monetary math; every call site parsed `"Tag=N"` into a tuple; every
lookup constructed the id by parsing the envelope key). Lifting them once
at the parse seam — verified by the JSON converter — eliminates ~50 lines
of boilerplate per consumer.

**Risk:** a future contributor reading `Item.cs` might assume the
remaining properties match JSON literally and copy that assumption into a
new POCO. The class-level summary comment names the three exceptions
explicitly, and `Item.Keywords` / `Item.Value` carry deviation notes on
their individual property doc-comments.

### Two deliberate deviations on `Recipe` from "JSON shape exactly"

**Where:** [Models/Recipes/Recipe.cs](../src/Mithril.Reference/Models/Recipes/Recipe.cs).
Companion to `Item` — every gameplay module that reads recipes also pays
the same lifting tax, so we collapsed the slim `RecipeEntry` projection
into the canonical POCO (#216). Two deviations from the literal JSON
shape:

1. **`Key` (string)** — *lifted from the JSON envelope key.* `recipes.json`
   ships entries keyed by `"recipe_1234"` with no `Key` field on the entry
   itself. `IReferenceDataService.Recipes` is keyed by it, and the
   leveling simulator threads it through `RecipeKey` on simulation steps.
   The deserializer assigns it at load time (same pattern as `Item.Id`).
2. **`Ingredients: IReadOnlyList<RecipeIngredient>` (was nullable)** —
   *tightened to non-nullable.* Sampling of the bundled `recipes.json`:
   4427/4427 entries carry an `Ingredients` field (11 with empty list,
   4416 non-empty, 0 missing). Slim already exposed it as non-nullable;
   the full POCO now matches.

The polymorphic `RecipeIngredient` hierarchy (abstract base +
`RecipeItemIngredient` / `RecipeKeywordIngredient` subclasses)
**carries through unchanged from slim to the full POCO** — this is *not*
a deviation; it's the type-safe encoding of the JSON's mutually-exclusive
`ItemCode` / `ItemKeys` field-presence discriminator. The JSON has no
explicit `T`-style tag so a bespoke
[RecipeIngredientConverter](../src/Mithril.Reference/Serialization/Converters/RecipeIngredientConverter.cs)
picks the concrete subclass at deserialize time. Consumers pattern-match
on subclass; illegal states (both fields set, neither set) are not
representable. An interim refactor folded the hierarchy into a flat
class with nullable `ItemCode`/`ItemKeys`; the resulting field-presence
branching at every call site was strictly worse type-safety than the
slim shape, so we restored the hierarchy in a corrective follow-up.

The `ResultItems` / `ProtoResultItems` migration from the slim
`RecipeItemRef(long ItemCode, int StackSize, float? ChanceToConsume)`
record to the full `RecipeResultItem` class is a mechanical type rename:
`ItemCode` / `StackSize` are present on both shapes, and the slim path
always passed `null` for `ChanceToConsume` on result items (it only
applied to ingredients, where the full POCO's
`RecipeIngredient.ChanceToConsume` covers it). `RecipeResultItem` carries
an extra `PercentChance` field that the full JSON shape provides for
probabilistic result slots — the slim path silently dropped it.

**Why these and not others:** same "every consumer was paying the tax"
rule as `Item`. Every call site looked up the key via `recipe.Key` and
asserted `Ingredients` was non-null before iterating. Lifting both at
the parse seam eliminated boilerplate per consumer across Celebrimbor /
Bilbo / Elrond / Mithril.Shared.Wpf without sacrificing type safety.

**Risk:** the `RewardSkillXpDropOffPct` field was `float?` on slim and
`double?` on the full POCO. Float→double widening introduces precision
artifacts (`(double)0.1f * 10 != 1.0`) that the slim arithmetic absorbed.
The Elrond drop-off computation switched from float to double accordingly,
and the test fakes' `AddRecipe` signatures retyped to match. Real bundled
data only ships `0.1` as a literal double, so production behaviour matches
the pre-unification path; the only behaviour change is at the test seam.

### `StoreService.ParsedCapIncreases`: computed companion, not a converter-time deviation

**Where:** [Models/Npcs/NpcService.cs](../src/Mithril.Reference/Models/Npcs/NpcService.cs)
(`StoreService`), parsed by
[StoreCapIncreaseParser](../src/Mithril.Reference/Models/Npcs/StoreCapIncreaseParser.cs).

`StoreService.CapIncreases` ships as raw colon strings
(`"Despised:5000:Armor,Weapon,CorpseTrophy"` — `Tier:GoldCap:keyword,…`). Two
consumers historically re-parsed it independently: Smaug's
`ReferenceDataService.ParseCapIncreases` (→ a slim `NpcStoreCapIncrease`
record) and Silmarillion's `NpcsTabViewModel.FormatCapIncrease` split logic.
#350 unified both on the canonical `StoreCapIncrease` record + parser in this
project.

**Why a computed companion, not the `Item.Keywords` converter pattern:** the
`Item`/`Recipe` deviations *replace* the raw shape at the JSON-converter seam
(`Keywords: IReadOnlyList<ItemKeyword>?` was `IReadOnlyList<string>?`). Here we
instead keep `CapIncreases` JSON-faithful and add a get-only
`ParsedCapIncreases => StoreCapIncreaseParser.Parse(CapIncreases)`. Reasons:

- No bespoke `JsonConverter` needed — the parse is a pure string split, not a
  deserialization concern; a converter would be ceremony.
- The model stays **attribute-free** per the `Models/` README. The computed
  property carries *no* `[JsonIgnore]`: reference data is deserialize-only and
  no test serializes models back, so a get-only property never round-trips and
  the attribute would be both unnecessary and a README violation.
- Both raw and parsed forms stay available — the raw list is still the literal
  JSON contract for the drift-validation harness; the parsed list is the
  queryable seam for nested store-cap filtering (#351 `WITH ANY|ALL`).

**The rule this implies:** when a field's parse is a pure transform of its own
raw value (no cross-field or envelope-key input), prefer a computed companion
over a converter-time replacement — it keeps the JSON contract honest, needs no
serialization plumbing, and is the cheapest place to expose a queryable shape.
Reach for the converter pattern (`Item.Keywords`) only when *every* consumer
pays the un-shape tax or the lifted value depends on out-of-field context
(`Item.Id` from the envelope key).

### `RecipeRequirement` is a separate hierarchy from `QuestRequirement`

**Where:** [Models/Quests/QuestRequirement.cs](../src/Mithril.Reference/Models/Quests/QuestRequirement.cs)
and [Models/Recipes/RecipeRequirement.cs](../src/Mithril.Reference/Models/Recipes/RecipeRequirement.cs).
The two abstract bases share no inheritance; their concrete subclasses use
suffixes (`HasEffectKeywordRecipeRequirement`, `MoonPhaseRecipeRequirement`)
to disambiguate where T-value names overlap.

**The overlap:** eight T-values appear in both files —
`HasEffectKeyword`, `MoonPhase`, `EquipmentSlotEmpty`, `PetCount`,
`Appearance`, `EntityPhysicalState`, `FullMoon`, `TimeOfDay`. A naïve reading
suggests these *are* the same requirement and should share concrete types
across `Quests/` and `Recipes/`.

**Why we kept them separate:** field sets diverge between the two domains
in subtle ways:

- Quest `HasEffectKeyword` has just `Keyword`. Recipe `HasEffectKeyword` adds
  an optional `MinCount` field.
- Quest `TimeOfDay` has a `Hours` string field plus `MinHour`/`MaxHour`.
  Recipe `TimeOfDay` has only `MinHour`/`MaxHour`.
- Quest `PetCount` requires `MinCount` and `MaxCount`. Recipe `PetCount`
  requires only `MaxCount` (and on a different domain — quest pet vs druid
  summoned pet).

If we shared a single `HasEffectKeywordRequirement` class, it would carry
the union of both field sets, leaving consumers unable to tell whether a
given field was permitted in their context. That's worse than two classes
that each precisely describe their own domain.

**Trade-offs accepted:**

- ❌ **Boilerplate.** ~8 duplicated subclasses across the two files.
- ❌ **Cross-domain consumers cannot iterate generically.** A future query
  like "every requirement gated on a moon phase" needs to walk both
  hierarchies. Today there are no such consumers.
- ✅ **Each domain stays honest.** A `RecipeRequirement` carries exactly the
  fields recipes are observed to use; same for quests. Adding a field to one
  doesn't muddle the other.
- ✅ **Schema drift in one domain doesn't propagate.** If Elder Game adds a
  new field to recipe-side `HasEffectKeyword`, only the recipe model needs
  updating; the quest model stays pinned.

**Reconsider when:** a real cross-domain consumer materialises (e.g. a
"runtime behaviour evaluator" that needs to check whether the player is
currently a vampire, regardless of whether the gating context is a quest or
a recipe). At that point, the right move is probably to extract a shared
abstract base — e.g. `IRuntimeStateRequirement` — that both
`QuestRequirement` and `RecipeRequirement` can implement on their relevant
subclasses, rather than collapsing the two hierarchies into one.

**Reconsider also when:** the duplicated subclasses start drifting
accidentally (e.g. someone adds a field to one but forgets the other). If
that becomes a real source of bugs, the cost calculation flips.

### `SourceEntry` is a single shared hierarchy across three files

**Where:** [Models/Sources/SourceEntry.cs](../src/Mithril.Reference/Models/Sources/SourceEntry.cs).
One abstract `SourceEntry` base + 19 concrete subclasses, used by
`sources_items.json`, `sources_recipes.json`, and `sources_abilities.json`
via the same `SourceDiscriminators.BuildEntryConverter()` and the same
`ReferenceDeserializer.ParseSources()` entry point.

**Why this departs from the Quest/Recipe rule:** the
`QuestRequirement`/`RecipeRequirement` split was driven by *field-set
divergence* between domains — recipe `HasEffectKeyword` has a `MinCount`
field that quest `HasEffectKeyword` lacks, etc. The sources_* files have no
such divergence: a `Quest` entry's shape is `{ type, questId }` in every
file that contains one; an `Item` entry's shape is `{ type, itemTypeId }`
identically across files. Different *which* types appear, but the same
*what* shape per type.

**The two competing rules, reconciled:**

1. *Separate hierarchies when field sets diverge.* (Quest vs Recipe.)
2. *Share hierarchies when field sets are identical.* (sources_* family.)

The deciding test is: would a consumer asking "every place that grants
recipe X" want a single typed result type, or three? For sources_*, the
answer is one — a cross-file query is the natural shape. For quest vs
recipe requirements, the answer is two — they're evaluated in different
contexts and the field sets carry context-specific meaning.

**Naming convention deviation:** the sources_*.json files use lowercase
`type` as the discriminator and camelCase property names (`npc`,
`itemTypeId`, `friendlyName`). POCO property names match literally,
producing non-idiomatic C# names (`SourceEntry.type`, `ItemSource.itemTypeId`).
Consistent with the project-wide "literal-match property names" policy
that avoids per-type contract resolvers, but worth noting that consumers
will see lowercase identifiers when accessing these fields.

### Top-level shape is not always `Dictionary<string, T>`

**Where:** [ReferenceDeserializer.cs](../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs).
Most BundledData files use the convention <c>{ "id_N": { …entry… } }</c>,
deserialized as <c>Dictionary&lt;string, T&gt;</c>. Three files break the
mould and need bespoke top-level shapes:

- **`directedgoals.json`** — top is a JSON *array*, not an object.
  `ParseDirectedGoals` returns `IReadOnlyList<DirectedGoal>`.
- **`lorebookinfo.json`** — top is a single object with one root key
  (`"Categories"`) wrapping a nested dictionary. `ParseLorebookInfo` returns
  a `LorebookInfo` POCO (not a dictionary).
- **`landmarks.json`** — top is a dictionary, but values are *lists* of
  landmark records, not single records. `ParseLandmarks` returns
  `IReadOnlyDictionary<string, IReadOnlyList<Landmark>>`.

**Why flag this:** the validation harness's `IParserSpec.CountEntries`
takes `object` precisely so each spec can interpret "entry count" however
fits its file's shape. The broader implication: future POCO authors must
spot-check the top-level shape (`isinstance(data, dict)` vs `list`, single
key vs many) before assuming the dictionary envelope.

### `tsysprofiles.json` ships no POCO at all

**Where:** [ReferenceDeserializer.ParseTsysProfiles](../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs).

The file's shape is `Dictionary<string, List<string>>` — each profile name
maps to a flat list of power-key strings. Wrapping that in a one-field
POCO would add ceremony with zero clarity benefit. Returned directly as
`IReadOnlyDictionary<string, IReadOnlyList<string>>`.

**The rule this implies:** when a file's shape is genuinely a
trivially-typed collection (string→string, string→list-of-string,
string→int), don't invent a POCO. The Models/ folder is for *records with
named fields* — a list-of-string already has the structure baked in.

### Always inspect *element types* of list-typed fields, don't infer

**Where:** [Models/Abilities/Ability.cs](../src/Mithril.Reference/Models/Abilities/Ability.cs).
The Phase 4 inspection script reported `ConditionalKeywords: 922 types={'list': 922}`
and `AmmoKeywords: 613 types={'list': 613}`. I initially modelled both as
`IReadOnlyList<string>?` because the field-name suffix (<i>Keywords</i>)
suggested string keywords. Validation failed twice — the elements are
records:

```json
"ConditionalKeywords": [
    { "Default": true, "EffectKeywordMustNotExist": "BarrageAoE", "Keyword": "Melee" },
    { "EffectKeywordMustExist": "BarrageAoE", "Keyword": "Burst" }
]
```

```json
"AmmoKeywords": [
    { "Count": 1, "ItemKeyword": "SporeBomb1" }
]
```

**The rule this implies:** when the field-shape inspector reports
`{'list': N}` for a field, the report says nothing about *element type*.
A separate pass to count `kind(element)` per list-typed field is needed
before assuming `IReadOnlyList<string>?`. Field names that *sound* like
strings (`Keywords`, `*Reqs`, `*Tags`) are particularly misleading — Project
Gorgon happily uses them for both flat string lists and lists of richer
records.

**Cheap defensive workflow for future files:** the Python inspection
should always include the per-list-field element-kind counter from the
abilities-debug pass, not just the parent field's `kind`. Costs ~10 lines,
catches this class of bug before it hits the validation harness.

---

## Operational notes

### Pre-merge drift detection via `tools/RefreshAndValidate`

The `BundledDataValidationTests` harness only gates the JSON checked into
this repo. The Phase 6 `IDiagnosticsSink` instrumentation only fires *at
runtime on a user's machine* — by then the broken model is already
shipped. To close the gap, a daily GitHub Actions cron
([cdn-drift-check.yml](../.github/workflows/cdn-drift-check.yml)) runs
[tools/RefreshAndValidate](../tools/RefreshAndValidate/Program.cs) against
the live CDN. The tool fetches every BundledData file from
`https://cdn.projectgorgon.com/{version}/data/{file}.json` (version
auto-detected, with `v469` as the fallback), runs every `IParserSpec`
discovered in `Mithril.Reference` over them, and exits non-zero on any
parse failure, sub-floor entry count, or unknown discriminator.

A red `CDN drift check` workflow means **Elder Game shipped a new
discriminator value the model layer hasn't been updated to recognise**.
Resolution path:

1. Click into the failing workflow run; the failing file and the new
   `T`-value (or other discriminator) appear in the log.
2. In `Mithril.Reference`, add a new concrete subclass for the new
   discriminator value and an entry in the corresponding
   `JsonSubTypes`/discriminator map.
3. Run `dotnet test tests/Mithril.Reference.Tests` locally to confirm
   the bundled gate still passes.
4. Commit, PR, merge — the next cron run will go green.

The tool can also be run locally (`dotnet run --project
tools/RefreshAndValidate`) any time you want to refresh-and-check before
bumping `src/Mithril.Shared/Reference/BundledData/`. It does not modify
the bundled JSON; auto-bundled-data-refresh is a deferred follow-up.

---
