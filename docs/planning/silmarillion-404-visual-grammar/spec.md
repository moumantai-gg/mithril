# Silmarillion #404 — fact / control / link visual grammar: audit → standard → migrate

**Tracked in:** #404

> **For the owning agent.** This is a *program* plan, not a single-PR task. You
> own it end to end across multiple PRs and several human/design decision gates.
> Your job between gates is to produce the artifact that *unblocks* the gate, then
> stop and present — not to push past a gate on your own judgement. The two
> guardrails in "Anti-goals" are load-bearing: violating either reproduces the
> exact debt this issue exists to pay down.

## Context

A design critique ([chip-critique](https://api.anthropic.com/v1/design/h/pLUeHE3xm41GCSG3irbBcQ?open_file=explorations%2Fchip-critique.html))
found the shared `EntityChip` ([Mithril.Shared.Wpf](../../../src/Mithril.Shared.Wpf))
renders navigable references with the same bordered/surface-filled box as the
header **stat badges** (`Skill N`, `MaxUses`, cooldown), and breaks prose in
`{prefix} [chip]` rows. Root cause is the **absence of a visual grammar**
separating three (probably four) semantically different things; the chip
collision is one symptom. The debt accumulated because every detail surface made
a locally-reasonable rendering choice with no system to conform to (six chip
corrections in [#400](https://github.com/moumantai-gg/mithril/pull/400) alone).

This plan is the **presentation axis only**. The *coverage* axis ("what data is
shown / omitted / why") is a separate, already-tracked concern owned by
[docs/silmarillion-field-coverage.md](../../silmarillion-field-coverage.md) — Recipe
VERIFIED, the other eight an audit baseline. Do not re-open it.

## Ownership model & decision gates

Agent owns: every audit, classification, inventory, migration PR, and the
guardrail. Agent does **not** own: the per-tier *visual* design (pigment,
glyph, spacing, hover) — that is the designer's call, informed by your artifacts.

| Gate | Blocks on | Agent deliverable that unblocks it |
|---|---|---|
| **G1** tier set ratified | design + maintainer | Phase 0 strawman tier definitions |
| **G2** grammar covers reality | design + maintainer | Phase 1+2 treatment×tier matrix |
| **G3** target treatments chosen | **designer** | Phase 3 gap list + harness-ready specimens |
| **G4** each migration PR | maintainer review | Phase 5 per-area PR, consistency-checked |

At G1–G3 you produce the artifact and **present + stop**. Do not start the next
phase until the gate clears (record the clearance in #404).

## Phase 0 — Strawman the tiers (top-down, before any audit)

Derive the tier set from *semantics*, not from current code. Starting strawman
(refine, don't discard without cause):

- **Fact** — passive data the user reads. Stat badges, counts, mono footers.
- **Control** — user actuates it. Buttons, inputs, the query box.
- **Link** — navigable reference to another entity. `EntityChip` today.
- **Set-affordance** — "go see a set of N", not one entity. The recipe
  keyword-slot "view all N →" popup; `ItemSourceChip`'s navigable↔plain degrade.
  This tier is the most likely to be missing/contested — treat it as a first-
  class hypothesis to validate in Phase 1, not an afterthought.

Deliverable: a short tier-definition section appended to #404 (definition +
1-line "you know it's this tier when…" test each). **Gate G1.**

## Phase 1 — Pilot on Recipe (depth, not breadth)

Recipe detail is already coverage-VERIFIED and is the freshest surface. Classify
**every rendered element** in [RecipeDetailView.xaml](../../../src/Silmarillion.Module/Views/RecipeDetailView.xaml)
+ [RecipeDetailViewModel.cs](../../../src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs)
(title, icon, stat badges, flavor, requirement rows incl. the new
`RecipeRequirementRow` dual-shape, shared-cooldown row, cost lines, sources,
ingredient/produced/keyword-slot chips, effects stub, footer) into:

`element → intended tier → current treatment → delta`

Output: a `treatment × tier` matrix for Recipe. The off-diagonal cells are the
Recipe migration backlog; "fits no tier" cells are grammar gaps. Expect the
set-affordance tier and the prose-vs-list link question to surface here.

## Phase 2 — Sweep the other eight (breadth, validate not discover)

Detail views: `Glob src/Silmarillion.Module/Views/*DetailView.xaml`
(Quest, StorageVault, Npc, Area, Effect, Lorebook, PlayerTitle, Ability — Item
has no detail pane by design, confirm against the coverage doc). For each, you
are **not** re-discovering from scratch — you are checking "does the
Recipe-derived tier set + treatment inventory hold here; what new edge cases
appear." Extend the single shared matrix; do not write nine independent reports.

Deliverable: one consolidated `treatment × tier` matrix across all views +
an enumerated list of (a) distinct current treatments, (b) grammar-gap cells.
**Gate G2** — present the matrix; confirm the tier set survives contact with all
nine views before anything is standardized.

## Phase 3 — Resolve grammar gaps, then ratify targets

For every "fits no tier" cell: either extend the grammar (new tier / sub-rule)
or argue it into an existing tier — explicitly, in writing. **No pixel target is
chosen until every gap is closed**, or migration will improvise into the gaps
(the original failure mode).

Then, in the design-system preview harness (the bundle the critique came
from *is* that harness — `mithril-design-system/.../preview/`), produce
specimen artboards for each tier so the designer can choose the visual target.
Carry the engineering constraints into the specimens, do not re-decide them:

- Link tier: **V2** (small lead-icon + gold name, no box) is the agreed
  engineering position. **V5 (prose/list dual-form) is rejected** — its
  call-site render-context flag is the optional-context footgun this codebase
  is repeatedly bitten by (compiles green, ships wrong on the next surface;
  permanent reviewer ambiguity). Present V2; only re-open V5 if the designer
  raises it, and if so attach this rejection rationale.
- Fact tier: must read **inert** (borderless label:value or mono) so it cannot
  be confused with link/control. This is half the grammar, not "orthogonal."
- Avoid V1 (no icon → loses NPC/item/recipe type recognition) and V3 (dotted
  underline = Windows tooltip-trigger mis-affordance).

Deliverable: closed gap list + harness specimens. **Gate G3** — designer picks
the per-tier visual target. Record the chosen targets in #404 and as a "visual
grammar" section in the design system / a `docs/` reference.

## Phase 4 — Encode the standard as a shared primitive

Land the chosen treatments as **shared, named WPF styles/controls** in
`Mithril.Shared.Wpf` (one per tier), not per-view copies. The link tier replaces
`EntityChip`'s internals; the fact tier replaces the ad-hoc per-view stat-badge
borders. Call sites express *which tier*, never raw pixels — that is what makes
the grammar enforceable instead of advisory. No behavior change, no view
migrated yet; this PR is the primitive + its tests + harness parity.

## Phase 5 — Migrate, one area per PR, consistency-gated

Recipe first (pilot → proves the primitive on the surface you know best), then
one PR per remaining detail view. Each PR's **acceptance bar is cross-tab
consistency**, verified explicitly against the matrix — *not* per-tab judgement.
Per-tab judgement is precisely how the debt accumulated; the bar is the point.
Keep `dotnet test tests/Silmarillion.Tests` green each PR; spot-verify the live
view per the project's "tests green ≠ shipped" memory.

## Phase 6 — Guardrail so it can't re-rot

Add a cheap conformance check: a test (or analyzer) that fails when a detail
view introduces a raw bordered/box style for an entity reference instead of the
shared link primitive — i.e., the next surface *cannot* improvise. Update
[docs/silmarillion-field-coverage.md](../../silmarillion-field-coverage.md)'s
visual-debt note to "resolved" with a pointer to the grammar reference.

## Anti-goals (violating these reproduces the debt)

1. **Do not re-audit "what is shown."** Presentation only. Coverage is settled
   and separately tracked; re-flagging it re-opens decided questions.
2. **Do not let the audit define the standard.** Semantics define the tiers
   (top-down, Phase 0/G1); the audit *validates coverage of* the tiers and
   *scopes the migration*. "Catalog current treatments → average them into a
   standard" anchors the target to the mess and is explicitly forbidden.
3. **Do not migrate before all grammar gaps are closed** (Phase 3). A migration
   into an undefined cell is a new improvisation.
4. **Do not make the visual call.** That is the designer's at G3. You supply
   classification, constraints, and specimens — decision support, not the
   decision.

## Acceptance (program-level)

Every entity reference and every stat fact across all nine detail views renders
through a shared tier primitive; fact/control/link/set-affordance are mutually
unmistakable; the conformance guardrail is in place; #404's visual-debt note is
closed. The proof is not "it looks better" — it is "no surface can render an
entity reference as a box again without the build telling it no."

## Starting coordinates

- Detail views: `src/Silmarillion.Module/Views/*DetailView.xaml` (+ matching
  `*DetailViewModel.cs`).
- Shared chip: `EntityChip` in `src/Mithril.Shared.Wpf` (consumed by every tab).
- Stat badges: inline `Border`s in each `*DetailView.xaml` header (grep
  `NullOrEmptyToVis` / `stat`-style chips; Recipe's are the
  `SkillRequirementChip`/`MaxUsesChip`/`CooldownChip` `Border`s).
- Coverage axis (read, do not modify in audit phases):
  [docs/silmarillion-field-coverage.md](../../silmarillion-field-coverage.md).
- Critique + harness: the Mithril design-system bundle the critique link serves.
