using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of a Treasure-System <see cref="PowerEntry"/> for the
/// Silmarillion Treasure tab's Power detail pane (#435), hostable in both the
/// master-detail right pane and the popup <see cref="Silmarillion.Views.TreasureDetailWindow"/>.
/// <para>
/// Every region is classed to exactly one ratified tier per
/// <c>docs/planning/silmarillion-412-treasure-detail/spec.md</c>:
/// the title is the <c>InternalName</c> <b>verbatim</b> (Q1 — no humanize / no
/// affix-derivation); the affix is illustrative Fact-body only (present parts only);
/// Skill is a Confirmed Link (Degraded only if the Skills surface is unshipped); Slots
/// are tag-form Set-references; the Tiers ladder is the CF#3 FactTable
/// (<see cref="TreasureTierLadderVm"/>); Pools are Confirmed <c>layers</c> Links; the
/// footer is the G-a KEY (copyable, = title) + ROW (<c>power_NNNN</c>, inert).
/// </para>
/// <para>
/// <b>Recipes cross-link is deferred to #214 (verified out-of-scope here).</b> The
/// #433 brief's Carry-forward #2 bound recipe-side rendering to #214. Verified against
/// live v470: the only in-scope join (<c>power → ProfilesByPower → Item.TSysProfile →
/// RecipesByProducedItem</c>) is real but near-catalog-granular — almost every power is
/// in the catch-all <c>"All"</c> profile, so it resolves to the same enormous
/// "every enchanted/max-enchanted crafted-gear recipe" set for nearly every power.
/// Presenting that as power-specific implies a precision the in-scope data lacks
/// (correctness-adjacent to the roll-resolution ban). The power-precise join lives in
/// recipe <c>ResultEffects</c> (<c>AddItemTSysPower</c> / <c>ExtractTSysPower</c> /
/// <c>TSysCraftedEquipment</c>) via <c>ResultEffectsParser</c> — the #214 surface.
/// No recipes section is rendered until #214 supplies the precise index.
/// </para>
/// </summary>
public sealed class PowerDetailViewModel
{
    public PowerDetailViewModel(
        PowerEntry power,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        ICommand? openEntityCommand = null)
    {
        Power = power;
        // Q1 ruling: InternalName verbatim. The resolver returns it unchanged for
        // EntityKind.Power; calling through it keeps the seam honest rather than
        // hard-coding the bypass here.
        DisplayName = nameResolver.Resolve(EntityRef.Power(power.InternalName));
        SkillDisplay = ResolveSkillDisplay(refData, power.Skill);

        (HasAffix, AffixIllustration) = BuildAffixIllustration(power.Prefix, power.Suffix);

        SkillLink = BuildSkillLink(power.Skill, nameResolver, navigator);
        SlotSetRefs = (power.Slots ?? (IReadOnlyList<string>)[])
            .Where(s => !string.IsNullOrEmpty(s))
            // Constraint members, not a live filter — tag-form, not actionable
            // (availability corollary: still the blue Set-ref chassis, inert click).
            .Select(s => new SetRefVm(s, MatchCount: null, IsActionable: false))
            .ToList();

        TierLadder = TreasureTierLadderVm.Build(power, refData.Attributes, SkillDisplay ?? "");

        PoolLinks = BuildPoolLinks(power.InternalName, refData, nameResolver, navigator);

        OpenEntityCommand = openEntityCommand;

        // G-a footer. KEY = InternalName: a cross-entity reference key (profiles join by
        // it) ⇒ copyable. ROW = power_NNNN: storage-only ⇒ inert. The KEY text duplicates
        // the Fact-title text by design (Q1 consequence) — the strip stays because it
        // carries the copy affordance the title does not.
        var ids = new List<FactFooterId>(2)
        {
            new("KEY", power.InternalName, copyable: true),
        };
        if (!string.IsNullOrEmpty(power.EnvelopeKey))
            ids.Add(new FactFooterId("ROW", power.EnvelopeKey, copyable: false));
        Footer = FactFooterVm.Of(ids.ToArray());
    }

    public PowerEntry Power { get; }

    /// <summary>The gold Cambria Fact-title — <c>InternalName</c> verbatim (Q1).</summary>
    public string DisplayName { get; }

    /// <summary>Resolved skill display name (e.g. "Sword"); null when the power has no skill.</summary>
    public string? SkillDisplay { get; }

    /// <summary>True when the power carries at least one affix part to illustrate.</summary>
    public bool HasAffix { get; }

    /// <summary>
    /// The illustrative affix Fact-body sentence — present parts only, framed as
    /// approximate ("appears on items roughly as …, illustrative, not the canonical
    /// name"). Never a reconstructed canonical item name (Q1 / #434). Empty when
    /// <see cref="HasAffix"/> is false.
    /// </summary>
    public string AffixIllustration { get; }

    /// <summary>Skill cross-link — <c>sparkles</c> glyph, Confirmed (Degraded if the
    /// Skills surface is unshipped — <c>IsNavigable</c> handles that at rest-identical).</summary>
    public LinkVm? SkillLink { get; }

    /// <summary>Equip-slot constraint set — tag-form Set-references (not navigation).</summary>
    public IReadOnlyList<SetRefVm> SlotSetRefs { get; }

    /// <summary>The Tiers ladder (CF#3 FactTable). Null ⇒ N=0, the section self-hides.</summary>
    public TreasureTierLadderVm? TierLadder { get; }

    /// <summary>"Appears in pools" — Confirmed <c>layers</c> Links (authoritative
    /// <c>tsysprofiles</c> join; G-d does not apply).</summary>
    public IReadOnlyList<LinkVm> PoolLinks { get; }

    public ICommand? OpenEntityCommand { get; }

    /// <summary>G-a footer: copyable KEY (InternalName) + inert ROW (power_NNNN).</summary>
    public FactFooterVm Footer { get; }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string? ResolveSkillDisplay(IReferenceDataService refData, string? skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return null;
        if (refData.Skills.TryGetValue(skillKey!, out var s) && !string.IsNullOrEmpty(s.DisplayName))
            return s.DisplayName;
        return skillKey;
    }

    /// <summary>
    /// Compose the illustrative affix sentence from whichever parts exist. The «item»
    /// slot is a literal ellipsis placeholder — never a reconstructed item name. Phrased
    /// as approximate per the #434 ruling (affix logic is non-replicable engine-side).
    /// </summary>
    private static (bool HasAffix, string Text) BuildAffixIllustration(string? prefix, string? suffix)
    {
        var hasPrefix = !string.IsNullOrWhiteSpace(prefix);
        var hasSuffix = !string.IsNullOrWhiteSpace(suffix);
        if (!hasPrefix && !hasSuffix) return (false, "");

        string shape = (hasPrefix, hasSuffix) switch
        {
            (true, true) => $"“{prefix!.Trim()} … {suffix!.Trim()}”",
            (true, false) => $"“{prefix!.Trim()} …”",
            _ => $"“… {suffix!.Trim()}”",
        };
        return (true, $"Appears on items roughly as {shape} — illustrative, not the canonical name.");
    }

    private static LinkVm? BuildSkillLink(
        string? skillKey,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(skillKey)) return null;
        var reference = EntityRef.Skill(skillKey!);
        var chip = new EntityChipVm(
            DisplayName: resolver.Resolve(reference),
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
        return LinkVm.From(chip);
    }

    private static IReadOnlyList<LinkVm> BuildPoolLinks(
        string powerInternalName,
        IReferenceDataService refData,
        IEntityNameResolver resolver,
        IReferenceNavigator navigator)
    {
        if (!refData.ProfilesByPower.TryGetValue(powerInternalName, out var profiles) || profiles.Count == 0)
            return [];
        return profiles
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(profileName =>
            {
                var reference = EntityRef.Profile(profileName);
                return new LinkVm(
                    DisplayName: resolver.Resolve(reference),
                    Glyph: LinkGlyph.Pool,
                    Reference: reference,
                    IsNavigable: navigator.CanOpen(reference));
            })
            .ToList();
    }
}
