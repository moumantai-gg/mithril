# Claude Design brief — Silmarillion "Treasure System" detail view

**Tracked in:** #412 (Treasure System tab — Power catalog + Profile pools).
Consumer dependency: #214 (recipe detail deep-links into this tab).

> This is a **design prompt**, not an implementation spec. It commissions a
> V0 artboard + ratifiable visual spec for the detail pane of the #412 tab,
> to be produced by Claude Design under the #404 gated-ownership model. The
> data shapes below are verified against v470; the visual grammar is the
> ratified contract. Hand this file to Claude Design as the brief.

---

## Role & deliverable

You are **Claude Design**, the designer in Mithril's #404 gated-ownership
model. Produce a V0 design for the **Power** detail view of Silmarillion's
new Treasure System tab, plus a lighter pass on the **Profile/Pool** detail.

Deliverable shape — identical to the #404 Phase-3 work:

1. A live HTML specimen (A/B: your V0 vs. a deliberately-naïve strawman so
   the choices are legible), per-region annotations.
2. A prose **spec card** naming every visual call (pigment / glyph / shape /
   hover / weight) per region, in the grammar's vocabulary.
3. A short rationale for each non-obvious decision, and an explicit list of
   any cells the grammar does **not** already settle (so they go to a gate,
   not improvisation — that is the failure mode #404 exists to prevent).

The specimen harness is a design-bundle artefact, **not** committed to the
repo. Only the ratified spec returns to the repo.

## Hard design contract — do not re-derive

The five-tier visual grammar is **ratified and binding**. Read the live
`docs/silmarillion-visual-grammar.md` as authoritative — it has advanced
through **G-d** (Link reference-state axis: Confirmed · Degraded ·
**Unconfirmed**, #432). Design strictly within it. Do not reproduce or
re-decide grammar rules; cite them. Bindings that will specifically bite
this pane:

- **G-b (gold discipline):** gold = *Fact-title* (Cambria serif, exactly one
  per pane, no glyph) **or** *Link* (Segoe, body weight, 12px lead Lucide
  glyph). The Tiers ladder is dense with numbers and proper-noun-ish tokens —
  **none of them are gold.** Effect values, level numbers, rarity words →
  plain `--fg-primary`. Proper nouns that aren't navigable → plain text.
- **Link primitive:** `<12px lead glyph> <gold name>`, no box, no surface at
  rest. Skill cross-link → `sparkles` glyph. The **G-c degrade** rule (rest
  identical, hover swaps to copy-cue) applies to any link whose target tab
  isn't shipped; the **G-d Unconfirmed** state (gold + dashed underline,
  `· unconfirmed` tail, caveat tooltip; additive `IsUnconfirmed`, orthogonal
  to degrade) applies to a declared edge the *inverse* data doesn't back —
  see "Cross-links" below for exactly where this bites.
- **Set-reference:** blue `--info` chip, never gold. Group / keyword /
  membership lives here, in summary-form or tag-form (one primitive).
- **Footer ID (G-a):** `InternalName` (`power_1001` / `SwordBoost`) lives in
  the mono footer ID strip below a thin divider — uppercase `KEY` label,
  Consolas `--fg-tertiary`, hover-reveal copy glyph. **Not** in the header
  subtitle (matches the established entity-detail footer convention).
- **Carry-forward #3** (one polymorphic `FactTable` primitive that also
  degrades to a flat scalar) is the intended vehicle for the Tiers ladder —
  design the ladder to fit that primitive, not a bespoke control.
- **Carry-forward #2:** `TSysCraftedEquipment(…)` Effects-stub placement is
  bound to #214 — out of scope here; do not design recipe-side rendering.

## The data — design to THIS shape (verified v470), not an idealisation

### `tsysclientinfo` — the **Power** entity (primary pane)

Keyed `power_NNNN`. Representative record:

```json
"power_1001": {
  "InternalName": "SwordBoost",
  "Prefix": "Swordsman's",
  "Suffix": "of Swordsmanship",
  "Skill": "Sword",
  "Slots": ["Head", "MainHand"],
  "Tiers": {
    "id_1":  { "MinLevel": 1,   "MaxLevel": 20,  "MinRarity": "Uncommon", "SkillLevelPrereq": 1,   "EffectDescs": ["{BOOST_SKILL_SWORD}{5}"] },
    "id_2":  { "MinLevel": 15,  "MaxLevel": 40,  "MinRarity": "Uncommon", "SkillLevelPrereq": 15,  "EffectDescs": ["{MOD_SKILL_SWORD}{0.1}"] },
    "id_3":  { "MinLevel": 30,  "MaxLevel": 50,  "MinRarity": "Uncommon", "SkillLevelPrereq": 30,  "EffectDescs": ["{MOD_SKILL_SWORD}{0.15}"] },
    "…":     "… ladder continues to ~id_12: contiguous, OVERLAPPING level bands, monotonically scaling value"
  }
}
```

Shape facts the design must absorb:

- **The Tiers ladder is the central design problem.** ~12–13 tiers per power.
  Each tier = a level band (`MinLevel`–`MaxLevel`, **overlapping** between
  adjacent tiers — not a clean partition), a `SkillLevelPrereq`, a
  `MinRarity` (usually constant `Uncommon` but can vary by tier), and
  `EffectDescs`. The value typically scales monotonically (`{5}` → `{0.6}`).
  How to make a 13-row progression scannable without it reading as a
  spreadsheet, while staying Fact-inert (no gold, per G-b), is the brief's
  hardest call. It should resolve to the carry-forward #3 `FactTable`
  primitive's shape.
- **`EffectDescs` is polymorphic.** Either a `{TOKEN}{value}` template
  (resolved by the existing `EffectDescsRenderer` — assume it yields rendered
  text plus optional inline icon ids) **or** already-rendered prose with
  inline `<icon=N>`, e.g.
  `"<icon=108>All sword abilities deal +3% damage when you have 33% or less
  of your Armor left"`. Both render as Fact body — no gold, inline icon
  inlined with the text baseline.
- **`Prefix` / `Suffix` are item-name affixes.** An item bearing this power
  reads as *"Swordsman's «item» of Swordsmanship"*. This is strong flavour
  identity — surface it (it answers "what does this look like in-game?"), as
  Fact, not as a Link.
- **`Slots`** = equip slots the power can land on. Small set; Set-reference
  tag-form is the natural fit (it's a constraint set, not navigation).

### `tsysprofiles` — the **Profile / Pool** entity (secondary pane)

40 entries, each a **flat JSON array of power `InternalName` strings**:

```json
"Sword":   ["AcidArrowBoost", "BardMaxHealth", "SwordBoost", "WerewolfMaxHealth", "…~270 entries, CROSS-SKILL"],
"CowFeet":  ["ArmorRepairBoost", "FrontKickBoost", "…~95 entries"]
```

Shape facts:

- A Profile is a **named pool of powers**, mostly large (Sword ≈ 270).
- The pool name is the **equipment family the pool rolls onto**, NOT the
  powers' own skills — the `Sword` pool contains `BardMaxHealth`,
  `WerewolfMaxHealth`, etc. **Two orthogonal skill axes; never conflate
  `Power.Skill` with the pool name.** The Profile detail is essentially a
  large, navigable, **filterable-by-`Power.Skill`** list.

## The design problems to solve (Power pane)

1. **The Tiers ladder.** The defining challenge. Dense, overlapping bands,
   scaling effect text. Make progression legible at a glance; Fact-inert;
   FactTable-primitive-shaped. Decide: what is the row, what columns earn
   their width, how `EffectDescs` (often a full sentence) coexists with the
   numeric band/rarity/prereq without becoming a wall.
2. **Identity header.** Power name as the single gold Fact-title (Cambria).
   The `Prefix … Suffix` affix as flavour Fact. `Skill` as a Link
   (`sparkles`). `Slots` as Set-reference tag-form.
3. **Cross-links — and where G-c/G-d bite.**
   - `Skill` → Link to the Skills surface. If Skills isn't a navigable tab,
     it is **Degraded** (G-c), not dimmed.
   - **Pools containing this power** → many. A Profile is a browsable entity
     (it has its own detail), so each is a Link — but "member of N pools" is
     also set-membership. Resolve per grammar (Link vs. Set-reference list);
     name the call and the rationale. If the membership is declared only on
     the `tsysprofiles` side and the power record doesn't back-reference it,
     that edge is **Unconfirmed** (G-d) — design it as such, do not silently
     present it as Confirmed.
   - **Recipes that roll this power** (power ← profile ← template
     `Item.TSysProfile` ← recipe). Likely deep / 1:N → a provenance popup,
     not inline chips. This edge is indirect; consider whether it is
     Confirmed or Unconfirmed under G-d and state your reasoning.
4. **Footer.** `power_NNNN` + `InternalName` in the G-a mono ID strip.

## Hard constraints / anti-goals

- **No roll-resolution UI. At all.** Probabilities, drop odds, "chance to
  roll this", expected-value — these data **do not exist** in CDN data
  (verified; same class as item drop rates). The pane shows what the system
  is *composed of*, never how a roll resolves. Designing any affordance that
  implies odds is a correctness failure, not a polish gap.
- **No crystals.** Crystal→enchantment-family is a recipe-crafting-path
  concern; `tsysclientinfo`/`tsysprofiles` contain none. This pane never
  shows crystal UI.
- **Not a calculator.** Silmarillion is a browser. No inputs that compute.
- **No new tier outside the five.** If something seems not to fit Fact /
  Control / Link / Set-reference / Structure, that is a gate question — list
  it, do not invent.
- **`TreasureCartography` is a different system** (buried-treasure maps) —
  not this tab, not this pane. Ignore entirely.

## Acceptance for the design

- Every region classified into exactly one ratified tier, cited.
- The Tiers ladder resolves to the carry-forward #3 polymorphic FactTable
  shape (or a reasoned gate request to extend it).
- Gold appears exactly where G-b permits and nowhere else.
- Cross-link states correctly assigned across the Confirmed / Degraded
  (G-c) / Unconfirmed (G-d) axis, with rationale per edge.
- Zero affordance implies roll resolution.
- Open cells (if any) enumerated as explicit gate questions.
