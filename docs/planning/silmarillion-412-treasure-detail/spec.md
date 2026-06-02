# Ratified visual spec — Silmarillion Treasure System detail views

**Tracked in:** #435 (implement Power + Profile detail views).
Design ratified via #433 (commission brief) + #434 (gate-review directive);
Claude Design's V1 passed the gate clean, owner-ratified 2026-05-17.

> This is the repo-resident lift of the **ratified V1 spec**. The V1 HTML
> specimen is a design-bundle artifact and is **not** committed (per the #433
> brief's rule: *only the ratified spec returns to the repo*). #433/#434 carry
> the rulings narrative; this doc is the per-region grammar contract Phase 4
> (the #435 build) encodes against, so the spec isn't bundle-trapped. It is the
> source of truth for the Power/Profile detail panes; do not re-derive grammar
> calls — cite [`silmarillion-visual-grammar.md`](../../silmarillion-visual-grammar.md)
> (G3 + G-d, binding) and this doc.

## Implementation amendment — Recipes leg deferred to #214 (2026-05-18)

The ratified design's `Power → Recipes` chain (`recipe → template.TSysProfile →
profile → power`, rows marked **Recipes disclosure** / **Power → Recipes**
below) was **verified data-invalid for the in-scope index** during the #435
build and is **deferred to #214**:

- `Item.TSysProfile` *is* populated and *does* match `tsysprofiles` keys
  (`Sword`/`NewbSword`/`Dagger`/… verified v470) — the **Pools** links are
  real and shipped.
- But the recipe hop is not: `RecipesByProducedItem` only indexes recipes by
  `ResultItems`/`ProtoResultItems`. TSysProfile-bearing items that *are*
  recipe-produced are almost all `Crafted*` gear carrying the **catch-all
  `"All"` profile** (which contains ≈ the entire power catalogue, `SwordBoost`
  included). So the chain resolves to the *same* enormous
  "every enchanted/max-enchanted crafted-gear recipe" set for nearly every
  power — presenting it as power-specific implies precision the in-scope data
  lacks (correctness-adjacent to the roll-resolution ban). Loot/Stock
  TSysProfile items are not recipe-produced at all.
- The power-precise join is recipe `ResultEffects`
  (`AddItemTSysPower` / `ExtractTSysPower` / `TSysCraftedEquipment`) via
  `ResultEffectsParser` — the **#214** surface (#433 Carry-forward #2 bound
  recipe-side rendering to #214).

**Net for #435:** no Recipes section is rendered; no `ItemsByTSysProfile`
index ships; only `ProfilesByPower` (Pools) ships. The **Recipes disclosure**
spec-card row and the **Power → Recipes** cross-link row below are retained as
the *ratified intent* but are explicitly **NOT built here** — they are #214's.
Q4/Q5 remain ratified as the design contract for when #214 supplies the
precise index.

## Data shapes (verified live v470, 2026-05-18)

- `tsysclientinfo` — 1946 entries, keyed `power_NNNN`. Fields are exactly
  `InternalName`, `Prefix`, `Suffix`, `Skill`, `Slots`, `Tiers` (plus the
  parser-only `IsUnavailable` / `IsHiddenFromTransmutation`). **No
  `DisplayName`; `resolve_strings` returns null for every candidate key.**
  `Tiers` is keyed `id_N`; each tier carries `MinLevel`, `MaxLevel`,
  `MinRarity`, `SkillLevelPrereq`, `EffectDescs`. `SwordBoost` (`power_1001`):
  12 tiers, contiguous **overlapping** level bands (1–20 … 100–140),
  monotonically scaling `{MOD_SKILL_SWORD}` 0.10 → 0.60, `MinRarity` constant
  `Uncommon`. Some powers carry only a `Prefix`, some only a `Suffix`, some
  both. `EffectDescs` is polymorphic: `{TOKEN}{value}` template **or**
  already-rendered prose with inline `<icon=N>`.
- `tsysprofiles` — 40 entries, each a flat JSON array of power `InternalName`
  strings. The profile name is the **equipment family the pool rolls onto**,
  NOT the powers' own `Skill` (the `Sword` pool carries `BardMaxHealth`,
  `WerewolfMaxHealth`, …). Two orthogonal skill axes — never conflate
  `Power.Skill` with the pool name.

## Power detail · spec card (every region → exactly one ratified tier)

| Region | Tier | Pigment | Glyph | Shape · spacing | Hover |
|---|---|---|---|---|---|
| **Power name** | Fact · title-loud | `--accent`, 1×/pane | none (the icon slot is the 40px title-glyph, not a glyph) | Cambria 18pt 600, no border. **`InternalName` verbatim — no humanization, no affix-derivation** (Q1; affix logic is non-replicable engine-side) | none |
| **Affix flavor** | Fact · body · *illustrative only* | `--fg-quaternary` framing prose · `--fg-secondary` affix tokens · `--fg-quaternary` mono «item» slot | none | conditional render: **only the affix parts that exist** (Prefix-only / Suffix-only / both); framed approximate ("appears on items roughly as …", "— illustrative, not the canonical name"); **never** a reconstructed canonical item name | none |
| **Skill row** | Structure + Link | label `--fg-tertiary` · Link name `--accent` · `sparkles` `--accent` | `sparkles` Lucide (abstract entity) | inline-prefix `Skill:` · Link no surface · 2px tap pad | 10% gold tint on Link · **Confirmed** (Degraded only if the Skills surface is unshipped at build) |
| **Slots row** | Structure + Set-reference | chip text `--info` · idle fill `rgba(30,58,95,.30)` | none (Set-ref default) | `--radius-sm` · 1px blue border · 1×8 pad · `t-set-row` gap 5px | fill darkens / border brightens · **not active by default** (constraint members, not a live filter) |
| **Tiers ladder** | Fact · CF#3 FactTable | values `--fg-primary` · labels `--fg-quaternary` · constant-rarity cells dimmed to `--fg-quaternary` in place · band fill `--info` 55% · **Rare rows: `--info` + 1px inset/white outline (weight, NOT `--accent`)** — G-b forbids a third gold meaning; reconcile with rarity scheme #54 in a Phase-4 follow-up | none in body · inline iconId baseline-aligned within rendered `EffectDescs` | 6-column grid · no surface · 1px `--border-faint` row dividers · tabular numerals · 24px ordinal rail · 72px band rail · effect col 1fr · row hover = 2% white wash (Fact-body affordance, not a Control) | row tint only · **no row-click action** (ladder is inert; navigation is the Skill/Pool Links above) |
| **Pools section** | Link · **Confirmed** | `--accent` gold name · standard Link, no dashed underline, no tail | `layers` Lucide (abstract membership) | no surface · 2px tap pad · 5px gap between siblings | 10% gold tint · vanilla Link hover · *`tsysprofiles` is the authoritative join — G-d Unconfirmed does NOT apply to one-side normalized FKs (precedent on #404)* |
| **Recipes disclosure** | Control · contents Link Confirmed | chassis `--bg-surface` · border `--border-subtle` · label `--fg-primary` | optional `list` Lucide 12px lead | `--radius-md` · 1px border · 5×12 pad · button chassis · popup-on-click (per `itemuses`/#318 — ratified) | `--bg-surface-hover` · `--border-strong` · click opens provenance **popup** of **Confirmed** Links |
| **Footer ID strip** | Fact · footer-quiet · G-a | `KEY` label `--fg-quaternary` · value `--fg-tertiary` mono | `copy` Lucide 11px revealed on hover (KEY only); none on ROW | below 1px `--border-faint` divider · 14px gap · mono 9.5pt | KEY: row tint + copy-cue, click copies · ROW: `cursor:default`, no hover (storage-only) |

**Q1 consequence (kept deliberately):** the G-a footer `KEY` is also
`InternalName`, so the footer text duplicates the Fact-title text. **Keep the
strip regardless** — it carries the *copy affordance*, which the title does
not (Powers have no second identity). Treat the redundancy as intended; Phase 4
must **not** "fix" it by removing the footer.

## Cross-link state — one row per edge

| Edge | State | Why |
|---|---|---|
| `Power.Skill` → Skill | **Confirmed** | Direct single-valued ref into the Skills surface. Vanilla Link. Degraded (G-c) only if the Skills tab is unshipped at build time. |
| `Power.Slots` → equip slots | **Set-reference** | Not a navigation edge — equip slots aren't a browsable entity. `["Head","MainHand"]` is a constraint set; tag-form Set-ref. |
| Power → Pools containing it | **Confirmed** | Each pool is a browsable entity (Profile detail) and an authoritative `tsysprofiles` entry. One-side normalized FK — verifiable O(1); the inverse data *is* the edge. Vanilla Confirmed `layers` Links. *V0 had this Unconfirmed; corrected by gate.* |
| Power → Recipes that can roll it | **Confirmed**, behind a Control disclosure | Chain `recipe → template.TSysProfile → profile → power` — indirect & 1:N but every hop is an authoritative normalized lookup. Disclosure is a *density* call (Control button → popup), not a confidence call (per `itemuses`/#318). Popup contents are Confirmed Link-tier. |

**G-d does not apply to this pane.** Unconfirmed is reserved for the #407-class
case (an edge one side asserts that the inverse data does not substantiate). A
one-side normalized foreign key is how *every* reverse-index in Silmarillion
works (items↔recipes too) — if a normalized join were Unconfirmed, every
reverse-lookup would degrade and G-d would void. Pools/Recipes are Confirmed.

## Ruled gate cells (the receipt for Phase 4)

- **Q1 · ruled** — Fact-title = `InternalName` verbatim. No humanization, no
  PascalCase split, no `resolve_strings` (#405 presupposes a resolvable
  DisplayName; Powers have none → #405 out of domain). **Affix-as-title is
  foreclosed** (non-replicable engine-side logic — same error class as
  inventing roll-resolution). Footer-redundancy kept deliberately (above).
  Affix stays Fact-body, illustrative, only-present-parts.
- **Q3 · ratified** — the 72px band micro-chart dual-encodes
  `MinLevel…MaxLevel` (graphically + numerals); legitimate dual-encoding of
  one Fact **now that the Rare-band gold is off** (Rare = `--info` + inset).
- **Q4 · ruled (precedent on #404)** — Pool/Recipe edges are **Confirmed**, not
  Unconfirmed. `tsysprofiles` is the authoritative normalized join.
- **Q5 · ratified** — Recipes provenance = **popup** (per `itemuses`/#318);
  drawer / dedicated-tab options closed.
- **Q2 · resolved → Option A** (ruling on #435/#404; revisit tracked #436):
  the CF#3 ladder uses **whole-table-shape polymorphism only** — grid (N≥2) /
  N=1 inline scalar / N=0 hidden — with constant cells **dimmed in place**. Do
  **not** build per-column hoist.
- **Q6 · resolved → reuse `IconImage`** (ruling on #435/#404): inline
  EffectDescs `<icon=N>` renders via the existing `Mithril.Shared.Wpf/IconImage`
  control backed by `IIconCacheService`, hosted ~1em baseline-aligned,
  copy-safe. It is **not** a Link (no gold / nav / affordance). No new icon
  primitive. (In this build `EffectDescsRenderer` already lifts `<icon=N>` →
  `EffectLine.IconId` and strips the markup from the visible/copyable text; the
  shared `EffectLineTemplate` renders that `IconId` through `IconImage` — the
  ratified mechanism, no stray glyph in copied text.)

## Profile / Pool detail (lighter pass)

- Fact-title = pool name (e.g. `Sword`). Footer KEY = pool name (copyable).
- Fact body must make the **two orthogonal skill axes** explicit: the pool
  name is the *equipment family the pool rolls onto*, **not** the powers'
  own skills.
- `Drawn into <Skill> family items` — a Skill Link (Confirmed; Degraded if
  Skills unshipped).
- `Filter by Power.Skill` — tag-form Set-references + a free-text filter,
  labelled *orthogonal to pool name*.
- Powers list — navigable Confirmed Links (one per power; `Power.Skill` shown
  per row as inert Fact; tier count inert Fact). Large pools (Sword ≈ 270);
  list is virtualized + filterable by `Power.Skill`.

## Hard constraints (data-availability ceilings — non-negotiable)

- **No roll-resolution UI.** No drop odds / expected value / "chance to roll".
  The data does not exist; an affordance implying odds is a *correctness*
  failure, not a polish gap.
- **Crystal-free.** Crystal→enchantment is a recipe-crafting concern, absent
  from `tsysclientinfo`/`tsysprofiles`.
- **Not a calculator.** Silmarillion is a browser; no inputs that compute.
- **`TreasureCartography` is a different system** (buried-treasure maps,
  `Create*TreasureMap*`) — excluded entirely. See `pg_treasure_term_overload`.
- **Affix is illustrative only** — never a deterministic player-facing
  identity. See `pg_tsys_power_naming_ceiling`.
