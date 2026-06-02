using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Effects;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;

namespace Mithril.Shared.Reference;

/// <summary>
/// Manages dev-published JSON reference data from cdn.projectgorgon.com.
/// Loaded eagerly at construction from disk cache (or bundled fallback) so
/// consumers see a populated dictionary synchronously. CDN refresh runs in
/// the background and raises <see cref="FileUpdated"/> when new data lands.
/// </summary>
public interface IReferenceDataService
{
    IReadOnlyList<string> Keys { get; }

    IReadOnlyDictionary<long, Item> Items { get; }

    /// <summary>InternalName → <see cref="Item"/> lookup. Useful when the log gives an InternalName but no item id.</summary>
    IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }

    /// <summary>
    /// Catalog-side keyword → items index. Powers keyword-matched recipe ingredients
    /// (e.g. the auxiliary-crystal slot on every <c>*E</c> enchanted recipe). Rebuilt
    /// whenever <c>items.json</c> reloads.
    /// </summary>
    ItemKeywordIndex KeywordIndex { get; }

    /// <summary>recipe key (e.g. "recipe_1234") → <see cref="Recipe"/>.</summary>
    IReadOnlyDictionary<string, Recipe> Recipes { get; }

    /// <summary>InternalName → <see cref="Recipe"/> lookup. Matches RecipeCompletions keys from character exports.</summary>
    IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; }

    /// <summary>
    /// Recipes indexed by the InternalName of any item they produce (via
    /// <see cref="Recipe.ResultItems"/>, falling back to <see cref="Recipe.ProtoResultItems"/>
    /// when ResultItems is empty). Built whenever items.json or recipes.json reloads.
    /// Powers the reference-browser's item-detail "Produced by" cross-link section.
    /// Defaults to empty so test fakes don't need to opt into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByProducedItem => EmptyRecipeIndex;

    /// <summary>
    /// Recipes indexed by the InternalName of any item they consume as an ingredient via a
    /// direct <see cref="RecipeItemIngredient"/>. <see cref="RecipeKeywordIngredient"/> slots
    /// are kind-based (e.g. "any Crystal") and don't map to a single InternalName — they're
    /// surfaced through <see cref="KeywordsUsedInRecipeSlots"/> instead. Built whenever
    /// items.json or recipes.json reloads. Defaults to empty so test fakes don't need to opt
    /// into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => EmptyRecipeIndex;

    /// <summary>
    /// Provenance-retaining variant of <see cref="RecipesByIngredientItem"/> (#318 slice 4,
    /// surface 1 — Items "Used in"). Same membership — recipes that consume the keyed item
    /// via a direct <see cref="RecipeItemIngredient"/> — but each member is a
    /// <see cref="RecipeIngredientItemMatch"/> retaining <em>why</em> it qualified, so the
    /// provenance popup renders membership <em>and</em> provenance from the index directly
    /// with no second (query-string) derivation that could silently diverge. The
    /// relationship is single-reason (<see cref="RecipeIngredientItemMatchReason.DirectIngredient"/>),
    /// so the popup collapses to a flat list per the #318 Discipline rule. A recipe is
    /// carried once even if it lists the item in several slots — a distinct-member count
    /// equals the displayed "View all N". Built whenever items.json or recipes.json reloads.
    /// Defaults to empty so test fakes don't need to opt into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> RecipesByIngredientItemWithReason
        => EmptyRecipeIngredientItemMatchIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientItemMatch>> EmptyRecipeIngredientItemMatchIndex
        = new Dictionary<string, IReadOnlyList<RecipeIngredientItemMatch>>(StringComparer.Ordinal);

    /// <summary>
    /// Provenance-retaining index for the recipe-detail <em>keyword</em> surface (#318
    /// slice 4, surface 3 — retiring the synthetic <c>ItemKeyword</c> #270 deep link).
    /// Keyed by a recipe keyword slot's <see cref="RecipeKeywordIngredient.ItemKeys"/>
    /// list <c>'+'</c>-joined (the exact encoding the retired <c>EntityRef.ItemKeyword</c>
    /// factory used, so the key form is stable across the migration). The value is the
    /// items that satisfy that slot — i.e. exactly what
    /// <see cref="ItemKeywordIndex.ItemsMatching"/> returns for the slot's keys (the
    /// single set materialization) — each wrapped in a <see cref="RecipeKeywordItemMatch"/>
    /// retaining <em>why</em> it qualified. The provenance popup renders membership
    /// <em>and</em> provenance from this index directly: there is no second
    /// (query-string) derivation that could silently diverge from the materialized set
    /// (the #318 invariant; this is precisely the dual-derivation fault the
    /// <c>ItemKeyword</c> + <c>ItemKeywordQueryMapper</c> pair had — the mapper failed
    /// whole-slot on <c>MinTSysPrereq:</c>/<c>SkillPrereq:</c> keys the synthesized
    /// keyword index nonetheless matched).
    /// <para>
    /// The relationship is single-reason
    /// (<see cref="RecipeKeywordItemMatchReason.KeywordMatch"/>) — an item qualifies iff
    /// it carries every (synthesized) tag in the slot — so the popup collapses to a flat
    /// list per the #318 Discipline rule. Each item is carried once per slot key
    /// (<see cref="ItemKeywordIndex.ItemsMatching"/> already dedups), so a distinct-member
    /// count equals the displayed "View all N". Built whenever items.json or recipes.json
    /// reloads (same triggers as <see cref="KeywordsUsedInRecipeSlots"/>, from the same
    /// slot accumulation, so the two cannot drift). Defaults to empty so test fakes don't
    /// need to opt into cross-linking.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<RecipeKeywordItemMatch>> ItemsByRecipeKeywordSlotWithReason
        => EmptyRecipeKeywordItemMatchIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RecipeKeywordItemMatch>> EmptyRecipeKeywordItemMatchIndex
        = new Dictionary<string, IReadOnlyList<RecipeKeywordItemMatch>>(StringComparer.Ordinal);

    /// <summary>
    /// The flat set of every distinct keyword tag that appears in any
    /// <see cref="RecipeKeywordIngredient.ItemKeys"/> across all recipes. Powers the item-detail
    /// "Used as" section: an item's chip set is <c>item.Keywords ∩ KeywordsUsedInRecipeSlots</c>,
    /// so chips only appear for keywords that actually lead to at least one recipe. Built
    /// whenever recipes.json reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => Array.Empty<string>();

    /// <summary>
    /// Provenance-retaining reverse index for the item-detail "Used as" surface (#318
    /// slice 4, surface 2 — <c>RecipeIngredientKeyword</c> #259): keyword tag → the
    /// recipes that consume <em>any item carrying that tag</em> via a
    /// <see cref="RecipeKeywordIngredient"/> slot. Same membership semantics the retired
    /// <c>RecipeIngredientKeyword</c> deep link expressed as the query
    /// <c>IngredientKeywords CONTAINS "&lt;tag&gt;"</c> — but materialized once, with each
    /// member a <see cref="RecipeIngredientKeywordMatch"/> retaining <em>why</em> it
    /// qualified, so the provenance popup renders membership <em>and</em> provenance from
    /// the index directly with no second (query-string) derivation that could silently
    /// diverge (the #318 invariant). The relationship is single-reason
    /// (<see cref="RecipeIngredientKeywordMatchReason.KeywordIngredientSlot"/>), so the
    /// popup collapses to a flat list per the #318 Discipline rule. A recipe is carried
    /// once per tag even if it lists that tag in several keyword slots — a distinct-member
    /// count equals the displayed "View all N". Derived from the <em>same</em>
    /// keyword-slot accumulation that builds <see cref="KeywordsUsedInRecipeSlots"/>, so
    /// the two surfaces cannot diverge. Built whenever recipes.json reloads. Defaults to
    /// empty so test fakes don't need to opt into cross-linking.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientKeywordMatch>> RecipesByIngredientKeywordWithReason
        => EmptyRecipeIngredientKeywordMatchIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RecipeIngredientKeywordMatch>> EmptyRecipeIngredientKeywordMatchIndex
        = new Dictionary<string, IReadOnlyList<RecipeIngredientKeywordMatch>>(StringComparer.Ordinal);

    /// <summary>
    /// Friendly display names for keyword tags, sourced from singleton-slot Descs in recipes
    /// (looked up via <c>strings_all["recipe_&lt;id&gt;_Ingredients_&lt;idx&gt;_Desc"]</c>, falling
    /// back to <see cref="RecipeKeywordIngredient.Desc"/> from recipes.json). Only singleton slots
    /// — those whose <see cref="RecipeKeywordIngredient.ItemKeys"/> has exactly one entry — are
    /// considered, because composite-tuple Descs describe the AND-matched composite, not any one
    /// keyword. Keywords whose only friendly Desc is identical to the raw tag are omitted (callers
    /// should treat "missing" as "use the raw tag, optionally split by camel-case"). First match
    /// wins (recipe-id iteration order is stable). Built whenever recipes.json or strings_all.json
    /// reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, string> KeywordDisplayNames => EmptyStringMap;

    private static readonly IReadOnlyDictionary<string, string> EmptyStringMap
        = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Recipe>> EmptyRecipeIndex
        = new Dictionary<string, IReadOnlyList<Recipe>>(StringComparer.Ordinal);

    /// <summary>Skill key (e.g. "Meditation", "AncillaryArmorAugmentBrewing") → SkillEntry.
    /// Keys are id-shaped (ASCII identifier-safe) and match recipes' RewardSkill field.
    /// For the human-readable in-game name use <see cref="SkillEntry.DisplayName"/>.</summary>
    IReadOnlyDictionary<string, SkillEntry> Skills { get; }

    /// <summary>XP table InternalName (e.g. "TypicalNoncombatSkill") → XpTableEntry.</summary>
    IReadOnlyDictionary<string, XpTableEntry> XpTables { get; }

    /// <summary>NPC key (e.g. "NPC_Marna") → NpcEntry with gift preferences.</summary>
    IReadOnlyDictionary<string, NpcEntry> Npcs { get; }

    /// <summary>
    /// NPC <c>InternalName</c> (the JSON envelope key, e.g. <c>"NPC_Marna"</c>, <c>"Altar_Druid"</c>) →
    /// the full <see cref="Npc"/> POCO from <c>npcs.json</c>. Unlike <see cref="Npcs"/>, which is a
    /// slim projection consumed by Arwen for gift calculation, this dictionary exposes Services,
    /// Preferences, ItemGifts, Pos, AreaFriendlyName etc. for Silmarillion's master-detail tab.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Npc> NpcsByInternalName => EmptyNpcMap;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → recipes the NPC teaches, derived from
    /// <see cref="RecipeSources"/> entries with <c>Type == "Training"</c>. Built whenever
    /// recipes.json, npcs.json, or sources_recipes.json reloads. Defaults to empty so test
    /// fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesTaughtByNpc => EmptyRecipeIndex;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → items the NPC sells, derived from
    /// <see cref="ItemSources"/> entries with <c>Type == "Vendor"</c>. Built whenever items.json,
    /// npcs.json, or sources_items.json reloads. Defaults to empty so test fakes don't need to
    /// opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsSoldByNpc => EmptyItemIndex;

    private static readonly IReadOnlyDictionary<string, Npc> EmptyNpcMap
        = new Dictionary<string, Npc>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> EmptyItemIndex
        = new Dictionary<string, IReadOnlyList<Item>>(StringComparer.Ordinal);

    /// <summary>
    /// Area key (e.g. <c>"AreaSerbule"</c>) → friendly display names. Pulled from
    /// <c>areas.json</c>; primary use is resolving area codes that appear on
    /// non-NPC item sources (drops, quests).
    /// </summary>
    IReadOnlyDictionary<string, AreaEntry> Areas { get; }

    /// <summary>
    /// Area key (e.g. <c>"AreaSerbule"</c>) → list of <see cref="Landmark"/> POCOs that
    /// live in that area. Mirrors the bundled <c>landmarks.json</c> shape directly
    /// (no projection / no slim envelope — the POCO already carries display + Type + Combo).
    /// Powers Silmarillion's Area-detail "Landmarks in this area" section. Defaults to empty
    /// so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Landmark>> Landmarks => EmptyLandmarkIndex;

    /// <summary>
    /// Reverse index from area key → NPCs whose <see cref="Mithril.Reference.Models.Npcs.Npc.AreaName"/>
    /// matches the key. Powers Silmarillion's Area-detail "NPCs in this area" chip cluster without
    /// scanning every NPC on each Area selection. Built whenever <c>npcs.json</c> or
    /// <c>areas.json</c> reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<NpcEntry>> NpcsByArea => EmptyNpcByAreaIndex;

    /// <summary>
    /// Provenance-retaining variant of <see cref="NpcsByArea"/> (#318 slice 4, surface 4 —
    /// Areas "NPCs in this area"). Same membership — NPCs whose
    /// <see cref="Mithril.Reference.Models.Npcs.Npc.AreaName"/> equals the area key — but
    /// each member is a <see cref="NpcByAreaMatch"/> retaining <em>why</em> it qualified,
    /// so the provenance popup renders membership <em>and</em> provenance from the index
    /// directly with no second (query-string) derivation that could silently diverge (the
    /// #318 invariant — replaces the retired <c>NpcByArea</c> synthetic-kind deep link).
    /// The relationship is single-reason (<see cref="NpcByAreaMatchReason.InArea"/>), so
    /// the popup collapses to a flat list per the #318 Discipline rule. An NPC is carried
    /// once — a distinct-member count equals the displayed "View all N". Derived from the
    /// same accumulation as <see cref="NpcsByArea"/> so the two indices cannot diverge.
    /// Built whenever <c>npcs.json</c> or <c>areas.json</c> reloads.
    /// <para>
    /// The default <b>projects from <see cref="NpcsByArea"/></b> (single trivial
    /// <see cref="NpcByAreaMatchReason.InArea"/> reason) rather than returning empty: any
    /// fake that only opts into <see cref="NpcsByArea"/> still feeds the popup-from-index
    /// without a ripple across every consumer test, and the projection is exactly what
    /// <c>ReferenceDataService.BuildAreaNpcCrossLinkIndex</c> materializes from the same
    /// accumulation — so the default faithfully models production. The production service
    /// overrides this property with the once-materialized index, so this default is never
    /// hit there.
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<NpcByAreaMatch>> NpcsByAreaWithReason
        => NpcsByArea.Count == 0
            ? EmptyNpcByAreaMatchIndex
            : NpcsByArea.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<NpcByAreaMatch>)kv.Value
                    .Select(n => new NpcByAreaMatch(n, NpcByAreaMatchReason.InArea))
                    .ToList(),
                StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Landmark>> EmptyLandmarkIndex
        = new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<NpcEntry>> EmptyNpcByAreaIndex
        = new Dictionary<string, IReadOnlyList<NpcEntry>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<NpcByAreaMatch>> EmptyNpcByAreaMatchIndex
        = new Dictionary<string, IReadOnlyList<NpcByAreaMatch>>(StringComparer.Ordinal);

    /// <summary>
    /// Item InternalName → sources describing how the item can be obtained
    /// (Vendor / Recipe / Quest / Monster / HangOut / NpcGift / Barter / Angling / …).
    /// Pulled from <c>sources_items.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; }

    /// <summary>
    /// Recipe InternalName → sources describing how the recipe is acquired
    /// (Training / Skill / Effect / Quest / NpcGift / …). Pulled from
    /// <c>sources_recipes.json</c>. Mirrors <see cref="ItemSources"/>. Defaults
    /// to an empty dictionary so existing test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> RecipeSources => EmptyRecipeSourceIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RecipeSource>> EmptyRecipeSourceIndex
        = new Dictionary<string, IReadOnlyList<RecipeSource>>(StringComparer.Ordinal);

    /// <summary>
    /// Ability envelope key (e.g. <c>"ability_42"</c>) → the full <see cref="Ability"/> POCO from
    /// <c>abilities.json</c>. Carries the wide ability metadata surface (animations,
    /// prerequisites, ammo, sidebar visibility, the nested <see cref="Ability.PvE"/> stat block,
    /// etc.) consumed by Silmarillion's Abilities tab. Defaults to empty so test fakes don't
    /// need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Ability> Abilities => EmptyAbilityMap;

    /// <summary>
    /// Ability <c>InternalName</c> (e.g. <c>"Sword1"</c>, <c>"Mentalism5"</c>) →
    /// <see cref="Ability"/>. Used by the navigator + chip cross-links and by
    /// <see cref="IEntityNameResolver"/> to project ability InternalNames to display names.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Ability> AbilitiesByInternalName => EmptyAbilityMap;

    /// <summary>
    /// Skill key (e.g. <c>"Sword"</c>, <c>"Mentalism"</c>) → abilities tagged to that skill via
    /// <see cref="Ability.Skill"/>. Powers the Abilities-tab master-list skill facet. Built
    /// whenever <c>abilities.json</c> reloads. Defaults to empty so test fakes don't need to
    /// opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesBySkill => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from prior ability <c>InternalName</c> → abilities that name it in
    /// <see cref="Ability.UpgradeOf"/>. Powers the ability-detail "Upgrades to" reverse view
    /// on the prior ability. Built whenever <c>abilities.json</c> reloads. Defaults to empty
    /// so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesUpgradingFrom => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from <see cref="Ability.AbilityGroup"/> → all abilities in that group.
    /// Powers the ability-detail "Other abilities in group" roster section. Built whenever
    /// <c>abilities.json</c> reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesInGroup => EmptyAbilityIndex;

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → abilities the NPC teaches, derived from
    /// <see cref="AbilitySources"/> entries with <c>Type == "Training"</c>. Built whenever
    /// <c>abilities.json</c>, <c>npcs.json</c>, or <c>sources_abilities.json</c> reloads.
    /// Mirrors <see cref="RecipesTaughtByNpc"/>. Defaults to empty so test fakes don't need
    /// to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Ability>> AbilitiesTaughtByNpc => EmptyAbilityIndex;

    /// <summary>
    /// Ability <c>InternalName</c> → sources describing how the ability is acquired
    /// (Training / Skill / Quest / Effect / …). Pulled from <c>sources_abilities.json</c>.
    /// Mirrors <see cref="ItemSources"/> and <see cref="RecipeSources"/>. Defaults to empty
    /// so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> AbilitySources => EmptyAbilitySourceIndex;

    private static readonly IReadOnlyDictionary<string, Ability> EmptyAbilityMap
        = new Dictionary<string, Ability>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Ability>> EmptyAbilityIndex
        = new Dictionary<string, IReadOnlyList<Ability>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EffectAbilityMatch>> EmptyEffectAbilityMatchIndex
        = new Dictionary<string, IReadOnlyList<EffectAbilityMatch>>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AbilitySource>> EmptyAbilitySourceIndex
        = new Dictionary<string, IReadOnlyList<AbilitySource>>(StringComparer.Ordinal);

    /// <summary>
    /// Placeholder token (e.g. <c>"MAX_ARMOR"</c>) → <see cref="AttributeEntry"/> with the
    /// human-readable label and formatting hints used to render <see cref="Item.EffectDescs"/>.
    /// Pulled from <c>attributes.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, AttributeEntry> Attributes { get; }

    /// <summary>
    /// Power InternalName (e.g. <c>"ShamanicHeadArmor"</c>, <c>"ArcheryBoost"</c>) →
    /// <see cref="PowerEntry"/> describing the per-tier <see cref="EffectDescs"/> used by
    /// <c>AddItemTSysPower</c> augmentation recipes. Pulled from <c>tsysclientinfo.json</c>.
    /// </summary>
    IReadOnlyDictionary<string, PowerEntry> Powers { get; }

    /// <summary>
    /// Random-roll profile name (e.g. <c>"All"</c>, <c>"Sword"</c>, <c>"MainHandAugment"</c>) →
    /// list of power InternalNames eligible to roll on items carrying that <see cref="Item.TSysProfile"/>.
    /// Pulled from <c>tsysprofiles.json</c>; consumed by <c>ExtractTSysPower</c> and
    /// <c>TSysCraftedEquipment</c> "Possible augments" previews.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; }

    /// <summary>
    /// Power <c>InternalName</c> → the <c>tsysprofiles</c> profile names whose pool
    /// contains it (the inverse of <see cref="Profiles"/>). Powers the Silmarillion
    /// Treasure-tab Power-detail "Appears in pools" Confirmed Links (#435). The
    /// <c>tsysprofiles</c> join is authoritative/normalized, so this reverse-view is a
    /// <em>Confirmed</em> edge — G-d Unconfirmed does not apply (precedent on #404). Built
    /// whenever <c>tsysprofiles.json</c> reloads. Defaults to empty so test fakes don't
    /// need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProfilesByPower => EmptyStringListIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyStringListIndex
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Quest envelope key (e.g. <c>"quest_10001"</c>) → the full <see cref="Quest"/> POCO from
    /// <c>quests.json</c>. Carries typed <see cref="Quest.Requirements"/>, <see cref="Quest.Rewards"/>,
    /// <see cref="Quest.Objectives"/>, follow-ups, NPC refs, reuse timers, and the description /
    /// success / preface text blocks needed by Silmarillion's Quests tab and Gandalf's repeatable-
    /// quest timer source.
    /// </summary>
    IReadOnlyDictionary<string, Quest> Quests { get; }

    /// <summary>
    /// Quest <c>InternalName</c> → <see cref="Quest"/> POCO. Matches Quest sources in
    /// <c>sources_items.json</c> and the per-character <c>quests.json</c> active-journal entries.
    /// </summary>
    IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; }

    /// <summary>
    /// Reverse index from NPC <c>InternalName</c> → quests where the NPC is either the giver
    /// (<see cref="Quest.QuestNpc"/>) or the favor anchor (<see cref="Quest.FavorNpc"/>). Built
    /// whenever <c>quests.json</c> or <c>npcs.json</c> reloads. Powers the NPCs-tab "Quests
    /// given" section. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsByGiverNpc => EmptyQuestIndex;

    /// <summary>
    /// Reverse index from item <c>InternalName</c> → quests whose <see cref="Quest.Rewards_Items"/>
    /// awards that item. Built whenever <c>quests.json</c> or <c>items.json</c> reloads. Powers the
    /// Items-tab "Awarded by" section. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Quest>> QuestsRewardingItem => EmptyQuestIndex;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Quest>> EmptyQuestIndex
        = new Dictionary<string, IReadOnlyList<Quest>>(StringComparer.Ordinal);

    /// <summary>
    /// Flat ordered list of every <see cref="DirectedGoal"/> from
    /// <c>directedgoals.json</c> — the "stuff to do" in-game guidance panel. Mixed-type
    /// list of category headers (<see cref="DirectedGoal.IsCategoryGate"/> = true,
    /// e.g. <c>"Anagoge Island"</c>, <c>"Serbule Hills"</c>) and per-area sub-goals
    /// (e.g. <c>"Grow a potato"</c>, <c>"Meet Nightshade"</c>) keyed to a parent gate
    /// via <see cref="DirectedGoal.CategoryGateId"/>. Defaults to empty so test fakes
    /// don't need to opt in.
    /// </summary>
    IReadOnlyList<DirectedGoal> DirectedGoals => Array.Empty<DirectedGoal>();

    /// <summary>
    /// Flat list of conditional "if an ability has these keywords, the listed
    /// attributes apply" rules from <c>abilitykeywords.json</c>. The predicate is
    /// <see cref="AbilityKeyword.MustHaveAbilityKeywords"/>; the consequents are the
    /// <c>AttributesThat*</c> lists. Defaults to empty so test fakes don't need to
    /// opt in. Consumed by the Effects tab (#244) — not folded into per-ability
    /// detail, since the rules are keyed by keyword predicate, not by ability InternalName.
    /// </summary>
    IReadOnlyList<AbilityKeyword> AbilityKeywordRules => Array.Empty<AbilityKeyword>();

    /// <summary>
    /// Flat list of conditional damage-over-time rules from <c>abilitydynamicdots.json</c>
    /// that layer on top of an ability at runtime when <see cref="AbilityDynamicDot.ReqAbilityKeywords"/>,
    /// <see cref="AbilityDynamicDot.ReqActiveSkill"/>, and <see cref="AbilityDynamicDot.ReqEffectKeywords"/>
    /// predicates match. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyList<AbilityDynamicDot> AbilityDynamicDots => Array.Empty<AbilityDynamicDot>();

    /// <summary>
    /// Flat list of conditional tooltip-value rules from
    /// <c>abilitydynamicspecialvalues.json</c> that layer a labelled value onto an
    /// ability's tooltip when <see cref="AbilityDynamicSpecialValue.ReqAbilityKeywords"/>
    /// and <see cref="AbilityDynamicSpecialValue.ReqEffectKeywords"/> predicates match.
    /// Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyList<AbilityDynamicSpecialValue> AbilityDynamicSpecialValues => Array.Empty<AbilityDynamicSpecialValue>();

    /// <summary>
    /// Effect envelope key (e.g. <c>"effect_10003"</c>) → <see cref="Effect"/> POCO from
    /// <c>effects.json</c>. Carries the full effect metadata surface (description, keywords,
    /// duration, stacking type, spew text, particle name, triggering-ability-keyword gate)
    /// consumed by Silmarillion's Effects tab. Defaults to empty so test fakes don't need
    /// to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Effect> Effects => EmptyEffectMap;

    /// <summary>
    /// Effect <c>InternalName</c> → <see cref="Effect"/>. Mirrors <see cref="Effects"/> for
    /// kinds where the envelope key and the InternalName coincide (the deserializer lifts
    /// the envelope key onto <see cref="Effect.InternalName"/>). Same shape as
    /// <see cref="ItemsByInternalName"/> / <see cref="AbilitiesByInternalName"/>.
    /// </summary>
    IReadOnlyDictionary<string, Effect> EffectsByInternalName => EmptyEffectMap;

    /// <summary>
    /// Effect <see cref="Effect.Keywords"/> tag → effects carrying that tag. Powers the
    /// <c>EntityKind.EffectKeyword</c> synthetic deep-link target (Effects tab filtered to
    /// <c>Keywords CONTAINS "&lt;tag&gt;"</c>) and the on-detail "Other effects with this
    /// keyword" cross-link. Built whenever <c>effects.json</c> reloads. Defaults to empty.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByKeyword => EmptyEffectIndex;

    /// <summary>
    /// Effect <see cref="Effect.StackingType"/> → all effects sharing that stacking group.
    /// Powers the on-detail "Stacks with" section. Built whenever <c>effects.json</c>
    /// reloads. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByStackingType => EmptyEffectIndex;

    /// <summary>
    /// Effect-keyword tag → abilities whose
    /// <see cref="Ability.EffectKeywordReqs"/> ∪
    /// <see cref="Ability.EffectKeywordsIndicatingEnabled"/> ∪
    /// <see cref="Ability.TargetEffectKeywordReq"/> contains that tag.
    /// <para>
    /// <b>Excludes</b> abilities with <see cref="Ability.InternalAbility"/> = <c>true</c>
    /// — engine-internal scaffolding (mob skills, mount transitions) with no player-facing
    /// display name that would otherwise pollute the on-detail chip cluster.
    /// </para>
    /// Built whenever <c>abilities.json</c> or <c>effects.json</c> reloads. Powers the
    /// on-detail "Required by abilities" chip cluster and the "View all N" provenance
    /// popup that replaced the retired <c>AbilityByEffectKeyword</c> synthetic deep-link
    /// (#318). Defaults to empty so test fakes don't need to opt in.
    /// <para>
    /// Each value member is an <see cref="EffectAbilityMatch"/> carrying the qualifying
    /// <see cref="Ability"/> <b>and</b> the <see cref="EffectAbilityMatchReason"/> flags
    /// recording which of the three unioned fields matched. The set is materialized
    /// exactly once here, retaining provenance, so a reverse-lookup surface renders
    /// membership <i>and</i> reason without a second derivation (see
    /// the #318 invariant). An ability
    /// qualifying via several fields appears <b>once</b> with several reason flags set,
    /// so a distinct-member count equals the displayed "View all N".
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<EffectAbilityMatch>> AbilitiesByEffectKeyword
        => EmptyEffectAbilityMatchIndex;

    /// <summary>
    /// Ability-keyword tag → effects whose <see cref="Effect.AbilityKeywords"/> list
    /// contains it. Powers the on-detail "Procs from abilities with keyword" section
    /// (which abilities can trigger this effect's behaviour). Reverse of
    /// <see cref="AbilitiesByEffectKeyword"/> — that one says "abilities that gate on
    /// having this effect", this one says "abilities that trigger this effect's
    /// behaviour". Built whenever <c>effects.json</c> reloads. Defaults to empty.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Effect>> EffectsByTriggeringAbilityKeyword => EmptyEffectIndex;

    private static readonly IReadOnlyDictionary<string, Effect> EmptyEffectMap
        = new Dictionary<string, Effect>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Effect>> EmptyEffectIndex
        = new Dictionary<string, IReadOnlyList<Effect>>(StringComparer.Ordinal);

    // ── Lorebooks (#247) ──────────────────────────────────────────────────

    /// <summary>
    /// Lorebook envelope key (e.g. <c>"Book_101"</c>) → <see cref="Lorebook"/> POCO from
    /// <c>lorebooks.json</c>. The envelope key and <see cref="Lorebook.InternalName"/> are
    /// <i>different</i> identifiers (same divergence as Recipe's <c>recipe_NNNN</c> vs
    /// human-form InternalName). Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Lorebook> Lorebooks => EmptyLorebookMap;

    /// <summary>
    /// Lorebook <see cref="Lorebook.InternalName"/> (e.g. <c>"TheWastedWishes"</c>) →
    /// <see cref="Lorebook"/>. The kind target's selection contract follows the cookbook
    /// InternalName convention and matches the existing <see cref="EntityRef.Lorebook(string)"/>
    /// factory. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, Lorebook> LorebooksByInternalName => EmptyLorebookMap;

    /// <summary>
    /// Numeric Book id (101, 102, …) → <see cref="Lorebook"/>. The id is lifted from the
    /// numeric suffix of the envelope key (<c>"Book_101"</c> → <c>101</c>). Powers the
    /// inbound reverse lookup from <see cref="Item.BestowLoreBook"/> (an <c>int?</c>) on
    /// Item detail. Defaults to empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<int, Lorebook> LorebooksById => EmptyLorebookByIdMap;

    /// <summary>
    /// Lorebook <see cref="Lorebook.InternalName"/> → items whose
    /// <see cref="Item.BestowLoreBook"/> matches this book's numeric id. The single
    /// materialization for the #318 popup-from-index "Items that bestow this book" surface
    /// (single-reason — an item qualifies exactly one way). Built whenever
    /// <c>lorebooks.json</c> or <c>items.json</c> reloads. Defaults to empty so test fakes
    /// don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Item>> ItemsBestowingLorebook => EmptyItemIndex;

    /// <summary>
    /// Sidecar metadata from <c>lorebookinfo.json</c>: category key (e.g. <c>"Gods"</c>) →
    /// display info (Title / SubTitle / SortTitle). Drives the master-list group headers
    /// and category facet. Built whenever <c>lorebookinfo.json</c> reloads. Defaults to
    /// empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, LorebookCategoryInfo> LorebookCategories => EmptyLorebookCategoryMap;

    private static readonly IReadOnlyDictionary<string, Lorebook> EmptyLorebookMap
        = new Dictionary<string, Lorebook>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<int, Lorebook> EmptyLorebookByIdMap
        = new Dictionary<int, Lorebook>();

    private static readonly IReadOnlyDictionary<string, LorebookCategoryInfo> EmptyLorebookCategoryMap
        = new Dictionary<string, LorebookCategoryInfo>(StringComparer.Ordinal);

    // ── Player titles (#248) ──────────────────────────────────────────────

    /// <summary>
    /// Player-title envelope key (e.g. <c>"Title_5018"</c>) → <see cref="PlayerTitle"/>
    /// POCO from <c>playertitles.json</c> (~679 entries). The envelope key is the only
    /// identifier the POCO carries — there is no separate InternalName, so the kind
    /// target's selection contract is the <c>"Title_N"</c> envelope key itself (matches
    /// the existing <see cref="EntityRef.PlayerTitle(string)"/> factory). Defaults to
    /// empty so test fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, PlayerTitle> PlayerTitles => EmptyPlayerTitleMap;

    private static readonly IReadOnlyDictionary<string, PlayerTitle> EmptyPlayerTitleMap
        = new Dictionary<string, PlayerTitle>(StringComparer.Ordinal);

    // NOTE (#248): no QuestsAwardingTitle reverse index. Quests do grant titles
    // (Rewards_Effects "BestowTitle(<arg>)", 17 occurrences in bundled quests.json),
    // but the BestowTitle argument is a free-form slug in a *different namespace*
    // (e.g. "Warsmith", "ScionOfPaullus", "Event_DidMyPart") with no structured key
    // relationship to the "Title_N" envelope keys — the PlayerTitle POCO carries no
    // matching identifier. Linking them would require a lossy
    // strip-colour→strip-punctuation→fuzzy-compare heuristic against a sometimes-
    // prefixed slug, i.e. exactly the synthesised linkage #248 forbids. So the
    // "Quests awarding this title" popup-from-index surface is intentionally NOT
    // built; no index, no Gate-C test (the test is merge-blocking only IF the popup
    // ships). See the PR body for the data finding.
    // ── StorageVaults (#249) ──────────────────────────────────────────────

    /// <summary>
    /// StorageVault envelope key → <see cref="StorageVault"/> POCO from
    /// <c>storagevaults.json</c>. The envelope key is the operator NPC's internal name
    /// (e.g. <c>"NPC_CharlesThompson"</c>), or a <c>"*"</c>-prefixed account-wide form
    /// (e.g. <c>"*AccountStorage_Serbule"</c> — a transfer chest, no operator NPC). The
    /// envelope key is the selection / deep-link contract (matches the existing
    /// <see cref="EntityRef.StorageVault(string)"/> factory). Defaults to empty so test
    /// fakes don't need to opt in.
    /// </summary>
    IReadOnlyDictionary<string, StorageVault> StorageVaults => EmptyStorageVaultMap;

    private static readonly IReadOnlyDictionary<string, StorageVault> EmptyStorageVaultMap
        = new Dictionary<string, StorageVault>(StringComparer.Ordinal);

    /// <summary>
    /// All localizable strings from <c>strings_all.json</c>, keyed by their
    /// dotted/slashed string ID. Primary use today is friendly-name resolution
    /// for chest / NPC / cow / tree prefabs via the
    /// <c>npc_&lt;Area&gt;/&lt;InternalName&gt;_Name</c> convention (with
    /// <c>npc_&lt;InternalName&gt;_Name</c> fallback for area-agnostic
    /// prefabs). See [Player-Log-Signals#display-name-resolution-strings_all]
    /// in the wiki.
    /// </summary>
    IReadOnlyDictionary<string, string> Strings { get; }

    ReferenceFileSnapshot GetSnapshot(string key);

    Task RefreshAsync(string key, CancellationToken ct = default);

    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Fire-and-forget refresh of every known file. Intended for app start.</summary>
    void BeginBackgroundRefresh();

    event EventHandler<string>? FileUpdated;
}
