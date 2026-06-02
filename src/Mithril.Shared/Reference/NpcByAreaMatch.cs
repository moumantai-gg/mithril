namespace Mithril.Shared.Reference;

/// <summary>
/// Why an NPC qualified as a member of the area → NPCs index ("NPCs in this area")
/// for a given area envelope key (#318 slice 4, surface 4). Mirrors the
/// <see cref="RecipeIngredientItemMatch"/> / <see cref="RecipeIngredientItemMatchReason"/>
/// shape (surface 1) and <see cref="EffectAbilityMatch"/> (slices 2+3) so every 1:N
/// reverse-lookup index carries the same provenance-retaining structure. The popup
/// renders membership <em>and</em> provenance from this index directly — there is no
/// second (query-string) derivation that could silently diverge (the #318 invariant; see
/// the #318 invariant).
/// <para>
/// <b>This relationship is single-reason.</b> An NPC is "in this area" iff its
/// <see cref="Mithril.Reference.Models.Npcs.Npc.AreaName"/> equals the area key — the
/// single accumulation done in <c>ReferenceDataService.BuildAreaNpcCrossLinkIndex</c>.
/// There is no other mechanic by which an NPC belongs to an area, so the only reason is
/// <see cref="InArea"/>. Per the #318 <em>Discipline</em> rule a single trivial reason is
/// noise, so the popup collapses to a flat list (one section ⇒
/// <see cref="Wpf.ProvenancePopupViewModel.IsFlat"/>). The <c>[Flags]</c> enum +
/// match-record shape is retained anyway for structural parity with
/// <see cref="RecipeIngredientItemMatchReason"/> / <see cref="EffectAbilityMatchReason"/>
/// and so a future second area-membership mechanic can be added as another flag without
/// reshaping the index or the popup contract.
/// </para>
/// </summary>
[System.Flags]
public enum NpcByAreaMatchReason
{
    /// <summary>No reason. Never present on a real index member; the zero value.</summary>
    None = 0,

    /// <summary>
    /// The NPC's <see cref="Mithril.Reference.Models.Npcs.Npc.AreaName"/> equals the area
    /// envelope key. The sole reason in this single-reason relationship.
    /// </summary>
    InArea = 1 << 0,
}

/// <summary>
/// One member of the area → NPCs index for an area envelope key: the qualifying
/// <see cref="NpcEntry"/> plus the <see cref="NpcByAreaMatchReason"/> flags recording why
/// it qualified. An NPC is carried <b>once</b> (the index dedups by NPC), so a
/// distinct-member count over these records equals the displayed "View all N".
/// </summary>
public sealed record NpcByAreaMatch(NpcEntry Npc, NpcByAreaMatchReason Reason);
