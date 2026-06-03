# `MapSceneRef` Standardization + Consumer Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote `MapSceneRef` from "projection-time NPC-scope identifier" to the universal calibration identity south of `IMapState`. Retype every consumer of `IMapCalibrationService` to take a typed `MapSceneRef`. Introduce a persisted `SceneAssetCache` for cold-start resolution. Retire the #836 `LegolasSettings.AreaCalibrations` legacy parity loop.

**Architecture:** `MapSceneRef` gains a third `MapAssetKey` field. `IMapState` collapses three loose strings into one `CurrentMapScene : MapSceneRef?` property. `MapAssetChanged` event payload retypes to the composite. Every `IMapCalibrationService` method's `string areaKey` parameter retypes to `MapSceneRef scene`. A new `SceneAssetCache` (persisted at `%LocalAppData%/Mithril/MapCalibration/scene-asset-cache.json`, seeded at startup from the `baseline.json ∩ areas.json` intersection) covers the cold-start cell where `IMapState.CurrentMapScene` is null but `CurrentArea` is known. `LegolasAreaCalibrationMigration`, `IMapCalibrationService.ImportUserRefinements`, `UserRefinementStore.ImportFromLegacy`, and the `LegolasSettings.AreaCalibrations` dual-write/clear are all deleted.

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 13, MSBuild `Mithril.slnx`, xUnit + FluentAssertions, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, `System.Text.Json` (source-generated contexts).

**Spec:** [`docs/planning/map-calibration-1041-mapsceneref-standardization/spec.md`](spec.md). Decisions D1-D9 are ratified there; this plan does not re-litigate them.

**Issue:** [mithril#1041](https://github.com/moumantai-gg/mithril/issues/1041). Lands as a single squash-merged PR against `main`.

---

## Build / test cheat sheet

```bash
# Build everything (warnings as errors enforced; CleanBinObj clears stale obj/ first)
dotnet build Mithril.slnx

# Run all tests
dotnet test Mithril.slnx

# Run one test project
dotnet test tests/Mithril.MapCalibration.Tests

# Run one test by FQN substring
dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheTests.Record_Then_Resolve"
```

> **Important — close Mithril.exe before building.** The repo's `check-mithril-running.ps1` PreToolUse hook blocks `dotnet build/test` while the shell is running (memory `mithril_build_file_lock_silent`). If a build mysteriously fails with `MSB3026` / `MSB3027`, close Mithril first.

> **Build-state caveat (D9).** Phases 1-4 land as one atomic PR. Intermediate states between tasks may not build cleanly because the interface surface change is cross-cutting. The plan's commits per task may not produce buildable intermediate trees — they're logically-ordered TDD-style commits that the final-state suite gates. If a task's "verify build" step fails mid-plan, that's expected unless flagged otherwise.

---

## Codebase cohesion corrections (READ BEFORE TASK 1)

Surfaced during plan-time cohesion review. The plan body below was drafted from spec intent; these are the verified actual-state corrections to apply as you go:

### Test project naming + folder layout

- **Test project is `Legolas.Tests`, NOT `Legolas.Module.Tests`.** Every reference in the plan body to `tests/Legolas.Module.Tests/` should read `tests/Legolas.Tests/`. The existing `AreaCalibrationServiceTests.cs` lives at `tests/Legolas.Tests/Services/AreaCalibrationServiceTests.cs`.
- **`tests/Mithril.MapCalibration.Tests/` has minimal subfoldering.** Existing tests like `MapCalibrationServiceTests.cs` and `BundledBaselineLoaderTests.cs` live **at the root**, not under `Internal/`. Only `UserRefinementStoreMigrationTests.cs` is under `Internal/`. For new test files in this plan: keep `SceneAssetCacheStoreTests.cs`, `SceneAssetCacheSeederTests.cs`, `SceneAssetCacheRecorderTests.cs` under `Internal/` (mirrors `UserRefinementStoreMigrationTests`); keep `SceneAssetCacheTests.cs`, `MapSceneRefTests.cs`, `SceneResolutionTests.cs` at the root (mirrors `MapCalibrationServiceTests`).
- **`tests/Arda.World.Player.Tests/` has NO `Internal/` subfolder.** Existing `MapAssetLoaderTests.cs` is at the root. Task 4's path of `tests/Arda.World.Player.Tests/Internal/MapAssetLoaderTests.cs` should read `tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs`. Same for the `MapScopeTests.cs` reference in Task 5.
- **`MapScopeTests.cs` does not exist today.** Task 5's "Update tests to expect composite delegation" creates this file fresh. The existing `tests/Arda.World.Player.Tests/MapTests.cs` covers area+previous-area delegation on `IMapState`; consider extending it instead of adding `MapScopeTests.cs` (read `MapTests.cs` first to decide).
- **`tests/Mithril.Overlay.Tests/OverlayWindowServiceTests.cs` does not exist today.** Task 17's "Update tests" should target the existing files: `OverlayProjectionTests.cs` (covers `ProjectMarkers` static), `OverlayWindowBindingTests.cs` (covers DI binding), and `MarkerSceneRendererTests.cs`. Add the cache-fallback assertion to `OverlayProjectionTests.cs`; add the `MapAssetChanged` subscription assertion to a new test class if no existing one fits. Pre-existing fakes are in `tests/Mithril.Overlay.Tests/Fakes/` — `FakeMapCalibrationService.cs`, `StubAreaState.cs`, `CapturingLoggerFactory.cs` are reusable.

### File paths in `src/`

- **`LegolasSettings.cs` is at `src/Legolas.Module/Domain/LegolasSettings.cs`** (not `src/Legolas.Module/LegolasSettings.cs`). Task 21 path correction.
- **`BundledBaselineLoader` is `internal static`** in `Mithril.MapCalibration.Internal`. Its public API is `Load(ILogger?)`, NOT `LoadFromBundled()`. Task 23's integration test must use `BundledBaselineLoader.Load(NullLogger.Instance)` (and `tests/Mithril.MapCalibration.Tests` already has `InternalsVisibleTo` configured to access internals).

### Type signature corrections

- **`AreaEntry` constructor takes 3 args, not 2.** Actual shape: `public sealed record AreaEntry(string Key, string FriendlyName, string ShortFriendlyName);` in namespace `Mithril.Shared.Reference` (NOT `Mithril.Shared.Reference.Models.Areas`). Every test fixture in Tasks 11, 13, 23 that constructs `new AreaEntry(...)` needs a third arg (use `ShortFriendlyName: <FriendlyName>` for simplicity if not under test).
- **`MapCalibrationService` constructor signature today** is `(IReadOnlyDictionary<string, AreaCalibration> baseline, UserRefinementStore userStore, double goodResidualThresholdPx, ILogger? logger)` — matches the plan's assumption. ✓
- **`UserRefinementStore` constructor** is `(string directory, ILogger? logger = null)` — matches. ✓
- **`OverlayWindowService` is already `IHostedService`** (line 55), so the existing `StartAsync` is the subscription site for `MapAssetChanged`, not a new `Initialize` method. Task 17 Step 3 should hook the subscription into the existing `StartAsync` body.

### DI wiring (Task 14)

- **`IUserDataPaths` does NOT exist** anywhere in the codebase. The plan's reference is invented. The actual DI extension `AddMithrilMapCalibration(this IServiceCollection services, string storageDirectory, double goodResidualThresholdPx)` already takes a `string storageDirectory` parameter — the caller resolves the `%LocalAppData%/Mithril/MapCalibration` path itself. The new cache services should follow the same pattern: register them with the SAME `storageDirectory` (parent dir contains both `refinements.json` and `scene-asset-cache.json`).
- **Concrete Task 14 rewrite:** in the existing `AddMithrilMapCalibration` extension (around `MapCalibrationServiceCollectionExtensions.cs:34-37`), the singleton factory captures `storageDirectory`. Add cache registrations using the same captured value:

  ```csharp
  services.AddSingleton(sp => new SceneAssetCacheStore(
      directory: storageDirectory,
      logger: sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.SceneAssetCacheStore")));
  services.AddSingleton<ISceneAssetCache>(sp =>
      new SceneAssetCache(
          sp.GetRequiredService<SceneAssetCacheStore>(),
          sp.GetService<ILoggerFactory>()?.CreateLogger("Mithril.MapCalibration.SceneAssetCache")));
  services.AddHostedService<SceneAssetCacheRecorder>();
  ```

  For the seeder (which needs `IReferenceDataService.Areas`), add a small `IHostedService` that runs after `IReferenceDataService` is ready, OR fold the seed call into the `MapCalibrationService` factory if `IReferenceDataService` can be resolved there (it's registered at app composition root, so resolution should work).
- **`IReferenceDataService.Areas`** at `src/Mithril.Shared/Reference/IReferenceDataService.cs:211` is `IReadOnlyDictionary<string, AreaEntry>`. ✓ matches plan.

### Namespace corrections

- `MapSceneRef`, `IMapCalibrationService`, `MapCalibrationService`, `SceneAssetCache`, etc. all live in `Mithril.MapCalibration` (NOT `Mithril.Shared.MapCalibration` — that namespace exists but holds only `IClassDataTpkProvisioner` for the sidecar concern).
- `AreaEntry` is in `Mithril.Shared.Reference` (single-segment namespace, not nested under `Models.Areas`).

### Verification owed (unresolved at plan-time)

- The exact `IDomainEventSubscriber.Subscribe<T>` signature — confirm against the live Arda contract before Task 12 (`SceneAssetCacheRecorder`) is committed. The test fixture in Task 12 mocks it; verify the interface accepts `Action<T> handler` not `Action<T> handler, CancellationToken token` or similar.
- The exact `OverlayWindowService.StartAsync` body — Task 17 instructs adding a `MapAssetChanged` subscription there; check that no existing subscription in `StartAsync` would conflict (e.g., a per-render subscription on `AreaChanged` that should be removed at the same time).
- `LegolasSettings.AreaCalibrations`'s JSON source-generation. If `LegolasSettings` uses a source-generated `JsonSerializerContext` (per CLAUDE.md "Settings classes implement INotifyPropertyChanged with source-generated JSON serialization contexts"), the `[Obsolete]` annotation on the field must not exclude it from serialization — verify the field still round-trips after Task 21.

---

## Implementation order

Tasks land in dependency order so reviewers can trace the diff layer-by-layer:

1. **Phase 1 — Arda foundation** (Tasks 1-5) — `MapSceneRef.MapAssetKey`, `IMapState` reshape, `MapAssetChanged` reshape, `MapAssetLoader` rebuild, `MapScope` delegation.
2. **Phase 2 — Calibration core API reshape** (Tasks 6-8) — `IMapCalibrationService` retyping, `MapCalibrationService` impl, delete `ImportUserRefinements` + `ImportFromLegacy`.
3. **Phase 3 — `SceneAssetCache` mechanism** (Tasks 9-14) — new persistence + service + seeder + recorder + resolution helper + DI wiring.
4. **Phase 4a — Capture consumers** (Tasks 15-16) — `AutoCalibrationEngine`, `AutoCalibrationTrigger`.
5. **Phase 4b — Renderer** (Task 17) — `OverlayWindowService` + interface renames.
6. **Phase 4c — Legolas** (Tasks 18-22) — `AreaCalibrationService`, `PlayerLogIngestionService`, delete `LegolasAreaCalibrationMigration`, ViewModels, `[Obsolete]` annotation.
7. **Phase 5 — Integration + verification** (Tasks 23-25) — headline integration test, full-solution test, INDEX update.

---

## Phase 1 — Arda foundation

### Task 1: Extend `MapSceneRef` with `MapAssetKey` field

**Files:**
- Modify: `src/Mithril.MapCalibration/MapSceneRef.cs`
- Test: `tests/Mithril.MapCalibration.Tests/MapSceneRefTests.cs` (NEW)

- [ ] **Step 1: Write the failing test**

Create `tests/Mithril.MapCalibration.Tests/MapSceneRefTests.cs`:

```csharp
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class MapSceneRefTests
{
    [Fact]
    public void Construction_RequiresAllThreeFields()
    {
        var scene = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        scene.ParentAreaKey.Should().Be("AreaCave1");
        scene.SceneFriendlyName.Should().Be("Hogan's Basement");
        scene.MapAssetKey.Should().Be("Map_HogansKeepBasement");
    }

    [Fact]
    public void DirectlyRegisteredArea_HasNullSceneFriendlyName()
    {
        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        scene.SceneFriendlyName.Should().BeNull();
        scene.MapAssetKey.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void WithExpression_AllowsPartialMutation()
    {
        var original = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        var next = original with { SceneFriendlyName = "Goblin Dungeon", MapAssetKey = "Map_GoblinDungeon" };
        next.ParentAreaKey.Should().Be("AreaCave1");
        next.SceneFriendlyName.Should().Be("Goblin Dungeon");
        next.MapAssetKey.Should().Be("Map_GoblinDungeon");
    }
}
```

- [ ] **Step 2: Run test to verify it fails to compile**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapSceneRefTests"`
Expected: COMPILE FAILURE — `MapSceneRef` constructor takes 2 arguments, not 3.

- [ ] **Step 3: Extend `MapSceneRef`**

Replace the entire body of `src/Mithril.MapCalibration/MapSceneRef.cs`:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Composite identifier for a single Unity scene's calibration scope — the universal
/// calibration identity south of <see cref="Arda.World.Player.IMapState"/>.
///
/// <para><see cref="ParentAreaKey"/> is the areas.json key (always non-null in
/// practice — Arda surfaces it from <c>!!! Initializing area! </c>).
/// <see cref="SceneFriendlyName"/> is the sub-zone-level npcs.json
/// <c>AreaFriendlyName</c>; <c>null</c> for directly-registered areas, set for
/// aggregator-area sub-zones (e.g. for the Hogan's Keep basement scene under
/// <c>AreaCave1</c>, <c>SceneFriendlyName</c> is <c>"Hogan's Basement"</c>).
/// <see cref="MapAssetKey"/> is the literal Unity Texture2D name (e.g.
/// <c>"Map_HogansKeepBasement"</c>) — verbatim from the runtime-key bracket in
/// the Player.log <c>Downloading Map</c> line. This is the calibration store
/// key everywhere south of <see cref="Arda.World.Player.IMapState"/>:
/// <see cref="IMapCalibrationService"/>'s persistence is keyed on it.</para>
/// </summary>
/// <remarks>
/// Used by <c>Mithril.MapCalibration.Capture.IAreaReferenceProvider.ForArea</c>
/// to scope NPC lookups to the right sub-zone (consumer uses
/// <see cref="ParentAreaKey"/> + <see cref="SceneFriendlyName"/>; ignores
/// <see cref="MapAssetKey"/>). And by <see cref="IMapCalibrationService"/>'s
/// every public method as the typed lookup parameter
/// (mithril#1041 — promotes the type from projection identifier to universal
/// calibration identity).
/// </remarks>
public readonly record struct MapSceneRef(
    string ParentAreaKey,
    string? SceneFriendlyName,
    string MapAssetKey);
```

- [ ] **Step 4: Run test to verify it passes (and existing callers compile)**

Run: `dotnet build src/Mithril.MapCalibration`
Expected: build SUCCEEDS in the `Mithril.MapCalibration` project (but the broader solution may not build — existing call sites in `AutoCalibrationEngine` etc. construct `MapSceneRef` with 2 args and will break in Phase 4).

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~MapSceneRefTests"`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/MapSceneRef.cs tests/Mithril.MapCalibration.Tests/MapSceneRefTests.cs
git commit -m "feat(map-calibration): MapSceneRef gains MapAssetKey field (mithril#1041)"
```

---

### Task 2: Reshape `IMapState` (composite `CurrentMapScene`)

**Files:**
- Modify: `src/Arda/Arda.Contracts/State/Player/IMapState.cs`

- [ ] **Step 1: Replace the three string properties with the composite**

Open `src/Arda/Arda.Contracts/State/Player/IMapState.cs`. Replace lines 26-39 (the existing `// --- Map asset` block with `CurrentMapAsset`, `CurrentSceneFriendlyName`, `MapAssetMeasuredAt`) with:

```csharp
    // --- Map asset (per-Unity-scene texture identity) ---

    /// <summary>Composite map-scene identity (parent area + sub-zone friendly name + Unity asset key),
    /// or <c>null</c> until the first <c>Downloading Map</c> line is observed this session.
    /// <para>Live truth — preferred over <c>Mithril.MapCalibration.ISceneAssetCache</c>
    /// resolution. Source: Player.log's <c>Downloading Map ... runtime key ...[Map_&lt;X&gt;]</c>
    /// line, parsed by <c>Arda.World.Player.Internal.MapAssetLoader</c>.</para></summary>
    Mithril.MapCalibration.MapSceneRef? CurrentMapScene { get; }

    /// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
    DateTimeOffset? MapSceneMeasuredAt { get; }
```

- [ ] **Step 2: Verify the interface compiles standalone**

Run: `dotnet build src/Arda/Arda.Contracts`
Expected: build SUCCEEDS (interface compiles; consumers in `Arda.World.Player`, `Mithril.MapCalibration.Capture`, etc. will fail to build until later tasks).

- [ ] **Step 3: Commit**

```bash
git add src/Arda/Arda.Contracts/State/Player/IMapState.cs
git commit -m "refactor(arda): IMapState collapses 3 strings into CurrentMapScene composite (mithril#1041)"
```

---

### Task 3: Reshape `MapAssetChanged` payload

**Files:**
- Modify: `src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs`

- [ ] **Step 1: Replace the payload shape**

Replace the entire body of `src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs`:

```csharp
using Arda.Abstractions.Logs;
using Mithril.MapCalibration;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when PG's asset loader fetches a per-scene map texture
/// (the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_&lt;X&gt;]"
/// Player.log line). Carries the previous + current composite scene identity
/// (<see cref="MapSceneRef"/>) — subscribers can diff fields directly via record
/// equality. For aggregator <c>AreaX</c> entries (e.g. <c>AreaCave1</c>),
/// <see cref="MapSceneRef.SceneFriendlyName"/> identifies the specific sub-scene
/// where the parent area's <c>FriendlyName</c> would not.
/// </summary>
public readonly record struct MapAssetChanged(
    MapSceneRef? PreviousScene,
    MapSceneRef? CurrentScene,
    LogLineMetadata Metadata);
```

- [ ] **Step 2: Verify the event compiles standalone**

Run: `dotnet build src/Arda/Arda.Contracts`
Expected: build SUCCEEDS.

- [ ] **Step 3: Commit**

```bash
git add src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs
git commit -m "refactor(arda): MapAssetChanged payload retypes to (MapSceneRef?, MapSceneRef?) (mithril#1041)"
```

---

### Task 4: Rebuild `MapAssetLoader` to produce composites

**Files:**
- Modify: `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`
- Test: `tests/Arda.World.Player.Tests/Internal/MapAssetLoaderTests.cs`

- [ ] **Step 1: Update existing tests to expect the composite shape**

Open `tests/Arda.World.Player.Tests/Internal/MapAssetLoaderTests.cs`. Find every assertion against `state.CurrentMapAsset` / `state.CurrentSceneFriendlyName` / event payload's `CurrentMapAsset` / `CurrentSceneFriendlyName` and switch to the composite. Example translation:

```csharp
// BEFORE
loader.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
loader.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");

// AFTER
loader.CurrentMapScene.Should().NotBeNull();
loader.CurrentMapScene!.Value.MapAssetKey.Should().Be("Map_HogansKeepBasement");
loader.CurrentMapScene!.Value.SceneFriendlyName.Should().Be("Hogan's Basement");
loader.CurrentMapScene!.Value.ParentAreaKey.Should().Be("AreaCave1"); // NEW expectation
```

Apply the same translation to event-payload assertions: `evt.CurrentMapAsset` → `evt.CurrentScene?.MapAssetKey` etc.

- [ ] **Step 2: Add the `with`-expression sub-zone transition test**

Append to `MapAssetLoaderTests.cs`:

```csharp
[Fact]
public void SubZoneTransition_WithinSameParentArea_PreservesParentAreaKey()
{
    // Hogan's Basement first, then Goblin Dungeon — both under AreaCave1.
    // Initial: ParentArea unknown until we wire an upstream area-state into the
    // test; here we simulate by parsing two lines in sequence and asserting the
    // composite update path uses `with` semantics.
    var loader = new MapAssetLoader();
    var meta1 = new LogLineMetadata(DateTimeOffset.UtcNow, isReplay: false);
    var meta2 = meta1 with { Timestamp = meta1.Timestamp + TimeSpan.FromSeconds(5) };

    var line1 = "[44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 for area Hogan's Basement runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[Map_HogansKeepBasement]";
    var line2 = "[deadbeefdeadbeefdeadbeefdeadbeef] GUID deadbeefdeadbeefdeadbeefdeadbeef for area Goblin Dungeon runtime key deadbeefdeadbeefdeadbeefdeadbeef[Map_GoblinDungeon]";

    loader.Handle(line1.AsSpan(), sourceLog: "Player.log", meta1);
    var afterFirst = loader.CurrentMapScene!.Value;

    loader.Handle(line2.AsSpan(), sourceLog: "Player.log", meta2);
    var afterSecond = loader.CurrentMapScene!.Value;

    // ParentAreaKey is provided by upstream IAreaState; if the loader doesn't
    // carry it, the field stays string.Empty. The transition test asserts the
    // MapAssetKey and SceneFriendlyName transitioned.
    afterSecond.MapAssetKey.Should().Be("Map_GoblinDungeon");
    afterSecond.SceneFriendlyName.Should().Be("Goblin Dungeon");
    afterSecond.MapAssetKey.Should().NotBe(afterFirst.MapAssetKey);
}
```

> **Note on `ParentAreaKey` source.** The parser-only `MapAssetLoader` doesn't have direct access to `IAreaState.CurrentArea` — the `for area <X>` token in the log line is the **friendly name**, not the areas.json key. The composite is built using the parent area key from the most-recently-observed `Initializing area!` event (wired in via `MapScope`'s composition; the loader takes an `IAreaState` dependency in its ctor). If your existing `MapAssetLoader` doesn't have the `IAreaState` dep, add it as part of this task — review the constructor changes carefully.

- [ ] **Step 3: Update `MapAssetLoader` impl**

Open `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`. Replace the three private string fields and three public string-returning getters with the composite:

```csharp
private MapSceneRef? _currentScene;
private DateTimeOffset? _mapSceneMeasuredAt;

public MapSceneRef? CurrentMapScene => _currentScene;
public DateTimeOffset? MapSceneMeasuredAt => _mapSceneMeasuredAt;
```

Update the parser to construct/update the composite. The `Handle` method should:
1. Parse `SceneFriendlyName` (substring between `for area ` and ` runtime key `).
2. Parse `MapAssetKey` (substring inside the last `[…]` block).
3. Look up `ParentAreaKey` from the injected `IAreaState.CurrentArea`. If null, use `string.Empty` (which carries forward when no `Initializing area!` has fired yet — caller-visible as "we have an asset but not yet a parent area"; the resolution helper handles the empty case as a strict-gate trigger).
4. Build the composite via either fresh construction or `with`-expression if the same parent area and only `MapAssetKey`/`SceneFriendlyName` are differing.
5. Publish `MapAssetChanged(_previousScene, _currentScene, metadata)` when either field actually changed.

Example body:

```csharp
internal void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
{
    // ... existing malformed-line guards stay verbatim ...

    var friendlyName = ParseSceneFriendlyName(args);
    var assetKey = ParseMapAssetKey(args);
    if (friendlyName is null || assetKey is null) return; // malformed; silent skip

    var parentAreaKey = _areaState.CurrentArea ?? string.Empty;
    var prev = _currentScene;
    var next = prev is { } existing && existing.ParentAreaKey == parentAreaKey
        ? existing with { SceneFriendlyName = friendlyName, MapAssetKey = assetKey }
        : new MapSceneRef(parentAreaKey, friendlyName, assetKey);

    if (prev == next) return; // idempotent re-parse — no event

    _currentScene = next;
    _mapSceneMeasuredAt = metadata.Timestamp;
    _publisher.Publish(new MapAssetChanged(prev, next, metadata));
}
```

- [ ] **Step 4: Run tests to verify**

Run: `dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapAssetLoaderTests"`
Expected: all `MapAssetLoaderTests` PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs tests/Arda.World.Player.Tests/Internal/MapAssetLoaderTests.cs
git commit -m "refactor(arda): MapAssetLoader produces MapSceneRef composite (mithril#1041)"
```

---

### Task 5: Update `MapScope` delegations + tests

**Files:**
- Modify: `src/Arda/Arda.World.Player/Internal/MapScope.cs`
- Test: `tests/Arda.World.Player.Tests/Internal/MapScopeTests.cs`

- [ ] **Step 1: Update tests to expect composite delegation**

Open `tests/Arda.World.Player.Tests/Internal/MapScopeTests.cs`. Replace the three string-delegation tests with two composite-delegation tests:

```csharp
[Fact]
public void CurrentMapScene_DelegatesToMapAssetLoader()
{
    var scope = BuildScope(out var mapAsset);
    var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
    mapAsset.SetCurrentScene(scene); // test seam
    ((IMapState)scope).CurrentMapScene.Should().Be(scene);
}

[Fact]
public void MapSceneMeasuredAt_DelegatesToMapAssetLoader()
{
    var scope = BuildScope(out var mapAsset);
    var when = DateTimeOffset.UtcNow;
    mapAsset.SetMapSceneMeasuredAt(when); // test seam
    ((IMapState)scope).MapSceneMeasuredAt.Should().Be(when);
}
```

If the test seams don't exist, add internal `SetCurrentScene(MapSceneRef?)` and `SetMapSceneMeasuredAt(DateTimeOffset?)` test seams to `MapAssetLoader` (with `[InternalsVisibleTo("Arda.World.Player.Tests")]` already in place per the existing pattern).

- [ ] **Step 2: Update `MapScope` impl**

Open `src/Arda/Arda.World.Player/Internal/MapScope.cs`. Replace the three `CurrentMapAsset` / `CurrentSceneFriendlyName` / `MapAssetMeasuredAt` delegations with:

```csharp
public MapSceneRef? CurrentMapScene => mapAsset.CurrentMapScene;
public DateTimeOffset? MapSceneMeasuredAt => mapAsset.MapSceneMeasuredAt;
```

- [ ] **Step 3: Run tests to verify**

Run: `dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapScopeTests"`
Expected: 2 new delegation tests PASS; existing tests for `CurrentArea`, position, weather, pins still pass.

- [ ] **Step 4: Commit**

```bash
git add src/Arda/Arda.World.Player/Internal/MapScope.cs tests/Arda.World.Player.Tests/Internal/MapScopeTests.cs
git commit -m "refactor(arda): MapScope delegates CurrentMapScene + MapSceneMeasuredAt (mithril#1041)"
```

---

## Phase 2 — Calibration core API reshape

### Task 6: Retype `IMapCalibrationService` + delete `ImportUserRefinements`

**Files:**
- Modify: `src/Mithril.MapCalibration/IMapCalibrationService.cs`

- [ ] **Step 1: Replace the interface body**

Replace the entire body of `src/Mithril.MapCalibration/IMapCalibrationService.cs`:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Shared infra for per-scene world&#8596;pixel projection. Owns the catalogue
/// of solved <see cref="AreaCalibration"/> transforms (one per Unity asset key,
/// e.g. <c>"Map_AreaSerbule"</c>) and arbitrates between three anchor sources:
/// user refinement (highest precedence when residual is "good") &gt; community
/// sync (reserved slot, future) &gt; bundled baseline (fallback).
/// </summary>
/// <remarks>
/// Every public method takes a typed <see cref="MapSceneRef"/> (mithril#1041 —
/// promotes the type from projection identifier to universal calibration
/// identity). The impl reads <see cref="MapSceneRef.MapAssetKey"/> for the
/// inner dictionary lookup; <see cref="MapSceneRef.ParentAreaKey"/> and
/// <see cref="MapSceneRef.SceneFriendlyName"/> are along for the ride. The
/// callers' typed parameter prevents the "did I pass area or asset?" footgun
/// that bare-string parameters left in place.
/// </remarks>
public interface IMapCalibrationService
{
    /// <summary>True when an anchor source has produced a transform for the scene.</summary>
    bool IsCalibrated(MapSceneRef scene);

    /// <summary>Project a world coord to a pixel in the scene's map space. Returns null
    /// when the scene is uncalibrated.</summary>
    PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom);

    /// <summary>Inverse projection — pixel → world coord. Returns null when uncalibrated.</summary>
    WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom);

    /// <summary>The active calibration record for a scene (or null if uncalibrated).</summary>
    AreaCalibration? GetCalibration(MapSceneRef scene);

    /// <summary>All currently-active calibrations, keyed by <see cref="MapSceneRef.MapAssetKey"/>
    /// (the persistence horizon — the store knows only the asset key; for parent-area
    /// resolution use <c>ISceneAssetCache</c>). Reflects the stacked-source decision.</summary>
    IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; }

    /// <summary>Every candidate calibration for a scene, regardless of which one won.
    /// Used by debug surfaces that want to compare sources.</summary>
    IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene);

    /// <summary>Apply a per-user (or auto-captured) refinement. Persists; raises
    /// <see cref="Changed"/>; flows into the stacked transform per precedence.</summary>
    void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration);

    /// <summary>Drop a per-user refinement for a scene (revert to baseline / community).</summary>
    void ClearUserRefinement(MapSceneRef scene);

    /// <summary>
    /// Raised when the active transform changes for any scene. Payload = the
    /// changed scene (composite, not just the asset key — the writer has the
    /// full identity in hand at the raise site).
    ///
    /// <para><b>Threading contract:</b> delivered <em>synchronously on the
    /// thread that performed the write</em>. UI subscribers MUST marshal back
    /// onto the dispatcher themselves.</para>
    /// </summary>
    event EventHandler<MapSceneRef>? Changed;
}
```

> **Note:** `ImportUserRefinements` is deleted entirely (D6). Every reference to it in the impl + tests + consumers gets cleaned up in Tasks 7-8 and Task 22.

- [ ] **Step 2: Verify the interface compiles standalone**

Run: `dotnet build src/Mithril.MapCalibration`
Expected: build SUCCEEDS in `Mithril.MapCalibration` (broader solution will not — call sites in `AutoCalibrationEngine`, `OverlayWindowService`, `AreaCalibrationService`, etc. are still string-typed).

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/IMapCalibrationService.cs
git commit -m "refactor(map-calibration): IMapCalibrationService takes MapSceneRef everywhere; delete ImportUserRefinements (mithril#1041)"
```

---

### Task 7: Update `MapCalibrationService` impl

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`

- [ ] **Step 1: Retype every method**

Open `src/Mithril.MapCalibration/Internal/MapCalibrationService.cs`. Replace every method that took `string areaKey` to take `MapSceneRef scene` and extract `.MapAssetKey` for the inner lookup. Delete the `ImportUserRefinements` method entirely. Update `Changed` event to `EventHandler<MapSceneRef>?`. Update `RaiseChanged` to take `MapSceneRef`.

Concrete shape:

```csharp
public event EventHandler<MapSceneRef>? Changed;

public bool IsCalibrated(MapSceneRef scene) => GetCalibration(scene) is not null;

public AreaCalibration? GetCalibration(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return null;

    if (_userStore.TryGet(scene.MapAssetKey, out var user)
        && user.ResidualPixels <= _goodResidualThresholdPx)
        return user;

    if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) return baseline;

    if (_userStore.TryGet(scene.MapAssetKey, out var fallbackUser)) return fallbackUser;

    return null;
}

public PixelPoint? WorldToWindow(MapSceneRef scene, WorldCoord world, double currentZoom) =>
    GetCalibration(scene)?.WorldToWindow(world, currentZoom);

public WorldCoord? WindowToWorld(MapSceneRef scene, PixelPoint pixel, double currentZoom) =>
    GetCalibration(scene)?.WindowToWorld(pixel, currentZoom);

public IReadOnlyList<AreaCalibration> GetAllSources(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return Array.Empty<AreaCalibration>();
    var sources = new List<AreaCalibration>(capacity: 2);
    if (_userStore.TryGet(scene.MapAssetKey, out var user)) sources.Add(user);
    if (_baseline.TryGetValue(scene.MapAssetKey, out var baseline)) sources.Add(baseline);
    return sources;
}

public void SaveUserRefinement(MapSceneRef scene, AreaCalibration calibration)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey))
        throw new ArgumentException("scene.MapAssetKey required", nameof(scene));
    ArgumentNullException.ThrowIfNull(calibration);

    _userStore.Save(scene.MapAssetKey, calibration);
    _logger?.LogInformation("Saved user refinement for {MapAssetKey} (residual {Residual:F2}px, references {Count}).",
        scene.MapAssetKey, calibration.ResidualPixels, calibration.ReferenceCount);
    RaiseChanged(scene);
}

public void ClearUserRefinement(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return;
    if (_userStore.Remove(scene.MapAssetKey))
    {
        _logger?.LogInformation("Cleared user refinement for {MapAssetKey}.", scene.MapAssetKey);
        RaiseChanged(scene);
    }
}

// AllCalibrations stays IReadOnlyDictionary<string, AreaCalibration> — persistence horizon, unchanged.
public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations
{
    get
    {
        var keys = new HashSet<string>(_baseline.Keys, StringComparer.Ordinal);
        foreach (var key in _userStore.All.Keys) keys.Add(key);
        var result = new Dictionary<string, AreaCalibration>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            // The dict iteration is the only place where we synthesize a MapSceneRef from
            // a raw asset key. Parent area + scene friendly name are unknown to the store,
            // so we pass them as ("", null). GetCalibration only reads MapAssetKey.
            var synthetic = new MapSceneRef(ParentAreaKey: string.Empty, SceneFriendlyName: null, MapAssetKey: key);
            if (GetCalibration(synthetic) is { } cal) result[key] = cal;
        }
        return result;
    }
}

private void RaiseChanged(MapSceneRef scene)
{
    EventHandler<MapSceneRef>? handler;
    lock (_eventGate) handler = Changed;
    handler?.Invoke(this, scene);
}
```

**DELETE the `ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source)` method entirely** (lines 118-135 in the current file). The xmldoc reference at the top of the file should also be cleaned up.

- [ ] **Step 2: Verify the impl compiles standalone**

Run: `dotnet build src/Mithril.MapCalibration`
Expected: build SUCCEEDS in `Mithril.MapCalibration` (consumers still broken).

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/MapCalibrationService.cs
git commit -m "refactor(map-calibration): MapCalibrationService impl matches MapSceneRef-typed interface (mithril#1041)"
```

---

### Task 8: Delete `UserRefinementStore.ImportFromLegacy` + tests

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`
- Modify: `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreTests.cs`

- [ ] **Step 1: Delete the method**

Open `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs`. Delete the entire `ImportFromLegacy` method (lines 92-130) and the `MathEquals` helper if it's only used by `ImportFromLegacy`.

> **Check:** if `MathEquals` is also used elsewhere in the file, leave it. If not, delete it too.

Also delete the xmldoc cross-reference at line 96-99 ("Migration entry point...").

- [ ] **Step 2: Delete `ImportFromLegacy` tests**

Open `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreTests.cs`. Delete every test method whose name contains `ImportFromLegacy` (`Import_AddsNewEntries`, `Import_SkipsExisting`, `Import_OverwritesOnMathDiff`, etc.).

Keep the v1→v2 migrator tests verbatim — they're still load-bearing.

- [ ] **Step 3: Run tests to verify still-living tests pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~UserRefinementStoreTests"`
Expected: surviving tests PASS; `ImportFromLegacy` tests no longer exist.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/UserRefinementStore.cs tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreTests.cs
git commit -m "refactor(map-calibration): retire UserRefinementStore.ImportFromLegacy (mithril#1041 D6)"
```

---

## Phase 3 — `SceneAssetCache` mechanism

### Task 9: Add `ISceneAssetCache` interface + `SceneAssetCache` class with tests

**Files:**
- Create: `src/Mithril.MapCalibration/ISceneAssetCache.cs`
- Create: `src/Mithril.MapCalibration/SceneAssetCache.cs`
- Create: `tests/Mithril.MapCalibration.Tests/SceneAssetCacheTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Mithril.MapCalibration.Tests/SceneAssetCacheTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class SceneAssetCacheTests
{
    private static SceneAssetCache CreateInMemoryCache()
    {
        var store = new SceneAssetCacheStore(InMemoryFileBackend.Create(), NullLogger.Instance);
        return new SceneAssetCache(store, NullLogger.Instance);
    }

    [Fact]
    public void Record_Then_Resolve_RoundtripsTheMapSceneRef()
    {
        var cache = CreateInMemoryCache();
        var scene = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");
        cache.Record(scene, DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaCave1", "Hogan's Basement");

        resolved.Should().NotBeNull();
        resolved!.Value.Should().Be(scene);
    }

    [Fact]
    public void Record_OverwritesExisting_LiveWinsOverSeeded()
    {
        var cache = CreateInMemoryCache();
        var stale = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbuleOld");
        var fresh = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        cache.Record(stale, DateTimeOffset.UtcNow.AddMinutes(-5));
        cache.Record(fresh, DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaSerbule", null);

        resolved!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void TryResolve_WithNonNullFriendly_DoesNotMatchEntryStoredWithNullFriendly()
    {
        var cache = CreateInMemoryCache();
        cache.Record(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"), DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaSerbule", "Some Sub-Zone");

        resolved.Should().BeNull(); // composite-key strictness
    }

    [Fact]
    public void TryResolve_WithNullFriendly_DoesNotMatchEntryStoredWithNonNullFriendly()
    {
        var cache = CreateInMemoryCache();
        cache.Record(new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement"), DateTimeOffset.UtcNow);

        var resolved = cache.TryResolve("AreaCave1", null);

        resolved.Should().BeNull();
    }
}
```

> **Note on `InMemoryFileBackend`.** This is a test helper introduced in Task 10's tests (for `SceneAssetCacheStore`). For Task 9's tests we need an inline mock — adjust if `SceneAssetCacheStore` accepts a function-based backend, otherwise construct against the real disk in a temp dir via `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())`. Use `Path.GetTempPath` + cleanup in `IDisposable` if needed.

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheTests"`
Expected: COMPILE FAILURE — `SceneAssetCache`, `ISceneAssetCache`, `SceneAssetCacheStore` don't exist yet.

- [ ] **Step 3: Add the interface**

Create `src/Mithril.MapCalibration/ISceneAssetCache.cs`:

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Per-install cache of (ParentAreaKey, SceneFriendlyName?) → MapAssetKey
/// pairings learned from observed <c>MapAssetChanged</c> events and pre-seeded
/// at startup from the bundled-baseline ∩ areas.json intersection. Provides
/// the cold-start fallback for the resolution cascade
/// (see <c>SceneResolution.ResolveCurrentScene</c>): when <c>IMapState.CurrentMapScene</c>
/// is null but <c>CurrentArea</c> is known, the cache supplies a synthesized
/// <see cref="MapSceneRef"/> for the renderer / autocal-trigger / Legolas.
/// </summary>
public interface ISceneAssetCache
{
    /// <summary>Look up the cached <see cref="MapSceneRef"/> for a
    /// <c>(parentAreaKey, sceneFriendlyName)</c> pair. Composite-key strict —
    /// null friendly name does NOT match a stored entry with a non-null
    /// friendly name and vice versa. Returns null on miss.</summary>
    MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName);

    /// <summary>Write-through record of an observation. Overwrites any prior
    /// entry for the same composite key (live observation is authoritative;
    /// seed entries lose). Persists transactionally; on IOException the
    /// in-memory state is rolled back and the exception is re-thrown.</summary>
    void Record(MapSceneRef scene, DateTimeOffset observedAt);
}
```

- [ ] **Step 4: Add the class**

Create `src/Mithril.MapCalibration/SceneAssetCache.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mithril.MapCalibration.Internal;

namespace Mithril.MapCalibration;

/// <summary>
/// Default <see cref="ISceneAssetCache"/> implementation. Delegates persistence
/// to <see cref="SceneAssetCacheStore"/>; the cache itself is the thread-safe
/// in-memory dict + the write-through Record path.
/// </summary>
public sealed class SceneAssetCache : ISceneAssetCache
{
    private readonly SceneAssetCacheStore _store;
    private readonly ILogger? _logger;

    public SceneAssetCache(SceneAssetCacheStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName)
    {
        if (string.IsNullOrEmpty(parentAreaKey)) return null;
        if (_store.TryGet(parentAreaKey, sceneFriendlyName, out var entry))
            return new MapSceneRef(parentAreaKey, sceneFriendlyName, entry.MapAssetKey);
        return null;
    }

    public void Record(MapSceneRef scene, DateTimeOffset observedAt)
    {
        if (string.IsNullOrEmpty(scene.ParentAreaKey) || string.IsNullOrEmpty(scene.MapAssetKey))
            return; // an under-defined composite — don't poison the cache
        _store.Record(scene.ParentAreaKey, scene.SceneFriendlyName, scene.MapAssetKey, observedAt);
    }
}
```

- [ ] **Step 5: Run tests to verify they now fail at a later step (store still missing)**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheTests"`
Expected: COMPILE FAILURE — `SceneAssetCacheStore` doesn't exist yet. (Task 10.)

- [ ] **Step 6: Commit (preliminary — tests not green yet, but the interface + class shape is set)**

```bash
git add src/Mithril.MapCalibration/ISceneAssetCache.cs src/Mithril.MapCalibration/SceneAssetCache.cs tests/Mithril.MapCalibration.Tests/SceneAssetCacheTests.cs
git commit -m "feat(map-calibration): add ISceneAssetCache + SceneAssetCache (mithril#1041)"
```

---

### Task 10: Add `SceneAssetCacheStore` (persistence)

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/SceneAssetCacheStore.cs`
- Create: `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheStoreTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheStoreTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public class SceneAssetCacheStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SceneAssetCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Roundtrip_WriteThenReread_RestoresEntries()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.Record("AreaSerbule", null, "Map_AreaSerbule", DateTimeOffset.UtcNow);
        store.Record("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement", DateTimeOffset.UtcNow);

        var reloaded = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        reloaded.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        serbule.MapAssetKey.Should().Be("Map_AreaSerbule");
        reloaded.TryGet("AreaCave1", "Hogan's Basement", out var hogans).Should().BeTrue();
        hogans.MapAssetKey.Should().Be("Map_HogansKeepBasement");
    }

    [Fact]
    public void Load_EmptyFile_StartsEmpty()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.TryGet("AnyArea", null, out _).Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        // No prior writes; store loads from missing file
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.TryGet("AnyArea", null, out _).Should().BeFalse();
    }

    [Fact]
    public void Load_GarbageJson_StartsEmptyAndDoesNotThrow()
    {
        var filePath = Path.Combine(_tempDir, "scene-asset-cache.json");
        File.WriteAllText(filePath, "{ this is not valid json");

        var act = () => new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        act.Should().NotThrow();
    }

    [Fact]
    public void Load_PoisonedEntry_SkipsButLoadsOthers()
    {
        var filePath = Path.Combine(_tempDir, "scene-asset-cache.json");
        File.WriteAllText(filePath, """
            {
                "schemaVersion": 1,
                "entries": [
                    { "parentArea": "AreaSerbule", "sceneFriendlyName": null, "mapAssetKey": "Map_AreaSerbule", "lastObservedAt": "2026-06-03T20:01:17+00:00" },
                    { "parentArea": "Bad", "sceneFriendlyName": null, "mapAssetKey": null, "lastObservedAt": "this isn't a date" },
                    { "parentArea": "AreaEltibule", "sceneFriendlyName": null, "mapAssetKey": "Map_AreaEltibule", "lastObservedAt": "2026-06-03T20:01:17+00:00" }
                ]
            }
            """);

        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        store.TryGet("AreaSerbule", null, out _).Should().BeTrue();
        store.TryGet("AreaEltibule", null, out _).Should().BeTrue();
        store.TryGet("Bad", null, out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheStoreTests"`
Expected: COMPILE FAILURE — `SceneAssetCacheStore` doesn't exist.

- [ ] **Step 3: Add `SceneAssetCacheStore`**

Create `src/Mithril.MapCalibration/Internal/SceneAssetCacheStore.cs`:

```csharp
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>Per-install persistence for <see cref="SceneAssetCache"/>.
/// Mirrors <see cref="UserRefinementStore"/>'s transactional shape: atomic
/// temp+rename writes, IOException-rollback, per-entry resilient parse.</summary>
internal sealed class SceneAssetCacheStore
{
    private readonly string _filePath;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry> _entries
        = new(SceneAssetCacheKeyComparer.Ordinal);

    public SceneAssetCacheStore(string directory, ILogger? logger = null)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "scene-asset-cache.json");
        _logger = logger;
        Load();
    }

    public bool TryGet(string parentAreaKey, string? sceneFriendlyName, out SceneAssetCacheEntry entry)
    {
        lock (_gate)
        {
            var key = new SceneAssetCacheKey(parentAreaKey, sceneFriendlyName);
            return _entries.TryGetValue(key, out entry!);
        }
    }

    public void Record(string parentAreaKey, string? sceneFriendlyName, string mapAssetKey, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            var key = new SceneAssetCacheKey(parentAreaKey, sceneFriendlyName);
            var hadPrior = _entries.TryGetValue(key, out var prior);
            _entries[key] = new SceneAssetCacheEntry(mapAssetKey, observedAt);
            try { Persist(); }
            catch
            {
                if (hadPrior) _entries[key] = prior!;
                else _entries.Remove(key);
                throw;
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            using var stream = File.OpenRead(_filePath);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return;

            var loaded = new Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry>(SceneAssetCacheKeyComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                try
                {
                    if (!entry.TryGetProperty("parentArea", out var pa) ||
                        pa.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("mapAssetKey", out var ak) ||
                        ak.ValueKind != JsonValueKind.String) continue;

                    var parentArea = pa.GetString()!;
                    var mapAssetKey = ak.GetString()!;
                    string? sceneFriendlyName = null;
                    if (entry.TryGetProperty("sceneFriendlyName", out var sfn) &&
                        sfn.ValueKind == JsonValueKind.String) sceneFriendlyName = sfn.GetString();

                    DateTimeOffset observedAt = DateTimeOffset.MinValue;
                    if (entry.TryGetProperty("lastObservedAt", out var ts) &&
                        ts.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(ts.GetString(), out var parsedTs))
                        observedAt = parsedTs;

                    var key = new SceneAssetCacheKey(parentArea, sceneFriendlyName);
                    loaded[key] = new SceneAssetCacheEntry(mapAssetKey, observedAt);
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    _logger?.LogWarning(ex, "Skipping unparseable scene-asset-cache entry — {Reason}.", ex.Message);
                }
            }
            _entries = loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger?.LogWarning(ex, "Failed to load scene-asset-cache at {Path} — starting empty.", _filePath);
            _entries = new Dictionary<SceneAssetCacheKey, SceneAssetCacheEntry>(SceneAssetCacheKeyComparer.Ordinal);
        }
    }

    private void Persist()
    {
        // Serialise as { schemaVersion: 1, entries: [...] }.
        var sortedEntries = _entries
            .OrderBy(kv => kv.Key.ParentAreaKey, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.SceneFriendlyName, StringComparer.Ordinal)
            .Select(kv => new SceneAssetCacheFileEntry(
                ParentArea: kv.Key.ParentAreaKey,
                SceneFriendlyName: kv.Key.SceneFriendlyName,
                MapAssetKey: kv.Value.MapAssetKey,
                LastObservedAt: kv.Value.LastObservedAt))
            .ToArray();
        var file = new SceneAssetCacheFile(SchemaVersion: 1, Entries: sortedEntries);
        var json = JsonSerializer.Serialize(file, MapCalibrationJsonContext.Default.SceneAssetCacheFile);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
        else File.Move(tmp, _filePath);
    }
}

internal readonly record struct SceneAssetCacheKey(string ParentAreaKey, string? SceneFriendlyName);
internal readonly record struct SceneAssetCacheEntry(string MapAssetKey, DateTimeOffset LastObservedAt);

internal sealed class SceneAssetCacheKeyComparer : IEqualityComparer<SceneAssetCacheKey>
{
    public static readonly SceneAssetCacheKeyComparer Ordinal = new();
    public bool Equals(SceneAssetCacheKey x, SceneAssetCacheKey y) =>
        string.Equals(x.ParentAreaKey, y.ParentAreaKey, StringComparison.Ordinal) &&
        string.Equals(x.SceneFriendlyName, y.SceneFriendlyName, StringComparison.Ordinal);
    public int GetHashCode(SceneAssetCacheKey k) =>
        HashCode.Combine(k.ParentAreaKey, k.SceneFriendlyName ?? "");
}

internal sealed record SceneAssetCacheFile(int SchemaVersion, SceneAssetCacheFileEntry[] Entries);
internal sealed record SceneAssetCacheFileEntry(
    string ParentArea,
    string? SceneFriendlyName,
    string MapAssetKey,
    DateTimeOffset LastObservedAt);
```

- [ ] **Step 4: Add the JSON context entries**

Open `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs`. Add to the existing `[JsonSerializable]` declarations:

```csharp
[JsonSerializable(typeof(SceneAssetCacheFile))]
[JsonSerializable(typeof(SceneAssetCacheFileEntry))]
[JsonSerializable(typeof(SceneAssetCacheFileEntry[]))]
```

- [ ] **Step 5: Run all cache-store tests + the cache tests from Task 9**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCache"`
Expected: all `SceneAssetCacheTests` (Task 9) and `SceneAssetCacheStoreTests` (this task) PASS.

> **Edge case to verify by reading the test output:** `Roundtrip_WriteThenReread_RestoresEntries` should pass because `SceneAssetCacheStore`'s constructor calls `Load`; the second store reads the file the first wrote.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/SceneAssetCacheStore.cs src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheStoreTests.cs
git commit -m "feat(map-calibration): SceneAssetCacheStore persistence + JSON context (mithril#1041)"
```

---

### Task 11: Add `SceneAssetCacheSeeder`

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/SceneAssetCacheSeeder.cs`
- Create: `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheSeederTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheSeederTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Mithril.Shared.Reference.Models.Areas;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public class SceneAssetCacheSeederTests : IDisposable
{
    private readonly string _tempDir;

    public SceneAssetCacheSeederTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-seeder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void Seed_PopulatesIntersectionOfBaselineAndAreas()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        var baseline = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal)
        {
            ["Map_AreaSerbule"] = TestCalibration(),
            ["Map_AreaEltibule"] = TestCalibration(),
            ["Map_AreaKurMountains"] = TestCalibration(),
            ["Map_HogansKeepBasement"] = TestCalibration(), // present but no matching AreaX in areas.json
        };
        var areas = new Dictionary<string, AreaEntry>(StringComparer.Ordinal)
        {
            ["AreaSerbule"] = TestAreaEntry("AreaSerbule"),
            ["AreaEltibule"] = TestAreaEntry("AreaEltibule"),
            ["AreaKurMountains"] = TestAreaEntry("AreaKurMountains"),
            ["AreaCave1"] = TestAreaEntry("AreaCave1"), // present but no matching Map_AreaCave1 in baseline
        };

        SceneAssetCacheSeeder.Seed(store, baseline, areas, NullLogger.Instance);

        store.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        serbule.MapAssetKey.Should().Be("Map_AreaSerbule");
        store.TryGet("AreaEltibule", null, out _).Should().BeTrue();
        store.TryGet("AreaKurMountains", null, out _).Should().BeTrue();

        // No spurious seeds — AreaCave1 has no matching baseline, Map_HogansKeepBasement has no matching AreaX
        store.TryGet("AreaCave1", null, out _).Should().BeFalse();
        store.TryGet("HogansKeepBasement", null, out _).Should().BeFalse();
    }

    [Fact]
    public void Seed_DoesNotOverwriteEntriesFromObservation()
    {
        var store = new SceneAssetCacheStore(_tempDir, NullLogger.Instance);
        // Simulate a prior live observation that already populated the cache
        store.Record("AreaSerbule", null, "Map_AreaSerbuleObservedFromLive", DateTimeOffset.UtcNow);

        var baseline = new Dictionary<string, AreaCalibration>(StringComparer.Ordinal)
        {
            ["Map_AreaSerbule"] = TestCalibration(),
        };
        var areas = new Dictionary<string, AreaEntry>(StringComparer.Ordinal)
        {
            ["AreaSerbule"] = TestAreaEntry("AreaSerbule"),
        };

        SceneAssetCacheSeeder.Seed(store, baseline, areas, NullLogger.Instance);

        store.TryGet("AreaSerbule", null, out var serbule).Should().BeTrue();
        // Observation wins — seeded entry uses LastObservedAt = MinValue, observation has now.
        serbule.MapAssetKey.Should().Be("Map_AreaSerbuleObservedFromLive");
    }

    private static AreaCalibration TestCalibration() =>
        new AreaCalibration(
            Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
            MirrorNorth: false, CalibrationZoom: 1.0,
            ResidualPixels: 0.0, ReferenceCount: 0,
            Source: CalibrationSource.BundledBaseline, LocatorScale: null);

    private static AreaEntry TestAreaEntry(string key) =>
        new AreaEntry(Key: key, FriendlyName: key.Replace("Area", ""));
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheSeederTests"`
Expected: COMPILE FAILURE — `SceneAssetCacheSeeder` doesn't exist.

- [ ] **Step 3: Add `SceneAssetCacheSeeder`**

Create `src/Mithril.MapCalibration/Internal/SceneAssetCacheSeeder.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mithril.Shared.Reference.Models.Areas;

namespace Mithril.MapCalibration.Internal;

/// <summary>One-shot startup helper that pre-populates <see cref="SceneAssetCacheStore"/>
/// from the bundled-baseline ∩ areas.json intersection. For each baseline key
/// <c>"Map_&lt;X&gt;"</c> where <c>X</c> exists in <see cref="IReferenceDataService.Areas"/>,
/// records <c>(X, null) → "Map_X"</c> with <see cref="DateTimeOffset.MinValue"/> so any
/// real observation wins on first write.</summary>
internal static class SceneAssetCacheSeeder
{
    private const string MapAssetPrefix = "Map_";

    public static void Seed(
        SceneAssetCacheStore store,
        IReadOnlyDictionary<string, AreaCalibration> baseline,
        IReadOnlyDictionary<string, AreaEntry> areas,
        ILogger? logger = null)
    {
        var seeded = 0;
        foreach (var baselineKey in baseline.Keys)
        {
            if (!baselineKey.StartsWith(MapAssetPrefix, StringComparison.Ordinal)) continue;
            var areaCandidate = baselineKey.Substring(MapAssetPrefix.Length);
            if (!areas.ContainsKey(areaCandidate)) continue;

            // Skip if a prior observation already populated this cell — `Record`
            // would overwrite the observed entry, but we read it via TryGet so
            // we can compare timestamps before overwriting.
            if (store.TryGet(areaCandidate, null, out var existing) &&
                existing.LastObservedAt > DateTimeOffset.MinValue)
            {
                continue; // observation wins
            }

            store.Record(areaCandidate, sceneFriendlyName: null, mapAssetKey: baselineKey, DateTimeOffset.MinValue);
            seeded++;
        }

        if (seeded > 0)
            logger?.LogInformation("Seeded {Count} directly-registered scene-asset-cache entries from baseline ∩ areas.json.", seeded);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheSeederTests"`
Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/SceneAssetCacheSeeder.cs tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheSeederTests.cs
git commit -m "feat(map-calibration): SceneAssetCacheSeeder (baseline ∩ areas.json) (mithril#1041)"
```

---

### Task 12: Add `SceneAssetCacheRecorder` (`IHostedService`)

**Files:**
- Create: `src/Mithril.MapCalibration/Internal/SceneAssetCacheRecorder.cs`
- Create: `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheRecorderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheRecorderTests.cs`:

```csharp
using Arda.Contracts;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public class SceneAssetCacheRecorderTests
{
    [Fact]
    public async Task StartAsync_SubscribesAndRecordsLiveEvents()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        var scene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        bus.Fire(new MapAssetChanged(PreviousScene: null, CurrentScene: scene, Metadata: NewMetadata(isReplay: false)));

        cache.TryResolve("AreaSerbule", null)!.Value.MapAssetKey.Should().Be("Map_AreaSerbule");

        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RecordsReplayEventsToo()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        var scene = new MapSceneRef("AreaEltibule", null, "Map_AreaEltibule");
        bus.Fire(new MapAssetChanged(null, scene, NewMetadata(isReplay: true)));

        cache.TryResolve("AreaEltibule", null).Should().NotBeNull();
        await recorder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NullCurrentScene_DoesNotRecord()
    {
        var (cache, bus, recorder) = BuildHarness();
        await recorder.StartAsync(CancellationToken.None);

        bus.Fire(new MapAssetChanged(null, CurrentScene: null, NewMetadata(isReplay: false)));

        cache.TryResolve("AnyArea", null).Should().BeNull();
        await recorder.StopAsync(CancellationToken.None);
    }

    private static (TestCache, TestDomainEventBus, SceneAssetCacheRecorder) BuildHarness()
    {
        var cache = new TestCache();
        var bus = new TestDomainEventBus();
        var recorder = new SceneAssetCacheRecorder(bus, cache);
        return (cache, bus, recorder);
    }

    private static Arda.Abstractions.Logs.LogLineMetadata NewMetadata(bool isReplay) =>
        new(Timestamp: DateTimeOffset.UtcNow, IsReplay: isReplay);

    private sealed class TestCache : ISceneAssetCache
    {
        private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
        public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
            _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
        public void Record(MapSceneRef scene, DateTimeOffset observedAt) =>
            _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    }

    private sealed class TestDomainEventBus : IDomainEventSubscriber
    {
        private readonly List<Delegate> _handlers = new();
        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            _handlers.Add(handler);
            return new DummyDisposable(_handlers, handler);
        }
        public void Fire<T>(T evt) where T : struct
        {
            foreach (var h in _handlers.OfType<Action<T>>()) h(evt);
        }
        private sealed class DummyDisposable : IDisposable
        {
            private readonly List<Delegate> _list;
            private readonly Delegate _handler;
            public DummyDisposable(List<Delegate> list, Delegate handler) { _list = list; _handler = handler; }
            public void Dispose() => _list.Remove(_handler);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheRecorderTests"`
Expected: COMPILE FAILURE — `SceneAssetCacheRecorder` doesn't exist.

- [ ] **Step 3: Add `SceneAssetCacheRecorder`**

Create `src/Mithril.MapCalibration/Internal/SceneAssetCacheRecorder.cs`:

```csharp
using Arda.Contracts;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// <see cref="IHostedService"/> that subscribes to <see cref="MapAssetChanged"/> and
/// writes every observation to <see cref="ISceneAssetCache"/>. Replay metadata is
/// honoured the same as live — the file replay is the cheapest learning signal.
/// </summary>
internal sealed class SceneAssetCacheRecorder : IHostedService, IDisposable
{
    private readonly IDomainEventSubscriber _bus;
    private readonly ISceneAssetCache _cache;
    private readonly ILogger? _logger;
    private IDisposable? _subscription;

    public SceneAssetCacheRecorder(IDomainEventSubscriber bus, ISceneAssetCache cache, ILogger? logger = null)
    {
        _bus = bus;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnMapAssetChanged(MapAssetChanged evt)
    {
        if (evt.CurrentScene is not { } scene) return;
        try
        {
            _cache.Record(scene, evt.Metadata.Timestamp);
        }
        catch (Exception ex) when (ex is IOException)
        {
            // Lossy: in-memory state was rolled back by Record's transactional
            // wrapper. Log + drop; the next observation will retry.
            _logger?.LogWarning(ex,
                "Failed to persist scene-asset-cache entry for {ParentArea}/{Friendly}; will retry on next observation.",
                scene.ParentAreaKey, scene.SceneFriendlyName);
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneAssetCacheRecorderTests"`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/SceneAssetCacheRecorder.cs tests/Mithril.MapCalibration.Tests/Internal/SceneAssetCacheRecorderTests.cs
git commit -m "feat(map-calibration): SceneAssetCacheRecorder hosted service (mithril#1041)"
```

---

### Task 13: Add `SceneResolution` helper

**Files:**
- Create: `src/Mithril.MapCalibration/SceneResolution.cs`
- Create: `tests/Mithril.MapCalibration.Tests/SceneResolutionTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Mithril.MapCalibration.Tests/SceneResolutionTests.cs`:

```csharp
using Arda.World.Player;
using FluentAssertions;
using Mithril.MapCalibration;
using Xunit;

namespace Mithril.MapCalibration.Tests;

public class SceneResolutionTests
{
    [Fact]
    public void LiveCurrentMapScene_IsPreferred()
    {
        var live = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        var state = new FakeMapState { CurrentMapScene = live, CurrentArea = "AreaSerbule" };
        var cache = new FakeCache(); // empty

        SceneResolution.ResolveCurrentScene(state, cache).Should().Be(live);
    }

    [Fact]
    public void CacheFallback_WhenLiveSceneIsNull()
    {
        var cached = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = "AreaSerbule" };
        var cache = new FakeCache();
        cache.Add(("AreaSerbule", null), cached);

        SceneResolution.ResolveCurrentScene(state, cache).Should().Be(cached);
    }

    [Fact]
    public void StrictGate_BothLiveAndCacheNull()
    {
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = "AreaUnknown" };
        var cache = new FakeCache();

        SceneResolution.ResolveCurrentScene(state, cache).Should().BeNull();
    }

    [Fact]
    public void StrictGate_CurrentAreaIsEmpty()
    {
        var state = new FakeMapState { CurrentMapScene = null, CurrentArea = "" };
        var cache = new FakeCache();
        cache.Add(("", null), new MapSceneRef("", null, "Map_X"));

        // An empty parent area key is treated as unknown — never resolve through it.
        SceneResolution.ResolveCurrentScene(state, cache).Should().BeNull();
    }

    private sealed class FakeMapState : IMapState
    {
        public string? CurrentArea { get; set; }
        public string? PreviousArea { get; set; }
        public DateTimeOffset? TransitionedAt { get; set; }
        public MapSceneRef? CurrentMapScene { get; set; }
        public DateTimeOffset? MapSceneMeasuredAt { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public DateTimeOffset? PositionMeasuredAt { get; set; }
        public PositionSource? PositionSource { get; set; }
        public string? CurrentWeather { get; set; }
        public DateTimeOffset? WeatherMeasuredAt { get; set; }
        public IReadOnlyList<MapPinEntry> Pins => Array.Empty<MapPinEntry>();
    }

    private sealed class FakeCache : ISceneAssetCache
    {
        private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
        public void Add((string ParentArea, string? Friendly) key, MapSceneRef scene) => _store[key] = scene;
        public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
            _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
        public void Record(MapSceneRef scene, DateTimeOffset observedAt) =>
            _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneResolutionTests"`
Expected: COMPILE FAILURE — `SceneResolution` doesn't exist.

- [ ] **Step 3: Add the helper**

Create `src/Mithril.MapCalibration/SceneResolution.cs`:

```csharp
using Arda.World.Player;

namespace Mithril.MapCalibration;

/// <summary>Cold-start scene resolution helper consumed by every renderer +
/// autocal call site. Pure function, no side effects.</summary>
public static class SceneResolution
{
    /// <summary>Resolve the current <see cref="MapSceneRef"/> using the cascade
    /// (a) <see cref="IMapState.CurrentMapScene"/> (live truth, preferred),
    /// (b) <see cref="ISceneAssetCache.TryResolve"/> on
    /// <see cref="IMapState.CurrentArea"/> with <c>sceneFriendlyName: null</c>
    /// (seeded or learned), (c) <c>null</c> (strict gate — uncalibrated).</summary>
    public static MapSceneRef? ResolveCurrentScene(IMapState state, ISceneAssetCache cache)
    {
        if (state.CurrentMapScene is { } live) return live;
        if (state.CurrentArea is { Length: > 0 } area &&
            cache.TryResolve(area, sceneFriendlyName: null) is { } cached)
            return cached;
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~SceneResolutionTests"`
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/SceneResolution.cs tests/Mithril.MapCalibration.Tests/SceneResolutionTests.cs
git commit -m "feat(map-calibration): SceneResolution.ResolveCurrentScene helper (mithril#1041)"
```

---

### Task 14: Wire DI for cache services

**Files:**
- Modify: `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`

- [ ] **Step 1: Add DI registrations**

Open `src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs`. In the `AddMithrilMapCalibration` method, after the existing `IMapCalibrationService` registration, add:

```csharp
// Scene-asset cache (mithril#1041) — composite-key cache of observed/seeded
// (ParentArea, SceneFriendlyName?) → MapAssetKey pairings. Cold-start fallback
// for the resolution helper consumed by OverlayWindowService, AutoCalibrationTrigger,
// and AreaCalibrationService.
services.AddSingleton(sp => new Mithril.MapCalibration.Internal.SceneAssetCacheStore(
    directory: Path.Combine(sp.GetRequiredService<IUserDataPaths>().MithrilLocalAppData, "MapCalibration"),
    logger: sp.GetService<ILoggerFactory>()?.CreateLogger("MapCalibration.SceneAssetCacheStore")));
services.AddSingleton<Mithril.MapCalibration.ISceneAssetCache>(sp =>
    new Mithril.MapCalibration.SceneAssetCache(
        sp.GetRequiredService<Mithril.MapCalibration.Internal.SceneAssetCacheStore>(),
        sp.GetService<ILoggerFactory>()?.CreateLogger("MapCalibration.SceneAssetCache")));
services.AddHostedService<Mithril.MapCalibration.Internal.SceneAssetCacheRecorder>();
```

Then in the bootstrap path that runs after `IReferenceDataService` is constructible — find where `BundledBaselineLoader.Load(...)` is called and feed the same dict into `SceneAssetCacheSeeder.Seed(...)`:

```csharp
// After baseline is loaded (existing line) — seed the cache from baseline ∩ areas.json.
Mithril.MapCalibration.Internal.SceneAssetCacheSeeder.Seed(
    store: sp.GetRequiredService<Mithril.MapCalibration.Internal.SceneAssetCacheStore>(),
    baseline: bundledBaseline,
    areas: sp.GetRequiredService<IReferenceDataService>().Areas,
    logger: sp.GetService<ILoggerFactory>()?.CreateLogger("MapCalibration.SceneAssetCacheSeeder"));
```

> **Note:** the precise factory-lambda shape depends on the existing extension's structure (synchronous vs. lazily-resolved baseline). If `bundledBaseline` is captured in a `Func<>` or `Lazy<>` to defer until `IReferenceDataService` is ready, hook the seeder into the same deferred resolution. Read the existing extension carefully before editing.

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build src/Mithril.MapCalibration`
Expected: build SUCCEEDS in `Mithril.MapCalibration`. (Broader solution still broken — that's expected through Task 17.)

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/DependencyInjection/MapCalibrationServiceCollectionExtensions.cs
git commit -m "feat(map-calibration): wire DI for SceneAssetCache + Recorder + Seeder (mithril#1041)"
```

---

## Phase 4a — Capture consumers

### Task 15: Update `AutoCalibrationEngine` to read `CurrentMapScene`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs`

- [ ] **Step 1: Update `EngineFakes.FakeMapState` to carry the composite**

Open `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs`. Update `FakeMapState`:

```csharp
public sealed class FakeMapState : IMapState
{
    public string? CurrentArea { get; set; }
    public string? PreviousArea { get; set; }
    public DateTimeOffset? TransitionedAt { get; set; }
    public MapSceneRef? CurrentMapScene { get; set; }
    public DateTimeOffset? MapSceneMeasuredAt { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public DateTimeOffset? PositionMeasuredAt { get; set; }
    public PositionSource? PositionSource { get; set; }
    public string? CurrentWeather { get; set; }
    public DateTimeOffset? WeatherMeasuredAt { get; set; }
    public IReadOnlyList<MapPinEntry> Pins => Array.Empty<MapPinEntry>();
}
```

Add `FakeSceneAssetCache` if it doesn't already exist:

```csharp
public sealed class FakeSceneAssetCache : ISceneAssetCache
{
    private readonly Dictionary<(string, string?), MapSceneRef> _store = new();
    public void Add(MapSceneRef scene) => _store[(scene.ParentAreaKey, scene.SceneFriendlyName)] = scene;
    public MapSceneRef? TryResolve(string parentAreaKey, string? sceneFriendlyName) =>
        _store.TryGetValue((parentAreaKey, sceneFriendlyName), out var s) ? s : null;
    public void Record(MapSceneRef scene, DateTimeOffset observedAt) => Add(scene);
}
```

- [ ] **Step 2: Update existing engine tests to use the composite**

Open `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs`. For every test that sets `state.CurrentMapAsset = "Map_<X>"` and `state.CurrentSceneFriendlyName = "<Friendly>"`, switch to:

```csharp
state.CurrentMapScene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule");
```

Add a new test for the cache-fallback path:

```csharp
[Fact]
public async Task CacheFallback_ResolvesSceneFromCachedEntry()
{
    var fakes = new EngineFakes();
    fakes.MapState.CurrentMapScene = null;
    fakes.MapState.CurrentArea = "AreaSerbule";
    fakes.SceneAssetCache.Add(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"));

    var engine = fakes.BuildEngine();
    var outcome = await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

    // Engine reached the texture-provider phase using the cached MapAssetKey.
    fakes.TextureProvider.RequestedAssetKeys.Should().Contain("Map_AreaSerbule");
}
```

- [ ] **Step 3: Update `AutoCalibrationEngine` impl**

Open `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`. Constructor: inject `ISceneAssetCache` in addition to `IMapState`. Replace the strict-gate-on-`CurrentMapAsset` branch:

```csharp
public AutoCalibrationEngine(
    IMapState mapState,
    ISceneAssetCache sceneCache,
    IAreaReferenceProvider references,
    IBaseTextureProvider baseTextures,
    /* existing deps... */)
{
    _mapState = mapState;
    _sceneCache = sceneCache;
    /* existing assigns... */
}

public async Task<AutoCalibrationOutcome> TryCalibrateCurrentAreaAsync(CancellationToken ct)
{
    var scene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
    if (scene is null)
    {
        return new AutoCalibrationOutcome(
            Persisted: false,
            AreaKey: _mapState.CurrentArea ?? string.Empty,
            RejectReason: "Map asset not yet known — change zones once or restart while in this scene.",
            OutcomeCategory: OutcomeVocabulary.MapAssetNotYetKnown);
    }

    var assetKey = scene.Value.MapAssetKey;
    // ... rest of the method: replace every prior `area` / `CurrentMapAsset` read with `assetKey`
    //     and `_references.ForArea(...)` call's MapSceneRef argument with `scene.Value`.
    //     `GetCalibration(...)` calls take `scene.Value` typed.
}
```

Carefully audit the whole `AutoCalibrationEngine.cs` file — every use of `_areaState.CurrentArea`, `_mapState.CurrentMapAsset`, `_mapState.CurrentSceneFriendlyName` needs to either go through `scene.Value` (the composite) or be removed.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineTests"`
Expected: existing tests PASS (updated to the composite shape); new cache-fallback test PASSES.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs tests/Mithril.MapCalibration.Capture.Tests/
git commit -m "refactor(map-calibration): AutoCalibrationEngine reads CurrentMapScene + cache fallback (mithril#1041)"
```

---

### Task 16: Update `AutoCalibrationTrigger`

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs`
- Modify: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs`

- [ ] **Step 1: Update existing tests + add `MapAssetChanged` subscription test**

Open `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs`. Update fixture wiring to pass `FakeMapState` + `FakeSceneAssetCache`. Add:

```csharp
[Fact]
public async Task MapAssetChanged_TriggersAttemptForNewSubZone()
{
    var fakes = new TriggerFakes();
    fakes.MapState.CurrentArea = "AreaCave1";
    fakes.MapState.CurrentMapScene = new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement");

    await fakes.Trigger.StartAsync(CancellationToken.None);

    fakes.Bus.Fire(new MapAssetChanged(
        PreviousScene: new MapSceneRef("AreaCave1", "Hogan's Basement", "Map_HogansKeepBasement"),
        CurrentScene: new MapSceneRef("AreaCave1", "Goblin Dungeon", "Map_GoblinDungeon"),
        Metadata: new LogLineMetadata(DateTimeOffset.UtcNow, isReplay: false)));

    // Eventually — the trigger fire-and-forgets, but the runner.WasInvoked flag should flip
    await WaitFor(() => fakes.Runner.WasInvoked);
    fakes.Runner.WasInvoked.Should().BeTrue();
}
```

- [ ] **Step 2: Update `AutoCalibrationTrigger` impl**

Open `src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs`. Constructor: add `IMapState` + `ISceneAssetCache` injection. Add `_mapAssetChangedSub` field. In `StartAsync`:

```csharp
public Task StartAsync(CancellationToken cancellationToken)
{
    _areaChangedSub = _bus.Subscribe<AreaChanged>(OnAreaChanged);
    _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
    return Task.CompletedTask;
}

private void OnMapAssetChanged(MapAssetChanged evt)
{
    if (evt.CurrentScene is not { } scene) return;
    // Fire-and-forget to the thread pool — same shape as OnAreaChanged.
    _ = Task.Run(() => OnSceneChangedAsync(scene));
}
```

Refactor the existing `OnAreaChangedAsync(string area)` to take a `MapSceneRef`:

```csharp
internal async Task OnSceneChangedAsync(MapSceneRef scene)
{
    var key = scene.MapAssetKey;
    if (string.IsNullOrWhiteSpace(key)) return;

    lock (_gate)
    {
        if (_persistedScenes.Contains(key)) return;
        if (!_inFlightScenes.Add(key)) return;
    }

    try
    {
        if (_region.Current is null) return;
        if (_windowLocator.Locate() is null) return;

        var existing = _calibrationService.GetCalibration(scene);
        if (existing is not null && existing.Source != CalibrationSource.BundledBaseline) return;

        _logger.LogInformation("Auto-attempting calibration on scene-in to {AssetKey}.", key);
        AutoCalibrationOutcome? outcome = null;
        try { outcome = await _runner.TryCalibrateCurrentAreaAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Auto-calibration attempt for {AssetKey} threw.", key); }

        if (outcome is null) return;
        if (outcome.Persisted)
        {
            lock (_gate) { _persistedScenes.Add(key); }
            _overlay.SetStatusMessage(null);
        }
        else
        {
            _overlay.SetStatusMessage(CalibrationStatusFormatter.ForOutcome(outcome));
        }
    }
    finally
    {
        lock (_gate) { _inFlightScenes.Remove(key); }
    }
}
```

`OnAreaChanged` becomes a thinner wrapper that resolves via `SceneResolution.ResolveCurrentScene(_mapState, _sceneCache)` and delegates to `OnSceneChangedAsync`. Rename `_persistedAreas` → `_persistedScenes` and `_inFlightAreas` → `_inFlightScenes` (and their HashSet types stay `HashSet<string>` — keyed on `MapAssetKey`).

- [ ] **Step 3: Run tests to verify pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationTriggerTests"`
Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationTrigger.cs tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationTriggerTests.cs
git commit -m "refactor(map-calibration): AutoCalibrationTrigger subscribes to MapAssetChanged + uses MapSceneRef (mithril#1041)"
```

---

## Phase 4b — Renderer

### Task 17: Update `OverlayWindowService` + rename overlay interfaces

**Files:**
- Modify: `src/Mithril.Overlay/Internal/OverlayWindowService.cs`
- Modify: `src/Mithril.Overlay/IOverlaySceneContext.cs`
- Modify: `src/Mithril.Overlay/IWorldOverlayMarkers.cs`
- Modify: `tests/Mithril.Overlay.Tests/OverlayWindowServiceTests.cs`

- [ ] **Step 1: Rename `IOverlaySceneContext.CurrentAreaKey` → `CurrentMapAssetKey`**

Open `src/Mithril.Overlay/IOverlaySceneContext.cs`. Rename the property:

```csharp
/// <summary>The Unity asset key of the currently-rendered map scene
/// (e.g. <c>"Map_AreaSerbule"</c>) — what scene drawers index by for per-scene
/// resource caching. Was <c>CurrentAreaKey</c> pre-mithril#1041; the rename
/// matches the post-#1021 calibration-key shape.</summary>
string CurrentMapAssetKey { get; }
```

Update every consumer of `IOverlaySceneContext.CurrentAreaKey` in `src/` to use the new name (`Legolas.Module` scene drawers will surface here).

- [ ] **Step 2: Rename `IWorldOverlayMarkers.CurrentArea` → `CurrentMapAssetKey`**

Open `src/Mithril.Overlay/IWorldOverlayMarkers.cs`. Rename the setter property:

```csharp
/// <summary>The Unity asset key of the currently-rendered scene. Renamed from
/// <c>CurrentArea</c> by mithril#1041 — was a misnomer; the value has always
/// been the asset key, not the areas.json area.</summary>
string? CurrentMapAssetKey { get; set; }
```

- [ ] **Step 3: Update `OverlayWindowService`**

Open `src/Mithril.Overlay/Internal/OverlayWindowService.cs`. Add fields:

```csharp
private readonly IMapState _mapState;
private readonly ISceneAssetCache _sceneCache;
private readonly IDomainEventSubscriber _bus;
private IDisposable? _mapAssetChangedSub;
```

Constructor: add `IMapState`, `ISceneAssetCache`, and `IDomainEventSubscriber` injection alongside the existing deps. In whatever startup/init method exists (or in a new `IHostedService` if needed), subscribe to `MapAssetChanged`:

```csharp
public void Initialize() // or whatever the existing init seam is
{
    _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);
    // ... existing init ...
}

private void OnMapAssetChanged(MapAssetChanged evt)
{
    // Marshal to the WPF dispatcher and request a frame invalidation.
    if (Application.Current?.Dispatcher is { } disp && !disp.CheckAccess())
        disp.BeginInvoke(InvalidateNextFrame);
    else
        InvalidateNextFrame();
}

private void InvalidateNextFrame()
{
    // Use the existing surface-invalidation API; if there isn't one, the next
    // OnSurfaceRender tick will re-read state through ResolveCurrentScene anyway.
    _surface?.RequestRender();
}
```

In `OnSurfaceRender` (around line 270 — the existing method): replace every read of `_areaState.CurrentArea` with a single resolution call at the top:

```csharp
private void OnSurfaceRender(object? sender, RenderEventArgs e)
{
    SetReady(true);
    _brushCache.Bind(e.RenderTarget);

    var scene = SceneResolution.ResolveCurrentScene(_mapState, _sceneCache);
    if (scene is null)
    {
        SetStatusMessage(UncalibratedMessage);
        // Scene drawers still run for pixel-native passes (preserves #872/#887 dissolved-#868 invariant).
        _markers.CurrentMapAssetKey = null;
        InvokeSceneDrawerLoopWithoutCalibration(e);
        return;
    }

    var assetKey = scene.Value.MapAssetKey;
    _markers.CurrentMapAssetKey = assetKey;

    var isCalibrated = _calibration.IsCalibrated(scene.Value);
    if (!isCalibrated)
    {
        if (!string.Equals(_lastSeenUncalibratedAssetKey, assetKey, StringComparison.Ordinal))
        {
            _lastSeenUncalibratedAssetKey = assetKey;
            _logger?.LogInformation(
                "OverlayWindowService: scene {AssetKey} is uncalibrated; surfacing 'not calibrated' chip and skipping marker projection.",
                assetKey);
        }
        SetStatusMessage(UncalibratedMessage);
    }
    else
    {
        _lastSeenUncalibratedAssetKey = null;
        SetStatusMessage(null);
    }

    var currentZoom = SnapshotZoom();
    var drawers = _sceneDrawers;
    if (drawers.Length > 0)
    {
        _sceneContext.BeginFrame(e.RenderTarget, e.Factory, _brushCache, assetKey, currentZoom);
        using (var sceneAct = MithrilActivitySources.Overlay.StartActivity("scene"))
        {
            sceneAct?.SetTag("scene.asset_key", assetKey);
            sceneAct?.SetTag("drawer_count", drawers.Length);
            for (var i = 0; i < drawers.Length; i++)
                InvokeSceneDrawerIsolated(drawers[i], i);
        }
    }

    if (!isCalibrated) return;

    var snapshot = _markers.CurrentMapAssetKeyMarkers; // renamed accessor
    var projected = ProjectMarkers(snapshot, scene.Value, _calibration, currentZoom, onMiss: this, snapshotCount: snapshot.Count);
    // ... rest of the existing method (telemetry, render call) stays, just rename area→assetKey ...
}
```

`ProjectMarkers` signature changes: `string areaKey` → `MapSceneRef scene`. The `calibration.WorldToWindow(...)` call inside takes the typed scene.

`OverlaySceneContext.Project` ([:638-647](src/Mithril.Overlay/Internal/OverlayWindowService.cs:638)) captures the resolved scene into its `BeginFrame` snapshot (the parameter renames from `areaKey` to `mapAssetKey`):

```csharp
public PixelPoint? Project(double worldX, double worldZ)
{
    // The scene captured at BeginFrame time is stable for this frame.
    return _owner._calibration.WorldToWindow(
        _currentScene, new WorldCoord(worldX, 0, worldZ), _currentZoom);
}
```

This requires `OverlaySceneContext` to store the typed `MapSceneRef` from `BeginFrame` — adjust the field + parameter accordingly.

- [ ] **Step 4: Update tests**

Open `tests/Mithril.Overlay.Tests/OverlayWindowServiceTests.cs`. Update fakes — every `FakeMapCalibrationService.IsCalibrated(string)` becomes `IsCalibrated(MapSceneRef)` etc. Add fixtures for `IMapState` + `ISceneAssetCache` injection.

Add a cache-fallback test:

```csharp
[Fact]
public void OnSurfaceRender_CacheFallback_EngagesCalibratedRender()
{
    var fakes = new OverlayFakes();
    fakes.MapState.CurrentMapScene = null; // no live
    fakes.MapState.CurrentArea = "AreaSerbule";
    fakes.SceneAssetCache.Add(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"));
    fakes.MapCalibration.SetCalibrated(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"));

    fakes.Service.DriveSceneForTest(fakes.RenderTarget, fakes.Factory, "Map_AreaSerbule", currentZoom: 1.0);

    fakes.StatusMessage.Should().BeNull(); // no "uncalibrated" chip
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/Mithril.Overlay.Tests --filter "FullyQualifiedName~OverlayWindowServiceTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.Overlay/ tests/Mithril.Overlay.Tests/
git commit -m "refactor(overlay): OverlayWindowService consumes MapSceneRef + cache fallback (mithril#1041)"
```

---

## Phase 4c — Legolas

### Task 18: Update `IAreaCalibrationService` + `AreaCalibrationService` impl

**Files:**
- Modify: `src/Legolas.Module/Services/AreaCalibrationService.cs`
- Modify: `tests/Legolas.Module.Tests/Services/AreaCalibrationServiceTests.cs`

- [ ] **Step 1: Update existing tests to expect the typed shape**

Open `tests/Legolas.Module.Tests/Services/AreaCalibrationServiceTests.cs`. Every test asserting against `service.CurrentAreaKey` → switch to `service.CurrentScene?.ParentAreaKey` (for area-axis assertions) or `service.CurrentScene?.MapAssetKey` (for asset-axis assertions).

Add a test for the `OnMapCalChanged` equality bug fix:

```csharp
[Fact]
public void OnMapCalChanged_FiresChangedEvent_WhenAssetKeyMatches()
{
    var service = BuildService(/* with currentScene = (AreaSerbule, null, Map_AreaSerbule) */);
    var changed = false;
    service.Changed += (_, _) => changed = true;

    // Engine emits a Map_AreaSerbule-shaped Changed event
    fakes.MapCal.RaiseChanged(new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"));

    changed.Should().BeTrue(); // pre-#1041 this dropped because string.Equals(Map_AreaSerbule, AreaSerbule) was false
}
```

Delete every test that asserts against `_settings.AreaCalibrations[key] = ...` writes — that path is being deleted.

- [ ] **Step 2: Update `IAreaCalibrationService` + impl**

Open `src/Legolas.Module/Services/AreaCalibrationService.cs`. Interface changes:

```csharp
public interface IAreaCalibrationService
{
    /// <summary>Composite scene identity for the current calibration scope.</summary>
    MapSceneRef? CurrentScene { get; }

    string? CurrentAreaFriendlyName { get; } // unchanged
    bool IsCurrentAreaCalibrated { get; }
    AreaCalibration? CurrentCalibration { get; }
    IReadOnlyList<CalibrationReference> CurrentAreaReferences { get; }
    IReadOnlyList<AreaEntry> AllAreas { get; }
    event EventHandler? Changed;

    /// <summary>Select a scene by typed composite. Replaces SelectArea(string).</summary>
    void SelectScene(MapSceneRef scene);

    AreaCalibration? CalibrateCurrentArea(/* unchanged */);
    void ClearCurrentAreaCalibration();
    void NoteSurvey(string name, MetreOffset offset);
    event EventHandler<CalibrationSurveyObservation>? SurveyObserved;
}
```

Impl changes:

```csharp
public MapSceneRef? CurrentScene { get; private set; }

public bool IsCurrentAreaCalibrated =>
    CurrentScene is { } scene && _mapCal.IsCalibrated(scene);

public AreaCalibration? CurrentCalibration =>
    CurrentScene is { } scene ? _mapCal.GetCalibration(scene) : null;

public void SelectScene(MapSceneRef scene)
{
    if (string.IsNullOrWhiteSpace(scene.MapAssetKey)) return;

    CurrentScene = scene;
    CurrentAreaFriendlyName = _refData.Areas.TryGetValue(scene.ParentAreaKey, out var entry)
        ? entry.FriendlyName
        : scene.ParentAreaKey;
    _currentRefs = BuildReferences(scene.ParentAreaKey);

    if (_mapCal.GetCalibration(scene) is { } calibration)
        _projector.ApplyCalibration(calibration);

    Changed?.Invoke(this, EventArgs.Empty);
}

private void OnMapCalChanged(object? sender, MapSceneRef payload)
{
    if (CurrentScene is not { } current) return;
    if (payload.MapAssetKey != current.MapAssetKey) return;
    if (_mapCal.GetCalibration(current) is { } calibration)
        _projector.ApplyCalibration(calibration);
    Changed?.Invoke(this, EventArgs.Empty);
}

public AreaCalibration? CalibrateCurrentArea(
    IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements,
    double calibrationZoom = 1.0)
{
    if (CurrentScene is not { } scene || placements is null || placements.Count < 2)
        return null;

    var refs = placements
        .Select(p => new LandmarkCalibrationSolver.Reference(p.World.X, p.World.Z, p.Pixel))
        .ToList();
    var solved = LandmarkCalibrationSolver.Solve(refs);
    if (solved is null) return null;
    var calibration = solved with
    {
        CalibrationZoom = calibrationZoom > 1e-6 ? calibrationZoom : 1.0,
    };

    // SINGLE write path (D6) — no more dual-write to _settings.AreaCalibrations.
    _mapCal.SaveUserRefinement(scene, calibration);
    return calibration;
}

public void ClearCurrentAreaCalibration()
{
    if (CurrentScene is not { } scene) return;
    _mapCal.ClearUserRefinement(scene);
}
```

**DELETE** the dual-write at lines 224-225 (`_settings.AreaCalibrations[key] = calibration; _saver.Touch();`) and the dual-clear at line 264 (`if (_settings.AreaCalibrations.Remove(key)) _saver.Touch();`).

Drop the `SelectArea(string areaKey)` method entirely. If you need a transition wrapper for the manual area-picker call site in `CalibrationSessionViewModel`, add a small synthesizer:

```csharp
// Helper for the manual area-picker (CalibrationSessionViewModel): synthesize a MapSceneRef
// for a directly-registered area. The asset key follows the "Map_" + AreaEntry.Key convention.
// For aggregator areas this is wrong — but the picker only offers directly-registered areas.
public static MapSceneRef MapSceneRefForDirectlyRegisteredArea(string areaKey)
    => new MapSceneRef(areaKey, sceneFriendlyName: null, mapAssetKey: "Map_" + areaKey);
```

- [ ] **Step 3: Run tests to verify pass**

Run: `dotnet test tests/Legolas.Module.Tests --filter "FullyQualifiedName~AreaCalibrationServiceTests"`
Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Legolas.Module/Services/AreaCalibrationService.cs tests/Legolas.Module.Tests/Services/AreaCalibrationServiceTests.cs
git commit -m "refactor(legolas): AreaCalibrationService uses MapSceneRef + drops legacy dual-write (mithril#1041)"
```

---

### Task 19: Update `PlayerLogIngestionService` (subscribe to `MapAssetChanged`)

**Files:**
- Modify: `src/Legolas.Module/Services/PlayerLogIngestionService.cs`
- Modify: `tests/Legolas.Module.Tests/Services/PlayerLogIngestionServiceTests.cs`

- [ ] **Step 1: Update tests**

Open `tests/Legolas.Module.Tests/Services/PlayerLogIngestionServiceTests.cs`. Replace `AreaChanged` event firings with `MapAssetChanged` event firings carrying the composite. Assertions: `_areaCalibration.SelectScene(scene)` was called instead of `SelectArea(string)`.

- [ ] **Step 2: Update impl**

Open `src/Legolas.Module/Services/PlayerLogIngestionService.cs`. Drop `IAreaState _areaState` field + constructor parameter. Drop `_areaChangedSub` + `_lastArea` + `OnAreaChanged` method.

Add:

```csharp
private IDisposable? _mapAssetChangedSub;
private MapSceneRef? _lastScene;

public override Task StartAsync(CancellationToken cancellationToken)
{
    // No initial-state seed — IMapState's CurrentMapScene is null until the first
    // Downloading Map line, which Arda replays through the bus. The recorder's
    // cache and the resolution helper handle cold-start.

    _mapFxSub = _bus.Subscribe<MapFxObserved>(OnMapFxObserved);
    _delayLoopSub = _bus.Subscribe<DelayLoopStarted>(OnDelayLoopStarted);
    _screenTextSub = _bus.Subscribe<ScreenTextObserved>(OnScreenTextObserved);
    _mapAssetChangedSub = _bus.Subscribe<MapAssetChanged>(OnMapAssetChanged);

    _logger?.LogInformation("Subscribed to Arda domain events");
    return base.StartAsync(cancellationToken);
}

private void OnMapAssetChanged(MapAssetChanged evt)
{
    if (evt.CurrentScene is not { } scene) return;
    if (_lastScene is { } prev && prev == scene) return;
    _lastScene = scene;
    _areaCalibration.SelectScene(scene);
}

public override void Dispose()
{
    _mapFxSub?.Dispose();
    _delayLoopSub?.Dispose();
    _screenTextSub?.Dispose();
    _mapAssetChangedSub?.Dispose();
    base.Dispose();
}
```

- [ ] **Step 3: Run tests to verify pass**

Run: `dotnet test tests/Legolas.Module.Tests --filter "FullyQualifiedName~PlayerLogIngestionServiceTests"`
Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Legolas.Module/Services/PlayerLogIngestionService.cs tests/Legolas.Module.Tests/Services/PlayerLogIngestionServiceTests.cs
git commit -m "refactor(legolas): PlayerLogIngestionService subscribes to MapAssetChanged (mithril#1041)"
```

---

### Task 20: Delete `LegolasAreaCalibrationMigration`

**Files:**
- **DELETE:** `src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs`
- **DELETE:** `tests/Legolas.Module.Tests/Services/LegolasAreaCalibrationMigrationTests.cs` (if it exists)
- Modify: `src/Legolas.Module/LegolasModule.cs`

- [ ] **Step 1: Delete the migration service**

```bash
rm "src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs"
# Check if the test file exists; if it does, delete it
rm -f "tests/Legolas.Module.Tests/Services/LegolasAreaCalibrationMigrationTests.cs"
```

- [ ] **Step 2: Remove the `IHostedService` registration**

Open `src/Legolas.Module/LegolasModule.cs`. Find the line registering `LegolasAreaCalibrationMigration` (around line 122 or 169 based on earlier grep) and delete it. Also drop the `IAreaState` injection from any factory lambda that fed `PlayerLogIngestionService` (since that service no longer takes `IAreaState`).

- [ ] **Step 3: Verify the project compiles**

Run: `dotnet build src/Legolas.Module`
Expected: build SUCCEEDS.

- [ ] **Step 4: Commit**

```bash
git add -u src/Legolas.Module/Services/LegolasAreaCalibrationMigration.cs src/Legolas.Module/LegolasModule.cs tests/Legolas.Module.Tests/
git commit -m "refactor(legolas): retire LegolasAreaCalibrationMigration (mithril#1041 D6)"
```

---

### Task 21: Annotate `LegolasSettings.AreaCalibrations` `[Obsolete]`

**Files:**
- Modify: `src/Legolas.Module/LegolasSettings.cs` (or wherever `AreaCalibrations` is declared — grep first)

- [ ] **Step 1: Locate the field**

```bash
grep -n "AreaCalibrations" src/Legolas.Module/*.cs
```

- [ ] **Step 2: Add `[Obsolete]` annotation**

Open the file that declares `AreaCalibrations`. Replace the declaration with:

```csharp
/// <summary>Legacy per-area calibration dictionary lifted to
/// <see cref="Mithril.MapCalibration.IMapCalibrationService"/> in #836. This
/// field is retained for one release cycle so on-disk data isn't dropped from
/// existing <c>LegolasSettings.json</c> mid-cycle. Removed in a follow-up PR.</summary>
[Obsolete("Calibrations now live exclusively in IMapCalibrationService. " +
          "Field retained for one cycle to avoid downgrade-window data loss; " +
          "removed in a follow-up PR after mithril#1041.")]
public Dictionary<string, AreaCalibration> AreaCalibrations { get; init; } = new();
```

> **Important:** ensure the JSON source-generation context for `LegolasSettings` still includes this field — the serializer must preserve it for downgrade compat. If a `[JsonSerializable]` declaration excludes deprecated fields, do NOT add such exclusion.

> **Build warning treatment:** `Directory.Build.targets` enforces warnings-as-errors. The `[Obsolete]` annotation produces CS0618 in every existing reader of this field. The plan is to delete those readers (we've done that in Tasks 18 and 20). Any leftover reader will surface as a build error — fix or suppress with rationale.

- [ ] **Step 3: Verify the project compiles**

Run: `dotnet build src/Legolas.Module`
Expected: build SUCCEEDS (no more readers of `AreaCalibrations` after Tasks 18 + 20).

- [ ] **Step 4: Commit**

```bash
git add src/Legolas.Module/LegolasSettings.cs
git commit -m "refactor(legolas): mark LegolasSettings.AreaCalibrations [Obsolete] (mithril#1041 D6)"
```

---

### Task 22: Update Legolas ViewModels

**Files:**
- Modify: `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs`
- Modify: `src/Legolas.Module/ViewModels/LegolasWizardViewModel.cs`
- Modify: `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`
- Modify: `src/Legolas.Module/Services/PinCalibrationCoordinator.cs`

- [ ] **Step 1: `CalibrationSessionViewModel`**

Open `src/Legolas.Module/ViewModels/CalibrationSessionViewModel.cs`. Replace every `_service.CurrentAreaKey` (5 sites) with `_service.CurrentScene?.ParentAreaKey`. The line at `:268` comparing `value.Key == _service.CurrentAreaKey` becomes:

```csharp
if (value is null || value.Key == _service.CurrentScene?.ParentAreaKey) return;
```

The area-picker call at `:385` that today does something like `_service.SelectArea(...)` should call:

```csharp
_service.SelectScene(AreaCalibrationService.MapSceneRefForDirectlyRegisteredArea(value.Key));
```

(using the synthesizer added in Task 18).

- [ ] **Step 2: `LegolasWizardViewModel:437`**

Open `src/Legolas.Module/ViewModels/LegolasWizardViewModel.cs`. Replace:

```csharp
public bool IsAreaKnown => _areaCalibration.CurrentAreaKey is not null;
```

With:

```csharp
public bool IsAreaKnown => _areaCalibration.CurrentScene is not null;
```

- [ ] **Step 3: `MapOverlayViewModel`**

Open `src/Legolas.Module/ViewModels/MapOverlayViewModel.cs`. Find every read of `_areaCalibration.CurrentAreaKey` (3 sites) and replace with `_areaCalibration.CurrentScene?.ParentAreaKey`. The site at `:1089` that reads `_areaState?.CurrentArea is not { Length: > 0 } areaKey` — replace with:

```csharp
if (Mithril.MapCalibration.SceneResolution.ResolveCurrentScene(_areaState, _sceneCache) is not { } scene)
{
    return; // strict-gate
}
```

(Requires injecting `ISceneAssetCache` into `MapOverlayViewModel`. Update the constructor and DI registration to match.)

- [ ] **Step 4: `PinCalibrationCoordinator:488`**

Open `src/Legolas.Module/Services/PinCalibrationCoordinator.cs`. Replace the log-string fallback:

```csharp
// BEFORE
_service.CurrentAreaKey ?? "(unknown)"

// AFTER
_service.CurrentScene?.MapAssetKey ?? "(unknown)"
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Legolas.Module`
Expected: build SUCCEEDS.

- [ ] **Step 6: Commit**

```bash
git add src/Legolas.Module/ViewModels/ src/Legolas.Module/Services/PinCalibrationCoordinator.cs
git commit -m "refactor(legolas): ViewModels consume CurrentScene composite (mithril#1041)"
```

---

## Phase 5 — Integration + verification

### Task 23: Headline integration test

**Files:**
- Create: `tests/Legolas.Module.Tests/Integration/Legolas_PerSceneCalibration_IntegrationTests.cs`

- [ ] **Step 1: Write the integration test**

Create `tests/Legolas.Module.Tests/Integration/Legolas_PerSceneCalibration_IntegrationTests.cs`:

```csharp
using Arda.World.Player;
using FluentAssertions;
using Legolas.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Legolas.Module.Tests.Integration;

/// <summary>Headline regression-fix proof per mithril#1041 spec §5.8.
/// Verifies the three states of the resolution cascade against real
/// AreaCalibrationService + MapCalibrationService + SceneAssetCache wiring.</summary>
public class Legolas_PerSceneCalibration_IntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public Legolas_PerSceneCalibration_IntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mithril-headline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* */ }
    }

    [Fact]
    public void LiveTruth_AreaSerbule_RendersAgainstBaseline()
    {
        var harness = Harness.Build(_tempDir,
            mapStateConfig: state => { state.CurrentMapScene = new MapSceneRef("AreaSerbule", null, "Map_AreaSerbule"); });

        harness.AreaCalibration.SelectScene(harness.MapState.CurrentMapScene!.Value);

        harness.AreaCalibration.CurrentScene.Should().NotBeNull();
        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeTrue();
        harness.AreaCalibration.CurrentCalibration.Should().NotBeNull();
    }

    [Fact]
    public void CacheFallback_AreaSerbule_RendersAgainstBaseline()
    {
        var harness = Harness.Build(_tempDir,
            mapStateConfig: state => { state.CurrentMapScene = null; state.CurrentArea = "AreaSerbule"; });

        // Cache pre-seeded by Harness.Build — Map_AreaSerbule entry exists for (AreaSerbule, null).
        var resolved = SceneResolution.ResolveCurrentScene(harness.MapState, harness.SceneAssetCache);
        resolved.Should().NotBeNull();
        harness.AreaCalibration.SelectScene(resolved!.Value);

        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeTrue();
        harness.AreaCalibration.CurrentCalibration.Should().NotBeNull();
    }

    [Fact]
    public void StrictGate_UnknownArea_ReturnsNull()
    {
        var harness = Harness.Build(_tempDir,
            mapStateConfig: state => { state.CurrentMapScene = null; state.CurrentArea = "AreaUnknownNeverSeen"; });

        var resolved = SceneResolution.ResolveCurrentScene(harness.MapState, harness.SceneAssetCache);
        resolved.Should().BeNull();
        // AreaCalibrationService.SelectScene not called → CurrentScene stays null.
        harness.AreaCalibration.CurrentScene.Should().BeNull();
        harness.AreaCalibration.IsCurrentAreaCalibrated.Should().BeFalse();
    }

    private sealed class Harness
    {
        public IMapState MapState { get; init; } = null!;
        public ISceneAssetCache SceneAssetCache { get; init; } = null!;
        public IMapCalibrationService MapCalibration { get; init; } = null!;
        public IAreaCalibrationService AreaCalibration { get; init; } = null!;

        public static Harness Build(string tempDir, Action<FakeMapState> mapStateConfig)
        {
            // Real BundledBaselineLoader + UserRefinementStore (no user refinements)
            var baseline = BundledBaselineLoader.LoadFromBundled();
            var userStore = new UserRefinementStore(directory: tempDir, logger: NullLogger.Instance);
            var mapCal = new MapCalibrationService(baseline, userStore, goodResidualThresholdPx: 12.0, NullLogger.Instance);

            // Real SceneAssetCacheStore + SceneAssetCache + Seeder
            var cacheStore = new SceneAssetCacheStore(directory: tempDir, logger: NullLogger.Instance);
            var sceneCache = new SceneAssetCache(cacheStore, NullLogger.Instance);
            var fakeAreas = new Dictionary<string, Mithril.Shared.Reference.Models.Areas.AreaEntry>(StringComparer.Ordinal)
            {
                ["AreaSerbule"] = new("AreaSerbule", "Serbule"),
                ["AreaEltibule"] = new("AreaEltibule", "Eltibule"),
                ["AreaKurMountains"] = new("AreaKurMountains", "Kur Mountains"),
            };
            SceneAssetCacheSeeder.Seed(cacheStore, baseline, fakeAreas, NullLogger.Instance);

            // Fake IMapState
            var mapState = new FakeMapState();
            mapStateConfig(mapState);

            // Real AreaCalibrationService (needs IReferenceDataService — use a minimal fake)
            var refData = new FakeReferenceDataService(fakeAreas);
            var settings = new Legolas.Domain.LegolasSettings();
            var saver = new SettingsAutoSaver<Legolas.Domain.LegolasSettings>(/* test factory */ null!);
            var projector = new FakeCoordinateProjector();
            var areaCal = new AreaCalibrationService(refData, settings, projector, saver, mapCal);

            return new Harness
            {
                MapState = mapState,
                SceneAssetCache = sceneCache,
                MapCalibration = mapCal,
                AreaCalibration = areaCal,
            };
        }
    }

    private sealed class FakeMapState : IMapState
    {
        public string? CurrentArea { get; set; }
        public string? PreviousArea { get; set; }
        public DateTimeOffset? TransitionedAt { get; set; }
        public MapSceneRef? CurrentMapScene { get; set; }
        public DateTimeOffset? MapSceneMeasuredAt { get; set; }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public DateTimeOffset? PositionMeasuredAt { get; set; }
        public PositionSource? PositionSource { get; set; }
        public string? CurrentWeather { get; set; }
        public DateTimeOffset? WeatherMeasuredAt { get; set; }
        public IReadOnlyList<MapPinEntry> Pins => Array.Empty<MapPinEntry>();
    }

    // Fake IReferenceDataService / FakeCoordinateProjector boilerplate — match the existing
    // Legolas.Module.Tests fixture pattern. Look at AreaCalibrationServiceTests for examples
    // and copy the minimum surface needed.
}
```

> **Caveat:** the exact `IReferenceDataService` + `SettingsAutoSaver` + `ICoordinateProjector` fakes depend on the existing `Legolas.Module.Tests` fixture conventions. Read `tests/Legolas.Module.Tests/Services/AreaCalibrationServiceTests.cs` for the existing pattern and reuse fakes where possible.

- [ ] **Step 2: Run the integration test**

Run: `dotnet test tests/Legolas.Module.Tests --filter "FullyQualifiedName~Legolas_PerSceneCalibration_IntegrationTests"`
Expected: 3 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Legolas.Module.Tests/Integration/Legolas_PerSceneCalibration_IntegrationTests.cs
git commit -m "test(legolas): headline integration test for per-scene calibration resolution (mithril#1041)"
```

---

### Task 24: Full-solution build + test

- [ ] **Step 1: Close Mithril.exe if it's running**

Per memory `mithril_build_file_lock_silent`. The PreToolUse hook will refuse `dotnet build` otherwise.

- [ ] **Step 2: Clean build**

```bash
dotnet build Mithril.slnx
```

Expected: SUCCEEDS with no warnings (warnings-as-errors enforced). If `CS0618` surfaces on `LegolasSettings.AreaCalibrations`, find the leftover reader and remove it.

- [ ] **Step 3: Full test suite**

```bash
dotnet test Mithril.slnx
```

Expected: all tests PASS.

- [ ] **Step 4: Cleanup-verification grep checks (Section 3 Step 5 of the spec)**

```bash
# Should find no hits in src/
git grep -n "GetCalibration(.*areaKey\|IsCalibrated(string\|WorldToWindow(string\|WindowToWorld(string" -- src/
git grep -n "_settings\.AreaCalibrations\[" -- src/
git grep -n "ImportUserRefinements\|ImportFromLegacy" -- src/ tests/
git grep -n "AreaCalibrations\.Remove\|AreaCalibrations\.Add" -- src/ tests/
```

Expected: no output. (`IMapCalibrationService` xmldoc + the `[Obsolete]` field declaration are the only allowed references.)

```bash
# Should find the new test files
git grep -l "SceneAssetCacheTests\|SceneAssetCacheStoreTests\|SceneAssetCacheSeederTests\|SceneAssetCacheRecorderTests" -- tests/
```

Expected: 4 file matches.

- [ ] **Step 5: Commit any cleanup found**

If the cleanup grep surfaces leftover references, fix them and commit:

```bash
git add -A
git commit -m "chore(map-calibration): mop up leftover bare-key references (mithril#1041)"
```

---

### Task 25: Manual smoke test + INDEX.md update + PR

- [ ] **Step 1: Manual smoke test (owner-verified outside the unit-test gate)**

Per spec §8. Launch Mithril against:

1. A Player.log containing `Initializing area! AreaSerbule` + `Downloading Map ... Map_AreaSerbule`. Open the overlay. Confirm baseline-anchored render (no "uncalibrated" chip, markers project correctly).
2. The same `Initializing` line but **no** `Downloading Map`. Confirm cache-fallback resolves via the seed (Serbule baseline render appears anyway).
3. `Initializing area! AreaUnknown` (unrecognized area). Confirm strict gate engages (uncalibrated chip surfaces).

> Per memory `mithril_running_hook_misses_claude_worktrees`: if launching from a worktree, verify DLL-mtime > source-edit + a red→green transition yourself before claiming the smoke test passed.

- [ ] **Step 2: Update `docs/planning/INDEX.md`**

Open `docs/planning/INDEX.md`. Insert a new row (alphabetically sorted, between `map-calibration-1021-per-scene-keying` and `map-calibration-detection-project-split`):

```markdown
| [map-calibration-1041-mapsceneref-standardization](map-calibration-1041-mapsceneref-standardization/) | active | [#1041](https://github.com/moumantai-gg/mithril/issues/1041) | Promote `MapSceneRef` to universal calibration identity; SceneAssetCache for cold-start; retire #836 LegolasSettings.AreaCalibrations dual-write |
```

- [ ] **Step 3: Commit the INDEX update**

```bash
git add docs/planning/INDEX.md
git commit -m "docs(planning): index mithril#1041 MapSceneRef standardization (status=active)"
```

- [ ] **Step 4: Push the branch + open the PR**

```bash
# Push under a descriptive branch name (overwriting the harness-generated claude/* name)
git push origin HEAD:mithril/1041-mapsceneref-standardization

# Open the PR
gh pr create \
    --title "feat(map-calibration): MapSceneRef standardization + consumer migration (closes #1041)" \
    --body-file - <<EOF
## Summary

- Promotes \`MapSceneRef\` from "projection-time NPC-scope identifier" to the universal calibration identity south of \`IMapState\`. \`IMapCalibrationService\` methods retype to take \`MapSceneRef\`; \`IMapState.CurrentMapScene\` + \`MapAssetChanged\` payload collapse to the composite.
- Adds \`SceneAssetCache\` (persisted at \`%LocalAppData%/Mithril/MapCalibration/scene-asset-cache.json\`) for cold-start resolution when \`IMapState.CurrentMapScene == null\`. Pre-seeded at startup from the \`baseline.json ∩ areas.json\` intersection (12 directly-registered areas).
- Retires the #836 legacy parity loop: deletes \`LegolasAreaCalibrationMigration\`, \`IMapCalibrationService.ImportUserRefinements\`, \`UserRefinementStore.ImportFromLegacy\`, and the \`LegolasSettings.AreaCalibrations\` dual-write/clear. The settings field itself stays \`[Obsolete]\` for one cycle.
- Fixes the headline regression: \`OverlayWindowService\`, \`AutoCalibrationTrigger\`, and \`AreaCalibrationService\` all migrate off bare-area-key lookups against \`IMapCalibrationService\` (which #1040 made invisible to \`Map_<X>\`-keyed persistence).

## Spec + Plan

- Spec: \`docs/planning/map-calibration-1041-mapsceneref-standardization/spec.md\`
- Plan: \`docs/planning/map-calibration-1041-mapsceneref-standardization/plan.md\`

## Test plan

- [ ] \`dotnet build Mithril.slnx\` succeeds (warnings-as-errors enforced).
- [ ] \`dotnet test Mithril.slnx\` — full suite green, including \`Legolas_PerSceneCalibration_IntegrationTests\` (3 variants per spec §5.8).
- [ ] Manual smoke test §8 variants verified in a worktree-launched Mithril.exe.
- [ ] Grep cleanup checks per Task 24 Step 4 surface no leftover bare-key references.

— drafted by Claude (Opus 4.7), posted by @arthur-conde
EOF
```

Expected: PR URL returned. Confirm the PR opens in draft state if you want spec-review eyes first.

- [ ] **Step 5: Mark this task complete**

The PR is open; the implementing engineer's job is done. Reviewer eyes + owner smoke-test sign-off close out before merge.

---

## Self-review checklist (run after the plan is written)

This is the writing-plans skill's required self-review pass. Run mentally before handing off.

**1. Spec coverage:** every D1-D9 decision in spec §3 maps to one or more tasks:
- D1 (`MapSceneRef` everywhere): Tasks 1, 6, 7, 15-22.
- D2 (`Changed` payload retype): Tasks 6, 7.
- D3 (cache pre-seed + cascade): Tasks 9-14.
- D4 (renderer subscribes to `MapAssetChanged`): Task 17.
- D5 (drop `AreaChanged` in `PlayerLogIngestionService`): Task 19.
- D6 (retire `#836` parity loop): Tasks 8, 18, 20, 21.
- D7 (`UserRefinement*` rename deferred): NO task; explicitly out of scope.
- D8 (sub-zone picker deferred): NO task; explicitly out of scope.
- D9 (single atomic PR): captured in build-state caveat at top.

**2. Placeholder scan:** no "TBD", "TODO", "implement later" in the task bodies. Every code block contains complete code.

**3. Type consistency:** `MapSceneRef` constructor signature `(string ParentAreaKey, string? SceneFriendlyName, string MapAssetKey)` consistent across all tasks. `SceneAssetCacheKey` / `SceneAssetCacheEntry` consistent between `SceneAssetCache.cs` and `SceneAssetCacheStore.cs`. `SceneResolution.ResolveCurrentScene` signature consistent across all consumer-task code blocks.

**4. Files touched coverage:** every file listed in spec §6 appears in at least one task. Cross-checked.
