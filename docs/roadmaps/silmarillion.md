# Silmarillion · Reference Browser — roadmap

> **Active backlog:** [Mithril Roadmap Project — `Module: Silmarillion`](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Silmarillion%22).
>
> **Sibling doc:** [silmarillion-field-coverage.md](silmarillion-field-coverage.md) — *within* a shipped tab, which POCO fields render vs. are deliberately omitted vs. are gaps. This doc is the *which-entities-get-a-tab* axis; that one is the *which-fields-within-a-tab* axis.

What shipped in v1 and the rationale for which CDN sources will (and won't) become future tabs. Per-item *task tracking* lives in Issues; this doc keeps the design narrative — *why* each piece was scoped this way, and the bucketing rule that decides what becomes a tab.

## Context

Project Gorgon's CDN serves **29 reference-data files** (pg-data MCP, v470). Every file has a faithful POCO parser as of `Mithril.Reference` Phase 0–6 ([mithril-reference.md](mithril-reference.md)), so the constraint for the browser module is no longer *can we parse it* — it's *is it worth browsing*.

Silmarillion v1 (PR #236, merged 2026-05-13) shipped the two highest-payoff browse surfaces: **Items** and **Recipes**. The architecture is built to fan out:

- [`EntityRef`](../src/Mithril.Shared/Reference/EntityRef.cs) enumerates 11 entity kinds — `Item, Recipe, Ability, Effect, Npc, Quest, Lorebook, Landmark, Area, PlayerTitle, StorageVault` — plus one synthetic deep-link variant (`RecipeIngredientKeyword`, added by #259) used to route keyword chips into a pre-filtered Recipes tab; the synthetic kind isn't a CDN source and isn't part of the Bucket A/B/C/D classification below. Cross-link chips for kinds without a registered `IReferenceKindTarget` render as plain text and silently become navigable the moment that target ships (#239 retired the old `V1TabbedKinds` HashSet).
- The master-detail pattern (`MithrilQueryBox` → virtualised list → detail pane with cross-link chips) is duplicable per tab — adding a tab is `TabItem + View + ViewModel + EntityKind handling` plus registering an `IReferenceKindTarget` (the old `V1TabbedKinds` gate was retired by #239).

This doc is the rationale for which of the CDN sources warrant that duplication. **As of 2026-05-15 every Bucket B tab has shipped** — the sections below are now the *as-built record + design rationale*, not a deferral plan. The bucketing rule itself still governs any future source.

## What shipped in v1

- **Items tab** — master-detail. Right-pane shows produced-by recipes, consumed-by recipes, and sources (NPCs / quests / monsters / etc.). Cross-link chips degrade to plain text for non-tabbed kinds.
- **Recipes tab** — master-detail. Right-pane shows ingredient chips, result chips, and skill / level metadata. Skill internal names resolve to display names via `IReferenceDataService.Skills`. An "open in window" command supports side-by-side comparison.
- **Reference navigator** — `IReferenceNavigator` + `EntityRef` decouple "open this entity" from view implementation. Back-stack history is unbounded.
- **Result-icon fallback** — recipes with `IconId=0` walk `ResultItems → ProtoResultItems → [0].ItemCode` for a usable icon.

Polish follow-ups filed against v1 (compact rows, right-pane reading-order rework, recipe-sources section, `mithril://` deep-link route) are tracked on the Project board, not enumerated here.

## Bucketing rule

A CDN source earns a tab if it is **an enumerable set of player-recognisable entities** with a stable name and detail-worthy fields. It does **not** earn a tab if it is a numeric lookup table, an engine-internal mechanic, or a sub-table consumed only by another entity's detail view.

Applied to the 29 sources:

| Bucket | Count | Status | Treatment |
|---|---|---|---|
| A — Tab (v1) | 2 | ✅ Shipped 2026-05-13 | `items`, `recipes` (#207, PR #236) |
| B — Tab | 9 | ✅ All shipped 2026-05-14 → 05-15 | One tab per remaining `EntityKind` — **except `Landmark`, which folded into the Areas tab as a provenance section rather than a sibling tab** (#318 slice 4, PR #324). 8 tabs cover the 9 kinds. |
| C — Fold into a Bucket-A or -B tab | 10 | Mixed (see table) | Provider / index / sub-tables that surface as sections, filters, or sidecars within a parent tab |
| D — Never a tab | 8 | n/a | Lookup / engine-internal / raw-projection feeds |

Totals: 2 + 9 + 10 + 8 = 29, and Bucket A + Bucket B covers every `EntityKind` value that maps to a CDN source. (`EntityKind` also carries the synthetic `RecipeIngredientKeyword` variant noted above; it's deep-link routing, not a tab.)

> **Field-level coverage is a separate axis.** A shipped tab does not mean every POCO
> field is rendered — see [silmarillion-field-coverage.md](silmarillion-field-coverage.md)
> for which properties each detail view surfaces, omits by design, or genuinely misses.

## Bucket B — shipped (as-built record + design rationale)

All eight tabs landed 2026-05-14 → 05-15, in roughly cross-link-dependency order. The
*why this shape* rationale is retained because it still explains how each detail view
is built; status and any deviation-from-plan are called out per entity.

### NPCs — ✅ #241 (PR #280, 2026-05-14), first Bucket B tab

**Design rationale:** Highest cross-link payoff in Bucket B (recipe teachers, item sources, gift preferences via Arwen, quest givers / turn-ins). A full NPC card has its own polymorphism (`Services`, `Preferences`, `ItemGifts`) — built first among Bucket B so the recipe-sources cross-link target ("this recipe is taught by NPC X") became a live link.

**As built:** Same `TabItem + View + ViewModel` shape. Detail pane surfaces Services (Store / Barter / Consignment / Training), gift-preference chips, and back-links to areas. Follow-ups: skill-name resolution in Training rows + Store cap-keyword chips (#292, PR #294), Consignment ItemTypes as keyword chips (#299, PR #300).

### Quests — ✅ #242 (PR #286, 2026-05-14)

**Design rationale:** Quest POCOs are the most mature in the codebase (Phase 1 canary: 25 requirement T-values, 9 reward T-values, full validation harness coverage). The deferral was UI-only — no parser obstacle.

**As built:** Card-style tab; detail pane renders objectives, requirements, and rewards as typed chips. `directedgoals.json` fold-in as a "guided objectives" filter chip is the *intended* sidecar treatment (Bucket C) — verify it actually shipped before relying on it; the tab itself does not depend on it.

### Abilities and Effects — ✅ Abilities #243 (PR #293, 2026-05-14), Effects #244 (PR #298, 2026-05-14)

**Design rationale:** Abilities (8.7 MB) and Effects (6.5 MB) are paired — items and abilities reference effect strings, so neither tab is complete without the other. Sequenced Abilities first so Effects' cross-link chips had destinations on both ends, then Effects. Abilities shipped with the `ITabViewModel` refactor that the multi-tab module needed.

**As built:** The three conditional-rule sub-tables (`abilitykeywords`, `abilitydynamicdots`, `abilitydynamicspecialvalues`) fold into the **Effects tab**, not Abilities — they're predicate-shaped rules engines (`ReqAbilityKeywords` / `ReqEffectKeywords` / `ReqActiveSkill` → attribute deltas / DoTs / tooltip values), naturally consumed effect-side. That sub-table plumbing is tracked separately as **#288**; the Effects tab shipped independently of it. The effect→abilities cross-link was reworked to a provenance-retaining index + popup (#318 slices 1–3, PRs #320/#321).

### Areas and Landmarks — ✅ Areas #245 (closes #246, PR #310, 2026-05-15)

**Design rationale:** Geographic browsing contextualises quests and NPCs (where does this happen, where does this NPC live) — lower-payoff than the entity-centric tabs, so built after them.

**Deviation from plan:** The original plan was "Landmarks as a sibling tab cross-linking to its parent Area." **As built, Landmark did not get its own tab** — it folds into the Areas detail pane as a grouped provenance popup ("NPCs in this area" + landmarks by type), via #318 slice 4 surface 4 (PR #324, folds in #311). `Landmark` remains an `EntityKind` for deep-link routing but is not independently browsable. This is the one Bucket B entity whose treatment changed from the plan.

### Lorebooks — ✅ #247 (PR #322, 2026-05-15)

**Design rationale:** Self-contained narrative content; ideal master-detail shape (list of titles → page body), no urgent cross-link demand — a cheap standalone win once the core entity tabs were in. `lorebookinfo` is the intended in-tab metadata sidecar (Bucket C).

**As built:** Standalone tab with a long-form detail body; inbound `Item.BestowLoreBook → Lorebook` 1:1 chip (#247 slice e). Detail footer later reworked to copyable key/name segments (2026-05-15).

### PlayerTitles and StorageVaults — ✅ PlayerTitles #248 (PR #328), StorageVaults #249 (PR #329), 2026-05-15

**Design rationale:** Completionist / long-tail tabs — small, low-traffic, no blocking dependency. PlayerTitles cross-links from Quests; StorageVaults complements Bilbo's existing inventory view. Shipped last, together, as the Bucket B closeout.

**As built:** Both standard master-detail tabs. (A sequential-merge splice between #328 and #329 was caught and repaired — #331, PR #331 — with no shipped impact.)

---

## Bucket C — folded into existing tabs (not their own tab)

All parent tabs now exist, so "(when shipped)" no longer applies. The remaining
distinction is whether the *fold-in itself* has landed (verified) vs. is still the
intended-but-unverified treatment.

| Source | Folds into | Fold-in status | How it surfaces |
|---|---|---|---|
| `sources_items` | Items tab | ✅ Shipped v1 (#236) | Sources section in item detail |
| `sources_recipes` | Recipes tab | ✅ Shipped (#235, PR #258) | "Taught by" / sources section in recipe detail |
| `itemuses` | Items tab | ✅ Shipped (#318 slice 4, PRs #323/#325) | "Used in" / "Used as" → provenance popup (replaced the naive synthetic-kind fan-out; retired `RecipeIngredientItem`/`RecipeIngredientKeyword`) |
| `sources_abilities` | Abilities tab | ✅ Shipped (#243) | Sources block in ability detail (`AbilityDetailViewModel` reads `IReferenceDataService.AbilitySources`; `AbilitiesTabViewModel` subscribes to the file) |
| `lorebookinfo` | Lorebooks tab | ✅ Shipped (#247) | Category-title resolution + grouping — `LorebooksTabViewModel` consumes `IReferenceDataService.LorebookCategories` to build `LorebookCategoryGroup`s with resolved `CategoryDisplayTitle` (it is a list grouping, not a per-detail sidecar) |
| `directedgoals` | ~~Quests tab~~ — none | ✋ **Skipped by decision (2026-05-16)** | Originally planned as a "guided objectives" filter chip in the Quests tab; consciously **not built**. Grounded reason (data inspected v470): it is the *only* Bucket C source with **no foreign keys to any other entity**. Contents = 9 zone "category gates" + freeform newbie hint cards (`Label` + two prose paragraphs `LargeHint`/`SmallHint`). `Zone` is a display string, not an `areas.json` key; `CategoryGateId` only self-joins within `directedgoals`; entity mentions ("Ivyn the farmer", "Therese in Serbule") exist only as English prose in the hint text — extracting them is NLP, not a join. Every other Bucket C source is a join/cross-link provider, which is exactly what Silmarillion's chip-driven master-detail exploits; this one offers nothing to anchor, so the module's core strength can't be applied. Effectively Bucket D in hindsight, kept in this row for traceability rather than re-numbering the buckets. Not a backlog item — no issue to file. |
| `abilitykeywords` | Effects tab | 🔲 Tracked #288 | Conditional attribute-delta rules predicated on `MustHaveAbilityKeywords` — effect-side, not ability-side |
| `abilitydynamicdots` | Effects tab | 🔲 Tracked #288 | Conditional DoT rules predicated on `ReqAbilityKeywords` + `ReqActiveSkill` + `ReqEffectKeywords` |
| `abilitydynamicspecialvalues` | Effects tab | 🔲 Tracked #288 | Conditional ability-tooltip values predicated on `ReqAbilityKeywords` + `ReqEffectKeywords` |
| `skills` | Recipes + Abilities tabs | ✅ Shipped | Filter chips and skill-level metadata; small enough (~30 skills) that a dedicated tab adds little |

These would each require their own master-detail just to render a row count and a few fields — better as enrichment of a parent entity. All fold-ins above were verified against source on 2026-05-16 except the three #288 sub-tables (still tracked) and `directedgoals`, which was **consciously dropped** as low-value (see its row). This is a closed decision, not an open gap — do not re-file it as a backlog item when a future audit notices the Quests tab has no guided-objectives chip; point that audit here.

## Bucket D — never a tab

| Source | Why not |
|---|---|
| `items_raw` | Raw projection of `items`; internal parser feed only |
| `strings_all` | 15 MB localisation dictionary; pure render-time lookup |
| `xptables` | Numeric tables consumed by skill progression math |
| `advancementtables` | Internal advancement mechanic |
| `attributes` | Low-level effect-attribute primitives consumed by the Effects renderer |
| `ai` | NPC behaviour trees; engine-side, not player-browsable |

> **Reclassified 2026-05-17 — `tsysclientinfo` / `tsysprofiles` left Bucket D.**
> Previously listed here as "calculator-shaped, not card-shaped… not a Silmarillion
> tab." That verdict reasoned from the raw-JSON label, not the contents. Verified
> v470: `tsysclientinfo` is a faceted **Power** entity table (skill-tagged, tiered,
> slot/rarity-gated) and `tsysprofiles` is 40 named **Profile** pools (flat power-name
> arrays) — card-shaped and **crystal-free**. The only calculator-shaped thing, roll
> *resolution* (probabilities/precise rolled power), is absent from CDN data entirely
> (data-availability ceiling, same class as item drop rates) — so it is out of scope
> by *data*, not by shape. Owner-ratified as a dedicated **Treasure System tab**
> (**#412**); recipe detail (#214, rescoped) deep-links into it. Only roll
> *calculation* stays Celebrimbor's. See `module-charters.md` Silmarillion *In scope*
> + its 2026-05-17 History entry for the full ruling.

## History

- **2026-05-13** — v1 shipped (PR #236, Items + Recipes tabs, cross-link navigator, master-detail scaffold). Bucketing rationale captured in this doc; per-tab follow-ups filed against `module:silmarillion` on the [Roadmap Project](https://github.com/users/arthur-conde/projects/3/views/1?filterQuery=module%3A%22Silmarillion%22).
- **2026-05-13** — `EntityKind` grew the synthetic `RecipeIngredientKeyword` variant via PR #259 (keyword-collapse design for the item-detail "Used as" section, replacing a naive fan-out widening of `RecipesByIngredientItem`). Sets the precedent for "deep-link to a tab with pre-filled query" as a kind-target shape distinct from "select a row by name."
- **2026-05-14** — Bucket C reclassification: `abilitykeywords`, `abilitydynamicdots`, `abilitydynamicspecialvalues` move from "folds into Abilities tab" to "folds into Effects tab" (#288). Data-shape verification during the #243 Abilities handoff drafting showed these are predicate-keyed conditional rules engines, not per-ability metadata; their natural rendering destination is effect-side, not ability-side. Bucket math unchanged (still 10 entries in C, 29 total).
- **2026-05-14 → 05-15** — **Bucket B fully shipped.** NPCs #241 (PR #280), Quests #242 (#286), Abilities #243 (#293), Effects #244 (#298) on 05-14; Areas #245 (#310), Lorebooks #247 (#322), PlayerTitles #248 (#328), StorageVaults #249 (#329) on 05-15. Cross-link-dependency order (NPCs first to light up recipe-teacher links; Abilities before Effects). The #233 tab-style refactor (PR #272) and the #243 `ITabViewModel` refactor were the multi-tab enablers.
- **2026-05-15** — **Plan deviation recorded:** `Landmark` did *not* become a sibling tab as the Areas/Landmarks section predicted. It folded into the Areas detail pane as a grouped provenance popup (#318 slice 4 surface 4, PR #324). Bucket B count unchanged (9 kinds, 8 tabs).
- **2026-05-16** — Doc reframed from deferral plan to as-built record now that all Bucket B tabs shipped; [silmarillion-field-coverage.md](silmarillion-field-coverage.md) split off as the per-field axis. Bucket C "(when shipped)" markers resolved against source: `sources_abilities` and `lorebookinfo` fold-ins confirmed shipped; **`directedgoals` confirmed *not* built** (no module reference; Quests tab shipped without it) — and, by decision the same day, **consciously dropped** after inspecting the v470 data: it is the only Bucket C source with no foreign keys (self-contained prose hint cards), so Silmarillion's cross-link model has nothing to anchor. Recorded as a closed, evidence-backed decision in its Bucket C row.
- **2026-05-17** — **`tsysclientinfo` / `tsysprofiles` left Bucket D → Treasure System tab (#412).** Owner-ratified reversal of the "calculator-shaped, not a Silmarillion tab" Bucket-D verdict, which had reasoned from the raw-JSON label rather than the contents. v470 inspection: `tsysclientinfo` is a faceted Power entity table (skill-tagged, tiered, slot/rarity-gated), `tsysprofiles` is 40 named Profile pools (flat power-name arrays) — card-shaped, crystal-free; only roll *resolution* is calculator-shaped and it is absent from CDN data entirely (data ceiling, not shape). #214 rescoped from inline pool render to a deep-link into the new tab (single render site). The companion naming collision — `Create{Region}TreasureMap{Quality}` is the `TreasureCartography` skill, a different system — was flagged out of scope on #214/#412. Charter (`module-charters.md`) updated in the same PR. Echoes the 2026-05-13 "deep-link to a tab with pre-filled query" kind-target precedent.
