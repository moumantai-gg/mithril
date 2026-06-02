# Silmarillion #404 — Phase 5 fan-out + Phase 6 (orchestration handoff)

**Tracked in:** #404

> **Cold-start dispatch.** You are taking over orchestration of the #404
> visual-grammar program from a prior session. Everything you need is in this
> doc + the authoritative sources it points to. Do **not** try to reconstruct
> the prior conversation; it is fully captured on #404 and in-repo. Gates
> G1–G4 are **cleared**; the Recipe pilot is **merged**. Your job is the
> mechanical fan-out (8 views) then the Phase 6 guardrail.

## Status (what is already on `main`)

- Shared primitives: `Link`/`LinkVm`, `SetRef`/`SetRefVm`, `FactTable`/`FactTableVm`,
  `FactFooter`/`FactFooterVm`, `StructureLabelBehavior`, `FontSizeTimes` converter,
  the `#404` token block — all in `src/Mithril.Shared.Wpf/`.
- `docs/silmarillion-visual-grammar.md` — the G3-ratified grammar **incl.
  amend-1 (hybrid icons), amend-2 (em-relative sizing + Link `Density` +
  40px title-glyph), Phase-4 carry-forwards, decision log**.
- `src/Silmarillion.Module/Views/RecipeDetailView.xaml` (+ VM) — the **migrated
  Recipe pilot**: the worked, G4-cleared reference. **Read it first as the
  copy-template.**
- Gates: G1 (tiers) · G2 (matrix) · G3 (+amend-1/2) · **G4 (pilot)** — all
  CLEARED on #404. The Phase-1/2 element→tier matrices are #404 comments.

## Remaining work

1. **Phase 5 fan-out** — migrate the 8 remaining detail views, **one PR per
   view**, each a consistency-diff against the pilot (NOT fresh design).
2. **Phase 6** — add a conformance guardrail (test/analyzer) that fails the
   build if a detail view renders a raw bordered box for an entity reference
   instead of the shared primitive; then flip the visual-debt note in
   `docs/silmarillion-field-coverage.md` to "resolved" pointing at the grammar.

## The canonical pattern (copy verbatim per view — this is the whole job)

Derived from the merged pilot. For each view, classify every element via the
**Phase-1/2 matrix** (#404), then apply:

| Element kind | → Primitive / treatment |
|---|---|
| `EntityChip` (1:1 entity ref) | `c:Link`, DataContext via `LinkVm.From(EntityChipVm)`; `ClickCommand` wired exactly as the old chip (same `DataContext.OpenEntityCommand` RelativeSource). Ingredient-role refs: pass `LinkGlyph.Ingredient` explicitly. |
| `ItemSourceChip` (source/taught-by) | `c:Link` via `LinkVm.From(ItemSourceChipVm)` (provenance suffix from `Detail`). |
| Inline-in-a-sentence ref (requirement/dual-shape rows, "Giver:", "Area:") | `Link` with **`Density="Prose"`** + inline prefix as Structure. |
| Entity **enumeration** list (ingredients/produced/sources/“in this area”/teaches/sells…) | `Link` with **`Density="List"`**, `ItemsPanel` = vertical `<StackPanel/>`, per-item `Margin="0,0,0,3"`. |
| Keyword/filter/“view all N”/stacking chips | `c:SetRef`; summary-form sets `MatchCount` (renders `Label · N matches →`); positionally-material stacked slots set `SlotOrdinal` 1..N (renders ordinal **outside** the chip as a left-gutter marker) + section label `"… · N slots"`. |
| Stat-badge `Border` boxes | one inert `c:FactTable` (`FactTableVm.Strip`/`.Grid`/`.Scalar`); **label-value pairs** (e.g. `FactPair("Skill", value)`); phrase-form stats (MaxUses/Cooldown-like) keep `null` label; no gold, no box. Capacity-style polymorphic → Strip/Grid/Scalar (see carry-forward #3). |
| Section labels / inline field-prefixes / group headers | `Style="{StaticResource StructureSectionLabelStyle}"` / `StructureInlinePrefixStyle` / `StructureGroupHeaderStyle` — these now auto-apply UPPERCASE + ~0.08em tracking via `StructureLabelBehavior` (zero call-site work; keep the `:` on inline prefixes). |
| Title text / entity icon / footer ID | `FactTitleStyle` (×1.5em) · the entity icon adopts the **fixed-40px** title-glyph style (`NearestNeighbor`) · footer → `c:FactFooter` (`FactFooterVm.Key(InternalName)` = copyable; `EnvelopeKey`-style = inert) kept inside the `DetailExportHost` wrapper (no shared-infra surgery). |
| Description / prose / cost lines / mono stubs | `FactBodyStyle` (inert). `ResultEffects`/`TSysCraftedEquipment` stub: **leave as-is, #214-bound — do not restyle/move**. |

**Do NOT** delete `EntityChip`/`ItemSourceChip`/legacy styles — other unmigrated
views still use them until their PR. **Do NOT** touch the shared primitives or
`Resources.xaml` token/style blocks during fan-out (the pattern is already
encoded there; if a view needs something the primitive can't do, that's a
flag-and-stop, not an ad-hoc primitive edit).

## Binding decisions — ratified, do NOT re-litigate

- **"D · Context-aware" vertical-list is canonical**; the older single-export
  Orcpick HTML is **non-authoritative** (#404 correction). Reconcile only
  against the **current** handoff bundle's grammar doc / phase-3 harness.
- Density rule (Prose = inline-in-sentence; List = entity enumerations,
  own-line ×1.5em) — pinned.
- Set-ref summary = `Label · N matches →` (count-aware "1 match"); ordinal is a
  left-gutter marker **outside** the chip; `· N slots` suffix; recipe keyword
  slots are positionally material per the `TSysCraftedEquipment` canonical case.
- Stat strip = label-value (`Skill …`). Structure = UPPERCASE+tracking via the
  shared behavior. em-relative sizing; fixed-40px title-glyph.
- Accepted, recorded fidelity trade-offs (NOT defects, do not "fix"):
  hair-space tracking ≈0.06–0.1em (font-metric, em-scaling) vs exact 0.08em;
  `NearestNeighbor` sprite softness off integer scale.
- The harness **explainer captions** are pedagogy — do NOT ship them as UI.
- Out of scope: coverage **#407/#408**, the **#214** Effects-stub placement.

## Per-view notes (order: simplest→hardest; one PR each)

1. **PlayerTitle** — *no entity refs* (pure Fact/Structure/Control). Only:
   title→`FactTitleStyle`, scope badges→inert Fact (FactTable or body), tooltip
   →`FactBody`, footer→`FactFooter`, section labels→Structure. Smallest;
   good first to prove the non-chip path.
2. **Lorebook** · 3. **Area** · 4. **Effect** · 5. **Npc** · 6. **StorageVault**
   (polymorphic Capacity → FactTable Strip/Grid/Scalar; keyword tags = SetRef
   tag-form, *availability corollary* — never a grey Fact pill) ·
   7. **Ability** (largest; many chip sections + the dual-shape) ·
   8. **Quest** (dual-shape requirement/reward rows — mirror the pilot's
   `RecipeRequirementRowVm` wrapper approach; `EntityChip→LinkVm` is VM-side
   projection, the ratified pattern).

Use the **Phase-1/2 consolidated matrix on #404** as each view's
element→tier source of truth; the pilot VM (`RecipeDetailViewModel`) shows the
VM-projection idiom (`*Links`, wrapper VMs, `FactTableVm`, `FactFooterVm`,
`SetRefVm` w/ `SlotOrdinal`).

## Process & cadence (per view)

1. Branch off latest `main`; migrate ONE view (+ its VM/projection + tests).
2. Build isolated: `dotnet build src/Silmarillion.Module/...` and
   `src/Mithril.Shared.Wpf/...` (do **not** build `Mithril.slnx` for the inner
   loop — RG1000 BAML dup-key is a known stale-obj/parallel flake).
3. `dotnet test tests/Silmarillion.Tests` (+ Shared.Tests if touched) green;
   update VM tests to the new shape.
4. Guard: `git diff --name-only origin/main...HEAD` = only that view's files.
   **No other view, no primitive, no Resources.xaml.**
5. Full check: `scripts/start.ps1 -Build` → `Build 0W/0E` ·
   `XamlResourceLint OK` · `Application started`. **If `MSB3026/3027 "file is
   locked by Mithril (NNNN)"`: that is the known stale-process deploy-lock,
   NOT a compile error — `taskkill //F //IM Mithril.exe`, rebuild.** "tests
   green ≠ shipped" — the boot smoke + a live eyeball matter for XAML.
6. Commit locally, push, open ONE PR for that view; G4 = maintainer
   *consistency review vs the pilot*, not fresh design. Wait for merge before
   the next view (consistency is the acceptance bar; serial avoids drift).
7. WPF footgun: object/record bindings use `NullToVis`, never the string-only
   `NullOrEmptyToVis`.

## Anti-goals (violating these reproduces the debt)

1. Do not re-open the grammar or re-decide visual values — per-PR G4 is a
   consistency diff against the merged pilot.
2. Do not migrate >1 view per PR (per-view consistency-gate is the point).
3. Do not edit shared primitives/`Resources.xaml`/legacy controls during
   fan-out — flag-and-stop if a primitive gap appears.
4. Do not reconcile against stale single-artboard exports — current handoff
   bundle only.
5. Do not touch coverage (#407/#408) or the #214 Effects stub.

## Pointers

- Worked reference: `src/Silmarillion.Module/Views/RecipeDetailView.xaml` (+
  `ViewModels/RecipeDetailViewModel.cs`, `RecipesTabViewModel.cs`).
- Grammar: `docs/silmarillion-visual-grammar.md` (amendments + carry-forwards +
  decision log).
- #404: G1/G2/G3-CLEARED, G3 amend-1/2, the layout-pin reaffirmation, the
  authoritative-source correction, G4-CLEARED, Phase-1/2 matrices.
- Prior dispatches (provenance, all sibling files in this slug folder): `spec.md`
  (program plan), `phase3-design.md` (EXECUTED), `phase4-primitives.md` (EXECUTED).
