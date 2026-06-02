# Silmarillion polish v1 + registry refactors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bundle three #207-followup polish issues (#229 module-scoped deep-link routes, #231 compact row layout, #234 item-detail reading-order rework) with two parallel "switch-as-registry" refactors (DeepLinkRouter and IReferenceNavigator), into a single PR on branch `feat/silmarillion-polish-v1`.

**Architecture:** Refactor the existing `DeepLinkRouter` (a `switch (action)` with six near-identical cases) into a DI-registered `IDeepLinkHandler` registry. Each module's handler lives alongside its existing import-target implementation. Apply the same pattern to `IReferenceNavigator` (HashSet + two switches → `IReferenceKindTarget` registry) so the upcoming NPCs tab (the immediate next PR) is purely additive. Two XAML changes: compact two-line row templates for the master-detail left pane, and DockPanel-based restructure of `ItemDetailView` that lets cross-link sections move to the top.

**Tech Stack:** .NET 10, C# latest, WPF (`net10.0-windows`), CommunityToolkit.Mvvm, xunit + FluentAssertions. Build via `dotnet build Mithril.slnx`; test via `dotnet test`.

**Design spec:** [spec.md](spec.md) — every task references its spec section number.

**Tracked issues:** #229, #231, #234, #239 (bundled).

---

## File Structure

### Created files

| Path | Responsibility | Task |
|---|---|---|
| `src/Mithril.Shared.Wpf/Modules/IDeepLinkHandler.cs` | Handler interface (action + TryHandle) | T1 |
| `src/Mithril.Shared.Wpf/Modules/ItemDeepLinkHandler.cs` | `mithril://item/<name>` handler | T2 |
| `src/Mithril.Shared.Wpf/Modules/RecipeDeepLinkHandler.cs` | `mithril://recipe/<name>` handler | T2 |
| `src/Celebrimbor.Module/Services/CraftListDeepLinkHandler.cs` | `mithril://list/<base64>` handler | T5 |
| `src/Pippin.Module/Sharing/PippinDeepLinkHandler.cs` | `mithril://pippin/<base64>` handler | T6 |
| `src/Legolas.Module/Sharing/LegolasDeepLinkHandler.cs` | `mithril://legolas/<base64>` handler | T7 |
| `src/Elrond.Module/Services/ElrondDeepLinkHandler.cs` | `mithril://elrond/<skill>` handler | T8 |
| `src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs` | `mithril://silmarillion/<kind>/<name>` handler | T10 |
| `src/Mithril.Shared/Reference/IReferenceKindTarget.cs` | Kind-target interface | T13 |
| `src/Silmarillion.Module/Navigation/ItemsKindTarget.cs` | Items adapter for the registry | T14 |
| `src/Silmarillion.Module/Navigation/RecipesKindTarget.cs` | Recipes adapter for the registry | T15 |
| `tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs` | Handler-level tests (#229) | T9 |
| `tests/Silmarillion.Tests/Navigation/ItemsKindTargetTests.cs` | Items adapter tests (#239) | T14 |
| `tests/Silmarillion.Tests/Navigation/RecipesKindTargetTests.cs` | Recipes adapter tests (#239) | T15 |
| `tests/Silmarillion.Tests/ViewModels/SilmarillionViewModelTests.cs` | Top-level VM tests for OnNavigated / OpenInWindow (#239) | T17 |

### Modified files

| Path | Change | Task |
|---|---|---|
| `src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs` | Rewrite ~170 → ~40 lines as registry dispatcher | T3 |
| `src/Mithril.Shell/DependencyInjection/ShellServiceCollectionExtensions.cs` | Register Item/Recipe handlers from shell DI; rewire DeepLinkRouter ctor | T3 |
| `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs` | Re-wire to register handlers via DI; add silmarillion integration test in T9 | T4, T9 |
| `src/Celebrimbor.Module/CelebrimborModule.cs` | Register `CraftListDeepLinkHandler` | T5 |
| `src/Pippin.Module/PippinModule.cs` | Register `PippinDeepLinkHandler` | T6 |
| `src/Legolas.Module/LegolasModule.cs` | Register `LegolasDeepLinkHandler` | T7 |
| `src/Elrond.Module/ElrondModule.cs` | Register `ElrondDeepLinkHandler` | T8 |
| `src/Silmarillion.Module/SilmarillionModule.cs` | Register `SilmarillionDeepLinkHandler` + 2 kind targets | T10, T16 |
| `src/Silmarillion.Module/Views/Resources.xaml` | Replace card templates with compact rows | T11 |
| `src/Mithril.Shared.Wpf/ItemDetailView.xaml` | DockPanel + StackPanel + cross-link reorder | T12 |
| `src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs` | `V1TabbedKinds` deleted; ctor takes `IEnumerable<IReferenceKindTarget>` | T16 |
| `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs` | Switches → registry lookups | T17 |
| `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs` | Re-wire `CanOpen` tests to register targets; add duplicate-kind test | T16 |
| `src/Mithril.Shell/Views/AboutSettingsView.xaml` | Update example URI to module-scoped form | T18 |
| `[project-gorgon.wiki]/Deep-Linking.md` (or equivalent) | Document preferred form; legacy noted | T18 |

---

## Commit map

| Commit | Tasks | Description |
|---|---|---|
| 1 | T1–T8 | DeepLink router → handler-registry refactor (no new URI behavior) |
| 2 | T9–T10 | Silmarillion deep-link handler + tests (#229) |
| 3 | T11 | Compact row templates (#231) |
| 4 | T12 | ItemDetailView restructure (#234) |
| 5 | T13–T17 | Navigator → kind-target registry refactor (#239) |
| 6 | T18 | Wiki update + AboutSettingsView example string |

The branch `feat/silmarillion-polish-v1` already exists with two prior commits (spec + spec correction). Push and open PR via `gh pr create` after T18.

---

## Task 1: Define `IDeepLinkHandler` interface

**Files:**
- Create: `src/Mithril.Shared.Wpf/Modules/IDeepLinkHandler.cs`

Spec §1.

- [ ] **Step 1: Create the interface file**

```csharp
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Modules;

/// <summary>
/// A single mithril:// scheme dispatch handler. The <see cref="DeepLinkRouter"/>
/// looks handlers up by <see cref="Action"/> (the URI host segment) and delegates
/// payload parsing and dispatch to each implementation.
///
/// Replaces the per-action <c>switch</c> the router used pre-#229. Each handler
/// owns its payload grammar (regex, length cap, segment-splitting), its dependency
/// null-checks, and its diagnostic messages.
/// </summary>
public interface IDeepLinkHandler
{
    /// <summary>The first path segment after <c>mithril://</c>. Must be lowercase ASCII.</summary>
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

- [ ] **Step 2: Build to verify the file compiles**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: build succeeds (interface alone introduces no consumer).

---

## Task 2: Extract Item + Recipe handlers

**Files:**
- Create: `src/Mithril.Shared.Wpf/Modules/ItemDeepLinkHandler.cs`
- Create: `src/Mithril.Shared.Wpf/Modules/RecipeDeepLinkHandler.cs`

Spec §2. Both handlers depend only on `IReferenceNavigator` so they stay in the shared layer and degrade gracefully when Silmarillion isn't loaded (`NoOpReferenceNavigator` accepts the call).

- [ ] **Step 1: Create `ItemDeepLinkHandler.cs`**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>
/// Handles <c>mithril://item/&lt;internalName&gt;</c>. Internal names in the reference
/// data are ASCII identifiers; the regex refuses anything that could confuse downstream
/// lookups or smuggle separators.
/// </summary>
public sealed class ItemDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public ItemDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "item";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: item payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Item(subPath));
        return true;
    }
}
```

- [ ] **Step 2: Create `RecipeDeepLinkHandler.cs` (same shape)**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;

namespace Mithril.Shared.Modules;

/// <summary>Handles <c>mithril://recipe/&lt;internalName&gt;</c>. See <see cref="ItemDeepLinkHandler"/>.</summary>
public sealed class RecipeDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public RecipeDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "recipe";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: recipe payload '{subPath}' failed validation.");
            return false;
        }
        _navigator.Open(EntityRef.Recipe(subPath));
        return true;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Mithril.Shared.Wpf/Mithril.Shared.Wpf.csproj`
Expected: build succeeds.

---

## Task 3: Rewrite `DeepLinkRouter` + shell DI

**Files:**
- Modify: `src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs`
- Modify: `src/Mithril.Shell/DependencyInjection/ShellServiceCollectionExtensions.cs:84-90`

Spec §3.

- [ ] **Step 1: Replace `DeepLinkRouter.cs` body**

Replace the entire file content with:

```csharp
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Modules;

/// <summary>
/// Default <see cref="IDeepLinkRouter"/>. Builds a per-action lookup from injected
/// <see cref="IDeepLinkHandler"/>s and delegates payload parsing + dispatch to the
/// matching handler. Each handler owns its payload grammar; the router only
/// validates the scheme and finds the right handler.
/// </summary>
public sealed class DeepLinkRouter : IDeepLinkRouter
{
    private const string Scheme = "mithril";

    private readonly IReadOnlyDictionary<string, IDeepLinkHandler> _handlers;
    private readonly IDiagnosticsSink? _diag;

    public DeepLinkRouter(IEnumerable<IDeepLinkHandler> handlers, IDiagnosticsSink? diag = null)
    {
        // Fail-loud on duplicate Action registrations — that's a DI ordering bug,
        // not graceful-degradation territory.
        var byAction = new Dictionary<string, IDeepLinkHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            var key = h.Action.ToLowerInvariant();
            if (byAction.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Duplicate IDeepLinkHandler registration for action '{key}': " +
                    $"{byAction[key].GetType().FullName} and {h.GetType().FullName}.");
            byAction[key] = h;
        }
        _handlers = byAction;
        _diag = diag;
    }

    public bool Handle(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            _diag?.Info("DeepLink", $"Rejected: not a well-formed URI: '{uri}'.");
            return false;
        }
        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            _diag?.Info("DeepLink", $"Rejected: scheme '{parsed.Scheme}' is not 'mithril'.");
            return false;
        }

        var action = parsed.Host.ToLowerInvariant();
        var subPath = parsed.AbsolutePath.TrimStart('/');

        if (!_handlers.TryGetValue(action, out var handler))
        {
            _diag?.Info("DeepLink", $"Rejected: unknown action '{action}'.");
            return false;
        }
        return handler.TryHandle(subPath, _diag);
    }
}
```

- [ ] **Step 2: Update `ShellServiceCollectionExtensions.AddMithrilItemDetail`**

Find the existing block in `src/Mithril.Shell/DependencyInjection/ShellServiceCollectionExtensions.cs` (around lines 77–90) and replace the `DeepLinkRouter` registration. The new shape — pasted in full so the executor doesn't have to diff:

```csharp
    public static IServiceCollection AddMithrilItemDetail(this IServiceCollection services) =>
        services
            .AddSingleton<IItemDetailPresenter, ItemDetailPresenter>()
            .AddSingleton<IModuleActivator, ShellModuleActivator>()
            // Shell-side deep-link handlers: depend only on IReferenceNavigator
            // (NoOp when Silmarillion isn't loaded), so they belong here, not in
            // a module's Register(). Modules register their own action handlers.
            .AddSingleton<IDeepLinkHandler, ItemDeepLinkHandler>()
            .AddSingleton<IDeepLinkHandler, RecipeDeepLinkHandler>()
            // Router pulls every registered IDeepLinkHandler. Module-side handlers
            // register from their own IMithrilModule.Register() implementations.
            .AddSingleton<IDeepLinkRouter>(sp => new DeepLinkRouter(
                sp.GetServices<IDeepLinkHandler>(),
                sp.GetService<IDiagnosticsSink>()));
```

Drop the old `using` for `ICraftListImportTarget` etc. if it's now unused. Drop the comment line about "factory form so module-side import targets and IDiagnosticsSink stay optional" — it's no longer accurate.

- [ ] **Step 3: Build the whole solution**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds. Tests don't compile yet (DeepLinkRouterTests still calls the old constructor) — that's Task 4.

---

## Task 4: Re-wire `DeepLinkRouterTests` to use handlers

**Files:**
- Modify: `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs`

Spec §"Router refactor (commit 1)" testing. Existing test cases all stay; the test factory shape changes.

- [ ] **Step 1: Add a test-side handler-builder helper**

At the bottom of the test class (inside the same `DeepLinkRouterTests` class but above the private `Recording*` types), add a private factory:

```csharp
    private static DeepLinkRouter BuildRouter(
        IReferenceNavigator nav,
        ICraftListImportTarget? listTarget = null,
        IPippinShareImportTarget? pippinTarget = null,
        ILegolasShareImportTarget? legolasTarget = null,
        IElrondSkillImportTarget? elrondTarget = null)
    {
        var handlers = new List<IDeepLinkHandler>
        {
            new ItemDeepLinkHandler(nav),
            new RecipeDeepLinkHandler(nav),
        };
        if (listTarget is not null)
            handlers.Add(new Celebrimbor.Services.CraftListDeepLinkHandler(listTarget));
        if (pippinTarget is not null)
            handlers.Add(new Pippin.Sharing.PippinDeepLinkHandler(pippinTarget));
        if (legolasTarget is not null)
            handlers.Add(new Legolas.Sharing.LegolasDeepLinkHandler(legolasTarget));
        if (elrondTarget is not null)
            handlers.Add(new Elrond.Services.ElrondDeepLinkHandler(elrondTarget));
        return new DeepLinkRouter(handlers);
    }
```

This won't compile yet — the module-side handlers don't exist. Tasks 5–8 add them and add the needed project references. Add a `// TODO(T4): finalise project refs once T5-T8 land` comment above the helper and proceed; the test project gains those refs in Tasks 5–8.

- [ ] **Step 2: Replace each `new DeepLinkRouter(...)` call site**

Search for `new DeepLinkRouter` in the file. Replace each construction with a `BuildRouter(...)` call passing the matching targets. Example diff for one site:

```csharp
// Before:
var router = new DeepLinkRouter(nav, listImport: null, pippinImport: pippinTarget);

// After:
var router = BuildRouter(nav, pippinTarget: pippinTarget);
```

Every old call site has a structurally-identical replacement; the helper's named parameters map 1:1 to the old constructor's optional arguments.

- [ ] **Step 3: Add a new test for duplicate-action registration**

Append to `DeepLinkRouterTests`:

```csharp
    [Fact]
    public void Constructor_DuplicateAction_Throws()
    {
        var nav = new RecordingNavigator();
        var handlers = new IDeepLinkHandler[]
        {
            new ItemDeepLinkHandler(nav),
            new ItemDeepLinkHandler(nav),  // duplicate
        };
        var act = () => new DeepLinkRouter(handlers);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate IDeepLinkHandler*item*");
    }
```

- [ ] **Step 4: Build (will fail until T5–T8)**

Run: `dotnet build tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj`
Expected: compile errors for the module-side handler types referenced in `BuildRouter`. Acceptable — Tasks 5–8 add them.

---

## Task 5: Celebrimbor `CraftListDeepLinkHandler`

**Files:**
- Create: `src/Celebrimbor.Module/Services/CraftListDeepLinkHandler.cs`
- Modify: `src/Celebrimbor.Module/CelebrimborModule.cs` (register handler)
- Modify: `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` (add ProjectReference to Celebrimbor.Module)

Spec §2.

- [ ] **Step 1: Create the handler**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Celebrimbor.Services;

/// <summary>Handles <c>mithril://list/&lt;base64url&gt;</c> craft-list imports.</summary>
public sealed class CraftListDeepLinkHandler : IDeepLinkHandler
{
    // base64url alphabet = [A-Za-z0-9_-]. Length cap matches the pre-registry behaviour.
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly ICraftListImportTarget _target;

    public CraftListDeepLinkHandler(ICraftListImportTarget target) => _target = target;

    public string Action => "list";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: list payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
```

- [ ] **Step 2: Register in `CelebrimborModule.Register`**

Find `services.AddSingleton<ICraftListImportTarget, CraftListImportTarget>();` (around line 36) and add immediately after:

```csharp
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new CraftListDeepLinkHandler(sp.GetRequiredService<ICraftListImportTarget>()));
```

Add the `using Mithril.Shared.Modules;` import at the top if it isn't already there.

- [ ] **Step 3: Add ProjectReference from test project**

In `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj`, ensure there's a `<ProjectReference Include="..\..\src\Celebrimbor.Module\Celebrimbor.Module.csproj" />` entry. If not, add it inside the existing `<ItemGroup>` of project references.

- [ ] **Step 4: Build solution**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds for Celebrimbor.Module and the test project (other handlers still missing — those tests fail to compile until T6/T7/T8).

---

## Task 6: Pippin `PippinDeepLinkHandler`

**Files:**
- Create: `src/Pippin.Module/Sharing/PippinDeepLinkHandler.cs`
- Modify: `src/Pippin.Module/PippinModule.cs`
- Modify: `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` (ProjectReference to Pippin.Module — likely already present via existing share-target tests; verify)

Spec §2.

- [ ] **Step 1: Create the handler**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Pippin.Sharing;

/// <summary>Handles <c>mithril://pippin/&lt;base64url&gt;</c> shared-progress imports.</summary>
public sealed class PippinDeepLinkHandler : IDeepLinkHandler
{
    // Pippin payloads are larger than list because they encode the full-catalogue progress dump.
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,16384}$", RegexOptions.Compiled);

    private readonly IPippinShareImportTarget _target;

    public PippinDeepLinkHandler(IPippinShareImportTarget target) => _target = target;

    public string Action => "pippin";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: pippin payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
```

- [ ] **Step 2: Register in `PippinModule.Register`**

Find the existing `services.AddSingleton<IPippinShareImportTarget>(...)` block (around line 71) and add immediately after:

```csharp
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new PippinDeepLinkHandler(sp.GetRequiredService<IPippinShareImportTarget>()));
```

Add `using Mithril.Shared.Modules;` at the top if missing.

- [ ] **Step 3: Build**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds for Pippin.Module; test project still fails until T7/T8.

---

## Task 7: Legolas `LegolasDeepLinkHandler`

**Files:**
- Create: `src/Legolas.Module/Sharing/LegolasDeepLinkHandler.cs`
- Modify: `src/Legolas.Module/LegolasModule.cs`
- Modify: `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` (verify ProjectReference)

Spec §2.

- [ ] **Step 1: Create the handler**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Legolas.Sharing;

/// <summary>Handles <c>mithril://legolas/&lt;base64url&gt;</c> survey-report imports.</summary>
public sealed class LegolasDeepLinkHandler : IDeepLinkHandler
{
    // Legolas payloads are bounded by a single survey run (≤ a couple dozen items + timestamps).
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_-]{1,8192}$", RegexOptions.Compiled);

    private readonly ILegolasShareImportTarget _target;

    public LegolasDeepLinkHandler(ILegolasShareImportTarget target) => _target = target;

    public string Action => "legolas";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: legolas payload (len={subPath.Length}) failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
```

- [ ] **Step 2: Register in `LegolasModule.Register`**

Find the `services.AddSingleton<ILegolasShareImportTarget>(...)` block and add immediately after:

```csharp
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new LegolasDeepLinkHandler(sp.GetRequiredService<ILegolasShareImportTarget>()));
```

Add `using Mithril.Shared.Modules;` at the top if missing.

- [ ] **Step 3: Build**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds for Legolas.Module.

---

## Task 8: Elrond `ElrondDeepLinkHandler` + close commit 1

**Files:**
- Create: `src/Elrond.Module/Services/ElrondDeepLinkHandler.cs`
- Modify: `src/Elrond.Module/ElrondModule.cs`
- Modify: `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` (verify ProjectReference)

Spec §2.

- [ ] **Step 1: Create the handler**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;

namespace Elrond.Services;

/// <summary>
/// Handles <c>mithril://elrond/&lt;skillKey&gt;</c>. Skill keys are id-shaped
/// (matches <c>SkillEntry.Key</c>); reuse the strict item-style pattern. Hyphens
/// and spaces are explicitly NOT permitted — the human-readable display name is
/// never on the wire.
/// </summary>
public sealed class ElrondDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex PayloadPattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IElrondSkillImportTarget _target;

    public ElrondDeepLinkHandler(IElrondSkillImportTarget target) => _target = target;

    public string Action => "elrond";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (!PayloadPattern.IsMatch(subPath))
        {
            diag?.Info("DeepLink", $"Rejected: elrond payload '{subPath}' failed validation.");
            return false;
        }
        _target.ImportFromLinkPayload(subPath);
        return true;
    }
}
```

- [ ] **Step 2: Register in `ElrondModule.Register`**

Find the `services.AddSingleton<IElrondSkillImportTarget>(...)` block (around line 42) and add immediately after:

```csharp
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new ElrondDeepLinkHandler(sp.GetRequiredService<IElrondSkillImportTarget>()));
```

Add `using Mithril.Shared.Modules;` at the top if missing.

- [ ] **Step 3: Remove the TODO marker in DeepLinkRouterTests**

Open `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs` and delete the `// TODO(T4): finalise project refs once T5-T8 land` comment added in T4 — the project refs now resolve.

- [ ] **Step 4: Build + run all DeepLinkRouter tests**

Run:
```
dotnet build Mithril.slnx
dotnet test tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj --filter "FullyQualifiedName~DeepLinkRouterTests" -v normal
```
Expected: all DeepLinkRouter tests pass (including the new `Constructor_DuplicateAction_Throws`). Coverage parity with pre-refactor.

- [ ] **Step 5: Commit (commit 1)**

```
git add src/Mithril.Shared.Wpf/Modules/IDeepLinkHandler.cs \
        src/Mithril.Shared.Wpf/Modules/ItemDeepLinkHandler.cs \
        src/Mithril.Shared.Wpf/Modules/RecipeDeepLinkHandler.cs \
        src/Mithril.Shared.Wpf/Modules/DeepLinkRouter.cs \
        src/Mithril.Shell/DependencyInjection/ShellServiceCollectionExtensions.cs \
        src/Celebrimbor.Module/Services/CraftListDeepLinkHandler.cs \
        src/Celebrimbor.Module/CelebrimborModule.cs \
        src/Pippin.Module/Sharing/PippinDeepLinkHandler.cs \
        src/Pippin.Module/PippinModule.cs \
        src/Legolas.Module/Sharing/LegolasDeepLinkHandler.cs \
        src/Legolas.Module/LegolasModule.cs \
        src/Elrond.Module/Services/ElrondDeepLinkHandler.cs \
        src/Elrond.Module/ElrondModule.cs \
        tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs \
        tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj
```

Commit message:
```
refactor(deep-link): IDeepLinkRouter switch → IDeepLinkHandler registry

The DeepLinkRouter's switch (action) had grown to six structurally-identical
cases (validate payload, null-check optional import target, dispatch, log).
Extract each branch into an IDeepLinkHandler implementation; the router
becomes a registry lookup keyed by Action.

Per-module handlers register from each IMithrilModule.Register(). Item +
Recipe stay in Mithril.Shared.Wpf since they depend only on
IReferenceNavigator (NoOp-friendly when Silmarillion isn't loaded).

No new URI behavior. All existing DeepLinkRouter tests pass.

Precursor to #229 (silmarillion-scoped routes).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 9: SilmarillionDeepLinkHandler — failing tests

**Files:**
- Create: `tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs`

Spec §4 + "Silmarillion handler (commit 2)" testing.

- [ ] **Step 1: Create the test file**

```csharp
using FluentAssertions;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class SilmarillionDeepLinkHandlerTests
{
    [Fact]
    public void TryHandle_ItemKind_OpensItem()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("item/CraftedLeatherBoots5", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));
    }

    [Fact]
    public void TryHandle_RecipeKind_OpensRecipe()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var handled = handler.TryHandle("recipe/MakeTomatoSauce", diag: null);

        handled.Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }

    [Theory]
    [InlineData("npc/Marna")]            // unknown kind segment
    [InlineData("ability/Hatchet")]
    [InlineData("")]                     // empty subPath
    [InlineData("item")]                 // no second segment
    [InlineData("item/")]                // empty payload after slash
    [InlineData("item/has space")]       // illegal payload char
    [InlineData("item/has-hyphen")]      // hyphen rejected by EntityPayloadPattern
    [InlineData("item/extra/segment")]   // extra path segments forbidden
    public void TryHandle_Malformed_ReturnsFalse_AndDoesNotDispatch(string subPath)
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        handler.TryHandle(subPath, diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void TryHandle_PayloadOverLengthCap_IsRejected()
    {
        var nav = new RecordingNavigator();
        var handler = new SilmarillionDeepLinkHandler(nav);

        var tooLong = new string('A', 129);
        handler.TryHandle($"item/{tooLong}", diag: null).Should().BeFalse();
        nav.LastOpened.Should().BeNull();
    }

    [Fact]
    public void Action_IsSilmarillion()
    {
        var nav = new RecordingNavigator();
        new SilmarillionDeepLinkHandler(nav).Action.Should().Be("silmarillion");
    }

    private sealed class RecordingNavigator : IReferenceNavigator
    {
        public EntityRef? LastOpened { get; private set; }
        public EntityRef? Current => LastOpened;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool CanOpen(EntityRef reference) => true;
        public void Open(EntityRef reference) => LastOpened = reference;
        public void Back() { }
        public void Forward() { }
        public event EventHandler<NavigatedEventArgs>? Navigated { add { } remove { } }
    }
}
```

- [ ] **Step 2: Verify the tests fail (handler doesn't exist yet)**

Run: `dotnet build tests/Silmarillion.Tests/Silmarillion.Tests.csproj`
Expected: build fails — `SilmarillionDeepLinkHandler` type not found. Acceptable; Task 10 creates it.

---

## Task 10: SilmarillionDeepLinkHandler — implementation + commit 2

**Files:**
- Create: `src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs`
- Modify: `src/Silmarillion.Module/SilmarillionModule.cs`
- Modify: `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs` (add integration test)
- Modify: `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` (ProjectReference to Silmarillion.Module if absent)

Spec §4.

- [ ] **Step 1: Create the handler**

```csharp
using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Handles <c>mithril://silmarillion/&lt;kind&gt;/&lt;internalName&gt;</c> — the
/// module-scoped form (issue #229). Symmetric with <c>mithril://pippin/...</c>,
/// <c>mithril://legolas/...</c>, etc. The legacy single-kind forms
/// (<c>mithril://item/...</c> / <c>mithril://recipe/...</c>) remain supported
/// via <see cref="ItemDeepLinkHandler"/> / <see cref="RecipeDeepLinkHandler"/>.
/// </summary>
public sealed class SilmarillionDeepLinkHandler : IDeepLinkHandler
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9_]{1,128}$", RegexOptions.Compiled);

    private readonly IReferenceNavigator _navigator;

    public SilmarillionDeepLinkHandler(IReferenceNavigator navigator) => _navigator = navigator;

    public string Action => "silmarillion";

    public bool TryHandle(string subPath, IDiagnosticsSink? diag)
    {
        if (string.IsNullOrEmpty(subPath))
        {
            diag?.Info("DeepLink", "Rejected: silmarillion payload is empty.");
            return false;
        }

        // Strictly two segments: kind/name. Extra segments are rejected so the
        // grammar stays unambiguous when future kinds ship.
        var slash = subPath.IndexOf('/');
        if (slash < 0 || slash == subPath.Length - 1)
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion payload '{subPath}' missing kind or name segment.");
            return false;
        }
        var kind = subPath.AsSpan(0, slash).ToString().ToLowerInvariant();
        var name = subPath[(slash + 1)..];
        if (name.Contains('/'))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion payload '{subPath}' has extra segments.");
            return false;
        }
        if (!NamePattern.IsMatch(name))
        {
            diag?.Info("DeepLink", $"Rejected: silmarillion name '{name}' failed validation.");
            return false;
        }

        switch (kind)
        {
            case "item":
                _navigator.Open(EntityRef.Item(name));
                return true;
            case "recipe":
                _navigator.Open(EntityRef.Recipe(name));
                return true;
            default:
                diag?.Info("DeepLink", $"Rejected: silmarillion kind '{kind}' is not yet routable.");
                return false;
        }
    }
}
```

- [ ] **Step 2: Register in `SilmarillionModule.Register`**

Open `src/Silmarillion.Module/SilmarillionModule.cs`. Find the existing `Register(IServiceCollection)`. Add immediately after the `services.AddSingleton<IReferenceNavigator, SilmarillionReferenceNavigator>();` line:

```csharp
        // Module-scoped mithril://silmarillion/<kind>/<name> route (issue #229).
        // Legacy mithril://item/<name> / mithril://recipe/<name> remain wired in
        // Mithril.Shared.Wpf.
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new SilmarillionDeepLinkHandler(sp.GetRequiredService<IReferenceNavigator>()));
```

Add `using Mithril.Shared.Modules;` and `using Silmarillion.Navigation;` at the top if missing.

- [ ] **Step 3: Run handler tests**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj --filter "FullyQualifiedName~SilmarillionDeepLinkHandlerTests" -v normal`
Expected: all tests from Task 9 pass.

- [ ] **Step 4: Add router-level integration test**

In `tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs`, append:

```csharp
    [Fact]
    public void SilmarillionRoute_DispatchedToReferenceNavigator()
    {
        var nav = new RecordingNavigator();
        var router = new DeepLinkRouter(new IDeepLinkHandler[]
        {
            new ItemDeepLinkHandler(nav),
            new RecipeDeepLinkHandler(nav),
            new Silmarillion.Navigation.SilmarillionDeepLinkHandler(nav),
        });

        router.Handle("mithril://silmarillion/item/CraftedLeatherBoots5").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Item("CraftedLeatherBoots5"));

        router.Handle("mithril://silmarillion/recipe/MakeTomatoSauce").Should().BeTrue();
        nav.LastOpened.Should().Be(EntityRef.Recipe("MakeTomatoSauce"));
    }
```

If `tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj` doesn't already reference `Silmarillion.Module.csproj`, add the `<ProjectReference>` entry to the existing references `<ItemGroup>`.

- [ ] **Step 5: Run all tests**

Run: `dotnet test Mithril.slnx --filter "FullyQualifiedName~DeepLinkRouter|FullyQualifiedName~SilmarillionDeepLinkHandler"`
Expected: all pass.

- [ ] **Step 6: Commit (commit 2)**

```
git add src/Silmarillion.Module/Navigation/SilmarillionDeepLinkHandler.cs \
        src/Silmarillion.Module/SilmarillionModule.cs \
        tests/Silmarillion.Tests/Navigation/SilmarillionDeepLinkHandlerTests.cs \
        tests/Mithril.Shared.Tests/Modules/DeepLinkRouterTests.cs \
        tests/Mithril.Shared.Tests/Mithril.Shared.Tests.csproj
```

Commit message:
```
feat(silmarillion): mithril://silmarillion/<kind>/<name> deep-link route (#229)

Module-scoped form symmetric with mithril://pippin/..., mithril://legolas/...,
etc. Strictly two-segment grammar (kind + name); unknown kinds rejected for
forward-compat as Bucket-B tabs ship.

Legacy mithril://item/<name> and mithril://recipe/<name> remain supported.
The about-settings panel and wiki Deep-Linking page are updated to point at
the preferred form in commit 6.

Closes #229.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 11: Compact row templates (commit 3)

**Files:**
- Modify: `src/Silmarillion.Module/Views/Resources.xaml`

Spec §5.

- [ ] **Step 1: Replace both DataTemplates**

Open the file. Replace the entire `ItemCardTemplate` and `RecipeCardTemplate` blocks (current content shown for context, around lines 11–52). New content:

```xml
    <!-- Compact two-line row for Items tab. Bound DataContext: Mithril.Reference.Models.Items.Item.
         Replaces the card layout in #231; ~36px per row, ~40+ visible. Key name kept as
         "*CardTemplate" to avoid rippling into both tab views — names are wallpaper. -->
    <DataTemplate x:Key="ItemCardTemplate">
        <DockPanel LastChildFill="True">
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

    <!-- Compact two-line row for Recipes tab. Bound DataContext: Silmarillion.ViewModels.RecipeListRow. -->
    <DataTemplate x:Key="RecipeCardTemplate">
        <DockPanel LastChildFill="True">
            <wpf:IconImage DockPanel.Dock="Left" IconId="{Binding IconId}"
                           Width="24" Height="24" Margin="6,3,6,3" VerticalAlignment="Center"/>
            <StackPanel VerticalAlignment="Center" Margin="0,3,6,3">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold"
                           Foreground="#FFD4A847" TextTrimming="CharacterEllipsis"
                           FontSize="{DynamicResource AppFontSizeHint}"/>
                <TextBlock Foreground="#88FFFFFF"
                           FontSize="{DynamicResource AppFontSizeSmall}"
                           TextTrimming="CharacterEllipsis"
                           Visibility="{Binding SkillDisplayName, Converter={StaticResource NullOrEmptyToVis}}">
                    <Run Text="{Binding SkillDisplayName, Mode=OneWay}"/>
                    <Run Text=" "/>
                    <Run Text="{Binding SkillLevelReq, Mode=OneWay}"/>
                </TextBlock>
            </StackPanel>
        </DockPanel>
    </DataTemplate>
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds (XAML compiles).

- [ ] **Step 3: Manual visual verification**

Launch: `dotnet run --project src/Mithril.Shell`

Navigate to Silmarillion module. Confirm on both tabs (Items + Recipes):
- Rows are ~36px tall (24px icon + tight padding).
- Two-line layout: name on line 1 in gold/semibold; subtitle on line 2 in dim grey/small.
- Subtitle hides for items without an equip slot (e.g. consumables).
- Long names truncate with `…` ellipsis rather than wrapping.
- Hover and selection accent still work.
- ~40+ rows fit in the visible area (vs ~17 before).
- Scroll smoothness unchanged (virtualization still on).

Close the app.

- [ ] **Step 4: Commit (commit 3)**

```
git add src/Silmarillion.Module/Views/Resources.xaml
git commit -m "$(cat <<'EOF'
feat(silmarillion): compact two-line row layout for browse lists (#231)

Replace card-shaped DataTemplates with row-shaped compact layout: 24px
icon + name (line 1) + dim subtitle (line 2, equip-slot for items /
skill+level for recipes). Row height drops from ~80px to ~36px; visible
row count rises from ~17 to ~40+.

Card layout was inherited from Celebrimbor's RecipeCard, but Celebrimbor
uses cards as inspect-popups while Silmarillion uses them for browsable
catalogue navigation — density beats card-shape for the browse use case.

Closes #231.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: ItemDetailView restructure (commit 4)

**Files:**
- Modify: `src/Mithril.Shared.Wpf/ItemDetailView.xaml`

Spec §6.

- [ ] **Step 1: Replace the top-level structure**

The current file uses `<Border>` → `<Grid>` with 23 `<RowDefinition>`s and one `<TextBlock Grid.Row="22" VerticalAlignment="Bottom">` for the footer. Replace with `<Border>` → `<DockPanel>` → footer-docked-bottom + body `<StackPanel>`.

Apply this targeted edit (preserve every existing child element's content; only their containers change, and the cross-link sections move):

Replace:
```xml
    <Border Padding="14,12">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="*"/>
                <!-- ...20 more RowDefinitions... -->
            </Grid.RowDefinitions>
            <!-- existing children with Grid.Row="N" -->
```

With:
```xml
    <Border Padding="14,12">
        <DockPanel>
            <!-- Internal-name footer pins to the bottom of the DockPanel. With the
                 host ContentControl bound to ScrollViewer.ViewportHeight, the
                 DockPanel stretches to viewport and the footer pins to the bottom
                 of the right pane even for sparse items. Matches the pattern
                 already used by RecipeDetailView. -->
            <TextBlock DockPanel.Dock="Bottom" Text="{Binding InternalName}"
                       Foreground="#88FFFFFF"
                       FontFamily="{DynamicResource AppMonoFontFamily}"
                       FontSize="{DynamicResource AppFontSizeSmall}"
                       Margin="0,8,0,0" HorizontalAlignment="Right"/>
            <StackPanel>
                <!-- children below, reordered: cross-links float up to right
                     after Description (was rows 19-21, now rows 2-4). -->
```

Strip every `Grid.Row="N"` attribute from the child `<DockPanel>` / `<StackPanel>` blocks. Remove the old footer `<TextBlock Grid.Row="22">` block (replaced by the docked footer above).

Re-order the body children to this sequence (each becomes a direct child of the new `<StackPanel>`):

1. Header `<DockPanel>` (was Row 0)
2. Description `<StackPanel>` (was Row 1)
3. **Sources `<StackPanel>`** (was Row 19 — moved up)
4. **Produced by `<StackPanel>`** (was Row 20 — moved up)
5. **Used in `<StackPanel>`** (was Row 21 — moved up)
6. Skill requirements `<StackPanel>` (was Row 2)
7. Effects `<StackPanel>` (was Row 3 — drop the `<ScrollViewer MaxHeight="480">` wrapper? **No** — keep it as-is; it caps the inline effects panel's height inside the parent scroll. The outer ItemsTabView ScrollViewer is the one with the host MinHeight binding.)
8. Augmentation (was Row 4)
9. Applied augment waxes (was Row 5)
10. Infusions (was Row 6)
11. Treasure Effects (was Row 7)
12. Teaches recipe (was Row 8)
13. Additional effects (was Row 9)
14. Progresses research (was Row 10)
15. Grants XP (was Row 11)
16. Reveals words of power (was Row 12)
17. Teaches ability (was Row 13)
18. Produces (was Row 14)
19. Bonuses while equipped (was Row 15)
20. Modifies crafted item (was Row 16)
21. Cooldown adjustments (was Row 17)
22. Extraction (was Row 18)

Each `<StackPanel>`'s existing `Visibility="{Binding ...Count, Converter={StaticResource PositiveIntToVis}}"` binding is preserved verbatim — that's still how empty sections hide.

For the Effects section specifically: drop the `Height="*"` semantics (was on `Grid.Row="3"`'s `RowDefinition`); the section becomes a plain `<StackPanel>` like the others. The inner `<ScrollViewer MaxHeight="480">` stays.

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: build succeeds. If there are XAML compile errors, the most likely cause is a leftover `Grid.Row="N"` attribute on a child — search the file for `Grid.Row` and remove every occurrence.

- [ ] **Step 3: Manual visual verification**

Launch: `dotnet run --project src/Mithril.Shell`

Navigate to Silmarillion → Items. Verify:

**Sparse item** (e.g., Lorebook 1, candle wick, or any material with no equip slot and no effects): Sources / Produced by / Used in sections render immediately below the description. Internal-name footer is at the bottom of the right pane regardless of scroll position. No empty band between description and cross-links.

**Effect-heavy item** (e.g., any crafted weapon at tier 5+ with Treasure Effects): all sections render in the new order. The "Effects" section's internal `MaxHeight="480"` scroller still works for very long effect lists. Scrolling the outer pane works. No visual regressions.

**Popup window**: right-click an item (or use the OpenInWindow command path) to open `ItemDetailWindow`. The popup sizes to content so the footer-pin behavior is irrelevant; verify the new section order looks reasonable.

Close the app.

- [ ] **Step 4: Commit (commit 4)**

```
git add src/Mithril.Shared.Wpf/ItemDetailView.xaml
git commit -m "$(cat <<'EOF'
feat(silmarillion): ItemDetailView reading-order rework for sparse items (#234)

Replace the Grid+Height="*" slack-absorber pattern with a DockPanel
wrapping a StackPanel body; internal-name footer is DockPanel.Dock=Bottom.
Move cross-link sections (Sources / Produced by / Used in) from rows
19-21 to immediately after Description — sparse items no longer force the
eye past a dead zone of empty effects-region to reach the recipes that
produce/consume them.

Mirrors the structure already used by RecipeDetailView; the two detail
views are now structurally consistent.

Closes #234.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: `IReferenceKindTarget` interface

**Files:**
- Create: `src/Mithril.Shared/Reference/IReferenceKindTarget.cs`

Spec §7.

- [ ] **Step 1: Create the interface**

```csharp
namespace Mithril.Shared.Reference;

/// <summary>
/// A single entity-kind dispatch target registered with the Silmarillion
/// reference navigator. Each tab module registers one implementation; the
/// navigator and the host VM enumerate registered targets instead of
/// enumerating <c>switch (EntityKind)</c> cases (issue #239).
///
/// Replaces the hardcoded <c>V1TabbedKinds</c> HashSet and the two switches
/// in <c>SilmarillionViewModel</c>. As Bucket-B tabs ship (NPCs → Quests →
/// Areas+Landmarks → Lorebooks → Abilities → Effects → PlayerTitles →
/// StorageVaults) each adds one more target — no touches to the navigator
/// or the host VM.
/// </summary>
public interface IReferenceKindTarget
{
    /// <summary>The entity kind this target is responsible for. One target per kind.</summary>
    EntityKind Kind { get; }

    /// <summary>The TabControl index this target's UI lives at.</summary>
    int TabIndex { get; }

    /// <summary>
    /// Look the entity up by internal name and select it in the tab's
    /// master-detail. Returns false if the entity isn't in the reference data
    /// (e.g. a stale deep link).
    /// </summary>
    bool TrySelectByInternalName(string internalName);

    /// <summary>
    /// Open the current detail in a popup window. Returns false if the tab
    /// has no current detail (nothing selected).
    /// </summary>
    bool TryOpenInWindow();
}
```

Lives in `Mithril.Shared` (not `Mithril.Shared.Wpf`) — `EntityKind` is here, and the contract stays presentation-agnostic. The concrete implementations are presentation-coupled and live in `Silmarillion.Module`.

- [ ] **Step 2: Build**

Run: `dotnet build src/Mithril.Shared/Mithril.Shared.csproj`
Expected: build succeeds.

---

## Task 14: `ItemsKindTarget` + tests

**Files:**
- Create: `src/Silmarillion.Module/Navigation/ItemsKindTarget.cs`
- Create: `tests/Silmarillion.Tests/Navigation/ItemsKindTargetTests.cs`

Spec §8.

- [ ] **Step 1: Create the failing test**

```csharp
using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class ItemsKindTargetTests
{
    [Fact]
    public void Kind_IsItem()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Item);
    }

    [Fact]
    public void TabIndex_IsZero()
    {
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(0);
    }

    [Fact]
    public void TrySelectByInternalName_KnownItem_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, refData) = BuildTarget();
        var item = new Item { Id = 5010, InternalName = "Tomato", Name = "Tomato" };
        refData.AddItem(item);
        vm.RehydrateAllItems();

        var ok = target.TrySelectByInternalName("Tomato");

        ok.Should().BeTrue();
        vm.SelectedItem.Should().Be(item);
    }

    [Fact]
    public void TrySelectByInternalName_UnknownItem_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedItem.Should().BeNull();  // precondition

        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedItem.Should().BeNull();
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (ItemsKindTarget Target, ItemsTabViewModel Vm, FakeReferenceData RefData) BuildTarget()
    {
        var refData = new FakeReferenceData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new ItemsTabViewModel(refData, nav);
        var target = new ItemsKindTarget(vm, refData);
        return (target, vm, refData);
    }
}
```

This test depends on a `FakeReferenceData` helper. If `tests/TestSupport/FakeReferenceData.cs` doesn't already expose `AddItem` + a settable `ItemsByInternalName`, reuse what's there or extend it minimally. If extending breaks other consumers, copy the minimal surface into a private inner class:

```csharp
    private sealed class FakeReferenceData : Mithril.Shared.Reference.IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items = new();
        private readonly Dictionary<string, Item> _byName = new(StringComparer.Ordinal);
        public void AddItem(Item item) { _items[item.Id] = item; if (item.InternalName is not null) _byName[item.InternalName] = item; }
        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _byName;
        // ...all other members throw NotImplementedException — tests don't use them.
        // For brevity, executor should copy the minimal stub used by existing
        // Silmarillion test fixtures (see tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs).
    }
```

**Executor note:** Don't reinvent the wheel. Open [tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs](../../../tests/Silmarillion.Tests/ViewModels/ItemsTabViewModelTests.cs) first to see the fake-data shape that test already uses and copy that pattern.

- [ ] **Step 2: Verify the test fails (type doesn't exist)**

Run: `dotnet build tests/Silmarillion.Tests/Silmarillion.Tests.csproj`
Expected: `ItemsKindTarget` not found.

- [ ] **Step 3: Create the implementation**

```csharp
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary>
/// <see cref="IReferenceKindTarget"/> adapter for the Items tab. Lives next to
/// the navigator so the registry is owned by the same module that hosts the
/// tabs. Wires the navigator's "select this entity" call into the tab VM's
/// existing master-detail selection, and the "open in window" call into the
/// existing <see cref="ItemDetailWindow"/> popup.
/// </summary>
public sealed class ItemsKindTarget : IReferenceKindTarget
{
    private readonly ItemsTabViewModel _vm;
    private readonly IReferenceDataService _refData;

    public ItemsKindTarget(ItemsTabViewModel vm, IReferenceDataService refData)
    {
        _vm = vm;
        _refData = refData;
    }

    public EntityKind Kind => EntityKind.Item;

    public int TabIndex => 0;

    public bool TrySelectByInternalName(string internalName)
    {
        if (!_refData.ItemsByInternalName.TryGetValue(internalName, out var item))
            return false;
        _vm.SelectedItem = item;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new ItemDetailWindow(_vm.DetailViewModel).Show();
        return true;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj --filter "FullyQualifiedName~ItemsKindTargetTests" -v normal`
Expected: all four pass.

---

## Task 15: `RecipesKindTarget` + tests

**Files:**
- Create: `src/Silmarillion.Module/Navigation/RecipesKindTarget.cs`
- Create: `tests/Silmarillion.Tests/Navigation/RecipesKindTargetTests.cs`

Spec §8.

- [ ] **Step 1: Create the failing test**

```csharp
using FluentAssertions;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class RecipesKindTargetTests
{
    [Fact]
    public void Kind_IsRecipe()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Recipe);
    }

    [Fact]
    public void TabIndex_IsOne()
    {
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(1);
    }

    [Fact]
    public void TrySelectByInternalName_KnownRecipe_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, refData) = BuildTarget();
        var recipe = new Recipe { Key = "recipe_123", InternalName = "MakeSalsa", Name = "Make Salsa" };
        refData.AddRecipe(recipe);
        vm.RehydrateAllRecipes();

        var ok = target.TrySelectByInternalName("MakeSalsa");

        ok.Should().BeTrue();
        vm.SelectedRecipe.Should().Be(recipe);
    }

    [Fact]
    public void TrySelectByInternalName_UnknownRecipe_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedRecipe.Should().BeNull();
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (RecipesKindTarget Target, RecipesTabViewModel Vm, FakeReferenceData RefData) BuildTarget()
    {
        // Use the same FakeReferenceData pattern as ItemsKindTargetTests; if the
        // shared TestSupport version supports recipes, prefer that; otherwise use
        // a local inner-class fake matching the shape used by RecipesTabViewModelTests.
        var refData = new FakeReferenceData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new RecipesTabViewModel(refData, nav);
        var target = new RecipesKindTarget(vm, refData);
        return (target, vm, refData);
    }
}
```

**Executor note:** Same as T14 — read `tests/Silmarillion.Tests/ViewModels/RecipesTabViewModelTests.cs` first for the existing fake-data convention before writing the inner class.

- [ ] **Step 2: Verify failing**

Run: `dotnet build tests/Silmarillion.Tests/Silmarillion.Tests.csproj`
Expected: `RecipesKindTarget` not found.

- [ ] **Step 3: Create the implementation**

```csharp
using Mithril.Shared.Reference;
using Silmarillion.ViewModels;
using Silmarillion.Views;

namespace Silmarillion.Navigation;

/// <summary><see cref="IReferenceKindTarget"/> adapter for the Recipes tab.
/// See <see cref="ItemsKindTarget"/>.</summary>
public sealed class RecipesKindTarget : IReferenceKindTarget
{
    private readonly RecipesTabViewModel _vm;
    private readonly IReferenceDataService _refData;

    public RecipesKindTarget(RecipesTabViewModel vm, IReferenceDataService refData)
    {
        _vm = vm;
        _refData = refData;
    }

    public EntityKind Kind => EntityKind.Recipe;

    public int TabIndex => 1;

    public bool TrySelectByInternalName(string internalName)
    {
        if (!_refData.RecipesByInternalName.TryGetValue(internalName, out var recipe))
            return false;
        _vm.SelectedRecipe = recipe;
        return true;
    }

    public bool TryOpenInWindow()
    {
        if (_vm.DetailViewModel is null) return false;
        new RecipeDetailWindow { DataContext = _vm.DetailViewModel }.Show();
        return true;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj --filter "FullyQualifiedName~RecipesKindTargetTests" -v normal`
Expected: all five pass.

---

## Task 16: Navigator → registry + update existing tests

**Files:**
- Modify: `src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs`
- Modify: `src/Silmarillion.Module/SilmarillionModule.cs` (register kind targets)
- Modify: `tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs`

Spec §9.

- [ ] **Step 1: Rewrite the navigator**

Replace the entire body of `SilmarillionReferenceNavigator.cs` with:

```csharp
using Mithril.Shared.Reference;

namespace Silmarillion.Navigation;

/// <summary>
/// Live implementation of <see cref="IReferenceNavigator"/>. Maintains unbounded
/// back/forward stacks of <see cref="EntityRef"/>. Registered by
/// <see cref="SilmarillionModule"/> and overrides the shell's
/// <c>NoOpReferenceNavigator</c> via last-singleton-wins DI semantics.
///
/// <see cref="CanOpen"/> is registry-driven: a kind is navigable iff an
/// <see cref="IReferenceKindTarget"/> has been registered for it. As Bucket-B
/// tabs ship (NPCs → Quests → …), each one adds a target and chip
/// clickability for that kind flips on automatically.
/// </summary>
public sealed class SilmarillionReferenceNavigator : IReferenceNavigator
{
    private readonly IReadOnlyDictionary<EntityKind, IReferenceKindTarget> _targets;

    private readonly Stack<EntityRef> _back = new();
    private readonly Stack<EntityRef> _forward = new();

    public SilmarillionReferenceNavigator(IEnumerable<IReferenceKindTarget> targets)
    {
        // Fail-loud on duplicate Kind registrations — same shape as DeepLinkRouter.
        var byKind = new Dictionary<EntityKind, IReferenceKindTarget>();
        foreach (var t in targets)
        {
            if (byKind.ContainsKey(t.Kind))
                throw new InvalidOperationException(
                    $"Duplicate IReferenceKindTarget registration for kind '{t.Kind}': " +
                    $"{byKind[t.Kind].GetType().FullName} and {t.GetType().FullName}.");
            byKind[t.Kind] = t;
        }
        _targets = byKind;
    }

    public EntityRef? Current { get; private set; }
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public event EventHandler<NavigatedEventArgs>? Navigated;

    public bool CanOpen(EntityRef reference) => _targets.ContainsKey(reference.Kind);

    public void Open(EntityRef reference)
    {
        var previous = Current;
        if (previous is not null) _back.Push(previous);
        _forward.Clear();
        Current = reference;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Open));
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        var previous = Current;
        if (previous is not null) _forward.Push(previous);
        Current = _back.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Back));
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        var previous = Current;
        if (previous is not null) _back.Push(previous);
        Current = _forward.Pop();
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, Current, NavigationKind.Forward));
    }
}
```

- [ ] **Step 2: Update `SilmarillionReferenceNavigatorTests.cs`**

Every existing test that constructs `new SilmarillionReferenceNavigator()` needs an empty-or-stub `IEnumerable<IReferenceKindTarget>` parameter. Replace all `new SilmarillionReferenceNavigator()` calls in the test file with `new SilmarillionReferenceNavigator(NavTargets())` where:

```csharp
    private static IEnumerable<IReferenceKindTarget> NavTargets() => new IReferenceKindTarget[]
    {
        new StubTarget(EntityKind.Item),
        new StubTarget(EntityKind.Recipe),
    };

    private sealed class StubTarget : IReferenceKindTarget
    {
        public StubTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
    }
```

Add the helper + private class near the other private helpers at the bottom of `SilmarillionReferenceNavigatorTests`.

Replace the existing `CanOpen_TrueForV1TabbedKinds_FalseOtherwise` `[Theory]` (lines 138–154 of the existing file) with three new tests:

```csharp
    [Fact]
    public void CanOpen_RegisteredKind_ReturnsTrue()
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());

        nav.CanOpen(EntityRef.Item("anything")).Should().BeTrue();
        nav.CanOpen(EntityRef.Recipe("anything")).Should().BeTrue();
    }

    [Theory]
    [InlineData(EntityKind.Ability)]
    [InlineData(EntityKind.Npc)]
    [InlineData(EntityKind.Quest)]
    [InlineData(EntityKind.Lorebook)]
    [InlineData(EntityKind.Landmark)]
    [InlineData(EntityKind.Area)]
    [InlineData(EntityKind.PlayerTitle)]
    [InlineData(EntityKind.StorageVault)]
    [InlineData(EntityKind.Effect)]
    public void CanOpen_UnregisteredKind_ReturnsFalse(EntityKind kind)
    {
        var nav = new SilmarillionReferenceNavigator(NavTargets());

        nav.CanOpen(new EntityRef(kind, "anything")).Should().BeFalse();
    }

    [Fact]
    public void Constructor_DuplicateKind_Throws()
    {
        var targets = new IReferenceKindTarget[]
        {
            new StubTarget(EntityKind.Item),
            new StubTarget(EntityKind.Item),
        };
        var act = () => new SilmarillionReferenceNavigator(targets);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Duplicate IReferenceKindTarget*Item*");
    }
```

- [ ] **Step 3: Register kind targets in `SilmarillionModule.Register`**

Open `src/Silmarillion.Module/SilmarillionModule.cs`. Replace the body of `Register(IServiceCollection)` so the navigator + tab VMs + kind targets register in the right order. The whole method becomes:

```csharp
    public void Register(IServiceCollection services)
    {
        // Replace the shell-registered NoOpReferenceNavigator. Last AddSingleton<T> wins
        // for non-keyed singleton resolution, and module Register() runs after shell DI
        // setup. The navigator pulls all IReferenceKindTarget registrations via DI.
        services.AddSingleton<IReferenceNavigator>(sp => new SilmarillionReferenceNavigator(
            sp.GetServices<IReferenceKindTarget>()));

        services.AddSingleton<ItemsTabViewModel>();
        services.AddSingleton<RecipesTabViewModel>();
        services.AddSingleton<SilmarillionViewModel>();

        // Kind targets registered after the tab VMs so DI can resolve them.
        services.AddSingleton<IReferenceKindTarget>(sp => new ItemsKindTarget(
            sp.GetRequiredService<ItemsTabViewModel>(),
            sp.GetRequiredService<IReferenceDataService>()));
        services.AddSingleton<IReferenceKindTarget>(sp => new RecipesKindTarget(
            sp.GetRequiredService<RecipesTabViewModel>(),
            sp.GetRequiredService<IReferenceDataService>()));

        // Module-scoped mithril://silmarillion/<kind>/<name> route (issue #229).
        services.AddSingleton<IDeepLinkHandler>(sp =>
            new SilmarillionDeepLinkHandler(sp.GetRequiredService<IReferenceNavigator>()));

        services.AddSingleton<SilmarillionView>(sp => new SilmarillionView
        {
            DataContext = sp.GetRequiredService<SilmarillionViewModel>(),
        });
    }
```

Update the using imports at the top of the file to include `Silmarillion.Navigation;` if not already there.

- [ ] **Step 4: Run the navigator tests**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj --filter "FullyQualifiedName~SilmarillionReferenceNavigatorTests" -v normal`
Expected: all tests pass (existing back/forward tests untouched + new registry tests pass).

---

## Task 17: `SilmarillionViewModel` → registry + tests

**Files:**
- Modify: `src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs`
- Create: `tests/Silmarillion.Tests/ViewModels/SilmarillionViewModelTests.cs`

Spec §10.

- [ ] **Step 1: Replace `SilmarillionViewModel.cs` body**

Replace the entire file with:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;

namespace Silmarillion.ViewModels;

/// <summary>
/// Top-level view-model for the Silmarillion reference-data browser. Hosts the
/// per-tab view-models and the navigation commands surfaced in the header chrome
/// (Back / Forward / Open-in-window). Subscribes to <see cref="IReferenceNavigator.Navigated"/>
/// to drive automatic tab-switching when an entity of a different kind is opened
/// (e.g. clicking a Recipe cross-link from an Item detail).
///
/// OnNavigated and OpenInWindow dispatch via the <see cref="IReferenceKindTarget"/>
/// registry; per-kind switches were retired in #239.
/// </summary>
public sealed partial class SilmarillionViewModel : ObservableObject
{
    private readonly IReferenceNavigator _navigator;
    private readonly IReadOnlyDictionary<EntityKind, IReferenceKindTarget> _targets;

    public SilmarillionViewModel(
        ItemsTabViewModel items,
        RecipesTabViewModel recipes,
        IReferenceNavigator navigator,
        IEnumerable<IReferenceKindTarget> targets)
    {
        Items = items;
        Recipes = recipes;
        _navigator = navigator;

        var byKind = new Dictionary<EntityKind, IReferenceKindTarget>();
        foreach (var t in targets) byKind[t.Kind] = t;
        _targets = byKind;

        BackCommand = new RelayCommand(() => _navigator.Back(), () => _navigator.CanGoBack);
        ForwardCommand = new RelayCommand(() => _navigator.Forward(), () => _navigator.CanGoForward);

        _navigator.Navigated += OnNavigated;
    }

    public ItemsTabViewModel Items { get; }
    public RecipesTabViewModel Recipes { get; }

    /// <summary>0 = Items, 1 = Recipes. Two-way bound to the TabControl in the view.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand ForwardCommand { get; }

    [RelayCommand(CanExecute = nameof(CanOpenInWindow))]
    private void OpenInWindow()
    {
        if (_navigator.Current is { } current
            && _targets.TryGetValue(current.Kind, out var target))
        {
            target.TryOpenInWindow();
        }
    }

    private bool CanOpenInWindow() => _navigator.Current is not null;

    private void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        OpenInWindowCommand.NotifyCanExecuteChanged();

        if (e.Current is null) return;
        if (!_targets.TryGetValue(e.Current.Kind, out var target)) return;

        SelectedTabIndex = target.TabIndex;
        target.TrySelectByInternalName(e.Current.InternalName);
    }
}
```

The constructor's `IReferenceDataService` parameter is now gone — the targets carry the ref-data dependency themselves. Drop the corresponding `using Silmarillion.Views;` if it was only there for the popup `new …Window(...)` calls (now removed since `TryOpenInWindow` lives on the targets).

- [ ] **Step 2: Create the failing test**

```csharp
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class SilmarillionViewModelTests
{
    [Fact]
    public void OnNavigated_ItemKind_SwitchesTabAndCallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var recipesTarget = new RecordingTarget(EntityKind.Recipe, tabIndex: 1);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget, recipesTarget });

        nav.Open(EntityRef.Item("Tomato"));

        vm.SelectedTabIndex.Should().Be(0);
        itemsTarget.LastSelectedInternalName.Should().Be("Tomato");
        recipesTarget.LastSelectedInternalName.Should().BeNull();
    }

    [Fact]
    public void OnNavigated_RecipeKind_SwitchesTabAndCallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var recipesTarget = new RecordingTarget(EntityKind.Recipe, tabIndex: 1);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget, recipesTarget });

        nav.Open(EntityRef.Recipe("MakeSalsa"));

        vm.SelectedTabIndex.Should().Be(1);
        recipesTarget.LastSelectedInternalName.Should().Be("MakeSalsa");
    }

    [Fact]
    public void OnNavigated_UnregisteredKind_DoesNotChangeTab()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        vm.SelectedTabIndex = 0;
        nav.Open(new EntityRef(EntityKind.Npc, "NPC_Marna"));

        vm.SelectedTabIndex.Should().Be(0);  // unchanged
        itemsTarget.LastSelectedInternalName.Should().BeNull();
    }

    [Fact]
    public void OpenInWindow_NoCurrent_NoOp()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, _) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        // CanExecute should be false; explicit call is a no-op (TryOpenInWindow not invoked).
        vm.OpenInWindowCommand.CanExecute(null).Should().BeFalse();
        itemsTarget.OpenInWindowCallCount.Should().Be(0);
    }

    [Fact]
    public void OpenInWindow_CurrentItem_CallsTarget()
    {
        var itemsTarget = new RecordingTarget(EntityKind.Item, tabIndex: 0);
        var (vm, nav) = BuildVm(new IReferenceKindTarget[] { itemsTarget });

        nav.Open(EntityRef.Item("Tomato"));
        vm.OpenInWindowCommand.Execute(null);

        itemsTarget.OpenInWindowCallCount.Should().Be(1);
    }

    private static (SilmarillionViewModel Vm, SilmarillionReferenceNavigator Nav) BuildVm(
        IReferenceKindTarget[] targets)
    {
        var nav = new SilmarillionReferenceNavigator(targets);
        // Empty/null tab VMs are fine — the tests only exercise the dispatch path,
        // not the tab VMs themselves (those are tested separately).
        // Pass null-forgiving stubs; SilmarillionViewModel never reads .Items / .Recipes here.
        var vm = new SilmarillionViewModel(items: null!, recipes: null!, nav, targets);
        return (vm, nav);
    }

    private sealed class RecordingTarget : IReferenceKindTarget
    {
        public RecordingTarget(EntityKind kind, int tabIndex) { Kind = kind; TabIndex = tabIndex; }
        public EntityKind Kind { get; }
        public int TabIndex { get; }
        public string? LastSelectedInternalName { get; private set; }
        public int OpenInWindowCallCount { get; private set; }
        public bool TrySelectByInternalName(string internalName)
        {
            LastSelectedInternalName = internalName;
            return true;
        }
        public bool TryOpenInWindow()
        {
            OpenInWindowCallCount++;
            return true;
        }
    }
}
```

**Executor caveat:** the `items: null!, recipes: null!` shape works ONLY because the existing `SilmarillionViewModel` only assigns these to `Items` / `Recipes` properties without dereferencing them in the navigation path. If a constructor null-check is added later, the test will need a real or stubbed `ItemsTabViewModel` / `RecipesTabViewModel`. The current constructor (after Task 17 step 1) doesn't null-check.

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj --filter "FullyQualifiedName~SilmarillionViewModelTests" -v normal`
Expected: all five pass.

- [ ] **Step 4: Run the full Silmarillion test suite to catch regressions**

Run: `dotnet test tests/Silmarillion.Tests/Silmarillion.Tests.csproj`
Expected: every test passes.

- [ ] **Step 5: Commit (commit 5)**

```
git add src/Mithril.Shared/Reference/IReferenceKindTarget.cs \
        src/Silmarillion.Module/Navigation/ItemsKindTarget.cs \
        src/Silmarillion.Module/Navigation/RecipesKindTarget.cs \
        src/Silmarillion.Module/Navigation/SilmarillionReferenceNavigator.cs \
        src/Silmarillion.Module/ViewModels/SilmarillionViewModel.cs \
        src/Silmarillion.Module/SilmarillionModule.cs \
        tests/Silmarillion.Tests/Navigation/SilmarillionReferenceNavigatorTests.cs \
        tests/Silmarillion.Tests/Navigation/ItemsKindTargetTests.cs \
        tests/Silmarillion.Tests/Navigation/RecipesKindTargetTests.cs \
        tests/Silmarillion.Tests/ViewModels/SilmarillionViewModelTests.cs
```

Commit message:
```
refactor(silmarillion): IReferenceNavigator → IReferenceKindTarget registry (#239)

Parallel pattern to commit 1's DeepLinkRouter refactor. The
V1TabbedKinds HashSet and the two switches in SilmarillionViewModel
(OnNavigated, OpenInWindow) all enumerated the same two-kind list and
all needed to grow as Bucket-B tabs ship.

Each tab module now registers an IReferenceKindTarget; the navigator
exposes CanOpen as a registry lookup; the top-level VM dispatches via
the same registry. As NPCs / Quests / Areas / etc. ship, each adds one
more target — zero touches to the navigator or top-level VM.

No user-visible behavior change. CanOpen still returns true for Item +
Recipe and false for everything else. Tab-switching on cross-link
navigation still works.

Closes #239.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

---

## Task 18: Wiki + about-settings example + commit 6

**Files:**
- Modify: `src/Mithril.Shell/Views/AboutSettingsView.xaml`
- Modify: project-gorgon.wiki Deep-Linking page (separate checkout at `i:\src\project-gorgon.wiki`)

Spec §11.

- [ ] **Step 1: Update the example URI in AboutSettingsView**

Open `src/Mithril.Shell/Views/AboutSettingsView.xaml`. Find line 146 (the existing `Text="Register a mithril:// URL handler so other apps can open Mithril directly at a specific item. Example: mithril://item/CraftedLeatherBoots5."`). Replace the example URI in that string with the module-scoped form:

```xml
                       Text="Register a mithril:// URL handler so other apps can open Mithril directly at a specific item. Example: mithril://silmarillion/item/CraftedLeatherBoots5. Stored in your user registry (HKCU); no admin rights required."/>
```

The legacy form keeps working but the surfaced example points at the preferred form.

- [ ] **Step 2: Build the shell**

Run: `dotnet build src/Mithril.Shell/Mithril.Shell.csproj`
Expected: build succeeds.

- [ ] **Step 3: Update the wiki Deep-Linking page**

Switch into the wiki checkout:
```bash
cd ../project-gorgon.wiki
```

Find the deep-linking page. Common candidate file names: `Deep-Linking.md`, `Deep‐Linking.md` (en-dash), `URI-Scheme.md`. If none exists, search for `mithril://item` in the wiki and edit whichever page mentions it:

```bash
git grep "mithril://item"
```

Update that page to:

1. Show `mithril://silmarillion/item/<internalName>` and `mithril://silmarillion/recipe/<internalName>` as the **preferred** forms (with one or two concrete examples).
2. Note `mithril://item/<internalName>` / `mithril://recipe/<internalName>` as **legacy but still supported** for backwards compat with existing links.
3. Add `mithril://silmarillion/...` to whatever table or list enumerates the supported schemes.

Commit on the wiki repo:
```bash
git add <the-edited-page>.md
git commit -m "$(cat <<'EOF'
Deep-Linking: document mithril://silmarillion/<kind>/<name> as preferred

The legacy mithril://item/ and mithril://recipe/ forms continue to work,
but the module-scoped form is now preferred for parity with the other
modules' deep-link schemes (pippin, legolas, elrond).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push origin master
```

(The wiki repo's default branch is `master`, not `main` — distinct from this repo.)

- [ ] **Step 4: Commit AboutSettingsView change (commit 6 on this repo)**

Back in the main repo (`cd ../project gorgon`):

```
git add src/Mithril.Shell/Views/AboutSettingsView.xaml
git commit -m "$(cat <<'EOF'
docs: point about-settings deep-link example at module-scoped form

Surface the preferred mithril://silmarillion/item/... form in the
example string. Legacy forms keep working.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 19: End-to-end manual verification + open PR

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test Mithril.slnx`
Expected: every test passes.

- [ ] **Step 2: Launch the app and verify the polish trio + URI dispatch**

Run: `dotnet run --project src/Mithril.Shell`

Verify in-app:
1. **Silmarillion → Items**: compact rows render, two-line layout, ~40+ rows visible.
2. **Silmarillion → Recipes**: same density, skill+level on line 2.
3. **Click an item with no effects** (e.g. a Lorebook, a candle wick): the right pane shows Sources / Produced by / Used in immediately after Description, internal-name footer pinned to bottom.
4. **Click an effect-heavy item** (e.g. any rare crafted weapon): all sections render in the new order with no regressions.
5. **Click a cross-link chip** (e.g. a recipe chip on an item detail): the Recipes tab opens and selects the right recipe. Back-button (mouse XButton1) navigates back to the item.
6. **OpenInWindow** (Ctrl+O or the toolbar button if present): popup window shows the current detail.

Then test URI dispatch via OS-level activation:
```bash
# Legacy form:
Start-Process "mithril://item/CraftedLeatherBoots5"
# Should open Silmarillion → Items → CraftedLeatherBoots5.

# Module-scoped form:
Start-Process "mithril://silmarillion/item/CraftedLeatherBoots5"
# Should open the same item.

# Recipe form:
Start-Process "mithril://silmarillion/recipe/MakeTomatoSauce"
# Should open Silmarillion → Recipes → MakeTomatoSauce.
```

Close the app.

- [ ] **Step 3: Push and open PR**

```
git push -u origin feat/silmarillion-polish-v1
gh pr create --title "Silmarillion polish v1 (#229/#231/#234) + DeepLink + Navigator registries (#239)" --body "$(cat <<'EOF'
## Summary

Bundles three #207-followup polish issues with two parallel "switch-as-registry" refactors. Spec: [docs/planning/silmarillion-polish-v1/spec.md](docs/planning/silmarillion-polish-v1/spec.md). Plan: [docs/planning/silmarillion-polish-v1/plan.md](docs/planning/silmarillion-polish-v1/plan.md).

- **#229** — `mithril://silmarillion/<kind>/<name>` module-scoped routes alongside legacy `mithril://item/...` / `mithril://recipe/...`.
- **#231** — Compact two-line row layout in the master-detail left pane (~36px rows vs ~80px cards).
- **#234** — Cross-link sections (Sources / Produced by / Used in) move to immediately after Description in `ItemDetailView`.
- **DeepLink registry refactor** — Precursor for #229. Six structurally-identical `switch (action)` cases become `IDeepLinkHandler` implementations registered per module.
- **#239** — Same pattern for `IReferenceNavigator`: the `V1TabbedKinds` HashSet and the two switches in `SilmarillionViewModel` become an `IReferenceKindTarget` registry. The next PR (NPCs tab) is now purely additive.

#235 (recipe-sources section) is intentionally out of scope — needs new reference-data plumbing and will land as a separate PR.

## Test plan

- [x] `dotnet build Mithril.slnx` — succeeds
- [x] `dotnet test Mithril.slnx` — all pass
- [x] DeepLinkRouter tests cover legacy + module-scoped routes
- [x] SilmarillionDeepLinkHandlerTests cover the new grammar
- [x] SilmarillionReferenceNavigatorTests cover registry-driven CanOpen + duplicate-kind throw
- [x] ItemsKindTargetTests + RecipesKindTargetTests cover the adapter shape
- [x] SilmarillionViewModelTests cover OnNavigated + OpenInWindow dispatch
- [x] Visual: compact rows render at expected density on both tabs
- [x] Visual: sparse items show cross-links above the fold
- [x] Visual: effect-heavy items render with no regressions
- [x] URI: `Start-Process mithril://silmarillion/item/<name>` opens the item
- [x] URI: legacy `Start-Process mithril://item/<name>` still works

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Return the PR URL.

---

## Self-Review Notes

**Spec coverage:** Every spec section maps to a task — §1 (T1), §2 (T2, T5–T8), §3 (T3), §4 (T9, T10), §5 (T11), §6 (T12), §7 (T13), §8 (T14, T15), §9 (T16), §10 (T17), §11 (T18).

**Type consistency:** `IDeepLinkHandler.TryHandle(string subPath, IDiagnosticsSink? diag)` signature is used identically across T1, T2, T5–T8, T10. `IReferenceKindTarget` has the same `Kind` / `TabIndex` / `TrySelectByInternalName` / `TryOpenInWindow` shape across T13, T14, T15, T16, T17. `SilmarillionReferenceNavigator(IEnumerable<IReferenceKindTarget>)` constructor signature is consistent between T16's implementation and T17's tests.

**Placeholders:** None.

**Out-of-band note for executor:** When a step says "search for X" or "find the existing block around line N," line numbers may have drifted by the time the executor reads the file. Use the surrounding code context shown in the task as the actual locator, not the line number.
