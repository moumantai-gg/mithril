# Per-Scene Calibration Keying Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Switch Mithril's auto-calibration store from `areas.json` `AreaX` keys (one-per-registered-area) to **literal per-scene Unity asset names** (`Map_HogansKeepBasement`, `Map_AreaSerbule`, … — verbatim from the `Downloading Map` log line). Unblocks the ~51 aggregator sub-zones autocal currently cannot calibrate.

**Architecture:** A new Arda L3 handler parses the unbracketed `Downloading Map [GUID] ... runtime key GUID[Map_<X>]` log line and writes the asset name + sub-zone friendly name into the existing `IMapState` umbrella. `AutoCalibrationEngine` switches DI from `IAreaState` to `IMapState`, reads `CurrentMapAsset`, and refuses outright when null (strict gate, per ratified D3). The NPC reference provider gains a composite `MapSceneRef(ParentAreaKey, SceneFriendlyName?)` key for sub-zone-aware filtering. Persistence (`map-calibration-baseline.json` + `UserRefinementStore`) migrates from `AreaSerbule` to `Map_AreaSerbule`-shaped keys via a `schemaVersion` bump 1→2 and a one-shot load-time prefix migrator.

**Tech Stack:** .NET 10 (`net10.0-windows`), C# 13, MSBuild `Mithril.slnx`, xUnit + FluentAssertions, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, `System.Text.Json` (source-generated contexts).

**Spec:** [`docs/planning/map-calibration-1021-per-scene-keying/spec.md`](spec.md). Decisions D1–D8 are ratified there; this plan does not re-litigate them.

**Issue:** [mithril#1021](https://github.com/moumantai-gg/mithril/issues/1021). Lands as a single squash-merged PR against `main`, independently of #914.

---

## Build / test cheat sheet

```bash
# Build everything (warnings as errors enforced; CleanBinObj clears stale obj/ first)
dotnet build Mithril.slnx

# Run all tests
dotnet test Mithril.slnx

# Run one test project
dotnet test tests/Arda.Dispatch.Tests

# Run one test by FQN substring
dotnet test tests/Arda.Dispatch.Tests --filter "FullyQualifiedName~VerbExtractorTests.Parse_DownloadingMap"
```

> **Important — close Mithril.exe before building.** The repo's `check-mithril-running.ps1` PreToolUse hook blocks `dotnet build/test` while the shell is running (stale-DLL file-lock protection, memory `mithril_build_file_lock_silent`). If a build mysteriously fails with `MSB3026` / `MSB3027`, close Mithril first.

---

## Implementation order

Tasks land in dependency order so the build stays green between commits. Phases:

1. **Arda foundation** (Tasks 1–7) — new verb + handler + IMapState extension + DI wire-up. Autocal hasn't switched over yet, so this is no-op for runtime behaviour but unblocks every downstream task.
2. **Calibration shared type** (Task 8) — `MapSceneRef`.
3. **Provider seam migration** (Tasks 9–10) — `IAreaReferenceProvider.ForArea` composite key + filter, plus param renames.
4. **Sidecar contract rename** (Tasks 11–12) — `ExtractRequest`/`ProcessAssetExtractor`/sidecar `Program.cs` accept `--asset`.
5. **Autocal switch** (Tasks 13–15) — DI swap, strict gate, status vocabulary.
6. **Persistence migration** (Tasks 16–19) — baseline.json hand-edit + `UserRefinementStore` v1→v2 migrator.
7. **Final integration** (Tasks 20–22) — full-solution build/test, INDEX.md status update on landing, PR.

---

## Phase 1 — Arda foundation

### Task 1: Add `Verbs.DownloadingMap` const

**Files:**
- Modify: `src/Arda/Arda.Dispatch/Verbs.cs` (after the `InitializingArea` const, around line 13)

- [ ] **Step 1: Add the synthetic verb constant**

```csharp
// Insert after the existing `public const string InitializingArea = "InitializingArea";` line.

/// <summary>Synthetic verb for the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_<X>]" asset-loader line.</summary>
public const string DownloadingMap = "DownloadingMap";
```

- [ ] **Step 2: Build (should succeed; nothing consumes the const yet)**

Run: `dotnet build Mithril.slnx`
Expected: succeeds with no warnings related to the new const.

- [ ] **Step 3: Commit**

```bash
git add src/Arda/Arda.Dispatch/Verbs.cs
git commit -m "feat(arda): add Verbs.DownloadingMap synthetic verb constant"
```

### Task 2: VerbExtractor recognizes `Downloading Map ` prefix

**Files:**
- Modify: `src/Arda/Arda.Dispatch/VerbExtractor.cs:50-62` (alongside the existing `LoadingLevel` / `InitializingArea` branches)
- Test: `tests/Arda.Dispatch.Tests/VerbExtractorTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `VerbExtractorTests.cs` after the existing `Parse_InitializingArea_ReturnsSyntheticKey` test:

```csharp
[Fact]
public void Parse_DownloadingMap_ReturnsSyntheticKey()
{
    var line = "Downloading Map [0e88b64bdd834cc41a23b8357802f254] GUID 0e88b64bdd834cc41a23b8357802f254 for area Kur Mountains runtime key 0e88b64bdd834cc41a23b8357802f254[Map_AreaKurMountains]";
    var result = VerbExtractor.Parse(line.AsSpan());
    result.Verb.ToString().Should().Be(Verbs.DownloadingMap);
    result.Args.ToString().Should().Be(
        "[0e88b64bdd834cc41a23b8357802f254] GUID 0e88b64bdd834cc41a23b8357802f254 for area Kur Mountains runtime key 0e88b64bdd834cc41a23b8357802f254[Map_AreaKurMountains]");
}

[Fact]
public void Parse_DownloadingMapAlone_ReturnsEmpty()
{
    // Defensive: bare "Downloading Map" without a space-prefixed body should not match;
    // it's a line shape we've never observed and shouldn't synthesize.
    var result = VerbExtractor.Parse("Downloading Map".AsSpan());
    result.IsEmpty.Should().BeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Arda.Dispatch.Tests --filter "FullyQualifiedName~VerbExtractorTests.Parse_DownloadingMap"`
Expected: 2 tests, both FAIL — `Parse_DownloadingMap_ReturnsSyntheticKey` asserts the verb is `"DownloadingMap"` but gets the empty result (IsEmpty=true).

- [ ] **Step 3: Add the prefix branch**

Insert in `VerbExtractor.Parse` after the existing `if (log.StartsWith("!!! Initializing area!"))` branch (around line 61):

```csharp
if (log.StartsWith("Downloading Map "))
    return new ParsedVerb(Verbs.DownloadingMap, log["Downloading Map ".Length..]);
```

(No bare-prefix branch for `"Downloading Map"` alone — the second test asserts that bare form remains unrecognized.)

- [ ] **Step 4: Run tests; both should pass**

Run: `dotnet test tests/Arda.Dispatch.Tests --filter "FullyQualifiedName~VerbExtractorTests.Parse_DownloadingMap"`
Expected: 2 PASS.

- [ ] **Step 5: Run the full Arda.Dispatch.Tests suite to confirm no regressions**

Run: `dotnet test tests/Arda.Dispatch.Tests`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Arda/Arda.Dispatch/VerbExtractor.cs tests/Arda.Dispatch.Tests/VerbExtractorTests.cs
git commit -m "feat(arda): VerbExtractor recognizes 'Downloading Map' asset-loader prefix"
```

### Task 3: Add `MapAssetChanged` domain event

**Files:**
- Create: `src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs`

- [ ] **Step 1: Create the event record**

```csharp
using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when PG's asset loader fetches a per-scene map texture
/// (the unbracketed "Downloading Map [GUID] ... runtime key GUID[Map_<X>]"
/// Player.log line). Carries both the literal Unity Texture2D name
/// (<paramref name="CurrentMapAsset"/>, including the <c>Map_</c> prefix)
/// and the sub-zone-level friendly name from the same line
/// (<paramref name="CurrentSceneFriendlyName"/>, matching npcs.json's
/// <c>AreaFriendlyName</c>). For aggregator <c>AreaX</c> entries (e.g.
/// <c>AreaCave1</c>), <see cref="CurrentSceneFriendlyName"/> identifies the
/// specific sub-scene where the parent area's <c>FriendlyName</c> would not.
/// </summary>
public readonly record struct MapAssetChanged(
    string? PreviousMapAsset,
    string? CurrentMapAsset,
    string? CurrentSceneFriendlyName,
    LogLineMetadata Metadata);
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Arda/Arda.Contracts/Events/Player/MapAssetChanged.cs
git commit -m "feat(arda): add MapAssetChanged domain event"
```

### Task 4: Implement `MapAssetLoader` handler

**Files:**
- Create: `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`
- Test: `tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs`

This is the bulk of the parser work. TDD step-by-step.

- [ ] **Step 1: Write the failing happy-path test**

Create `tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs`:

```csharp
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class MapAssetLoaderTests
{
    private readonly SpyEventBus _bus = new();
    private readonly MapAssetLoader _handler;

    public MapAssetLoaderTests()
    {
        _handler = new MapAssetLoader(_bus);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    /// <summary>Simulates what the DispatchTable does for a DownloadingMap verb:
    /// args is everything after "Downloading Map " in the source line.</summary>
    private void Dispatch(string friendlyArea, string mapAsset, LogLineMetadata? meta = null)
    {
        var source =
            $"Downloading Map [44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 "
            + $"for area {friendlyArea} runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[{mapAsset}]";
        var args = source["Downloading Map ".Length..].AsSpan();
        _handler.Handle(args, default, source, meta ?? Meta());
    }

    [Fact]
    public void Parses_HogansBasement_HappyPath()
    {
        Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

        _handler.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
        _handler.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");
        _handler.MapAssetMeasuredAt.Should().NotBeNull();

        var changed = _bus.Published<MapAssetChanged>().Should().ContainSingle().Subject;
        changed.PreviousMapAsset.Should().BeNull();
        changed.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
        changed.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapAssetLoaderTests"`
Expected: FAIL — type `MapAssetLoader` doesn't exist.

- [ ] **Step 3: Implement `MapAssetLoader`**

Create `src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs`:

```csharp
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Parses the unbracketed Player.log "Downloading Map [GUID] GUID GUID for
/// area <FriendlyAreaName> runtime key GUID[<AssetName>]" line (synthetic
/// verb <see cref="Verbs.DownloadingMap"/>) into per-scene map state. The
/// asset name in the runtime-key bracket is the literal Unity Texture2D
/// name (including the <c>Map_</c> prefix) and is the calibration key
/// downstream consumers use.
/// </summary>
/// <remarks>
/// <para>Malformed lines (missing <c>for area </c>, missing the runtime-key
/// bracket, empty args) are silently skipped — no state mutation, no event
/// published. The dispatch table doesn't inspect return values; safe-degrade
/// is the established Arda parser pattern.</para>
///
/// <para>Idempotent: a re-parse of the same line is a no-op event (state
/// changes once; subsequent identical parses don't fire <see cref="MapAssetChanged"/>).</para>
/// </remarks>
internal sealed class MapAssetLoader : IFrameHandler
{
    private readonly IDomainEventPublisher _bus;

    public MapAssetLoader(IDomainEventPublisher bus)
    {
        _bus = bus;
    }

    public string? CurrentMapAsset { get; private set; }
    public string? CurrentSceneFriendlyName { get; private set; }
    public DateTimeOffset? MapAssetMeasuredAt { get; private set; }

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        // Locate "for area " and " runtime key " — both must be present, in order,
        // for the line to be well-formed. Defensive IndexOf-from-after-marker pattern
        // mirrors the Map.cs InitializingArea handler.
        const string ForArea = "for area ";
        const string RuntimeKey = " runtime key ";

        var forAreaIdx = args.IndexOf(ForArea);
        if (forAreaIdx < 0) return;

        var friendlyStart = forAreaIdx + ForArea.Length;
        var runtimeKeyIdx = args[friendlyStart..].IndexOf(RuntimeKey);
        if (runtimeKeyIdx < 0) return;

        var friendlyName = args.Slice(friendlyStart, runtimeKeyIdx).ToString();

        // Asset name lives in the LAST [...] bracket. The earlier [GUID] at args-head
        // is also a bracket pair, so we match from the right.
        var lastOpen = args.LastIndexOf('[');
        var lastClose = args.LastIndexOf(']');
        if (lastOpen < 0 || lastClose < 0 || lastClose <= lastOpen + 1) return;

        var mapAsset = args.Slice(lastOpen + 1, lastClose - lastOpen - 1).ToString();

        // Idempotent: only mutate + publish on actual change.
        if (string.Equals(mapAsset, CurrentMapAsset, StringComparison.Ordinal)
            && string.Equals(friendlyName, CurrentSceneFriendlyName, StringComparison.Ordinal))
        {
            return;
        }

        var previous = CurrentMapAsset;
        CurrentMapAsset = mapAsset;
        CurrentSceneFriendlyName = friendlyName;
        MapAssetMeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new MapAssetChanged(previous, CurrentMapAsset, CurrentSceneFriendlyName, metadata));
    }
}
```

- [ ] **Step 4: Run the happy-path test; should pass**

Run: `dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapAssetLoaderTests.Parses_HogansBasement_HappyPath"`
Expected: PASS.

- [ ] **Step 5: Add edge-case tests (append to `MapAssetLoaderTests.cs`)**

```csharp
[Theory]
[InlineData("Hogan's Basement", "Map_HogansKeepBasement")]  // apostrophe
[InlineData("Caves Beneath Kur Mountains", "Map_AreaKurCaves")]  // spaces
[InlineData("Serbule", "Map_AreaSerbule")]
[InlineData("Anagoge Island", "Map_AreaNewbieIsland")]  // friendly renamed; asset codename stable
public void Parses_VariousFriendlyAndAssetForms(string friendly, string asset)
{
    Dispatch(friendly, asset);
    _handler.CurrentMapAsset.Should().Be(asset);
    _handler.CurrentSceneFriendlyName.Should().Be(friendly);
}

[Fact]
public void Idempotent_ReParse_DoesNotRepublish()
{
    Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
    Dispatch("Hogan's Basement", "Map_HogansKeepBasement");
    _bus.Published<MapAssetChanged>().Should().ContainSingle();
}

[Fact]
public void Transition_PopulatesPreviousMapAsset()
{
    Dispatch("Serbule", "Map_AreaSerbule");
    Dispatch("Hogan's Basement", "Map_HogansKeepBasement");

    var events = _bus.Published<MapAssetChanged>().ToList();
    events.Should().HaveCount(2);
    events[1].PreviousMapAsset.Should().Be("Map_AreaSerbule");
    events[1].CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
}

[Theory]
[InlineData("")]
[InlineData("[GUID] no for-area no runtime-key")]
[InlineData("[GUID] for area X but no runtime key delimiter")]
[InlineData("[GUID] for area X runtime key GUID no_close_bracket")]
[InlineData("[GUID] for area X runtime key GUID[]")]   // empty bracket
public void Malformed_SilentSkip_NoStateMutation_NoEvent(string args)
{
    _handler.Handle(args.AsSpan(), default, "Downloading Map " + args, Meta());
    _handler.CurrentMapAsset.Should().BeNull();
    _handler.CurrentSceneFriendlyName.Should().BeNull();
    _bus.Published<MapAssetChanged>().Should().BeEmpty();
}

[Fact]
public void LastBracketWins_NotTheArgsHeadGuidBracket()
{
    // The args-head [GUID] block must not be mistaken for the runtime-key bracket.
    // The happy-path tests already cover this implicitly; this is the explicit assertion.
    Dispatch("Test Area", "Map_TestScene");
    _handler.CurrentMapAsset.Should().Be("Map_TestScene");
    _handler.CurrentMapAsset.Should().NotStartWith("44d50fb"); // not the GUID
}
```

- [ ] **Step 6: Run all `MapAssetLoaderTests`; all should pass**

Run: `dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapAssetLoaderTests"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Arda/Arda.World.Player/Internal/MapAssetLoader.cs tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs
git commit -m "feat(arda): MapAssetLoader handler parses Downloading Map asset-loader line"
```

### Task 5: Extend `IMapState` contract

**Files:**
- Modify: `src/Arda/Arda.Contracts/State/Player/IMapState.cs` (insert between `// --- Area ---` and `// --- Position ---` blocks)

- [ ] **Step 1: Add the three new properties**

Insert after the existing `// --- Area ---` block (after the `TransitionedAt` property, before the `// --- Position ---` comment):

```csharp
    // --- Map asset (per-Unity-scene texture identity) ---

    /// <summary>Literal Unity Texture2D name for the currently-displayed map texture (e.g. <c>"Map_HogansKeepBasement"</c>),
    /// including the <c>Map_</c> prefix. Source: Player.log's <c>Downloading Map ... runtime key ...[Map_&lt;X&gt;]</c> line.
    /// <c>null</c> until the first such line is observed in this session.</summary>
    string? CurrentMapAsset { get; }

    /// <summary>Sub-zone-level friendly area name from the same line (e.g. <c>"Hogan's Basement"</c>), which for
    /// aggregator areas (<c>AreaCave1</c>, etc.) differs from <see cref="IMapState.CurrentArea"/>'s parent FriendlyName.
    /// Matches the per-NPC <c>AreaFriendlyName</c> field in npcs.json.</summary>
    string? CurrentSceneFriendlyName { get; }

    /// <summary>Timestamp of the most recent <c>Downloading Map</c> line.</summary>
    DateTimeOffset? MapAssetMeasuredAt { get; }
```

- [ ] **Step 2: Build (will fail — `MapScope` doesn't implement these yet)**

Run: `dotnet build Mithril.slnx`
Expected: build fails — `MapScope` doesn't implement the new IMapState members. **This is intentional** — Task 6 implements them.

- [ ] **Step 3: Continue to Task 6 to extend MapScope, then build + commit together**

Skip the commit here; Tasks 5 and 6 land as one commit.

### Task 6: Extend `MapScope` to delegate the new IMapState properties

**Files:**
- Modify: `src/Arda/Arda.World.Player/Internal/MapScope.cs`

- [ ] **Step 1: Add `MapAssetLoader` parameter + delegations**

Replace the existing class with:

```csharp
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Composite that implements <see cref="IMapState"/> by delegating to the
/// individual map-scoped handlers (<see cref="Map"/>, <see cref="Position"/>,
/// <see cref="Weather"/>, <see cref="MapPins"/>, <see cref="MapAssetLoader"/>).
/// Registered as a singleton; consumers inject <see cref="IMapState"/> for a
/// flat view of all map state.
/// </summary>
internal sealed class MapScope(
    Map map,
    Position position,
    Weather weather,
    MapPins pins,
    MapAssetLoader mapAsset) : IMapState
{
    public string? CurrentArea => map.CurrentArea;
    public string? PreviousArea => map.PreviousArea;
    public DateTimeOffset? TransitionedAt => map.TransitionedAt;

    public string? CurrentMapAsset => mapAsset.CurrentMapAsset;
    public string? CurrentSceneFriendlyName => mapAsset.CurrentSceneFriendlyName;
    public DateTimeOffset? MapAssetMeasuredAt => mapAsset.MapAssetMeasuredAt;

    public double? X => position.X;
    public double? Y => position.Y;
    public double? Z => position.Z;
    public DateTimeOffset? PositionMeasuredAt => position.MeasuredAt;
    public PositionSource? PositionSource => position.Source;

    public string? CurrentWeather => weather.CurrentWeather;
    public DateTimeOffset? WeatherMeasuredAt => weather.MeasuredAt;

    public IReadOnlyList<MapPinEntry> Pins => pins.Pins;
}
```

- [ ] **Step 2: Build (will still fail — `PlayerWorldExtensions` hasn't been updated)**

Run: `dotnet build Mithril.slnx`
Expected: build fails on `PlayerWorldExtensions.cs` line ~131 where `MapScope` is constructed with the old 4-arg signature.

- [ ] **Step 3: Continue to Task 7 for DI wiring**

Skip commit; Tasks 5, 6, 7 land as one commit.

### Task 7: Register `MapAssetLoader` in `PlayerWorldExtensions`

**Files:**
- Modify: `src/Arda/Arda.World.Player/PlayerWorldExtensions.cs`

- [ ] **Step 1: Register the singleton + update `MapScope` construction + register the dispatch handler**

Three edits in this file:

(a) Insert after the `// --- Map pins handler ---` block (around line 128) and **before** the `// --- Map scope composite ---` block:

```csharp
// --- Map asset loader handler (Downloading Map line) ---
builder.Services.AddSingleton(sp =>
{
    var bus = sp.GetRequiredService<IDomainEventPublisher>();
    return new MapAssetLoader(bus);
});
```

(b) Update the `MapScope` construction (around line 131) to pass the new dep:

```csharp
// --- Map scope composite (flat IMapState over all map-scoped handlers) ---
builder.Services.AddSingleton<IMapState>(sp => new MapScope(
    sp.GetRequiredService<Map>(),
    sp.GetRequiredService<Position>(),
    sp.GetRequiredService<Weather>(),
    sp.GetRequiredService<MapPins>(),
    sp.GetRequiredService<MapAssetLoader>()));
```

(c) Add the dispatch-table registration inside `ConfigureHandlers` (after the existing `RegisterHandler(registry, Verbs.InitializingArea, map);` line, around line 186):

```csharp
var mapAsset = sp.GetRequiredService<MapAssetLoader>();
RegisterHandler(registry, Verbs.DownloadingMap, mapAsset);
```

- [ ] **Step 2: Build (should succeed now)**

Run: `dotnet build Mithril.slnx`
Expected: clean build.

- [ ] **Step 3: Run Arda.World.Player.Tests; all should pass**

Run: `dotnet test tests/Arda.World.Player.Tests`
Expected: all green (no regressions in `MapTests`, `MapPinTests`, etc.).

- [ ] **Step 4: Commit Tasks 5+6+7 together**

```bash
git add src/Arda/Arda.Contracts/State/Player/IMapState.cs src/Arda/Arda.World.Player/Internal/MapScope.cs src/Arda/Arda.World.Player/PlayerWorldExtensions.cs
git commit -m "feat(arda): IMapState surfaces CurrentMapAsset + CurrentSceneFriendlyName

Extends the IMapState umbrella with three new properties delegated to
the new MapAssetLoader handler; registers MapAssetLoader as a singleton
and against Verbs.DownloadingMap in the dispatch table."
```

---

## Phase 2 — Calibration shared types

### Task 8: New `MapSceneRef` record

**Files:**
- Create: `src/Mithril.MapCalibration/MapSceneRef.cs`

- [ ] **Step 1: Create the record**

```csharp
namespace Mithril.MapCalibration;

/// <summary>
/// Composite identifier for a single Unity scene's calibration scope.
/// <see cref="ParentAreaKey"/> is the areas.json key (always non-null in
/// practice — Arda surfaces it from <c>!!! Initializing area! </c>).
/// <see cref="SceneFriendlyName"/> is the sub-zone-level npcs.json
/// <c>AreaFriendlyName</c>; <c>null</c> for directly-registered areas,
/// set for aggregator-area sub-zones (e.g. for the Hogan's Keep basement
/// scene under <c>AreaCave1</c>, <c>SceneFriendlyName</c> is
/// <c>"Hogan's Basement"</c>).
/// </summary>
/// <remarks>
/// Used by <see cref="Capture.IAreaReferenceProvider.ForArea"/> to scope
/// NPC lookups to the right sub-zone. Landmarks.json has no sub-zone field,
/// so the landmark filter uses <see cref="ParentAreaKey"/> alone — partial
/// coverage for aggregator scenes is documented in the spec (mithril#1021).
/// </remarks>
public readonly record struct MapSceneRef(
    string ParentAreaKey,
    string? SceneFriendlyName);
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration/MapSceneRef.cs
git commit -m "feat(map-calibration): add MapSceneRef composite scene-identity record"
```

---

## Phase 3 — Provider seam migration

### Task 9: Change `IAreaReferenceProvider.ForArea` signature + filter

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs`
- Modify: `src/Mithril.MapCalibration.Capture/ReferenceDataAreaReferenceProvider.cs:49-115`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AreaReferenceProviderTests.cs`

- [ ] **Step 1: Write failing tests for the new sub-zone filter**

Add to `AreaReferenceProviderTests.cs` (alongside the existing area-only tests):

```csharp
[Fact]
public void ForArea_AggregatorSceneFriendlyName_NarrowsNpcsToThatSubzone()
{
    var refData = new FakeAreaReferenceData
    {
        NpcsByInternalName =
        {
            ["NPC_Gorvessa"] = new() { AreaName = "AreaCave1", AreaFriendlyName = "Hogan's Basement", Name = "Gorvessa", Pos = "x:100 y:0 z:200" },
            ["NPC_Goblin"]   = new() { AreaName = "AreaCave1", AreaFriendlyName = "Goblin Dungeon",   Name = "Goblin",   Pos = "x:300 y:0 z:400" },
        },
    };
    var refs = new ReferenceDataAreaReferenceProvider(refData)
        .ForArea(new MapSceneRef("AreaCave1", "Hogan's Basement"));

    refs.Should().ContainSingle(r => r.Name == "Gorvessa");
    refs.Should().NotContain(r => r.Name == "Goblin");
}

[Fact]
public void ForArea_NullSceneFriendlyName_CollapsesToAreaOnlyFilter()
{
    // Directly-registered area case: SceneFriendlyName == null → existing behaviour,
    // every NPC under AreaName == ParentAreaKey is returned.
    var refData = new FakeAreaReferenceData
    {
        NpcsByInternalName =
        {
            ["NPC_A"] = new() { AreaName = "AreaSerbule", AreaFriendlyName = "Serbule", Name = "A", Pos = "x:1 y:0 z:1" },
            ["NPC_B"] = new() { AreaName = "AreaSerbule", AreaFriendlyName = "Serbule", Name = "B", Pos = "x:2 y:0 z:2" },
        },
    };
    var refs = new ReferenceDataAreaReferenceProvider(refData)
        .ForArea(new MapSceneRef("AreaSerbule", SceneFriendlyName: null));

    refs.Should().HaveCount(2);
}

[Fact]
public void ForArea_AggregatorWithoutMatchingFriendlyName_ReturnsEmpty()
{
    var refData = new FakeAreaReferenceData
    {
        NpcsByInternalName =
        {
            ["NPC_X"] = new() { AreaName = "AreaCave1", AreaFriendlyName = "Goblin Dungeon", Name = "X", Pos = "x:1 y:0 z:1" },
        },
    };
    var refs = new ReferenceDataAreaReferenceProvider(refData)
        .ForArea(new MapSceneRef("AreaCave1", "Hogan's Basement"));

    refs.Should().BeEmpty();
}
```

Also rewrite **every existing call-site** in this file (and `ReferenceProviderSolverSeamTests.cs`) from `.ForArea("AreaSerbule")` → `.ForArea(new MapSceneRef("AreaSerbule", null))`. Mechanical search-and-replace.

- [ ] **Step 2: Update `IAreaReferenceProvider` and the implementation**

`src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs`:

```csharp
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

public interface IAreaReferenceProvider
{
    /// <summary>
    /// Landmark + NPC references for the scene identified by
    /// <paramref name="sceneRef"/>. NPCs are filtered on
    /// <c>(AreaName == ParentAreaKey)</c>, further narrowed by
    /// <c>AreaFriendlyName == SceneFriendlyName</c> when the latter is non-null.
    /// Landmarks are filtered on <c>ParentAreaKey</c> alone (landmarks.json
    /// has no sub-zone field). For directly-registered areas
    /// (<see cref="MapSceneRef.SceneFriendlyName"/> null) the filter collapses
    /// to the legacy area-only behaviour.
    /// </summary>
    IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef);
}
```

`src/Mithril.MapCalibration.Capture/ReferenceDataAreaReferenceProvider.cs` — update the `ForArea` method signature and the NPC filter (the rest of the method body, landmark loop, and error counters stay byte-for-byte the same):

```csharp
public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef)
{
    var areaKey = sceneRef.ParentAreaKey;
    if (string.IsNullOrWhiteSpace(areaKey)) return Array.Empty<LandmarkReference>();

    var result = new List<LandmarkReference>();
    var malformedCoords = 0;

    // Landmarks: area-only filter (no sub-zone field on landmarks.json — partial
    // coverage for aggregator scenes is documented; RANSAC tolerates the noise).
    if (_refData.Landmarks.TryGetValue(areaKey, out var landmarks))
    {
        foreach (var lm in landmarks)
        {
            // ... existing landmark loop body unchanged ...
        }
    }

    // NPCs: (AreaName, AreaFriendlyName) pair when SceneFriendlyName is set,
    // else area-only (back-compat for directly-registered areas).
    foreach (var npc in _refData.NpcsByInternalName.Values)
    {
        if (npc is null) continue;
        if (!string.Equals(npc.AreaName, areaKey, StringComparison.Ordinal)) continue;
        if (sceneRef.SceneFriendlyName is { } sub
            && !string.Equals(npc.AreaFriendlyName, sub, StringComparison.Ordinal)) continue;
        if (string.IsNullOrWhiteSpace(npc.Pos)) continue;
        if (TryParseWorld(npc.Pos, out var world))
        {
            result.Add(new LandmarkReference(CanonicalLandmarkTypes.Npc, npc.Name ?? "NPC", world));
        }
        else
        {
            malformedCoords++;
        }
    }

    if (malformedCoords > 0)
    {
        _logger?.LogWarning(
            "Dropped {Count} reference(s) in area {Area} with malformed coords — possible landmarks.json / npcs.json "
            + "coord-shape change. Verification owed (#914): confirm the \"x:N y:N z:N\" position shape vs live data.",
            malformedCoords, areaKey);
    }

    return result;
}
```

- [ ] **Step 3: Update `EngineFakes.FakeAreaRefs` to implement the new signature**

In `tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs`, change `FakeAreaRefs.ForArea`:

```csharp
internal sealed class FakeAreaRefs : IAreaReferenceProvider
{
    public List<LandmarkReference> References { get; set; } = new();
    public MapSceneRef? LastSceneRef { get; private set; }

    public IReadOnlyList<LandmarkReference> ForArea(MapSceneRef sceneRef)
    {
        LastSceneRef = sceneRef;
        return References;
    }
}
```

- [ ] **Step 4: Build (every old call-site has been rewritten in Step 1 — build should be green)**

Run: `dotnet build Mithril.slnx`
Expected: clean build. If anything still calls `.ForArea("string")`, find and migrate it.

- [ ] **Step 5: Run the reference-provider test suite**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AreaReferenceProvider"`
Expected: all green, including the 3 new sub-zone tests.

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/IAreaReferenceProvider.cs \
        src/Mithril.MapCalibration.Capture/ReferenceDataAreaReferenceProvider.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AreaReferenceProviderTests.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs \
        tests/Mithril.MapCalibration.Capture.Tests/ReferenceProviderSolverSeamTests.cs
git commit -m "feat(map-calibration): IAreaReferenceProvider.ForArea takes MapSceneRef

Composite key (ParentAreaKey, SceneFriendlyName?) lets sub-zone scenes
in aggregator AreaX entries (AreaCave1, AreaCave2, ...) scope NPC
references to the right sub-zone. Null SceneFriendlyName collapses to
the existing area-only filter (back-compat for directly-registered
areas). Landmarks stay area-only (no sub-zone field on landmarks.json)."
```

### Task 10: Param renames on `IBaseTextureProvider` + `CachedBaseTextureProvider`

**Files:**
- Modify: `src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs`
- Modify: `src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs`

Mechanical cosmetic rename for honesty — `areaKey` → `mapAssetKey`. The string type is unchanged.

- [ ] **Step 1: Rename in the interface**

```csharp
// In IBaseTextureProvider.cs — the only method:
GrayImage? TryGetBaseTexture(string mapAssetKey);
```

Also update the docstring's example: `"AreaSerbule"` → `"Map_AreaSerbule"`. Note the new identifier is the literal Unity Texture2D name (link the wiki section in the docstring `<para>`).

- [ ] **Step 2: Rename in the implementation**

In `CachedBaseTextureProvider.cs`, every `areaKey` parameter / local rename to `mapAssetKey`. Log message templates retain the property name (`{Area}` → `{MapAsset}`) to keep telemetry consistent with the new identifier.

- [ ] **Step 3: Build + run Detection tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~CachedBaseTextureProvider"`
Expected: clean build + tests green.

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration.Detection/IBaseTextureProvider.cs src/Mithril.MapCalibration.Detection/Internal/CachedBaseTextureProvider.cs
git commit -m "refactor(map-calibration): rename TryGetBaseTexture(areaKey) to mapAssetKey"
```

---

## Phase 4 — Sidecar contract rename

### Task 11: Rename `ExtractRequest.AreaKey` → `MapAssetName`

**Files:**
- Modify: `src/Mithril.MapCalibration/IAssetExtractor.cs` (the record)
- Modify: `src/Mithril.MapCalibration.Detection/Internal/ProcessAssetExtractor.cs` (CLI flag emission)
- Modify any test fakes / call-sites that build `ExtractRequest`.

- [ ] **Step 1: Rename the record field**

In `IAssetExtractor.cs`:

```csharp
public sealed record ExtractRequest(
    string InstallRoot,
    string OutDir,
    ExtractKind Kind,
    string? MapAssetName,   // was: AreaKey
    string? ExpectPgVersion,
    string? TpkPath);
```

- [ ] **Step 2: Update the CLI flag emission in `ProcessAssetExtractor.BuildStartInfo`**

Find the existing `psi.ArgumentList.Add("--area");` (around line 161) and update:

```csharp
if (request.MapAssetName is not null)
{
    psi.ArgumentList.Add("--asset");
    psi.ArgumentList.Add(request.MapAssetName);
}
```

(The original `--area` branch will be replaced; same shape, new flag + property.)

- [ ] **Step 3: Update tests that construct `ExtractRequest`**

Grep `tests/` for `AreaKey:` (named-argument form) and rewrite to `MapAssetName:`. Same for any positional callers.

- [ ] **Step 4: Build + run AssetExtractor / ProcessAssetExtractor tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~ProcessAssetExtractor"`
Expected: clean build + tests green.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration/IAssetExtractor.cs src/Mithril.MapCalibration.Detection/Internal/ProcessAssetExtractor.cs tests/
git commit -m "refactor(map-calibration): rename ExtractRequest.AreaKey to MapAssetName; sidecar gets --asset

CLI flag --area renamed to --asset (sidecar Program.cs update lands
in the next commit). Record field name + ProcessAssetExtractor's
ArgumentList emission updated in lockstep."
```

### Task 12: Update sidecar `Program.cs` to accept `--asset`

**Files:**
- Modify: `tools/Mithril.AssetExtractor/Program.cs`
- Modify: `tools/Mithril.AssetExtractor/README.md`

- [ ] **Step 1: Update the argument parser**

In `Program.cs`, find the `case "--area":` line (around line 190 in the `Parse` method) and:

(a) Replace `case "--area":` with `case "--asset":`. Update the local variable name from `area` to `asset` for clarity.

(b) Update the `--icons + --area mutually exclusive` validation message: `--icons and --asset are mutually exclusive`.

(c) Update the required-arg validation: `one of --icons or --asset <Map_<X>> is required`.

(d) Inside the texture-extraction code path, when constructing the bundle glob, lowercase the asset name before matching (the existing code already does this for `Map_AreaSerbule` → `map_areaserbule`; verify the same path works for an `--asset Map_HogansKeepBasement` invocation and lowercases correctly).

(e) The variable that gets compared against `BundleGlobLowercase` / fed to the texture extractor was previously called `area`. Rename to `asset` throughout the local scope of the `Texture` extract kind for honesty; the **value** is now the literal `Map_<X>` form (including the prefix). The pre-existing path that built `"Map_" + area` to derive the texture name no longer needs to prepend — the asset variable already contains the full name. Locate any `"Map_" + area` or `$"Map_{area}"` interpolation in the sidecar and replace with the asset variable directly.

- [ ] **Step 2: Update the README**

In `tools/Mithril.AssetExtractor/README.md`:

(a) Replace every `--area <AreaKey>` with `--asset <MapAssetName>`.

(b) Update the example:

```bash
# one scene's map texture
mithril-asset-extract --install "C:\...\Project Gorgon" --out C:\tmp\cache --asset Map_AreaSerbule
mithril-asset-extract --install "C:\...\Project Gorgon" --out C:\tmp\cache --asset Map_HogansKeepBasement
```

(c) Update the "`Map_<AreaKey>` base texture" prose to say "the literal `Map_<AssetName>` Unity Texture2D name (e.g. `Map_AreaSerbule`, `Map_HogansKeepBasement`)".

- [ ] **Step 3: Build the sidecar (it's outside `Mithril.slnx` — build its csproj directly)**

Run: `dotnet build tools/Mithril.AssetExtractor`
Expected: clean build.

- [ ] **Step 4: If sidecar tests exist (`tools/Mithril.AssetExtractor.Tests`?), run them**

Run: `dotnet test tools/Mithril.AssetExtractor.Tests` (skip if directory doesn't exist)
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add tools/Mithril.AssetExtractor/Program.cs tools/Mithril.AssetExtractor/README.md
git commit -m "refactor(asset-extractor): CLI flag --area renamed to --asset

Accepts the literal Map_<AssetName> Unity Texture2D name (with the
Map_ prefix). Sidecar internally lowercases for the bundle glob
match; callers always pass PascalCase. README updated."
```

---

## Phase 5 — Autocal switch

### Task 13: New `MapAssetNotYetKnown` outcome vocabulary

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs`

- [ ] **Step 1: Add the vocabulary entry**

In `OutcomeVocabulary.cs` (alongside the existing outcome constants), add:

```csharp
/// <summary>
/// Refusal: autocal was invoked before any Downloading Map line was observed
/// in this session, so the per-scene Map_<X> asset name is unknown. The user
/// hint surfaces in the toast + Palantir debug surface.
/// </summary>
public const string MapAssetNotYetKnown = "map_asset_not_known";
```

- [ ] **Step 2: Build**

Run: `dotnet build Mithril.slnx`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/Diagnostics/OutcomeVocabulary.cs
git commit -m "feat(map-calibration): add MapAssetNotYetKnown outcome vocabulary entry"
```

### Task 14: `CalibrationStatusFormatter` routes the new outcome

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs`
- Test: `tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs` (if it exists; otherwise grep for an existing formatter test class to extend)

- [ ] **Step 1: Write the failing test (TDD)**

Add a test that confirms the formatter produces the user-facing string for the new outcome:

```csharp
[Fact]
public void Format_MapAssetNotYetKnown_ReturnsZoneChangeHint()
{
    var outcome = new AutoCalibrationOutcome(
        Persisted: false,
        AreaKey: "AreaCave1",
        RejectReason: OutcomeVocabulary.MapAssetNotYetKnown);

    CalibrationStatusFormatter.Format(outcome).Should().Contain("Map asset not yet known")
        .And.Contain("change zones once or restart while in this scene");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationStatusFormatter.Format_MapAssetNotYetKnown"`
Expected: FAIL.

- [ ] **Step 3: Add the format branch**

In `CalibrationStatusFormatter.Format`, add a case (alongside the existing `RejectReason` branches):

```csharp
if (outcome.RejectReason == OutcomeVocabulary.MapAssetNotYetKnown)
{
    return "Map asset not yet known — change zones once or restart while in this scene.";
}
```

- [ ] **Step 4: Run test; should pass**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~CalibrationStatusFormatter.Format_MapAssetNotYetKnown"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/CalibrationStatusFormatter.cs tests/Mithril.MapCalibration.Capture.Tests/CalibrationStatusFormatterTests.cs
git commit -m "feat(map-calibration): format MapAssetNotYetKnown as zone-change user hint"
```

### Task 15: `AutoCalibrationEngine` switches DI from `IAreaState` to `IMapState`; adds strict gate

**Files:**
- Modify: `src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs`
- Modify: `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` (DI registration if it explicitly mentions `IAreaState`)
- Test: `tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs`

- [ ] **Step 1: Write failing tests for the strict gate**

Add to `AutoCalibrationEngineTests.cs`:

```csharp
[Fact]
public async Task TryCalibrate_NoCurrentMapAsset_ReturnsRejectWithMapAssetNotYetKnown()
{
    var mapState = new FakeMapState
    {
        CurrentArea = "AreaCave1",          // areas.json key is set...
        CurrentMapAsset = null,             // ...but no scene-level asset known yet
        CurrentSceneFriendlyName = null,
    };
    var baseTextures = new FakeBaseTextureProvider();    // never invoked
    var solver = new FakeSolver();                       // never invoked

    var engine = BuildEngine(mapState: mapState, baseTextures: baseTextures, solver: solver);

    var outcome = await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

    outcome.Persisted.Should().BeFalse();
    outcome.RejectReason.Should().Be(OutcomeVocabulary.MapAssetNotYetKnown);
    outcome.AreaKey.Should().Be("AreaCave1"); // context for logs, not used as lookup key
    baseTextures.Calls.Should().BeEmpty();
    solver.SolveCalls.Should().Be(0);
}

[Fact]
public async Task TryCalibrate_CurrentMapAssetSet_PassesLiteralAssetKeyDownstream()
{
    var mapState = new FakeMapState
    {
        CurrentArea = "AreaCave1",
        CurrentMapAsset = "Map_HogansKeepBasement",
        CurrentSceneFriendlyName = "Hogan's Basement",
    };
    var baseTextures = new FakeBaseTextureProvider { ResolveAs = MakeGrayImage() };
    var refs = new FakeAreaRefs { References = SomeReferences() };
    var solver = new FakeSolver { Solve = ALandedCalibration() };

    var engine = BuildEngine(mapState, baseTextures, refs, solver);
    await engine.TryCalibrateCurrentAreaAsync(CancellationToken.None);

    baseTextures.Calls.Should().Contain("Map_HogansKeepBasement");
    refs.LastSceneRef.Should().Be(new MapSceneRef("AreaCave1", "Hogan's Basement"));
}
```

(Helper builders — `BuildEngine`, `MakeGrayImage`, `SomeReferences`, `ALandedCalibration`, `FakeBaseTextureProvider`, `FakeSolver`, `FakeMapState` — live in `EngineFakes.cs`. Extend that file as needed; `FakeMapState` is new — see Step 3.)

- [ ] **Step 2: Run tests; expect compile failures (FakeMapState doesn't exist) then runtime fail**

Run: `dotnet test tests/Mithril.MapCalibration.Capture.Tests --filter "FullyQualifiedName~AutoCalibrationEngineTests.TryCalibrate_"`
Expected: build failure on missing `FakeMapState`.

- [ ] **Step 3: Add `FakeMapState` to `EngineFakes.cs`**

```csharp
internal sealed class FakeMapState : IMapState
{
    public string? CurrentArea { get; set; }
    public string? PreviousArea { get; set; }
    public DateTimeOffset? TransitionedAt { get; set; }

    public string? CurrentMapAsset { get; set; }
    public string? CurrentSceneFriendlyName { get; set; }
    public DateTimeOffset? MapAssetMeasuredAt { get; set; }

    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public DateTimeOffset? PositionMeasuredAt { get; set; }
    public PositionSource? PositionSource { get; set; }

    public string? CurrentWeather { get; set; }
    public DateTimeOffset? WeatherMeasuredAt { get; set; }

    public IReadOnlyList<MapPinEntry> Pins { get; set; } = Array.Empty<MapPinEntry>();
}
```

If `FakeBaseTextureProvider` and `FakeSolver` aren't yet in `EngineFakes.cs`, add them with minimal call-recording shape (a `List<string> Calls` and an `int SolveCalls` counter).

- [ ] **Step 4: Apply the engine change**

In `AutoCalibrationEngine.cs`:

(a) Constructor: replace `IAreaState _areaState` with `IMapState _mapState`. Drop `IAreaState` dep.

(b) `TryCalibrateCurrentAreaAsync` (around line 151–153) replace:

```csharp
var area = _areaState.CurrentArea ?? string.Empty;
var attempt = new CalibrationAttemptContext(area, DateTimeOffset.UtcNow);
```

with:

```csharp
// Strict gate (#1021 D3): refuse outright when the per-scene asset isn't known yet.
if (string.IsNullOrEmpty(_mapState.CurrentMapAsset))
{
    return new AutoCalibrationOutcome(
        Persisted: false,
        AreaKey: _mapState.CurrentArea,
        RejectReason: OutcomeVocabulary.MapAssetNotYetKnown);
}

var assetKey = _mapState.CurrentMapAsset;
var sceneRef = new MapSceneRef(
    ParentAreaKey: _mapState.CurrentArea ?? string.Empty,
    SceneFriendlyName: _mapState.CurrentSceneFriendlyName);
var attempt = new CalibrationAttemptContext(assetKey, DateTimeOffset.UtcNow);
```

(c) Every downstream use of `area` in the method body is replaced with `assetKey` for the texture/sidecar path, and `sceneRef` for the reference-provider call (around line 308 — `var references = _references.ForArea(sceneRef);`).

(d) `ResolveBaseTextureAsync(string area, ...)` parameter is renamed to `string assetKey` for honesty; every internal use updated.

- [ ] **Step 5: Build + run tests**

Run: `dotnet build Mithril.slnx && dotnet test tests/Mithril.MapCalibration.Capture.Tests`
Expected: clean build, every existing AutoCalibrationEngine test still green, the two new tests green.

- [ ] **Step 6: Update DI registration if it referenced `IAreaState`**

Open `src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs` and search for `IAreaState`. If `AutoCalibrationEngine` was being constructed there with `sp.GetRequiredService<IAreaState>()`, replace with `sp.GetRequiredService<IMapState>()`. Otherwise (if it just resolves all deps from DI), no change.

- [ ] **Step 7: Build + run shell integration check**

Run: `dotnet build Mithril.slnx`
Expected: clean (DI graph still resolves).

- [ ] **Step 8: Commit**

```bash
git add src/Mithril.MapCalibration.Capture/AutoCalibrationEngine.cs \
        src/Mithril.MapCalibration.Capture/DependencyInjection/CaptureServiceCollectionExtensions.cs \
        tests/Mithril.MapCalibration.Capture.Tests/AutoCalibrationEngineTests.cs \
        tests/Mithril.MapCalibration.Capture.Tests/Fixtures/EngineFakes.cs
git commit -m "feat(map-calibration): autocal reads IMapState.CurrentMapAsset; strict gate on null

DI swap: AutoCalibrationEngine takes IMapState instead of IAreaState.
Reads CurrentMapAsset for the texture lookup key and builds a
MapSceneRef(ParentAreaKey, SceneFriendlyName) for the reference
provider. When CurrentMapAsset is null, returns Persisted:false with
RejectReason: MapAssetNotYetKnown — no detector or solver invoked.
Per #1021 D3 (ratified): no Map_<area> fallback reconstruction."
```

---

## Phase 6 — Persistence migration

### Task 16: Hand-edit bundled baseline.json

**Files:**
- Modify: `src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json`

- [ ] **Step 1: Rewrite the file**

Replace the contents with (anchor values byte-for-byte unchanged; only `schemaVersion` bump + key prefix):

```jsonc
{
  "$schema": "https://moumantai-gg.github.io/mithril/map-calibration-baseline-v1.json",
  "schemaVersion": 2,
  "anchors": {
    "Map_AreaSerbule": {
      "scale": 0.8225888770409359,
      "rotationRadians": 7.088147823900868E-05,
      "originX": -159.67286441908084,
      "originY": 2271.6816745475235,
      "referenceCount": 8,
      "residualPixels": 0.3030622255943075,
      "source": "BundledBaseline"
    },
    "Map_AreaEltibule": {
      "scale": 0.7632337115580504,
      "rotationRadians": 3.141276165642632,
      "originX": 2146.21356708398,
      "originY": -202.47314358570964,
      "referenceCount": 5,
      "residualPixels": 0.650247611982931,
      "source": "BundledBaseline"
    },
    "Map_AreaKurMountains": {
      "scale": 0.5686213259799479,
      "rotationRadians": -3.141467542457235,
      "originX": 2188.818672930765,
      "originY": -141.52793011390565,
      "referenceCount": 8,
      "residualPixels": 0.7317759321517253,
      "source": "BundledBaseline"
    }
  }
}
```

- [ ] **Step 2: Build (runs `BundledBaselineLoader` indirectly via existing tests if any)**

Run: `dotnet build Mithril.slnx`
Expected: clean.

- [ ] **Step 3: Run the baseline loader test (extending it in the next task)**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~BundledBaseline"`
Expected: existing tests still green (they may assert anchor shape but probably not specific key names; if anything fails, update the test snapshot in Task 17).

- [ ] **Step 4: Commit**

```bash
git add src/Mithril.MapCalibration/BundledData/map-calibration-baseline.json
git commit -m "chore(map-calibration): bump baseline.json to schemaVersion 2 with Map_<X> keys

AreaSerbule -> Map_AreaSerbule, AreaEltibule -> Map_AreaEltibule,
AreaKurMountains -> Map_AreaKurMountains. Anchor values byte-for-byte
unchanged; only the outer schema version + key prefix changes."
```

### Task 17: Extend `BundledBaselineLoaderTests` snapshot

**Files:**
- Modify: `tests/Mithril.MapCalibration.Tests/Internal/BundledBaselineLoaderTests.cs`

- [ ] **Step 1: Assert schemaVersion + Map_-prefixed keys**

Find the existing test that loads the baseline (or add one if absent — grep for `BundledBaseline` in `tests/Mithril.MapCalibration.Tests/`). Add:

```csharp
[Fact]
public void BundledBaseline_v2_KeysHaveMapPrefix()
{
    var loader = new BundledBaselineLoader(/* with the same ctor args the existing tests use */);
    var anchors = loader.Load();   // adapt to the existing API

    anchors.Should().ContainKey("Map_AreaSerbule");
    anchors.Should().ContainKey("Map_AreaEltibule");
    anchors.Should().ContainKey("Map_AreaKurMountains");
    anchors.Should().NotContainKey("AreaSerbule"); // catches an accidental partial rename
}
```

- [ ] **Step 2: Run the suite**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~BundledBaseline"`
Expected: green (including the new test).

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.MapCalibration.Tests/Internal/BundledBaselineLoaderTests.cs
git commit -m "test(map-calibration): assert baseline.json v2 keys are Map_-prefixed"
```

### Task 18: `UserRefinementStore` load-time migrator (v1 → v2)

**Files:**
- Modify: `src/Mithril.MapCalibration/Internal/UserRefinementStore.cs:163-200` (the existing `Load` method)
- Modify: `src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs` (if a wrapper DTO with `schemaVersion` is needed; otherwise stay on `JsonDocument`)

- [ ] **Step 1: Write failing migration test (TDD)**

Create `tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Internal;

public class UserRefinementStoreMigrationTests : IDisposable
{
    private readonly string _dir;
    public UserRefinementStoreMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mithril-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Path_ => Path.Combine(_dir, "refinements.json");

    private static string V1Json => """
        {
          "calibrations": {
            "AreaSerbule": {
              "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
              "referenceCount": 4, "residualPixels": 0.5,
              "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
            }
          }
        }
        """;

    [Fact]
    public void Load_V1File_PrefixesKeysWithMapAndPersistsAsV2()
    {
        File.WriteAllText(Path_, V1Json);

        var store = new UserRefinementStore(_dir);

        store.TryGet("Map_AreaSerbule", out var cal).Should().BeTrue();
        cal.Scale.Should().BeApproximately(0.82, 1e-9);

        // File rewritten with schemaVersion 2, Map_-prefixed key.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("calibrations").EnumerateObject()
            .Select(p => p.Name).Should().ContainSingle().Which.Should().Be("Map_AreaSerbule");
    }

    [Fact]
    public void Load_V2File_NoMutation()
    {
        var v2 = """
        {
          "schemaVersion": 2,
          "calibrations": {
            "Map_AreaSerbule": {
              "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
              "referenceCount": 4, "residualPixels": 0.5,
              "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
            }
          }
        }
        """;
        File.WriteAllText(Path_, v2);
        var before = File.ReadAllBytes(Path_);

        var store = new UserRefinementStore(_dir);
        store.TryGet("Map_AreaSerbule", out _).Should().BeTrue();

        // Idempotent: file unchanged byte-for-byte (no rewrite triggered).
        File.ReadAllBytes(Path_).Should().Equal(before);
    }

    [Fact]
    public void Load_V1FileWithAlreadyPrefixedKey_NotDoublePrefixed()
    {
        // Defensive: a v1 file with a key that already starts with "Map_" stays as-is.
        var weird = """
        {
          "calibrations": {
            "Map_AreaSerbule": {
              "scale": 0.82, "rotationRadians": 0.0, "originX": 100.0, "originY": 200.0,
              "referenceCount": 4, "residualPixels": 0.5,
              "source": "UserRefinement", "schemaVersion": 1, "calibrationZoom": 1.0, "mirrorNorth": false
            }
          }
        }
        """;
        File.WriteAllText(Path_, weird);

        var store = new UserRefinementStore(_dir);
        store.TryGet("Map_AreaSerbule", out _).Should().BeTrue();
        store.TryGet("Map_Map_AreaSerbule", out _).Should().BeFalse();
    }

    [Fact]
    public void Load_MissingFile_NoMigration_NoCrash()
    {
        var store = new UserRefinementStore(_dir);
        store.All.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests; expect failures**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~UserRefinementStoreMigration"`
Expected: 3 of 4 tests fail (the v2-no-mutation + missing-file cases may already pass since the existing `Load` is tolerant; the v1 prefix cases will fail because no migrator exists yet).

- [ ] **Step 3: Add the migrator to `Load`**

In `UserRefinementStore.Load`, after the existing JSON parse but before populating `_refinements`, add the version check:

```csharp
// Detect schema version. Absent field → v1 (legacy shape, predates this field).
int schemaVersion = 1;
if (doc.RootElement.TryGetProperty("schemaVersion", out var verProp)
    && verProp.ValueKind == JsonValueKind.Number
    && verProp.TryGetInt32(out var v))
{
    schemaVersion = v;
}

var needsMigration = schemaVersion < 2;
```

Inside the existing `foreach (var entry in calibrations.EnumerateObject())` loop, transform the key when migrating:

```csharp
var key = entry.Name;
if (needsMigration && !key.StartsWith("Map_", StringComparison.Ordinal))
{
    key = "Map_" + key;
}
// ... existing per-entry resilient parse uses `key` instead of `entry.Name` when populating `loaded` ...
loaded[key] = cal.Value;
```

After the loop, if `needsMigration && loaded.Count > 0`, persist immediately:

```csharp
_refinements = loaded;
if (needsMigration && _refinements.Count > 0)
{
    _logger?.LogInformation(
        "Migrated {Count} user refinement(s) to v2 (Map_<X> keying).",
        _refinements.Count);
    Persist();   // existing transactional Persist path; throws → rolls back via the existing pattern below
}
else
{
    _refinements = loaded;   // (no migration, just assign — the line above is in the migrate-then-persist branch)
}
```

Update `Persist` (or wherever the file gets written) to include a top-level `"schemaVersion": 2` field alongside the existing `"calibrations"` block. The exact serialization site is in the existing `Persist()` method — add `schemaVersion: 2` to the wrapper.

- [ ] **Step 4: Run the migration tests; all should pass**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~UserRefinementStoreMigration"`
Expected: 4 PASS.

- [ ] **Step 5: Run the broader `UserRefinementStore` tests (pre-existing)**

Run: `dotnet test tests/Mithril.MapCalibration.Tests --filter "FullyQualifiedName~UserRefinementStore"`
Expected: all green. **If a pre-existing test seeds a v1-shaped file and asserts a bare key, update it to assert the migrated Map_-prefixed key.**

- [ ] **Step 6: Commit**

```bash
git add src/Mithril.MapCalibration/Internal/UserRefinementStore.cs \
        src/Mithril.MapCalibration/Internal/MapCalibrationJsonContext.cs \
        tests/Mithril.MapCalibration.Tests/Internal/UserRefinementStoreMigrationTests.cs
git commit -m "feat(map-calibration): UserRefinementStore v1->v2 load-time prefix migrator

Detects absent schemaVersion as v1 and prefixes every refinement key
with Map_ on load, then persists immediately with schemaVersion: 2.
Idempotent: a v2 file is a no-op load (file untouched on disk).
Defensive: a v1 file with a key that already starts with Map_ is
not double-prefixed."
```

### Task 19: Telemetry tag for the new outcome

**Files:**
- Modify: `src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs` (or wherever `MapCalibration.Attempts` is declared)
- Modify: `docs/perf-trace-schema.md` (add the new tag)

- [ ] **Step 1: Locate the existing `MapCalibration.Attempts` counter**

Grep: `grep -n "MapCalibration.Attempts" src/Mithril.Shared/Diagnostics/Telemetry/`. The counter and its tag catalog live alongside.

- [ ] **Step 2: Add `outcome` tag value `map_asset_not_known` to the tag catalog**

If the tag catalog has an explicit allowed-values list, add `"map_asset_not_known"` to it (default-on per the Safe/Identifying convention; no scrubbing).

- [ ] **Step 3: Update `docs/perf-trace-schema.md`**

Find the row documenting the `outcome` tag's allowed values for `map_calibration.attempts` and append `map_asset_not_known` to the list with a one-line description.

- [ ] **Step 4: Build**

Run: `dotnet build Mithril.slnx`
Expected: clean.

- [ ] **Step 5: Commit**

```bash
git add src/Mithril.Shared/Diagnostics/Telemetry/MithrilMeters.cs docs/perf-trace-schema.md
git commit -m "docs(telemetry): record 'outcome=map_asset_not_known' on map_calibration.attempts"
```

### Task 20: Golden-fixture extension

**Files:**
- Modify: `tests/Mithril.Shared.Tests/Logging/Fixtures/per-rule/asset-loader-noise.log`

- [ ] **Step 1: Append a real `Downloading Map` block**

Append (preserving the existing 21 lines):

```
Downloading Map [44d50fb35fa65dd4cbb84e3af49ca0a4] GUID 44d50fb35fa65dd4cbb84e3af49ca0a4 for area Hogan's Basement runtime key 44d50fb35fa65dd4cbb84e3af49ca0a4[Map_HogansKeepBasement]
Completed load of [44d50fb35fa65dd4cbb84e3af49ca0a4]: ChainOperation<IList`1> - Dependencies [Assets/Art/Maps/Map_HogansKeepBasement.png, Assets/Art/Maps/Map_HogansKeepBasement.png]. Succeeded.
UnloadTime: 0.123400 ms
>> Map_HogansKeepBasement (UnityEngine.Texture2D) UnityEngine.Texture2D
```

- [ ] **Step 2: Run the per-rule fixture test that consumes this file**

Grep: `grep -rn "asset-loader-noise" tests/` to find the consuming test. Run it.
Expected: existing assertions still pass; if the test counts non-game-line drops, the count may shift — update the expected value to match the new line count.

- [ ] **Step 3: Commit**

```bash
git add tests/Mithril.Shared.Tests/Logging/Fixtures/per-rule/asset-loader-noise.log
git commit -m "test(arda): extend asset-loader-noise fixture with live Downloading Map capture"
```

### Task 21: Replay-drain ordering test

**Files:**
- Modify: `tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs`

- [ ] **Step 1: Add the replay-drain test**

```csharp
[Fact]
public void Replay_LastDownloadingMapLineWins()
{
    Dispatch("Serbule", "Map_AreaSerbule", Meta(isReplay: true));
    Dispatch("Eltibule", "Map_AreaEltibule", Meta(isReplay: true));
    Dispatch("Hogan's Basement", "Map_HogansKeepBasement", Meta(isReplay: true));

    _handler.CurrentMapAsset.Should().Be("Map_HogansKeepBasement");
    _handler.CurrentSceneFriendlyName.Should().Be("Hogan's Basement");
    _bus.Published<MapAssetChanged>().Should().HaveCount(3);
}
```

- [ ] **Step 2: Run + commit**

```bash
dotnet test tests/Arda.World.Player.Tests --filter "FullyQualifiedName~MapAssetLoaderTests.Replay"
git add tests/Arda.World.Player.Tests/MapAssetLoaderTests.cs
git commit -m "test(arda): MapAssetLoader replay-drain order — last Downloading Map wins"
```

---

## Phase 7 — Final integration + PR

### Task 22: Full-solution build + test + PR

- [ ] **Step 1: Verify Mithril.exe is closed** (the build/test hook will block otherwise — memory rule).

- [ ] **Step 2: Full build + full test run**

```bash
dotnet build Mithril.slnx
dotnet test Mithril.slnx
```
Expected: 100% green. If anything is red, fix it before opening the PR.

- [ ] **Step 3: Confirm the touched-files summary matches the spec**

Run: `git diff --stat origin/main`
Expected: roughly ~16 production files + ~9 test files + the baseline.json + sidecar README.

- [ ] **Step 4: Push the branch**

```bash
git push -u origin plan/map-calibration-1021-per-scene-keying
```

- [ ] **Step 5: Open the PR (use `--body-file` per memory rule about gh + Bash)**

Write the PR body to a temp file under `$env:TEMP`, then:

```bash
gh pr create --repo moumantai-gg/mithril \
    --base main --head plan/map-calibration-1021-per-scene-keying \
    --title "feat(map-calibration): per-scene calibration keying (closes #1021)" \
    --body-file "$env:TEMP\mithril-1021-impl-pr-body.md"
```

The body should:
- Link to `docs/planning/map-calibration-1021-per-scene-keying/spec.md` and `plan.md`.
- Note `closes #1021`.
- Test plan checklist: (a) all tests green, (b) Hogan's Basement scene-load triggers a `MapAssetChanged` event in live capture, (c) autocal walkthrough in `AreaSerbule` still solves identically.
- AI-trailer per memory: `— drafted by Claude (Opus 4.7), posted by @arthur-conde`.

- [ ] **Step 6: Wait for CI green, then squash-merge**

```bash
gh pr merge <PR_NUMBER> --repo moumantai-gg/mithril --squash --delete-branch
```

- [ ] **Step 7: Flip INDEX.md status to `shipped` in a follow-up commit (or include in the squash)**

Either bake it into the squash commit body (`docs/planning/INDEX.md` row updated as part of the PR), or land a one-line follow-up:

```bash
# In docs/planning/INDEX.md, change the map-calibration-1021-per-scene-keying row
# from `active` to `shipped` and add the merged PR number.
git add docs/planning/INDEX.md
git commit -m "docs(planning): flip map-calibration-1021-per-scene-keying to shipped"
```

---

## Self-review checklist (run before declaring the plan complete)

- [ ] Every spec section §5.1–§5.5 has at least one task covering it.
- [ ] Every file in spec §6 appears as a Modify/Create line in at least one task.
- [ ] Every test surface in spec §5.5 has a corresponding task with the test code shown.
- [ ] Decisions D1–D8 from spec §3 are reflected in tasks without being re-litigated.
- [ ] No `TBD`, `TODO`, `FIXME`, or "implement appropriate X" placeholders.
- [ ] Each task ends with a commit step using conventional-commit message format.
- [ ] Method/type names match across tasks (`MapSceneRef`, `MapAssetLoader`, `CurrentMapAsset`, `MapAssetChanged`, `MapAssetNotYetKnown`, `--asset` flag, `MapAssetName` record field).
- [ ] Test class names align with existing convention (`*Tests` suffix, FluentAssertions + xUnit pattern verified against `MapTests.cs` and `VerbExtractorTests.cs`).
