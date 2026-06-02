# Gate review → Claude Design revision directive — #412 Treasure System detail V0

**Tracked in:** #412. Companion to the commission brief
[`2026-05-17-silmarillion-412-treasure-detail-design.md`](2026-05-17-silmarillion-412-treasure-detail-design.md).
Grammar precedent recorded on #404 (G-d scope).

> This is the gatekeeper review of Claude Design's V0 (`treasure-system.html`,
> design-bundle artifact — **not** committed; only the ratified spec returns
> to the repo). Owner-ratified rulings below are **directives**, not opinions:
> the designer declined to land them unilaterally (correctly), so they come
> back ratified. Revise V0 against this; do not re-litigate the ratified cells.

---

## Verdict

Structurally strong and grammar-faithful. The Tiers ladder (CF#3 FactTable,
N=0/1/2/≥3 polymorphism, overlapping-band micro-chart) is the correct answer
to the central problem and is **kept as-is**. The data-availability ceiling
was correctly treated as a *correctness* line (strawman includes the
forbidden "Roll probability" block; V0 omits it). Three calls change; the
rest of the deck is confirmed.

## Keep (ratified — do not change)

- Tiers ladder: CF#3 FactTable shape, flat-scalar degrade, band micro-chart.
  Fact-inert, no gold on values. **Except** the Rare-band fill — see Apply #2.
- `Slots` → Set-reference tag-form (not navigation). Correct.
- `Power.Skill` → Link, `sparkles`, Confirmed. Correct.
- G-a footer ID strip; strawman/V0 A·B; per-edge rationale; the six gates
  surfaced rather than improvised. Process correct.

## Apply — hard corrections (not negotiable)

**1. Pools and Recipes are `Confirmed`, not `Unconfirmed` (G-d).**
Owner-ratified. `tsysprofiles` is the **canonical, authoritative, normalized
membership table** — it *is* the backing data, not a declared-but-unbacked
assertion. G-d Unconfirmed is reserved for the #407-class case (an edge one
side asserts that the inverse data does not substantiate). "The power record
doesn't self-reference its pools" describes a one-directional foreign key —
i.e. *every* reverse-index in Silmarillion (items↔recipes included, which
render as Confirmed provenance). If a normalized join is Unconfirmed, every
reverse-lookup degrades and the G-d distinction voids; it also paints a
"scary" caveat on the single most reliable relationship in the dataset (the
forbidden inverted-affordance lie, grammar doc § availability corollary).
- Pools section → plain **Confirmed** Links (`layers` glyph, gold, gold-tint
  hover). Remove dashed-underline + `· unconfirmed` tail + the asymmetry
  tooltip.
- Recipes → **Confirmed** Links inside the provenance popup. The Control
  disclosure stays (justified by 1:N indirection, consistent with the
  `itemuses`/#318 precedent) — but the contents are Confirmed, not
  Unconfirmed.
- Delete the "Why this is not degrade" Unconfirmed framing for these edges.
  The G-d *state* remains in the grammar for #407-class edges; it simply does
  not apply to authoritative join tables. (Precedent recorded on #404.)

**2. Rare-band fill at `--accent` (gold) is a literal G-b violation.**
`.ladder-band-fill.rare` paints gold, rationalized as "a rarity hint, not a
Link cue." G-b strips gold from everything except Fact-title and Link
*precisely so gold cannot mean a third thing*. Re-encode the Rare band via
the rarity colour scheme (tracked #54), or via weight/border — not
`--accent`. Everything else in the ladder is gold-clean; keep it that way.

## Apply — owner gate rulings

**Q1 — gold Fact-title = `InternalName` verbatim.** Owner-ratified.
*Reviewer self-correction (recorded for honesty):* the review first asserted
"resolve from strings, never humanize" per the #405 no-CamelCase-split rule.
**Verified false for Powers** — `resolve_strings` returns null for every
candidate key and the `tsysclientinfo` record has **no `DisplayName` field**
(fields are exactly `InternalName`, `Prefix`, `Suffix`, `Skill`, `Slots`,
`Tiers`). The #405 rule presupposes a resolvable DisplayName; for Powers none
exists, so the rule is out of domain.

**Affix-as-title is also foreclosed (owner, domain rationale).** The
`Prefix`/`Suffix` affixes are **not a usable title source** and not just a
deprioritised option: *we do not know the rules PG uses to apply them.* Some
powers carry only a `Prefix`, some only a `Suffix`, some both; the logic that
governs which apply, and how they compose onto an item name, is engine-side
and **not deterministically replicable from CDN data**. Any affix-derived
title would be a guess at non-replicable game logic — the same class of error
as inventing roll-resolution. This removes affix-stitching from the option
set entirely (not "ungrammatical, reject" but "non-replicable, cannot").

Ruling:
- Title = `InternalName` verbatim (`SwordBoost`) in the gold Cambria
  Fact-title. Not humanized (drop V0's PascalCase split); **not
  affix-derived** (non-replicable, per above); no transformation of any kind.
- **Consequence to handle deliberately:** the G-a footer `KEY` is also
  `InternalName` → it now duplicates the title *text*. Keep the footer KEY
  regardless — it carries the **copy affordance** (the title is not
  copyable). Treat the text redundancy as intended (Powers have no second
  identity), not a bug to design away. Annotate this in the spec card so
  Phase 4 doesn't "fix" it by removing the footer.
- The affix **stays as Fact-body flavour only**, but V0's rendering must be
  corrected: it hard-codes `Prefix … «item» … Suffix` (both present). Render
  **only the affix parts that exist** (Prefix-only / Suffix-only / both) and
  phrase it as *illustrative, not authoritative* — e.g. "appears on items
  roughly as …", never as a reconstructed canonical item name. It is **not**
  the player-facing identity in any deterministic sense; it is an
  approximate flavour cue, and the copy must not imply otherwise.

## Confirmed gates (no change — these were correctly left open)

- **Q2** constant-column collapse — genuine CF#3 per-column-vs-per-table
  question. Carry to Phase-4 gate; V0's dim-to-quaternary is an acceptable
  interim.
- **Q3** band dual-encoding — legitimate dual-encoding of one Fact (confirmed
  *once the Rare gold is removed per Apply #2*).
- **Q5** recipes popup vs drawer vs tab — **popup**, decided by consistency
  with the `itemuses`/#318 provenance precedent (this is now a ruling, not an
  open gate).
- **Q6** Fact-tier inline iconId — real Phase-3 gap; carry to grammar gate.
  Reviewer lean: option (b) (same sprite primitive as Link, `IsLink=false`)
  to avoid a fork. Designer may argue (a).

## Deliverable for the revision

Re-emit the V0 with Apply #1/#2 and the Q1 ruling folded in, the spec card
and edge-rationale updated to match (Pools/Recipes rows → Confirmed; Q1 row →
InternalName-verbatim + footer-redundancy note), and Q2/Q6 left as explicitly
flagged Phase-4/grammar gates. Q3/Q5 move from "open gate" to "ratified" in
the spec card. The artifact remains design-bundle-only; only the updated
ratified spec is what Phase 4 encodes against.
