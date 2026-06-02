# Silmarillion: StorageVaults tab (#249)

**Tracked in:** #249 — `module:silmarillion` / `area:ui` / `type:feature`. #203 umbrella (Bucket B long-tail).

**Companion docs:**
- [silmarillion-tab-cookbook.md](../../silmarillion-tab-cookbook.md) — **read first.** Standard tab scaffolding. The chip-vs-popup rule is fully shipped; **this tab has no 1:N fan-out surface** — every cross-link here is a 1:1 `EntityChip`. Do not invent a popup or a synthetic kind.
- Reference implementations to mirror (merged): `LorebooksKindTarget`/`LorebooksTabViewModel`/`LorebookDetailViewModel` + views (PR #322) for scaffolding; `AreaDetailViewModel` (PR #324) for the per-group table-style rendering pattern.

> Lowest-payoff tab in Bucket B. Keep it tight. The only non-mechanical work: the `Levels` favor→slots capacity table, the polymorphic `Requirements` rendering, and the **Bilbo-overlap check the issue explicitly demands**.

## Data shape — StorageVault

[`Mithril.Reference.Models.Misc.StorageVault`](../../../src/Mithril.Reference/Models/Misc/StorageVault.cs) (`storagevaults.json`, small dataset, envelope key = NPC internal name, `*`-prefixed for account-wide):

```csharp
public sealed class StorageVault
{
    public string? Area { get; set; }                 // e.g. "AreaSerbule" → Area 1:1 chip
    public int ID { get; set; }
    public string? NpcFriendlyName { get; set; }       // display label; key is NPC internal name
    public string? Grouping { get; set; }
    public IReadOnlyDictionary<string,int>? Levels { get; set; } // favor tier → slot count
    public int? NumSlots { get; set; }                 // flat slot count when not favor-scaled
    public IReadOnlyList<string>? RequiredItemKeywords { get; set; }
    public string? RequirementDescription { get; set; } // e.g. "Potions and Alchemy Ingredients"
    public bool? HasAssociatedNpc { get; set; }         // false ⇒ no operator NPC chip (e.g. transfer chests)
    public IReadOnlyList<StorageRequirement>? Requirements { get; set; } // polymorphic, see below
    public string? SlotAttribute { get; set; }
    public string? NumSlotsScriptAtomic { get; set; }
    public int? NumSlotsScriptAtomicMaxValue { get; set; }
    public int? NumSlotsScriptAtomicMinValue { get; set; }
    public IReadOnlyDictionary<string,int>? EventLevels { get; set; } // rare (≈1 entry)
}
```

`StorageRequirement` is a **polymorphic** base (discriminator `T`) with subclasses: `StorageInteractionFlagSetRequirement` (`InteractionFlag`), `StorageQuestCompletedRequirement` (`Quest` → **Quest 1:1 chip**), `StorageServerRulesFlagSetRequirement` (`Flag`), `StorageIsLongtimeAnimalRequirement`, `StorageIsWardenRequirement`, `UnknownStorageRequirement` (`DiscriminatorValue`). Render by intent grouping (mirror the quest-detail polymorphic-requirements UX: reverse-lookup internal names to display where possible, group by meaning, don't mechanically dump subclass names — see project memory *quest-detail rendering — UX over mechanical projection*).

Real samples: `*AccountStorage_Serbule` (Area `AreaSerbule`, `HasAssociatedNpc:false`, `NumSlots:0` — a transfer chest); `NPC_CharlesThompson` (Area `AreaSerbule`, `Levels{Friends:32,CloseFriends:40,…SoulMates:64}`, `RequiredItemKeywords:[Alchemy,Potion,…]`, `RequirementDescription:"Potions and Alchemy Ingredients"`); `IvynsChest` (`Requirements:{T:InteractionFlagSet, InteractionFlag:"Ivyn_Gave_Passcode"}`).

## ⚠️ Mandatory pre-work — Bilbo overlap check (issue requirement)

The #249 issue explicitly requires: *"Bilbo already consumes related storage data — verify there's no parallel projection that should be consolidated rather than duplicated."* Before building, grep `src/Bilbo.Module/` for any `StorageVault`/`storagevaults` consumption or a parallel storage-location model. Record findings in the PR:
- If Bilbo has its own storage projection: do **not** silently duplicate. Plumb `IReferenceDataService.StorageVaults` (the canonical source) and note whether Bilbo should later migrate onto it (file/reference a follow-up issue if consolidation is non-trivial — do not expand this PR's scope to refactor Bilbo).
- If Bilbo only consumes live inventory (not the canonical vault list): no conflict — proceed; note that in the PR.

## Cross-links — all 1:1 chips (no popup)

- **Operator NPC** — only when `HasAssociatedNpc == true`; resolve the envelope key (NPC internal name) → NPC `EntityChip`. Fold into the header/metadata row (single chip → not its own section, per the section-folding lesson).
- **Parent Area** — `Area` → Area `EntityChip`, metadata row.
- **Quest requirement** — any `StorageQuestCompletedRequirement.Quest` → Quest `EntityChip` inline in the Requirements rendering.

None of these is a fan-out set. **Do not build a `ProvenancePopupViewModel` here.** ("Vaults in this area" reverse-lookup is an Areas-tab concern, explicitly out of scope.)

## Scope

1. **Service plumbing on `IReferenceDataService`.** `ParseStorageVaults` exists ([`ReferenceDeserializer.cs:207`](../../../src/Mithril.Reference/Serialization/ReferenceDeserializer.cs#L207)) but is **not** plumbed onto the service. Add `IReadOnlyDictionary<string, StorageVault> StorageVaults` (envelope key → POCO) + empty-default fallback; wire `LoadStorageVaults`/`ParseAndSwapStorageVaults` into ctor + `RefreshAsync` switch + `RefreshAllAsync` + `Keys` + `GetSnapshot`; `FileUpdated("storagevaults")`. Mirror exactly the Lorebooks plumbing pattern PR #322 established.
2. **Kind target** `StorageVaultsKindTarget.cs` mirroring `LorebooksKindTarget`. `Kind => EntityKind.StorageVault` (already enumerated); `EntityRef.StorageVault(string)` already exists (`EntityRef.cs:114`) — grep for stale call sites, fix in-PR if any. Next free `TabIndex`; `TrySelectByInternalName`; `TryOpenInWindow`.
3. **Tab VM + view + detail VM + view + DI**, standard cookbook scaffolding mirroring the Lorebooks set + the `<DataTemplate>` in `SilmarillionView.xaml`. Row record: envelope key (selection), `NpcFriendlyName` (display), Area key, account-wide flag (derived from `*` prefix / `HasAssociatedNpc`), effective slot summary, Grouping facet. Detail sections:
   - **Header** — `NpcFriendlyName`, internal-name footer (envelope key, mono small per convention).
   - **Metadata row** — Parent Area chip; Operator NPC chip (only if `HasAssociatedNpc`); account-wide badge (only when true — noise-filter default).
   - **Capacity** — if `Levels` present, a favor-tier → slot-count table (mirror `AreaDetailViewModel`'s per-group table pattern; order favor tiers canonically, not dict order); else `NumSlots`; surface `SlotAttribute`/script-atomic min–max only when present. `EventLevels` is rare — render only when non-null, clearly labeled as event-gated.
   - **Access requirements** — `RequirementDescription` (prose), `RequiredItemKeywords` (small chip/label cluster — these are item keyword tags, NOT navigable entities; render as plain tags), and the polymorphic `Requirements` grouped by intent (Quest-completed → Quest chip; flags → human label; longtime-animal/warden → a plain badge; Unknown → its `DiscriminatorValue` behind a noise-filtered "(unrecognised requirement)" so a future schema addition degrades gracefully, not as a crash or blank).
4. **Deep-link route** `mithril://silmarillion/storagevault/<key>` — pass-through via `SilmarillionDeepLinkHandler` (no code). Add `Open_StorageVault_*` to `SilmarillionReferenceNavigatorTests`. Note the `*`-prefixed account-wide keys — ensure the route + selection round-trip handles the literal `*` (URL-encode/observe the existing handler's behaviour; add a test for a `*`-prefixed key).
5. **Tab order / `V1TabbedKinds`:** append after the last current tab; add `EntityKind.StorageVault` to `V1TabbedKinds` if not present.

## Tests

Cookbook trio: `StorageVaultsTabViewModelTests` (list construction, account-wide derivation, Grouping facet, `FileUpdated("storagevaults")` rebuild preserving selection), `StorageVaultsKindTargetTests` (incl. a `*`-prefixed key hit), extend `SilmarillionReferenceNavigatorTests` (`Open_StorageVault_*` incl. `*`-prefixed), `StorageVaultDetailViewModelTests` (Area + NPC chip resolution incl. `HasAssociatedNpc:false` ⇒ no NPC chip; favor-table projection ordered canonically; each `StorageRequirement` subclass renders sensibly incl. `UnknownStorageRequirement` graceful degrade; noise filters), `ReferenceDataServiceStorageVaultTests` (plumbing round-trip; refresh rebuilds). Real-bundled-data sanity walk: a transfer chest (`HasAssociatedNpc:false`), a favor-scaled vendor vault (`Levels` populated), an interaction-flag-gated chest (`Requirements`).

## Verification ladder

1. `dotnet build Mithril.slnx` — warnings-as-errors clean.
2. `dotnet test Mithril.slnx` — full suite green.
3. **Real-data walk before manual smoke.**
4. `dotnet run --project src/Mithril.Shell` — Vaults tab appears; pick a favor-scaled vault → capacity table renders ordered; Area/NPC chips navigate; a quest-gated vault shows the Quest chip; a transfer chest shows no NPC chip and doesn't crash on `NumSlots:0`; deep-link to both a plain and a `*`-prefixed key selects.

## Hard constraints (orchestrated)

- **Worktree discipline (non-negotiable):** work ONLY in your assigned worktree; never write/edit/`git` the main repo path `I:\src\project gorgon`; resolve stale-cache/`wpftmp` desync within the worktree against disk truth (an earlier session leaked 186 divergent lines into the main tree). All commits/builds/tests/`gh` inside the worktree.
- Branch off current `main`; feature branch `feat/249-storagevaults-tab`; PR via `gh pr create` against `moumantai-gg/mithril` main. **Do NOT merge.**
- Commits **signed** (1Password auto-lock disabled this session — sign, push, open the PR yourself). Fallback: signing fails ⇒ do **not** `--no-gpg-sign`, do not push unsigned — commit locally, stop, report. Author `Arthur Conde <arthur.conde@live.com>`. Commit trailer `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`. PR body opens `Tracked in: #249`, includes the **Bilbo-overlap finding**, and ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- `dotnet build Mithril.slnx` (0/0) + `dotnet test Mithril.slnx` green from the worktree; shell boots past `=== startup done ===`.
- **Concurrency:** the sibling #248 PlayerTitles sub-session runs in parallel and also adds plumbing to `IReferenceDataService.cs`/`ReferenceDataService.cs`. Keep additions localized/append-style so the orchestrator's sequential merge+rebase stays conflict-free.

## Out of scope

- Refactoring Bilbo onto the canonical `StorageVaults` projection (note + reference a follow-up issue if warranted; don't do it here).
- "Vaults in this area" reverse-lookup (Areas-tab concern).
- Any synthetic-kind / popup surface — there is no fan-out relationship on this tab.

---

*Drafted by Claude (Opus 4.7), filed by @arthur-conde via Claude Code on 2026-05-15.*
