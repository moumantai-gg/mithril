# Smaug #385 — converge `MinFavorTier` to typed `FavorTier`; audit residual string-favor seams

**Tracked in:** #385

## Context

#370 collapsed the four favor-tier models into one canonical
`Mithril.Reference.Models.Npcs.FavorTier` (+ `FavorScale`, `FavorTierExtensions`),
deleted the Arwen/Smaug duplicates, and retyped `IFavorLookupService.GetFavorTier`
to `FavorTier?` (merged in #387, commit `8415ce9`). To keep #387 out of the
reference-projection layer, **one seam was deliberately left stringly-typed**:
the vendor's minimum-favor-to-trade gate. #385 is the dedicated pass to converge
it and confirm nothing else string-favor remains in Smaug *logic*.

### The exact seam

The game's `npcs.json` carries a per-service "minimum favor to access this
service" as a string on the POCO base
[`Mithril.Reference.Models.Npcs.NpcService.Favor`](../../../src/Mithril.Reference/Models/Npcs/NpcService.cs#L19) (`string?`).
`ReferenceDataService` projects each POCO service into the slim
[`Mithril.Shared.Reference.NpcService`](../../../src/Mithril.Shared/Reference/NpcEntry.cs#L37-L40)
record — whose second positional member is `string? MinFavorTier`
([NpcEntry.cs:39](../../../src/Mithril.Shared/Reference/NpcEntry.cs#L39)) — at
[ReferenceDataService.cs:1110](../../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L1110):

```csharp
.Select(s => new NpcService(
    s.Type,
    s.Favor,                                   // ← raw string flows straight into MinFavorTier
    s is PocoNpcStoreService store ? StoreCapIncreaseParser.ParseRequiringGold(store.CapIncreases) : []))
```

So `MinFavorTier` is a raw, unparsed token string at rest in the slim model.
Post-#387, Smaug parses it to `FavorTier` **at each point of use** via
`FavorTierExtensions.Parse` (aliased `Parse` in those files). There are exactly
two logic sites:

- [VendorCapResolver.cs:44](../../../src/Smaug.Module/Domain/VendorCapResolver.cs#L44):
  `if (store.MinFavorTier is { } minTier && currentTier < Parse(minTier)) return null;`
- [SellPlannerService.cs:102-108](../../../src/Smaug.Module/State/SellPlannerService.cs#L102-L108):
  `isAccessible = store.MinFavorTier is null || (playerTier ?? FavorTier.Neutral) >= Parse(store.MinFavorTier);`
  and `estimateTier = playerTier ?? (store.MinFavorTier is { } m ? Parse(m) : FavorTier.Neutral);`

(`VendorCatalogService`/`StorageSellbackService` only *carry* `MinFavorTier`
through to display records — see "Display layer" below — they do not compare it.)

### Behavioural equivalence requirement (load-bearing)

The current gate semantics, which **must be preserved exactly**:

| `MinFavorTier` value | current behaviour |
|---|---|
| `null` | no gate (service always accessible favor-wise) |
| a real tier token (e.g. `"Friends"`) | gated: blocked when player tier `<` it |
| an unrecognised / junk string | **not gated** — `Parse` → `FavorTier.Unknown` (`int.MinValue`); `currentTier < Unknown` is always false, so the gate never blocks |

The unparseable→not-gated case is subtle and is the exact reason the retype must
go through `FavorTierExtensions.Parse` at the projection (which yields `Unknown`
for junk), **not** a throwing parse. After the retype the comparison is a direct
enum `<` / `>=` and the table above must still hold (verify `Unknown = int.MinValue`
keeps "junk = not gated"; `null` MinFavorTier keeps "no gate").

**Display parity corollary** (Approach step 5): wherever `MinFavorTier` drives a
*shown requirement* (`ResolveFavorRequirement`), `null` **and** `Unknown` both
render as *no requirement line*. The displayed requirement must never assert a
gate the logic doesn't enforce.

## Approach

**Push the parse into the projection; compare typed everywhere downstream.**

1. **Retype the slim record field.** `Mithril.Shared.Reference.NpcService.MinFavorTier`:
   `string?` → `FavorTier?`
   ([NpcEntry.cs:39](../../../src/Mithril.Shared/Reference/NpcEntry.cs#L39)). Add
   `using Mithril.Reference.Models.Npcs;` to `NpcEntry.cs` (Mithril.Shared already
   references Mithril.Reference — `NpcEntry.cs` already aliases `StoreCapIncrease`
   from it, so no new project dependency).
2. **Parse at the projection.** [ReferenceDataService.cs:1110](../../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L1110):
   `s.Favor` → `s.Favor is { } f ? FavorTierExtensions.Parse(f) : (FavorTier?)null`.
   Decision (preserves the table above): **null `Favor` → `null`**; **non-null
   `Favor` → `Parse(Favor)`** (junk → `FavorTier.Unknown`, *not* collapsed to
   `null`). Do not map junk to `null` — that would flip "junk = not gated" only
   coincidentally and lose the distinction; keep `Unknown` so the gate math is
   the single source of the not-gated rule.
3. **Drop the two Smaug boundary parses.** With `store.MinFavorTier` now
   `FavorTier?`:
   - VendorCapResolver.cs:44 → `if (store.MinFavorTier is { } req && currentTier < req) return null;`
   - SellPlannerService.cs:102-103 → `... || (playerTier ?? FavorTier.Neutral) >= store.MinFavorTier;`
     (note: `>= ` against a non-null `FavorTier`; keep the `is null` short-circuit
     so a null gate stays "accessible"). Line 108 →
     `estimateTier = playerTier ?? store.MinFavorTier ?? FavorTier.Neutral;`
   - Remove the now-unused `Parse` alias / `using static FavorTierExtensions`
     from any Smaug file that no longer calls a static `FavorTierExtensions`
     member — **but only if** it truly has none left (see
     [[favortier-extensions-using-static-not-dead]] — `.DisplayName()`/`.ToToken()`
     are extension calls that still need the namespace/`using static` under the
     alias pattern; do not blindly delete).
5. **Cross-project consumer (NOT just Smaug).** The slim
   `NpcService.MinFavorTier` has one consumer outside Smaug that the retype
   hard-breaks under warnings-as-errors:
   [`IngredientSourcesViewModel.ResolveFavorRequirement`](../../../src/Mithril.Shared.Wpf/IngredientSourcesViewModel.cs#L199-L216)
   (`Mithril.Shared.Wpf`). Lines 212-213 do `string.IsNullOrEmpty(svc.MinFavorTier)`
   (now a type error) + `$"Requires {svc.MinFavorTier} or higher"`. Fix:
   - **Aliases, NOT a namespace import.** This file references POCO-vs-slim
     `NpcService`/`NpcPreference` in `<see cref>` doc comments (lines 162-166);
     a blanket `using Mithril.Reference.Models.Npcs;` creates CS0104 /
     ambiguous-cref errors under warnings-as-errors (the
     [[favortier-extensions-using-static-not-dead]] collision). Mirror the Smaug
     pattern instead:
     ```csharp
     using FavorTier = Mithril.Reference.Models.Npcs.FavorTier;
     using static Mithril.Reference.Models.Npcs.FavorTierExtensions;
     ```
   - Body (212-213) →
     ```csharp
     if (svc.MinFavorTier is not { } tier || tier == FavorTier.Unknown) return null;
     return $"Requires {tier.DisplayName()} or higher";
     ```
   - **Decision (display ⇔ gate parity):** collapse `Unknown` into the same
     "no requirement shown" branch as `null`. The gate logic treats `Unknown`
     as *not gated*; printing "Requires Unknown or higher" would assert a
     requirement the gate doesn't enforce — exactly the display/logic divergence
     #385 exists to kill. Real-token behaviour is unchanged; junk shifts from
     "Requires &lt;junk&gt; or higher" to no line (strictly more correct).
4. **Display layer — keep `string?`, convert at the boundary** (mirrors how #387
   handled `PlayerFavorTier`). The three Smaug service records
   ([SellPlannerService.cs:32](../../../src/Smaug.Module/State/SellPlannerService.cs#L32),
   [StorageSellbackService.cs:25](../../../src/Smaug.Module/State/StorageSellbackService.cs#L25),
   [VendorCatalogService.cs:23](../../../src/Smaug.Module/State/VendorCatalogService.cs#L23))
   and the four VMs that read them
   ([SellPlannerViewModel.cs:25/123](../../../src/Smaug.Module/ViewModels/SellPlannerViewModel.cs#L25),
   [StorageSellbackViewModel.cs:14/76](../../../src/Smaug.Module/ViewModels/StorageSellbackViewModel.cs#L14),
   [VendorCatalogViewModel.cs:14/47](../../../src/Smaug.Module/ViewModels/VendorCatalogViewModel.cs#L14),
   [VendorShopViewModel.cs:14/69](../../../src/Smaug.Module/ViewModels/VendorShopViewModel.cs#L14))
   bind `string` in XAML. Keep those `string?`/`string`; at each
   `MinFavorTier: store.MinFavorTier` record-construction site (lines
   SellPlanner :119, StorageSellback :146, VendorCatalog :105) convert via
   `store.MinFavorTier?.DisplayName()`. The VMs' `?? ""` fallbacks then stay.
   Do **not** retype the display records/VMs/XAML — that is churn for no
   single-source benefit (the logic is already typed; display wants a label).

## Files to modify

- [src/Mithril.Shared/Reference/NpcEntry.cs](../../../src/Mithril.Shared/Reference/NpcEntry.cs) — field retype + `using`.
- [src/Mithril.Shared/Reference/ReferenceDataService.cs](../../../src/Mithril.Shared/Reference/ReferenceDataService.cs#L1110) — parse-at-projection.
- [src/Smaug.Module/Domain/VendorCapResolver.cs](../../../src/Smaug.Module/Domain/VendorCapResolver.cs#L44) — typed gate.
- [src/Smaug.Module/State/SellPlannerService.cs](../../../src/Smaug.Module/State/SellPlannerService.cs#L102-L119) — typed gate + estimate + `MinFavorTier:` display conversion.
- [src/Smaug.Module/State/StorageSellbackService.cs](../../../src/Smaug.Module/State/StorageSellbackService.cs#L146) — `MinFavorTier:` display conversion.
- [src/Smaug.Module/State/VendorCatalogService.cs](../../../src/Smaug.Module/State/VendorCatalogService.cs#L105) — `MinFavorTier:` display conversion (note: `storeService?.MinFavorTier?.DisplayName()` — null-conditional already there).
- [src/Mithril.Shared.Wpf/IngredientSourcesViewModel.cs](../../../src/Mithril.Shared.Wpf/IngredientSourcesViewModel.cs#L199-L216) — cross-project consumer; alias-not-namespace + `Unknown`-suppression per Approach step 5.
- [src/Mithril.Reference/Models/Npcs/FavorTier.cs](../../../src/Mithril.Reference/Models/Npcs/FavorTier.cs#L26) — stale doc comment ("`NpcService.MinFavorTier` remains `string?`") becomes false; update it in the same PR.
- Audit grep confirmed the above is the *complete* consumer set of the slim `NpcService.MinFavorTier`. Silmarillion's `MinFavorTier` carriers are fed from the **POCO** `s.Favor`/`p.Favor`, not the slim record — see Out of scope.

## Calibration — explicitly NOT migrated (token-at-rest)

`PriceObservation.FavorTier` is a persisted `string`
([PriceCalibration.cs:66](../../../src/Smaug.Module/Domain/PriceCalibration.cs#L66))
and is baked into the persisted calibration keys `AbsoluteKey`/`RatioKey`
([PriceCalibrationService.cs:253-256](../../../src/Smaug.Module/Domain/PriceCalibrationService.cs#L253-L256),
`"NpcKey|InternalName|FavorTier|CivicPrideBucket"`). **Decision: keep token at
rest, no migration** (retyping/normalising it would be a versioned
calibration-data schema change — out of scope, and unnecessary). The only
required audit: confirm the value passed into
[`RecordObservation`](../../../src/Smaug.Module/Domain/PriceCalibrationService.cs#L101)
(`favorTier` → set at [:127](../../../src/Smaug.Module/Domain/PriceCalibrationService.cs#L127))
is a **canonical token** — i.e. trace its caller and ensure it is
`someFavorTier.ToToken()` (or a `FavorTierExtensions.Parse(raw).ToToken()`
round-trip), so a stray raw/unknown spelling can't silently create a
mis-keyed cache entry. If the caller already passes a canonical token, record
that finding and change nothing. If a versioned migration is ever wanted, it
must be an explicit `SchemaVersion` bump (see the settings-migration /
json-versioning conventions), never silent.

### Audit outcome (traced — decision recorded, no code change)

The `favorTier` arg to `RecordObservation` is **not** a `.ToToken()`-canonicalised
value. Chain:
[`RecordObservation(…, _context.ActiveFavorTier!, …)`](../../../src/Smaug.Module/State/VendorIngestionService.cs#L91)
← `VendorSellContext.ActiveFavorTier` (`string?`,
[VendorSellContext.cs:14/42](../../../src/Smaug.Module/State/VendorSellContext.cs#L14))
← `VendorScreenOpened.FavorTier` ← the **raw regex capture group** at
[VendorLogParser.cs:61](../../../src/Smaug.Module/Parsing/VendorLogParser.cs#L61)
(`m.Groups[2].Value` from `ProcessVendorScreen(entityId, FavorTier, …)`),
never round-tripped through `Parse`/`ToToken`.

**Decision: document + defer; no #385 code change.** In practice the value is
canonical because `FavorTier`'s tokens are modelled directly on PG's emitted
tier names, so the empirically-observed key shape is already correct. The
defensive risk (PG emits an unrecognised spelling → it bakes verbatim into a
mis-keyed `AbsoluteKey`/`RatioKey`) is real but cannot be fixed inside #385:
canonicalising at ingest *changes the persisted key shape*, which is a versioned
calibration-data migration and **must** be an explicit `SchemaVersion` bump per
the json-versioning convention — out of #385 scope. #385 therefore:
- changes nothing in the calibration path,
- adds a **characterization test** pinning the current raw-token passthrough
  (so a future migration is a deliberate, test-visible change, not a silent one),
- filed follow-up **#397** ("canonicalise calibration favor token at ingest
  behind a `SchemaVersion` bump"); this doc + #397 are the recorded rationale
  per the Acceptance "if anything stays `string`, record the rationale" clause.

## Testing

TDD. The behavioural-equivalence table is the spec — pin it.

- **Projection tests** (`tests/Mithril.Shared.Tests`, the existing NPC/ReferenceData
  projection suite): `MinFavorTier` projects to `FavorTier?` — `null Favor → null`;
  `"Friends" → FavorTier.Friends`; an unrecognised `Favor` string → `FavorTier.Unknown`
  (not `null`).
- **`VendorCapResolverTests`** (`tests/Smaug.Tests/VendorCapResolverTests.cs`):
  add/confirm the three equivalence rows — null gate ⇒ accessible; real-tier gate
  blocks a below-tier player and admits an at/above player; **junk gate ⇒ not
  blocked** (the `Unknown = int.MinValue` path). These must pass identically
  before (string parse) and after (typed) the change — ideally land the tests
  first against the pre-#385 code, watch them stay green post-change (refactor
  safety net), per TDD-for-refactor.
- **SellPlanner accessibility**: a test that `isAccessible` is unchanged across
  the retype for null / real / junk `MinFavorTier`.
- **`ResolveFavorRequirement`** (`tests/Mithril.Shared.Tests`, Wpf VM suite):
  `null MinFavorTier → null`; real tier → `"Requires <DisplayName> or higher"`;
  junk/`Unknown` → `null` (display ⇔ gate parity, Approach step 5).
- **Calibration characterization test**: pin the *current* raw-token passthrough
  into the persisted key (audit found the caller passes the raw log token, not
  `.ToToken()`). This is a guard so the deferred canonicalisation migration is
  test-visible, not a behaviour change in #385.
- Full `dotnet test Mithril.slnx` green; `grep -rn "MinFavorTier" src --include=*.cs`
  shows no remaining `string`-typed *comparison* (only display-record `string`
  carriers + the projection).

## Out of scope

- The canonical type, `FavorScale`, Arwen — all landed in #387.
- Retyping the **display** records / VMs / XAML (`MinFavorTier` as a shown label
  stays `string`).
- Any calibration-data migration / `PriceObservation.FavorTier` retype (token
  stays at rest; audit-only).
- `StoreCapIncrease.Tier` (already typed since #373) and `PlayerFavorTier`
  (already handled in #387).
- **Silmarillion's `MinFavorTier` — do not touch.**
  `NpcsTabViewModel.cs:223/451` + `NpcServiceRow`/`NpcPreferenceRow` carry
  `string? MinFavorTier` fed from the **POCO** `s.Favor`/`p.Favor`, not the
  slim `Mithril.Shared.Reference.NpcService`, so the retype does not reach them.
  In particular `NpcsTabViewModel.cs:223` has a deliberate
  `Despised → null` special-case — **must not** be "converged" into the
  projection; its semantics differ from the gate's null/Unknown rules.

## Acceptance

- `Mithril.Shared.Reference.NpcService.MinFavorTier` is `FavorTier?`; the parse
  happens once, at the `ReferenceDataService` projection.
- No `string`-typed favor *comparison/gating* remains in Smaug logic; the only
  remaining `string` favor is (a) display-record carriers (converted via
  `.DisplayName()` at construction) and (b) persisted calibration tokens (parsed
  through `FavorTierExtensions.Parse` on the way in).
- The behavioural-equivalence table holds (null / real / junk gate) — proven by
  tests that are green both before and after the refactor.
- Full solution builds 0 warnings / 0 errors and `dotnet test Mithril.slnx` is
  all green; post-merge re-verify on merged `main` (non-negotiable).
- #385 closed by the PR; if any decision is made to keep something `string`, the
  rationale is recorded in the issue/PR.
