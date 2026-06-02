# Silmarillion #404 — Phase 3 design dispatch (close grammar gaps → harness specimens → G3)

**Tracked in:** #404

> **Status: EXECUTED — G3 ratified 2026-05-17.** This dispatch is spent;
> retained for provenance. Claude Design's ratified output is
> [`docs/silmarillion-visual-grammar.md`](../../silmarillion-visual-grammar.md).
> Phase 4 (shared WPF primitives) is the next dispatch — do not re-run this one.

> **For the designer (Claude Design).** This is the Phase 3 spec of the #404
> visual-grammar program. Phases 0–2 and gates G1/G2 are **closed and
> authoritative on the issue** — do not reopen them. Your job is to take the
> ratified grammar + scoped findings and produce the artifact that unblocks
> **G3** (per-tier visual target chosen), then stop. The hard constraints below
> are settled engineering positions; re-deriving them reproduces the exact debt
> #404 exists to pay down.

## Where this sits

Gate state at dispatch: G1 ✅ (five tiers ratified) → Phase 1 ✅ (Recipe pilot,
maintainer-confirmed) → Phase 2 ✅ (nine-view sweep) → **G2 ✅ CLEARED**. Phase 3
is the next unit; **G3 is a designer gate** and is yours. Phase 4 (encoding the
chosen targets as shared `Mithril.Shared.Wpf` primitives) and Phase 5 (migration)
are *not* in scope here — keep that boundary or the gated-ownership model breaks
down the way it did before #404 was filed.

The presentation axis is the only axis. The coverage axis is separately tracked
(#407 source-duplication, #408 stale Item-detail-pane doc) and is **out of scope**.

## Read first (authoritative, in order)

1. [`spec.md`](spec.md) — the program plan (sibling in this slug folder).
2. Issue #404, comment **"G1 — CLEARED. Final ratified visual grammar"** — the
   five tiers (Fact · Control · Link · Set-reference · Structure), principles
   P1/P2, the availability corollary.
3. Issue #404, comment `4468112072` — Phase 1 Recipe `treatment × tier` matrix.
4. Issue #404, comment `4468177334` — Phase 2 consolidated nine-view matrix +
   16-treatment inventory.
5. Issue #404, comment `4468464536` — **"G2 — CLEARED"**. This scopes your work
   (E1/E3/E4 ratified; E5 footer data-governed; E6 split; G-a/G-b/G-c carried).
6. The design-system preview harness the critique came from (the chip-critique
   bundle linked in the program plan's *Context* section) — this **is** your
   specimen harness.

## Deliverable (Phase 3 → unblocks G3)

A. **Close every remaining grammar-gap cell in writing first** — for G-a, G-b,
   G-c either extend the grammar or argue the cell into an existing tier,
   explicitly, in prose. *No visual target is chosen until every gap is closed
   in writing* (a target picked into an open gap is a new improvisation — the
   original failure mode).
B. **Produce specimen artboards** in the preview harness for each of the five
   tiers, carrying the constraints below (do not re-derive them).
C. **Choose the per-tier visual target** (pigment, glyph, spacing, hover,
   weight). This is *your* call; the classification and constraints are
   decision support, not the decision.
D. **Record it**: post the chosen targets as a "visual grammar" section to
   #404 as the G3 decision, and add it as a `docs/` reference. Then **stop at
   G3**.

## Hard constraints — settled, do NOT re-open

- **Link tier = V2**: small lead-icon + gold name, **no box**. **V5**
  (prose/list dual-form) is **rejected** — the optional-context render-flag
  footgun. Present V2; only revisit V5 if you (as designer) raise it, and if so
  attach the V5 rejection rationale from the program plan.
- **Fact tier must read INERT** (borderless label:value or mono) — impossible to
  confuse with Link or Control. This is half the grammar, not orthogonal.
- **Avoid V1** (no icon → loses NPC/item/recipe type recognition) and **V3**
  (dotted underline = Windows tooltip-trigger mis-affordance).
- **Set-reference must be visually NON-confusable with Link** (door vs drawer)
  and must **not** default to Link's look. Per E4 this tier is broad (filter /
  keyword / group / stacking chips across many views), not a single button.
- **Structure is thin** (section labels; inline field-label prefixes per E1) —
  must not read as Link/Fact chips (E2), lowest priority/stakes.
- **Tier is semantic; visual weight is orthogonal (P2).** The Fact weight
  spectrum (title-loud … footer-quiet) is a design axis you own, separate from
  tier — never classify by appearance.

## Open G3 questions you must resolve

- **G-a — Fact-with-copy-affordance (the footer).** A Fact may carry a
  click-to-copy affordance without reading as a Control. It must satisfy:
  support **0, 1, or 2** identifiers per entity; copyable **iff** the
  identifier is a cross-entity reference key (`InternalName`/title key), and
  **inert** for storage-only keys (`EnvelopeKey`-style).
- **G-b — gold `#D4A847` overload.** Currently shared by Fact-title, Fact gold
  badges/values, Structure group labels, Set-reference, *and* the Link target.
  Resolve so Fact-loud / Link / Set-reference / Structure-group are mutually
  legible — pigment alone is insufficient. *(Highest-judgement item.)*
- **G-c — Link availability degrade, both directions.** (i) A link whose
  target tab isn't shipped is still a Link — an identical look promises a dead
  click; a dimmed cue risks leaking our shipping schedule (inherited tension;
  you decide it here). (ii) Non-navigable Facts currently styled chip-like must
  stop reading as dead links. Also: the single Link primitive must subsume
  **both** `EntityChip` and `ItemSourceChip`, so the Link spec must include the
  non-tabbed degrade sub-form + an optional provenance/kind label.

## Anti-goals (violating these reproduces the debt)

1. Do **not** re-open coverage. #407 / #408 are coverage-axis and out of scope.
2. Do **not** re-decide the hard constraints above.
3. Do **not** pick any pixel target until every grammar gap is closed in writing.
4. You make the **visual** call; you do **not** write the shared primitive
   (Phase 4) or the migration (Phase 5).
