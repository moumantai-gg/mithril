using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Data carrier for the shared provenance popup (<see cref="ProvenancePopupWindow"/>).
/// Renders a 1:N relationship as <em>membership and provenance</em>: an ordered list of
/// reason-labeled sections, each a list of navigable <see cref="EntityChipVm"/> rows.
/// <para>
/// This is the terminal shape of the #318 invariant: the set is materialized exactly once
/// in the source index (which retains <em>why</em> each member qualified); the popup is a
/// view over that object. There is no second derivation — the popup never re-runs a query
/// to populate itself, so the silent-divergence bug class it dissolves cannot recur. See
/// the #318 invariant.
/// </para>
/// <para>
/// <b>Single-reason collapse.</b> A provenance section with one trivial reason is noise
/// (the doc's "Discipline" rule). When the input has exactly one section, the popup hides
/// the section header and renders a flat chip list — provenance is shown only when it aids
/// understanding (a member is required vs. merely enabled-by vs. targeted).
/// </para>
/// <para>
/// <b>"To Query" affordance.</b> <see cref="ToQueryCommand"/> is the orchestrator's
/// reserved per-section projection hook (a labeled, explicitly-lossy push into the
/// destination tab's query box). The API surface is final so later slices reuse it
/// unchanged; the effect&#8594;abilities migration in this PR ships with it unset (no
/// button rendered) — the popup-from-index <em>is</em> the correct surface, the To-Query
/// projection logic is a deliberate fast-follow. It is never the source of a displayed
/// count or membership claim.
/// </para>
/// </summary>
public sealed class ProvenancePopupViewModel
{
    public ProvenancePopupViewModel(
        string title,
        IReadOnlyList<ProvenancePopupSection> sections,
        ICommand? toQueryCommand = null)
    {
        Title = title;
        Sections = sections;
        ToQueryCommand = toQueryCommand;

        // Distinct-member count: sections may share members (a multi-reason member appears
        // in more than one section). The headline count must equal the "View all N" the
        // user clicked, so it counts each distinct EntityChipVm.Reference once — never the
        // sum of section sizes.
        TotalCount = sections
            .SelectMany(s => s.Chips)
            .Select(c => c.Reference)
            .Distinct()
            .Count();

        // Single-reason collapse: one section => flat list, no section chrome.
        IsFlat = sections.Count <= 1;
        FlatChips = IsFlat && sections.Count == 1
            ? sections[0].Chips
            : Array.Empty<EntityChipVm>();
    }

    /// <summary>Popup window title (e.g. the effect's display name).</summary>
    public string Title { get; }

    /// <summary>The reason-labeled sections, in caller-supplied order.</summary>
    public IReadOnlyList<ProvenancePopupSection> Sections { get; }

    /// <summary>
    /// Distinct member count across all sections — equals the "View all N" the user
    /// clicked. A member qualifying for several reasons is counted once.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// True when there's a single section: the popup renders <see cref="FlatChips"/> with
    /// no per-section header (a single trivial reason is noise per the Discipline rule).
    /// </summary>
    public bool IsFlat { get; }

    /// <summary>The flat chip list when <see cref="IsFlat"/>; empty otherwise.</summary>
    public IReadOnlyList<EntityChipVm> FlatChips { get; }

    /// <summary>
    /// Optional per-section "To Query" projection command. Receives the
    /// <see cref="ProvenancePopupSection.QueryProjection"/> token of the section whose
    /// button was clicked. Null => no To-Query button rendered (the state this PR ships
    /// for effect&#8594;abilities — see the class remarks).
    /// </summary>
    public ICommand? ToQueryCommand { get; }

    /// <summary>True when both a command and at least one section projection token exist.</summary>
    public bool HasToQuery =>
        ToQueryCommand is not null && Sections.Any(s => s.QueryProjection is not null);
}

/// <summary>
/// One reason-labeled section of a <see cref="ProvenancePopupViewModel"/>: a human label
/// ("Requires", "Enabled by", "Targets"), the navigable chips that qualified for that
/// reason, and an optional opaque projection token consumed by
/// <see cref="ProvenancePopupViewModel.ToQueryCommand"/> if the host wires one.
/// </summary>
public sealed record ProvenancePopupSection(
    string Label,
    IReadOnlyList<EntityChipVm> Chips,
    string? QueryProjection = null);
