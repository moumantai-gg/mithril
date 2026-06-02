# Silmarillion Reference-Browser Implementation Plan

**Tracked in:** #207 (umbrella: #203)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the `Silmarillion` module — a master-detail browser for CDN reference data. v1 scope = Items + Recipes tabs with full cross-linking, router-shaped navigation (back/forward buttons + Alt+Left/Right + mouse XButton1/XButton2), "open in window" affordance, and `DeepLinkRouter` migration to delegate through `IReferenceNavigator`.

**Architecture:** New `Silmarillion.Module` registers a real `IReferenceNavigator` (replaces shell's `NoOpReferenceNavigator` via DI override — last `AddSingleton<T>` wins). The module hosts two tabs (Items, Recipes), each master-detail. Detail content lives in `UserControl`s (`ItemDetailView`, `RecipeDetailView`) hostable in both tab (master-detail right pane) and popup `Window` (`ItemDetailWindow`, `RecipeDetailWindow`). Cross-links go through `IReferenceNavigator.Open(EntityRef)`; `DeepLinkRouter` migrates to delegate to the same primitive. Two new one-to-many indices on `IReferenceDataService` (`RecipesByProducedItem`, `RecipesByIngredientItem`) power the cross-linking.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, xunit + FluentAssertions, MahApps.Metro.IconPacks.Lucide.

**Branch:** `feat/207-silmarillion-reference-browser` (already created).

**Module name decision:** Defaulting to **Silmarillion** (literal lore-book of the legendarium). Issue lists Galadriel and Faramir as alternatives — if user swaps before execution, replace `Silmarillion` → `{NewName}` everywhere in the plan including: project folder `src/Silmarillion.Module/`, root namespace `Silmarillion`, `IMithrilModule.Id = "silmarillion"`, GH label `module:silmarillion`, branch name suffix.

**Stub-tracking convention:** Per spec, anywhere v1 ships a placeholder for richer functionality, mark with `// TODO(stub:#NN): description`. Two known stubs:
- `// TODO(stub:#214)` — recipe `ResultEffects` text rendering (replaced by chip templates in #214)
- `// TODO(stub:#NN)` for `RecipeSources` if exposing it is deferred (file new issue at the time, link in the marker)

---

## Phase 1 — Foundation: indices, chip data carriers, view extraction

Pure infrastructure. No user-visible change. After this phase, item-detail popups look identical; new fields exist but no caller populates them.

### Task 1.1: Add `RecipesByProducedItem` and `RecipesByIngredientItem` to `IReferenceDataService`

**Files:**
- Modify: `src/Mithril.Shared/Reference/IReferenceDataService.cs`
- Modify: `src/Mithril.Shared/Reference/ReferenceDataService.cs`
- Test: `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeIndexTests.cs` (new)

- [ ] **Step 1.1.1: Write failing tests**

Create `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeIndexTests.cs`:

```csharp
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public sealed class ReferenceDataServiceRecipeIndexTests
{
    [Fact]
    public void RecipesByProducedItem_IndexesByResultItemInternalName()
    {
        var service = ReferenceDataServiceTestHelpers.LoadFromBundled();

        // Pick a known recipe with a stable ResultItem (e.g., AlchemyExperiment - any recipe whose result is in items.json).
        // Find one programmatically:
        var sample = service.Recipes.Values
            .Where(r => r.ResultItems is { Count: > 0 })
            .Select(r => new { Recipe = r, ResultItem = service.Items.TryGetValue(r.ResultItems![0].ItemCode, out var i) ? i : null })
            .FirstOrDefault(x => x.ResultItem?.InternalName is not null);

        sample.Should().NotBeNull("the bundled data should contain at least one recipe with a resolvable result item");

        service.RecipesByProducedItem.Should().ContainKey(sample!.ResultItem!.InternalName!);
        service.RecipesByProducedItem[sample.ResultItem.InternalName!].Should().Contain(sample.Recipe);
    }

    [Fact]
    public void RecipesByProducedItem_FallsBackToProtoResultItems_WhenResultItemsEmpty()
    {
        var service = ReferenceDataServiceTestHelpers.LoadFromBundled();

        var sample = service.Recipes.Values
            .Where(r => (r.ResultItems is null || r.ResultItems.Count == 0) && r.ProtoResultItems is { Count: > 0 })
            .Select(r => new { Recipe = r, ResultItem = service.Items.TryGetValue(r.ProtoResultItems![0].ItemCode, out var i) ? i : null })
            .FirstOrDefault(x => x.ResultItem?.InternalName is not null);

        if (sample is null)
        {
            // Bundled data may or may not contain proto-only recipes; if not, assertion is vacuous.
            return;
        }

        service.RecipesByProducedItem.Should().ContainKey(sample.ResultItem!.InternalName!);
        service.RecipesByProducedItem[sample.ResultItem.InternalName!].Should().Contain(sample.Recipe);
    }

    [Fact]
    public void RecipesByIngredientItem_IndexesItemIngredients_ByInternalName()
    {
        var service = ReferenceDataServiceTestHelpers.LoadFromBundled();

        var sample = service.Recipes.Values
            .SelectMany(r => r.Ingredients.OfType<RecipeItemIngredient>().Select(i => new { Recipe = r, Ingredient = i }))
            .Select(x => new { x.Recipe, Item = service.Items.TryGetValue(x.Ingredient.ItemCode, out var i) ? i : null })
            .FirstOrDefault(x => x.Item?.InternalName is not null);

        sample.Should().NotBeNull("the bundled data should contain at least one recipe with an item ingredient resolvable to an internal name");

        service.RecipesByIngredientItem.Should().ContainKey(sample!.Item!.InternalName!);
        service.RecipesByIngredientItem[sample.Item.InternalName!].Should().Contain(sample.Recipe);
    }

    [Fact]
    public void RecipesByIngredientItem_DoesNotIndexKeywordIngredients()
    {
        var service = ReferenceDataServiceTestHelpers.LoadFromBundled();

        // Keyword ingredients are not single-item — they should not produce an entry keyed by any item internal name.
        // Pick a recipe known to use only a keyword ingredient and assert no item-keyed entry mentions it.
        // (This is a structural property: the index is built only from RecipeItemIngredient, never RecipeKeywordIngredient.)
        // The most reliable assertion: total entries in RecipesByIngredientItem matches the sum of distinct (recipe, itemCode) pairs over RecipeItemIngredient.
        var expectedCount = service.Recipes.Values
            .SelectMany(r => r.Ingredients.OfType<RecipeItemIngredient>()
                .Where(i => service.Items.TryGetValue(i.ItemCode, out var item) && item.InternalName is not null)
                .Select(i => new { Recipe = r, ItemName = service.Items[i.ItemCode].InternalName! }))
            .Distinct()
            .Count();

        var actualCount = service.RecipesByIngredientItem.Sum(kv => kv.Value.Count);
        actualCount.Should().Be(expectedCount);
    }
}
```

You will also need `ReferenceDataServiceTestHelpers.LoadFromBundled()` — likely already exists for other tests; if not, add a simple loader in `tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceTestHelpers.cs`:

```csharp
namespace Mithril.Shared.Tests.Reference;

internal static class ReferenceDataServiceTestHelpers
{
    public static IReferenceDataService LoadFromBundled()
    {
        // Construct ReferenceDataService against a temp cache dir using the bundled fallback data.
        // Mirror the constructor signature from src/Mithril.Shared/Reference/ReferenceDataService.cs lines 116-156.
        var tempCache = Path.Combine(Path.GetTempPath(), "MithrilTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCache);
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "BundledData");
        var http = new HttpClient();
        return new ReferenceDataService(cacheDir: tempCache, http: http, diag: null, bundledDir: bundledDir, perf: null);
    }
}
```

(Adjust the bundled-data path lookup to match how other reference-data tests locate it — `grep`/find an existing test in `tests/Mithril.Shared.Tests/Reference/` that already constructs `ReferenceDataService` and copy its bootstrap pattern.)

- [ ] **Step 1.1.2: Run test to verify it fails**

```
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataServiceRecipeIndexTests"
```

Expected: 4 failures with "does not contain property RecipesByProducedItem" (or compile errors if interface lacks the property).

- [ ] **Step 1.1.3: Add interface members**

Modify `src/Mithril.Shared/Reference/IReferenceDataService.cs`, adding after the existing recipe-related properties:

```csharp
    /// <summary>
    /// Recipes indexed by the internal-name of any item they produce (via <c>ResultItems</c>, falling back to
    /// <c>ProtoResultItems</c> when <c>ResultItems</c> is empty). Built once at service init. Used by the
    /// reference-browser module's item-detail "Produced by recipes" cross-link section.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByProducedItem { get; }

    /// <summary>
    /// Recipes indexed by the internal-name of any item they consume as an ingredient (via
    /// <c>RecipeItemIngredient</c> entries only — <c>RecipeKeywordIngredient</c> entries are excluded
    /// since they're kind-based, not item-based). Built once at service init.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem { get; }
```

- [ ] **Step 1.1.4: Implement in `ReferenceDataService`**

In `src/Mithril.Shared/Reference/ReferenceDataService.cs`:

1. Add private fields near the other recipe fields:
   ```csharp
   private IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _recipesByProducedItem = ReadOnlyDictionary<string, IReadOnlyList<Recipe>>.Empty;
   private IReadOnlyDictionary<string, IReadOnlyList<Recipe>> _recipesByIngredientItem = ReadOnlyDictionary<string, IReadOnlyList<Recipe>>.Empty;
   ```
2. Add public property accessors that return the backing fields.
3. At the end of `LoadRecipes()` (or in a new `BuildRecipeCrossLinkIndices()` called from there — read existing structure first), after `_recipes` and `_recipesByInternalName` are populated and after `_items` is populated (verify the load order: LoadItems must precede this), build the two indices:

   ```csharp
   private void BuildRecipeCrossLinkIndices()
   {
       var produced = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
       var ingredient = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);

       foreach (var recipe in _recipes.Values)
       {
           // Produced — prefer ResultItems, fall back to ProtoResultItems.
           var resultSource = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems) ?? Array.Empty<RecipeResultItem>();
           foreach (var result in resultSource)
           {
               if (!_items.TryGetValue(result.ItemCode, out var item) || item.InternalName is null)
               {
                   continue;
               }
               if (!produced.TryGetValue(item.InternalName, out var list))
               {
                   list = new List<Recipe>();
                   produced[item.InternalName] = list;
               }
               if (!list.Contains(recipe))
               {
                   list.Add(recipe);
               }
           }

           // Ingredient — item ingredients only (keyword ingredients are kind-based).
           foreach (var ing in recipe.Ingredients.OfType<RecipeItemIngredient>())
           {
               if (!_items.TryGetValue(ing.ItemCode, out var item) || item.InternalName is null)
               {
                   continue;
               }
               if (!ingredient.TryGetValue(item.InternalName, out var list))
               {
                   list = new List<Recipe>();
                   ingredient[item.InternalName] = list;
               }
               if (!list.Contains(recipe))
               {
                   list.Add(recipe);
               }
           }
       }

       _recipesByProducedItem = produced.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value);
       _recipesByIngredientItem = ingredient.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Recipe>)kv.Value);
   }
   ```

4. Call `BuildRecipeCrossLinkIndices()` once at the end of construction after `LoadItems()` and `LoadRecipes()` have run. Also call it again from `LoadItems`/`LoadRecipes` if those are reachable via `RefreshAsync` and would invalidate the indices.

- [ ] **Step 1.1.5: Build & run tests**

```
dotnet build Mithril.slnx
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~ReferenceDataServiceRecipeIndexTests"
```

Expected: build clean (0 errors, 0 warnings); all 4 tests pass.

- [ ] **Step 1.1.6: Commit**

```
git add src/Mithril.Shared/Reference/IReferenceDataService.cs src/Mithril.Shared/Reference/ReferenceDataService.cs tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceRecipeIndexTests.cs tests/Mithril.Shared.Tests/Reference/ReferenceDataServiceTestHelpers.cs
git commit -m "feat(reference): add RecipesByProducedItem and RecipesByIngredientItem cross-link indices"
```

### Task 1.2: Add chip data carriers to `Mithril.Shared.Wpf`

These are display-VMs carrying enough data to render a clickable cross-link. They live alongside `ItemDetailContext` because that's what extends to reference them. The rendering control (`EntityChip` UserControl) lands later in Phase 5; until then, sections render plain text.

**Files:**
- Create: `src/Mithril.Shared.Wpf/EntityChip.cs` (data carriers only — UserControl arrives in Phase 5)

- [ ] **Step 1.2.1: Create the data carriers**

```csharp
namespace Mithril.Shared.Wpf;

using Mithril.Shared.Reference;

/// <summary>
/// Data-carrying view-model for a clickable (or plain-text) entity cross-link.
/// Used in <see cref="ItemDetailContext"/> and recipe detail panes to back the <c>EntityChip</c> visual.
/// </summary>
public sealed record EntityChipVm(
    string DisplayName,
    int IconId,
    EntityRef Reference,
    bool IsNavigable);

/// <summary>
/// Display-VM for an item source (NPC vendor, monster drop, quest reward, …) shown in item detail.
/// Not all sources map to an entity tab — <see cref="EntityReference"/> is nullable for plain-text rows.
/// </summary>
public sealed record ItemSourceChipVm(
    string DisplayName,
    string? Detail,           // e.g. "from <NPC name> in <Area>" — optional secondary line
    int? IconId,              // optional — source may not have one
    EntityRef? EntityReference,
    bool IsNavigable);
```

(Naming: `EntityChipVm` and `ItemSourceChipVm` rather than `RecipeChip`/`ItemSourceChip` from the spec — `EntityChipVm` is the general carrier reused for any kind, including recipes; the spec said "field names per implementation taste".)

- [ ] **Step 1.2.2: Build to verify it compiles**

```
dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj
```

Expected: PASS.

- [ ] **Step 1.2.3: Commit**

```
git add src/Mithril.Shared.Wpf/EntityChip.cs
git commit -m "feat(shared-wpf): add EntityChipVm and ItemSourceChipVm data carriers for cross-link chips"
```

### Task 1.3: Extract `ItemDetailView` UserControl from `ItemDetailWindow`

Structural refactor. No behavior change.

**Files:**
- Create: `src/Mithril.Shared.Wpf/ItemDetailView.xaml` + `.xaml.cs`
- Modify: `src/Mithril.Shared.Wpf/ItemDetailWindow.xaml` + `.xaml.cs`

- [ ] **Step 1.3.1: Create `ItemDetailView.xaml`**

The body content lives inside the inner `<Border Padding="14,12">...<Grid>...</Grid></Border>` of `ItemDetailWindow.xaml` (lines ~95-450). Move it verbatim into a new `UserControl`:

```xml
<UserControl x:Class="Mithril.Shared.Wpf.ItemDetailView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:Mithril.Shared.Wpf"
             mc:Ignorable="d"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compat/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008">
    <UserControl.Resources>
        <!-- move any window-scoped resources here from ItemDetailWindow.xaml -->
    </UserControl.Resources>
    <Border Padding="14,12">
        <Grid>
            <!-- 20 RowDefinitions + 15 conditional sections moved verbatim from ItemDetailWindow.xaml -->
        </Grid>
    </Border>
</UserControl>
```

Key practice: copy the `<Grid>` element and its `RowDefinitions` exactly as-is. Move any `<Window.Resources>` entries that are referenced inside the body into `<UserControl.Resources>`. Leave the outer chrome (the `<Border Background="#FF1A1A1A">` + title-bar `<Border DockPanel.Dock="Top">`) in the Window.

- [ ] **Step 1.3.2: Create `ItemDetailView.xaml.cs`**

```csharp
namespace Mithril.Shared.Wpf;

public partial class ItemDetailView
{
    public ItemDetailView()
    {
        InitializeComponent();
    }
}
```

`DataContext` comes from the host (Window in legacy callers; master-detail pane in Silmarillion).

- [ ] **Step 1.3.3: Update `ItemDetailWindow.xaml` to host the UserControl**

Replace the inner `<Border Padding="14,12">...</Border>` body with:

```xml
<local:ItemDetailView />
```

The Window's outer chrome (transparent background, custom title bar, drag handlers) stays. The Window's `DataContext` already flows down to the embedded UserControl.

- [ ] **Step 1.3.4: Build & smoke-test**

```
dotnet build Mithril.slnx
```

Then run the app:
```
dotnet run --project src/Mithril.Shell
```

Trigger an item-detail popup (any module that uses `IItemDetailPresenter.Show(...)`, e.g. Celebrimbor's recipe inspector — click on any crafted item). Visually compare: title, icon, equip slot, description, skill reqs, effects, all preview sections render identically. The drag/close handlers in the title bar still work.

- [ ] **Step 1.3.5: Commit**

```
git add src/Mithril.Shared.Wpf/ItemDetailView.xaml src/Mithril.Shared.Wpf/ItemDetailView.xaml.cs src/Mithril.Shared.Wpf/ItemDetailWindow.xaml
git commit -m "refactor(shared-wpf): extract ItemDetailView UserControl from ItemDetailWindow body"
```

### Task 1.4: Extend `ItemDetailContext` with cross-link fields

**Files:**
- Modify: `src/Mithril.Shared.Wpf/ItemDetailContext.cs`
- Modify: `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs`
- Modify: `src/Mithril.Shared.Wpf/ItemDetailView.xaml`

- [ ] **Step 1.4.1: Add three fields to `ItemDetailContext`**

In `src/Mithril.Shared.Wpf/ItemDetailContext.cs`, add to the record:

```csharp
    IReadOnlyList<EntityChipVm>? ProducedByRecipes = null,
    IReadOnlyList<EntityChipVm>? ConsumedByRecipes = null,
    IReadOnlyList<ItemSourceChipVm>? Sources = null,
```

Update `Empty` and any constructor uses to pass `null` for the new fields (they already default to `null` so existing call sites compile).

- [ ] **Step 1.4.2: Expose on `ItemDetailViewModel`**

In `src/Mithril.Shared.Wpf/ItemDetailViewModel.cs`, add three properties projecting from the context, mirroring the existing pattern for the other context-driven collections:

```csharp
    public IReadOnlyList<EntityChipVm> ProducedByRecipes { get; }
    public IReadOnlyList<EntityChipVm> ConsumedByRecipes { get; }
    public IReadOnlyList<ItemSourceChipVm> Sources { get; }
```

Initialize from `context.ProducedByRecipes ?? Array.Empty<EntityChipVm>()` (and likewise for the other two) in the full-context constructor. Default-construct as empty in the simpler overloads.

- [ ] **Step 1.4.3: Render in `ItemDetailView.xaml`**

Add three new sections at the end of the body Grid (rows 20-22, bump the footer to row 23 — or use a single auto-row `StackPanel` if rewriting the layout; the easier path is to extend the existing Grid):

```xml
<!-- Sources (e.g. "Sold by Lawren in Serbule") — only if list is non-empty -->
<StackPanel Grid.Row="20" Margin="0,8,0,0">
    <StackPanel.Style>
        <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Visible"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding Sources.Count}" Value="0">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </StackPanel.Style>
    <TextBlock Text="Sources" FontWeight="SemiBold" Margin="0,0,0,4"/>
    <ItemsControl ItemsSource="{Binding Sources}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <!-- Phase 1 placeholder: plain text. Replaced by EntityChip in Phase 5. -->
                <TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>

<!-- Produced by recipes — only if list is non-empty -->
<StackPanel Grid.Row="21" Margin="0,8,0,0">
    <!-- Visibility style as above, binding to ProducedByRecipes.Count -->
    <TextBlock Text="Produced by" FontWeight="SemiBold" Margin="0,0,0,4"/>
    <ItemsControl ItemsSource="{Binding ProducedByRecipes}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>

<!-- Used in recipes — only if list is non-empty -->
<StackPanel Grid.Row="22" Margin="0,8,0,0">
    <!-- Visibility style as above, binding to ConsumedByRecipes.Count -->
    <TextBlock Text="Used in" FontWeight="SemiBold" Margin="0,0,0,4"/>
    <ItemsControl ItemsSource="{Binding ConsumedByRecipes}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

Also add `RowDefinition Height="Auto"` entries for rows 20, 21, 22; bump the existing footer's `Grid.Row` to 23.

(Refactor note: if this leaves the Grid sprawling, consider switching the outer container to a `StackPanel` instead — visual result is identical for top-down stacked sections. Decision deferred to the engineer.)

- [ ] **Step 1.4.4: Build & smoke-test**

```
dotnet build Mithril.slnx
dotnet run --project src/Mithril.Shell
```

Trigger any item-detail popup. The three new sections render Collapsed (no caller populates them yet), so the popup looks identical to before. Test passes by visual inspection.

- [ ] **Step 1.4.5: Run tests to confirm nothing broke**

```
dotnet test Mithril.slnx
```

Expected: all green. ItemDetailViewModel constructors compile and existing callers pass null for the new context fields by default.

- [ ] **Step 1.4.6: Commit**

```
git add src/Mithril.Shared.Wpf/ItemDetailContext.cs src/Mithril.Shared.Wpf/ItemDetailViewModel.cs src/Mithril.Shared.Wpf/ItemDetailView.xaml
git commit -m "feat(shared-wpf): extend ItemDetailContext with ProducedByRecipes/ConsumedByRecipes/Sources cross-link sections"
```

---

## Phase 2 — Module skeleton

Empty module, lazy-loaded, registered, visible in shell. No content yet.

### Task 2.1: Create `Silmarillion.Module` project

**Files:**
- Create: `src/Silmarillion.Module/Silmarillion.Module.csproj`
- Create: `src/Silmarillion.Module/SilmarillionModule.cs`
- Create: `src/Silmarillion.Module/Views/SilmarillionView.xaml` + `.xaml.cs`
- Create: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs`
- Modify: `Mithril.slnx`

- [ ] **Step 2.1.1: Create the csproj**

Path: `src/Silmarillion.Module/Silmarillion.Module.csproj`. Pattern from `src/Palantir.Module/Palantir.Module.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RootNamespace>Silmarillion</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="MahApps.Metro.IconPacks.Lucide" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Mithril.Reference\Mithril.Reference.csproj" />
    <ProjectReference Include="..\Mithril.Shared\Mithril.Shared.csproj" />
    <ProjectReference Include="..\Mithril.Shared.Wpf\Mithril.Shared.Wpf.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2.1.2: Create placeholder view + view-model**

`src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Silmarillion.ViewModels;

public partial class SilmarillionViewModel : ObservableObject
{
    // Tabs and detail content arrive in later phases.
}
```

`src/Silmarillion.Module/Views/SilmarillionView.xaml`:

```xml
<UserControl x:Class="Silmarillion.Views.SilmarillionView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <TextBlock Text="Silmarillion — reference data (Items + Recipes tabs arriving in subsequent commits)"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Foreground="#88FFFFFF" FontStyle="Italic"/>
    </Grid>
</UserControl>
```

`src/Silmarillion.Module/Views/SilmarillionView.xaml.cs`:

```csharp
namespace Silmarillion.Views;

public partial class SilmarillionView
{
    public SilmarillionView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 2.1.3: Create `SilmarillionModule.cs`**

```csharp
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;
using Mithril.Shared.Modules;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion;

public sealed class SilmarillionModule : IMithrilModule
{
    public string Id => "silmarillion";
    public string DisplayName => "Silmarillion · Reference";
    public PackIconLucideKind Icon => PackIconLucideKind.BookOpen;
    public string? IconUri => null;
    public int SortOrder => 950;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(SilmarillionView);
    public Type? SettingsViewType => null;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<SilmarillionViewModel>();
        services.AddSingleton<SilmarillionView>(sp => new SilmarillionView
        {
            DataContext = sp.GetRequiredService<SilmarillionViewModel>(),
        });
    }
}
```

- [ ] **Step 2.1.4: Register project in solution**

```
dotnet sln Mithril.slnx add src/Silmarillion.Module/Silmarillion.Module.csproj
```

- [ ] **Step 2.1.5: Build & verify discovery**

```
dotnet build Mithril.slnx
```

Verify `src/Mithril.Shell/bin/Debug/net10.0-windows/modules/Silmarillion.Module.dll` exists (Directory.Build.targets auto-copies).

Run:
```
dotnet run --project src/Mithril.Shell
```

Expected: sidebar shows a "Silmarillion · Reference" entry after Palantir (SortOrder 900 < 950). Click it; placeholder view displays.

- [ ] **Step 2.1.6: Commit**

```
git add src/Silmarillion.Module/ Mithril.slnx
git commit -m "feat(silmarillion): empty module scaffold (lazy-loaded reference-data browser placeholder)"
```

### Task 2.2: Create GH label `module:silmarillion`

- [ ] **Step 2.2.1: Create the label**

```
gh label create "module:silmarillion" --description "Touches the silmarillion module" --color "BFD4F2"
```

Expected: "Label 'module:silmarillion' created in arthur-conde/project-gorgon".

- [ ] **Step 2.2.2: No commit (GitHub-side change only).**

---

## Phase 3 — Real `SilmarillionReferenceNavigator`

Live navigator replacing `NoOpReferenceNavigator`. Module DI registration overrides shell's default.

### Task 3.1: Implement navigator

**Files:**
- Create: `src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs`
- Modify: `src/Silmarillion.Module/SilmarillionModule.cs` (register navigator)
- Test: `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` (new)
- Test project: `tests/Silmarillion.Tests/Silmarillion.Tests.csproj` (new)

- [ ] **Step 3.1.1: Create test project**

Path: `tests/Silmarillion.Tests/Silmarillion.Tests.csproj`. Mirror `tests/Palantir.Tests/Palantir.Tests.csproj` (or any module test project; pattern is uniform):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RootNamespace>Silmarillion.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Silmarillion.Module\Silmarillion.Module.csproj" />
    <ProjectReference Include="..\..\src\Mithril.Shared\Mithril.Shared.csproj" />
  </ItemGroup>
</Project>
```

Add to solution:
```
dotnet sln Mithril.slnx add tests/Silmarillion.Tests/Silmarillion.Tests.csproj
```

- [ ] **Step 3.1.2: Write failing tests**

`tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs`:

```csharp
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionReferenceNavigatorTests
{
    [Fact]
    public void Open_SetsCurrent_AndFiresNavigatedWithOpenKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Open(EntityRef.Item("Tomato"));

        nav.Current.Should().Be(EntityRef.Item("Tomato"));
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(NavigationKind.Open);
        captured.Previous.Should().BeNull();
        captured.Current.Should().Be(EntityRef.Item("Tomato"));
    }

    [Fact]
    public void Open_AfterFirstOpen_PushesPreviousToBackStack()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("Tomato"));
        nav.Open(EntityRef.Recipe("MakeSalsa"));

        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Open_ClearsForwardStack()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        nav.CanGoForward.Should().BeTrue();

        nav.Open(EntityRef.Item("C"));

        nav.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void Back_PopsBackStack_PushesToForwardStack_FiresNavigatedWithBackKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Back();

        nav.Current.Should().Be(EntityRef.Item("A"));
        nav.CanGoBack.Should().BeFalse();
        nav.CanGoForward.Should().BeTrue();
        captured!.Kind.Should().Be(NavigationKind.Back);
        captured.Previous.Should().Be(EntityRef.Item("B"));
        captured.Current.Should().Be(EntityRef.Item("A"));
    }

    [Fact]
    public void Forward_PopsForwardStack_PushesToBackStack_FiresNavigatedWithForwardKind()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("B"));
        nav.Back();
        NavigatedEventArgs? captured = null;
        nav.Navigated += (_, e) => captured = e;

        nav.Forward();

        nav.Current.Should().Be(EntityRef.Item("B"));
        nav.CanGoBack.Should().BeTrue();
        nav.CanGoForward.Should().BeFalse();
        captured!.Kind.Should().Be(NavigationKind.Forward);
    }

    [Fact]
    public void Back_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator();
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Back();

        fired.Should().BeFalse();
        nav.Current.Should().BeNull();
    }

    [Fact]
    public void Forward_WhenStackEmpty_IsNoOp_AndDoesNotFireNavigated()
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        var fired = false;
        nav.Navigated += (_, _) => fired = true;

        nav.Forward();

        fired.Should().BeFalse();
    }

    [Theory]
    [InlineData(EntityKind.Item, true)]
    [InlineData(EntityKind.Recipe, true)]
    [InlineData(EntityKind.Ability, false)]
    [InlineData(EntityKind.Npc, false)]
    [InlineData(EntityKind.Quest, false)]
    [InlineData(EntityKind.Lorebook, false)]
    [InlineData(EntityKind.Landmark, false)]
    [InlineData(EntityKind.Area, false)]
    [InlineData(EntityKind.PlayerTitle, false)]
    [InlineData(EntityKind.StorageVault, false)]
    [InlineData(EntityKind.Effect, false)]
    public void CanOpen_TrueForV1TabbedKinds_FalseOtherwise(EntityKind kind, bool expected)
    {
        var nav = new SilmarillionReferenceNavigator();
        nav.CanOpen(new EntityRef(kind, "x")).Should().Be(expected);
    }

    [Fact]
    public void OpeningSameEntityTwice_StillPushesToBackStack()
    {
        // Design choice: "open same item again" is still a history step. User can hit Back to undo.
        var nav = new SilmarillionReferenceNavigator();
        nav.Open(EntityRef.Item("A"));
        nav.Open(EntityRef.Item("A"));

        nav.CanGoBack.Should().BeTrue();
    }
}
```

- [ ] **Step 3.1.3: Run tests to verify they fail**

```
dotnet test tests/Silmarillion.Tests
```

Expected: build error — `SilmarillionReferenceNavigator` not defined.

- [ ] **Step 3.1.4: Implement `SilmarillionReferenceNavigator`**

`src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs`:

```csharp
using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Real implementation of <see cref="IReferenceNavigator"/>. Maintains unbounded back/forward stacks
/// of <see cref="EntityRef"/>. Registered by <c>SilmarillionModule</c>; overrides the shell's
/// <c>NoOpReferenceNavigator</c> via last-singleton-wins DI semantics.
/// </summary>
public sealed class SilmarillionReferenceNavigator : IReferenceNavigator
{
    private static readonly HashSet<EntityKind> NavigableKindsV1 = new() { EntityKind.Item, EntityKind.Recipe };

    private readonly Stack<EntityRef> _back = new();
    private readonly Stack<EntityRef> _forward = new();

    public EntityRef? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public event EventHandler<NavigatedEventArgs>? Navigated;

    public bool CanOpen(EntityRef reference) => NavigableKindsV1.Contains(reference.Kind);

    public void Open(EntityRef reference)
    {
        var previous = Current;
        if (previous is not null)
        {
            _back.Push(previous);
        }
        _forward.Clear();
        Current = reference;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Open));
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var previous = Current;
        if (previous is not null)
        {
            _forward.Push(previous);
        }
        Current = _back.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Back));
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var previous = Current;
        if (previous is not null)
        {
            _back.Push(previous);
        }
        Current = _forward.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Forward));
    }
}
```

- [ ] **Step 3.1.5: Register in module DI**

In `src/Silmarillion.Module/SilmarillionModule.cs`, add to `Register`:

```csharp
        services.AddSingleton<IReferenceNavigator, SilmarillionNavigation.SilmarillionReferenceNavigator>();
```

(Add `using` for `Silmarillion.Navigation`.)

This intentionally overrides the shell's `NoOpReferenceNavigator` registered earlier — last `AddSingleton<T>` wins for non-keyed singletons.

- [ ] **Step 3.1.6: Run all tests**

```
dotnet build Mithril.slnx
dotnet test tests/Silmarillion.Tests
```

Expected: build clean, all 11 navigator tests green.

- [ ] **Step 3.1.7: Commit**

```
git add src/Silmarillion.Module/Navigation/ src/Silmarillion.Module/SilmarillionModule.cs tests/Silmarillion.Tests/ Mithril.slnx
git commit -m "feat(silmarillion): real ReferenceNavigator with Back/Forward history stacks"
```

---

## Phase 4 — `RecipeDetailView` UserControl + `RecipeDetailWindow`

Greenfield detail view for recipes. Hostable in both master-detail (right pane) and Window (open-in-window affordance).

### Task 4.1: `RecipeDetailViewModel`

**Files:**
- Create: `src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs`

- [ ] **Step 4.1.1: Write failing tests**

```csharp
using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class RecipeDetailViewModelTests
{
    [Fact]
    public void Projection_FromRecipePoco_PreservesNameDescriptionSkillLevelAndIconId()
    {
        var recipe = new Recipe
        {
            Key = "recipe_1",
            InternalName = "MakeTomatoSauce",
            Name = "Make Tomato Sauce",
            Description = "Crush 3 tomatoes into sauce.",
            Skill = "Cooking",
            SkillLevelReq = 12,
            IconId = 4242,
            Ingredients = [],
        };

        var vm = new RecipeDetailViewModel(recipe, ingredients: [], producedItems: [], resultEffectsText: []);

        vm.DisplayName.Should().Be("Make Tomato Sauce");
        vm.InternalName.Should().Be("MakeTomatoSauce");
        vm.Description.Should().Be("Crush 3 tomatoes into sauce.");
        vm.Skill.Should().Be("Cooking");
        vm.SkillLevelReq.Should().Be(12);
        vm.IconId.Should().Be(4242);
    }

    [Fact]
    public void SkillRequirementChip_ProjectsSkillAndLevel()
    {
        var recipe = new Recipe { Key = "r", Skill = "Cooking", SkillLevelReq = 30, Ingredients = [] };
        var vm = new RecipeDetailViewModel(recipe, ingredients: [], producedItems: [], resultEffectsText: []);

        vm.SkillRequirementChip.Should().Be("Cooking 30");
    }

    [Fact]
    public void ResultEffectsText_RendersAsPlainStrings_NotChips()
    {
        // TODO(stub:#214) — v1 renders raw effect strings, replaced by rich chips in #214.
        var recipe = new Recipe { Key = "r", Ingredients = [] };
        var effects = new[] { "TSysCraftedEquipment(...)", "AddItemTSysPowerWax(...)" };

        var vm = new RecipeDetailViewModel(recipe, ingredients: [], producedItems: [], resultEffectsText: effects);

        vm.ResultEffectsText.Should().BeEquivalentTo(effects);
    }
}
```

- [ ] **Step 4.1.2: Run tests, verify fail**

```
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipeDetailViewModelTests"
```

Expected: build error, `RecipeDetailViewModel` not defined.

- [ ] **Step 4.1.3: Implement**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

public sealed class RecipeDetailViewModel : ObservableObject
{
    public RecipeDetailViewModel(
        Recipe recipe,
        IReadOnlyList<EntityChipVm> ingredients,
        IReadOnlyList<EntityChipVm> producedItems,
        IReadOnlyList<string> resultEffectsText)
    {
        Recipe = recipe;
        Ingredients = ingredients;
        ProducedItems = producedItems;
        ResultEffectsText = resultEffectsText;
    }

    public Recipe Recipe { get; }
    public string DisplayName => Recipe.Name ?? Recipe.InternalName ?? Recipe.Key;
    public string InternalName => Recipe.InternalName ?? "";
    public string? Description => Recipe.Description;
    public string? Skill => Recipe.Skill;
    public int SkillLevelReq => Recipe.SkillLevelReq;
    public int IconId => Recipe.IconId;
    public string SkillRequirementChip => $"{Recipe.Skill} {Recipe.SkillLevelReq}";

    public IReadOnlyList<EntityChipVm> Ingredients { get; }
    public IReadOnlyList<EntityChipVm> ProducedItems { get; }

    /// <summary>
    /// TODO(stub:#214): plain-string rendering of recipe ResultEffects. Replaced by rich chip templates in #214.
    /// </summary>
    public IReadOnlyList<string> ResultEffectsText { get; }
}
```

- [ ] **Step 4.1.4: Run tests, verify pass**

```
dotnet test tests/Silmarillion.Tests --filter "FullyQualifiedName~RecipeDetailViewModelTests"
```

- [ ] **Step 4.1.5: Commit**

```
git add src/Silmarillion.Module/ViewModels/RecipeDetailViewModel.cs tests/Silmarillion.Tests/ViewModels/RecipeDetailViewModelTests.cs
git commit -m "feat(silmarillion): RecipeDetailViewModel with ingredient/result/effect projections"
```

### Task 4.2: `RecipeDetailView` UserControl

**Files:**
- Create: `src/Silmarillion.Module/Views/RecipeDetailView.xaml` + `.xaml.cs`

- [ ] **Step 4.2.1: Create the UserControl**

`src/Silmarillion.Module/Views/RecipeDetailView.xaml`:

```xml
<UserControl x:Class="Silmarillion.Views.RecipeDetailView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:wpf="clr-namespace:Mithril.Shared.Wpf;assembly=Mithril.Shared.Wpf">
    <Border Padding="14,12" Background="#FF1A1A1A">
        <StackPanel>
            <!-- Header: icon + name + internal name subscript -->
            <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                <wpf:IconImage IconId="{Binding IconId}" Width="32" Height="32" Margin="0,0,8,0"/>
                <StackPanel>
                    <TextBlock Text="{Binding DisplayName}" FontSize="16" FontWeight="SemiBold"/>
                    <TextBlock Text="{Binding InternalName}" FontSize="10" Foreground="#88FFFFFF" FontFamily="Consolas"/>
                </StackPanel>
            </StackPanel>

            <!-- Skill requirement chip -->
            <Border Background="#3398FF" CornerRadius="3" Padding="6,2" HorizontalAlignment="Left" Margin="0,0,0,8">
                <TextBlock Text="{Binding SkillRequirementChip}" FontSize="11" Foreground="#FFFFFF"/>
            </Border>

            <!-- Description -->
            <TextBlock Text="{Binding Description}" TextWrapping="Wrap" Margin="0,0,0,8"
                       Visibility="{Binding Description, Converter={StaticResource NullOrEmptyToCollapsedConverter}}"/>

            <!-- Ingredients -->
            <TextBlock Text="Ingredients" FontWeight="SemiBold" Margin="0,4,0,2"
                       Visibility="{Binding Ingredients.Count, Converter={StaticResource ZeroToCollapsedConverter}}"/>
            <ItemsControl ItemsSource="{Binding Ingredients}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <!-- Phase 1 placeholder — replaced by EntityChip in Phase 5 -->
                        <TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <!-- Produced items -->
            <TextBlock Text="Produces" FontWeight="SemiBold" Margin="0,8,0,2"
                       Visibility="{Binding ProducedItems.Count, Converter={StaticResource ZeroToCollapsedConverter}}"/>
            <ItemsControl ItemsSource="{Binding ProducedItems}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>

            <!-- Result effects (text stub — TODO(stub:#214)) -->
            <TextBlock Text="Effects" FontWeight="SemiBold" Margin="0,8,0,2"
                       Visibility="{Binding ResultEffectsText.Count, Converter={StaticResource ZeroToCollapsedConverter}}"/>
            <ItemsControl ItemsSource="{Binding ResultEffectsText}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding}" FontFamily="Consolas" FontSize="11" Foreground="#CCCCCC" TextWrapping="Wrap" Margin="0,1"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </Border>
</UserControl>
```

**Converters note:** If `NullOrEmptyToCollapsedConverter` and `ZeroToCollapsedConverter` don't already exist in `Mithril.Shared.Wpf`, define them in `src/Silmarillion.Module/Views/Converters.cs` (or check `Mithril.Shared.Wpf/Resources.xaml` for existing equivalents first — `IsHasContentConverter` or similar may exist).

`src/Silmarillion.Module/Views/RecipeDetailView.xaml.cs`:

```csharp
namespace Silmarillion.Views;

public partial class RecipeDetailView
{
    public RecipeDetailView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4.2.2: Build to verify**

```
dotnet build src/Silmarillion.Module
```

- [ ] **Step 4.2.3: Commit**

```
git add src/Silmarillion.Module/Views/RecipeDetailView.xaml src/Silmarillion.Module/Views/RecipeDetailView.xaml.cs
git commit -m "feat(silmarillion): RecipeDetailView UserControl with header/skill/ingredients/produces/effects sections"
```

### Task 4.3: `RecipeDetailWindow` thin Window wrapper

**Files:**
- Create: `src/Silmarillion.Module/Views/RecipeDetailWindow.xaml` + `.xaml.cs`

- [ ] **Step 4.3.1: Create the Window**

Mirror `ItemDetailWindow`'s outer chrome (transparent background, drag, close). Body hosts `<local:RecipeDetailView/>`. See `src/Mithril.Shared.Wpf/ItemDetailWindow.xaml` lines 1-95 for the template.

- [ ] **Step 4.3.2: Build & commit**

```
dotnet build src/Silmarillion.Module
git add src/Silmarillion.Module/Views/RecipeDetailWindow.xaml src/Silmarillion.Module/Views/RecipeDetailWindow.xaml.cs
git commit -m "feat(silmarillion): RecipeDetailWindow chrome wrapper hosting RecipeDetailView"
```

---

## Phase 5 — `EntityChip` shared UserControl

The visual that renders clickable-vs-plain cross-link chips. Lives in `Mithril.Shared.Wpf` for reuse.

### Task 5.1: Create `EntityChip`

**Files:**
- Create: `src/Mithril.Shared.Wpf/EntityChip.xaml` + `.xaml.cs`

- [ ] **Step 5.1.1: Create the UserControl**

`src/Mithril.Shared.Wpf/EntityChip.xaml`:

```xml
<UserControl x:Class="Mithril.Shared.Wpf.EntityChip"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:Mithril.Shared.Wpf"
             x:Name="Root">
    <ContentControl>
        <ContentControl.Style>
            <Style TargetType="ContentControl">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate>
                            <!-- Plain-text variant: simple TextBlock -->
                            <StackPanel Orientation="Horizontal">
                                <local:IconImage IconId="{Binding ElementName=Root, Path=IconId}" Width="14" Height="14" Margin="0,0,4,0"
                                                 Visibility="{Binding ElementName=Root, Path=HasIcon, Converter={StaticResource BoolToVisibilityConverter}}"/>
                                <TextBlock Text="{Binding ElementName=Root, Path=DisplayName}" Foreground="#CCCCCC"/>
                            </StackPanel>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding ElementName=Root, Path=IsNavigable}" Value="True">
                        <Setter Property="Template">
                            <Setter.Value>
                                <ControlTemplate>
                                    <!-- Clickable variant: Button styled as a chip -->
                                    <Button Command="{Binding ElementName=Root, Path=ClickCommand}"
                                            Background="#3398FF" Foreground="#FFFFFF" BorderThickness="0"
                                            Padding="6,2" Cursor="Hand">
                                        <StackPanel Orientation="Horizontal">
                                            <local:IconImage IconId="{Binding ElementName=Root, Path=IconId}" Width="14" Height="14" Margin="0,0,4,0"
                                                             Visibility="{Binding ElementName=Root, Path=HasIcon, Converter={StaticResource BoolToVisibilityConverter}}"/>
                                            <TextBlock Text="{Binding ElementName=Root, Path=DisplayName}"/>
                                        </StackPanel>
                                    </Button>
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </ContentControl.Style>
    </ContentControl>
</UserControl>
```

`src/Mithril.Shared.Wpf/EntityChip.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

public partial class EntityChip : UserControl
{
    public EntityChip()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is EntityChipVm vm)
        {
            DisplayName = vm.DisplayName;
            IconId = vm.IconId;
            HasIcon = vm.IconId > 0;
            IsNavigable = vm.IsNavigable;
            Reference = vm.Reference;
        }
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }
    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(EntityChip), new PropertyMetadata(""));

    public int IconId
    {
        get => (int)GetValue(IconIdProperty);
        set => SetValue(IconIdProperty, value);
    }
    public static readonly DependencyProperty IconIdProperty = DependencyProperty.Register(
        nameof(IconId), typeof(int), typeof(EntityChip), new PropertyMetadata(0));

    public bool HasIcon
    {
        get => (bool)GetValue(HasIconProperty);
        set => SetValue(HasIconProperty, value);
    }
    public static readonly DependencyProperty HasIconProperty = DependencyProperty.Register(
        nameof(HasIcon), typeof(bool), typeof(EntityChip), new PropertyMetadata(false));

    public bool IsNavigable
    {
        get => (bool)GetValue(IsNavigableProperty);
        set => SetValue(IsNavigableProperty, value);
    }
    public static readonly DependencyProperty IsNavigableProperty = DependencyProperty.Register(
        nameof(IsNavigable), typeof(bool), typeof(EntityChip), new PropertyMetadata(false));

    public EntityRef? Reference { get; private set; }

    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }
    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand), typeof(ICommand), typeof(EntityChip), new PropertyMetadata(null));
}
```

**Wiring note for consumers:** The chip's click invokes `ClickCommand` with `CommandParameter` defaulting to `null`. Consumers will set `ClickCommand` (via DI or VM-supplied `IRelayCommand<EntityRef>`) at the parent template level. Alternative simpler wiring: the chip directly resolves `IReferenceNavigator` via a static service-locator pattern — but the codebase prefers DI, so command-binding wins.

- [ ] **Step 5.1.2: Build**

```
dotnet build src/Mithril.Shared.Wpf
```

- [ ] **Step 5.1.3: Update existing placeholder XAML to use `EntityChip`**

In `src/Mithril.Shared.Wpf/ItemDetailView.xaml` (the 3 placeholder sections from Phase 1, Step 1.4.3), and `src/Silmarillion.Module/Views/RecipeDetailView.xaml` (the placeholder ingredient/produces sections), replace:

```xml
<TextBlock Text="{Binding DisplayName}" Margin="0,1"/>
```

with:

```xml
<local:EntityChip />
```

(Each ItemTemplate's DataContext is already the `EntityChipVm`, so the chip pulls its fields automatically.)

- [ ] **Step 5.1.4: Build & smoke-test**

```
dotnet build Mithril.slnx
```

App smoke test: existing item-detail popups (Celebrimbor click-through) — the three new cross-link sections still render Collapsed since no caller populates them.

- [ ] **Step 5.1.5: Commit**

```
git add src/Mithril.Shared.Wpf/EntityChip.xaml src/Mithril.Shared.Wpf/EntityChip.xaml.cs src/Mithril.Shared.Wpf/ItemDetailView.xaml src/Silmarillion.Module/Views/RecipeDetailView.xaml
git commit -m "feat(shared-wpf): EntityChip control with plain/clickable variants driven by IsNavigable"
```

---

## Phase 6 — Items tab

Master-detail Items tab: card list (left) with search; detail pane (right) hosting `ItemDetailView`.

### Task 6.1: `ItemsTabViewModel`

**Files:**
- Create: `src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs`

- [ ] **Step 6.1.1: Failing tests**

```csharp
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class ItemsTabViewModelTests
{
    [Fact]
    public void AllItems_PopulatedFromReferenceData()
    {
        var refData = FakeReferenceData.WithItems(
            new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 },
            new Item { Id = 2, InternalName = "Lettuce", Name = "Lettuce", IconId = 2 });

        var vm = new ItemsTabViewModel(refData, navigator: new FakeNavigator());

        vm.AllItems.Should().HaveCount(2);
    }

    [Fact]
    public void SelectingAnItem_UpdatesCurrentDetailViewModel()
    {
        var item = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 };
        var refData = FakeReferenceData.WithItems(item);
        var vm = new ItemsTabViewModel(refData, navigator: new FakeNavigator());

        vm.SelectedItem = item;

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Item.Should().Be(item);
    }
}
```

(Define `FakeReferenceData` and `FakeNavigator` as test fixtures in `tests/Silmarillion.Tests/TestSupport/`. `FakeReferenceData` implements `IReferenceDataService` with manually-populated dictionaries. `FakeNavigator` is the existing `NoOpReferenceNavigator` or a minimal stub.)

- [ ] **Step 6.1.2: Run & verify fail**

- [ ] **Step 6.1.3: Implement**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

public partial class ItemsTabViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;

    public ItemsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator)
    {
        _refData = refData;
        _navigator = navigator;
        AllItems = refData.ItemsByInternalName.Values.OrderBy(i => i.Name).ToList();
    }

    public IReadOnlyList<Item> AllItems { get; }

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private Item? _selectedItem;

    [ObservableProperty]
    private ItemDetailViewModel? _detailViewModel;

    partial void OnSelectedItemChanged(Item? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }
        var context = BuildCrossLinkContext(value);
        DetailViewModel = new ItemDetailViewModel(value, _refData, context);
    }

    private ItemDetailContext BuildCrossLinkContext(Item item)
    {
        var produced = _refData.RecipesByProducedItem.TryGetValue(item.InternalName ?? "", out var p)
            ? p.Select(r => new EntityChipVm(r.Name ?? r.InternalName ?? r.Key, r.IconId, EntityRef.Recipe(r.InternalName ?? r.Key), _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key)))).ToList()
            : null;
        var consumed = _refData.RecipesByIngredientItem.TryGetValue(item.InternalName ?? "", out var c)
            ? c.Select(r => new EntityChipVm(r.Name ?? r.InternalName ?? r.Key, r.IconId, EntityRef.Recipe(r.InternalName ?? r.Key), _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key)))).ToList()
            : null;
        var sources = _refData.ItemSources.TryGetValue(item.InternalName ?? "", out var s)
            ? s.Select(src => new ItemSourceChipVm(
                  DisplayName: src.SourceTypeName + ": " + src.SourceDisplayName,
                  Detail: null,
                  IconId: null,
                  EntityReference: null,    // most sources don't map to a v1 entity tab; leave null for plain text
                  IsNavigable: false)).ToList()
            : null;
        return new ItemDetailContext(
            ProducedByRecipes: produced,
            ConsumedByRecipes: consumed,
            Sources: sources);
    }
}
```

(Note: the `ItemSource` type's actual property names need confirming — read `src/Mithril.Shared/Reference/ItemSource.cs` or equivalent before writing the projection. The above is a plausible shape; the engineer adjusts to match the actual record fields.)

- [ ] **Step 6.1.4: Run tests, verify pass**

- [ ] **Step 6.1.5: Commit**

```
git add src/Silmarillion.Module/ViewModels/ItemsTabViewModel.cs tests/Silmarillion.Tests/
git commit -m "feat(silmarillion): ItemsTabViewModel with master-detail selection + cross-link context"
```

### Task 6.2: `ItemsTabView` + `ItemCardTemplate`

**Files:**
- Create: `src/Silmarillion.Module/Views/ItemsTabView.xaml` + `.xaml.cs`
- Create: `src/Silmarillion.Module/Views/Resources.xaml` (DataTemplates resource dictionary)

- [ ] **Step 6.2.1: Create `Resources.xaml` with `ItemCardTemplate`**

Model on `src/Celebrimbor.Module/Views/Resources.xaml` `RecipeCardTemplate` (lines 23+). Card body: icon (40×40) + name + skill reqs + equip slot.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:wpf="clr-namespace:Mithril.Shared.Wpf;assembly=Mithril.Shared.Wpf">
    <DataTemplate x:Key="ItemCardTemplate">
        <Border Background="#FF2A2A2A" BorderBrush="#55FFFFFF" BorderThickness="1"
                CornerRadius="4" Padding="10" MaxWidth="280" Margin="4">
            <StackPanel Orientation="Horizontal">
                <wpf:IconImage IconId="{Binding IconId}" Width="40" Height="40" Margin="0,0,8,0"/>
                <StackPanel>
                    <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                    <TextBlock Text="{Binding EquipSlot}" FontSize="10" Foreground="#88FFFFFF"/>
                </StackPanel>
            </StackPanel>
        </Border>
    </DataTemplate>
</ResourceDictionary>
```

- [ ] **Step 6.2.2: Create `ItemsTabView.xaml`**

```xml
<UserControl x:Class="Silmarillion.Views.ItemsTabView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:wpf="clr-namespace:Mithril.Shared.Wpf;assembly=Mithril.Shared.Wpf"
             xmlns:query="clr-namespace:Mithril.Shared.Wpf.Query;assembly=Mithril.Shared.Wpf">
    <UserControl.Resources>
        <ResourceDictionary Source="Resources.xaml"/>
    </UserControl.Resources>
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="3*"/>
        </Grid.ColumnDefinitions>

        <!-- Query box spans both columns -->
        <wpf:MithrilQueryBox Grid.Row="0" Grid.ColumnSpan="2"
                             QueryText="{Binding QueryText, Mode=TwoWay}"
                             Watermark="bare text or e.g. Name = 'Tomato'"
                             Margin="6"/>

        <!-- Left: card list -->
        <ScrollViewer Grid.Row="1" Grid.Column="0" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding AllItems}"
                          ItemTemplate="{StaticResource ItemCardTemplate}"
                          query:QueryFilter.QueryText="{Binding QueryText}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate>
                        <WrapPanel/>
                    </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
            </ItemsControl>
        </ScrollViewer>

        <!-- Right: detail pane -->
        <Grid Grid.Row="1" Grid.Column="1">
            <ContentControl Content="{Binding DetailViewModel}">
                <ContentControl.ContentTemplate>
                    <DataTemplate>
                        <wpf:ItemDetailView/>
                    </DataTemplate>
                </ContentControl.ContentTemplate>
            </ContentControl>
            <TextBlock Text="Select an item to view details"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       Foreground="#88FFFFFF" FontStyle="Italic"
                       Visibility="{Binding DetailViewModel, Converter={StaticResource NullToVisibleConverter}}"/>
        </Grid>
    </Grid>
</UserControl>
```

**Card click → SelectedItem wiring:** Add a `MouseLeftButtonUp` handler on the card Border (in `ItemCardTemplate`), or use an `InputBindings` `MouseBinding` invoking a `SelectCommand` on the parent VM with the card's DataContext as parameter. Simpler approach: wrap the card content in a `Button` styled to look like the Border (set `Background`, `BorderBrush`, etc. on Button itself). The Button's `Command="{Binding DataContext.SelectItemCommand, RelativeSource=...}"` and `CommandParameter="{Binding}"`.

For brevity here: add a `[RelayCommand] SelectItem(Item item)` to `ItemsTabViewModel` (mutates `SelectedItem = item`), and use Button-as-card.

- [ ] **Step 6.2.3: Build & smoke test**

Run shell. Click Silmarillion tab → empty (Items tab not yet wired into host view, that's Phase 8). Build cleanly.

- [ ] **Step 6.2.4: Commit**

```
git add src/Silmarillion.Module/Views/ItemsTabView.xaml src/Silmarillion.Module/Views/ItemsTabView.xaml.cs src/Silmarillion.Module/Views/Resources.xaml
git commit -m "feat(silmarillion): ItemsTabView with card list + filter + master-detail pane"
```

---

## Phase 7 — Recipes tab

Mirror of Phase 6 for Recipes.

### Task 7.1: `RecipesTabViewModel`

**Files:**
- Create: `src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs`
- Test: `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs`

- [ ] **Step 7.1.1: Failing tests** — mirror `ItemsTabViewModelTests` for Recipes (AllRecipes population, selection → DetailViewModel non-null).

- [ ] **Step 7.1.2: Implement**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

public partial class RecipesTabViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;

    public RecipesTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator)
    {
        _refData = refData;
        _navigator = navigator;
        AllRecipes = refData.Recipes.Values.OrderBy(r => r.Name ?? r.InternalName ?? r.Key).ToList();
    }

    public IReadOnlyList<Recipe> AllRecipes { get; }

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private Recipe? _selectedRecipe;

    [ObservableProperty]
    private RecipeDetailViewModel? _detailViewModel;

    partial void OnSelectedRecipeChanged(Recipe? value)
    {
        if (value is null) { DetailViewModel = null; return; }
        var ingredients = BuildIngredientChips(value);
        var produced = BuildProducedChips(value);
        var effects = value.ResultEffects ?? Array.Empty<string>();
        DetailViewModel = new RecipeDetailViewModel(value, ingredients, produced, effects);
    }

    private IReadOnlyList<EntityChipVm> BuildIngredientChips(Recipe r) =>
        r.Ingredients.OfType<RecipeItemIngredient>()
            .Select(ing => _refData.Items.TryGetValue(ing.ItemCode, out var item) && item.InternalName is not null
                ? new EntityChipVm(
                    DisplayName: $"{item.Name} ×{ing.StackSize}",
                    IconId: item.IconId,
                    Reference: EntityRef.Item(item.InternalName),
                    IsNavigable: _navigator.CanOpen(EntityRef.Item(item.InternalName)))
                : null)
            .Where(c => c is not null)
            .Cast<EntityChipVm>()
            .ToList();

    private IReadOnlyList<EntityChipVm> BuildProducedChips(Recipe r)
    {
        var source = (r.ResultItems is { Count: > 0 } ? r.ResultItems : r.ProtoResultItems) ?? Array.Empty<RecipeResultItem>();
        return source
            .Select(res => _refData.Items.TryGetValue(res.ItemCode, out var item) && item.InternalName is not null
                ? new EntityChipVm(
                    DisplayName: $"{item.Name} ×{res.StackSize}" + (res.PercentChance is { } pc ? $" ({pc:0}%)" : ""),
                    IconId: item.IconId,
                    Reference: EntityRef.Item(item.InternalName),
                    IsNavigable: _navigator.CanOpen(EntityRef.Item(item.InternalName)))
                : null)
            .Where(c => c is not null)
            .Cast<EntityChipVm>()
            .ToList();
    }
}
```

- [ ] **Step 7.1.3: Run tests** — green.

- [ ] **Step 7.1.4: Commit**

```
git add src/Silmarillion.Module/ViewModels/RecipesTabViewModel.cs tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs
git commit -m "feat(silmarillion): RecipesTabViewModel with ingredient/produces chip projections"
```

### Task 7.2: `RecipesTabView` + `RecipeCardTemplate`

**Files:**
- Create: `src/Silmarillion.Module/Views/RecipesTabView.xaml` + `.xaml.cs`
- Modify: `src/Silmarillion.Module/Views/Resources.xaml` (add `RecipeCardTemplate` — distinct from Celebrimbor's; Celebrimbor's renders ingredients inline, ours is just header + skill + result icon for compact card layout)

- [ ] **Step 7.2.1: Add `RecipeCardTemplate` to `Resources.xaml`**

```xml
<DataTemplate x:Key="RecipeCardTemplate">
    <Border Background="#FF2A2A2A" BorderBrush="#55FFFFFF" BorderThickness="1"
            CornerRadius="4" Padding="10" MaxWidth="280" Margin="4">
        <StackPanel Orientation="Horizontal">
            <wpf:IconImage IconId="{Binding IconId}" Width="40" Height="40" Margin="0,0,8,0"/>
            <StackPanel>
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                <TextBlock FontSize="10" Foreground="#88FFFFFF">
                    <Run Text="{Binding Skill}"/>
                    <Run Text=" "/>
                    <Run Text="{Binding SkillLevelReq}"/>
                </TextBlock>
            </StackPanel>
        </StackPanel>
    </Border>
</DataTemplate>
```

- [ ] **Step 7.2.2: Create `RecipesTabView.xaml`** — copy `ItemsTabView.xaml`, replace `AllItems` → `AllRecipes`, `SelectedItem` → `SelectedRecipe`, `ItemCardTemplate` → `RecipeCardTemplate`, `ItemDetailView` → `RecipeDetailView` (with appropriate xmlns for Silmarillion.Views).

- [ ] **Step 7.2.3: Build & commit**

```
dotnet build Mithril.slnx
git add src/Silmarillion.Module/Views/RecipesTabView.xaml src/Silmarillion.Module/Views/RecipesTabView.xaml.cs src/Silmarillion.Module/Views/Resources.xaml
git commit -m "feat(silmarillion): RecipesTabView with card list + filter + master-detail"
```

---

## Phase 8 — Main view (tab strip) + navigation chrome

### Task 8.1: Wire tabs into `SilmarillionView`

**Files:**
- Modify: `src/Silmarillion.Module/Views/SilmarillionView.xaml` + `.xaml.cs`
- Modify: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs`
- Modify: `src/Silmarillion.Module/SilmarillionModule.cs` (register new VMs)

- [ ] **Step 8.1.1: Update `SilmarillionViewModel`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace Silmarillion.ViewModels;

public partial class SilmarillionViewModel : ObservableObject
{
    public SilmarillionViewModel(ItemsTabViewModel items, RecipesTabViewModel recipes)
    {
        Items = items;
        Recipes = recipes;
    }

    public ItemsTabViewModel Items { get; }
    public RecipesTabViewModel Recipes { get; }

    [ObservableProperty]
    private int _selectedTabIndex = 0;  // 0 = Items, 1 = Recipes
}
```

- [ ] **Step 8.1.2: Update `SilmarillionView.xaml`**

```xml
<UserControl x:Class="Silmarillion.Views.SilmarillionView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:views="clr-namespace:Silmarillion.Views">
    <TabControl SelectedIndex="{Binding SelectedTabIndex, Mode=TwoWay}">
        <TabItem Header="Items">
            <views:ItemsTabView DataContext="{Binding Items}"/>
        </TabItem>
        <TabItem Header="Recipes">
            <views:RecipesTabView DataContext="{Binding Recipes}"/>
        </TabItem>
    </TabControl>
</UserControl>
```

- [ ] **Step 8.1.3: Register new VMs**

In `SilmarillionModule.Register`:

```csharp
services.AddSingleton<ItemsTabViewModel>();
services.AddSingleton<RecipesTabViewModel>();
```

- [ ] **Step 8.1.4: Build & smoke test**

Run shell, click Silmarillion. Tabs appear; items load; click cards; detail pane updates.

- [ ] **Step 8.1.5: Commit**

```
git add src/Silmarillion.Module/
git commit -m "feat(silmarillion): wire Items + Recipes tabs into SilmarillionView"
```

### Task 8.2: Back/forward header buttons + keyboard + mouse XButtons

**Files:**
- Modify: `src/Silmarillion.Module/Views/SilmarillionView.xaml` (add header with chevrons, input bindings)
- Modify: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs` (BackCommand, ForwardCommand)

- [ ] **Step 8.2.1: Add nav commands to `SilmarillionViewModel`**

```csharp
private readonly IReferenceNavigator _navigator;

public SilmarillionViewModel(
    ItemsTabViewModel items,
    RecipesTabViewModel recipes,
    IReferenceNavigator navigator)
{
    Items = items;
    Recipes = recipes;
    _navigator = navigator;
    _navigator.Navigated += OnNavigated;
    BackCommand = new RelayCommand(() => _navigator.Back(), () => _navigator.CanGoBack);
    ForwardCommand = new RelayCommand(() => _navigator.Forward(), () => _navigator.CanGoForward);
}

public IRelayCommand BackCommand { get; }
public IRelayCommand ForwardCommand { get; }

private void OnNavigated(object? sender, NavigatedEventArgs e)
{
    BackCommand.NotifyCanExecuteChanged();
    ForwardCommand.NotifyCanExecuteChanged();
    if (e.Current is not null)
    {
        switch (e.Current.Kind)
        {
            case EntityKind.Item:
                SelectedTabIndex = 0;
                Items.SelectedItem = _refData.ItemsByInternalName.GetValueOrDefault(e.Current.InternalName);
                break;
            case EntityKind.Recipe:
                SelectedTabIndex = 1;
                Recipes.SelectedRecipe = _refData.RecipesByInternalName.GetValueOrDefault(e.Current.InternalName);
                break;
        }
    }
    CommandManager.InvalidateRequerySuggested();
}
```

(Need to inject `IReferenceDataService _refData` too for the lookup. Also `System.Windows.Input` for `CommandManager`.)

- [ ] **Step 8.2.2: Add header chrome + input bindings to `SilmarillionView.xaml`**

Wrap the `TabControl` in a `DockPanel` with a header `StackPanel Orientation="Horizontal"` containing two chevron buttons. Bind `BackCommand`/`ForwardCommand`.

Add `InputBindings`:

```xml
<UserControl.InputBindings>
    <KeyBinding Modifiers="Alt" Key="Left" Command="{Binding BackCommand}"/>
    <KeyBinding Modifiers="Alt" Key="Right" Command="{Binding ForwardCommand}"/>
    <MouseBinding MouseAction="XButton1Click" Command="{Binding BackCommand}"/>
    <MouseBinding MouseAction="XButton2Click" Command="{Binding ForwardCommand}"/>
</UserControl.InputBindings>
```

(WPF doesn't have an `XButton1Click` `MouseAction` enum value by default — use code-behind handler in `SilmarillionView.xaml.cs` overriding `OnPreviewMouseDown` to inspect `e.ChangedButton == MouseButton.XButton1` and invoke `BackCommand`. Same for XButton2.)

- [ ] **Step 8.2.3: Build & smoke test**

Click an item → navigate to a recipe via cross-link (Phase 9 wires this; for now manually test with `_navigator.Open(...)` triggered from a debug button). Back/forward enable/disable correctly. Alt+Left/Right work. Mouse XButtons work.

- [ ] **Step 8.2.4: Commit**

```
git add src/Silmarillion.Module/
git commit -m "feat(silmarillion): back/forward chrome — header buttons + Alt+Left/Right + mouse XButtons"
```

### Task 8.3: "Open in window" affordance

**Files:**
- Modify: `src/Silmarillion.Module/Views/SilmarillionView.xaml` (icon button in header)
- Modify: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs` (OpenInWindowCommand)

- [ ] **Step 8.3.1: Add command to VM**

```csharp
[RelayCommand]
private void OpenInWindow()
{
    if (_navigator.Current is null) return;
    if (_navigator.Current.Kind == EntityKind.Item && Items.DetailViewModel is not null)
    {
        var win = new ItemDetailWindow(Items.DetailViewModel);
        win.Show();
    }
    else if (_navigator.Current.Kind == EntityKind.Recipe && Recipes.DetailViewModel is not null)
    {
        var win = new RecipeDetailWindow { DataContext = Recipes.DetailViewModel };
        win.Show();
    }
}
```

- [ ] **Step 8.3.2: Add icon button**

In header `StackPanel`, add a `Button` with `<iconPacks:PackIconLucide Kind="ExternalLink"/>` content bound to `OpenInWindowCommand`.

- [ ] **Step 8.3.3: Smoke test**

Click open-in-window → popup Window shows the same content. Multiple windows can coexist.

- [ ] **Step 8.3.4: Commit**

```
git add src/Silmarillion.Module/
git commit -m "feat(silmarillion): open-in-window affordance for current detail view"
```

---

## Phase 9 — Cross-link wiring

Make `EntityChip.ClickCommand` invoke `IReferenceNavigator.Open`.

### Task 9.1: Wire EntityChip click → navigator

**Files:**
- Modify: `src/Silmarillion.Module/Views/ItemsTabView.xaml` / `RecipesTabView.xaml` (pass nav command into the chip templates)
- Modify: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs` or new helper VM (expose `OpenEntityCommand` taking `EntityRef`)

- [ ] **Step 9.1.1: Add `OpenEntityCommand` on the page-level VM**

```csharp
[RelayCommand]
private void OpenEntity(EntityRef? reference)
{
    if (reference is null) return;
    _navigator.Open(reference);
}
```

- [ ] **Step 9.1.2: Bind on each `EntityChip`**

In the `ItemTemplate`s that host `EntityChip`, set `ClickCommand` via `RelativeSource FindAncestor` to the page VM:

```xml
<local:EntityChip ClickCommand="{Binding DataContext.OpenEntityCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"/>
```

Inside `EntityChip.xaml.cs`, update the click handler to pass `Reference` as the parameter:

```csharp
public ICommand? ClickCommand { ... }
private void OnClick(object sender, RoutedEventArgs e)
{
    ClickCommand?.Execute(Reference);
}
```

(Wire the Button's `Click` event to `OnClick` in the chip's clickable template.)

- [ ] **Step 9.1.3: Add cross-link routing test**

Test that clicking a `ProducedByRecipes` chip on an item detail invokes `IReferenceNavigator.Open(EntityRef.Recipe(name))`.

- [ ] **Step 9.1.4: Build & smoke test**

Click item → click a "Used in" recipe chip → recipes tab activates, recipe detail shows. Back works.

- [ ] **Step 9.1.5: Commit**

```
git add src/Silmarillion.Module/
git commit -m "feat(silmarillion): wire EntityChip clicks to IReferenceNavigator.Open with auto tab-switching"
```

---

## Phase 10 — `DeepLinkRouter` migration

### Task 10.1: Refactor `DeepLinkRouter` to delegate through `IReferenceNavigator`

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs`
- Modify: `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs`

- [ ] **Step 10.1.1: Update `DeepLinkRouterTests`**

Replace `IItemDetailPresenter` test recordings with `IReferenceNavigator` recordings for the `mithril://item/...` case. Add new tests for `mithril://recipe/...`.

```csharp
[Fact]
public void ItemUri_Dispatches_ToReferenceNavigator()
{
    var nav = new RecordingNavigator();
    var router = new DeepLinkRouter(nav, /* other deps */ null, null, null, null);
    router.TryRoute("mithril://item/TomatoSauce").Should().BeTrue();
    nav.LastOpened.Should().Be(EntityRef.Item("TomatoSauce"));
}

[Fact]
public void RecipeUri_Dispatches_ToReferenceNavigator()
{
    var nav = new RecordingNavigator();
    var router = new DeepLinkRouter(nav, null, null, null, null);
    router.TryRoute("mithril://recipe/MakeSalsa").Should().BeTrue();
    nav.LastOpened.Should().Be(EntityRef.Recipe("MakeSalsa"));
}
```

(`RecordingNavigator` is a small stub implementing `IReferenceNavigator`.)

- [ ] **Step 10.1.2: Run tests, verify they fail** (signature mismatch — router doesn't yet take navigator).

- [ ] **Step 10.1.3: Refactor `DeepLinkRouter`**

- Add `IReferenceNavigator` to constructor (required dep).
- In the `mithril://item/<payload>` case, replace `_presenter.Show(payload)` with `_navigator.Open(EntityRef.Item(payload))`.
- Add new case for `mithril://recipe/<payload>` → `_navigator.Open(EntityRef.Recipe(payload))`. Use the same ASCII-identifier regex as for `item`.
- **Decide:** Keep `IItemDetailPresenter` ctor dep for backward compat (Celebrimbor still uses it directly for its own popup window flow — confirm by grepping for `IItemDetailPresenter.Show` consumers; if it's only the router that depended on it, drop the param; otherwise keep).

  Per inventory: `IItemDetailPresenter` has 3 `Show` overloads and is consumed by Celebrimbor for its existing popup window flow. The router can drop the dep — Celebrimbor injects `IItemDetailPresenter` directly. Confirm via grep before dropping.

  `grep -rn "IItemDetailPresenter" src/ | grep -v "// "` — examine results; if only `DeepLinkRouter` and Celebrimbor module reference it, dropping from router is safe.

- [ ] **Step 10.1.4: Run tests, verify pass**

```
dotnet test tests/Mithril.Shared.Tests --filter "FullyQualifiedName~DeepLinkRouterTests"
```

- [ ] **Step 10.1.5: Smoke test**

Run shell. Copy `mithril://item/TomatoSauce` to clipboard, trigger the clipboard-link handler. Verify it opens Silmarillion's Items tab with TomatoSauce selected (not the popup Window).

- [ ] **Step 10.1.6: Commit**

```
git add src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs
git commit -m "refactor(deep-links): route mithril://item/ via IReferenceNavigator; add mithril://recipe/ scheme"
```

---

## Phase 11 — Acceptance walkthrough + PR

### Task 11.1: Manual acceptance walk

Verify each acceptance criterion from the issue:

- [ ] New tab visible in shell, lazy-loaded.
- [ ] Items tab: search works (both grammar `Name = 'X'` and bare-text `tom`); cards render with icon + name + equip slot; click → master-detail right pane shows `ItemDetailView` populated with preview shapes + cross-link sections.
- [ ] Recipes tab: search works; cards render; click → master-detail right pane shows `RecipeDetailView` with ingredients + results + skill + description + text-stub ResultEffects.
- [ ] Cross-link clicks from item detail → recipe detail and back work; tab switches automatically.
- [ ] CanOpen → false chips render as plain text (verify by inspecting an item with a non-tabbed-kind cross-link source).
- [ ] Back/forward navigation: header buttons enable/disable, Alt+Left/Right work, mouse XButton1/XButton2 work.
- [ ] "Open in window" pops the appropriate Window. Multiple windows can be open simultaneously.
- [ ] `mithril://item/X` from clipboard opens the master-detail (not the popup Window).
- [ ] `mithril://recipe/X` from clipboard opens the master-detail.
- [ ] `IReferenceNavigator.Open(EntityRef.Item("X"))` invoked from another module opens the browser focused on that item (try from a debug button in any other module, or via a test).

### Task 11.2: Final build & tests

- [ ] `dotnet build Mithril.slnx` clean — 0 warnings, 0 errors.
- [ ] `dotnet test Mithril.slnx` — all green.

### Task 11.3: Push + open PR

- [ ] **Step 11.3.1: Push branch**

```
git push -u origin feat/207-silmarillion-reference-browser
```

- [ ] **Step 11.3.2: Open PR**

```
gh pr create --title "feat(silmarillion): reference-data browser module (Items + Recipes v1) — #207" --body "$(cat <<'EOF'
Closes #207.

## Summary

Ships the Silmarillion reference-data browser:

- New module `Silmarillion.Module` (lazy, SortOrder 950 — slot after Palantir)
- Real `IReferenceNavigator` with router-shaped Back/Forward history; replaces `NoOpReferenceNavigator` via DI override
- Two new cross-link indices on `IReferenceDataService`: `RecipesByProducedItem`, `RecipesByIngredientItem`
- `ItemDetailView` extracted from `ItemDetailWindow` (Window now a thin chrome host)
- `ItemDetailContext` extended with `ProducedByRecipes`, `ConsumedByRecipes`, `Sources` sections (hidden when null/empty — existing callers unaffected)
- `RecipeDetailView` UserControl + `RecipeDetailWindow` (greenfield)
- `EntityChip` shared UserControl with plain/clickable variants driven by `IReferenceNavigator.CanOpen`
- Master-detail UX in each tab: card list (left) + search (`MithrilQueryBox` + `QueryFilter`) + detail pane (right)
- Navigation chrome: header back/forward buttons + Alt+Left/Right + mouse XButton1/XButton2 + "open in window" affordance
- `DeepLinkRouter` migration: `mithril://item/X` now routes through `IReferenceNavigator.Open(EntityRef.Item(X))`; new `mithril://recipe/X` scheme

## Out of scope (deferred per #207 spec)

- Tabs for Abilities, NPCs, Quests, Effects, Lorebooks, Landmarks, Areas, PlayerTitles, StorageVaults — copy-the-pattern follow-ups
- Rich chip-template rendering of recipe `ResultEffects` — #214
- Migrating existing Celebrimbor `ItemDetailWindow` callers to `IReferenceNavigator.Open` — step 5 of #203
- `mithril://list/`, `mithril://pippin/`, `mithril://legolas/`, `mithril://elrond/` schemes keep their existing module-target dispatch (not entity refs)

## Stubs tracked

Greppable via `git grep "TODO(stub:#"`:

- `TODO(stub:#214)` — recipe `ResultEffects` rendered as plain strings; #214 replaces with rich chip templates
- (Add `TODO(stub:#NN)` for `RecipeSources` if deferred)

## Test plan

- [x] `dotnet build Mithril.slnx` clean
- [x] `dotnet test Mithril.slnx` passes
- [x] Manual acceptance walk per issue's Acceptance section
- [ ] Reviewer verification: copy `mithril://item/<known item>` to clipboard, observe Silmarillion opens to that item

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 11.3.3: Report PR URL to user.**

---

## Self-review notes

**Spec coverage check:**
- ✅ New tab visible, lazy-loaded → Phase 2 + 8
- ✅ Items + Recipes search + cards + master-detail → Phases 6, 7, 8
- ✅ Cross-link clicks → Phase 9
- ✅ Back/forward chrome (buttons + keyboard + mouse XButtons) → Phase 8
- ✅ "Open in window" affordance → Phase 8.3
- ✅ `mithril://item/` + `mithril://recipe/` via navigator → Phase 10
- ✅ Tests: VM-level + navigator + router → Phases 3, 6, 7, 10
- ✅ Stub-tracking convention → noted in Phase 4 + Plan header
- ✅ Forward-compat (UserControls hostable in both Window and master-detail) → Phases 1.3, 4.2, 4.3
- ⚠️ `RecipeSources` section conditional on `IReferenceDataService.RecipeSources` exposure — the inventory didn't confirm whether `sources_recipes.json` is parsed today. Implementation note: at Task 7.1 / Task 4.1, check `find_sources_recipes` / `sources_recipes.json` exposure on `IReferenceDataService`. If not exposed: defer the section, mark `// TODO(stub:#NEW)` and file a new follow-up issue.

**Placeholder scan:** No `TBD`, `implement later`, or `add appropriate X` strings in the plan body. Stub markers (`TODO(stub:#NN)`) are intentional and tracked.

**Type consistency:** `EntityChipVm` / `ItemSourceChipVm` (data carriers, Phase 1.2), `EntityChip` (UserControl, Phase 5.1), `ItemsTabViewModel`/`RecipesTabViewModel` (Phases 6.1, 7.1), `SilmarillionReferenceNavigator` (Phase 3.1) — referenced consistently across phases.

---

## Execution choice

Plan saved. Two options:

1. **Subagent-Driven (recommended)** — Fresh subagent per task with two-stage review between tasks. Best for catching design drift early; slower but higher quality.
2. **Inline Execution** — Execute tasks in this session with checkpoint reviews at phase boundaries. Faster; relies on shared context.

Suggest: inline execution with phase-boundary commits (each phase ends in a commit that's reviewable on its own). Given the codebase familiarity already built in this session, subagents would lose useful context.
