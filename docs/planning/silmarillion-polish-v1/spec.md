# Silmarillion polish v1 + DeepLink + Navigator registry refactors

**Tracked in:** #229, #231, #234, #239 (bundled).

## Context

Three #207 follow-up polish issues for the Silmarillion reference browser, bundled with two parallel refactors of the same shape (switch/hardcoded-set converted to a DI-registered handler registry):

- **#229** — add `mithril://silmarillion/item/<name>` and `mithril://silmarillion/recipe/<name>` module-scoped routes alongside the existing `mithril://item/` / `mithril://recipe/` schemes.
- **#231** — replace the master-detail card layout (~80px per row, ~17 visible) with a compact two-line row layout (~36px per row, ~40+ visible).
- **#234** — move the cross-link sections (Sources / Produced by / Used in) from the bottom of [ItemDetailView.xaml](../../../src/Mithril.Shared.Wpf/ItemDetailView.xaml) to immediately after Description, so sparse items don't force the user's eye past a dead zone of empty Effects-region to reach the recipes that produce/consume them.
- **#239** — convert `IReferenceNavigator`'s three hardcoded `EntityKind` dispatches (the `V1TabbedKinds` HashSet and two switches in `SilmarillionViewModel`) into a `IReferenceKindTarget` registry. Forcing function: the next Silmarillion PR adds the **NPCs tab** (third kind), and per the upcoming-tabs research note, seven more Bucket-B tabs follow it. Refactoring now means each subsequent tab PR is purely additive (register one more target).

The DeepLinkRouter refactor: `Handle`'s big `switch (action)` has grown to six structurally-identical cases (validate payload regex → null-check optional import target → call one method on it → log a diagnostic), and #229 is the first multi-segment route that doesn't fit the existing shape. Adding case #7 inline would mean a nested switch; extracting to a handler registry is the natural home for the new route. Bundled here rather than deferred because the registry is *what makes #229 clean*.

The navigator refactor (#239) is the parallel pattern, motivated by Bucket-B rather than #229. Bundled alongside the DeepLinkRouter refactor because (a) both refactors are the same shape and review more clearly when read together, and (b) the next PR after this one adds the NPCs tab and benefits from landing on top of the registry.

#235 (recipe Sources section + `IReferenceDataService.RecipeSources` plumbing) is intentionally **out of scope** for this PR — it's the only follow-up that needs new reference-data infrastructure and benefits from being a separate review.

## Approach

**The router becomes a dispatcher.** `IDeepLinkHandler` defines `Action` (the URI host segment) and `TryHandle(string subPath, IDiagnosticsSink? diag)`. Each existing branch becomes a handler class owning its payload grammar; the router takes `IEnumerable<IDeepLinkHandler>` via DI and dispatches by host. Adding the silmarillion route is then just registering one more handler that internally parses its two-segment sub-path.

**Item/Recipe handlers stay in the shared layer.** They depend only on `IReferenceNavigator`, which is shell-registered as `NoOpReferenceNavigator` and overridden by Silmarillion via last-singleton-wins DI. Keeping these two handlers in `Mithril.Shared.Wpf` preserves the existing degradation path (Silmarillion uninstalled → URIs are accepted but no-op). The silmarillion handler lives in Silmarillion.Module since it's the module's own scheme.

**Card-shaped rows become row-shaped rows.** The card template chosen for #207 was modelled after Celebrimbor's RecipeCard, but Celebrimbor uses cards as inspect-popups; Silmarillion uses them for browsable catalogue navigation, which needs density. Two-line compact (24px icon + name on line 1, dim subtitle on line 2) was chosen over single-line+tail because the subtitle (equip slot / skill+level) is genuinely useful at scan time and the second line costs ~8px versus the four-line card it replaces.

**ItemDetailView's reading order is restructured by dropping the `Height="*"` slack-absorber.** The outer `Grid` with 23 explicit `RowDefinition`s becomes a `DockPanel` with the internal-name footer `Dock="Bottom"` and a `StackPanel` body. With the footer pinned by `DockPanel`, no body row needs to absorb slack, so the cross-link sections move from rows 19–21 to immediately after Description — the structural change *enables* the reorder. This mirrors the pattern already used by [RecipeDetailView.xaml](../../../src/Silmarillion.Module/Views/RecipeDetailView.xaml), making the two detail views structurally consistent.

**The navigator becomes a kind-target registry.** Each tab module registers an `IReferenceKindTarget` that knows its `EntityKind`, how to select an entity by internal name, and how to open the current detail in a popup window. `SilmarillionReferenceNavigator` takes `IEnumerable<IReferenceKindTarget>` and exposes `CanOpen` as a registry membership check; `SilmarillionViewModel`'s `OnNavigated` and `OpenInWindow` look up the target for the current kind and delegate. The interface lives in `Mithril.Shared` so future tabs can register from their own modules if ownership ever splits — every v1+v2 implementer is in Silmarillion today. Tab-index ordering stays simple: each target carries the index it expects.

## Files to modify

### 1. `IDeepLinkHandler` — new interface

New file `src/Mithril.Shared.Wpf/Modules/IDeepLinkHandler.cs`:

```csharp
public interface IDeepLinkHandler
{
    /// <summary>The first path segment after mithril://. Must be lowercase ASCII.</summary>
    string Action { get; }

    /// <summary>
    /// Handle the remainder of the URI path (everything after the host segment,
    /// with the leading '/' stripped). Implementations own their payload grammar
    /// and any per-handler diagnostic messages. Return false for validation
    /// failure or missing dependency; true on successful dispatch.
    /// </summary>
    bool TryHandle(string subPath, IDiagnosticsSink? diag);
}
```

### 2. Extract per-action handlers

New files under `src/Mithril.Shared.Wpf/Modules/`:

- `ItemDeepLinkHandler.cs` — `Action = "item"`. Validates against `EntityPayloadPattern` (the existing regex `^[A-Za-z0-9_]{1,128}$`). Calls `_navigator.Open(EntityRef.Item(payload))`. Takes `IReferenceNavigator` via constructor.
- `RecipeDeepLinkHandler.cs` — same shape, `EntityRef.Recipe(payload)`.

The two regex constants `EntityPayloadPattern`, `ListPayloadPattern`, `PippinPayloadPattern`, `LegolasPayloadPattern` move with their owning handler. Keep them `private static readonly` on each handler class — they're not shared across handlers.

New files in each owning module project, alongside the existing import-target implementation (so navigation lives co-located with its analogue):

- `Celebrimbor.Module/Services/CraftListDeepLinkHandler.cs` — `Action = "list"`, calls `ICraftListImportTarget.ImportFromLinkPayload`. Sits next to `CraftListImportTarget.cs`. Project already references `Mithril.Shared.Wpf`.
- `Pippin.Module/Sharing/PippinDeepLinkHandler.cs` — `Action = "pippin"`. Sits next to `PippinShareImportTarget.cs`.
- `Legolas.Module/Sharing/LegolasDeepLinkHandler.cs` — `Action = "legolas"`. Sits next to `LegolasShareImportTarget.cs`.
- `Elrond.Module/Services/ElrondDeepLinkHandler.cs` — `Action = "elrond"`. Sits next to `ElrondSkillImportTarget.cs`.

Each module's `Register(IServiceCollection)` adds `services.AddSingleton<IDeepLinkHandler, FooDeepLinkHandler>()`. Item and Recipe handlers register from shell DI ([ServiceCollectionExtensions.cs](../../../src/Mithril.Shared/DependencyInjection/ServiceCollectionExtensions.cs)) since they live in the shared layer.

### 3. `DeepLinkRouter` — rewrite as dispatcher

[DeepLinkRouter.cs](../../../src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs):

- Constructor takes `IEnumerable<IDeepLinkHandler> handlers` and `IDiagnosticsSink? diag`. Builds `Dictionary<string, IDeepLinkHandler>` keyed by `Action.ToLowerInvariant()`. Duplicate `Action` registrations throw at construction time (DI ordering bug — fail loud).
- `Handle(string uri)` becomes: validate well-formed URI, check scheme is `mithril`, look up handler by host, call `handler.TryHandle(parsed.AbsolutePath.TrimStart('/'), _diag)`. Unknown host → diag info + return false (existing behavior).
- File shrinks from ~170 lines to ~40.

### 4. `SilmarillionDeepLinkHandler` — new module-scoped route

New file `src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs`:

- `Action = "silmarillion"`. Takes `IReferenceNavigator` via constructor.
- `TryHandle` splits `subPath` on first `/` → `(kind, name)`. Validates `name` against `EntityPayloadPattern`. Unknown `kind` → diag info + return false. Dispatch:
  - `kind == "item"` → `_navigator.Open(EntityRef.Item(name))`
  - `kind == "recipe"` → `_navigator.Open(EntityRef.Recipe(name))`
- Register from [SilmarillionModule.Register](../../../src/Silmarillion.Module/SilmarillionModule.cs).

### 5. Compact row templates

[src/Silmarillion.Module/Views/Resources.xaml](../../../src/Silmarillion.Module/Views/Resources.xaml) — rewrite both DataTemplates. Border removed; row is a `DockPanel` with `IconImage` on the left and a `StackPanel` body.

```xml
<DataTemplate x:Key="ItemCardTemplate">
    <DockPanel Margin="0" LastChildFill="True">
        <wpf:IconImage DockPanel.Dock="Left" IconId="{Binding IconId}"
                       Width="24" Height="24" Margin="6,3,6,3" VerticalAlignment="Center"/>
        <StackPanel VerticalAlignment="Center" Margin="0,3,6,3">
            <TextBlock Text="{Binding Name}" FontWeight="SemiBold"
                       Foreground="#FFD4A847" TextTrimming="CharacterEllipsis"
                       FontSize="{DynamicResource AppFontSizeHint}"/>
            <TextBlock Text="{Binding EquipSlot, Converter={StaticResource CamelCaseSplit}}"
                       Foreground="#88FFFFFF"
                       FontSize="{DynamicResource AppFontSizeSmall}"
                       TextTrimming="CharacterEllipsis"
                       Visibility="{Binding EquipSlot, Converter={StaticResource NullOrEmptyToVis}}"/>
        </StackPanel>
    </DockPanel>
</DataTemplate>
```

The recipe template mirrors this shape, replacing the subtitle binding with `SkillDisplayName` + level (hidden when `SkillDisplayName` is empty via `NullOrEmptyToVis`).

Key keeps `*CardTemplate` despite no longer being cards — renaming would ripple into both Silmarillion tab views and isn't worth the diff. Names are wallpaper.

`TextTrimming="CharacterEllipsis"` on both lines preserves the 320px-wide listbox constraint without wrap-jumping row heights.

### 6. ItemDetailView restructure

[ItemDetailView.xaml](../../../src/Mithril.Shared.Wpf/ItemDetailView.xaml) — rewrite the top-level structure:

```xml
<Border Padding="14,12">
    <DockPanel>
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding InternalName}"
                   Foreground="#88FFFFFF"
                   FontFamily="{DynamicResource AppMonoFontFamily}"
                   FontSize="{DynamicResource AppFontSizeSmall}"
                   Margin="0,8,0,0" HorizontalAlignment="Right"/>
        <StackPanel>
            <!-- Header -->
            <!-- Description -->
            <!-- Sources (was row 19) -->
            <!-- Produced by (was row 20) -->
            <!-- Used in (was row 21) -->
            <!-- Skill requirements (was row 2) -->
            <!-- Effects (was row 3; Height="*" gone) -->
            <!-- Augmentation, WaxAugments, WaxItems, AugmentPools, TaughtRecipes,
                 EffectTags, ResearchProgress, XpGrants, WordsOfPower,
                 LearnedAbilities, ProducedItems, EquipBonuses,
                 CraftingEnhancements, RecipeCooldowns,
                 UnpreviewableExtractions (rest of optional sections,
                 same relative order as today) -->
        </StackPanel>
    </DockPanel>
</Border>
```

All sections become `StackPanel` children; their `Visibility` bindings already hide them when empty. The host `ContentControl` keeps its `MinHeight="{Binding ViewportHeight, ElementName=DetailScroller}"` binding (set in [ItemsTabView.xaml:78-85](../../../src/Silmarillion.Module/Views/ItemsTabView.xaml#L78-L85)), so the DockPanel stretches to viewport and the footer pins to the bottom of the viewport for short content.

Cross-link section internal ordering preserved (Sources → Produced by → Used in), matching the issue's recommended layout.

### 7. `IReferenceKindTarget` — new interface (issue #239)

New file `src/Mithril.Shared/Reference/IReferenceKindTarget.cs`:

```csharp
public interface IReferenceKindTarget
{
    /// <summary>The entity kind this target is responsible for. One target per kind.</summary>
    EntityKind Kind { get; }

    /// <summary>The TabControl index this target's UI lives at. Used by the
    /// host VM when an entity of this kind is navigated to.</summary>
    int TabIndex { get; }

    /// <summary>Look the entity up by internal name and select it in the tab's
    /// master-detail. Returns false if the entity isn't in the reference data.</summary>
    bool TrySelectByInternalName(string internalName);

    /// <summary>Open the current detail in a popup window. Returns false if
    /// the tab has no current detail (nothing selected).</summary>
    bool TryOpenInWindow();
}
```

Interface lives in `Mithril.Shared` (not `Mithril.Shared.Wpf`) because `EntityKind` already does — keeps the navigation contract layered above the WPF presentation layer. The two adapter classes that implement it are presentation-layer-concrete and live in `Silmarillion.Module/Navigation/`.

### 8. `ItemsKindTarget` and `RecipesKindTarget` — adapters (issue #239)

New files in `src/Silmarillion.Module/Navigation/`:

- `ItemsKindTarget.cs` — `Kind = EntityKind.Item`, `TabIndex = 0`. Constructor takes `ItemsTabViewModel` and `IReferenceDataService`. `TrySelectByInternalName` does `_refData.ItemsByInternalName.TryGetValue(...)` and sets `_vm.SelectedItem`. `TryOpenInWindow` reads `_vm.DetailViewModel` and instantiates [ItemDetailWindow](../../../src/Mithril.Shared.Wpf/ItemDetailWindow.xaml).
- `RecipesKindTarget.cs` — `Kind = EntityKind.Recipe`, `TabIndex = 1`. Same shape; uses `_refData.RecipesByInternalName` and `_vm.SelectedRecipe` / [RecipeDetailWindow](../../../src/Silmarillion.Module/Views/RecipeDetailWindow.xaml).

Both registered in [SilmarillionModule.Register](../../../src/Silmarillion.Module/SilmarillionModule.cs) via `services.AddSingleton<IReferenceKindTarget, ItemsKindTarget>()` / `RecipesKindTarget`.

### 9. `SilmarillionReferenceNavigator` — registry-driven `CanOpen` (issue #239)

[SilmarillionReferenceNavigator.cs](../../../src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs):

- Constructor takes `IEnumerable<IReferenceKindTarget> targets`. Build `Dictionary<EntityKind, IReferenceKindTarget>`; duplicate `Kind` registrations throw at construction time (same fail-loud as DeepLinkRouter).
- `V1TabbedKinds` field deleted.
- `CanOpen(reference)` becomes `_targets.ContainsKey(reference.Kind)`.
- `Open`, `Back`, `Forward`, the back/forward stacks, and the `Navigated` event are unchanged.

### 10. `SilmarillionViewModel` — registry lookups replace switches (issue #239)

[SilmarillionViewModel.cs](../../../src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs):

- Constructor takes `IEnumerable<IReferenceKindTarget> targets` and stores the same `Dictionary<EntityKind, IReferenceKindTarget>` (or reuses the navigator's via a small accessor — either way is fine; the dictionary is small).
- `OnNavigated`: replace the `switch (e.Current.Kind) { case Item: ...; case Recipe: ... }` block with `if (_targets.TryGetValue(e.Current.Kind, out var target)) { SelectedTabIndex = target.TabIndex; target.TrySelectByInternalName(e.Current.InternalName); }`. Unknown kind silently no-ops (matches today's degradation).
- `OpenInWindow`: replace the `switch (_navigator.Current?.Kind) { case Item: ...; case Recipe: ... }` block with `if (_navigator.Current is { } current && _targets.TryGetValue(current.Kind, out var target)) target.TryOpenInWindow();`.
- `CanOpenInWindow` remains `_navigator.Current is not null` (could tighten to "and a target exists" — same outcome since the navigator only accepts navigable kinds, but the tighter check is more honest).

### 11. Wiki — Deep-Linking page

[Mithril wiki](https://github.com/moumantai-gg/mithril/wiki) — update the Deep-Linking page (or equivalent reference) to:

- Document `mithril://silmarillion/item/<internalName>` and `mithril://silmarillion/recipe/<internalName>` as the **preferred** forms.
- Mark `mithril://item/<internalName>` / `mithril://recipe/<internalName>` as **legacy but still supported**.
- Update the example in [AboutSettingsView.xaml:146](../../../src/Mithril.Shell/Views/AboutSettingsView.xaml#L146) to use the module-scoped form.

Wiki commit goes in the [project-gorgon.wiki](../../../../project-gorgon.wiki) checkout (note: that repo's default branch is `master`, not `main` — distinct from this repo).

## Testing

### Router refactor (commit 1)

[tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs](../../../tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs) — existing test cases stay; refactor the test setup to register handlers via DI rather than passing import targets to the router constructor. Per-handler unit tests can live alongside each handler later — not blocking, since coverage is preserved end-to-end through the router tests.

### Silmarillion handler (commit 2)

New file `tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs`:

- `TryHandle_ItemKind_OpensItem` — `silmarillion/item/CraftedLeatherBoots5` → navigator opens `EntityRef.Item("CraftedLeatherBoots5")`.
- `TryHandle_RecipeKind_OpensRecipe` — `silmarillion/recipe/SkinSheep` → navigator opens `EntityRef.Recipe("SkinSheep")`.
- `TryHandle_UnknownKind_ReturnsFalse` — `silmarillion/npc/Marna` → false, navigator not invoked, diag breadcrumb logged.
- `TryHandle_InvalidPayload_ReturnsFalse` — `silmarillion/item/<garbage with separators>` → false.
- `TryHandle_NoSecondSegment_ReturnsFalse` — `silmarillion/item` (no name) → false.
- `TryHandle_EmptySubPath_ReturnsFalse` — `silmarillion/` or `silmarillion` → false.

Add one router-level integration test (`Handle_SilmarillionRoute_DispatchesToHandler`) to confirm DI registration works end-to-end.

### Compact rows (commit 3)

Manual verification only — XAML template change with no behavioral path to unit-test. Launch `dotnet run --project src/Mithril.Shell`, open Silmarillion's Items tab, confirm:

- Rows are visually compact (24px icon, two-line text).
- Subtitle hides when empty (e.g. for items with no equip slot).
- Hover/selection accent still works.
- Scroll performance unchanged (virtualization still on).
- Both tabs work (Items + Recipes).

### ItemDetailView restructure (commit 4)

Manual verification only — XAML restructure with no behavioral change. Confirm:

- Sparse item (e.g. a lorebook or candle-wick material): cross-link sections (Sources / Produced by / Used in) appear immediately after description; internal-name footer pins to the bottom of the right pane.
- Effect-heavy item (e.g. a weapon or armor piece): all sections render in the new order; scrolling works; no visual regressions.
- Popup [ItemDetailWindow.xaml](../../../src/Mithril.Shared.Wpf/ItemDetailWindow.xaml) (which hosts the same view) still renders correctly. The popup sizes to content so the footer-pin behavior is irrelevant there; verify the new section order looks reasonable.

### Navigator kind-target registry (commit 5)

[tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs](../../../tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs) — update existing `CanOpen` tests to register fake targets via DI rather than rely on the deleted `V1TabbedKinds` constant. Add:

- `CanOpen_KnownKind_ReturnsTrue` — registered target for Item → `CanOpen(EntityRef.Item(...))` returns true.
- `CanOpen_UnknownKind_ReturnsFalse` — no target registered for Npc → `CanOpen(EntityRef.Npc(...))` returns false.
- `Constructor_DuplicateKinds_Throws` — two targets with `Kind == Item` → constructor throws.

New tests for the adapters:

- `tests/Silmarillion.Tests/Navigation/ItemsKindTargetTests.cs`:
  - `TrySelectByInternalName_KnownItem_SelectsAndReturnsTrue`.
  - `TrySelectByInternalName_UnknownItem_ReturnsFalse_VmUnchanged`.
  - `TryOpenInWindow_NoDetail_ReturnsFalse`.
- `tests/Silmarillion.Tests/Navigation/RecipesKindTargetTests.cs` — same three shapes.

`tests/Silmarillion.Tests/ViewModels/SilmarillionViewModelTests.cs` (new or extended):
- `OnNavigated_ItemKind_SwitchesTabAndSelects` — registered Items target with a populated fake ref-data → navigator fires `Navigated(Item)` → `SelectedTabIndex == 0` and `Items.SelectedItem` is set.
- `OnNavigated_UnknownKind_NoChange` — Navigated event with an unregistered kind → no state mutation.
- `OpenInWindow_NoCurrent_NoOp` — `_navigator.Current is null` → no window shown (cannot directly assert "no window"; verify the target's `TryOpenInWindow` is not called via a spy).

## Out of scope

- **#235 (recipe Sources section + `IReferenceDataService.RecipeSources` plumbing)** — separate PR; needs new parser plumbing and is the largest of the four follow-ups. Will land independently.
- **Renaming `*CardTemplate` → `*RowTemplate`** in `Resources.xaml`. Names are wallpaper; rename would ripple into both tab views without changing behavior.
- **Removing legacy `mithril://item/` and `mithril://recipe/` schemes.** Issue calls for soft-deprecation (keep working, document the preferred form). No removal scheduled.
- **Per-handler unit tests for the extracted Item/Recipe/CraftList/Pippin/Legolas/Elrond handlers.** Coverage is preserved through the existing router tests; per-handler tests are a follow-up nicety, not a blocker.
- **Data-driven `TabControl` markup.** XAML tab declarations stay hand-written; targets map to their tabs by carrying a `TabIndex` int. When more than ~4 tabs ship, revisit a list-driven approach — not now.
- **Splitting tab ownership across modules** (NPCs in its own module, etc.). `IReferenceKindTarget` lives in `Mithril.Shared` so this is possible later; every v1+v2 implementer lives in Silmarillion.Module today.

## Commit sequence

1. **DeepLink router → handler-registry refactor** (`IDeepLinkHandler`, extract six handlers, rewrite router, re-wire tests, register each module's handler in its `Register(IServiceCollection)`). No new URI behavior; existing tests pass.
2. **Add Silmarillion deep-link handler** + tests + register in `SilmarillionModule.Register`. New URI behavior added; legacy URIs unchanged.
3. **Compact row templates** in `Resources.xaml`. Manual visual verification.
4. **ItemDetailView restructure**. Manual visual verification on both sparse and effect-heavy items.
5. **Navigator → kind-target registry refactor** (`IReferenceKindTarget`, `ItemsKindTarget`, `RecipesKindTarget`; navigator's `V1TabbedKinds` deleted; `SilmarillionViewModel`'s two switches replaced with dictionary lookups; tests). No new user-visible behavior; existing chip-clickability and tab-switch behavior preserved.
6. **Wiki update** + `AboutSettingsView.xaml` example string.
