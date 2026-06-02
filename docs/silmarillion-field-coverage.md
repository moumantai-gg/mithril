# Silmarillion · detail-view field coverage

> **Companion to [roadmaps/silmarillion.md](roadmaps/silmarillion.md).** The roadmap
> answers *which entities get a tab* (the Bucket A/B/C/D rule). This doc answers a
> different question: *within a shipped detail view, which POCO properties reach the
> user, which are deliberately omitted, and which are genuine gaps.*

## Why this is its own axis

Every CDN source has a faithful `Mithril.Reference` POCO (parser coverage is total —
see [roadmaps/mithril-reference.md](roadmaps/mithril-reference.md)). A detail view is a
*curated projection* of that POCO, not a property dump: the right design surfaces the
player-relevant mechanics and drops engine noise. So "property X is not bound" is not
automatically a defect — it is only a defect if X is player-relevant.

The purpose of this doc is to make that judgement **once, explicitly**, so the
deliberate omissions don't get re-flagged as gaps every time someone eyeballs a POCO
next to its view (the same phantom-gap problem ai.json / itemuses.json had at the
reference-library layer).

## Omission taxonomy

Unsurfaced properties fall into one of these classes. The first four are
**deliberate-by-design** and should stay omitted; the last is the only one worth
filing issues against.

| Class | Examples | Verdict |
|---|---|---|
| **VFX / appearance** | `Particle`, `LoopParticle`, `SelfParticle`, `TargetParticle`, `*Appearance*` | Omit — irrelevant to a reference browser |
| **Animation timing** | `UsageAnimation`, `UsageAnimationEnd`, `UsageDelay`, `*Delay*` | Omit — engine playback detail |
| **Engine / lifecycle flags** | `IsClientLocal`, `DeleteFromHistoryIfVersionChanged`, `AttuneOnPickup`, internal IDs (`ID`, envelope `Key`) | Omit — not player-facing semantics |
| **Raw keyword/tag lists** | `Keywords` on most entities | Usually omit — internal predicate tokens, not human-readable; surface only when a slot/filter consumes them |
| **Player-relevant mechanic, unsurfaced** | recipe gating, costs, cooldowns, prerequisite chains; quest narrative prose | **Candidate gap — file an issue** |

A property is a *candidate gap* only if it changes what a player would do/expect and
we already render the same class of data on another entity. Consistency across tabs is
the test: if Quest and StorageVault render a polymorphic `Requirements` block but
Recipe drops its `OtherRequirements`, that asymmetry is a gap, not a design choice.

---

## Recipe — VERIFIED 2026-05-16

The one entity audited against source directly
([Recipe.cs](../src/Mithril.Reference/Models/Recipes/Recipe.cs),
[RecipeDetailViewModel.cs](../src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs),
[RecipeDetailView.xaml](../src/Silmarillion.Module/Views/RecipeDetailView.xaml)).

**Surfaced:** `IconId`, `Name`→`DisplayName`, `InternalName` (footer), `Description`,
`Skill`+`SkillLevelReq` (chip, skill resolved to display name), `MaxUses` (chip),
`Ingredients` (item chips), keyword-slot ingredients (provenance popup, #318),
`ResultItems`/`ProtoResultItems`→"Produces", `ResultEffects` (plain-text stub, #214),
recipe sources ("Taught by", from `sources_recipes.json`), `OtherRequirements`
(one "Requirements" list of dual-shape rows — prose, or "{prefix} [inline chip]"
for `RecipeKnown`/cross-recipe-`RecipeUsed` — in authored order, the Quest
dual-shape idiom so cross-links read in the prose flow not as an orphaned pill
cluster; via `RecipeRequirementProjector`; `PetTypeTag` resolved through
`strings_all["npc_<tag>_Name"]` per the id→display-name convention — pets are
NPC/monster entities, "SummonedBakingBread" → "Rising Dough", not camel-split;
#342), `Costs` ("Cost" lines; #342),
`ResetTimeInSeconds` (cooldown chip beside `MaxUses`) + `SharesResetTimerWith`
(navigable recipe→recipe cross-link chip — every corpus value 19/19 is a real
recipe `InternalName` — labelled "Shares cooldown with", not prose; #342).

**Deliberate omissions** (taxonomy classes 1–4): `Key`, `UsageAnimation`,
`UsageAnimationEnd`, `UsageDelay`, `UsageDelayMessage`, `ActionLabel`, `Particle`,
`LoopParticle`, `SortSkill`, `DyeColor`, all `ItemMenu*`, `Keywords`,
`ValidationIngredientKeywords`, `RewardSkillXpDropOff*`, `RewardAllowBonusXp`,
`NumResultItems`, `RequiredAttributeNonZero`, `ResultEffectsThatCanFail`.

**Candidate gaps — player-relevant, unsurfaced (class 5).** Prevalence measured
against the bundled `recipes.json` (v470, 4427 entries) on 2026-05-16 — the gap is
*not* uniform, so it is not one issue:

| Property | Recipes carrying it | Why it matters | Precedent |
|---|---|---|---|
| **`PrereqRecipe`** | **2004 (~45%)** | Prerequisite-recipe chain — a primary crafting-progression axis | Navigable cross-link shape already exists (`EntityRef` → same Recipes tab); identical to how `Ingredients`/`Produces` chips work |

`OtherRequirements` (90, ~2%), `ResetTimeInSeconds` (51, ~1.2%),
`SharesResetTimerWith` (21, ~0.5%), and `Costs` (55, ~1.2%) **were** in this table
and are **now surfaced** (see "Surfaced" above) — resolved 2026-05-16, #342.

**Remaining gap — `PrereqRecipe`, broad, structural, high-value.** ~45% of all
recipes have a prerequisite the browser shows nowhere. It is a *navigable
cross-link* (recipe → prerequisite recipe), precisely what Silmarillion's chip
model is built for — the same shape as the shipped ingredient/produces chips, just
an unwired edge. This is the priority and stands alone.

> **Why the trio was resolved ahead of its "long-tail" priority.** It wasn't just
> Quest/StorageVault parity. These exact fields are the ones `CrossSkillPlanner`
> *deliberately punts on* (see
> [planner-recipe-field-consumption.md](planner-recipe-field-consumption.md)). The
> planner's punt is justified by a "user-asserted" contract — the user is assumed
> to know the gate exists. If the browser also hides it, that knowledge has no
> source: a silent trap, the `MaxUses`-bug shape one layer up. Surfacing them here
> is the *load-bearing complement* to the planner punt, not cosmetic completeness.

**Tracked in:** #341 (`PrereqRecipe` cross-link — priority, open) · #342
(`OtherRequirements` + `Costs` + reset-timer — **resolved 2026-05-16**).

---

## Other shipped detail views — AUDIT BASELINE (unverified) 2026-05-16

> **Verification owed.** The rows below come from an automated field-coverage audit,
> not a line-by-line source read. Treat as a baseline, not ground truth: when a tab is
> next touched, spot-verify its row and promote it to a VERIFIED section like Recipe's.
> Coverage is described qualitatively on purpose — the audit's percentages were
> estimates and are deliberately not reproduced here as fact.

| Entity | Coverage shape | Deliberate omissions (taxonomy 1–4) | Candidate gaps (class 5) |
|---|---|---|---|
| **Npc** | Comprehensive — slim POCO, fully surfaced | `AreaFriendlyName` (resolution fallback only) | None apparent |
| **Area** | Comprehensive | `ShortFriendlyName` filtered when == `FriendlyName` | None apparent |
| **Effect** | Near-complete | `Particle` (VFX), `SpewText` (combat-log string) | None apparent |
| **Lorebook** | Near-complete | `IsClientLocal`, `Visibility`, `Keywords`, `InternalName` | None apparent |
| **PlayerTitle** | Near-complete | `Keywords` | None apparent |
| **StorageVault** | Near-complete | `ID`, `Grouping` label, `SlotAttribute` | None apparent |
| **Ability** | Moderate — large POCO, core mechanics surfaced | Many VFX/animation/flag fields, attribute-delta lists, `SpecialInfo` | None confirmed — large flag tail is mostly class-3 noise |
| **Quest** | Moderate — typed objectives/requirements/rewards surfaced | Engine flags, `Reward_SkillLevels` dict | `PrefaceText` / `SuccessText` / `MidwayText` narrative prose — debatable; some players want lore text |

### Modeled but no detail view (by design — not gaps)

- **Item** — browsable in the Items master list; dedicated detail pane deferred per
  the roadmap ("cheapest standalone win once core entity tabs are in"). Cross-link
  infrastructure exists; the right-pane view does not. Tracked on the Roadmap Project.
- **Skill** — intentionally folded into Recipe/Ability tabs as chip/metadata
  (~30 skills; a dedicated tab adds little). No standalone tab planned.
- **Landmark** — non-standalone by design: renders inside Area detail as grouped
  provenance rows. Not a defect.

---

## Acting on this doc

- **`PrereqRecipe` cross-link** (#341) — the priority. ~45% of recipes affected; a
  navigable edge in Silmarillion's existing chip model.
- **Recipe-detail completeness pass** (#342) — **done 2026-05-16.**
  `OtherRequirements` + `Costs` + reset-timer now render. This was the
  load-bearing complement to the `CrossSkillPlanner` punt, not just
  Quest/StorageVault parity — keep it in lockstep: a new planner-punted
  `RecipeRequirement` arm must also get a `RecipeRequirementProjector` arm.
- Quest narrative prose is a *judgement call*, not a clear gap — decide before filing.
- Everything else unsurfaced is deliberate; do not file "increase coverage" issues
  against it. If a future audit re-flags class 1–4 properties, point it here.

### Visual grammar (#404) — RESOLVED (all detail panes, incl. the shared item-detail #424)

- **No fact / control / link visual grammar** (#404) — **RESOLVED 2026-05-17
  for the nine Silmarillion *tab* detail views; the shared cross-module
  item-detail pane followed via #424 (2026-05-17).**
  The original debt: the shared `EntityChip` was visually identical to the
  header stat badges (`Skill N`, `MaxUses`, cooldown) and broke prose in
  `{prefix} [chip]` rows; root cause was the *absence of a grammar*
  distinguishing passive facts from controls from navigable links (a
  *coverage-complete, presentation-wrong* state — #342's fields were all
  surfaced; the grammar was the debt).

  Closed by the #404 program: the ratified five-tier grammar
  (Fact · Control · Link · Set-reference · Structure) is encoded as shared WPF
  primitives (`Link` / `SetRef` / `FactTable` / `FactFooter` + the Structure
  styles — Phase 4) and **every Silmarillion *tab* detail view** is migrated to
  them (Phase 5: the Recipe pilot + the eight-view fan-out — PlayerTitle,
  Lorebook, Area, Effect, Npc, StorageVault, Ability, Quest). The link tier is the V2
  form (small lead-icon + gold name, no box); stat badges read inert via
  `FactTable`; keyword/stacking chips are Set-reference (ratified E4); the
  footer is the G-a/E5 `FactFooter`. The full grammar +
  amendments + decision log: [`docs/silmarillion-visual-grammar.md`](silmarillion-visual-grammar.md).

  A Phase-6 conformance guardrail
  (`DetailViewGrammarConformanceTests`) fails the build if any
  `src/Silmarillion.Module/Views/*DetailView.xaml` **or the shared
  `src/Mithril.Shared.Wpf/ItemDetailView.xaml`** re-introduces a legacy
  entity-reference chip (`EntityChip`/`ItemSourceChip`) instead of the shared
  primitive, so neither the Silmarillion tab detail views nor the cross-module
  item-detail pane can silently regress. Do not re-flag *those* detail-pane
  chips as a coverage gap; that surface is closed.

- **Shared cross-module item-detail pane — RESOLVED (#424, 2026-05-17).** The
  shared `Mithril.Shared.Wpf/ItemDetailView` / `ItemDetailWindow` (Item has no
  Silmarillion *tab* detail by design — see "Modeled but no detail view"
  above; its detail is the cross-module pane used by `ItemDetailWindow`
  popups, Bilbo, Celebrimbor, and cross-link "open in window") was
  deliberately **out of #404 Phase-5 scope** (Phase-5 anti-goal #3 forbade
  editing shared `Mithril.Shared.Wpf` primitives during the fan-out). It was
  migrated by its **own gated follow-up #424**: a mini-Phase-1 classification
  then a consistency-diff against the merged pilot + `EffectDetailView` —
  EquipSlot + skill-req pills folded into one inert `FactTable` strip;
  Sources / Produced by / Awarded by / Bestows lorebook / Used in / Used as
  rendered through `Link`; the two "View all N →" drawers as summary-form
  `SetRef`; the InternalName footer as the copyable-`KEY` `FactFooter`; the
  per-`*Preview` sections as inert Fact body. The grammar break at the
  Silmarillion→item-window navigation boundary is closed, and the Phase-6
  guardrail now covers this pane too (above). The VM change was additive — the
  legacy chip/string members the `ItemsTabViewModel` tests assert are
  retained. Do not re-flag this pane as a remaining grammar surface; it is
  closed.

### Declared-vs-reverse source duplication (#407) — RESOLVED (policy ratified 2026-05-17)

This is the **coverage axis's** own debt, the deliberate sibling of the #404
*presentation* axis above — the two were fenced apart on purpose. It governs
*whether a source row appears at all*, never how it is styled.

**The class.** A pane can surface the same entity twice under two headers when
one path reads declared `sources_*.json` and the other is the reverse-lookup of
the inverse relationship. A fresh cross-pane audit (production indices, v470 —
[#407](https://github.com/moumantai-gg/mithril/issues/407#issuecomment-4470910146))
found the class is **confined to the shared `Mithril.Shared.Wpf/ItemDetailView`
pane**, exactly two pairs:

| Declared "Sources" kind | Reverse twin header | Duplicate edges (v470) | Declared-only residue (no reverse twin) |
|---|---|--:|--:|
| `Recipe` → `EntityRef.Recipe` | **Produced by** (`RecipesByProducedItem`) | 4076 / 2702 items | **59 / 34 items** |
| `Quest` → `EntityRef.Quest` | **Awarded by** (`QuestsRewardingItem`) | 816 / 306 items | **45 / 45 items** |

RecipeDetail's "Taught by" (`sources_recipes.json`) and AbilityDetail's
NPC-trainer sources have **no in-pane reverse twin** — the class is genuinely
`ItemDetailView`-only (re-derived, not inherited from the prior
`ItemDetailView`-scoped check). Overlap is near-total but **partial**: the two
sides come from different JSON, so a real declared-only residue exists and is a
genuine sources/relational data-coverage signal.

**Ratified policy (maintainer sign-off — @arthur-conde, 2026-05-17;
[#407](https://github.com/moumantai-gg/mithril/issues/407#issuecomment-4471012842)):**

1. **Suppress declared, keep reverse — per (item, entity) edge.** A declared
   `ItemSource` row is dropped from "Sources" iff its resolved `Context` entity
   is already shown for that item under its dedicated reverse header
   (`Recipe`↔"Produced by", `Quest`↔"Awarded by"). The reverse header is the
   single role-appropriate home. The test is per-edge, never per-kind.
2. **Declared-only residue survives + carries an in-pane asymmetry warning.**
   Residue rows (no reverse twin) are **never silently dropped**; each carries
   an in-pane note that the declared↔reverse relationship is asymmetrical (the
   declared source is uncorroborated by the recipe/quest reverse data). This is
   a **verification-owed coverage signal** — see "Verification owed" below.
3. **Kind-prefix dropped for the entity-resolving kinds.** The leading
   `Quest:`/`Recipe:` text prefix is removed from residue rows; entity kind is
   carried by the established kind→lead-glyph standard (`LinkVm.GlyphFor`) the
   migrated Link grammar already encodes, and the asymmetry-warning text names
   the kind for `Quest` (glyph-less by grammar design). The NPC-*mechanic*
   prefixes (`Vendor:`/`Barter:`/`NpcGift:`/`HangOut:`) are **kept** — they
   encode acquisition method, which the NPC kind-glyph cannot.

**Implementation fence (load-bearing).** Remediation is **projection-layer
only** (`ItemsTabViewModel.BuildSourceChips`/`BuildCrossLinkContext`/
`ResolveSourceReference`/`FormatSourceDisplayName`). The asymmetry warning
ships through the **existing `LinkVm.ProvenanceSuffix` slot the migrated
grammar already renders** — no `*DetailView.xaml`, no Phase-4 primitive, no
`Resources.xaml` edit (the #404/#424 presentation axis stays frozen). A louder
warning treatment (colour/badge) would touch the frozen Link primitive and is a
**separate presentation-axis issue**, deliberately out of scope here.

> **Superseded (presentation only) by G-d (#431).** Claude Design's review of
> this shipped stopgap confirmed the provenance-slot-overload concern flagged
> on #429. The ratified **G-d Link reference-state axis**
> ([silmarillion-visual-grammar.md](silmarillion-visual-grammar.md) · G-d;
> #431) replaces the `ProvenanceSuffix` caveat with an additive
> `IsUnconfirmed` flag → dashed gold underline + one-word `· unconfirmed` tail
> + caveat `ToolTip`. **Only the presentation of the residue caveat changes**;
> the #407 *coverage policy* (suppress-declared-keep-reverse per edge,
> declared-only residue survives) is unchanged and remains correct. #429 was
> left untouched; G-d ships as its own PR.

The
additive cross-module contract (`Sources`/`ProducedByRecipes`/`AwardedByQuests`
/`Consumed*` — asserted by `ItemsTabViewModelTests`, consumed by Bilbo /
Celebrimbor / `ItemDetailWindow`) is preserved; the only intended behavioural
change is `Sources` shrinking by the suppressed dupes and residue rows gaining
the provenance-suffix warning, with the asserting tests updated deliberately
and the delta called out in the PR. A coverage-policy regression guard
(spirit-analogue of the Phase-6 `DetailViewGrammarConformanceTests`) asserts no
entity appears under both "Sources" and its reverse header for the same item.

> **Verification owed.** The declared-only residue (59 Recipe / 45 Quest edges
> as of v470) is a *real sources/relational data-coverage asymmetry*, not just a
> dedupe leftover: `sources_items.json` declares a recipe/quest the inverse
> `recipes.json`/`quests.json` data does not corroborate. Surfaced in-pane (the
> asymmetry warning) and tracked here; a future data-side reconciliation pass
> against the CDN is the task side. When this residue's scale changes materially
> on a CDN bump, re-audit and update the table above.

**Out of scope (filed separately — not folded in).**
`QuestObjectiveMacGuffin` (8/8) resolves `Context`→quest InternalName but is
not matched by `ResolveSourceReference`, rendering as a bare
`QuestObjectiveMacGuffin` text row that drops the resolved quest name. A latent
display defect, **not** a dedupe target (macguffin = the quest *consumes* the
item — the consume role, no reverse twin). Its own issue.

## History

- **2026-05-17** — #407 ratified: declared-vs-reverse source-duplication
  policy decided (suppress declared per-edge, keep reverse; declared-only
  residue survives with an in-pane asymmetry warning; entity-kind prefix
  dropped in favour of the kind→glyph standard). Cross-pane audit (production
  indices, v470) confirmed the class is `ItemDetailView`-only and quantified
  the partial overlap (4076 Recipe + 816 Quest duplicate edges; 59 + 45
  declared-only residue). Coverage axis, fenced from the #404/#424
  presentation axis; remediation is projection-layer only.
- **2026-05-16** — #342 resolved: `OtherRequirements` (typed lines + recipe
  cross-link chips, `RecipeRequirementProjector`), `Costs`, and reset-timer
  surfaced in the recipe detail. Reframed from "long-tail completeness" to the
  *load-bearing complement* to the `CrossSkillPlanner` deliberate punt — the
  display axis and the planner-consumption axis are now coupled by an explicit
  lockstep rule in both this doc and
  [planner-recipe-field-consumption.md](planner-recipe-field-consumption.md).
- **2026-05-16** — Recipe candidate gaps quantified against bundled `recipes.json`
  (v470). Reframed from one combined issue to two: `PrereqRecipe` (~45%, broad,
  cross-linkable — priority) vs. a long-tail completeness trio (1–2% each). Grounding
  measure, not inference. Filed as #341 (priority) and #342 (completeness).
- **2026-05-16** — Doc created. Recipe verified against source; remaining eight
  shipped detail views recorded as an unverified audit baseline. Field-coverage
  established as an axis distinct from the roadmap's tab-bucketing rule.
