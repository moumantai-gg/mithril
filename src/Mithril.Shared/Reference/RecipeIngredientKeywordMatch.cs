using Mithril.Reference.Models.Recipes;

namespace Mithril.Shared.Reference;

/// <summary>
/// Why a recipe qualified as a member of the item → consuming-recipes index for the
/// item-detail "Used as" surface (#318 slice 4, surface 2 — <c>RecipeIngredientKeyword</c>
/// #259) for a given keyword tag. Mirrors the <see cref="EffectAbilityMatch"/> /
/// <see cref="EffectAbilityMatchReason"/> shape from slice 1 and the
/// <see cref="RecipeIngredientItemMatch"/> shape from surface 1 so every 1:N
/// reverse-lookup index carries the same provenance-retaining structure. The popup
/// renders membership <em>and</em> provenance from this index directly — there is no
/// second (query-string) derivation that could silently diverge from the materialized
/// set (see the #318 invariant).
/// <para>
/// <b>This relationship is single-reason.</b> A recipe is "used as" a slot-match for a
/// keyword tag iff it has a <see cref="RecipeKeywordIngredient"/> slot whose
/// <see cref="RecipeKeywordIngredient.ItemKeys"/> contains that tag. There is exactly one
/// structural mechanic by which a recipe qualifies — the keyword-ingredient slot — so the
/// only reason is <see cref="KeywordIngredientSlot"/>. (Which item carries the tag, and
/// which of the item's tags matched, is data, not a structural reason; the analogue to
/// surface 1's single <see cref="RecipeIngredientItemMatchReason.DirectIngredient"/>
/// decision.) Per the #318 <em>Discipline</em> rule a single trivial reason is noise, so
/// the popup collapses to a flat list (one section ⇒
/// <see cref="Wpf.ProvenancePopupViewModel.IsFlat"/>). The <c>[Flags]</c> enum +
/// match-record shape is retained anyway for structural parity with
/// <see cref="EffectAbilityMatchReason"/> / <see cref="RecipeIngredientItemMatchReason"/>
/// and so a future second keyword-matching mechanic can be added as another flag without
/// reshaping the index or the popup contract.
/// </para>
/// </summary>
[System.Flags]
public enum RecipeIngredientKeywordMatchReason
{
    /// <summary>No reason. Never present on a real index member; the zero value.</summary>
    None = 0,

    /// <summary>
    /// The recipe has a <see cref="RecipeKeywordIngredient"/> slot whose
    /// <see cref="RecipeKeywordIngredient.ItemKeys"/> list contains the keyed keyword tag —
    /// i.e. any item carrying that tag satisfies the slot. The sole reason in this
    /// single-reason relationship.
    /// </summary>
    KeywordIngredientSlot = 1 << 0,
}

/// <summary>
/// One member of the keyword-tag → consuming-recipes index: the qualifying
/// <see cref="Recipe"/> plus the <see cref="RecipeIngredientKeywordMatchReason"/> flags
/// recording why it qualified. A recipe that references the same tag via several keyword
/// slots is carried <b>once</b> (the index dedups by recipe), so a distinct-member count
/// over these records equals the displayed "View all N".
/// </summary>
public sealed record RecipeIngredientKeywordMatch(Recipe Recipe, RecipeIngredientKeywordMatchReason Reason);
