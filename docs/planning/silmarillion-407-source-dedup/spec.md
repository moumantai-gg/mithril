# Silmarillion #407 — own the declared-vs-reverse source-duplication audit, policy, and remediation

**Tracked in:** #407

> **Cold-start dispatch.** You own #407 end-to-end: a fresh, *cross-pane* audit
> → drive the coverage-policy decision to maintainer ratification → remediate
> the projection to conform → add a regression guard. Everything you need is in
> this doc + the authoritative sources it points to. Do **not** reconstruct any
> prior conversation — it is fully captured on #407 (issue body + the
> 2026-05-17 audit comment) and in-repo. This is the **coverage axis**, the
> deliberate sibling of the now-complete #404 **presentation** axis (grammar);
> the two were fenced apart on purpose. Do not re-open or re-touch the grammar.

## Read first, in order

1. **#407** — the issue body (the policy question + acceptance) **and the
   2026-05-17 audit comment**
   (`https://github.com/moumantai-gg/mithril/issues/407#issuecomment-4470737861`):
   it enumerates the two `ItemDetailView` overlap pairs, the data scale, the
   partial-overlap subtlety, and a **proposed** policy. That proposal is an
   input, **not** a ratified decision — see "The decision is not yours" below.
2. `docs/silmarillion-field-coverage.md` — **owns the coverage axis.** Read
   §"Visual grammar (#404)" (note it is RESOLVED — presentation only; #424
   closed the shared `ItemDetailView` grammar) and §"Acting on this doc". This
   file is where the ratified #407 policy gets written; #407's acceptance is
   literally "a decided, documented policy in this file, projection conformed".
3. `docs/silmarillion-visual-grammar.md` — only for the axis-separation framing
   ("Coverage axis — #407/#408 explicitly out of scope"). **Do not migrate
   against it or re-decide any grammar value.** Grammar is done.
4. The just-merged grammar baseline so you know the *rendered* shape you must
   not disturb: PR #427 / #424 (the migrated `src/Mithril.Shared.Wpf/ItemDetailView.xaml`
   + the additive carriers in `ItemDetailViewModel.cs`) — the structural sibling
   of this effort, same blast-radius discipline. (The original dispatch doc was
   scratch and never landed in the repo; see the PR description for the design.)
5. The projection code (where the duplication is *produced*, and where the fix
   belongs — **not** XAML):
   - `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs` —
     `BuildCrossLinkContext`, `BuildSourceChips`, `ResolveSourceReference`,
     `BuildAwardedByQuestChips`, `BuildRecipeChips`, `BuildConsumedBy*`.
   - `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs` /
     `ItemDetailContext.cs` — the carriers + the additive-discipline contract.
   - The reverse indices + the declared model:
     `IReferenceDataService.{RecipesByProducedItem,QuestsRewardingItem,ItemSources}`,
     `Mithril.Shared/Reference/ItemSource.cs` (`(Type, Npc?, Context)`).
   - The other Silmarillion detail panes + their VMs (the audit must NOT be
     `ItemDetailView`-only — see scope): `src/Silmarillion.Module/Views/*DetailView.xaml`
     and their `*DetailViewModel` / the tab VMs that build them
     (`RecipesTabViewModel`, `NpcsTabViewModel`, …). Note `RecipeDetailView`'s
     "Taught by" rides `sources_recipes.json` (`RecipeSources`) — a candidate
     for the same declared-vs-reverse class against any recipe→source reverse.
6. The tests that pin the current projection contract:
   `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs` (asserts the
   legacy chip/string members + the `Consumed*`/popup shape) and any
   `*DetailViewModelTests`.

## What is already known (the prior audit — re-verify, do not trust blind)

The 2026-05-17 comment found, for `ItemDetailView` only, **two** overlap pairs
of one structural class (a declared `ItemSource` whose typed `Context` resolves
to an entity that *also* has its own reverse-lookup header):

| Declared "Sources" kind | Reverse twin header | Scale (v470) |
|---|---|---|
| `Recipe` → `EntityRef.Recipe(Context)` | **Produced by** (`RecipesByProducedItem`) | 4135 entries / 2736 items — the **dominant** case |
| `Quest` → `EntityRef.Quest(Context)` | **Awarded by** (`QuestsRewardingItem`) | 861 / 351 — #407's originally-cited (MetalSlab1) case |

NPC-anchored / plain-text source kinds have **no** reverse twin in that pane
and are not duplicated there.

**Re-verify this yourself** (scope-disproof discipline — an over-scoped negative
presented as verified is worse than an honest gap):

- Re-run the `sources_items.json` type tally and the counts; confirm v-current.
- Confirm the overlap is *partial*, not total: the two sides come from
  different JSON, so quantify declared-only / reverse-only / in-both for both
  pairs (a declared-only entry with no reverse twin is a real data-coverage
  signal, not a dedupe target — it must survive).
- **Extend the audit cross-pane.** #407's title is "Silmarillion detail panes",
  not `ItemDetailView`. Enumerate the same class across *every* detail
  pane/tab VM (Recipe "Taught by" vs any recipe-source reverse; Npc; Area;
  etc.). Produce the complete pair table before proposing the fix surface. Do
  not assume `ItemDetailView` is the only locus just because it was the example.

## The decision is not yours — drive it to ratification

#407's acceptance is an explicit *policy decision*, owned by the maintainer
(it is a coverage-axis call, not a mechanical fix). Your job is to make the
decision **well-posed and cheap to ratify**, not to pick it unilaterally:

1. Post the completed cross-pane audit to #407 (table + partial-overlap
   quantification).
2. Present the policy options crisply (use `AskUserQuestion`-style framing in
   session; on the issue, lay out the options + a recommendation). The standing
   recommendation to react to: *suppress a declared `ItemSource` row when its
   typed `Context` resolves to an entity already shown under its dedicated
   reverse-lookup header; "Sources" keeps only kinds with no reverse twin;
   declared-only entries (no reverse twin) survive and their asymmetry is
   flagged as a data-coverage signal; the `Quest:`/`Recipe:` kind-prefix stops
   being load-bearing for deduped kinds.* Open sub-questions to force a
   decision on: which header wins when both are non-empty; is the kind-prefix
   ever load-bearing; how to treat a declared-only entry; does the policy
   differ per pane.
3. Get explicit maintainer sign-off, then **write the ratified policy into
   `docs/silmarillion-field-coverage.md`** (it owns the coverage axis) before
   touching projection code. Doc-first: the issue's acceptance is the doc + the
   conformed projection, in that order.

## Remediation (only after the policy is ratified)

- Fix at the **projection layer** (the `*TabViewModel` builders /
  `ItemDetail*` carriers), **never** the XAML or the grammar primitives — the
  rendered grammar is correct and frozen (#424 closed it). The duplication is a
  data-projection decision, not a presentation one.
- **Additive-discipline contract (load-bearing).** The legacy chip/string
  members (`Sources`, `ProducedByRecipes`, `AwardedByQuests`, `Consumed*`, …)
  and their grammar carriers are asserted by `ItemsTabViewModelTests` and are
  the cross-module detail contract (Bilbo / Celebrimbor / `ItemDetailWindow`
  popups consume `ItemDetailViewModel`). Conform *without* silently breaking
  them; if the dedupe legitimately changes a member's contents, update the
  asserting tests **deliberately and visibly**, and call the contract change
  out in the PR — do not let a behavioural change ride in unannounced.
- Handle partial overlap exactly per the ratified policy; preserve declared-only
  entries; do not regress the #318 "Used in/Used as" popup counts (those are a
  different role — consume, not source — and are *not* a dedupe target).
- Add a **regression guard** analogous in spirit to the Phase-6
  `DetailViewGrammarConformanceTests` but for the coverage policy: a test that,
  over a representative corpus, asserts no entity appears under two
  source-family headers in the same pane unless the ratified policy explicitly
  allows it. Without a guard this silently rots back (xunit never renders the
  pane; the duplication compiles + passes today).

## Anti-goals (violating these reproduces the debt or breaches axis separation)

1. Do **not** re-open / re-touch the #404 grammar, the Phase-4 primitives,
   `Resources.xaml`, or any `*DetailView.xaml`. This axis is data/projection
   only. Presentation is RESOLVED.
2. Do **not** decide the policy unilaterally — ratification is the maintainer's;
   you make it well-posed and document the ratified outcome.
3. Do **not** ship the audit's prior negative ("only `ItemDetailView`, only two
   pairs") as fact — that was an `ItemDetailView`-scoped check. Re-derive
   cross-pane; enumerate plausible carriers before generalizing.
4. Do **not** break the additive contract silently; do **not** fold in the dead
   `RequirementChipVmConverter.cs` cleanup (#422 net-zero, separate) or any
   unrelated work.
5. Do **not** fold this into PR #427 or any grammar PR — it is its own PR(s),
   off latest `main`, after the policy lands in the doc. Doc-flip + projection
   may be one PR or split (audit-comment first, then a doc+code PR).

## Process & verification

1. Branch off latest `main` (never commit to `main`; feature branch + `gh pr
   create`). Squash-merge orphan caveat: if this is doc-then-code, prefer the
   add-then-act split (don't net-zero a single squashed PR).
2. Inner loop: isolated builds (`Mithril.Shared.Wpf` + `Silmarillion.Module` +
   a cross-module consumer e.g. `Bilbo.Module`); don't build `Mithril.slnx`
   for the inner loop (RG1000 BAML dup-key is a known stale-obj flake).
3. Full check: `dotnet build Mithril.slnx` → **0W/0E** · `XamlResourceLint OK`;
   `dotnet test` green for **all** item-detail consumers, not just
   Silmarillion: `tests/Silmarillion.Tests`, `tests/Mithril.Shared.Tests`,
   `tests/Bilbo.Tests`, `tests/Celebrimbor.Tests`.
4. `scripts/start.ps1 -Build` → `Build 0W/0E` · `XamlResourceLint OK` ·
   `Application started`. If `MSB3026/3027 "file is locked by Mithril (NNNN)"`:
   stale-process deploy-lock — `taskkill //F //IM Mithril.exe`, rebuild.
5. **Live eyeball (cannot be done headless — flag it owed in the PR):** open an
   item that carries *both* a declared `Recipe`/`Quest` source *and* the
   matching reverse edge (the audit will surface concrete internal names) and
   confirm it now appears under exactly one header per the ratified policy; spot
   the declared-only case still appearing in "Sources".
6. One coherent PR per the doc-vs-code split; `Tracked in: #407`; acceptance =
   maintainer review that the projection conforms to the documented policy.

## Pointers

- Issue + prior audit: #407 + comment `…issues/407#issuecomment-4470737861`.
- Coverage-axis owner doc: `docs/silmarillion-field-coverage.md` (write the
  ratified policy here).
- Structural sibling (the just-merged grammar effort, same blast-radius
  discipline): #424 / PR #427 (dispatch doc was scratch — not in repo).
- Projection: `Silmarillion.Module/ViewModels/ItemsTabViewModel.cs`,
  `Mithril.Shared.Wpf/ItemDetail{ViewModel,Context}.cs`,
  `Mithril.Shared/Reference/ItemSource.cs`,
  `IReferenceDataService.{RecipesByProducedItem,QuestsRewardingItem,ItemSources,RecipeSources}`.
- Contract tests: `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs`.
