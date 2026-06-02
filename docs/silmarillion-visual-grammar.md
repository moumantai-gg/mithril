# Mithril · Silmarillion visual grammar (G3 ratified)

> Issue: [#404](https://github.com/moumantai-gg/mithril/issues/404) ·
> Program plan: [`docs/planning/silmarillion-404-visual-grammar/spec.md`](planning/silmarillion-404-visual-grammar/spec.md) ·
> Phase 3 dispatch: [`docs/planning/silmarillion-404-visual-grammar/phase3-design.md`](planning/silmarillion-404-visual-grammar/phase3-design.md) ·
> Decision date: 2026-05-17 ·
> Phase: 3 — closed at gate G3.
>
> Authored by **Claude Design** (the designer per the #404 gated-ownership
> model) and ratified at G3. Reproduced here verbatim; the only repo-side
> additions are the reconciled decision log and the "Phase-4 carry-forward"
> section, both clearly delimited and not changing any visual call.
>
> Specimen harness (`explorations/phase-3-visual-grammar.html` — live A/B vs.
> V0, per-tier specimens, rendered G3 spec card) lives in the design-system
> bundle, **not** this repo; it is the source artifact, not a maintained file.
>
> Encoding the targets below as shared WPF primitives is Phase 4 — separate
> dispatch. **Do not migrate against this doc directly.**

---

## What G3 settles

G1 ratified five tiers: **Fact · Control · Link · Set-reference · Structure**.
G2 ratified Phase 2's nine-view inventory and surfaced three unresolved cells:

- **G-a** — Can a Fact carry a click-to-copy ID without becoming a Control?
- **G-b** — Gold #D4A847 is overloaded across Fact-title, Fact gold values, Structure-group, Set-reference, and Link. Resolve mutual legibility.
- **G-c** — Link availability degrade (both directions), and EntityChip+ItemSourceChip subsumption.

This document closes all three in prose, then declares one visual target per tier (pigment / glyph / shape / hover). Weight is orthogonal (P2) and gets its own axis at the end.

---

## G-a — Fact identifiers, copyable without becoming Control

A Fact may carry a click-to-copy affordance (the footer ID strip) without
reading as a Control. The boundary is set by **where the affordance lives**,
not what it does.

- **Location, not chassis.** The ID strip lives below a thin top-border divider, beneath the read-flow. Controls live *inside* the read-flow (above, beside, or at the foot of the action area).
- **No surface at rest.** Zero border, zero fill — mono text on the pane background.
- **0 / 1 / 2 identifiers per entity.** Strip hidden at 0; single cell at 1; dot-separated at 2.
- **Each cell has a tiny uppercase label** (`KEY`, `ROW`) in `--fg-quaternary`, then the value in `--fg-tertiary` mono.
- **Copyable iff cross-entity reference.** `KEY` (InternalName, title key) is copyable: hover reveals a 11px `copy` Lucide glyph, click copies, brief toast. `ROW` (EnvelopeKey-style storage-only) is **inert**: `cursor: default`, no hover state, no glyph.

**Why this isn't Control:** a Control declares itself at rest (chassis). The
footer ID declares itself only on contact (hover-revealed glyph). Scan-time
read is "another fact under the divider"; the copy affordance is *discovered*.

---

## G-b — Gold overload, resolved by stripping jobs

Gold gets exactly two jobs. Everything else loses its gold.

| Use | Decision |
|---|---|
| **Fact-title** (Cambria 18pt, 600, one per pane) | **Keep gold.** Loudest gold in the system. |
| **Link name** (body weight, with 12px lead glyph) | **Keep gold.** The icon disambiguates from title. |
| Gold values inside Fact body text | **Drop.** Gold-in-prose now *always* means Link. Proper nouns that aren't navigable render as plain text. |
| Structure-group labels | **Drop.** Move to `--fg-tertiary` / `--fg-quaternary`, tracked uppercase. Structure is a frame, not a finding. |
| Set-reference | **Drop.** Move to `--info` blue with a small bordered chip, matching `--q-keyword` from MithrilQueryBox. |

**Differentiators stacked under pigment:**
- **Shape** — Link has no box; Control and Set-reference do; Fact-title is typographic (serif).
- **Glyph** — Link always has a 12px lead Lucide. Nothing else in the system does.
- **Family** — Fact-title is Cambria; everything else is Segoe UI.

Net: gold now reads as **title** (serif, large, no glyph) or **link** (sans, body-weight, leading glyph). Two unambiguous patterns.

---

## G-c — Link availability degrade · single primitive subsumes Entity + Source

### (i) Degrade — identical at rest, kind on contact

- **Rest state is identical** to a shipped Link. No dim, no italic, no muted glyph. *Information-leak budget = 0* for shipping schedule.
- **Hover swaps the implicit cue.** Shipped Link's hover is the gold-tint background ("click to navigate"). Degraded Link's hover is a small `⧉` copy glyph at the end with a cooler / neutral hover tint ("click to copy").
- **Click action degrades, doesn't dead-end.** Click copies the canonical name to clipboard and flashes *"Detail view coming — name copied."* Useful action; no dead click; no schedule disclosure.
- **Why no dim:** dim is the UI vocabulary for "loading / unavailable / stale". A perfectly real game-fact that we just haven't wired up yet is none of those. Mixing them reads as a bug.

### (ii) Subsumption — one Link, three optional bits

The Link primitive **is** `<icon> <gold name>` plus:

- **provenance suffix** *(optional)* — italic, `--fg-quaternary`, 10pt, trailing: *— from Distil Brine.* Renders when Link acts as ItemSourceChip; absent everywhere else. **Not for name discriminators** — strings that are part of the entity's canonical name (`(Enchanted)`, `(Max-Enchanted)`, Mk. N suffixes, variant parentheticals) stay inside the gold name span. Provenance is *where this thing came from*, not *which variant of it this is*.
- **kind label** *(optional, rare)* — same slot: *— skill*, *— consumable*. Used when icon+name aren't enough.
- **degrade flag** *(optional)* — changes hover only (see above).

EntityChip and ItemSourceChip both collapse into this primitive in Phase 4.

---

## G-d — Link reference-state axis (Confirmed · Degraded · Unconfirmed)

*Ratified 2026-05-17 (G3 amend 3). Origin: Claude Design review of the #429
(#407) shipped baseline; design doc captured on #431. Closes the Link-tier
**state** axis G-c opened.*

G-c settled one state question (target unshipped → **Degraded**). A second,
orthogonal one was deferred: a reference can be **declared but uncorroborated**
— `sources_items.json` asserts an edge the inverse data (`recipes.json` /
`quests.json` reverse) does not back. #407 shipped a deliberate
**projection-only stopgap**: the caveat rode the Link's provenance suffix
(*"declared recipe source — not confirmed by recipe data"*). That overloads the
slot — provenance is *where the entity came from*; a data-integrity caveat is a
different thing, and a Link with real provenance *and* a brokenness caveat
fights itself. No scan-time signal, wall-of-italic at 3+ rows.

**The axis.** One question: *can the reader trust this edge?* Three positions,
**none requiring a new pigment or glyph** beyond what V2 Link ships:

| State | Meaning | Visual rule | Hover | Tail |
|---|---|---|---|---|
| **Confirmed** | Both ends agree (or asymmetric and this is the asserting side). | Gold name + type-glyph. No underline. | 10% gold tint. | Optional provenance only (italic, `--fg-quaternary`). |
| **Degraded** (G-c) | Reference real; target detail unshipped. | Identical to Confirmed at rest. | Neutral tint; copy-cue glyph. | None. |
| **Unconfirmed** (G-d) | Declared in the sources data; neither entity backs it in its own data. | Gold name + **dashed underline**, 1px, `rgba(212,168,71,0.55)`. Type-glyph unchanged. | Full caveat as the control `ToolTip`. | Short word `· unconfirmed`, `--fg-quaternary`. |

**Rules.**

- **Dashed underline is the primary, scan-time signal.** It carries an
  existing cross-software meaning (*this thing has a caveat*) — no new
  convention. It modifies the primitive locally (one decoration), works for
  sprite / Lucide / degraded Links alike, introduces **no new pigment** (stays
  out of the G-b gold pile — the gold name still navigates, the underline
  qualifies it).
- **The tail shrinks to one word.** `· unconfirmed` in `--fg-quaternary`; the
  long-form caveat moves to the hover `ToolTip` (data-driven). The provenance
  suffix slot is **freed** — never again double-loaded with a caveat.
- **State ⊥ availability.** Unconfirmed (assertion axis) is orthogonal to
  Degraded (`IsNavigable`, coverage axis) — same logic as the P2 weight ⊥ tier
  rule. A Link can be both; the dashed underline rides on top of the copy-cue
  hover, both legible because they live on different sub-elements
  (border-bottom vs. inline glyph). The VM carries an **additive `IsUnconfirmed`
  flag + optional tooltip**, *not* a single `state` enum (an enum can't express
  the compose case).
- **Section-level signal (optional, light).** When *every* Link in a Structure
  section is Unconfirmed — the compounding case where the residue is the
  entity's only source, so the pane would otherwise imply provenance it
  effectively lacks — prepend the section label with a small `unlink` Lucide
  glyph in `--fg-quaternary`. This is the light form of the rejected
  "separate *Declared, unconfirmed* block" (variant E): same gesture at the
  header, not a full split (a reader scanning "Sources" should still find the
  row under Sources). Variant E is reserved as an opt-in maintainer overlay,
  not the default.

**Rejected alternatives** (one line each, from the review): *glyph badge* —
fragile 9px sub-glyph composite on a busy sprite, costlier in WPF; *warn-hue
name* — gold→warn (`#D4A847`→`#E0A030`) is two ticks apart, reads as "the same
gold" in isolation; *separated section as default* — divorces the Link from the
section it semantically belongs in.

---

## The grammar — one target per tier

### Fact · inert · weight-axis

| | |
|---|---|
| **Role** | Truth about the entity. Title, stat strip, body, footer ID. |
| **Pigment** | Title: `--accent` `#D4A847` (one per pane). Values: `--fg-primary`. Labels and footer: `--fg-tertiary`. |
| **Glyph** | None in body. `copy` Lucide on footer-ID hover only. |
| **Shape · spacing** | No border. No surface. Title is Cambria serif. Stat strip = dot-separated label-value pairs. Footer below a thin `--border-faint` divider. |
| **Hover** | Body: none. Footer ID: row tint + copy-cue (KEY); none (ROW). |
| **Weight axis** | title-loud → body → meta → footer-quiet (see below). |
| **Replaces** | Stat-badge chips, gold-value-in-prose, current source-tag. |

### Control · interactive · self-evident

| | |
|---|---|
| **Role** | Buttons, inputs, toggles. The only tier with a real chassis. |
| **Pigment** | Chassis: `--bg-surface` with `--border-subtle`. Primary fill: `--accent` with `--accent-fg` label. |
| **Glyph** | Optional Lucide, 12px, before label. |
| **Shape · spacing** | `--radius-md` (3px). 1px border. 5×12 padding. The signature surface. |
| **Hover** | `--bg-surface-hover` / `--border-strong`. |

### Link · navigates · V2

| | |
|---|---|
| **Role** | Navigate to another row in the master-detail. Subsumes EntityChip and ItemSourceChip. |
| **Pigment** | Name: `--accent` `#D4A847`. Provenance suffix: `--fg-quaternary` italic. |
| **Glyph** | **Hybrid icon family — CDN sprite if `iconId` is set, Lucide otherwise** *(G3 amendments 2026-05-17 — see decision log)*. Sprites resolved as `https://cdn.projectgorgon.com/{version}/icons/icon_{iconId}.png`, rendered with `image-rendering: pixelated`. Tangible entities (items, recipes, NPCs, monsters) carry an `iconId`; abstract entities (skills, abilities, locations, keywords, factions) do not, and fall back to a kind-mapped Lucide glyph. **Sizes are em-relative, not px**, so they scale with the user's Appearance font-size slider: prose-density Link `1em` sprite / `0.75em` Lucide; list-density Link `1.5em` sprite / `1.125em` Lucide. One Link primitive, two inputs — `iconId` (drives family) and `density` (drives size); no entity-kind discriminator at the call site. |
| **Shape · spacing** | No border. No surface at rest. 2px tap padding (`margin: 0 -2px`) so the tint hover-box fits visually. Inline with text baseline. **Two row modes:** `Density="Prose"` — inline in a sentence, single Link or short list; `Density="List"` — own line per entry, sprite scaled up to `1.5em`, line-height ~1.7. |
| **Hover** | 10% gold tint background (`rgba(212,168,71,0.10)`). |
| **Degrade** | Rest = identical to shipped. Hover swaps nav-tint for neutral row-tint + trailing `⧉` copy glyph. Click copies the canonical name. |
| **State** *(G-d)* | Confirmed (default) · Degraded (above) · **Unconfirmed** — declared edge the inverse data doesn't back: gold name + dashed underline `rgba(212,168,71,0.55)`, short `· unconfirmed` tail (`--fg-quaternary`), full caveat as `ToolTip`. Additive `IsUnconfirmed` flag, orthogonal to Degrade. See G-d. |
| **Replaces** | EntityChip, ItemSourceChip. |

### Set-reference · filter / keyword / group / stacking

| | |
|---|---|
| **Role** | Narrows or stacks a set: filter chips, multi-select, inline keywords, group memberships. The drawer, not the door. |
| **Pigment** | Text: `--info` `#88B0E0` (matches `--q-keyword`). Idle fill: `rgba(30,58,95,0.30)`. Active fill: `--accent-soft` `#1E3A5F`. Border: blue-tinted, 1px. |
| **Glyph** | None by default. Optional `×` remove on active filters. |
| **Shape · spacing** | `--radius-sm` (2px). 1×8 padding. Sits in a `t-set-row` flex container with 5–6px gaps. |
| **Hover** | Fill darkens, border brightens — drawer-pulling feel. |
| **Why blue, not gold** | MithrilQueryBox already uses `--q-keyword` blue for query keywords. Set-reference *is* a query keyword in chip form. Pigment kinship makes that relationship visible and removes Set-reference from the gold pile. |

**Stacking semantics.** Two adjacent Set-refs with identical constraints are *not* a duplicate — they are two independent positional slots that happen to bind the same set (canonical case: a recipe with two "any Crystal" ingredient slots, where `TSysCraftedEquipment` references the actual crystal bound in each slot). Render:

- **Positionally material** (consumer references slot index) — stack vertically, one chip per row, with a small `--fg-quaternary` ordinal prefix (`1`, `2`, …). Section label gets a slot count suffix (`Keyword ingredients · 2 slots`).
- **Positionally inert** (consumer just counts matches) — render side-by-side with default `t-set-row` gap; let repetition speak.

### Structure · scaffolding · never gold

| | |
|---|---|
| **Role** | Section labels, inline field-label prefixes, group headers. The bracket around the value. |
| **Pigment** | `--fg-tertiary` (label, inline prefix). `--fg-quaternary` (group header). |
| **Glyph** | None. |
| **Shape · spacing** | Tracked uppercase (letter-spacing 0.08em). 9–9.5pt. Weight 600. Section labels get `margin-top: 10px`; inline prefixes terminate with `:`. |
| **Hover** | None. |
| **Lowest priority tier.** Must not read as Link (no gold, no glyph) or Fact-stat (no value beside it). |

---

## The Fact weight axis (orthogonal to tier — P2)

Every tier can render at every weight; this axis is named on Fact because Fact uses the full spectrum in a single pane.

| Weight | Sample | Spec |
|---|---|---|
| **Title-loud** | Brew Tincture of the Tides | Cambria · 18pt · 600 · `--accent` · 1×/pane |
| **Body** | Distil moonlit brine into a tincture. | Segoe UI · 11–12pt · `--fg-primary` |
| **Meta** | Skill **Alchemy 55** | Stat strip · 10.5pt · `--fg-tertiary` label + `--fg-primary` value |
| **Footer-quiet** | `BrewTinctureOfTheTides` | Consolas mono · 9.5pt · `--fg-tertiary` |

Set-reference also spans loud (toolbar filter chip) → quiet (inline keyword in prose). Control rarely goes quieter than meta-weight (a small icon-only toolbar button is the floor). Link is almost always body-weight by definition (it lives in sentences). Structure is always meta-weight or quieter.

---

## What this doc does NOT do

- **Phase 4 (WPF primitives)** — encoding `<mithril:Link/>`, `<mithril:SetRef/>`, `<mithril:FactStat/>`, `<mithril:FactFooter/>` as shared resources. Separate dispatch.
- **Phase 5 (migration)** — call-site sweep, `EntityChip` and `ItemSourceChip` collapse into `Link`, removal of legacy stat-badge chip. Separate dispatch.
- **Coverage axis** — issues [#407](https://github.com/moumantai-gg/mithril/issues/407) (source duplication) and [#408](https://github.com/moumantai-gg/mithril/issues/408) (stale Item-detail-pane doc) are explicitly out of scope here.

---

## All sizes are em-relative, not px

Mithril's Appearance settings give users a body-font picker and a base-size slider (~11–18pt). The Link primitive — and every tier in this spec — sizes itself in `em` relative to inherited font-size, so a user bumping body to 15pt gets sprites at 20px and Lucide at 15px without anything in the spec changing.

**Exception: the title-glyph (entity icon next to the Fact-title) stays a fixed 40px.** It's an entity stamp, not a UI element subject to accessibility scaling. Pixel art at non-integer scale multiples looks bad; fixing the size keeps the sprite at integer-pixel render. No border, no surface fill when an image is present — the image just sits in the slot.

| Element | Size | At 12pt default | At 15pt | At 18pt |
|---|---|---|---|---|
| Body | `1em` (defined by user) | 16px | 20px | 24px |
| Title (Fact-title) | `1.5em` | 24px | 30px | 36px |
| Title-glyph (entity icon) | **`40px` fixed** | 40px | 40px | 40px |
| Link prose sprite | `1em` | 16px | 20px | 24px |
| Link list sprite | `1.5em` | 24px | 30px | 36px |
| Link prose Lucide | `0.75em` | 12px | 15px | 18px |
| Link list Lucide | `1.125em` | 18px | ~22px | 27px |
| Stat strip | `0.9em` | ~14px | ~18px | ~22px |
| Footer ID | `0.78em` | ~12.5px | ~15.6px | ~18.7px |
| Section / Structure label | `0.78em` (tracked) | ~12.5px | ~15.6px | ~18.7px |

Phase 4 implements the Link primitive with `Density="Prose|List"` as its sole sizing input; the renderer multiplies inherited `FontSize` by the appropriate factor (a `FontSize × n` converter is the cleanest XAML approach).

---

## Decision log

Reconciled to the authoritative #404 thread (the bundle draft's speculative
2026-05-14/15 dates did not match the issue history; cited by permalink so the
log can't drift).

| Gate | Record on #404 | Decision |
|---|---|---|
| G1 | "G1 — CLEARED. Final ratified visual grammar" comment | Five-tier grammar ratified (Fact · Control · Link · Set-reference · Structure). |
| Phase 1 | [comment 4468112072](https://github.com/moumantai-gg/mithril/issues/404#issuecomment-4468112072) (matrix) · [4468157363](https://github.com/moumantai-gg/mithril/issues/404#issuecomment-4468157363) (maintainer-confirmed) | Recipe pilot `treatment × tier` matrix. |
| Phase 2 | [comment 4468177334](https://github.com/moumantai-gg/mithril/issues/404#issuecomment-4468177334) | Nine-view consolidated matrix + 16-treatment inventory. |
| G2 | [comment 4468464536](https://github.com/moumantai-gg/mithril/issues/404#issuecomment-4468464536) | Inventory cleared; E1/E3/E4 ratified, E5/E6 scoped; G-a / G-b / G-c flagged open. |
| **G3** | this document (2026-05-17) | **G-a / G-b / G-c closed; one visual target per tier ratified. Phase 4 unblocked.** |
| **G3 amend 1** | [#404 comment 4469052562](https://github.com/moumantai-gg/mithril/issues/404#issuecomment-4469052562) (2026-05-17) | **Link lead element → hybrid icon family** (sprite for tangible nouns, Lucide fallback for abstract). + provenance suffix ≠ name-discriminator. Surgical single-rule; Phase-5 Recipe pilot G4 finding. |
| **G3 amend 3** | #431 (2026-05-17); origin = Claude Design review of the #429/#407 baseline | **Link reference-state axis closed (G-d): Confirmed · Degraded · Unconfirmed.** Adds an additive `IsUnconfirmed` flag (⊥ the `IsNavigable`/Degraded axis) → dashed gold underline + one-word `· unconfirmed` tail + caveat `ToolTip`; frees the provenance slot the #407 stopgap overloaded. No new pigment/glyph. Re-touches Link primitive + `LinkVm`/`ItemSourceChipVm` carriers; supersedes #407's projection-only provenance-suffix caveat. Own PR, off latest `main`; #429 untouched. |
| **G3 amend 2** | #404 (2026-05-17) | **System-wide em-relative sizing + Link `Density`** (Prose/List) + **new fixed-40px title-glyph** rule. Root-cause of the pilot's "sprites too small at 12px" finding (px ignored the Appearance accessibility slider). Scope expansion *consciously accepted by maintainer* — re-touches Link + FactTable + FactFooter + Structure + Fact-title (all Phase-4 primitives) + adds the title-glyph element. Supersedes amend-1's fixed-12px. Pilot re-run on PR #411. |

---

## Phase-4 carry-forward (repo-side note — not a grammar change)

Two under-specifications surfaced when this grammar was sanity-walked against
the real V0 panes Claude Design designed from (Accelerated Orcpick Mk. 5;
Otherworldly Metal Slab). Neither is a tier-model gap or a constraint
violation — the grammar classifies both panes cleanly. They are recorded here
so Phase 4 **decides** them rather than improvising (the original failure mode
#404 exists to prevent):

1. **Per-element quantity (`Extraordinary Metal Slab ×2`).** The Link primitive's
   three optional bits (provenance suffix / kind label / degrade flag) do not
   include an adjacent count. Resolution to encode in Phase 4: a quantity is
   **adjacent Fact body text next to the Link, never part of the Link
   primitive** (consistent with "gold ⇒ Link, plain ⇒ Fact"). Stated so every
   call site does not re-decide where `×N` sits.
2. **`TSysCraftedEquipment(…)` Effects-stub line placement (body vs footer).**
   Raised during Phase 3 and deliberately left soft ("storage-only-key
   territory under G-a — *if you want to* demote to footer"). This is the
   **#214** ResultEffects rich-render stub, already separately tracked.
   **Deferred and bound to #214**; Phase 4 must not improvise its placement —
   it inherits whatever #214 decides.
3. **One polymorphic Fact-group primitive — including the degenerate scalar.**
   The recipe-header Fact-stat strip (horizontal, dot-separated `label: value`
   pairs) and the StorageVault favor-tier capacity table (vertical, 2-column
   `label → value` grid) are the same data rotated; Phase 4 should encode them
   as **one** layout-switchable primitive (`FactTable`-style), **not** two —
   same anti-fork rationale as the single-Link mandate subsuming
   `EntityChip`/`ItemSourceChip`. It must additionally degrade to a **single
   flat scalar** (e.g. a chest whose `Capacity` is just `16 slots` — no labels,
   no rows) without forking into a separate control. `Capacity` is fully
   polymorphic in the existing data — favor-tier table · flat slot count ·
   script-atomic range · event-gated overrides (Phase 2 inventory); Phase 4
   decides which shapes the one primitive renders vs. which stay plain Fact
   body lines. No grammar change: every shape is Fact-inert (G-b still strips
   gold from these values).
4. **One polymorphic Set-reference primitive — summary-form and tag-form.**
   The Phase 3 handoff's Bendith artboard makes explicit that Set-reference has
   two shapes on one blue chassis: *summary-form* (`Crystal · 150 matches →` —
   count + arrow ride on the chip) and *tag-form* (bare keyword chips:
   `Alchemy`, `Potion`, … — no count, no arrow, no lead glyph; the chip shape
   carries the tier). The designer confirmed the **grammar already covers this**
   (Set-reference Role: "filter / keyword / group / stacking"; glyph "None by
   default") — so this is **not** a grammar change. It is pinned here only so
   the Phase 4 `<mithril:SetRef/>` primitive is built shape-flexible (one
   primitive, the count+arrow optional), **not** forked into separate
   "filter chip" vs "keyword tag" controls — the identical anti-fork rationale
   as #3 and the single-Link mandate. Additionally: a tag-form Set-ref whose
   filter action is not yet wired is, by the ratified **availability
   corollary**, still a Set-reference in a non-activated state — it must render
   on the blue Set-ref chassis, **never** degrade to an inert grey Fact pill
   (the forbidden inverted-affordance lie; today's StorageVault keyword tags are
   exactly that degrade and Phase 5 must correct it, not preserve it).
