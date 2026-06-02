using Mithril.Reference.Models.Abilities;

namespace Mithril.Shared.Reference;

/// <summary>
/// Why an ability qualified as a member of the effect-keyword → abilities index for a
/// given tag. The effect→abilities relationship is the union of three ability fields;
/// the index used to flatten that union to a dedup'd ability set, discarding which field
/// matched. Retaining the reason is the invariant that lets the relationship explain
/// itself rather than requiring a second (query-string) derivation to agree with the
/// index (see the #318 invariant).
/// <para>
/// The three reasons are not mutually exclusive: one ability can qualify for the same
/// tag via more than one field (e.g. it both requires and is enabled by the keyword).
/// Such a member is carried <b>once</b> with multiple reason flags set — dedup intent
/// preserved, provenance complete. <see cref="EffectAbilityMatchReason"/> is therefore
/// a <c>[Flags]</c> enum.
/// </para>
/// </summary>
[System.Flags]
public enum EffectAbilityMatchReason
{
    /// <summary>No reason. Never present on a real index member; the zero value.</summary>
    None = 0,

    /// <summary>
    /// The tag appears in <see cref="Ability.EffectKeywordReqs"/> — the ability hard-requires
    /// the effect's keyword to be present (a gate).
    /// </summary>
    Requires = 1 << 0,

    /// <summary>
    /// The tag appears in <see cref="Ability.EffectKeywordsIndicatingEnabled"/> — having the
    /// effect's keyword enables / unlocks the ability.
    /// </summary>
    EnabledBy = 1 << 1,

    /// <summary>
    /// The tag equals <see cref="Ability.TargetEffectKeywordReq"/> — the ability targets
    /// something carrying the effect's keyword.
    /// </summary>
    Targets = 1 << 2,
}

/// <summary>
/// One member of the effect-keyword → abilities index for a tag: the qualifying
/// <see cref="Ability"/> plus the <see cref="EffectAbilityMatchReason"/> flags recording
/// which of the three unioned ability fields caused it to qualify. A member that matched
/// via several fields is represented by a single record with several reason flags set —
/// the index never double-counts a multi-reason ability, so a distinct-member count over
/// these records equals the displayed "View all N".
/// </summary>
public sealed record EffectAbilityMatch(Ability Ability, EffectAbilityMatchReason Reason);
