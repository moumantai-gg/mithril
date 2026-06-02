using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Misc;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of an <see cref="PocoEffect"/> for the Silmarillion Effects tab
/// detail pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.EffectDetailWindow"/>.
/// <para>
/// Nine sections: header, formatted description, metadata strip (duration + stacking type
/// + display mode), keyword chips, conditional-rule sub-tables fed by
/// <see cref="IReferenceDataService.AbilityDynamicDots"/> and
/// <see cref="IReferenceDataService.AbilityDynamicSpecialValues"/>, stacks-with cluster
/// (other effects sharing <see cref="PocoEffect.StackingType"/>), required-by-abilities
/// cluster with cap + a "View all N" provenance-popup affordance,
/// procs-from-abilities-with-keyword rows, and a SpewText footer.
/// </para>
/// <para>
/// <b>#318 — effect&#8594;abilities is the migrated 1:N surface.</b> The "View all N"
/// affordance no longer deep-links via the retired <c>AbilityByEffectKeyword</c>
/// synthetic kind (which re-derived the set from one of three unioned fields and
/// silently diverged — 23 of 24 tags hit an empty list). It opens a
/// <see cref="ProvenancePopupViewModel"/> fed
/// <see cref="IReferenceDataService.AbilitiesByEffectKeyword"/> directly, sectioned by
/// <see cref="EffectAbilityMatchReason"/>. The set is materialized once, in the index,
/// retaining provenance — there is no second derivation, so the bug class is dissolved
/// (see the #318 invariant).
/// </para>
/// </summary>
public sealed class EffectDetailViewModel
{
    /// <summary>
    /// Host-supplied opener for the required-by-abilities provenance popup. Defaults to
    /// <see cref="ShowProvenancePopupWindow"/> (creates + <c>Show()</c>s a
    /// <see cref="ProvenancePopupWindow"/>). Tests swap in a capturing delegate so the VM
    /// is fully assertable without spawning a window. Opening the popup this way never
    /// calls <c>IReferenceNavigator</c>, so it pushes no back/forward history — same
    /// non-navigating contract as <c>IReferenceKindTarget.TryOpenInWindow</c>.
    /// </summary>
    public static Action<ProvenancePopupViewModel, ICommand?> ProvenancePopupOpener { get; set; }
        = ShowProvenancePopupWindow;

    public EffectDetailViewModel(
        PocoEffect effect,
        string envelopeKey,
        IReferenceDataService refData,
        IReferenceNavigator navigator,
        IEntityNameResolver nameResolver,
        Silmarillion.SilmarillionSettings settings,
        ICommand? openEntityCommand = null)
    {
        Effect = effect;
        EnvelopeKey = envelopeKey;
        DisplayName = string.IsNullOrEmpty(effect.Name) ? envelopeKey : effect.Name!;

        DurationLabel = FormatDuration(effect.Duration);
        DisplayMode = NormaliseDisplayMode(effect.DisplayMode);

        KeywordChips = BuildKeywordChips(effect.Keywords, navigator);
        ConditionalDotRows = BuildConditionalDotRows(effect, refData);
        ConditionalSpecialValueRows = BuildConditionalSpecialValueRows(effect, refData);
        StackingTypeChip = BuildStackingTypeChip(effect, envelopeKey, refData, navigator);
        var (chips, total, popup) = BuildRequiredByAbilities(
            effect, refData, nameResolver, navigator, settings.RequiredByAbilitiesChipCap);
        RequiredByAbilityChips = chips;
        RequiredByAbilitiesTotal = total;
        RequiredByAbilitiesPopup = popup;
        ProcsFromAbilityKeywordRows = BuildProcsFromAbilityKeywordRows(effect.AbilityKeywords);

        SpewText = string.IsNullOrEmpty(effect.SpewText) ? null : effect.SpewText;

        OpenEntityCommand = openEntityCommand;
        ShowRequiredByAbilitiesPopupCommand = new RelayCommand(
            () => ProvenancePopupOpener(RequiredByAbilitiesPopup!, OpenEntityCommand),
            () => RequiredByAbilitiesPopup is not null);

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // Legacy chip/string/command members above stay (the existing tab tests
        // + the detail-pane contract); these are the grammar-tier carriers the
        // view binds. #404 Phase-2 / ratified E4 classification for Effect:
        //   • keyword chips + StackingType chip = SET-REFERENCE, not Link
        //     (activation scopes the Effects tab to a set of N — E4 RATIFIED,
        //     "Phase 5 must NOT migrate these into Link");
        //   • the "View all N →" RequiredBy ghost-gold button = Set-ref
        //     summary-form (matrix T7);
        //   • the RequiredBy ability chips = 1:1 entity refs = Link enumeration;
        //   • Duration / Display-mode = inert label-value Fact (→ FactTable);
        //   • the ProcsFromAbilityKeyword bordered non-nav tags = the forbidden
        //     inverted-affordance "grey Fact pill" the availability corollary
        //     says Phase 5 MUST correct, not preserve → tag-form Set-ref
        //     (IsActionable=false, still the blue chassis);
        //   • the inert-mono EnvelopeKey footer (matrix T14) = storage-only ⇒
        //     the inert `ROW` cell (E5), exactly PlayerTitle's path.

        // Keyword chips → tag-form Set-ref (no count/arrow; the chip shape
        // carries the tier). Actionable iff the keyword-filter target is wired
        // (navigable); the per-chip Activate bridges Set-ref's VM-param click to
        // the host OpenEntityCommand(reference) — the pilot RecipeKeywordSlotVm
        // "SetRefVm + own command" idiom. Non-navigable ⇒ IsActionable=false:
        // availability corollary (blue chassis, safe no-op), never a grey pill.
        KeywordSetRefs = KeywordChips.Select(BuildFilterSetRef).ToList();

        // StackingType → summary-form Set-ref ("{type} · {peers} matches →").
        // The legacy chip baked the peer count into its DisplayName ("Food
        // (325)"); the grammar puts the count on the chip via MatchCount.
        StackingTypeSetRef = BuildStackingTypeSetRef(
            effect, StackingTypeChip, OpenEntityCommand);

        // "Procs from abilities with keyword" tags: reverse-direction
        // ability-keyword tags with no wired surface (VM doc: "navigation is
        // deferred"). Carry-forward #4 / availability corollary: this is the
        // forbidden inert-bordered-box "grey pill" lie — Phase 5 corrects it to
        // a tag-form Set-ref on the blue chassis, IsActionable=false (unwired),
        // NOT preserved as a Fact box.
        ProcsFromAbilityKeywordSetRefs = ProcsFromAbilityKeywordRows
            .Select(r => new EffectFilterSetRefVm(
                new SetRefVm(r.Tag, MatchCount: null, IsActionable: false), null))
            .ToList();

        // RequiredBy ability chips → unified Link enumeration (Density="List").
        RequiredByAbilityLinks = RequiredByAbilityChips.Select(LinkVm.From).ToList();

        // "View all N →" RequiredBy ghost-gold button → Set-ref summary-form,
        // actionable via the EXISTING ShowRequiredByAbilitiesPopupCommand
        // (parameterless — SetRef passes the VM, RelayCommand ignores it). Null
        // when no ability relates (section hides). Gold→blue per ratified G-b.
        RequiredByAbilitiesSetRef = RequiredByAbilitiesPopup is null
            ? null
            : new SetRefVm("Required by abilities",
                MatchCount: RequiredByAbilitiesTotal, IsActionable: true);

        // Duration / Display-mode → ONE inert Fact strip (label-value pairs,
        // empties skipped; StripText "" ⇒ the shared Style self-hides) —
        // exactly the pilot StatStrip pattern. StackingType is NOT in here: it
        // is a Set-ref, not a Fact (kept separate, same visual band).
        var meta = new List<FactPair>(2);
        if (!string.IsNullOrEmpty(DurationLabel)) meta.Add(new FactPair("Duration", DurationLabel!));
        if (!string.IsNullOrEmpty(DisplayMode)) meta.Add(new FactPair("Display mode", DisplayMode!));
        MetadataStrip = FactTableVm.Strip(meta);

        // Footer (matrix #14 / G-a · ratified E5). The EnvelopeKey was already
        // an inert mono footer (matrix T14, not click-to-copy) — the author's
        // data judgement that it is a display/storage-only key. Its grammar
        // form is the inert `ROW` cell (copyable:false), exactly PlayerTitle's
        // path — NOT the copyable `KEY`. None() self-hides if keyless.
        Footer = string.IsNullOrEmpty(EnvelopeKey)
            ? FactFooterVm.None()
            : FactFooterVm.Of(new FactFooterId("ROW", EnvelopeKey, copyable: false));
    }

    private EffectFilterSetRefVm BuildFilterSetRef(EntityChipVm chip)
    {
        var wired = chip.IsNavigable && OpenEntityCommand is not null;
        var activate = wired
            ? new RelayCommand(() => OpenEntityCommand!.Execute(chip.Reference))
            : null;
        // Tag-form (MatchCount null — bare keyword, no count/arrow). IsActionable
        // mirrors LinkVm.IsNavigable's inverse-of-legacy stance: unwired ⇒ blue
        // chassis + no-op (availability corollary), never a degraded grey pill.
        return new EffectFilterSetRefVm(
            new SetRefVm(chip.DisplayName, MatchCount: null, IsActionable: wired),
            activate);
    }

    private static EffectFilterSetRefVm? BuildStackingTypeSetRef(
        PocoEffect effect, EntityChipVm? legacyChip, ICommand? openEntityCommand)
    {
        if (legacyChip is null || string.IsNullOrEmpty(effect.StackingType)) return null;
        // Recover the peer count the legacy chip baked into "Type (N)" — the
        // single source of truth is the same DisplayName the tests assert, so
        // the Set-ref count can't diverge from the legacy chip.
        var open = legacyChip.DisplayName.LastIndexOf('(');
        var close = legacyChip.DisplayName.LastIndexOf(')');
        int? count = open >= 0 && close > open
            && int.TryParse(
                legacyChip.DisplayName.AsSpan(open + 1, close - open - 1),
                out var n)
            ? n
            : null;
        var wired = legacyChip.IsNavigable && openEntityCommand is not null;
        var activate = wired
            ? new RelayCommand(() => openEntityCommand!.Execute(legacyChip.Reference))
            : null;
        // Summary-form: count rides on the chip ("{type} · N matches →").
        return new EffectFilterSetRefVm(
            new SetRefVm(effect.StackingType!, MatchCount: count, IsActionable: wired),
            activate);
    }

    private static void ShowProvenancePopupWindow(ProvenancePopupViewModel vm, ICommand? chipClick) =>
        new ProvenancePopupWindow { DataContext = vm, ChipClickCommand = chipClick }.Show();

    public PocoEffect Effect { get; }
    public string EnvelopeKey { get; }
    public string DisplayName { get; }
    public int IconId => Effect.IconId;
    public string? Description => Effect.Desc;

    /// <summary>Formatted duration label or <see langword="null"/> when the duration is missing/zero.</summary>
    public string? DurationLabel { get; }

    /// <summary>
    /// <see cref="PocoEffect.DisplayMode"/> when non-default. The universal default
    /// <c>"Effect"</c> is filtered out so the chip only surfaces for entries with
    /// information content (e.g. <c>"Cocoon"</c>, <c>"Curse"</c>).
    /// </summary>
    public string? DisplayMode { get; }

    public IReadOnlyList<EntityChipVm> KeywordChips { get; }
    public IReadOnlyList<EffectConditionalDotRow> ConditionalDotRows { get; }
    public IReadOnlyList<EffectConditionalSpecialValueRow> ConditionalSpecialValueRows { get; }

    /// <summary>True when at least one conditional sub-table row applies — drives the section header visibility.</summary>
    public bool HasConditionalRuleRows =>
        ConditionalDotRows.Count > 0 || ConditionalSpecialValueRows.Count > 0;

    /// <summary>
    /// Chip for the metadata strip's <c>"Stacking type:"</c> row — label is the StackingType
    /// value plus a peer-count suffix (e.g. <c>"Food (325)"</c>), reference is
    /// <see cref="EntityRef.EffectByStackingType(string)"/>. Clicking filters the Effects
    /// tab to <c>StackingType = "&lt;value&gt;"</c>. Null when the effect has no StackingType
    /// or is the sole entry in the stacking group (no peers means no useful filter target,
    /// per the cookbook's default-value-noise-filtering rule).
    /// <para>
    /// Folds what was originally a separate "Stacks with" section into the metadata strip:
    /// the chip is the StackingType's navigation affordance directly. Per #259's
    /// keyword-collapse precedent — don't fan out to per-effect chips when cardinality
    /// could be large (the "Food" stacking group alone has ~326 entries).
    /// </para>
    /// </summary>
    public EntityChipVm? StackingTypeChip { get; }
    public IReadOnlyList<EntityChipVm> RequiredByAbilityChips { get; }

    /// <summary>
    /// Distinct count of abilities related to this effect (membership of
    /// <see cref="IReferenceDataService.AbilitiesByEffectKeyword"/> across the effect's
    /// keywords, multi-reason members counted once). Drives the "View all N →" label.
    /// 0 ⇒ no relationship and the whole section hides.
    /// </summary>
    public int RequiredByAbilitiesTotal { get; }

    /// <summary>
    /// The provenance popup VM opened by <see cref="ShowRequiredByAbilitiesPopupCommand"/>,
    /// or <see langword="null"/> when no ability relates to this effect. Built from the
    /// index directly (membership + provenance), sectioned by
    /// <see cref="EffectAbilityMatchReason"/>; single-reason collapses to a flat list. This
    /// replaces the retired <c>AbilityByEffectKeyword</c> synthetic-kind deep link — there
    /// is no query re-derivation, so the displayed set cannot diverge from the index.
    /// </summary>
    public ProvenancePopupViewModel? RequiredByAbilitiesPopup { get; }

    /// <summary>
    /// Opens <see cref="RequiredByAbilitiesPopup"/> via <see cref="ProvenancePopupOpener"/>.
    /// Bound to the always-visible "View all N →" affordance. The popup is a window shown
    /// directly — opening it pushes no navigator history (#229 contract).
    /// </summary>
    public ICommand ShowRequiredByAbilitiesPopupCommand { get; }

    public IReadOnlyList<EffectProcsFromAbilityKeywordRow> ProcsFromAbilityKeywordRows { get; }
    public string? SpewText { get; }

    public ICommand? OpenEntityCommand { get; }

    // ── Phase 5 grammar-primitive carriers ──────────────────────────────────

    /// <summary>
    /// Keyword chips as tag-form Set-references (ratified E4 — keyword chips are
    /// Set-reference, not Link). Each carries its own Activate command bridging
    /// the shared <c>SetRef</c>'s VM-param click to <see cref="OpenEntityCommand"/>;
    /// non-navigable ⇒ <see cref="SetRefVm.IsActionable"/> false (availability
    /// corollary — blue chassis, safe no-op, never a grey Fact pill).
    /// </summary>
    public IReadOnlyList<EffectFilterSetRefVm> KeywordSetRefs { get; }

    /// <summary>
    /// The StackingType as a summary-form Set-reference
    /// (<c>"{type} · {peers} matches →"</c>; ratified E4). The count is recovered
    /// from <see cref="StackingTypeChip"/>'s <c>"Type (N)"</c> DisplayName so it
    /// cannot diverge from the legacy chip the tests assert. Null when there is
    /// no StackingType / no peers (the row hides), exactly like the legacy chip.
    /// </summary>
    public EffectFilterSetRefVm? StackingTypeSetRef { get; }

    /// <summary>
    /// "Procs from abilities with keyword" tags as tag-form Set-references with
    /// <see cref="SetRefVm.IsActionable"/> = false (the reverse-direction filter
    /// is not wired). Carry-forward #4 / availability corollary: this replaces
    /// the forbidden inert-bordered-box "grey pill" — still the blue Set-ref
    /// chassis, NOT a Fact box (Phase 5 must correct, not preserve).
    /// </summary>
    public IReadOnlyList<EffectFilterSetRefVm> ProcsFromAbilityKeywordSetRefs { get; }

    /// <summary>
    /// RequiredBy ability cross-links as the unified <see cref="LinkVm"/>
    /// (matrix #6/#10). The view renders these <c>Density="List"</c> — the
    /// canonical pilot enumeration pattern. Projection of
    /// <see cref="RequiredByAbilityChips"/> (same cap).
    /// </summary>
    public IReadOnlyList<LinkVm> RequiredByAbilityLinks { get; }

    /// <summary>
    /// The "View all N →" RequiredBy drawer as a summary-form Set-reference
    /// (matrix T7): <c>"Required by abilities · N matches →"</c>, actionable via
    /// <see cref="ShowRequiredByAbilitiesPopupCommand"/>. Null when no ability
    /// relates (section hides). Gold→blue per ratified G-b.
    /// </summary>
    public SetRefVm? RequiredByAbilitiesSetRef { get; }

    /// <summary>
    /// Duration / Display-mode as one inert Fact strip (label-value pairs,
    /// empties skipped; empty ⇒ <see cref="FactTableVm.StripText"/> "" so the
    /// shared <c>FactTable</c> Style self-hides). Mirrors the pilot StatStrip;
    /// StackingType is deliberately NOT here (it is a Set-ref, not a Fact).
    /// </summary>
    public FactTableVm MetadataStrip { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14 / G-a · ratified E5). The EnvelopeKey
    /// was already an inert-mono footer (matrix T14) — a display/storage-only
    /// key ⇒ the inert <c>ROW</c> cell (PlayerTitle's path), not a copyable
    /// <c>KEY</c>. <see cref="FactFooterVm.None"/> (self-hides) if keyless.
    /// </summary>
    public FactFooterVm Footer { get; }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Render the Duration field. JSON ships either an int second count, the literal string
    /// <c>"Permanent"</c>, or a sentinel int (<c>-1</c>, <c>-2</c>). The sentinel meanings are
    /// best-guess labels — Elder Game doesn't publish a key and reverse-engineering from
    /// bundled data suggests <c>-1</c> = until cleansed and <c>-2</c> = until removed (e.g.
    /// <c>effect_10003</c> Sticky! is <c>-2</c>). If a future PG patch contradicts this,
    /// the projection needs to change — there's no automatic drift detector.
    /// </summary>
    private static string? FormatDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return null;
        if (string.Equals(duration, "Permanent", StringComparison.Ordinal)) return "Permanent";
        if (!int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return duration;

        if (seconds == 0) return null;
        if (seconds == -1) return "Until cleansed";
        if (seconds == -2) return "Until removed";
        if (seconds < 0) return duration;
        if (seconds < 60) return $"{seconds} second{(seconds == 1 ? "" : "s")}";
        if (seconds < 3600)
        {
            var mins = seconds / 60;
            var remSec = seconds % 60;
            return remSec == 0
                ? $"{mins} minute{(mins == 1 ? "" : "s")}"
                : $"{mins} min {remSec} sec";
        }
        var hours = seconds / 3600;
        var remMin = (seconds % 3600) / 60;
        return remMin == 0
            ? $"{hours} hour{(hours == 1 ? "" : "s")}"
            : $"{hours} hr {remMin} min";
    }

    /// <summary>
    /// Filter out <see cref="PocoEffect.DisplayMode"/>'s universal default so the chip
    /// only renders when it carries information per the cookbook's <em>Default-value
    /// noise filtering</em> rule. <c>"Effect"</c> dominates the bundled file (the
    /// implicit default when JSON omits the field); rare values like <c>"Cocoon"</c>
    /// or <c>"Curse"</c> are the player-facing variants.
    /// </summary>
    private static string? NormaliseDisplayMode(string? raw) =>
        string.IsNullOrEmpty(raw) || string.Equals(raw, "Effect", StringComparison.Ordinal)
            ? null
            : raw;

    private static IReadOnlyList<EntityChipVm> BuildKeywordChips(
        IReadOnlyList<string>? keywords,
        IReferenceNavigator navigator)
    {
        if (keywords is null || keywords.Count == 0) return [];
        var list = new List<EntityChipVm>(keywords.Count);
        foreach (var tag in keywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            var reference = EntityRef.EffectKeyword(tag);
            list.Add(new EntityChipVm(
                DisplayName: tag,
                IconId: 0,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference)));
        }
        return list;
    }

    private static IReadOnlyList<EffectConditionalDotRow> BuildConditionalDotRows(
        PocoEffect effect, IReferenceDataService refData)
    {
        var rules = refData.AbilityDynamicDots;
        if (rules.Count == 0) return [];

        var list = new List<EffectConditionalDotRow>();
        foreach (var rule in rules)
        {
            if (!AbilityRulePredicate.Matches(rule.ReqEffectKeywords, effect.Keywords)) continue;
            var display = FormatDotRule(rule);
            list.Add(new EffectConditionalDotRow(display, rule.ReqAbilityKeywords ?? []));
        }
        return list;
    }

    private static string FormatDotRule(AbilityDynamicDot rule)
    {
        // "Deals N damage per tick × M ticks of <DamageType>" — keep the format close to the
        // bundled-data shape so the rendered prose matches in-game tooltip expectations.
        var damageType = string.IsNullOrEmpty(rule.DamageType) ? "(unspecified)" : rule.DamageType;
        var body = $"{rule.DamagePerTick} damage × {rule.NumTicks} ticks of {damageType}";

        var gates = new List<string>();
        if (rule.ReqAbilityKeywords is { Count: > 0 } abKw)
            gates.Add($"ability keywords [{string.Join(", ", abKw)}]");
        if (!string.IsNullOrEmpty(rule.ReqActiveSkill))
            gates.Add($"active skill {rule.ReqActiveSkill}");

        return gates.Count > 0 ? $"{body}, gated by {string.Join(" + ", gates)}" : body;
    }

    private static IReadOnlyList<EffectConditionalSpecialValueRow> BuildConditionalSpecialValueRows(
        PocoEffect effect, IReferenceDataService refData)
    {
        var rules = refData.AbilityDynamicSpecialValues;
        if (rules.Count == 0) return [];

        var list = new List<EffectConditionalSpecialValueRow>();
        foreach (var rule in rules)
        {
            if (!AbilityRulePredicate.Matches(rule.ReqEffectKeywords, effect.Keywords)) continue;
            if (rule.SkipIfZero == true && rule.Value == 0) continue;
            list.Add(new EffectConditionalSpecialValueRow(
                FormatSpecialValueRule(rule),
                rule.ReqAbilityKeywords ?? []));
        }
        return list;
    }

    private static string FormatSpecialValueRule(AbilityDynamicSpecialValue rule)
    {
        var label = string.IsNullOrEmpty(rule.Label) ? "(value)" : rule.Label;
        var value = rule.Value.ToString("0.##", CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrEmpty(rule.Suffix) ? "" : $" {rule.Suffix}";
        var body = $"{label} {value}{suffix}";

        if (rule.ReqAbilityKeywords is { Count: > 0 } abKw)
            return $"{body}, gated by ability keywords [{string.Join(", ", abKw)}]";
        return body;
    }

    /// <summary>
    /// Build the metadata-strip StackingType chip. Returns <see langword="null"/> when the
    /// effect has no StackingType or is the only entry in its stacking group — the entire
    /// "Stacking type:" row hides in those cases (the chip *is* the row's payload, so
    /// no chip means no row).
    /// </summary>
    private static EntityChipVm? BuildStackingTypeChip(
        PocoEffect effect,
        string envelopeKey,
        IReferenceDataService refData,
        IReferenceNavigator navigator)
    {
        if (string.IsNullOrEmpty(effect.StackingType)) return null;
        if (!refData.EffectsByStackingType.TryGetValue(effect.StackingType!, out var peers)) return null;

        var peerCount = 0;
        foreach (var peer in peers)
        {
            if (peer.InternalName is null) continue;
            if (peer.InternalName == envelopeKey) continue;
            peerCount++;
        }
        if (peerCount == 0) return null;

        var reference = EntityRef.EffectByStackingType(effect.StackingType!);
        return new EntityChipVm(
            DisplayName: $"{effect.StackingType} ({peerCount})",
            IconId: 0,
            Reference: reference,
            IsNavigable: navigator.CanOpen(reference));
    }

    /// <summary>
    /// Build the cap-limited "Required by abilities" chip cluster, the distinct member
    /// count, and the provenance popup VM — all from
    /// <see cref="IReferenceDataService.AbilitiesByEffectKeyword"/> directly (#318). Reads
    /// the union of <c>AbilitiesByEffectKeyword[tag]</c> across every tag in
    /// <see cref="PocoEffect.Keywords"/> (the index excludes
    /// <see cref="Ability.InternalAbility"/> rows already and is dedup'd per tag).
    /// <para>
    /// Provenance is preserved across tags: an ability that qualifies under several tags /
    /// fields is carried <b>once</b>, OR-ing its <see cref="EffectAbilityMatchReason"/>
    /// flags — so the distinct count equals the displayed "View all N" and the popup's
    /// section membership is exactly the index membership (no second derivation). The chip
    /// cluster shows the first <paramref name="cap"/> by Skill→Level; the popup carries the
    /// full set sectioned Requires / Enabled by / Targets, collapsing to a flat list when
    /// only one reason is present.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<EntityChipVm> Chips, int Total, ProvenancePopupViewModel? Popup)
        BuildRequiredByAbilities(
            PocoEffect effect,
            IReferenceDataService refData,
            IEntityNameResolver nameResolver,
            IReferenceNavigator navigator,
            int cap)
    {
        if (effect.Keywords is null || effect.Keywords.Count == 0) return ([], 0, null);

        // Distinct-by-InternalName across tags, OR-accumulating the reason flags so a
        // member that qualified under several tags/fields is carried once with complete
        // provenance. This is the single materialization — the popup is a view over it.
        var byName = new Dictionary<string, (Ability Ability, EffectAbilityMatchReason Reason)>(
            StringComparer.Ordinal);
        foreach (var tag in effect.Keywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            if (!refData.AbilitiesByEffectKeyword.TryGetValue(tag, out var matches)) continue;
            foreach (var match in matches)
            {
                var ability = match.Ability;
                if (ability.InternalName is null) continue;
                if (byName.TryGetValue(ability.InternalName, out var existing))
                    byName[ability.InternalName] = (existing.Ability, existing.Reason | match.Reason);
                else
                    byName[ability.InternalName] = (ability, match.Reason);
            }
        }

        if (byName.Count == 0) return ([], 0, null);

        var ordered = byName.Values.ToList();
        ordered.Sort(static (a, b) =>
        {
            var skillCmp = StringComparer.OrdinalIgnoreCase.Compare(a.Ability.Skill, b.Ability.Skill);
            if (skillCmp != 0) return skillCmp;
            return a.Ability.Level.CompareTo(b.Ability.Level);
        });

        EntityChipVm Chip(Ability ability)
        {
            var reference = EntityRef.Ability(ability.InternalName!);
            return new EntityChipVm(
                DisplayName: nameResolver.Resolve(reference),
                IconId: ability.IconID,
                Reference: reference,
                IsNavigable: navigator.CanOpen(reference));
        }

        var visibleCount = Math.Min(cap, ordered.Count);
        var chips = new List<EntityChipVm>(visibleCount);
        for (var i = 0; i < visibleCount; i++)
            chips.Add(Chip(ordered[i].Ability));

        // Build the provenance sections: one section per reason that has members, in the
        // canonical Requires → Enabled by → Targets order. Members already sorted; section
        // chips inherit that order. ProvenancePopupViewModel collapses to a flat list when
        // only one section results (a single trivial reason is noise — #318 Discipline).
        var sections = new List<ProvenancePopupSection>(3);
        AddSection(sections, ordered, EffectAbilityMatchReason.Requires, "Requires", Chip);
        AddSection(sections, ordered, EffectAbilityMatchReason.EnabledBy, "Enabled by", Chip);
        AddSection(sections, ordered, EffectAbilityMatchReason.Targets, "Targets", Chip);

        // ToQueryCommand intentionally unset (#318 orchestrator decision): the API surface
        // is final so later slices reuse ProvenancePopupViewModel unchanged, but the
        // effect→abilities To-Query projection logic is a deliberate fast-follow — the
        // popup-from-index is already the correct, count-bearing surface.
        var popup = new ProvenancePopupViewModel(
            title: $"Abilities related to {DisplayNameOf(effect)}",
            sections: sections);

        return (chips, byName.Count, popup);
    }

    private static void AddSection(
        List<ProvenancePopupSection> sections,
        List<(Ability Ability, EffectAbilityMatchReason Reason)> ordered,
        EffectAbilityMatchReason reason,
        string label,
        Func<Ability, EntityChipVm> chip)
    {
        var members = ordered
            .Where(m => m.Reason.HasFlag(reason))
            .Select(m => chip(m.Ability))
            .ToList();
        if (members.Count == 0) return;
        sections.Add(new ProvenancePopupSection(label, members));
    }

    private static string DisplayNameOf(PocoEffect effect)
    {
        if (!string.IsNullOrEmpty(effect.Name)) return effect.Name!;
        if (!string.IsNullOrEmpty(effect.InternalName)) return effect.InternalName!;
        return "this effect";
    }

    private static IReadOnlyList<EffectProcsFromAbilityKeywordRow> BuildProcsFromAbilityKeywordRows(
        IReadOnlyList<string>? abilityKeywords)
    {
        if (abilityKeywords is null || abilityKeywords.Count == 0) return [];
        var list = new List<EffectProcsFromAbilityKeywordRow>(abilityKeywords.Count);
        foreach (var tag in abilityKeywords)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            list.Add(new EffectProcsFromAbilityKeywordRow(tag));
        }
        return list;
    }
}

/// <summary>
/// One row in <see cref="EffectDetailViewModel.ConditionalDotRows"/>. Plain projected text
/// (predicate rules don't deep-link to entity rows). <paramref name="GatingAbilityKeywords"/>
/// is surfaced for tooltip / future filtering use.
/// </summary>
public sealed record EffectConditionalDotRow(string Display, IReadOnlyList<string> GatingAbilityKeywords);

/// <summary>One row in <see cref="EffectDetailViewModel.ConditionalSpecialValueRows"/>.</summary>
public sealed record EffectConditionalSpecialValueRow(string Display, IReadOnlyList<string> GatingAbilityKeywords);

/// <summary>
/// One row in <see cref="EffectDetailViewModel.ProcsFromAbilityKeywordRows"/>. The
/// <paramref name="Tag"/> is rendered as plain text — this is the reverse direction
/// ("abilities whose keyword triggers this effect") and has no navigable surface yet;
/// navigation is deferred to a follow-up.
/// </summary>
public sealed record EffectProcsFromAbilityKeywordRow(string Tag);

/// <summary>
/// A Set-reference carrier (the pilot <c>RecipeKeywordSlotVm</c> idiom): the
/// <see cref="SetRefVm"/> the shared <c>SetRef</c> binds, plus the per-chip
/// <see cref="Activate"/> command that bridges <c>SetRef.ActivateCommand</c>
/// (which passes the <see cref="SetRefVm"/>) to the host's
/// <c>OpenEntityCommand(reference)</c>. <see cref="Activate"/> is null for an
/// unwired tag (availability corollary — the chip still renders on the blue
/// chassis; the click is a safe no-op).
/// </summary>
public sealed class EffectFilterSetRefVm
{
    public EffectFilterSetRefVm(SetRefVm setRef, System.Windows.Input.ICommand? activate)
    {
        SetRef = setRef;
        Activate = activate;
    }

    /// <summary>The data carrier the shared <c>SetRef</c> control binds.</summary>
    public SetRefVm SetRef { get; }

    /// <summary>
    /// Parameterless reveal/filter command wired to <c>SetRef.ActivateCommand</c>
    /// (it ignores the passed <see cref="SetRefVm"/> and invokes the host
    /// <c>OpenEntityCommand</c> with the captured reference). Null when unwired.
    /// </summary>
    public System.Windows.Input.ICommand? Activate { get; }
}
