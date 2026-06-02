using Mithril.Reference.Models.Recipes;

namespace Mithril.Shared.Reference;

/// <summary>
/// Why a recipe qualified as a member of the item → consuming-recipes index ("Used in")
/// for a given item InternalName (#318 slice 4, surface 1). Mirrors the
/// <see cref="EffectAbilityMatch"/> / <see cref="EffectAbilityMatchReason"/> shape from
/// slice 1 so every 1:N reverse-lookup index carries the same provenance-retaining
/// structure. The popup renders membership <em>and</em> provenance from this index
/// directly — there is no second (query-string) derivation that could silently diverge
/// (see the #318 invariant).
/// <para>
/// <b>This relationship is single-reason.</b> An item is "used in" a recipe iff the recipe
/// references it through a direct <see cref="RecipeItemIngredient"/> (numeric item-code
/// binding). <see cref="RecipeKeywordIngredient"/> slots (e.g. "any Crystal") are a
/// distinct surface — the item-detail "Used as" section / <c>RecipeIngredientKeyword</c>
/// #259 — and never feed this index. There is no other mechanic by which an item is an
/// ingredient, so the only reason is <see cref="DirectIngredient"/>. Per the #318
/// <em>Discipline</em> rule a single trivial reason is noise, so the popup collapses to a
/// flat list (one section ⇒ <see cref="Wpf.ProvenancePopupViewModel.IsFlat"/>). The
/// <c>[Flags]</c> enum + match-record shape is retained anyway for structural parity with
/// <see cref="EffectAbilityMatchReason"/> and so a future second ingredient mechanic can
/// be added as another flag without reshaping the index or the popup contract.
/// </para>
/// </summary>
[System.Flags]
public enum RecipeIngredientItemMatchReason
{
    /// <summary>No reason. Never present on a real index member; the zero value.</summary>
    None = 0,

    /// <summary>
    /// The recipe binds this item directly by numeric id via a
    /// <see cref="RecipeItemIngredient"/> (<c>ItemCode</c> resolving to the item's
    /// <c>item_NNNN</c> envelope). The sole reason in this single-reason relationship.
    /// </summary>
    DirectIngredient = 1 << 0,
}

/// <summary>
/// One member of the item → consuming-recipes index for an item InternalName: the
/// qualifying <see cref="Recipe"/> plus the <see cref="RecipeIngredientItemMatchReason"/>
/// flags recording why it qualified. A recipe that references the same item via several
/// direct ingredient slots is carried <b>once</b> (the index dedups by recipe), so a
/// distinct-member count over these records equals the displayed "View all N".
/// </summary>
public sealed record RecipeIngredientItemMatch(Recipe Recipe, RecipeIngredientItemMatchReason Reason);
