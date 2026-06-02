# Feedback on the Effects tab handoff (#244 → PR #298)

Implementation feedback for [`silmarillion-244-effects-tab.md`](silmarillion-244-effects-tab.md) and the [cookbook](../../silmarillion-tab-cookbook.md), gathered during execution.

## What the plan got right

- **The "four wrong assumptions" pre-flight.** Saved at least one full PR worth of churn — both the items-effects join and the `ResultEffectsParser` reuse would have rabbit-holed. Worth pinning as a template section for future tab handoffs ("review the original issue body for stale assumptions before scoping").
- **Slice-shaped commit plan (a → e).** The (a) service plumbing / (b) audit-pass migrations / (c) skeleton / (d) sections / (e) tests boundary was clean. Each commit reviewable in isolation; no rebase pain.
- **Explicit trigger matrix for `BuildEffectAbilityCrossLinkIndices`.** Naming "which file refreshes trigger which index" upfront meant zero post-merge regressions on background-refresh paths.
- **The `Effect.InternalName` lift was scoped correctly as part of the PR**, not deferred. The downstream symmetry (kind targets, deep-link, resolver) wouldn't have been clean without it.

## What the plan missed

### 1. Stacks-with cardinality was unbenchmarked

The plan's Section 6 design ("Stacks with — chip per peer effect, EntityRef.Effect anchored") didn't measure peer-cluster sizes against bundled data. Actual sizes:

```
326 Food
190 Snack
 61 Instant
 54 CandleAppreciation
 48 Augury
```

Rendering 326 chips was unscannable. Two review iterations were needed — first to collapse to a single chip per StackingType (mirroring #259's keyword-collapse precedent), then to fold the chip into the metadata strip's "Stacking type:" row instead of a separate Section 6.

The plan's "Open question 3" already discussed cardinality but only for the `Keywords` chip strip; StackingType wasn't included in the same analysis. **Cookbook addition: any single-value field that drives a peer cluster (StackingType, AbilityGroup, Skill, EquipmentSlot, etc.) needs cardinality measurement before locking the chip-vs-collapse design.** One-liner:

```bash
grep -oh '"<Field>": "[^"]*"' src/Mithril.Shared/Reference/BundledData/<file>.json | sort | uniq -c | sort -rn | head -10
```

### 2. The `NullOrEmptyToVis` → `NullToVis` chip-converter footgun is invisible to tests

`NullOrEmptyToVisibilityConverter` does `value as string` — for an `EntityChipVm` (or any non-string reference type) that's null, so the converter always returns `Collapsed`. Every chip-typed `Visibility` binding using this converter hides regardless of chip value.

This bit me on `StackingTypeChip` after I folded the chip into the metadata strip. Existing chip surfaces have the same silent bug — `PrerequisiteChip`, `UpgradeOfChip`, `SharesResetTimerWithChip` on `AbilityDetailView`, `RepeatabilityChip` / `GiverChip` / `TurnInChip` on `QuestDetailView`, `SkillRequirementChip` on `RecipeDetailView`. Filed as [#301](https://github.com/moumantai-gg/mithril/issues/301).

**Cookbook addition: "Chip-typed `Visibility` bindings need `NullToVis`, not `NullOrEmptyToVis`. The latter accepts string-typed bindings only and silently collapses every other reference type."** Worth adding a build-time `XamlResourceLint` rule that flags `Binding *Chip*, Converter=NullOrEmptyToVis` patterns.

### 3. Real-bundled-data walk caught structural correctness but not visual rendering

The `RealBundledEffects_KnownEntries_ProjectSensibly` test asserts envelope-key lift, populated InternalName, meaningful entry count. It did NOT catch:
- The `NullOrEmptyToVis` silent-collapse bug (no view rendering involved)
- The "Section 6 redundant with Section 3" UX problem (no human in the loop)
- The "326-chip wall" problem (real data needed, but synthesised fixtures hid it)

The user's manual smoke walk (effect_9230 Bonus XP screenshot) caught what the test couldn't. **Cookbook strengthening: real-data walk should explicitly include "pick at least one entry with maximum-cardinality cluster membership for each cross-link section" — e.g. for Effects, pick an entry from `StackingType=Food` to see what the Stacks-with section looks like in the worst case.**

### 4. Section-vs-metadata-row fold

The plan's section structure had separate Section 3 (metadata strip: Duration / StackingType / DisplayMode plain text) and Section 6 (Stacks with chip cluster). Even after collapsing Section 6 to a single chip, the duplication of StackingType across both sections was poor UX — the eye lands on the plain-text "Stacking type: Food" first, the chip below is parallel infrastructure.

The right design (arrived at via review): one row, "Stacking type: [chip]", in the metadata strip. **Cookbook addition: "Section folding" — when a section header would only carry a single chip whose payload is already named in a sibling metadata row, fold the chip into the metadata row's value column. Don't add a dedicated section for a single-chip payload.**

## Process / workflow observations

- **Worktree-based execution + frequent push made review-during-implementation effortless.** The user's three mid-flight refinements (overflow pill kind, Duration sentinels, Stacks-with collapse) landed as small follow-up commits without disrupting the slice structure. The cookbook could note this explicitly as a recommended pattern.
- **`gh pr merge` from inside the worktree fails the local checkout step ("'main' is already used by worktree at ..."), but the remote merge succeeds anyway.** Purely cosmetic noise — worth a footnote in the cookbook's "Workflow" section so the next agent doesn't panic.
- **The "manual smoke handoff" verification step is load-bearing.** Two distinct bugs (StackingType cardinality, chip-converter silent collapse) were only catchable by a human running the shell against real data. The cookbook's verification ladder calls this out but the handoff doc didn't repeat the emphasis — worth re-iterating in every per-tab handoff.

## Verification owed

- **Duration sentinel mapping** — `-1 → "Until cleansed"` and `-2 → "Until removed"` are best-guess labels. If a future Elder Game patch changes meaning, the projection drifts silently. A test that asserts the mapping against a known-stable canonical entry (e.g. `effect_10003` Sticky! at `-2`) would surface a contradicting CDN ship as a test failure rather than a UX regression.

---

*Filed by Claude (Opus 4.7) during PR #298 execution, 2026-05-14.*
